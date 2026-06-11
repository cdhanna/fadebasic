// Shared mouse cursors for live sessions. Tracks the local user's
// pointer over the Editor + Game panels and broadcasts it via the
// session's awareness state, and renders remote peers' cursors as
// Figma-style arrow + name labels over the matching surface.
//
// Coordinate normalisation: each surface reports cursor positions as
// (nx, ny) in [0, 1] relative to its bounding box. Receivers multiply
// back by their own bounding box. This means peers with different
// window sizes see each other's cursors at the visually-equivalent
// spot.
//
// Visibility rules:
//   - Editor cursor: only visible if the remote peer's file matches
//     OUR active file. If they're looking at a different file, we
//     instead badge that file's tab via setEditorTabBadges.
//   - Game cursor: visible whenever the peer is over the game panel.
//   - No cursor at all when the peer's focus.scope is null.
//
// One mountSharedCursors() call wires up an entire session — sender
// listeners, receiver overlays, RAF render loop, tab badges. Returns
// a dispose function that tears it all down on session end.

import * as monaco from 'monaco-editor';
import type { CollabSession, PeerFocus, PeerView } from './sharing/collab/session';

export interface SharedCursorDeps {
    session: CollabSession;
    editor: monaco.editor.IStandaloneCodeEditor;
    /** Current active file name on this peer — used to gate "is the
     *  remote peer looking at the SAME file as me?" before rendering
     *  their cursor in the editor. Pass a getter so we always read
     *  fresh (the active file can change while the session lives). */
    getActiveFile: () => string | null;
    /** Called whenever the set of files that any peer is editing
     *  changes. Map keys are file names; values are { color, names }
     *  picked from the most recently-updated peer on that file (ts
     *  comparison). Empty map clears all dots. Caller decides which
     *  surfaces to badge — typically the editor tab strip (filtering
     *  the user's currently-active file) AND the workspace file list. */
    setPeerFilePresence: (filesByPath: Map<string, { color: string; names: string[] }>) => void;
}

export interface SharedCursorHandle {
    dispose(): void;
    /** Call after the active file changes — refreshes the in-editor
     *  cursor visibility (peers on the same file appear, peers on a
     *  different file disappear into the tab strip badge instead). */
    notifyActiveFileChanged(): void;
    /** Briefly pulse the on-screen cursor of `clientId` so the local
     *  user can spot it when clicking that peer's chip. No-op if the
     *  peer's cursor isn't currently rendered (different file, no
     *  focus, etc.). */
    pulseCursor(clientId: number): void;
}

/** Wire up the local user's mouse trackers + the receiver overlays
 *  for one session. Returns a handle the caller disposes on session
 *  end so we don't leak listeners or DOM elements between sessions. */
