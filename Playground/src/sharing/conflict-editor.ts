// Monaco-backed conflict resolution editor. One instance per conflicting
// file; mounts into a dedicated dockview panel.
//
// UI:
//   ┌─ Header ─────────────────────────────────────────────────────────────┐
//   │ <path>     N conflict(s) remaining                  [Save & close]   │
//   ├─ Per-region toolbar (one row per region in the file) ───────────────┤
//   │ #1 line 14   [Accept mine] [Accept theirs] [Both]   [Jump to →]      │
//   │ #2 line 42   [Accept mine] [Accept theirs] [Both]   [Jump to →]      │
//   ├─ Monaco editor on the file's existing model ────────────────────────┤
//   │  (full editing; the buttons just patch the model with the right     │
//   │   text and re-render)                                                │
//   └──────────────────────────────────────────────────────────────────────┘
//
// The model is owned by the host (main.ts opens the file via the regular
// tab system first, then hands the model to this module). Changes here
// flow through Monaco's onDidChangeContent the same way as a normal edit,
// so the collaboration gutter + autosave + file-list badge stay coherent.

import * as monaco from 'monaco-editor';
import { parseConflictRegions, type ParsedConflictRegion, hasConflictMarkers } from './diff3';
import { getLogger } from '../log-bus';

const CSS_PREFIX = 'fade-conflict';
const STYLE_ID = `${CSS_PREFIX}-styles`;

export interface ConflictEditorOptions {
    container: HTMLElement;
    /** Path of the file (for header + log messages + temp-model URI). */
    path: string;
    /** Initial content of the file (with `<<<<<<<...>>>>>>>` markers).
     *  The editor creates its own throwaway Monaco model from this; the
     *  regular tab's model is left alone, and edits only land in OPFS via
     *  `onSave`. */
    initialContent: string;
    /** Language id for syntax highlighting (e.g. 'fade', 'json'). Default
     *  is plaintext. */
    languageId?: string;
    /** Fired when the user clicks **Save & close** with no markers left.
     *  The host writes the supplied text back to OPFS and closes the panel. */
    onSave: (path: string, content: string) => Promise<void> | void;
    /** Fired when the user clicks **Close** without saving — discards in-
     *  memory edits. */
    onClose: () => void;
}

export interface ConflictEditorHandle {
    dispose(): void;
}

