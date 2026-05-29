# Fade Playground

Browser-based editor and runtime for FadeBasic. A static Vite SPA built
around Monaco / `@codingame/monaco-vscode-api` plus the FadeBasic
runtime compiled to WASM.

## Local development

```bash
cd Playground
npm install
npm run dev
```

`predev` builds the runtime and syncs docs into `public/`, so the first
boot takes a few seconds longer than subsequent ones. Dev server runs
on http://localhost:5311.

## Production build

```bash
npm run build
```

Outputs to `Playground/dist`. The runtime build and docs sync are
*not* run by `npm run build` — run them first if your local copies are
stale:

```bash
npm run build:runtime
npm run sync:public-docs
npm run build
```

## Deploying to Cloudflare Pages

The deployed site lives on Cloudflare Pages, project name
`fade-playground`. Two public URLs:

- **Production** — https://fade-playground.pages.dev (deploys from
  `--branch=main`)
- **Preview** — https://tests.fade-playground.pages.dev (deploys from
  `--branch=tests`, used for in-progress work that isn't on `main`
  yet)

Each deploy also gets a permanent hash URL like
`https://e436d744.fade-playground.pages.dev` — useful for sharing a
specific build without overwriting the canonical one.

### Deploy from your terminal

`wrangler` is installed in [`../oauth-proxy/node_modules`](../oauth-proxy)
— there's no separate wrangler dependency in this package. The deploy
is a one-shot upload of `dist/` to Cloudflare Pages.

```bash
# 1. From Playground/ — rebuild runtime, docs, and SPA.
npm run build:runtime
npm run sync:public-docs
npm run build

# 2. From oauth-proxy/ — push dist/ to Cloudflare Pages.
cd ../oauth-proxy
npx wrangler pages deploy ../Playground/dist \
    --project-name=fade-playground \
    --commit-dirty=true \
    --branch=tests
```

Swap `--branch=tests` for `--branch=main` to publish to the production
URL. `--commit-dirty=true` silences the "your git tree is dirty"
warning so the upload doesn't refuse to run.

### One-time setup

If you've never deployed before:

```bash
cd oauth-proxy
npx wrangler login                                    # browser auth
npx wrangler pages project create fade-playground \
    --production-branch=main                          # only once
```

The project name and production-branch are baked into the Cloudflare
account; you don't need to re-run this on subsequent deploys.

### CORS / OAuth proxy

The Playground's GitHub auth uses the device-flow proxy in
[`../oauth-proxy`](../oauth-proxy). That worker enforces an origin
allow-list — `ALLOWED_ORIGINS` in
[`oauth-proxy/wrangler.toml`](../oauth-proxy/wrangler.toml). When you
add a new Pages URL (custom domain, new preview branch alias), append
it to that list and `npm run deploy` from `oauth-proxy/`, or device
flow will 403.

Per-deploy hash URLs (`https://<hash>.fade-playground.pages.dev`) are
*not* in the allow-list. Use the branch alias URL for any flow that
hits the OAuth proxy.

### CORS echo (`public/_worker.js`)

The sandboxed MonoGame preview iframe ([src/monogame-host.ts](src/monogame-host.ts))
runs without `allow-same-origin`, so its origin is the literal string
`"null"`. Blazor's `dotnet.js` then fetches `blazor.boot.json` and the
runtime `.wasm` modules with `credentials: 'include'`, and browsers
refuse credentialed responses with `Access-Control-Allow-Origin: *`
— which is what Cloudflare Pages serves static assets with by default.
[public/_worker.js](public/_worker.js) is the production-side
counterpart to the `cors-echo-origin` plugin in
[vite.config.ts](vite.config.ts): it echoes the request's `Origin`
back (including the literal `"null"`) and sets
`Access-Control-Allow-Credentials: true`, identical to the dev-server
behavior. Vite copies `public/` to `dist/` on every build, so the
worker ships automatically — no extra deploy step.

### First-deploy SSL lag

Cloudflare's universal `*.pages.dev` cert covers
`fade-playground.pages.dev` instantly, but the per-project wildcard
that covers `*.fade-playground.pages.dev` (branch aliases, per-deploy
hash URLs) is provisioned asynchronously after project creation.
Until that lands — typically 15 min, sometimes longer — Firefox will
report `SSL_ERROR_NO_CYPHER_OVERLAP` and curl will see an outright
handshake failure on those subdomain URLs. The root URL works the
whole time, so deploy with `--branch=main` if you need an
immediately-reachable build.

### Size limits

Cloudflare Pages caps individual files at 25 MiB and total files at
20,000 per deploy. The onnxruntime WASM is the largest single file
(~23.5 MiB) — keep an eye on it if upstream bumps the bundle.