export function mountSharedCursors(deps: SharedCursorDeps): SharedCursorHandle {
    const { session, editor, getActiveFile, setPeerFilePresence } = deps;

    // ── overlay containers ─────────────────────────────────────────
    // One absolute-positioned root per surface. Cursors are children
    // of the matching root; rebuilt on each render frame from peer
    // state. Pointer-events disabled so they never steal clicks.
    const gameOverlay = makeOverlay('shared-cursor-overlay-game');
    const editorOverlay = makeOverlay('shared-cursor-overlay-editor');

    // Game panel: cursors live INSIDE the .panel-cell[data-panel=game]
    // so they follow dockview reparenting + tab switching. Mount once
    // here; if dockview tears down and rebuilds the cell, the overlay
    // goes with it (dispose() will rebuild on the next session).
    const gameCell = document.querySelector<HTMLElement>('.panel-cell[data-panel="game"]');
    if (gameCell) {
        gameCell.style.position = gameCell.style.position || 'relative';
        gameCell.appendChild(gameOverlay);
    }

    // Editor overlay sits inside the editor's content DOM so it scrolls
    // / scales with the editor naturally.
    const editorDom = editor.getDomNode();
    if (editorDom) {
        editorDom.style.position = editorDom.style.position || 'relative';
        editorDom.appendChild(editorOverlay);
    }

    // ── sender state (this peer's pointer) ─────────────────────────
    let pendingFocus: PeerFocus = null;
    let sendScheduled = false;
    let lastSentJson = '';
    function scheduleSend(next: PeerFocus) {
        pendingFocus = next;
        if (sendScheduled) return;
        sendScheduled = true;
        requestAnimationFrame(() => {
            sendScheduled = false;
            const json = pendingFocus ? JSON.stringify(pendingFocus) : 'null';
            if (json === lastSentJson) return;
            lastSentJson = json;
            try { session.setFocus(pendingFocus); }
            catch (e) { console.warn('[shared-cursor] setFocus failed', e); }
        });
    }
    function clearFocus() { scheduleSend(null); }

    // ── sender wiring ──────────────────────────────────────────────
    // Editor mouse tracking. Monaco gives us the line/column under
    // the mouse via the target; we anchor the broadcast to that
    // (with a sub-cell offset). When the receiver renders, they
    // resolve line/column → pixel via getScrolledVisiblePosition,
    // which AUTOMATICALLY accounts for their own scroll position.
    // Net effect: a peer's cursor sticks to the character they're
    // hovering, not the screen coordinates, so scroll moves it
    // correctly on the receiver.
    const editorMoveDispose = editor.onMouseMove((ev) => {
        const file = getActiveFile();
        if (!file) return;
        const target = ev.target;
        const pos = target.position;
        if (!pos) {
            // Hovering in a non-text area (margin, below-last-line,
            // overlay widget). Don't drop the focus — synthesize a
            // line/column from the nearest visible line so the
            // cursor still tracks something reasonable. clamp to
            // model bounds.
            const model = editor.getModel();
            if (!model) { clearFocus(); return; }
            const visibleRanges = editor.getVisibleRanges();
            const fallbackLine = visibleRanges[0]?.startLineNumber ?? 1;
            scheduleSend({
                scope: 'editor', file,
                line: fallbackLine, column: 1, dx: 0, dy: 0,
                ts: Date.now(),
            });
            return;
        }
        // Sub-cell offset: compute the pixel position the cell would
        // be rendered at on us, vs. the actual mouse position, and
        // express the delta as a fraction of a typical cell. This
        // gives smooth movement WITHIN a character without snapping.
        const cellTopLeft = editor.getScrolledVisiblePosition(pos);
        const browserEv = ev.event.browserEvent as MouseEvent | undefined;
        let dx = 0, dy = 0;
        if (cellTopLeft && browserEv) {
            const dom = editor.getDomNode();
            const rect = dom?.getBoundingClientRect();
            if (rect) {
                const localX = browserEv.clientX - rect.left;
                const localY = browserEv.clientY - rect.top;
                // Cell sizing — use line height for y; for x, approximate
                // with the editor's typical char width (fontInfo).
                const lineHeight = (editor.getOption(monaco.editor.EditorOption.lineHeight) as number) || 18;
                const charWidth = approximateCharWidth(editor);
                if (lineHeight > 0) dy = clamp01((localY - cellTopLeft.top) / lineHeight);
                if (charWidth > 0) dx = clamp01((localX - cellTopLeft.left) / charWidth);
            }
        }
        scheduleSend({
            scope: 'editor', file,
            line: pos.lineNumber, column: pos.column,
            dx, dy,
            ts: Date.now(),
        });
    });
    const editorBlurDispose = editor.onDidBlurEditorWidget(() => clearFocus());
    // Scroll changes don't move the SENDER's cursor (their mouse is
    // where it is), but receivers depend on a fresh awareness write
    // to recompute the on-screen position when *they* scroll. The
    // receiver triggers its own redraw on scroll separately (see
    // editor.onDidScrollChange below).
    const editorScrollDispose = editor.onDidScrollChange(() => scheduleRender());

    // Game panel mouse tracking. We listen on the .panel-cell (parent
    // of the iframes) so mouseleave fires when the pointer leaves the
    // panel. Iframe mousemove events don't bubble across the document
    // boundary, BUT the iframe is same-origin (allow-same-origin set
    // on the sandbox), so we can install a listener inside its
    // contentDocument as well.
    function onGameMove(e: MouseEvent) {
        if (!gameCell) return;
        const rect = gameCell.getBoundingClientRect();
        if (rect.width === 0 || rect.height === 0) return;
        const nx = clamp01((e.clientX - rect.left) / rect.width);
        const ny = clamp01((e.clientY - rect.top) / rect.height);
        scheduleSend({ scope: 'game', nx, ny, ts: Date.now() });
    }
    function onGameLeave() { clearFocus(); }
    gameCell?.addEventListener('mousemove', onGameMove);
    gameCell?.addEventListener('mouseleave', onGameLeave);

    // Bridge mousemove events from inside the preview iframes so the
    // cursor stays visible when hovering directly over the game
    // canvas. Iframes are SAME-ORIGIN (sandbox includes allow-same-
    // origin), so we can install listeners on contentDocument.
    //
    // Tricky bit: the monogame iframe is created LAZILY by
    // monogame-host the first time the host runs — it doesn't exist
    // at mount time. A MutationObserver on the game cell catches the
    // iframe whenever it appears (or is reloaded with a new src) and
    // re-bridges. Combined with the per-iframe `load` handler this
    // covers both "iframe is already in the DOM" and "iframe shows
    // up later" cases.
    const iframeMoveDisposers: Array<() => void> = [];
    const bridgedIframes = new WeakSet<HTMLIFrameElement>();
    function bridgeIframe(iframe: HTMLIFrameElement) {
        if (bridgedIframes.has(iframe)) return;
        bridgedIframes.add(iframe);
        const wire = () => {
            try {
                const doc = iframe.contentDocument;
                if (!doc) return;
                const handler = (e: MouseEvent) => {
                    const ifr = iframe.getBoundingClientRect();
                    const fakeEv = {
                        clientX: ifr.left + e.clientX,
                        clientY: ifr.top + e.clientY,
                    } as MouseEvent;
                    onGameMove(fakeEv);
                };
                doc.addEventListener('mousemove', handler);
                const leaveHandler = () => clearFocus();
                doc.addEventListener('mouseleave', leaveHandler);
                iframeMoveDisposers.push(() => {
                    try {
                        doc.removeEventListener('mousemove', handler);
                        doc.removeEventListener('mouseleave', leaveHandler);
                    } catch { /* doc may be gone */ }
                });
            } catch { /* cross-origin or detached — skip */ }
        };
        iframe.addEventListener('load', wire);
        iframeMoveDisposers.push(() => iframe.removeEventListener('load', wire));
        // Wire immediately too in case the iframe is already loaded.
        wire();
    }
    function bridgeAllInGameCell() {
        if (!gameCell) return;
        gameCell.querySelectorAll('iframe').forEach((f) => bridgeIframe(f as HTMLIFrameElement));
    }
    bridgeAllInGameCell();
    const gameCellObserver = gameCell ? new MutationObserver(() => bridgeAllInGameCell()) : null;
    gameCellObserver?.observe(gameCell!, { childList: true, subtree: true });

    // Browser-level leave (tab switch / minimize) — clear so others
    // don't see a stale cursor frozen at our last position forever.
    const onWindowBlur = () => clearFocus();
    const onVisibilityChange = () => {
        if (document.visibilityState !== 'visible') clearFocus();
    };
    window.addEventListener('blur', onWindowBlur);
    document.addEventListener('visibilitychange', onVisibilityChange);

    // ── receiver: render remote peers' cursors ─────────────────────
    // Re-render on every awareness change. Each tick wipes + re-builds
    // both overlays + the tab-badge map. Cheap because peer count is
    // small (<10 typical) and DOM nodes are tiny.
    const cursorEls = new Map<number, HTMLElement>(); // clientId → cursor el
    let renderScheduled = false;
    function scheduleRender() {
        if (renderScheduled) return;
        renderScheduled = true;
        requestAnimationFrame(() => {
            renderScheduled = false;
            render();
        });
    }

    function render() {
        const peers = session.getState().peers as PeerView[];
        const selfId = session.awareness.clientID;
        const activeFile = getActiveFile();
        const seen = new Set<number>();
        // Per-file presence map: most-recent peer wins the color slot,
        // but we accumulate every name on the file for the tooltip.
        // Internal map shape: file → { color, ts, names }.
        const presence = new Map<string, { color: string; ts: number; names: string[] }>();

        const gameRect = gameOverlay.getBoundingClientRect();
        const editorRect = editorOverlay.getBoundingClientRect();

        for (const peer of peers) {
            if (peer.clientId === selfId) continue;
            if (!peer.focus) continue;
            seen.add(peer.clientId);

            if (peer.focus.scope === 'editor') {
                const file = peer.focus.file;
                // Track file presence regardless of whether this peer
                // is on our active file — workspace list + tab badges
                // both want to know which files are being edited.
                const existing = presence.get(file);
                if (!existing) {
                    presence.set(file, { color: peer.identity.color, ts: peer.focus.ts, names: [peer.identity.displayName] });
                } else {
                    existing.names.push(peer.identity.displayName);
                    if (peer.focus.ts >= existing.ts) {
                        existing.ts = peer.focus.ts;
                        existing.color = peer.identity.color;
                    }
                }
                // Render the in-editor cursor only when the peer's
                // file matches OUR active file. Otherwise they're
                // surfaced via the presence dot above.
                if (file !== activeFile) continue;
                if (editorRect.width === 0 || editorRect.height === 0) continue;
                const px = editor.getScrolledVisiblePosition({ lineNumber: peer.focus.line, column: peer.focus.column });
                if (!px) continue;
                const charW = approximateCharWidth(editor);
                const lineH = (editor.getOption(monaco.editor.EditorOption.lineHeight) as number) || 18;
                const x = px.left + peer.focus.dx * charW;
                const y = px.top + peer.focus.dy * lineH;
                positionCursor(peer, editorOverlay, x, y);
            }
            else if (peer.focus.scope === 'game') {
                if (gameRect.width === 0 || gameRect.height === 0) continue;
                positionCursor(peer, gameOverlay, peer.focus.nx * gameRect.width, peer.focus.ny * gameRect.height);
            }
        }

        // Drop cursors for peers we didn't see this frame (gone or
        // focus became null).
        for (const [clientId, el] of cursorEls) {
            if (!seen.has(clientId)) {
                el.remove();
                cursorEls.delete(clientId);
            }
        }

        // Forward the cleaned presence map (no internal ts).
        const out = new Map<string, { color: string; names: string[] }>();
        for (const [file, p] of presence) {
            out.set(file, { color: p.color, names: p.names });
        }
        try { setPeerFilePresence(out); }
        catch (e) { console.warn('[shared-cursor] setPeerFilePresence threw', e); }
    }

    function positionCursor(peer: PeerView, parent: HTMLElement, x: number, y: number) {
        let el = cursorEls.get(peer.clientId);
        if (!el) {
            el = makeCursorEl(peer.identity.displayName, peer.identity.color);
            cursorEls.set(peer.clientId, el);
        }
        if (el.parentElement !== parent) parent.appendChild(el);
        el.style.transform = `translate(${x}px, ${y}px)`;
        // Update the name + color if the peer's identity changed.
        const nameEl = el.querySelector<HTMLElement>('.shared-cursor-name');
        if (nameEl && nameEl.textContent !== peer.identity.displayName) {
            nameEl.textContent = peer.identity.displayName;
        }
        el.style.setProperty('--cursor-color', peer.identity.color);
    }

    // Subscribe to awareness changes — the awareness emits on every
    // peer state mutation (including our own setLocalStateField calls,
    // which we don't care about, but the rAF debounce makes the
    // self-edit re-render essentially free).
    const onAwarenessChange = () => scheduleRender();
    session.awareness.on('change', onAwarenessChange);
    // Initial paint.
    scheduleRender();

    return {
        dispose() {
            // Sender teardown.
            editorMoveDispose.dispose();
            editorBlurDispose.dispose();
            editorScrollDispose.dispose();
            gameCell?.removeEventListener('mousemove', onGameMove);
            gameCell?.removeEventListener('mouseleave', onGameLeave);
            window.removeEventListener('blur', onWindowBlur);
            document.removeEventListener('visibilitychange', onVisibilityChange);
            for (const fn of iframeMoveDisposers) { try { fn(); } catch { /* ignore */ } }
            iframeMoveDisposers.length = 0;
            gameCellObserver?.disconnect();

            // Receiver teardown.
            try { session.awareness.off('change', onAwarenessChange); }
            catch { /* ignore */ }
            for (const el of cursorEls.values()) el.remove();
            cursorEls.clear();
            gameOverlay.remove();
            editorOverlay.remove();
            try { setPeerFilePresence(new Map()); }
            catch { /* ignore */ }
            // Clear our broadcast focus so peers stop seeing us.
            try { session.setFocus(null); } catch { /* ignore */ }
        },
        notifyActiveFileChanged() { scheduleRender(); },
        pulseCursor(clientId: number) {
            const el = cursorEls.get(clientId);
            if (!el) return;
            // Re-trigger the CSS animation by removing + re-adding
            // the class. requestAnimationFrame ensures the browser
            // commits the removal before the next add.
            el.classList.remove('shared-cursor-pulse');
            // eslint-disable-next-line no-void
            void el.offsetWidth; // force reflow
            el.classList.add('shared-cursor-pulse');
            window.setTimeout(() => el.classList.remove('shared-cursor-pulse'), 1400);
        },
    };
}

