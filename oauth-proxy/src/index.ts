// GitHub OAuth device-flow CORS proxy.
//
// Why this exists:
//   GitHub's device flow lets a browser-only app complete OAuth without
//   ever holding a `client_secret` — the token endpoint accepts only
//   `client_id` + `device_code`. The blocker is purely CORS: GitHub
//   doesn't send `Access-Control-Allow-Origin` on `github.com/login/*`,
//   so a browser `fetch` is rejected by the user agent before the
//   request even goes out. This worker relays exactly those two
//   endpoints with CORS headers attached.
//
// What it does NOT do:
//   - Store any credentials (client_secret, tokens, refresh tokens).
//   - Persist state across requests.
//   - Forward arbitrary requests to GitHub (the path allow-list is
//     enforced; anything else returns 403).
//   - Log request bodies (would leak device codes if the worker is
//     compromised).
//
// Threat model:
//   - If the worker host is fully compromised, an attacker could swap
//     the relay for a token-stealing relay. Mitigation: the worker
//     source is small enough to audit (~80 lines), and the user can
//     pin the deployed version's hash in the playground client.
//   - If the worker's allow-list of origins is widened to `*`, anyone
//     can use your worker to run device-flow requests against your
//     GitHub App. Not a security incident (they can't steal anyone's
//     tokens), but does charge requests against your free tier.

const GITHUB_ORIGIN = 'https://github.com';

/** Paths we are willing to relay. Anything else gets a flat 403.
 *  These are the only two endpoints the OAuth device flow uses
 *  (per docs.github.com/en/apps/.../using-the-device-flow-...). */
const ALLOWED_PATHS = new Set<string>([
    '/login/device/code',
    '/login/oauth/access_token',
]);

interface Env {
    /** Comma-separated origin allow-list. Configured in wrangler.toml
     *  `[vars]` and overridable per-environment. Whitespace is trimmed
     *  per entry. */
    ALLOWED_ORIGINS: string;
}

export default {
    async fetch(request: Request, env: Env): Promise<Response> {
        const url = new URL(request.url);
        const origin = request.headers.get('Origin');
        const allowList = parseAllowedOrigins(env.ALLOWED_ORIGINS);

        // Preflight. Respond before any allow-list check so the browser
        // gets a proper CORS reply even for disallowed origins (it'll
        // then reject the actual request based on missing
        // Access-Control-Allow-Origin). Reflect the requested method
        // and headers so common content-types work.
        if (request.method === 'OPTIONS') {
            return preflightResponse(origin, allowList);
        }

        // Origin gate. Reject anything from outside the allow-list with
        // a clear message — easier to debug than a generic CORS error.
        const allowedOrigin = pickAllowedOrigin(origin, allowList);
        if (!allowedOrigin) {
            // Log rejected origins to `wrangler tail` so deploy-time
            // mismatches show up immediately. The Origin header is
            // public per-request info; logging it is fine.
            console.warn(`[fade-oauth-proxy] rejected origin=${origin ?? '(none)'} path=${url.pathname} allow_list=${JSON.stringify(allowList)}`);
            return json(
                { error: 'origin_not_allowed', origin, allow_list: allowList },
                403,
                corsHeaders(null),
            );
        }

        // Path gate. The proxy is intentionally narrow — anything
        // outside the device-flow endpoints is refused.
        if (!ALLOWED_PATHS.has(url.pathname)) {
            return json(
                { error: 'path_not_allowed', path: url.pathname },
                403,
                corsHeaders(allowedOrigin),
            );
        }

        // Method gate. Both endpoints are POST.
        if (request.method !== 'POST') {
            return json(
                { error: 'method_not_allowed', method: request.method },
                405,
                corsHeaders(allowedOrigin),
            );
        }

        // Relay. Forward the body + the content-type, ask GitHub for
        // JSON back (the API supports `application/json` Accept and
        // returns a clean JSON object instead of the legacy
        // form-encoded shape).
        const upstreamUrl = `${GITHUB_ORIGIN}${url.pathname}`;
        const contentType = request.headers.get('Content-Type') ?? 'application/x-www-form-urlencoded';
        let upstream: Response;
        try {
            upstream = await fetch(upstreamUrl, {
                method: 'POST',
                headers: {
                    'Accept': 'application/json',
                    'Content-Type': contentType,
                    // Forwarding a User-Agent makes GitHub's logs and
                    // rate-limit attribution clearer; without it some
                    // edges return 403.
                    'User-Agent': 'fade-oauth-proxy/0.1 (+https://github.com/...)',
                },
                body: await request.text(),
            });
        } catch (e) {
            return json(
                { error: 'upstream_unreachable', detail: errMessage(e) },
                502,
                corsHeaders(allowedOrigin),
            );
        }

        // Pass-through. GitHub's body is small JSON; reading + re-
        // emitting it gives us a clean place to attach CORS headers.
        const body = await upstream.text();
        return new Response(body, {
            status: upstream.status,
            headers: {
                'Content-Type': upstream.headers.get('Content-Type') ?? 'application/json',
                ...corsHeaders(allowedOrigin),
            },
        });
    },
} satisfies ExportedHandler<Env>;

