import { defineConfig } from 'vite';
import basicSsl from '@vitejs/plugin-basic-ssl';

export default defineConfig({
    server: {
        port: 5311,
        strictPort: true,
        // HTTPS in dev — required for Firefox WebRTC to actually gather
        // ICE candidates on localhost. Firefox treats some pieces of the
        // RTCPeerConnection stack as HTTPS-only even though `localhost`
        // is officially a secure context, and silently leaves
        // `iceGatheringState` at 'new' on HTTP-localhost. The certificate
        // is self-signed via `basicSsl()` below; the browser will warn
        // once and remember after you click through. Passing an empty
        // ServerOptions ({}) is how Vite says "use HTTPS, take key/cert
        // from a plugin"; `true` alone isn't a valid value for this slot.
        https: {},
    },
    worker: {
        format: 'es',
    },
    plugins: [
        // Generates a self-signed cert + key for the dev server. No
        // system trust-store changes (unlike mkcert) — just a one-time
        // browser warning that lets you proceed.
        basicSsl(),
        // The sandboxed MonoGame preview iframe has a null/opaque origin.
        // Blazor's dotnet.js fetches with credentials: 'include', so the
        // browser rejects 'Access-Control-Allow-Origin: *' for those requests.
        // Echoing the request's Origin header back (including the literal
        // string "null" for sandboxed iframes) satisfies the browser's
        // credentialed-request CORS check without opening up to any real
        // external origin. Safe for a dev server only.
        {
            name: 'cors-echo-origin',
            configureServer(server) {
                server.middlewares.use((_req, res, next) => {
                    const origin = (_req as any).headers['origin'];
                    if (origin !== undefined) {
                        res.setHeader('Access-Control-Allow-Origin', origin);
                        res.setHeader('Access-Control-Allow-Credentials', 'true');
                    }
                    next();
                });
            },
        },
        // codingame's CSS registration system expects CSS imports to provide
        // their content as a default export string (it constructs CSSStyleSheets
        // via document.adoptedStyleSheets). Vite's default CSS handling injects
        // <style> tags instead. Rewriting the imports to ?inline makes Vite
        // return the CSS as a default string export.
        // Without this plugin, workbench parts get 0px dimensions because the
        // sizing CSS rules never apply.
        {
            name: 'load-vscode-css-as-string',
            enforce: 'pre',
            async resolveId(source, importer, options) {
                if (!source.endsWith('.css')) return undefined;
                const resolved = await this.resolve(source, importer, options);
                if (!resolved) return undefined;
                if (resolved.id.match(/node_modules\/(@codingame\/monaco-vscode|vscode|monaco-editor).*\.css$/)) {
                    console.log('[css-plugin] rewriting:', resolved.id.slice(-80));
                    return { ...resolved, id: resolved.id + '?inline' };
                }
                return undefined;
            },
        },
    ],
    optimizeDeps: {
        // The @codingame packages ship default-extension assets and use
        // `new URL('./resources/x', import.meta.url)` to find them. Vite's
        // dependency optimizer copies the JS to .vite/deps/ but NOT the
        // sibling resources/ folder, so the URL lookups 404. Excluding the
        // packages tells Vite to serve them as-is from node_modules/.
        exclude: [
            'vscode',
            'monaco-editor',
            '@codingame/monaco-vscode-api',
            '@codingame/monaco-vscode-editor-service-override',
            '@codingame/monaco-vscode-files-service-override',
            '@codingame/monaco-vscode-theme-defaults-default-extension',
            '@codingame/monaco-vscode-theme-service-override',
            '@codingame/monaco-vscode-textmate-service-override',
            '@codingame/monaco-vscode-languages-service-override',
            '@codingame/monaco-vscode-configuration-service-override',
            '@codingame/monaco-vscode-keybindings-service-override',
            '@codingame/monaco-vscode-views-service-override',
            '@codingame/monaco-vscode-output-service-override',
            '@codingame/monaco-vscode-explorer-service-override',
            '@codingame/monaco-vscode-storage-service-override',
            '@codingame/monaco-vscode-model-service-override',
            '@codingame/monaco-vscode-lifecycle-service-override',
            '@codingame/monaco-vscode-workbench-service-override',
            '@codingame/monaco-vscode-view-status-bar-service-override',
            '@codingame/monaco-vscode-view-title-bar-service-override',
            '@codingame/monaco-vscode-log-service-override',
            '@codingame/monaco-vscode-notifications-service-override',
            '@codingame/monaco-vscode-dialogs-service-override',
            '@codingame/monaco-vscode-working-copy-service-override',
            '@codingame/monaco-vscode-environment-service-override',
            '@codingame/monaco-vscode-quickaccess-service-override',
            '@codingame/monaco-vscode-extensions-service-override',
            '@codingame/monaco-vscode-markers-service-override',
        ],
    },
});
