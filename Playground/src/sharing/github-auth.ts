// GitHub OAuth — Device Flow.
//
// Why device flow: GitHub doesn't let a browser SPA complete the
// standard authorization-code flow (the token-exchange endpoint still
// requires `client_secret` even with PKCE, per GitHub's docs on
// client_secret being "Required" at /login/oauth/access_token). The
// device flow accepts only `client_id` at every step — the same
// mechanism `gh auth login` uses. The only catch: GitHub's
// /login/* endpoints don't send CORS headers, so a browser fetch
// gets refused before the request leaves the user agent. We work
// around that with a stateless CORS proxy (see ../../../oauth-proxy/)
// that this module's default URLs point at.
//
// Shape:
//   1. `requestDeviceCode` → server returns user_code, verification URL,
//      device_code (opaque), expiry, polling interval.
//   2. App shows the user the user_code + opens the verification URL.
//   3. App polls `pollForToken` until success/denial/expiry.
//   4. On success: access_token (`ghu_*`) + refresh_token (`ghr_*`).
//   5. When access_token expires (~8h default), `refreshAccessToken`
//      exchanges the refresh_token for a fresh pair.

import { DEVICE_CODE_URL, TOKEN_URL } from './github-auth-config';

const USER_URL = 'https://api.github.com/user';

export interface DeviceCodePrompt {
    userCode: string;                 // e.g. "WDJB-MJHT" — shown to the user
    verificationUri: string;          // canonical URL the user opens
    verificationUriComplete?: string; // pre-filled variant if GitHub returns it
    deviceCode: string;               // opaque, passed back to pollForToken
    expiresIn: number;                // seconds until the code expires
    interval: number;                 // seconds between polls (server-recommended)
}

/** Full GitHub-App user-access-token response. Access tokens are
 *  short-lived (default 8h); refresh tokens last 6 months. Both
 *  arrive together from the token endpoint. */
export interface TokenSet {
    accessToken: string;             // `ghu_*` — the bearer for API calls
    refreshToken?: string;           // `ghr_*` — exchange for a new access token. Absent for tokens that don't expire (legacy OAuth-App tokens, fine-grained PATs).
    expiresIn?: number;              // seconds until `accessToken` expires
    refreshTokenExpiresIn?: number;  // seconds until `refreshToken` expires
    scope?: string;                  // space-separated scope list (mostly empty for GitHub Apps)
    tokenType?: string;              // 'bearer' typically
}

export interface RequestDeviceCodeOptions {
    clientId: string;
    /** Optional space-separated scope list. GitHub Apps ignore this
     *  (permissions are App-level). OAuth Apps use it. */
    scope?: string;
    fetchImpl?: typeof fetch;
    /** Override the device-code endpoint. Defaults to the proxy URL
     *  from github-auth-config; tests can pin to a mock URL. */
    deviceCodeUrl?: string;
}

export interface PollForTokenOptions {
    clientId: string;
    deviceCode: string;
    interval: number;                 // seconds (will auto-back-off on slow_down)
    fetchImpl?: typeof fetch;
    sleepImpl?: (ms: number) => Promise<void>;
    signal?: AbortSignal;
    /** Override the token endpoint. Defaults to the proxy URL from
     *  github-auth-config; tests can pin to a mock URL. */
    tokenUrl?: string;
}

export interface SignInWithDeviceFlowOptions extends RequestDeviceCodeOptions {
    onPrompt: (prompt: DeviceCodePrompt) => void;
    sleepImpl?: (ms: number) => Promise<void>;
    signal?: AbortSignal;
    tokenUrl?: string;
}

export interface RefreshAccessTokenOptions {
    clientId: string;
    refreshToken: string;
    fetchImpl?: typeof fetch;
    tokenUrl?: string;
}

export class DeviceFlowError extends Error {
    constructor(
        public code:
            | 'access_denied'
            | 'expired_token'
            | 'unsupported_grant_type'
            | 'incorrect_client_credentials'
            | 'bad_refresh_token'
            | 'unknown',
        message: string,
    ) {
        super(message);
        this.name = 'DeviceFlowError';
    }
}

// ─── step 1: ask for a device code ──────────────────────────────────────────

export async function requestDeviceCode(opts: RequestDeviceCodeOptions): Promise<DeviceCodePrompt> {
    const fetchImpl = opts.fetchImpl ?? fetch.bind(globalThis);
    const url = opts.deviceCodeUrl ?? DEVICE_CODE_URL;
    const body: Record<string, string> = { client_id: opts.clientId };
    if (opts.scope) body.scope = opts.scope;
    const r = await fetchImpl(url, {
        method: 'POST',
        headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
    });
    if (!r.ok) throw new Error(`device code request failed: ${r.status} ${r.statusText}`);
    const payload = await r.json() as {
        device_code: string;
        user_code: string;
        verification_uri: string;
        verification_uri_complete?: string;
        expires_in: number;
        interval: number;
    };
    return {
        userCode: payload.user_code,
        verificationUri: payload.verification_uri,
        verificationUriComplete: payload.verification_uri_complete,
        deviceCode: payload.device_code,
        expiresIn: payload.expires_in,
        interval: payload.interval,
    };
}

// ─── step 2: poll until the user finishes authorizing ───────────────────────