export function mountConflictEditor(opts: ConflictEditorOptions): ConflictEditorHandle {
    injectStylesOnce();
    const log = getLogger('sharing');
    const root = opts.container;
    root.classList.add(`${CSS_PREFIX}-root`);
    root.replaceChildren();

    // ─── DOM ───────────────────────────────────────────────────────────────
    const header = el('div', `${CSS_PREFIX}-header`);
    const title = el('div', `${CSS_PREFIX}-title`);
    title.textContent = opts.path;
    const counter = el('div', `${CSS_PREFIX}-counter`);
    const closeBtn = button('Discard & close', () => {
        log.info(`Discarded conflict edits for ${opts.path}`);
        opts.onClose();
    }, 'ghost');
    const doneBtn = button('Save & close', async () => {
        if (hasConflictMarkers(model.getValue())) {
            log.warn(`Cannot save ${opts.path}: still has conflict markers`);
            counter.classList.add(`${CSS_PREFIX}-shake`);
            setTimeout(() => counter.classList.remove(`${CSS_PREFIX}-shake`), 400);
            return;
        }
        log.info(`Resolved ${opts.path}`);
        await opts.onSave(opts.path, model.getValue());
    }, 'primary');
    const headerActions = el('div', `${CSS_PREFIX}-header-actions`);
    headerActions.append(closeBtn, doneBtn);
    header.append(title, counter, headerActions);

    const regionList = el('div', `${CSS_PREFIX}-regions`);

    const monacoHost = el('div', `${CSS_PREFIX}-editor-host`);

    root.append(header, regionList, monacoHost);

    // ─── Throwaway model holding the in-flight merge ──────────────────────
    // We never use the regular tab's model directly — that would trigger
    // the editor's 600ms autosave-to-OPFS on every Accept-mine click, and
    // the user reasonably expects edits to stay in-memory until Save.
    // A URI under `inmemory://conflict/<path>` is unique to this panel
    // instance; we dispose it on close.
    const modelUri = monaco.Uri.parse(`inmemory://conflict/${encodeURIComponent(opts.path)}`);
    // If a stale model survived a previous panel close (shouldn't happen
    // with our dispose path, but defensive), drop it before recreating.
    const stale = monaco.editor.getModel(modelUri);
    if (stale) stale.dispose();
    const model = monaco.editor.createModel(opts.initialContent, opts.languageId, modelUri);

    const editor = monaco.editor.create(monacoHost, {
        model,
        automaticLayout: true,
        readOnly: false,
        minimap: { enabled: false },
        renderLineHighlight: 'all',
    });

    // Decoration collection for the conflict-region highlighting.
    const decorations = editor.createDecorationsCollection([]);

    function refresh() {
        const regions = parseConflictRegions(model.getValue());
        renderRegions(regions);
        applyDecorations(regions);
        updateCounter(regions.length);
    }

    function updateCounter(remaining: number) {
        if (remaining === 0) {
            counter.textContent = 'no conflicts remaining — ready to save';
            counter.className = `${CSS_PREFIX}-counter ${CSS_PREFIX}-counter-clean`;
            doneBtn.removeAttribute('disabled');
            doneBtn.title = '';
        } else {
            counter.textContent = `${remaining} conflict${remaining === 1 ? '' : 's'} remaining`;
            counter.className = `${CSS_PREFIX}-counter ${CSS_PREFIX}-counter-busy`;
            doneBtn.setAttribute('disabled', 'true');
            doneBtn.title = 'Resolve all conflict markers first';
        }
    }

    function renderRegions(regions: ParsedConflictRegion[]) {
        regionList.replaceChildren();
        if (regions.length === 0) return;
        regions.forEach((r, i) => {
            const row = el('div', `${CSS_PREFIX}-region-row`);
            const label = el('div', `${CSS_PREFIX}-region-label`);
            label.textContent = `#${i + 1}  line ${r.startLine}`;
            const acceptMine = button(`Accept mine${r.oursLabel ? ` (${r.oursLabel})` : ''}`,
                () => resolveRegion(r, 'mine'), 'mine-small');
            const acceptTheirs = button(`Accept theirs${r.theirsLabel ? ` (${r.theirsLabel})` : ''}`,
                () => resolveRegion(r, 'theirs'), 'theirs-small');
            const acceptBoth = button('Accept both', () => resolveRegion(r, 'both'), 'ghost-small');
            const jump = button('Jump to', () => {
                editor.revealLineNearTop(r.startLine);
                editor.setPosition({ lineNumber: r.startLine, column: 1 });
                editor.focus();
            }, 'ghost-small');
            row.append(label, acceptMine, acceptTheirs, acceptBoth, jump);
            regionList.append(row);
        });
    }

    function applyDecorations(regions: ParsedConflictRegion[]) {
        const decos: monaco.editor.IModelDeltaDecoration[] = [];
        for (const r of regions) {
            // Highlight the "ours" lines (between start+1 and mid-1).
            if (r.midLine > r.startLine + 1) {
                decos.push({
                    range: new monaco.Range(r.startLine + 1, 1, r.midLine - 1, 1),
                    options: { isWholeLine: true, className: `${CSS_PREFIX}-ours-bg` },
                });
            }
            // Highlight the "theirs" lines.
            if (r.endLine > r.midLine + 1) {
                decos.push({
                    range: new monaco.Range(r.midLine + 1, 1, r.endLine - 1, 1),
                    options: { isWholeLine: true, className: `${CSS_PREFIX}-theirs-bg` },
                });
            }
            // Marker lines: subtle bg so they're easier to spot.
            decos.push({
                range: new monaco.Range(r.startLine, 1, r.startLine, 1),
                options: { isWholeLine: true, className: `${CSS_PREFIX}-marker-bg` },
            });
            decos.push({
                range: new monaco.Range(r.midLine, 1, r.midLine, 1),
                options: { isWholeLine: true, className: `${CSS_PREFIX}-marker-bg` },
            });
            decos.push({
                range: new monaco.Range(r.endLine, 1, r.endLine, 1),
                options: { isWholeLine: true, className: `${CSS_PREFIX}-marker-bg` },
            });
        }
        decorations.set(decos);
    }

    function resolveRegion(r: ParsedConflictRegion, choice: 'mine' | 'theirs' | 'both') {
        let replacement: string;
        switch (choice) {
            case 'mine':   replacement = r.ours.join('\n');   break;
            case 'theirs': replacement = r.theirs.join('\n'); break;
            case 'both':
                // ours followed by theirs, deduped trivially if identical.
                replacement = (r.ours.join('\n') === r.theirs.join('\n'))
                    ? r.ours.join('\n')
                    : r.ours.join('\n') + (r.ours.length && r.theirs.length ? '\n' : '') + r.theirs.join('\n');
                break;
        }
        // Replace lines [startLine..endLine] (inclusive) with the chosen
        // text. Monaco ranges are end-exclusive on column, so we go from
        // (startLine, 1) to (endLine + 1, 1) to absorb the trailing newline.
        const range = new monaco.Range(r.startLine, 1, r.endLine + 1, 1);
        const text = replacement.length > 0 ? replacement + '\n' : '';
        model.pushEditOperations(
            [],
            [{ range, text }],
            () => null,
        );
        log.info(`Accepted ${choice} for ${opts.path} at line ${r.startLine}`);
        // refresh() is triggered by the onDidChangeContent listener below.
    }

    const sub = model.onDidChangeContent(() => refresh());
    refresh();

    return {
        dispose() {
            sub.dispose();
            decorations.clear();
            editor.dispose();
            // The temp model is private to this panel — dispose it so it
            // doesn't linger in Monaco's global model registry.
            model.dispose();
            root.replaceChildren();
            root.classList.remove(`${CSS_PREFIX}-root`);
        },
    };
}

