// Sign-in dialog — GitHub OAuth device flow.
//
// Replaces the previous PAT-paste dialog. The device flow is the only
// browser-safe path GitHub offers (web flow still requires
// `client_secret`); we route the two CORS-blocked endpoints through
// the stateless oauth-proxy worker. See ../../../oauth-proxy/README.md
// for the worker side.
//
// UX:
//   1. User clicks "Sign in with GitHub" → dialog opens, shows a
//      spinner while we request a device code.
//   2. Dialog shows a short user_code ("WDJB-MJHT") + an "Open
//      GitHub" button. Browser opens github.com/login/device in a new
//      tab; user pastes the code; the dialog polls in the background.
//   3. When GitHub returns the token, dialog closes and the panel
//      gets the TokenSet for storage.
//   4. Cancel button aborts polling and rejects with AbortError.
//
// The dialog itself: vanilla DOM, scoped CSS, resolves to the full
// TokenSet (access_token + refresh_token + expires_in).

import { GITHUB_APP_CLIENT_ID, GITHUB_OAUTH_SCOPE } from './github-auth-config';
import {
    DeviceFlowError,
    requestDeviceCode,
    pollForToken,
    type DeviceCodePrompt,
    type TokenSet,
} from './github-auth';

const CSS_PREFIX = 'fade-auth';
const STYLE_ID = `${CSS_PREFIX}-styles`;

export interface SignInDialogOptions {
    /** Override the initial explainer paragraph. */
    explainer?: string;
    /** Injected for tests. */
    fetchImpl?: typeof fetch;
    /** Injected for tests. */
    sleepImpl?: (ms: number) => Promise<void>;
    /** Override the App's client_id (defaults to the config module).
     *  Useful for tests; production uses GITHUB_APP_CLIENT_ID. */
    clientId?: string;
}

/**
 * Opens the modal device-flow dialog. Resolves with a TokenSet when
 * the user finishes authorizing; rejects with:
 *   - `DOMException('canceled', 'AbortError')` if the user clicks Cancel.
 *   - `DeviceFlowError` for explicit denial / expiry / config errors.
 *   - `Error` for network failures fetching the device code.
 *
 * The dialog stays open on a recoverable failure so the user can retry
 * without losing the dialog state.
 */