function makeOverlay(id: string): HTMLElement {
    const el = document.createElement('div');
    el.id = id;
    el.style.position = 'absolute';
    el.style.inset = '0';
    el.style.pointerEvents = 'none';
    el.style.zIndex = '60'; // above #game-stream-overlay (z-index 50)
    return el;
}

/** One cursor element: a small SVG arrow + colored name pill. The
 *  whole thing is transformed via translate() on each render so we
 *  don't churn the DOM tree on every frame. */
function makeCursorEl(name: string, color: string): HTMLElement {
    const wrap = document.createElement('div');
    wrap.className = 'shared-cursor';
    wrap.style.setProperty('--cursor-color', color);
    wrap.style.position = 'absolute';
    wrap.style.left = '0';
    wrap.style.top = '0';
    wrap.style.pointerEvents = 'none';
    wrap.style.willChange = 'transform';
    wrap.innerHTML = `
        <svg class="shared-cursor-arrow" width="14" height="18" viewBox="0 0 14 18" fill="none" xmlns="http://www.w3.org/2000/svg">
            <path d="M1 1 L1 14 L4.5 11 L7 17 L9.5 16 L7 10 L11 10 Z"
                  fill="var(--cursor-color)" stroke="#000" stroke-width="0.8"
                  stroke-linejoin="round" />
        </svg>
        <span class="shared-cursor-name"></span>
    `;
    const nameEl = wrap.querySelector<HTMLElement>('.shared-cursor-name')!;
    nameEl.textContent = name;
    return wrap;
}

function clamp01(v: number): number {
    if (!Number.isFinite(v)) return 0;
    if (v < 0) return 0;
    if (v > 1) return 1;
    return v;
}

/** Approximate width of one character in the editor's current font.
 *  Monaco's fontInfo isn't exposed via the public API as a stable
 *  shape, so we measure once and cache. Used for sub-cell offsets
 *  when rendering the cursor between two characters. */
const charWidthCache = new WeakMap<monaco.editor.IStandaloneCodeEditor, number>();
function approximateCharWidth(editor: monaco.editor.IStandaloneCodeEditor): number {
    const cached = charWidthCache.get(editor);
    if (cached) return cached;
    const dom = editor.getDomNode();
    if (!dom) return 7;
    // Use Monaco's measure helper if available; otherwise fall back
    // to creating a measurement span with the editor's computed font.
    const probe = document.createElement('span');
    probe.textContent = '0';
    probe.style.cssText = 'position:absolute;visibility:hidden;white-space:pre;font:inherit;';
    dom.appendChild(probe);
    const w = probe.getBoundingClientRect().width || 7;
    probe.remove();
    charWidthCache.set(editor, w);
    return w;
}
