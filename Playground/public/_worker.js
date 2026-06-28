// Cloudflare Pages worker: production-side counterpart to the
// "cors-echo-origin" vite middleware in vite.config.ts. The sandboxed
// MonoGame preview iframe has a null/opaque origin (it's sandboxed
// without allow-same-origin), and Blazor's dotnet.js fetches
// blazor.boot.json and the .wasm modules with credentials: 'include'.
// Browsers refuse credentialed responses with Access-Control-Allow-Origin: *,
// which is what Cloudflare Pages returns by default. This worker echoes
// the request's Origin header (including the literal string "null" for
// sandboxed iframes) and adds Access-Control-Allow-Credentials so the
// browser accepts the response.
//
// Static-asset serving is delegated to env.ASSETS; this worker only
// rewrites response headers.
export default {
    async fetch(request, env) {
        const response = await env.ASSETS.fetch(request);
        const origin = request.headers.get('Origin');
        if (origin === null) {
            return response;
        }
        const headers = new Headers(response.headers);
        headers.set('Access-Control-Allow-Origin', origin);
        headers.set('Access-Control-Allow-Credentials', 'true');
        headers.set('Vary', 'Origin');
        return new Response(response.body, {
            status: response.status,
            statusText: response.statusText,
            headers,
        });
    },
};