export function openSignInDialog(opts: SignInDialogOptions = {}): Promise<TokenSet> {
    return new Promise<TokenSet>((resolve, reject) => {
        injectStylesOnce();

        const clientId = opts.clientId ?? GITHUB_APP_CLIENT_ID;
        const abortController = new AbortController();

        const overlay = el('div', `${CSS_PREFIX}-overlay`);
        const panel = el('div', `${CSS_PREFIX}-panel`);
        overlay.appendChild(panel);

        function close() {
            overlay.remove();
        }
        function fail(err: unknown) {
            abortController.abort();
            close();
            reject(err);
        }
        function done(tokenSet: TokenSet) {
            close();
            resolve(tokenSet);
        }

        // Stage 1: requesting device code from GitHub (via proxy). Shows
        // a spinner. Quick — usually <500ms.
        const stage1 = el('div', `${CSS_PREFIX}-stage`);
        stage1.append(
            heading('Sign in with GitHub'),
            p(opts.explainer ?? 'You\'ll get a short code and a link to github.com. Paste the code there, authorize the app, and you\'re back — no token paperwork.'),
            row(spinner(), spanText('Requesting a device code…')),
        );
        const cancelStage1 = button('Cancel', 'ghost',
            () => fail(new DOMException('canceled', 'AbortError')));
        stage1.append(row(cancelStage1));

        panel.append(stage1);
        document.body.appendChild(overlay);

        // Kick off the device-code request immediately. If it fails
        // hard (network, proxy misconfig), surface an error inside the
        // dialog and offer Retry.
        void (async () => {
            let prompt: DeviceCodePrompt;
            try {
                prompt = await requestDeviceCode({
                    clientId,
                    // GITHUB_OAUTH_SCOPE is 'repo' for OAuth Apps;
                    // empty for GitHub Apps (which ignore scope).
                    // Empty-string scopes are stripped in
                    // requestDeviceCode so we don't post `scope: ''`.
                    scope: GITHUB_OAUTH_SCOPE || undefined,
                    fetchImpl: opts.fetchImpl,
                });
            } catch (e) {
                showRequestError(e);
                return;
            }
            // Replace stage1 with stage2 (code + open button + spinner).
            panel.replaceChildren();
            panel.append(buildStage2(prompt));
            startPolling(prompt);
        })();

        function showRequestError(err: unknown) {
            const msg = err instanceof Error ? err.message : String(err);
            const retryBtn = button('Retry', 'primary', () => {
                // Re-open. Simplest path; preserves the device-flow
                // promise without complex state machines.
                close();
                openSignInDialog(opts).then(resolve, reject);
            });
            const cancelBtn = button('Cancel', 'ghost',
                () => fail(new DOMException('canceled', 'AbortError')));
            panel.replaceChildren(
                heading('Couldn\'t reach the OAuth proxy'),
                p(`Failed to fetch a device code. The proxy worker may be down, or your network is blocking it. Details: ${msg}`),
                row(retryBtn, cancelBtn),
            );
        }

        function buildStage2(prompt: DeviceCodePrompt): HTMLElement {
            const wrap = el('div', `${CSS_PREFIX}-stage`);

            const codeBox = el('div', `${CSS_PREFIX}-codebox`);
            codeBox.textContent = prompt.userCode;
            codeBox.title = 'Click to copy';
            codeBox.addEventListener('click', () => {
                void navigator.clipboard?.writeText(prompt.userCode);
                codeBox.classList.add(`${CSS_PREFIX}-codebox-copied`);
                setTimeout(() => codeBox.classList.remove(`${CSS_PREFIX}-codebox-copied`), 600);
            });

            const verifyUrl = prompt.verificationUriComplete ?? prompt.verificationUri;
            const openBtn = button('Open GitHub →', 'primary', () => {
                window.open(verifyUrl, '_blank', 'noopener,noreferrer');
            });
            const cancelBtn = button('Cancel', 'ghost',
                () => fail(new DOMException('canceled', 'AbortError')));

            const statusLine = el('div', `${CSS_PREFIX}-status`);
            statusLine.append(spinner(), spanText('Waiting for you to authorize…'));

            const errorLine = el('p', `${CSS_PREFIX}-p ${CSS_PREFIX}-p-err`);
            errorLine.style.display = 'none';
            errorLine.id = `${CSS_PREFIX}-stage2-error`;

            wrap.append(
                heading('Authorize on github.com'),
                p('1. Click "Open GitHub" — it loads github.com/login/device in a new tab.'),
                p('2. Paste this code:'),
                codeBox,
                p('3. Confirm the app on the GitHub page. Come back here when you\'re done — we\'ll detect it automatically.'),
                row(openBtn, cancelBtn),
                statusLine,
                errorLine,
            );
            return wrap;
        }

        function startPolling(prompt: DeviceCodePrompt) {
            void (async () => {
                try {
                    const tokenSet = await pollForToken({
                        clientId,
                        deviceCode: prompt.deviceCode,
                        interval: prompt.interval,
                        fetchImpl: opts.fetchImpl,
                        sleepImpl: opts.sleepImpl,
                        signal: abortController.signal,
                    });
                    done(tokenSet);
                } catch (e) {
                    if (e instanceof DOMException && e.name === 'AbortError') {
                        // Cancel button already called fail() — nothing
                        // to do here.
                        return;
                    }
                    handlePollError(e);
                }
            })();
        }

        function handlePollError(err: unknown) {
            // DeviceFlowErrors are recoverable in some cases:
            //   - access_denied → terminal, user said no.
            //   - expired_token → recoverable, offer a fresh code.
            //   - unsupported_grant_type → config error on the App;
            //     terminal from the user's POV.
            //   - incorrect_client_credentials → config error; terminal.
            //   - bad_refresh_token → can't happen in this path (no
            //     refresh attempted yet); show as unknown.
            //   - unknown → show details; offer retry.
            const msg = err instanceof Error ? err.message : String(err);
            if (err instanceof DeviceFlowError && err.code === 'expired_token') {
                // Auto-restart with a fresh code.
                close();
                openSignInDialog(opts).then(resolve, reject);
                return;
            }
            const retryBtn = button('Start over', 'primary', () => {
                close();
                openSignInDialog(opts).then(resolve, reject);
            });
            const cancelBtn = button('Cancel', 'ghost',
                () => fail(err));
            const cause = err instanceof DeviceFlowError ? err.code : 'error';
            panel.replaceChildren(
                heading('Sign-in failed'),
                p(`${cause}: ${msg}`),
                row(retryBtn, cancelBtn),
            );
        }
    });
}

// ─── DOM helpers ────────────────────────────────────────────────────────────

function el<K extends keyof HTMLElementTagNameMap>(tag: K, className: string): HTMLElementTagNameMap[K] {
    const n = document.createElement(tag);
    n.className = className;
    return n;
}

