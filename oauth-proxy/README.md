# fade-oauth-proxy

A tiny stateless Cloudflare Worker that relays GitHub's two device-flow
endpoints with CORS headers attached. It exists so the Playground (a
static SPA) can run the OAuth device flow directly from the browser
without needing to hold a `client_secret`.

**It stores nothing.** No tokens, no secrets, no cookies, no database.
Source is ~150 lines including comments — audit it in a sitting.

## What problem it solves

GitHub's device flow lets a client get a user-access token using only a
`client_id` (no `client_secret`). Perfect for a browser SPA — except
`github.com/login/device/code` and `github.com/login/oauth/access_token`
don't send `Access-Control-Allow-Origin`, so browser `fetch` is blocked
by the user agent. This worker is the smallest fix: it forwards those
two requests with CORS headers, allow-listed to your origin.

## Prerequisites

- A free Cloudflare account.
- A registered **GitHub App** (not OAuth App — App's user-access tokens
  are short-lived and refreshable, which is what we want). Settings →
  Developer settings → GitHub Apps → New GitHub App.
- In the App settings: enable **"Device flow"** under "Identifying and
  authorizing users". The docs are explicit: *"Before you can use the
  device flow, you must first enable it in your app's settings."*
- The App's **Client ID** (visible on the App's settings page; looks
  like `Iv23li...`). The Playground client will use this directly —
  the proxy doesn't need to know it.

## First-time deploy

```bash
cd oauth-proxy
npm install
npx wrangler login            # opens browser, links your Cloudflare account
npx wrangler deploy
```

`wrangler deploy` prints the worker URL — something like
`https://fade-oauth-proxy.<your-account>.workers.dev`. Plug that into
the Playground's GitHub auth config as the proxy base URL.

## Configuring allowed origins

`wrangler.toml` has an `ALLOWED_ORIGINS` variable. Edit the list and
re-deploy to add more origins (staging, prod, a different dev port).

```toml
[vars]
ALLOWED_ORIGINS = "http://localhost:5173,https://playground.example.com"
```

For per-environment overrides without editing the file:

```bash
npx wrangler deploy --var ALLOWED_ORIGINS:"https://prod.example.com,https://staging.example.com"
```

Origins are matched exactly (scheme + host + port). No wildcards.

## Local development

```bash
npm run dev
```

`wrangler dev` boots the worker on `http://localhost:8787`. Point the
Playground at that URL in development; production builds use the
deployed `workers.dev` URL.

## Smoke test

The proxy's allow-list responds to bad inputs without hitting GitHub,
so you can sanity-check the deployment without an OAuth round-trip:

```bash
# Disallowed origin → 403
curl -i -X POST https://fade-oauth-proxy.<acct>.workers.dev/login/device/code \
    -H 'Origin: https://evil.example.com' \
    -H 'Content-Type: application/x-www-form-urlencoded' \
    -d 'client_id=Iv23liFAKE'

# Disallowed path → 403
curl -i -X POST https://fade-oauth-proxy.<acct>.workers.dev/repos/foo/bar \
    -H 'Origin: http://localhost:5173'

# OPTIONS preflight from an allowed origin → 204 with CORS headers
curl -i -X OPTIONS https://fade-oauth-proxy.<acct>.workers.dev/login/device/code \
    -H 'Origin: http://localhost:5173' \
    -H 'Access-Control-Request-Method: POST' \
    -H 'Access-Control-Request-Headers: content-type'

# Real device-code request (uses your App's client_id) → JSON with device_code, user_code, etc.
curl -i -X POST https://fade-oauth-proxy.<acct>.workers.dev/login/device/code \
    -H 'Origin: http://localhost:5173' \
    -H 'Content-Type: application/x-www-form-urlencoded' \
    -d 'client_id=<YOUR_APP_CLIENT_ID>'
```

## Logs

`npx wrangler tail` streams live worker logs. Useful while wiring up
the client; turn it off in steady state. The worker doesn't log
request bodies — device codes and tokens never enter logs.

## Threat model

- **Worker compromised.** An attacker who controls the worker can swap
  the relay for a token-stealing relay. Mitigation: source is small
  enough to read and self-host; pin a known hash if you're paranoid.
- **`ALLOWED_ORIGINS` set to `*`.** Anyone can use your worker to run
  device-flow requests against your GitHub App. Not a security
  incident (no token theft path), but does charge requests against
  your free tier. Don't do it.
- **Cloudflare account compromised.** Same as worker-compromised plus
  the attacker can redeploy. Use 2FA on your Cloudflare account.
- **`client_id` leakage.** Public information by design — anyone can
  see it. Not a credential. Logging it is fine; ALLOWED_ORIGINS is
  about rate-limiting your free tier, not protecting the `client_id`.

## What this is NOT

- Not an OAuth backend. It holds no `client_secret`. Don't add one.
- Not a generic CORS proxy. The path allow-list is intentionally
  narrow. Don't broaden it.
- Not a token cache. Tokens flow through the worker once and are
  forgotten before the next request.
