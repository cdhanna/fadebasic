// Markdown preview panel host. One instance per opened .md file; lives as
// a dockview panel of component "markdown-preview". Subscribes to the
// matching Monaco model and re-renders on every content change.

import * as monaco from 'monaco-editor';
import { marked } from 'marked';

// Resolves the model URI for a given workspace file name. Mirrors
// `monaco.Uri.file('/workspace/${name}')` from main.ts.
function uriFor(name: string): monaco.Uri {
    return monaco.Uri.file(`/workspace/${name}`);
}

function findModel(name: string): monaco.editor.ITextModel | null {
    const uri = uriFor(name);
    return monaco.editor.getModel(uri);
}

// Minimal sanitization: strip script/style tags + on* attributes. marked
// gives us HTML; we control the input (user-authored docs in their own
// workspace) but still don't want a stray <script> hijacking the page.
function scrub(html: string): string {
    return html
        .replace(/<\s*(script|style|iframe|object|embed)[^>]*>[\s\S]*?<\s*\/\s*\1\s*>/gi, '')
        .replace(/\son[a-z]+\s*=\s*"[^"]*"/gi, '')
        .replace(/\son[a-z]+\s*=\s*'[^']*'/gi, '')
        .replace(/javascript:/gi, '');
}

export interface MarkdownPreviewHandle {
    element: HTMLElement;
    init(): void;
    dispose(): void;
    // dockview calls update() when params change (rare for us).
    update?(): void;
}

// Build a preview host bound to a file name. Returns the dockview-shaped
// {element, init, dispose} contract so it can be created on demand.
export function createMarkdownPreview(filename: string): MarkdownPreviewHandle {
    const root = document.createElement('div');
    root.className = 'md-preview-host';
    root.dataset.filename = filename;

    const toolbar = document.createElement('div');
    toolbar.className = 'md-preview-toolbar';
    const title = document.createElement('span');
    title.className = 'md-preview-title';
    title.textContent = filename;
    toolbar.append(title);

    const body = document.createElement('div');
    body.className = 'md-preview-body';

    root.append(toolbar, body);

    let subscription: monaco.IDisposable | null = null;
    let rerenderTimer: number | undefined;
    let mountedModelUri: string | null = null;

    function render(text: string) {
        try {
            // marked.parse returns string when async=false (default).
            const html = marked.parse(text, { async: false, gfm: true, breaks: false }) as string;
            body.innerHTML = scrub(html);
        } catch (e: any) {
            body.innerHTML = '';
            const err = document.createElement('pre');
            err.className = 'md-preview-error';
            err.textContent = 'Failed to render markdown: ' + (e?.message ?? e);
            body.append(err);
        }
    }

    function scheduleRender(model: monaco.editor.ITextModel) {
        if (rerenderTimer != null) clearTimeout(rerenderTimer);
        rerenderTimer = window.setTimeout(() => render(model.getValue()), 80);
    }

    function attach(model: monaco.editor.ITextModel) {
        subscription?.dispose();
        subscription = model.onDidChangeContent(() => scheduleRender(model));
        mountedModelUri = model.uri.toString();
        render(model.getValue());
    }

    // Poll briefly for the model — the preview button can fire before the
    // model is registered (rare, but the editor lazily creates models on
    // first open).
    let attachAttempts = 0;
    function tryAttach() {
        const m = findModel(filename);
        if (m) { attach(m); return; }
        attachAttempts++;
        if (attachAttempts > 40) {
            body.innerHTML = '';
            const msg = document.createElement('div');
            msg.className = 'md-preview-empty';
            msg.textContent = `Open ${filename} first to enable preview.`;
            body.append(msg);
            return;
        }
        setTimeout(tryAttach, 100);
    }

    return {
        element: root,
        init() { tryAttach(); },
        update() {
            // If a different model with the same URI was swapped in, rebind.
            const m = findModel(filename);
            if (m && m.uri.toString() !== mountedModelUri) attach(m);
        },
        dispose() {
            subscription?.dispose();
            subscription = null;
            if (rerenderTimer != null) clearTimeout(rerenderTimer);
        },
    };
}

// Deterministic dockview panel id for a given file's preview. Keeping the
// id stable means "open preview" toggles between activating an existing
// panel and creating a new one — no duplicate previews per file.
export function previewPanelIdFor(filename: string): string {
    return `md-preview:${filename}`;
}