export async function pollForToken(opts: PollForTokenOptions): Promise<TokenSet> {
    const fetchImpl = opts.fetchImpl ?? fetch.bind(globalThis);
    const sleep = opts.sleepImpl ?? defaultSleep;
    const url = opts.tokenUrl ?? TOKEN_URL;
    let interval = opts.interval;

    for (;;) {
        throwIfAborted(opts.signal);
        await sleep(interval * 1000);
        throwIfAborted(opts.signal);

        const r = await fetchImpl(url, {
            method: 'POST',
            headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
            body: JSON.stringify({
                client_id: opts.clientId,
                device_code: opts.deviceCode,
                grant_type: 'urn:ietf:params:oauth:grant-type:device_code',
            }),
        });
        // GitHub returns 200 with an error payload for in-progress / failed
        // states — we have to inspect the body, not just the status code.
        const body = await r.json() as TokenResponse;
        if (body.access_token) return parseTokenSet(body);
        // Errors that aren't "keep polling" cases bubble out via switch.
        translatePollError(body);
        // slow_down requested — bump interval. authorization_pending →
        // keep going at the current rate. Both fall through to the next
        // loop iteration; everything else throws above.
        if (body.error === 'slow_down') interval += 5;
    }
}

// ─── refresh: trade ghr_* for a new (ghu_*, ghr_*) pair ─────────────────────

export async function refreshAccessToken(opts: RefreshAccessTokenOptions): Promise<TokenSet> {
    const fetchImpl = opts.fetchImpl ?? fetch.bind(globalThis);
    const url = opts.tokenUrl ?? TOKEN_URL;
    const r = await fetchImpl(url, {
        method: 'POST',
        headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
        body: JSON.stringify({
            client_id: opts.clientId,
            grant_type: 'refresh_token',
            refresh_token: opts.refreshToken,
        }),
    });
    const body = await r.json() as TokenResponse;
    if (body.access_token) return parseTokenSet(body);
    if (body.error === 'bad_refresh_token' || body.error === 'unauthorized_client') {
        throw new DeviceFlowError('bad_refresh_token', body.error_description ?? 'refresh token rejected');
    }
    throw new DeviceFlowError('unknown', `refresh failed: ${body.error ?? 'no error field'} ${body.error_description ?? ''}`);
}

// ─── one-call helper ────────────────────────────────────────────────────────

export async function signInWithDeviceFlow(opts: SignInWithDeviceFlowOptions): Promise<TokenSet> {
    const prompt = await requestDeviceCode(opts);
    opts.onPrompt(prompt);
    return pollForToken({
        clientId: opts.clientId,
        deviceCode: prompt.deviceCode,
        interval: prompt.interval,
        fetchImpl: opts.fetchImpl,
        sleepImpl: opts.sleepImpl,
        signal: opts.signal,
        tokenUrl: opts.tokenUrl,
    });
}

// ─── token validation ───────────────────────────────────────────────────────

export interface ValidatedToken {
    login: string;
    id: number;
    scopes: string[];                 // from the X-OAuth-Scopes header
}

/**
 * Sanity-check a token by calling GET /user. Works for any token type
 * (PAT, OAuth user-access, GitHub-App user-access). api.github.com sets
 * proper CORS headers so this call doesn't need the proxy.
 */
export async function validateToken(token: string, fetchImpl?: typeof fetch): Promise<ValidatedToken> {
    const f = fetchImpl ?? fetch.bind(globalThis);
    const r = await f(USER_URL, {
        headers: {
            Authorization: `Bearer ${token}`,
            Accept: 'application/vnd.github+json',
            'X-GitHub-Api-Version': '2022-11-28',
        },
    });
    if (!r.ok) {
        const text = await r.text().catch(() => '');
        throw new Error(`token validation failed: ${r.status} ${text.slice(0, 200)}`);
    }
    const body = await r.json() as { login: string; id: number };
    const scopesHeader = r.headers.get('X-OAuth-Scopes') ?? '';
    const scopes = scopesHeader.split(',').map((s) => s.trim()).filter(Boolean);
    return { login: body.login, id: body.id, scopes };
}

// ─── helpers ────────────────────────────────────────────────────────────────

interface TokenResponse {
    access_token?: string;
    refresh_token?: string;
    expires_in?: number;
    refresh_token_expires_in?: number;
    scope?: string;
    token_type?: string;
    error?: string;
    error_description?: string;
}

function parseTokenSet(body: TokenResponse): TokenSet {
    return {
        accessToken: body.access_token!,
        refreshToken: body.refresh_token,
        expiresIn: body.expires_in,
        refreshTokenExpiresIn: body.refresh_token_expires_in,
        scope: body.scope,
        tokenType: body.token_type,
    };
}

/** Translate the not-yet-authorized / hard-fail variants of the token
 *  endpoint response into either "keep looping" (no throw) or a
 *  DeviceFlowError. The success-with-token case is handled before
 *  this is called. */
function translatePollError(body: TokenResponse): void {
    switch (body.error) {
        case 'authorization_pending':
        case 'slow_down':
            return;
        case 'expired_token':
            throw new DeviceFlowError('expired_token', 'device code expired before the user authorized');
        case 'access_denied':
            throw new DeviceFlowError('access_denied', 'user denied authorization');
        case 'unsupported_grant_type':
            throw new DeviceFlowError(
                'unsupported_grant_type',
                'device flow not enabled on this App — turn it on in the App\'s "Identifying and authorizing users" settings',
            );
        case 'incorrect_client_credentials':
            throw new DeviceFlowError('incorrect_client_credentials', 'client_id is wrong');
        default:
            throw new DeviceFlowError(
                'unknown',
                `unexpected token response: ${body.error ?? 'no error field'} ${body.error_description ?? ''}`,
            );
    }
}

function defaultSleep(ms: number): Promise<void> {
    return new Promise((r) => setTimeout(r, ms));
}

function throwIfAborted(signal?: AbortSignal): void {
    if (signal?.aborted) {
        const reason = signal.reason ?? new DOMException('aborted', 'AbortError');
        throw reason;
    }
}