// ─── DOM + style helpers ───────────────────────────────────────────────────

function el<K extends keyof HTMLElementTagNameMap>(tag: K, cls: string): HTMLElementTagNameMap[K] {
    const n = document.createElement(tag);
    n.className = cls;
    return n;
}

type BtnVariant = 'primary' | 'ghost' | 'mine-small' | 'theirs-small' | 'ghost-small';
function button(label: string, onClick: () => void | Promise<void>, variant: BtnVariant): HTMLButtonElement {
    const b = document.createElement('button');
    b.className = `${CSS_PREFIX}-btn ${CSS_PREFIX}-btn-${variant}`;
    b.type = 'button';
    b.textContent = label;
    b.onclick = () => { void onClick(); };
    return b;
}

function injectStylesOnce(): void {
    if (document.getElementById(STYLE_ID)) return;
    const style = document.createElement('style');
    style.id = STYLE_ID;
    style.textContent = `
.${CSS_PREFIX}-root {
    display: flex; flex-direction: column;
    height: 100%; box-sizing: border-box;
    overflow: hidden;
    color: var(--vscode-foreground, #ddd);
    font: 13px/1.4 ui-sans-serif, system-ui, sans-serif;
}
.${CSS_PREFIX}-header {
    display: flex; align-items: center; gap: 12px;
    padding: 6px 12px;
    border-bottom: 1px solid var(--vscode-panel-border, #333);
    flex-shrink: 0;
}
.${CSS_PREFIX}-title {
    font-family: ui-monospace, monospace; font-size: 12px;
    flex-shrink: 0;
}
.${CSS_PREFIX}-counter {
    flex: 1 1 auto;
    font-size: 12px;
    padding: 2px 8px;
    border-radius: 3px;
}
.${CSS_PREFIX}-counter-busy  { background: rgba(255,140,90,0.15); color: #ffb074; }
.${CSS_PREFIX}-counter-clean { background: rgba(110,230,110,0.15); color: #8e8; }
.${CSS_PREFIX}-counter.${CSS_PREFIX}-shake {
    animation: ${CSS_PREFIX}-shake 0.3s;
}
@keyframes ${CSS_PREFIX}-shake {
    0%,100% { transform: translateX(0); }
    25% { transform: translateX(-4px); }
    75% { transform: translateX(4px); }
}
.${CSS_PREFIX}-header-actions { display: flex; gap: 6px; }
.${CSS_PREFIX}-regions {
    border-bottom: 1px solid var(--vscode-panel-border, #333);
    padding: 4px 8px;
    flex-shrink: 0;
    max-height: 30vh; overflow-y: auto;
    background: rgba(255,255,255,0.02);
}
.${CSS_PREFIX}-region-row {
    display: flex; align-items: center; gap: 6px;
    padding: 3px 0;
    flex-wrap: wrap;
}
.${CSS_PREFIX}-region-label {
    font-family: ui-monospace, monospace; font-size: 12px;
    color: #fc6;
    min-width: 110px;
}
.${CSS_PREFIX}-editor-host {
    flex: 1 1 auto; min-height: 0;
}
/* Per-side line backgrounds in the editor. Subtle so the editor text is
   still readable; the marker lines stand out more strongly. */
.${CSS_PREFIX}-ours-bg    { background: rgba(110,230,110,0.10); }
.${CSS_PREFIX}-theirs-bg  { background: rgba(120,180,255,0.10); }
.${CSS_PREFIX}-marker-bg  { background: rgba(255,140,90,0.18); }

.${CSS_PREFIX}-btn {
    appearance: none; border: 0; cursor: pointer;
    padding: 4px 10px; border-radius: 4px;
    font: inherit; font-size: 12px; font-weight: 500;
    transition: filter 0.1s;
}
.${CSS_PREFIX}-btn:hover { filter: brightness(1.15); }
.${CSS_PREFIX}-btn:disabled { opacity: 0.4; cursor: not-allowed; filter: none; }
.${CSS_PREFIX}-btn-primary {
    background: var(--vscode-button-background, #0e639c);
    color: var(--vscode-button-foreground, #fff);
}
.${CSS_PREFIX}-btn-ghost {
    background: transparent;
    color: inherit;
    border: 1px solid var(--vscode-panel-border, #555);
}
.${CSS_PREFIX}-btn-ghost-small {
    background: transparent; color: inherit;
    border: 1px solid var(--vscode-panel-border, #555);
    padding: 2px 8px; font-size: 11px;
}
.${CSS_PREFIX}-btn-mine-small {
    background: rgba(110,230,110,0.18); color: #afe9af;
    border: 1px solid rgba(110,230,110,0.4);
    padding: 2px 8px; font-size: 11px;
}
.${CSS_PREFIX}-btn-theirs-small {
    background: rgba(120,180,255,0.18); color: #b8d6ff;
    border: 1px solid rgba(120,180,255,0.4);
    padding: 2px 8px; font-size: 11px;
}
`;
    document.head.appendChild(style);
}