// ─── helpers ───────────────────────────────────────────────────────────────

function parseAllowedOrigins(raw: string | undefined): string[] {
    if (!raw) return [];
    return raw.split(',').map((s) => s.trim()).filter(Boolean);
}

/** Match `origin` against the allow-list. Returns the request's origin
 *  (so the response echoes it exactly per CORS spec) or null if
 *  disallowed. Supports two entry shapes in the allow-list:
 *    - Exact match:     `https://playground.example.com`
 *    - Port wildcard:   `http://localhost:*` matches any port on
 *      `http://localhost` (and only on that exact host).
 *  Wildcards only apply to the port — host wildcards would defeat the
 *  point of the allow-list. */
function pickAllowedOrigin(origin: string | null, allowList: string[]): string | null {
    if (!origin) return null;
    for (const entry of allowList) {
        if (entry === origin) return origin;
        if (entry.endsWith(':*')) {
            // `http://localhost:*` → `http://localhost:` (the literal
            // prefix the origin must start with, plus a port number).
            const prefix = entry.slice(0, -1); // drop trailing '*'
            if (origin.startsWith(prefix)) {
                // Anything after the prefix must be digits-only (a port
                // number). Reject `http://localhost:5173.evil.com` etc.
                const portPart = origin.slice(prefix.length);
                if (/^\d+$/.test(portPart)) return origin;
            }
        }
    }
    return null;
}

function corsHeaders(allowedOrigin: string | null): Record<string, string> {
    const h: Record<string, string> = {
        'Vary': 'Origin',
    };
    if (allowedOrigin) {
        h['Access-Control-Allow-Origin'] = allowedOrigin;
    }
    return h;
}

function preflightResponse(origin: string | null, allowList: string[]): Response {
    const allowedOrigin = pickAllowedOrigin(origin, allowList);
    const headers: Record<string, string> = {
        'Vary': 'Origin, Access-Control-Request-Headers',
        'Access-Control-Allow-Methods': 'POST, OPTIONS',
        // Echo whatever headers the client wants to send (typically
        // `content-type` and `accept`). Safer than a static list — it
        // matches the spec's intent and avoids surprise failures when
        // the playground client evolves.
        'Access-Control-Allow-Headers': 'Content-Type, Accept',
        'Access-Control-Max-Age': '600',
    };
    if (allowedOrigin) {
        headers['Access-Control-Allow-Origin'] = allowedOrigin;
    }
    return new Response(null, { status: 204, headers });
}

function json(payload: unknown, status: number, extraHeaders: Record<string, string>): Response {
    return new Response(JSON.stringify(payload), {
        status,
        headers: {
            'Content-Type': 'application/json',
            ...extraHeaders,
        },
    });
}

function errMessage(e: unknown): string {
    if (e instanceof Error) return e.message;
    try { return JSON.stringify(e); } catch { return String(e); }
}