function heading(text: string): HTMLElement {
    const h = el('h2', `${CSS_PREFIX}-h`);
    h.textContent = text;
    return h;
}

function p(text: string): HTMLElement {
    const n = el('p', `${CSS_PREFIX}-p`);
    n.textContent = text;
    return n;
}

function row(...children: HTMLElement[]): HTMLElement {
    const r = el('div', `${CSS_PREFIX}-row`);
    for (const c of children) r.appendChild(c);
    return r;
}

function spanText(text: string): HTMLSpanElement {
    const s = document.createElement('span');
    s.textContent = text;
    return s;
}

function button(text: string, variant: 'primary' | 'ghost', onClick: () => void): HTMLButtonElement {
    const b = document.createElement('button');
    b.className = `${CSS_PREFIX}-btn ${CSS_PREFIX}-btn-${variant}`;
    b.textContent = text;
    b.type = 'button';
    b.onclick = onClick;
    return b;
}

function spinner(): HTMLElement {
    const s = el('span', `${CSS_PREFIX}-spinner`);
    s.setAttribute('aria-hidden', 'true');
    return s;
}

// ─── styles (injected once into <head>) ─────────────────────────────────────

function injectStylesOnce(): void {
    if (document.getElementById(STYLE_ID)) return;
    const style = document.createElement('style');
    style.id = STYLE_ID;
    style.textContent = `
.${CSS_PREFIX}-overlay {
    position: fixed; inset: 0;
    background: rgba(0,0,0,0.45);
    display: flex; align-items: center; justify-content: center;
    z-index: 99999;
    font: 14px/1.4 ui-sans-serif, system-ui, sans-serif;
    color: inherit;
}
.${CSS_PREFIX}-panel {
    background: var(--vscode-editor-background, #1e1e1e);
    color: var(--vscode-foreground, #ddd);
    border: 1px solid var(--vscode-panel-border, #444);
    border-radius: 8px;
    padding: 24px 28px;
    max-width: 520px; width: calc(100% - 32px);
    box-shadow: 0 12px 36px rgba(0,0,0,0.5);
}
.${CSS_PREFIX}-stage {
    display: flex; flex-direction: column; gap: 12px;
}
.${CSS_PREFIX}-h { font-size: 18px; font-weight: 600; margin: 0 0 4px; }
.${CSS_PREFIX}-p { margin: 0; opacity: 0.9; }
.${CSS_PREFIX}-p-err { color: #e88; }
.${CSS_PREFIX}-codebox {
    font: 22px/1.2 ui-monospace, monospace;
    font-weight: 700;
    letter-spacing: 0.12em;
    text-align: center;
    padding: 12px 16px;
    border: 1px dashed var(--vscode-panel-border, #555);
    border-radius: 6px;
    background: rgba(255,255,255,0.04);
    cursor: pointer;
    user-select: all;
    transition: background 0.15s;
}
.${CSS_PREFIX}-codebox:hover { background: rgba(255,255,255,0.07); }
.${CSS_PREFIX}-codebox-copied {
    background: rgba(120, 220, 120, 0.18) !important;
    border-color: rgba(120, 220, 120, 0.5);
}
.${CSS_PREFIX}-status {
    display: flex; align-items: center; gap: 10px; opacity: 0.85;
    font-size: 13px;
    margin-top: 4px;
}
.${CSS_PREFIX}-spinner {
    width: 14px; height: 14px; border-radius: 50%;
    border: 2px solid currentColor; border-right-color: transparent;
    animation: ${CSS_PREFIX}-spin 0.8s linear infinite;
    display: inline-block;
}
@keyframes ${CSS_PREFIX}-spin { to { transform: rotate(360deg); } }
.${CSS_PREFIX}-row { display: flex; gap: 8px; flex-wrap: wrap; }
.${CSS_PREFIX}-btn {
    appearance: none; border: 0; cursor: pointer;
    padding: 8px 14px; border-radius: 6px;
    font: inherit; font-weight: 500;
    transition: filter 0.1s;
}
.${CSS_PREFIX}-btn:hover { filter: brightness(1.15); }
.${CSS_PREFIX}-btn:disabled { opacity: 0.5; cursor: not-allowed; filter: none; }
.${CSS_PREFIX}-btn-primary {
    background: var(--vscode-button-background, #0e639c);
    color: var(--vscode-button-foreground, #fff);
}
.${CSS_PREFIX}-btn-ghost {
    background: transparent;
    color: inherit;
    border: 1px solid var(--vscode-panel-border, #555);
}
`;
    document.head.appendChild(style);
}
