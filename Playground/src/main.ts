// Playground: Monaco editor + custom shell with OPFS-backed file list and tabs.
//
// Layout:
//   ┌─ header (title, status, heartbeat, Run) ────────────────────┐
//   ├─ sidebar ┬─ tabs ──────────────────────────────────────────┤
//   │ files    │  main.fbasic [x]  other.fbasic [x]              │
//   │          ├─────────────────────────────────────────────────┤
//   │          │  Monaco editor                                  │
//   │          │                                                 │
//   ├─ Output ─┴─────────────────────────────────────────────────┤
//   │ ... program output ...                                     │
//   └────────────────────────────────────────────────────────────┘

import * as monaco from 'monaco-editor';
import 'vscode/localExtensionHost';
import { initialize as initServices } from '@codingame/monaco-vscode-api';

// vscode-look web components used in the bottom panel + header.
import '@vscode-elements/elements/dist/vscode-badge';
import '@vscode-elements/elements/dist/vscode-button';
import '@vscode-elements/elements/dist/vscode-icon';

// Dockview: dockable / resizable panel layout. We use the vanilla-JS
// flavor (dockview-core). The page itself acts as the framework
// adapter via `createComponent`, returning <div>s pulled out of the
// hidden #panel-cells pool.
import { createDockview } from 'dockview-core';
import type { DockviewApi, SerializedDockview } from 'dockview-core';
import 'dockview-core/dist/styles/dockview.css';
// Codicons stylesheet URL — required for <vscode-icon> glyphs. The
// vscode-elements library specifically looks for a <link> element with
// id="vscode-codicon-stylesheet"; a Vite-injected <style> tag won't satisfy
// the check. Use ?url to grab the bundled URL and inject the link ourselves.
import codiconsUrl from '@vscode/codicons/dist/codicon.css?url';
{
    const link = document.createElement('link');
    link.id = 'vscode-codicon-stylesheet';
    link.rel = 'stylesheet';
    link.href = codiconsUrl;
    document.head.appendChild(link);
}

import getModelServiceOverride from '@codingame/monaco-vscode-model-service-override';
import getEditorServiceOverride from '@codingame/monaco-vscode-editor-service-override';
import getConfigurationServiceOverride, {
    updateUserConfiguration,
} from '@codingame/monaco-vscode-configuration-service-override';
import getKeybindingsServiceOverride from '@codingame/monaco-vscode-keybindings-service-override';
import getLanguagesServiceOverride from '@codingame/monaco-vscode-languages-service-override';
// Register a virtual file-system overlay so codingame's BrowserTextFileService
// can resolveFromFile() without throwing "Unable to resolve nonexistent file"
// when the editor tries to open one of our model URIs.
import {
    registerFileSystemOverlay,
    RegisteredFileSystemProvider,
    RegisteredMemoryFile,
} from '@codingame/monaco-vscode-files-service-override';

// Virtual in-memory provider for the `file:` scheme. We seed each OPFS-backed
// file into this when it's opened so the editor service can resolve it.
const virtualFs = new RegisteredFileSystemProvider(false);
registerFileSystemOverlay(1, virtualFs);

import EditorWorker from '@codingame/monaco-vscode-api/workers/editor.worker?worker';
(self as any).MonacoEnvironment = {
    getWorker: () => new EditorWorker(),
};
// Expose monaco globally for diagnostic probing from Playwright
(window as any).monaco = monaco;

const DEFAULT_SOURCE = [
    'print upper$("hello from the playground")',
    'for n = 1 to 5',
    '  print "tick " + str$(n)',
    '  wait ms(300)',
    'next',
    'x = rnd(100)',
    'y = x * 2',
].join('\n');

// ─── DOM refs ───────────────────────────────────────────────────────────────
const statusEl = document.getElementById('status')!;
// runBtn is a <vscode-button> custom element; it accepts `disabled` as an
// attribute just like a native button, but it isn't an HTMLButtonElement.
const runBtn = document.getElementById('run') as HTMLElement & { disabled: boolean };
const debugBtn = document.getElementById('debug') as HTMLElement & { disabled: boolean };
const resetLayoutBtn = document.getElementById('reset-layout') as HTMLElement;
const newFileBtn = document.getElementById('new-file') as HTMLButtonElement;
const fileListEl = document.getElementById('file-list')!;
const tabsEl = document.getElementById('tabs')!;
const editorContainer = document.getElementById('editor')!;
const editorPlaceholder = document.getElementById('editor-placeholder')!;
const outputEl = document.getElementById('output')!;
// ─── OPFS workspace ─────────────────────────────────────────────────────────
class OpfsWorkspace {
    private dir!: FileSystemDirectoryHandle;

    async init() {
        const root = await navigator.storage.getDirectory();
        this.dir = await root.getDirectoryHandle('workspace', { create: true });
        // Seed default file if workspace is empty
        const names = await this.list();
        if (names.length === 0) {
            await this.write('main.fbasic', DEFAULT_SOURCE);
        }
    }

    async list(): Promise<string[]> {
        const names: string[] = [];
        for await (const entry of (this.dir as any).values()) {
            if (entry.kind === 'file') names.push(entry.name);
        }
        names.sort();
        return names;
    }

    async read(name: string): Promise<string> {
        const fh = await this.dir.getFileHandle(name);
        const f = await fh.getFile();
        return await f.text();
    }

    async write(name: string, content: string): Promise<void> {
        const fh = await this.dir.getFileHandle(name, { create: true });
        const w = await fh.createWritable();
        await w.write(content);
        await w.close();
    }

    async delete(name: string): Promise<void> {
        await this.dir.removeEntry(name);
    }
}

// ─── Tabs + model management ────────────────────────────────────────────────
interface Tab {
    name: string;
    model: monaco.editor.ITextModel;
    dirty: boolean;
    saveTimer?: number;
}

const tabs = new Map<string, Tab>();
let activeName: string | null = null;
let editor: monaco.editor.IStandaloneCodeEditor | null = null;

function languageFor(name: string): string {
    if (name.endsWith('.fbasic') || name.endsWith('.fb')) return 'fade';
    return 'plaintext';
}

async function openFile(workspace: OpfsWorkspace, name: string) {
    let tab = tabs.get(name);
    if (!tab) {
        const text = await workspace.read(name);
        // file: scheme so codingame's TextModelResolverService can resolve us.
        // We still persist to OPFS — this URI is just the model's identifier.
        const uri = monaco.Uri.file(`/workspace/${name}`);
        // Push this file into the virtual FS overlay so any vscode-side
        // editor open won't fail with "Unable to resolve nonexistent file".
        virtualFs.registerFile(new RegisteredMemoryFile(uri, text));
        let model = monaco.editor.getModel(uri);
        if (!model) {
            model = monaco.editor.createModel(text, languageFor(name), uri);
        }
        // Hook this model for LSP push + decoration (if available)
        (window as any).__fadeHookModel?.(model);
        tab = { name, model, dirty: false };
        // Debounced auto-save: 600ms idle → write to OPFS
        model.onDidChangeContent(() => {
            tab!.dirty = true;
            clearTimeout(tab!.saveTimer);
            tab!.saveTimer = window.setTimeout(async () => {
                try {
                    await workspace.write(tab!.name, tab!.model.getValue());
                    tab!.dirty = false;
                    renderTabs();
                } catch (e) {
                    console.error('[fade] save failed for', tab!.name, e);
                }
            }, 600);
            renderTabs();
        });
        tabs.set(name, tab);
    }
    activeName = name;
    if (editor) {
        editor.setModel(tab.model);
        editor.focus();
    }
    editorContainer.style.display = '';
    editorPlaceholder.style.display = 'none';
    renderTabs();
    renderFileList(workspace);
}

function closeTab(name: string) {
    const tab = tabs.get(name);
    if (!tab) return;
    // If saveTimer is pending, flush via the existing timer logic on next event;
    // we just remove from open list (model stays alive in monaco's model registry).
    tabs.delete(name);
    if (activeName === name) {
        // Switch to another tab or empty state
        const next = tabs.keys().next().value;
        if (next) {
            activeName = next;
            if (editor) editor.setModel(tabs.get(next)!.model);
        } else {
            activeName = null;
            if (editor) editor.setModel(null);
            editorContainer.style.display = 'none';
            editorPlaceholder.style.display = 'flex';
        }
    }
    renderTabs();
}

function renderTabs() {
    tabsEl.innerHTML = '';
    for (const [name, tab] of tabs) {
        const el = document.createElement('div');
        el.className = 'tab' + (name === activeName ? ' active' : '');
        const label = document.createElement('span');
        label.className = tab.dirty ? 'dirty' : '';
        label.textContent = (tab.dirty ? '● ' : '') + name;
        label.onclick = () => {
            activeName = name;
            if (editor) editor.setModel(tab.model);
            renderTabs();
            renderFileListSelection();
        };
        const close = document.createElement('span');
        close.className = 'close';
        close.textContent = '×';
        close.onclick = (e) => {
            e.stopPropagation();
            closeTab(name);
        };
        el.append(label, close);
        tabsEl.append(el);
    }
}

async function renderFileList(workspace: OpfsWorkspace) {
    const names = await workspace.list();
    fileListEl.innerHTML = '';
    for (const name of names) {
        const li = document.createElement('li');
        li.textContent = name;
        if (name === activeName) li.classList.add('active');
        li.onclick = () => openFile(workspace, name);
        fileListEl.append(li);
    }
}

function renderFileListSelection() {
    for (const li of Array.from(fileListEl.children) as HTMLElement[]) {
        li.classList.toggle('active', li.textContent === activeName);
    }
}

// Inverse of the URI build in openFile.
function uriToName(uri: string): string {
    const ix = uri.lastIndexOf('/');
    return ix >= 0 ? uri.slice(ix + 1) : uri;
}

// ─── runner ─────────────────────────────────────────────────────────────────
interface RunnerOpts {
    onPrint: (line: string) => void;
    onAlert: (msg: string) => void;
}

interface Diagnostic {
    severity: number;
    range: {
        start: { line: number; character: number };
        end: { line: number; character: number };
    };
    message: string;
    code: string;
    source: string;
}

// Single worker that handles BOTH run and LSP messages. We tried a dedicated
// LSP worker; same exact Fade-core calls that work in this worker hang in a
// second worker instance, for reasons that need separate investigation. For
// now: one worker, both responsibilities. Run blocks LSP for the duration of a
// program execution, which is acceptable for v1.
class FadeRunner {
    public worker: Worker;
    private opts: RunnerOpts;
    private nextId = 0;
    private pending = new Map<number, (result: any) => void>();
    private onDiagnostics?: (uri: string, diagnostics: Diagnostic[]) => void;
    // Shared-buffer protocol for synchronous prompt$ from worker → main thread.
    // promptSync[0] = sync slot (Atomics.wait), promptSync[1] = response length.
    private promptSab: SharedArrayBuffer | null = null;
    private promptSync: Int32Array | null = null;
    private promptBytes: Uint8Array | null = null;
    onPromptRequest?: (msg: string) => Promise<string | null> | string | null;
    onDebugEvent?: (event: DebugEvent) => void;
    ready: Promise<void>;

    constructor(opts: RunnerOpts) {
        this.opts = opts;
        this.worker = new Worker('/runtime/worker.js', { type: 'module' });

        // Shared buffer for synchronous prompt$. Layout matches worker.js's
        // syncPromptFromMain — Int32 sync slot + Int32 length + UTF-8 bytes.
        // crossOriginIsolated may be false in dev; we still post the buffer
        // so a hosted prod build with COOP/COEP works out of the box.
        if (typeof SharedArrayBuffer !== 'undefined') {
            try {
                this.promptSab = new SharedArrayBuffer(4096);
                this.promptSync = new Int32Array(this.promptSab, 0, 2);
                this.promptBytes = new Uint8Array(this.promptSab, 8);
                this.worker.postMessage({ type: 'prompt-sab', buffer: this.promptSab });
            } catch (e) {
                console.warn('[fade] SharedArrayBuffer unavailable — prompt$ will return empty:', e);
            }
        }

        this.ready = new Promise<void>((resolve, reject) => {
            this.worker.onmessage = (e) => {
                const msg = e.data;
                if (msg.type === 'ready') resolve();
                else if (msg.type === 'print') this.opts.onPrint(msg.line);
                else if (msg.type === 'alert') this.opts.onAlert(msg.msg);
                else if (msg.type === 'result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.result);
                } else if (msg.type === 'lsp-tokens-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.tokens);
                } else if (msg.type === 'lsp-hover-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.hover);
                } else if (msg.type === 'lsp-completion-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.items);
                } else if (msg.type === 'lsp-signature-help-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.sig);
                } else if (msg.type === 'lsp-references-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.refs);
                } else if (msg.type === 'lsp-definition-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.def);
                } else if (msg.type === 'lsp-document-symbols-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.symbols);
                } else if (msg.type === 'lsp-folding-ranges-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.ranges);
                } else if (msg.type === 'lsp-format-result'
                        || msg.type === 'lsp-format-range-result'
                        || msg.type === 'lsp-format-on-type-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.edits);
                } else if (msg.type === 'lsp-rename-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.edit);
                } else if (msg.type === 'list-tests-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.tests);
                } else if (msg.type === 'run-tests-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.result);
                } else if (msg.type === 'debug-start-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.result);
                } else if (msg.type === 'debug-terminate-result'
                        || msg.type === 'debug-set-breakpoints-result'
                        || msg.type === 'debug-step-result'
                        || msg.type === 'debug-continue-result'
                        || msg.type === 'debug-pause-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(true);
                } else if (msg.type === 'debug-stack-frames-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.frames);
                } else if (msg.type === 'debug-scopes-result'
                        || msg.type === 'debug-variable-expansion-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.scopes);
                } else if (msg.type === 'debug-eval-result'
                        || msg.type === 'debug-repl-result'
                        || msg.type === 'debug-set-variable-result') {
                    const r = this.pending.get(msg.id);
                    this.pending.delete(msg.id);
                    if (r) r(msg.result);
                } else if (msg.type === 'debug-event') {
                    if (this.onDebugEvent) this.onDebugEvent(msg.event);
                } else if (msg.type === 'prompt-request') {
                    this.handlePromptRequest(msg.msg);
                } else if (msg.type === 'lsp-diagnostics') {
                    if (this.onDiagnostics) {
                        const parsed: Diagnostic[] = JSON.parse(msg.diagnostics);
                        this.onDiagnostics(msg.uri, parsed);
                    }
                } else if (msg.type === 'log') {
                    console.log('[runtime worker]', msg.message);
                } else if (msg.type === 'boot-error') reject(new Error(msg.message));
            };
            this.worker.onerror = (e) => reject(new Error('runtime worker error: ' + e.message));
        });
    }

    run(source: string): Promise<string> {
        const id = ++this.nextId;
        return new Promise<string>((resolve) => {
            this.pending.set(id, resolve);
            this.worker.postMessage({ type: 'run', id, source });
        });
    }

    setDocument(uri: string, text: string) {
        this.worker.postMessage({ type: 'lsp-set', uri, text });
    }

    async getTokens(uri: string): Promise<number[]> {
        const id = ++this.nextId;
        return new Promise<number[]>((resolve) => {
            this.pending.set(id, (tokensJson: string) => {
                try { resolve(JSON.parse(tokensJson)); } catch { resolve([]); }
            });
            this.worker.postMessage({ type: 'lsp-tokens', id, uri });
        });
    }

    async getHover(uri: string, line: number, character: number): Promise<HoverInfo | null> {
        const id = ++this.nextId;
        return new Promise<HoverInfo | null>((resolve) => {
            this.pending.set(id, (hoverJson: string) => {
                try {
                    const parsed = JSON.parse(hoverJson);
                    resolve(parsed);
                } catch { resolve(null); }
            });
            this.worker.postMessage({ type: 'lsp-hover', id, uri, line, character });
        });
    }

    async getCompletions(uri: string, line: number, character: number): Promise<CompletionItem[]> {
        const id = ++this.nextId;
        return new Promise<CompletionItem[]>((resolve) => {
            this.pending.set(id, (itemsJson: string) => {
                try {
                    const parsed = JSON.parse(itemsJson);
                    resolve(Array.isArray(parsed) ? parsed : []);
                } catch { resolve([]); }
            });
            this.worker.postMessage({ type: 'lsp-completion', id, uri, line, character });
        });
    }

    async getSignatureHelp(uri: string, line: number, character: number): Promise<SignatureHelp | null> {
        const id = ++this.nextId;
        return new Promise<SignatureHelp | null>((resolve) => {
            this.pending.set(id, (sigJson: string) => {
                try { resolve(JSON.parse(sigJson)); } catch { resolve(null); }
            });
            this.worker.postMessage({ type: 'lsp-signature-help', id, uri, line, character });
        });
    }

    async getReferences(uri: string, line: number, character: number): Promise<Location[]> {
        const id = ++this.nextId;
        return new Promise<Location[]>((resolve) => {
            this.pending.set(id, (refsJson: string) => {
                try {
                    const parsed = JSON.parse(refsJson);
                    resolve(Array.isArray(parsed) ? parsed : []);
                } catch { resolve([]); }
            });
            this.worker.postMessage({ type: 'lsp-references', id, uri, line, character });
        });
    }

    async getDefinition(uri: string, line: number, character: number): Promise<Location | null> {
        const id = ++this.nextId;
        return new Promise<Location | null>((resolve) => {
            this.pending.set(id, (defJson: string) => {
                try { resolve(JSON.parse(defJson)); } catch { resolve(null); }
            });
            this.worker.postMessage({ type: 'lsp-definition', id, uri, line, character });
        });
    }

    async getDocumentSymbols(uri: string): Promise<DocSymbol[]> {
        const id = ++this.nextId;
        return new Promise<DocSymbol[]>((resolve) => {
            this.pending.set(id, (json: string) => {
                try {
                    const parsed = JSON.parse(json);
                    resolve(Array.isArray(parsed) ? parsed : []);
                } catch { resolve([]); }
            });
            this.worker.postMessage({ type: 'lsp-document-symbols', id, uri });
        });
    }

    async getFoldingRanges(uri: string): Promise<FoldingRange[]> {
        const id = ++this.nextId;
        return new Promise<FoldingRange[]>((resolve) => {
            this.pending.set(id, (json: string) => {
                try {
                    const parsed = JSON.parse(json);
                    resolve(Array.isArray(parsed) ? parsed : []);
                } catch { resolve([]); }
            });
            this.worker.postMessage({ type: 'lsp-folding-ranges', id, uri });
        });
    }

    async format(uri: string, options: FormattingOptions): Promise<TextEdit[]> {
        const id = ++this.nextId;
        return new Promise<TextEdit[]>((resolve) => {
            this.pending.set(id, (json: string) => {
                try {
                    const parsed = JSON.parse(json);
                    resolve(Array.isArray(parsed) ? parsed : []);
                } catch { resolve([]); }
            });
            this.worker.postMessage({ type: 'lsp-format', id, uri, options: JSON.stringify(options) });
        });
    }

    async formatRange(uri: string, options: FormattingOptions, range: { startLine: number; startCh: number; endLine: number; endCh: number }): Promise<TextEdit[]> {
        const id = ++this.nextId;
        return new Promise<TextEdit[]>((resolve) => {
            this.pending.set(id, (json: string) => {
                try {
                    const parsed = JSON.parse(json);
                    resolve(Array.isArray(parsed) ? parsed : []);
                } catch { resolve([]); }
            });
            this.worker.postMessage({
                type: 'lsp-format-range', id, uri,
                options: JSON.stringify(options),
                ...range,
            });
        });
    }

    async formatOnType(uri: string, options: FormattingOptions, line: number, character: number): Promise<TextEdit[]> {
        const id = ++this.nextId;
        return new Promise<TextEdit[]>((resolve) => {
            this.pending.set(id, (json: string) => {
                try {
                    const parsed = JSON.parse(json);
                    resolve(Array.isArray(parsed) ? parsed : []);
                } catch { resolve([]); }
            });
            this.worker.postMessage({
                type: 'lsp-format-on-type', id, uri,
                options: JSON.stringify(options),
                line, character,
            });
        });
    }

    async rename(uri: string, line: number, character: number, newName: string): Promise<WorkspaceEdit | null> {
        const id = ++this.nextId;
        return new Promise<WorkspaceEdit | null>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); } catch { resolve(null); }
            });
            this.worker.postMessage({ type: 'lsp-rename', id, uri, line, character, newName });
        });
    }

    async listTests(source: string): Promise<TestEntry[]> {
        const id = ++this.nextId;
        return new Promise<TestEntry[]>((resolve) => {
            this.pending.set(id, (json: string) => {
                try {
                    const parsed = JSON.parse(json);
                    resolve(Array.isArray(parsed) ? parsed : []);
                } catch { resolve([]); }
            });
            this.worker.postMessage({ type: 'list-tests', id, source });
        });
    }

    async runTests(source: string, testName?: string): Promise<TestRunResult> {
        const id = ++this.nextId;
        return new Promise<TestRunResult>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); }
                catch { resolve({ passed: 0, failed: 0, duration: 0, results: [], printed: '', error: 'parse failed' }); }
            });
            this.worker.postMessage({ type: 'run-tests', id, source, testName: testName || '' });
        });
    }

    // ── Debug session ─────────────────────────────────────────────────────
    async debugStart(source: string): Promise<DebugStartResult> {
        const id = ++this.nextId;
        return new Promise<DebugStartResult>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); }
                catch { resolve({ ok: false, error: 'parse failed', statementLines: [] }); }
            });
            this.worker.postMessage({ type: 'debug-start', id, source });
        });
    }
    async debugStartTest(source: string, testName: string): Promise<DebugStartResult> {
        const id = ++this.nextId;
        return new Promise<DebugStartResult>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); }
                catch { resolve({ ok: false, error: 'parse failed', statementLines: [] }); }
            });
            this.worker.postMessage({ type: 'debug-start-test', id, source, testName });
        });
    }
    debugTerminate(): Promise<boolean> { return this.simpleDebugCall('debug-terminate'); }
    debugContinue(): Promise<boolean> { return this.simpleDebugCall('debug-continue'); }
    debugPause(): Promise<boolean> { return this.simpleDebugCall('debug-pause'); }
    debugStep(kind: 'over' | 'in' | 'out'): Promise<boolean> {
        const id = ++this.nextId;
        return new Promise<boolean>((resolve) => {
            this.pending.set(id, () => resolve(true));
            this.worker.postMessage({ type: 'debug-step', id, kind });
        });
    }
    debugSetBreakpoints(breakpoints: BreakpointRequest[]): Promise<boolean> {
        const id = ++this.nextId;
        return new Promise<boolean>((resolve) => {
            this.pending.set(id, () => resolve(true));
            this.worker.postMessage({
                type: 'debug-set-breakpoints', id,
                linesJson: JSON.stringify(breakpoints),
            });
        });
    }
    debugStackFrames(): Promise<DebugStackFrame[]> {
        const id = ++this.nextId;
        return new Promise<DebugStackFrame[]>((resolve) => {
            this.pending.set(id, (json: string) => {
                try {
                    const parsed = JSON.parse(json);
                    resolve(Array.isArray(parsed) ? parsed : []);
                } catch { resolve([]); }
            });
            this.worker.postMessage({ type: 'debug-stack-frames', id });
        });
    }
    debugScopes(frameId: number): Promise<DebugScopesResult> {
        const id = ++this.nextId;
        return new Promise<DebugScopesResult>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); } catch { resolve({ scopes: [] }); }
            });
            this.worker.postMessage({ type: 'debug-scopes', id, frameId });
        });
    }
    debugExpandVariable(variableId: number): Promise<DebugScopesResult> {
        const id = ++this.nextId;
        return new Promise<DebugScopesResult>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); } catch { resolve({ scopes: [] }); }
            });
            this.worker.postMessage({ type: 'debug-variable-expansion', id, variableId });
        });
    }
    debugEval(frameId: number, expression: string): Promise<DebugEvalResult | null> {
        return this.debugTextCall('debug-eval', { frameId, expression });
    }
    debugRepl(frameId: number, code: string): Promise<DebugEvalResult | null> {
        return this.debugTextCall('debug-repl', { frameId, code });
    }
    debugSetVariable(frameId: number, variableId: number, rhs: string): Promise<DebugEvalResult | null> {
        return this.debugTextCall('debug-set-variable', { frameId, variableId, rhs });
    }
    private simpleDebugCall(type: string): Promise<boolean> {
        const id = ++this.nextId;
        return new Promise<boolean>((resolve) => {
            this.pending.set(id, () => resolve(true));
            this.worker.postMessage({ type, id });
        });
    }
    private debugTextCall(type: string, payload: object): Promise<DebugEvalResult | null> {
        const id = ++this.nextId;
        return new Promise<DebugEvalResult | null>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); } catch { resolve(null); }
            });
            this.worker.postMessage({ type, id, ...payload });
        });
    }

    // Called when the worker requests a synchronous prompt. Delegates to the
    // host UI via onPromptRequest (set by bootstrap), then writes the user's
    // response into the SharedArrayBuffer + notifies the worker to wake up.
    private async handlePromptRequest(msg: string): Promise<void> {
        let answer: string | null = '';
        try {
            const cb = this.onPromptRequest;
            if (cb) answer = (await cb(msg)) ?? '';
            else answer = window.prompt(msg) ?? '';
        } catch {
            answer = '';
        }
        if (!this.promptSync || !this.promptBytes) return;
        const bytes = new TextEncoder().encode(answer ?? '');
        const max = this.promptBytes.length;
        const len = Math.min(bytes.length, max);
        this.promptBytes.set(bytes.subarray(0, len), 0);
        Atomics.store(this.promptSync, 1, len);
        Atomics.store(this.promptSync, 0, 1);
        Atomics.notify(this.promptSync, 0, 1);
    }

    setDiagnosticsHandler(fn: (uri: string, diagnostics: Diagnostic[]) => void) {
        this.onDiagnostics = fn;
    }
}

// DTOs matching the camelCase JSON from FadeBasic.LSP.Core.*
interface DocSymbol {
    name: string;
    detail: string;
    kind: number;
    range: { start: { line: number; character: number }; end: { line: number; character: number } };
    selectionRange: { start: { line: number; character: number }; end: { line: number; character: number } };
    children: DocSymbol[] | null;
}
interface FoldingRange {
    startLine: number;
    endLine: number;
    startCharacter: number | null;
    endCharacter: number | null;
    kind: number;
}
interface TextEdit {
    range: { start: { line: number; character: number }; end: { line: number; character: number } };
    newText: string;
}
interface WorkspaceEdit {
    changes: { [uri: string]: TextEdit[] };
}
interface FormattingOptions {
    tabSize: number;
    insertSpaces: boolean;
    casing: number; // 0=Ignore, 1=ToUpper, 2=ToLower
}
interface TestEntry {
    name: string;
    isAbstract: boolean;
    fromParent: string | null;
    sourceLine: number;
    sourceChar: number;
}
interface FailureFrame {
    functionName: string;
    lineNumber: number;     // 0-based, as emitted by the lexer
    charNumber: number;
    instructionIndex: number;
}
interface TestResult {
    name: string;
    passed: boolean;
    duration: number;
    failureMessage: string | null;
    failureReason: string | null;
    failureSourceText: string | null;
    failureInstructionIndex?: number;
    failureFrames?: FailureFrame[];
}
interface TestRunResult {
    passed: number;
    failed: number;
    duration: number;
    results: TestResult[];
    printed: string;
    error?: string;
}

// ─── Debug session DTOs (match FadeBasic.Launch types over the wire) ────
interface DebugStartResult {
    ok: boolean;
    error?: string;
    statementLines: number[];
}
interface BreakpointRequest {
    // Matches WebRuntime's BreakpointRequestDto (camelCase JSON via
    // JsonNamingPolicy.CamelCase). Use 0-based line numbers — the same
    // coordinate space the lexer's tokens use.
    line: number;
    column: number;
}
interface DebugStackFrame {
    name: string;
    lineNumber: number;
    colNumber: number;
}
interface DebugVariable {
    id: number;
    name: string;
    type: string;
    value: string;
    evalName: string;
    fieldCount: number;
    elementCount: number;
}
interface DebugScope {
    id: number;
    scopeName: string;
    evalName: string;
    variables: DebugVariable[];
}
interface DebugScopesResult {
    scopes: DebugScope[];
}
// Mirrors FadeBasic.Launch.DebugEvalResult — note there's NO `failed`
// boolean. The convention is: `id === -1` means the eval failed and
// `value` carries the error message. A successful eval returns the
// evaluated text in `value` and a non-negative `id`.
interface DebugEvalResult {
    id: number;
    value: string;
    type?: string;
    fieldCount?: number;
    elementCount?: number;
}

// Wire-format events emitted by the worker's debug-tick loop.
// `type` is the DebugMessageType enum name from C# (uppercase snake) or
// a synthetic 'complete' / 'error'.
interface DebugEvent {
    type: string;
    id?: number;
    json?: string;
    message?: string;
}

// Matches FadeBasic.LSP.Core.Handlers.LspSignatureHelp shape.
interface SignatureParam { label: string; documentation: string | null }
interface SignatureInformation {
    label: string;
    documentation: string | null;
    parameters: SignatureParam[];
    activeParameter: number;
}
interface SignatureHelp {
    signatures: SignatureInformation[];
    activeSignature: number;
    activeParameter: number;
}

interface Location {
    uri: string;
    range: { start: { line: number; character: number }; end: { line: number; character: number } };
}

interface HoverInfo {
    contents: string;
    range: { start: { line: number; character: number }; end: { line: number; character: number } };
}

// Matches FadeBasic.LSP.Core.LspCompletionItem shape (camelCase JSON).
interface CompletionItem {
    label: string;
    insertText: string;
    kind: number;
    detail: string;
    documentation: string;
    sortText: string;
    filterText: string;
    insertTextFormat: number;
    triggerParameterHints: boolean;
}

// ─── bootstrap ──────────────────────────────────────────────────────────────
async function bootstrap() {
    statusEl.textContent = 'Initializing services…';
    await initServices({
        ...getModelServiceOverride(),
        ...getEditorServiceOverride(async () => undefined),
        ...getConfigurationServiceOverride(),
        ...getKeybindingsServiceOverride(),
        ...getLanguagesServiceOverride(),
    });

    monaco.languages.register({
        id: 'fade',
        extensions: ['.fbasic', '.fb'],
        aliases: ['Fade', 'FadeBasic'],
    });

    // Pin our formatter as the default for fade — otherwise VSCode shows
    // "There are multiple formatters for 'Fade' files. One of them should be
    // configured as default formatter." whenever Format Document is invoked.
    // (Codingame's services register a no-op formatter we can't easily
    // disable, so the user-config override is the cleanest path.)
    try {
        await updateUserConfiguration(JSON.stringify({
            '[fade]': { 'editor.defaultFormatter': 'fade-basic' },
        }));
    } catch (e) {
        console.warn('[fade] could not pin default formatter:', e);
    }

    // Semantic-token type legend — must match WebRuntime/FadeLsp.cs's
    // TokenTypeLegend order (the encoded tokens use indexes into this list).
    const tokenTypes = [
        'comment', 'keyword', 'function', 'method', 'macro',
        'parameter', 'struct', 'type', 'operator', 'number', 'string',
    ];

    // Fade theme — based on vs-dark with colors for the semantic token types.
    monaco.editor.defineTheme('fade-dark', {
        base: 'vs-dark',
        inherit: true,
        rules: [
            { token: 'comment',   foreground: '6A9955', fontStyle: 'italic' },
            { token: 'keyword',   foreground: 'C586C0' },
            { token: 'function',  foreground: 'DCDCAA' },
            { token: 'method',    foreground: 'DCDCAA' },
            { token: 'macro',     foreground: 'C586C0' },
            { token: 'parameter', foreground: '9CDCFE' },
            { token: 'struct',    foreground: '4EC9B0' },
            { token: 'type',      foreground: '4EC9B0' },
            { token: 'operator',  foreground: 'D4D4D4' },
            { token: 'number',    foreground: 'B5CEA8' },
            { token: 'string',    foreground: 'CE9178' },
        ],
        colors: {},
    });
    monaco.editor.setTheme('fade-dark');

    statusEl.textContent = 'Booting Fade runtime worker…';
    const runner = new FadeRunner({
        onPrint: (line) => {
            outputEl.textContent += line + '\n';
            outputEl.scrollTop = outputEl.scrollHeight;
        },
        onAlert: (msg) => window.alert(msg),
    });
    await runner.ready;
    // Single worker handles both run and LSP duties.
    const lsp = runner;

    // Use the vscode.languages API (the codingame-backed one) rather than
    // monaco.languages — when getLanguagesServiceOverride is loaded, Monaco's
    // standalone language registry is no longer consulted by the editor; it
    // only checks the vscode-shaped registry.
    // Monaco's built-in semantic-token provider plumbing is fragile in our
    // codingame-services setup (the provider registers but Monaco never asks
    // for tokens, and adding a theme service blows up monaco.editor.defineTheme).
    // Bypass it entirely: decode the LSP-encoded tokens ourselves and apply
    // them via Monaco's decoration API with a CSS class per token type. CSS
    // in index.html colors each .fade-token-<type> class.
    const decorationsByUri = new Map<string, string[]>();

    async function applySemanticTokens(model: monaco.editor.ITextModel) {
        const uri = model.uri.toString();
        const tokens = await lsp.getTokens(uri);
        const newDecorations: monaco.editor.IModelDeltaDecoration[] = [];
        let line = 0;
        let ch = 0;
        for (let i = 0; i + 4 < tokens.length; i += 5) {
            const dLine = tokens[i];
            const dChar = tokens[i + 1];
            const len = tokens[i + 2];
            const typeIdx = tokens[i + 3];
            if (dLine > 0) { line += dLine; ch = dChar; }
            else { ch += dChar; }
            const tokenName = tokenTypes[typeIdx] ?? 'unknown';
            newDecorations.push({
                range: new monaco.Range(line + 1, ch + 1, line + 1, ch + 1 + len),
                options: {
                    inlineClassName: 'fade-token-' + tokenName,
                },
            });
        }
        const prev = decorationsByUri.get(uri) ?? [];
        const next = model.deltaDecorations(prev, newDecorations);
        decorationsByUri.set(uri, next);
        console.log('[fade-lsp] applied', newDecorations.length, 'decorations for', uri);
    }

    // Hover provider — surfaces diagnostic messages and basic token info.
    // When a debug session is paused, ALSO evaluates the hovered identifier
    // and prepends its live value (VSCode behavior).
    monaco.languages.registerHoverProvider('fade', {
        provideHover: async (model, position) => {
            const uri = model.uri.toString();
            const word = model.getWordAtPosition(position);

            // Try debug-eval first when paused.
            const contents: { value: string }[] = [];
            let range: monaco.IRange | undefined;
            if (debugSessionActive && debugPaused && word && activeFrameId != null) {
                try {
                    const evalResult = await runner.debugEval(activeFrameId, word.word);
                    if (evalResult && evalResult.id !== -1 && evalResult.value != null) {
                        const type = evalResult.type ? ` _(${evalResult.type})_` : '';
                        contents.push({ value: `**${word.word}** = \`${evalResult.value}\`${type}` });
                        range = new monaco.Range(
                            position.lineNumber, word.startColumn,
                            position.lineNumber, word.endColumn,
                        );
                    }
                } catch { /* fall through to LSP hover */ }
            }

            const hover = await runner.getHover(uri, position.lineNumber - 1, position.column - 1);
            if (hover) {
                contents.push({ value: hover.contents });
                if (!range) {
                    range = new monaco.Range(
                        hover.range.start.line + 1,
                        hover.range.start.character + 1,
                        hover.range.end.line + 1,
                        hover.range.end.character + 1,
                    );
                }
            }
            if (contents.length === 0) return null;
            return { range, contents };
        },
    });
    // Completion provider — driven entirely by the LSP via worker.
    monaco.languages.registerCompletionItemProvider('fade', {
        triggerCharacters: [' ', '.', '(', '=', '+', '*', '-', '/'],
        provideCompletionItems: async (model, position) => {
            const uri = model.uri.toString();
            const items = await runner.getCompletions(uri, position.lineNumber - 1, position.column - 1);
            const word = model.getWordUntilPosition(position);
            const range = new monaco.Range(
                position.lineNumber, word.startColumn,
                position.lineNumber, word.endColumn,
            );
            return {
                suggestions: items.map((it) => ({
                    label: it.label,
                    insertText: it.insertText,
                    kind: lspKindToMonaco(it.kind),
                    detail: it.detail,
                    documentation: it.documentation
                        ? { value: it.documentation, isTrusted: false }
                        : undefined,
                    sortText: it.sortText,
                    filterText: it.filterText,
                    insertTextRules: it.insertTextFormat === 2
                        ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet
                        : monaco.languages.CompletionItemInsertTextRule.None,
                    range,
                    command: it.triggerParameterHints
                        ? { id: 'editor.action.triggerParameterHints', title: '' }
                        : undefined,
                })),
            };
        },
    });

    // Signature help — driven by the worker's SignatureHelpHandler.
    monaco.languages.registerSignatureHelpProvider('fade', {
        signatureHelpTriggerCharacters: ['(', ','],
        signatureHelpRetriggerCharacters: [','],
        provideSignatureHelp: async (model, position) => {
            const uri = model.uri.toString();
            const sig = await runner.getSignatureHelp(uri, position.lineNumber - 1, position.column - 1);
            if (!sig || !sig.signatures?.length) return null;
            return {
                value: {
                    signatures: sig.signatures.map((s) => ({
                        label: s.label,
                        documentation: s.documentation ?? undefined,
                        parameters: s.parameters.map((p) => ({
                            label: p.label,
                            documentation: p.documentation ?? undefined,
                        })),
                    })),
                    activeSignature: sig.activeSignature,
                    activeParameter: sig.activeParameter,
                },
                dispose: () => { /* nothing to dispose */ },
            };
        },
    });

    // References provider — surfaces all uses of the symbol under the cursor.
    monaco.languages.registerReferenceProvider('fade', {
        provideReferences: async (model, position) => {
            const uri = model.uri.toString();
            const refs = await runner.getReferences(uri, position.lineNumber - 1, position.column - 1);
            return refs.map((r) => ({
                uri: monaco.Uri.parse(r.uri),
                range: new monaco.Range(
                    r.range.start.line + 1, r.range.start.character + 1,
                    r.range.end.line + 1, r.range.end.character + 1,
                ),
            }));
        },
    });

    // Goto-definition provider.
    monaco.languages.registerDefinitionProvider('fade', {
        provideDefinition: async (model, position) => {
            const uri = model.uri.toString();
            const def = await runner.getDefinition(uri, position.lineNumber - 1, position.column - 1);
            if (!def) return null;
            return {
                uri: monaco.Uri.parse(def.uri),
                range: new monaco.Range(
                    def.range.start.line + 1, def.range.start.character + 1,
                    def.range.end.line + 1, def.range.end.character + 1,
                ),
            };
        },
    });

    // Document outline / symbol provider — drives the Outline view and
    // breadcrumbs. LspSymbolKind values match the LSP SymbolKind enum.
    monaco.languages.registerDocumentSymbolProvider('fade', {
        displayName: 'Fade',
        provideDocumentSymbols: async (model) => {
            const uri = model.uri.toString();
            const syms = await runner.getDocumentSymbols(uri);
            return syms.map(toMonacoSymbol);
        },
    });

    function toMonacoSymbol(s: DocSymbol): monaco.languages.DocumentSymbol {
        return {
            name: s.name,
            detail: s.detail ?? '',
            kind: lspSymKindToMonaco(s.kind),
            tags: [],
            range: new monaco.Range(
                s.range.start.line + 1, s.range.start.character + 1,
                s.range.end.line + 1, s.range.end.character + 1,
            ),
            selectionRange: new monaco.Range(
                s.selectionRange.start.line + 1, s.selectionRange.start.character + 1,
                s.selectionRange.end.line + 1, s.selectionRange.end.character + 1,
            ),
            children: s.children?.map(toMonacoSymbol) ?? [],
        };
    }

    function lspSymKindToMonaco(k: number): monaco.languages.SymbolKind {
        // LSP SymbolKind → Monaco. Monaco uses a different numeric enum;
        // we map a handful explicitly and fall back to Variable.
        const M = monaco.languages.SymbolKind;
        switch (k) {
            case 12: return M.Function;
            case 13: return M.Variable;
            case 14: return M.Constant;
            case 23: return M.Struct;
            case 6:  return M.Method;
            case 11: return M.Interface;
            case 20: return M.Key;
            case 5:  return M.Class;
            default: return M.Variable;
        }
    }

    // Folding ranges — drives editor margin folding controls.
    monaco.languages.registerFoldingRangeProvider('fade', {
        provideFoldingRanges: async (model) => {
            const uri = model.uri.toString();
            const ranges = await runner.getFoldingRanges(uri);
            return ranges.map((r) => ({
                start: r.startLine + 1,
                end: r.endLine + 1,
                kind: r.kind === 1
                    ? monaco.languages.FoldingRangeKind.Comment
                    : r.kind === 2
                        ? monaco.languages.FoldingRangeKind.Imports
                        : monaco.languages.FoldingRangeKind.Region,
            }));
        },
    });

    // Formatting helpers: convert Monaco's FormattingOptions to our DTO.
    const buildFormattingOptions = (opts: monaco.languages.FormattingOptions): FormattingOptions => ({
        tabSize: opts.tabSize,
        insertSpaces: opts.insertSpaces,
        casing: 0, // Ignore — could be wired to a user setting later
    });
    const toMonacoEdit = (e: TextEdit): monaco.languages.TextEdit => ({
        range: new monaco.Range(
            e.range.start.line + 1, e.range.start.character + 1,
            e.range.end.line + 1, e.range.end.character + 1,
        ),
        text: e.newText,
    });

    // extensionId / displayName let VSCode treat this as a "named" formatter
    // — matched by the user config setting `[fade].editor.defaultFormatter`.
    const docFormatter: monaco.languages.DocumentFormattingEditProvider = {
        displayName: 'Fade Basic',
        provideDocumentFormattingEdits: async (model, opts) => {
            const edits = await runner.format(model.uri.toString(), buildFormattingOptions(opts));
            return edits.map(toMonacoEdit);
        },
    };
    (docFormatter as any).extensionId = 'fade-basic';
    monaco.languages.registerDocumentFormattingEditProvider('fade', docFormatter);

    const rangeFormatter: monaco.languages.DocumentRangeFormattingEditProvider = {
        displayName: 'Fade Basic',
        provideDocumentRangeFormattingEdits: async (model, range, opts) => {
            const edits = await runner.formatRange(model.uri.toString(), buildFormattingOptions(opts), {
                startLine: range.startLineNumber - 1,
                startCh: range.startColumn - 1,
                endLine: range.endLineNumber - 1,
                endCh: range.endColumn - 1,
            });
            return edits.map(toMonacoEdit);
        },
    };
    (rangeFormatter as any).extensionId = 'fade-basic';
    monaco.languages.registerDocumentRangeFormattingEditProvider('fade', rangeFormatter);

    monaco.languages.registerOnTypeFormattingEditProvider('fade', {
        autoFormatTriggerCharacters: ['(', ')', ',', '\n', ' '],
        provideOnTypeFormattingEdits: async (model, position, _ch, opts) => {
            const edits = await runner.formatOnType(
                model.uri.toString(),
                buildFormattingOptions(opts),
                position.lineNumber - 1, position.column - 1,
            );
            return edits.map(toMonacoEdit);
        },
    });

    // Rename provider — F2 in the editor.
    monaco.languages.registerRenameProvider('fade', {
        provideRenameEdits: async (model, position, newName) => {
            const uri = model.uri.toString();
            const result = await runner.rename(uri, position.lineNumber - 1, position.column - 1, newName);
            if (!result?.changes) {
                return { edits: [] };
            }
            const edits: monaco.languages.IWorkspaceTextEdit[] = [];
            for (const [resourceUri, textEdits] of Object.entries(result.changes)) {
                for (const e of textEdits) {
                    edits.push({
                        resource: monaco.Uri.parse(resourceUri),
                        textEdit: {
                            range: new monaco.Range(
                                e.range.start.line + 1, e.range.start.character + 1,
                                e.range.end.line + 1, e.range.end.character + 1,
                            ),
                            text: e.newText,
                        },
                        versionId: undefined,
                    });
                }
            }
            return { edits };
        },
    });

    function lspKindToMonaco(k: number): monaco.languages.CompletionItemKind {
        // Matches FadeBasic.LSP.Core.LspCompletionKind ordering
        switch (k) {
            case 1: return monaco.languages.CompletionItemKind.Variable;
            case 2: return monaco.languages.CompletionItemKind.Function;
            case 3: return monaco.languages.CompletionItemKind.Interface;
            case 4: return monaco.languages.CompletionItemKind.Keyword;
            case 5: return monaco.languages.CompletionItemKind.Field;
            case 6: return monaco.languages.CompletionItemKind.Class;
            case 7: return monaco.languages.CompletionItemKind.Constant;
            case 8: return monaco.languages.CompletionItemKind.Reference;
            case 9: return monaco.languages.CompletionItemKind.Folder;
            case 10: return monaco.languages.CompletionItemKind.Method;
            case 11: return monaco.languages.CompletionItemKind.Snippet;
            default: return monaco.languages.CompletionItemKind.Text;
        }
    }

    // Per-URI diagnostics cache for the Problems panel.
    const diagnosticsByUri = new Map<string, Diagnostic[]>();

    lsp.setDiagnosticsHandler((uri, diagnostics) => {
        // Find ALL models with this URI — codingame may create duplicate
        // model objects (different instances, same URI). Apply markers to
        // all of them so the live editor's model is always covered.
        const allModels = monaco.editor.getModels().filter((m) => m.uri.toString() === uri);
        if (!allModels.length) return;
        const activeModel = editor?.getModel();
        if (activeModel && activeModel.uri.toString() === uri) {
            void applySemanticTokens(activeModel);
        }
        const markers: monaco.editor.IMarkerData[] = diagnostics.map((d) => ({
            severity: d.severity === 1 ? monaco.MarkerSeverity.Error
                : d.severity === 2 ? monaco.MarkerSeverity.Warning
                : d.severity === 3 ? monaco.MarkerSeverity.Info
                : monaco.MarkerSeverity.Hint,
            startLineNumber: d.range.start.line + 1,
            startColumn: d.range.start.character + 1,
            endLineNumber: d.range.end.line + 1,
            endColumn: d.range.end.character + 1,
            message: d.message,
            code: d.code,
            source: d.source ?? 'fade',
        }));
        for (const m of allModels) {
            monaco.editor.setModelMarkers(m, 'fade', markers);
        }

        diagnosticsByUri.set(uri, diagnostics);
        renderProblems();
    });

    // ─── Bottom panel (vscode-tabs handles tab switching internally) ───────
    const bottomTabs = document.getElementById('bottom-tabs') as any;
    const problemsList = document.getElementById('problems-list')!;
    const problemsEmpty = document.getElementById('problems-empty')!;
    // Problems / Tests counts surface in the dockview panel titles
    // (e.g. "Problems (3)"). dockview-core doesn't expose badges natively
    // but setTitle() is just as visible.
    function setPanelTitle(panelId: string, title: string) {
        const p = dockApi.getPanel(panelId);
        if (p) p.api.setTitle(title);
    }

    // Auto-switch to Problems tab on first error after a clean run.
    let lastTotal = 0;
    function focusProblemsTab() {
        if (bottomTabs && 'selectedIndex' in bottomTabs) bottomTabs.selectedIndex = 1;
    }

    function renderProblems() {
        problemsList.innerHTML = '';
        let total = 0;
        for (const [uri, diags] of diagnosticsByUri) {
            for (const d of diags) {
                total++;
                const li = document.createElement('li');
                li.className = 'problem-item';

                const sevName = d.severity === 1 ? 'error' : d.severity === 2 ? 'warning' : 'info';
                // <vscode-icon> renders a codicon glyph. Names match codicon font keys.
                const icon = document.createElement('vscode-icon');
                icon.setAttribute('name', sevName);
                icon.className = sevName;

                const msg = document.createElement('span');
                msg.className = 'problem-message';
                msg.textContent = d.message;
                if (d.code) {
                    const code = document.createElement('span');
                    code.className = 'code';
                    code.textContent = d.code;
                    msg.append(code);
                }

                const loc = document.createElement('span');
                loc.className = 'problem-location';
                loc.textContent = `${uriToName(uri)}:${d.range.start.line + 1}:${d.range.start.character + 1}`;

                li.append(icon, msg, loc);
                li.onclick = () => {
                    const name = uriToName(uri);
                    const tab = tabs.get(name);
                    if (tab && editor) {
                        editor.setModel(tab.model);
                        activeName = name;
                        renderTabs();
                        renderFileListSelection();
                        const lineNumber = d.range.start.line + 1;
                        const column = d.range.start.character + 1;
                        editor.revealPositionInCenter({ lineNumber, column });
                        editor.setPosition({ lineNumber, column });
                        editor.focus();
                    }
                };
                problemsList.append(li);
            }
        }
        setPanelTitle('problems', total > 0 ? `Problems (${total})` : 'Problems');
        problemsEmpty.style.display = total === 0 ? '' : 'none';

        // First-error-after-clean: pop the Problems tab so the user notices.
        if (total > 0 && lastTotal === 0) focusProblemsTab();
        lastTotal = total;
    }




    // The polling loop below handles LSP pushes for the editor's current model.
    // Kept for compatibility with openFile (which calls this).
    (window as any).__fadeHookModel = (_model: monaco.editor.ITextModel) => { /* no-op */ };

    // ─── Tests panel ────────────────────────────────────────────────────────
    const testsListEl = document.getElementById('tests-list')!;
    const testsEmptyEl = document.getElementById('tests-empty')!;
    // tests count surfaces in the dockview tab title (see setPanelTitle).
    const testsRunAllBtn = document.getElementById('tests-run-all') as HTMLElement & { disabled: boolean };
    const testsRefreshBtn = document.getElementById('tests-refresh') as HTMLElement & { disabled: boolean };
    const testsStatusEl = document.getElementById('tests-status')!;
    const testsSearchEl = document.getElementById('tests-search') as HTMLInputElement;
    const testsSearchClearBtn = document.getElementById('tests-search-clear') as HTMLButtonElement;
    const testsLogEl = document.getElementById('tests-log')!;
    const testsLogRegion = document.getElementById('tests-log-region')!;
    const testsLogToggle = document.getElementById('tests-log-toggle')!;
    const testsLogChev = document.getElementById('tests-log-chev')!;
    const testsLogClearBtn = document.getElementById('tests-log-clear')!;

    type TestUiEntry = TestEntry & {
        status: 'idle' | 'running' | 'pass' | 'fail';
        duration?: number;
        failure?: string | null;
        failureFrames?: FailureFrame[];
    };
    let testEntries: TestUiEntry[] = [];
    let testsSearchQuery = '';

    // Jump the editor to a (1-based line, 1-based col). Lexer-emitted line
    // numbers in failureFrames are 0-based — callers translate at the seam.
    function jumpEditorTo(lineOneBased: number, columnOneBased: number = 1) {
        if (!editor) return;
        editor.revealLineInCenter(lineOneBased, monaco.editor.ScrollType.Smooth);
        editor.setPosition({ lineNumber: lineOneBased, column: columnOneBased });
        editor.focus();
    }

    // Inline log helpers. We keep a small log under the test list — separate
    // from the global Output panel — so test feedback is always one glance
    // away. Lines can carry a click-to-jump payload (failure frames).
    function appendTestLog(text: string, cls?: 'pass' | 'fail' | 'dim', frame?: FailureFrame) {
        if (!text) return;
        const line = document.createElement('div');
        line.className = 'tests-log-line' + (cls ? ' ' + cls : '');
        if (frame) {
            const span = document.createElement('span');
            span.className = 'test-failure-frame';
            span.textContent = text;
            // failureFrames are 0-based; +1 to bring into editor coordinates.
            const ln = (frame.lineNumber | 0) + 1;
            const col = ((frame.charNumber | 0) + 1) || 1;
            span.onclick = () => jumpEditorTo(ln, col);
            line.append(span);
        } else {
            line.textContent = text;
        }
        testsLogEl.append(line);
        testsLogEl.scrollTop = testsLogEl.scrollHeight;
    }
    function clearTestLog() { testsLogEl.innerHTML = ''; }
    testsLogClearBtn.addEventListener('click', clearTestLog);
    testsLogToggle.addEventListener('click', (e) => {
        if ((e.target as HTMLElement) === testsLogClearBtn) return;
        const collapsed = testsLogRegion.classList.toggle('collapsed');
        testsLogChev.className = collapsed ? 'codicon codicon-chevron-right' : 'codicon codicon-chevron-down';
    });

    function getActiveSource(): string {
        const activeTab = activeName ? tabs.get(activeName) : null;
        return activeTab?.model.getValue() ?? '';
    }

    async function refreshTests() {
        const source = getActiveSource();
        if (!source) {
            testEntries = [];
            renderTests();
            return;
        }
        const list = await runner.listTests(source);
        testEntries = list.map((t) => ({ ...t, status: 'idle' as const }));
        renderTests();
    }

    function renderTests() {
        testsListEl.innerHTML = '';
        const runnable = testEntries.filter((t) => !t.isAbstract).length;
        setPanelTitle('tests', runnable > 0 ? `Tests (${runnable})` : 'Tests');

        const q = testsSearchQuery.trim().toLowerCase();
        const visible = q
            ? testEntries.filter((t) => t.name.toLowerCase().includes(q))
            : testEntries;

        if (testEntries.length === 0) {
            testsEmptyEl.style.display = '';
            testsEmptyEl.textContent = 'No tests in this file.';
        } else if (visible.length === 0) {
            testsEmptyEl.style.display = '';
            testsEmptyEl.textContent = `No tests match “${testsSearchQuery}”.`;
        } else {
            testsEmptyEl.style.display = 'none';
        }

        for (const t of visible) {
            const li = document.createElement('li');
            li.className = 'test-item';

            const icon = document.createElement('vscode-icon');
            icon.className = 'test-status-icon ' + t.status;
            icon.setAttribute('name', iconNameForStatus(t.status));
            li.append(icon);

            const name = document.createElement('span');
            name.className = 'test-name' + (t.isAbstract ? ' abstract' : '');
            name.textContent = t.name + (t.isAbstract ? ' (abstract)' : '');
            // Click the name to jump to the test definition.
            name.style.cursor = 'pointer';
            name.onclick = () => jumpEditorTo((t.sourceLine | 0) + 1, ((t.sourceChar | 0) + 1) || 1);
            li.append(name);

            const dur = document.createElement('span');
            dur.className = 'test-duration';
            dur.textContent = t.duration != null ? `${Math.round(t.duration)} ms` : '';
            li.append(dur);

            const runBtn = document.createElement('vscode-button') as HTMLElement & { disabled: boolean };
            runBtn.setAttribute('icon', 'play');
            runBtn.setAttribute('secondary', 'true');
            runBtn.textContent = 'Run';
            if (t.isAbstract) runBtn.disabled = true;
            runBtn.onclick = () => runSingleTest(t.name);
            li.append(runBtn);

            // Debug this test — starts a session that begins at the test's
            // entry point (bridge constructs a fresh VM in test-execution
            // mode positioned there). Same controls as the regular Debug
            // button apply once running.
            const dbgBtn = document.createElement('vscode-button') as HTMLElement & { disabled: boolean };
            dbgBtn.setAttribute('icon', 'debug-alt');
            dbgBtn.setAttribute('secondary', 'true');
            dbgBtn.textContent = 'Debug';
            if (t.isAbstract) dbgBtn.disabled = true;
            dbgBtn.onclick = () => debugSingleTest(t.name);
            li.append(dbgBtn);

            if (t.failure) {
                const fail = document.createElement('div');
                fail.className = 'test-failure';
                fail.textContent = t.failure;
                li.append(fail);

                // Prefer a frame; fall back to the test's own sourceLine.
                const frame = t.failureFrames && t.failureFrames.length > 0 ? t.failureFrames[0] : null;
                if (frame) {
                    const loc = document.createElement('span');
                    loc.className = 'test-failure-loc test-failure-link';
                    const ln = (frame.lineNumber | 0) + 1;
                    const fn = frame.functionName ? frame.functionName + '() ' : '';
                    loc.textContent = `↳ ${fn}line ${ln}`;
                    const col = ((frame.charNumber | 0) + 1) || 1;
                    loc.onclick = (e) => { e.stopPropagation(); jumpEditorTo(ln, col); };
                    fail.append(loc);
                }
            }
            testsListEl.append(li);
        }
    }

    function iconNameForStatus(s: TestUiEntry['status']): string {
        switch (s) {
            case 'pass':    return 'pass';
            case 'fail':    return 'error';
            case 'running': return 'loading';
            default:        return 'circle-outline';
        }
    }

    function appendOutput(text: string) {
        if (!text) return;
        // Trailing newline strip — drainPrintBuffer adds them.
        const norm = text.endsWith('\n') ? text : text + '\n';
        outputEl.textContent += norm;
        outputEl.scrollTop = outputEl.scrollHeight;
    }

    function setTestsBusy(busy: boolean) {
        testsRunAllBtn.disabled = busy;
        testsRefreshBtn.disabled = busy;
    }

    async function runSingleTest(name: string) {
        const source = getActiveSource();
        if (!source) return;
        const idx = testEntries.findIndex((t) => t.name === name);
        if (idx < 0) return;
        testEntries[idx].status = 'running';
        testEntries[idx].failure = null;
        testEntries[idx].failureFrames = undefined;
        testEntries[idx].duration = undefined;
        renderTests();
        setTestsBusy(true);
        testsStatusEl.textContent = `Running ${name}…`;
        appendTestLog(`▶ ${name}`, 'dim');
        try {
            const r = await runner.runTests(source, name);
            applyResult(r);
        } catch (e: any) {
            testEntries[idx].status = 'fail';
            testEntries[idx].failure = e?.message ?? String(e);
            appendTestLog(`  ✗ ${name}: ${e?.message ?? e}`, 'fail');
            renderTests();
        } finally {
            setTestsBusy(false);
        }
    }

    async function runAllTests() {
        const source = getActiveSource();
        if (!source) return;
        for (const t of testEntries) {
            if (!t.isAbstract) {
                t.status = 'running';
                t.failure = null;
                t.failureFrames = undefined;
                t.duration = undefined;
            }
        }
        renderTests();
        setTestsBusy(true);
        testsStatusEl.textContent = 'Running…';
        appendTestLog(`▶ Run all`, 'dim');
        try {
            const r = await runner.runTests(source);
            applyResult(r);
        } finally {
            setTestsBusy(false);
        }
    }

    function applyResult(r: TestRunResult) {
        if (r.error) {
            appendOutput(r.error);
            appendTestLog(r.error, 'fail');
            for (const t of testEntries) if (t.status === 'running') t.status = 'idle';
            testsStatusEl.textContent = r.error;
            renderTests();
            return;
        }
        // Update entries by name.
        for (const res of r.results) {
            const e = testEntries.find((t) => t.name === res.name);
            if (!e) continue;
            e.status = res.passed ? 'pass' : 'fail';
            e.duration = res.duration;
            e.failure = res.passed ? null : (res.failureMessage || res.failureReason || 'Failed');
            e.failureFrames = res.passed ? undefined : (res.failureFrames || []);
        }
        // Headline + per-test rollup go both to Output and the inline log.
        const headline = `Tests: ${r.passed} passed, ${r.failed} failed (${Math.round(r.duration)} ms)`;
        appendOutput(`\n--- ${headline} ---`);
        if (r.printed) appendOutput(r.printed.trimEnd());
        for (const res of r.results) {
            if (!res.passed) {
                appendOutput(`  ✗ ${res.name}: ${res.failureMessage || res.failureReason || 'failed'}`);
            }
        }

        // Inline log: include printed stdout, then one block per result. Failure
        // frames render as click-to-jump links.
        if (r.printed) appendTestLog(r.printed.trimEnd(), 'dim');
        for (const res of r.results) {
            if (res.passed) {
                appendTestLog(`  ✓ ${res.name} (${Math.round(res.duration)} ms)`, 'pass');
            } else {
                const msg = res.failureMessage || res.failureReason || 'failed';
                appendTestLog(`  ✗ ${res.name}: ${msg}`, 'fail');
                if (res.failureFrames && res.failureFrames.length > 0) {
                    for (const f of res.failureFrames) {
                        const ln = (f.lineNumber | 0) + 1;
                        const fn = f.functionName ? f.functionName + '() ' : '';
                        appendTestLog(`      at ${fn}line ${ln}`, undefined, f);
                    }
                }
            }
        }
        appendTestLog(headline, r.failed > 0 ? 'fail' : 'pass');

        testsStatusEl.textContent = headline;
        renderTests();
    }

    // Search wiring — re-render the list as the user types. `search` event
    // also fires on the input's built-in clear button.
    testsSearchEl.addEventListener('input', () => {
        testsSearchQuery = testsSearchEl.value;
        testsSearchClearBtn.hidden = !testsSearchQuery;
        renderTests();
    });
    testsSearchEl.addEventListener('search', () => {
        testsSearchQuery = testsSearchEl.value;
        testsSearchClearBtn.hidden = !testsSearchQuery;
        renderTests();
    });
    testsSearchClearBtn.addEventListener('click', () => {
        testsSearchEl.value = '';
        testsSearchQuery = '';
        testsSearchClearBtn.hidden = true;
        renderTests();
        testsSearchEl.focus();
    });

    testsRunAllBtn.addEventListener('click', runAllTests);
    testsRefreshBtn.addEventListener('click', refreshTests);
    // Re-list on every doc push so the badge updates as the user types tests.
    const refreshDebounce = (() => {
        let timer: number | undefined;
        return () => {
            if (timer != null) clearTimeout(timer);
            timer = window.setTimeout(refreshTests, 400);
        };
    })();

    // Surface synchronous prompt$ via window.prompt for now. A nicer modal
    // can replace this later.
    runner.onPromptRequest = (msg) => window.prompt(msg, '');

    statusEl.textContent = 'Loading workspace…';
    const workspace = new OpfsWorkspace();
    await workspace.init();

    // ─── Dockview setup ─────────────────────────────────────────────────
    // `createComponent` returns the matching `.panel-cell` div from the
    // hidden pool. Dockview reparents it into the panel's content shell,
    // which exposes our long-lived DOM elements (#editor, #output, etc.)
    // back into the live DOM so existing getElementById refs still work.
    // Bumped to v3 when separate variables/call-stack panels were merged
    // back into a single `debug` panel with collapsible sections (matches
    // VSCode's Run-and-Debug sidebar). healLayout still recovers older
    // sessions but the version bump avoids a confusing intermediate state.
    const LAYOUT_STORAGE_KEY = 'fade.dockview.layout.v3';

    function setupDockview(): DockviewApi {
        const dockRoot = document.getElementById('dock-root')!;
        const panelCells = document.getElementById('panel-cells')!;

        const dock = createDockview(dockRoot, {
            // Built-in VSCode-like dark theme — matches the rest of the
            // playground's styling (vs-dark Monaco theme, vscode-elements).
            theme: { name: 'vs', className: 'dockview-theme-vs' },
            disableFloatingGroups: false,
            createComponent: ({ name }) => {
                const cell = panelCells.querySelector<HTMLElement>(
                    `.panel-cell[data-panel="${name}"]`,
                );
                if (!cell) {
                    const fallback = document.createElement('div');
                    fallback.textContent = `Unknown panel: ${name}`;
                    return { element: fallback, init() {}, dispose() {} };
                }
                // Snap the cell into the panel shell; on dispose move it
                // back to the offscreen pool so opening the panel again
                // works without losing state.
                return {
                    element: cell,
                    init() {},
                    dispose() { panelCells.appendChild(cell); },
                };
            },
        });

        // Try to restore the saved layout. If the JSON references panel
        // components we no longer ship (old `debug` panel, future renames),
        // dockview happily creates them with our fallback element — which
        // shows "Unknown panel: X" and traps the user with no way to get
        // the real panels back. Self-heal by:
        //   1. Removing any restored panel whose component name isn't in
        //      our known set.
        //   2. Re-adding any default panel that's missing.
        let restored = false;
        try {
            const raw = localStorage.getItem(LAYOUT_STORAGE_KEY);
            if (raw) {
                dock.fromJSON(JSON.parse(raw) as SerializedDockview);
                restored = true;
            }
        } catch (e) {
            console.warn('[fade] saved layout failed to restore — using default', e);
        }
        if (!restored) {
            buildDefaultLayout(dock);
        } else {
            healLayout(dock);
        }

        // Persist on every layout change (drag, resize, tab move…).
        let saveTimer: number | undefined;
        dock.onDidLayoutChange(() => {
            if (saveTimer != null) clearTimeout(saveTimer);
            saveTimer = window.setTimeout(() => {
                try {
                    localStorage.setItem(LAYOUT_STORAGE_KEY, JSON.stringify(dock.toJSON()));
                } catch (e) {
                    console.warn('[fade] layout save failed', e);
                }
            }, 250);
        });

        return dock;
    }

    // Single source of truth for the panels we ship. healLayout uses this
    // to drop restored panels with unknown component names + re-add any
    // expected default that's missing.
    const KNOWN_COMPONENTS = new Set([
        'workspace', 'editor', 'debug',
        'output', 'problems', 'tests', 'debug-console',
    ]);

    function healLayout(dock: DockviewApi) {
        try {
            // Pass 1: remove any restored panel whose component is unknown
            // (e.g. an old `debug` panel from a previous version).
            for (const panel of dock.panels.slice()) {
                const name = (panel as any).view?.contentComponent
                    ?? (panel as any).contentComponent
                    ?? (panel as any).component;
                if (typeof name === 'string' && !KNOWN_COMPONENTS.has(name)) {
                    console.warn('[fade] dropping stale panel', panel.id, 'component=', name);
                    dock.removePanel(panel);
                }
            }
            // If MOST defaults are missing the saved layout is too broken
            // to patch up gracefully (e.g. healing into a single-leaf grid
            // produces a weird stack). Nuke and rebuild from defaults
            // instead — saving the user from a confusing partial layout.
            const present = new Set(dock.panels.map((p) => p.id));
            const missingCriticalCount = ['editor', 'workspace', 'output']
                .filter((id) => !present.has(id)).length;
            if (missingCriticalCount >= 2) {
                console.warn('[fade] restored layout missing core panels — rebuilding default');
                try { dock.clear(); } catch { /* not all versions have clear */ }
                buildDefaultLayout(dock);
                return;
            }
            // Pass 2: re-add any default panel that's missing — the user can
            // close tabs but reloading should restore the full default set
            // so panels are always discoverable.
            const addMissing = (id: string, opts: { position?: any; renderer?: any; title?: string }) => {
                if (present.has(id)) return;
                console.warn('[fade] re-adding missing panel', id);
                dock.addPanel({
                    id,
                    component: id,
                    title: opts.title ?? capitalize(id),
                    position: opts.position,
                    renderer: opts.renderer,
                });
            };
            // Use the editor (if present) as the reference for re-adds so the
            // new panels show up in sensible places.
            const ref = dock.getPanel('editor');
            const RENDER_ALWAYS = 'always' as const;
            addMissing('editor', { renderer: RENDER_ALWAYS, title: 'Editor' });
            addMissing('workspace', {
                position: ref ? { referencePanel: ref.id, direction: 'left' } : undefined,
                renderer: RENDER_ALWAYS, title: 'Workspace',
            });
            addMissing('debug', {
                position: { referencePanel: dock.getPanel('workspace')?.id ?? 'editor', direction: 'below' },
                renderer: RENDER_ALWAYS, title: 'Debug',
            });
            addMissing('output', {
                position: { referencePanel: ref?.id ?? 'editor', direction: 'below' },
                renderer: RENDER_ALWAYS, title: 'Output',
            });
            const bottomRef = dock.getPanel('output')?.id ?? 'editor';
            addMissing('problems', {
                position: { referencePanel: bottomRef, direction: 'within' },
                renderer: RENDER_ALWAYS, title: 'Problems',
            });
            addMissing('tests', {
                position: { referencePanel: bottomRef, direction: 'within' },
                renderer: RENDER_ALWAYS, title: 'Tests',
            });
            addMissing('debug-console', {
                position: { referencePanel: bottomRef, direction: 'within' },
                renderer: RENDER_ALWAYS, title: 'Debug Console',
            });
        } catch (e) {
            console.warn('[fade] healLayout failed — falling back to default', e);
            try { dock.clear(); } catch { /* dockview clear may not exist */ }
            buildDefaultLayout(dock);
        }
    }

    function capitalize(s: string) { return s ? s[0].toUpperCase() + s.slice(1) : s; }

    function buildDefaultLayout(dock: DockviewApi) {
        // `renderer: 'always'` keeps the panel's element in the DOM even
        // when its tab is inactive. Without this, dockview detaches the
        // content for non-active tabs and document.getElementById() lookups
        // (e.g. for `debug-continue`) return null while bootstrap is wiring
        // up event handlers for buttons inside those panels.
        const RENDER_ALWAYS = 'always' as const;

        // Build the layout from the outside in:
        //   1. Editor as root (takes the whole viewport).
        //   2. Workspace LEFT of editor (creates the full-height left column).
        //   3. Variables / Call Stack BELOW workspace (vertical split inside
        //      the left column — they share that column with workspace).
        //   4. Bottom tab group BELOW editor (full-height right column gets
        //      split horizontally; the tab group spans the editor's column).
        //
        // The order matters: if workspace is added first and editor is added
        // `right` of it, dockview only splits workspace's local row — the
        // editor ends up the same height as the workspace section, with the
        // bottom panel cropping into the editor area.
        const editorPanel = dock.addPanel({
            id: 'editor',
            component: 'editor',
            title: 'Editor',
            renderer: RENDER_ALWAYS,
        });
        const workspacePanel = dock.addPanel({
            id: 'workspace',
            component: 'workspace',
            title: 'Workspace',
            position: { referencePanel: editorPanel.id, direction: 'left' },
            initialWidth: 260,
            renderer: RENDER_ALWAYS,
        });
        // Single consolidated Debug panel (Variables / Watch / Call Stack /
        // Breakpoints sections inside).
        dock.addPanel({
            id: 'debug',
            component: 'debug',
            title: 'Debug',
            position: { referencePanel: workspacePanel.id, direction: 'below' },
            renderer: RENDER_ALWAYS,
        });
        // Bottom tab group: Output / Problems / Tests / Debug Console.
        const outputPanel = dock.addPanel({
            id: 'output',
            component: 'output',
            title: 'Output',
            position: { referencePanel: editorPanel.id, direction: 'below' },
            initialHeight: 240,
            renderer: RENDER_ALWAYS,
        });
        dock.addPanel({
            id: 'problems',
            component: 'problems',
            title: 'Problems',
            position: { referencePanel: outputPanel.id, direction: 'within' },
            renderer: RENDER_ALWAYS,
        });
        dock.addPanel({
            id: 'tests',
            component: 'tests',
            title: 'Tests',
            position: { referencePanel: outputPanel.id, direction: 'within' },
            renderer: RENDER_ALWAYS,
        });
        dock.addPanel({
            id: 'debug-console',
            component: 'debug-console',
            title: 'Debug Console',
            position: { referencePanel: outputPanel.id, direction: 'within' },
            renderer: RENDER_ALWAYS,
        });
        const out = dock.getPanel('output');
        if (out) out.api.setActive();

        // `initialWidth/Height` on AddPanelOptions is honored as a hint but
        // dockview's grid balances new groups proportionally against
        // siblings — for a 3-region layout we end up with 50/50 columns by
        // default. Force the workspace + bottom-panel sizes via setSize.
        // rAF fires *before* dockview's first ResizeObserver measure-pass,
        // so its setSize gets clobbered; setTimeout pushes us past that.
        setTimeout(() => {
            try {
                const ws = dock.getPanel('workspace');
                if (ws) ws.api.setSize({ width: 220 });
                const o = dock.getPanel('output');
                if (o) o.api.setSize({ height: 240 });
            } catch (e) {
                console.warn('[fade] dockview setSize failed', e);
            }
        }, 50);
    }

    // ─── Dockview layout ────────────────────────────────────────────────
    // Build the dockable layout BEFORE monaco mounts so #editor is visible
    // in the DOM by the time create() runs (Monaco's automaticLayout
    // measures the container at construction).
    statusEl.textContent = 'Mounting layout…';
    const dockApi = setupDockview();
    // Expose for tests + future "Reset layout" command.
    (window as any).__fadeDockview = dockApi;

    // Reset Layout: nuke the persisted layout and reload. Useful when
    // something puts the dock into an awkward state we can't recover via
    // healLayout alone.
    resetLayoutBtn.addEventListener('click', () => {
        if (!confirm('Reset all panel layout to defaults?')) return;
        try { localStorage.removeItem(LAYOUT_STORAGE_KEY); } catch { /* ignore */ }
        location.reload();
    });

    statusEl.textContent = 'Mounting editor…';
    editor = monaco.editor.create(editorContainer, {
        value: '',
        language: 'fade',
        theme: 'fade-dark',
        automaticLayout: true,
        minimap: { enabled: false },
        fontSize: 14,
        hover: { enabled: 'on', delay: 200, sticky: true },
        'semanticHighlighting.enabled': true,
    } as monaco.editor.IStandaloneEditorConstructionOptions);

    // Watch ALL fade models in the registry and push when any change. Picks
    // up changes regardless of which model object the live editor uses (we
    // can have duplicates with the same URI under codingame's services).
    const lastPushedByUri = new Map<string, string>();
    setInterval(() => {
        let anyChanged = false;
        for (const m of monaco.editor.getModels()) {
            if (m.getLanguageId() !== 'fade') continue;
            const uri = m.uri.toString();
            const value = m.getValue();
            if (lastPushedByUri.get(uri) === value) continue;
            lastPushedByUri.set(uri, value);
            lsp.setDocument(uri, value);
            anyChanged = true;
        }
        // Re-discover tests in the background whenever the active file moves.
        if (anyChanged) refreshDebounce();
    }, 250);

    // Initial test scan once a file is open.
    setTimeout(refreshTests, 800);

    // Open the first file in the workspace.
    const names = await workspace.list();
    if (names.length > 0) {
        await openFile(workspace, names[0]);
    } else {
        editorContainer.style.display = 'none';
        editorPlaceholder.style.display = 'flex';
        await renderFileList(workspace);
    }

    statusEl.textContent = 'Ready.';
    runBtn.disabled = false;
    debugBtn.disabled = false;

    const runOnce = async () => {
        const activeTab = activeName ? tabs.get(activeName) : null;
        if (!activeTab) {
            outputEl.textContent = 'No file open.';
            return;
        }
        runBtn.disabled = true;
        outputEl.textContent = '';
        try {
            const result = await runner.run(activeTab.model.getValue());
            outputEl.textContent = result;
        } catch (e: any) {
            outputEl.textContent = 'Error: ' + (e?.message ?? e);
        } finally {
            runBtn.disabled = false;
        }
    };

    runBtn.addEventListener('click', runOnce);
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyR, runOnce);

    // ─── Debug session wiring ───────────────────────────────────────────
    const debugControlBar = document.getElementById('debug-control-bar')!;
    const debugDragHandle = document.getElementById('debug-drag-handle')!;

    // Make the floating debug toolbar draggable, like VSCode's. Grabbing
    // the handle (or any non-button area) and dragging anywhere
    // repositions it. Position is remembered for the session via inline
    // styles; we don't persist to localStorage — VSCode also resets
    // toolbar position on reload.
    {
        let dragging = false;
        let dragOffsetX = 0;
        let dragOffsetY = 0;
        const onMove = (e: MouseEvent) => {
            if (!dragging) return;
            const x = e.clientX - dragOffsetX;
            const y = e.clientY - dragOffsetY;
            // Clamp into viewport so the toolbar can't be dragged offscreen.
            const w = debugControlBar.offsetWidth;
            const h = debugControlBar.offsetHeight;
            const vw = window.innerWidth;
            const vh = window.innerHeight;
            const cx = Math.max(4, Math.min(vw - w - 4, x));
            const cy = Math.max(4, Math.min(vh - h - 4, y));
            debugControlBar.style.left = cx + 'px';
            debugControlBar.style.top = cy + 'px';
            // Once the user drags, drop the centering transform so left/top
            // is the source of truth.
            debugControlBar.style.transform = 'none';
        };
        const onUp = () => {
            if (!dragging) return;
            dragging = false;
            debugControlBar.classList.remove('dragging');
            window.removeEventListener('mousemove', onMove);
            window.removeEventListener('mouseup', onUp);
        };
        debugDragHandle.addEventListener('mousedown', (e) => {
            const rect = debugControlBar.getBoundingClientRect();
            dragging = true;
            dragOffsetX = e.clientX - rect.left;
            dragOffsetY = e.clientY - rect.top;
            debugControlBar.classList.add('dragging');
            // Pin current position so the next mousemove can offset from it.
            // The CSS default uses `right` for snapped position — clear it
            // and switch to `left` so drag updates can target a single
            // coordinate axis.
            debugControlBar.style.right = 'auto';
            debugControlBar.style.left = rect.left + 'px';
            debugControlBar.style.top = rect.top + 'px';
            debugControlBar.style.transform = 'none';
            window.addEventListener('mousemove', onMove);
            window.addEventListener('mouseup', onUp);
            e.preventDefault();
        });
    }
    const debugContinueBtn = document.getElementById('debug-continue') as HTMLElement & { disabled: boolean };
    const debugPauseBtn = document.getElementById('debug-pause') as HTMLElement & { disabled: boolean };
    const debugStepOverBtn = document.getElementById('debug-step-over') as HTMLElement & { disabled: boolean };
    const debugStepInBtn = document.getElementById('debug-step-in') as HTMLElement & { disabled: boolean };
    const debugStepOutBtn = document.getElementById('debug-step-out') as HTMLElement & { disabled: boolean };
    const debugStopBtn = document.getElementById('debug-stop') as HTMLElement & { disabled: boolean };
    const debugStatusEl = document.getElementById('debug-status')!;
    const debugFramesList = document.getElementById('debug-frames-list')!;
    const debugFramesEmpty = document.getElementById('debug-frames-empty')!;
    const debugVarsTree = document.getElementById('debug-vars-tree')!;
    const debugVarsEmpty = document.getElementById('debug-vars-empty')!;
    const debugReplOutput = document.getElementById('debug-repl-output')!;
    const debugReplInput = document.getElementById('debug-repl-input') as HTMLInputElement;
    // Watch + Breakpoints section refs (consolidated Debug panel).
    const watchListEl = document.getElementById('watch-list')!;
    const watchInputRow = document.getElementById('watch-input-row')!;
    const watchInput = document.getElementById('watch-input') as HTMLInputElement;
    const watchAddBtn = document.getElementById('watch-add-btn')!;
    const breakpointsListEl = document.getElementById('breakpoints-list')!;
    const breakpointsEmpty = document.getElementById('breakpoints-empty')!;
    const breakpointsClearBtn = document.getElementById('breakpoints-clear-btn')!;

    // ─── Section collapse (Variables / Watch / Call Stack / Breakpoints) ──
    for (const toggle of Array.from(document.querySelectorAll<HTMLElement>('.debug-section-toggle'))) {
        toggle.addEventListener('click', (e) => {
            // Don't collapse when clicking the "+" / clear buttons.
            if ((e.target as HTMLElement).classList.contains('section-action')) return;
            const body = document.querySelector<HTMLElement>(
                `[data-section-body="${toggle.dataset.section}"]`,
            );
            if (!body) return;
            const collapsed = body.classList.toggle('collapsed');
            toggle.classList.toggle('collapsed', collapsed);
        });
    }

    // Status string → CSS class on the status pill (drives color).
    function setDebugStatus(text: string, kind: 'idle' | 'running' | 'paused' | 'error' = 'idle') {
        debugStatusEl.textContent = text;
        debugStatusEl.className = 'debug-status ' + kind;
    }

    function setDebugEmptyStates(visible: boolean, message?: string) {
        const msg = message ?? 'No active debug session';
        debugFramesEmpty.textContent = msg;
        debugVarsEmpty.textContent = msg;
        debugFramesEmpty.style.display = visible ? '' : 'none';
        debugVarsEmpty.style.display = visible ? '' : 'none';
    }
    setDebugEmptyStates(true);

    // Wipe the live content of the inspection panels — used when a session
    // ends so we don't display stale frame/variable data from the last
    // pause point.
    function clearDebugInspectionPanels() {
        debugFramesList.innerHTML = '';
        debugVarsTree.innerHTML = '';
        activeFrameId = null;
        expandedVars.clear();
        // Watch expressions stay in the list (user-owned), but their values
        // become stale "—" until the next pause.
        for (const v of Array.from(watchListEl.querySelectorAll<HTMLElement>('.watch-value'))) {
            v.textContent = '—';
            v.className = 'watch-value';
        }
    }

    // Breakpoints are keyed per URI → set of 1-based line numbers.
    const breakpointsByUri = new Map<string, Set<number>>();
    let debugSessionActive = false;
    let debugPaused = false;
    let activeFrameId: number | null = null;
    // Decoration IDs the editor uses to draw breakpoint glyphs + the
    // "current line" highlight when paused.
    let bpDecorations: string[] = [];
    let currentLineDecorations: string[] = [];

    function setDebugButtons() {
        const hasSession = debugSessionActive;
        const paused = hasSession && debugPaused;
        debugContinueBtn.disabled = !paused;
        debugPauseBtn.disabled = !hasSession || paused;
        debugStepOverBtn.disabled = !paused;
        debugStepInBtn.disabled = !paused;
        debugStepOutBtn.disabled = !paused;
        debugStopBtn.disabled = !hasSession;
        debugReplInput.disabled = !paused;
        debugBtn.disabled = hasSession;
        runBtn.disabled = hasSession;
        // The whole control bar is hidden until a session starts (mirrors
        // VSCode's floating debug toolbar — it doesn't exist when nothing
        // is debugging).
        debugControlBar.toggleAttribute('hidden', !hasSession);
    }
    setDebugButtons();

    function refreshBreakpointDecorations() {
        const model = editor?.getModel();
        if (!model) return;
        const uri = model.uri.toString();
        const lines = breakpointsByUri.get(uri) ?? new Set();
        const decos: monaco.editor.IModelDeltaDecoration[] = [];
        for (const ln of lines) {
            decos.push({
                range: new monaco.Range(ln, 1, ln, 1),
                options: {
                    isWholeLine: false,
                    glyphMarginClassName: 'fade-breakpoint codicon codicon-circle-filled',
                    glyphMarginHoverMessage: { value: 'Breakpoint' },
                },
            });
        }
        bpDecorations = model.deltaDecorations(bpDecorations, decos);
    }

    function setCurrentLine(line: number | null) {
        const model = editor?.getModel();
        if (!model) return;
        if (line == null) {
            currentLineDecorations = model.deltaDecorations(currentLineDecorations, []);
            return;
        }
        currentLineDecorations = model.deltaDecorations(currentLineDecorations, [{
            range: new monaco.Range(line, 1, line, 1),
            options: {
                isWholeLine: true,
                className: 'fade-current',
                glyphMarginClassName: 'codicon codicon-debug-stackframe fade-current',
            },
        }]);
        // Scroll the editor so the current execution line is in view. Use
        // revealLineInCenterIfOutsideViewport so we don't jitter when the
        // line is already visible.
        try {
            editor?.revealLineInCenterIfOutsideViewport(line, monaco.editor.ScrollType.Smooth);
        } catch { /* editor may not be ready */ }
    }

    function syncBreakpointsToWorker() {
        const model = editor?.getModel();
        if (!model) return;
        const uri = model.uri.toString();
        const lines = [...(breakpointsByUri.get(uri) ?? new Set<number>())];
        // Monaco lines are 1-based; the lexer/token lineNumber the bridge
        // expects is 0-based. Drop one.
        const payload: BreakpointRequest[] = lines.map((ln) => ({ line: ln - 1, column: 0 }));
        void runner.debugSetBreakpoints(payload);
    }

    // Click in the glyph margin toggles a breakpoint on that line.
    editor.onMouseDown((e) => {
        if (e.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN) return;
        const line = e.target.position?.lineNumber;
        if (line == null) return;
        const model = editor!.getModel();
        if (!model) return;
        const uri = model.uri.toString();
        let set = breakpointsByUri.get(uri);
        if (!set) { set = new Set(); breakpointsByUri.set(uri, set); }
        if (set.has(line)) set.delete(line);
        else set.add(line);
        refreshBreakpointDecorations();
        renderBreakpoints();
        if (debugSessionActive) syncBreakpointsToWorker();
    });

    // ─── Breakpoints section ────────────────────────────────────────────
    function uriBasename(uri: string): string {
        const ix = uri.lastIndexOf('/');
        return ix >= 0 ? uri.slice(ix + 1) : uri;
    }

    function renderBreakpoints() {
        breakpointsListEl.innerHTML = '';
        let total = 0;
        for (const [uri, lines] of breakpointsByUri) {
            const sorted = [...lines].sort((a, b) => a - b);
            for (const line of sorted) {
                total++;
                const li = document.createElement('li');
                li.className = 'bp-item';
                li.title = `${uri}:${line}`;

                const dot = document.createElement('span');
                dot.className = 'bp-dot';
                li.append(dot);

                const loc = document.createElement('span');
                loc.className = 'bp-loc';
                const name = document.createElement('span');
                name.textContent = uriBasename(uri);
                const lineEl = document.createElement('span');
                lineEl.className = 'bp-line';
                lineEl.textContent = `:${line}`;
                loc.append(name, lineEl);
                li.append(loc);

                const remove = document.createElement('button');
                remove.className = 'bp-remove';
                remove.title = 'Remove breakpoint';
                remove.textContent = '✕';
                remove.onclick = (e) => {
                    e.stopPropagation();
                    const set = breakpointsByUri.get(uri);
                    if (!set) return;
                    set.delete(line);
                    if (set.size === 0) breakpointsByUri.delete(uri);
                    refreshBreakpointDecorations();
                    renderBreakpoints();
                    if (debugSessionActive) syncBreakpointsToWorker();
                };
                li.append(remove);

                // Clicking a breakpoint row reveals the line in the editor.
                li.onclick = () => {
                    editor?.revealLineInCenter(line, monaco.editor.ScrollType.Smooth);
                    editor?.setPosition({ lineNumber: line, column: 1 });
                    editor?.focus();
                };
                breakpointsListEl.append(li);
            }
        }
        breakpointsEmpty.style.display = total === 0 ? '' : 'none';
    }
    renderBreakpoints();

    breakpointsClearBtn.addEventListener('click', () => {
        if (breakpointsByUri.size === 0) return;
        if (!confirm('Remove all breakpoints?')) return;
        breakpointsByUri.clear();
        refreshBreakpointDecorations();
        renderBreakpoints();
        if (debugSessionActive) syncBreakpointsToWorker();
    });

    // ─── Watch section ──────────────────────────────────────────────────
    // Watch expressions live on the client; we re-evaluate them via
    // debugEval on every pause (and on add).
    const watchExpressions: string[] = [];

    function renderWatches() {
        watchListEl.innerHTML = '';
        for (let i = 0; i < watchExpressions.length; i++) {
            const expr = watchExpressions[i];
            const li = document.createElement('li');
            li.className = 'watch-item';
            const exprEl = document.createElement('span');
            exprEl.className = 'watch-expr';
            exprEl.textContent = expr;
            exprEl.title = expr;
            li.append(exprEl);

            const valEl = document.createElement('span');
            valEl.className = 'watch-value';
            valEl.textContent = '…';
            li.append(valEl);

            const removeBtn = document.createElement('button');
            removeBtn.className = 'watch-remove';
            removeBtn.title = 'Remove';
            removeBtn.textContent = '✕';
            removeBtn.onclick = (e) => {
                e.stopPropagation();
                watchExpressions.splice(i, 1);
                renderWatches();
                void refreshWatches();
            };
            li.append(removeBtn);
            watchListEl.append(li);
        }
    }

    async function refreshWatches() {
        const items = Array.from(watchListEl.querySelectorAll<HTMLElement>('.watch-item'));
        for (let i = 0; i < watchExpressions.length && i < items.length; i++) {
            const expr = watchExpressions[i];
            const valEl = items[i].querySelector<HTMLElement>('.watch-value')!;
            if (!debugSessionActive || !debugPaused || activeFrameId == null) {
                valEl.textContent = '—';
                valEl.className = 'watch-value';
                continue;
            }
            try {
                const result = await runner.debugEval(activeFrameId, expr);
                if (!result || result.id === -1) {
                    valEl.textContent = result?.value ?? 'error';
                    valEl.className = 'watch-value error';
                } else {
                    valEl.textContent = result.value;
                    const cls = valueColorClassForWatch(result.type);
                    valEl.className = 'watch-value' + (cls ? ' ' + cls : '');
                }
            } catch (e: any) {
                valEl.textContent = e?.message ?? 'error';
                valEl.className = 'watch-value error';
            }
        }
    }

    function valueColorClassForWatch(type: string | null | undefined): string {
        const t = (type ?? '').toLowerCase();
        if (t === 'string') return 'string';
        if (t === 'integer' || t === 'int' || t === 'real' || t === 'double' || t === 'float'
            || t === 'word' || t === 'byte') return 'number';
        return '';
    }

    function openWatchInput() {
        watchInputRow.removeAttribute('hidden');
        watchInput.value = '';
        watchInput.focus();
    }
    function closeWatchInput() { watchInputRow.setAttribute('hidden', ''); }
    watchAddBtn.addEventListener('click', openWatchInput);
    watchInput.addEventListener('keydown', async (e) => {
        if (e.key === 'Enter') {
            const expr = watchInput.value.trim();
            if (expr) {
                watchExpressions.push(expr);
                renderWatches();
                await refreshWatches();
            }
            closeWatchInput();
        } else if (e.key === 'Escape') {
            closeWatchInput();
        }
    });
    watchInput.addEventListener('blur', closeWatchInput);

    function appendReplLine(text: string, kind: 'in' | 'out' | 'err' = 'out') {
        if (!text) return;
        const prefix = kind === 'in' ? '› ' : kind === 'err' ? '! ' : '  ';
        debugReplOutput.textContent += prefix + text + '\n';
        debugReplOutput.scrollTop = debugReplOutput.scrollHeight;
    }

    // DebugStackFrame from C# only carries lineNumber/colNumber/name — frames
    // are addressed by *index* in the returned list (DebugScopeRequest.frameIndex).
    // Treat that index as the id we surface to the rest of this UI.
    async function refreshDebugView() {
        const frames = await runner.debugStackFrames();
        renderFrames(frames);
        if (frames.length > 0) {
            activeFrameId = 0;
            setCurrentLine(frames[0].lineNumber + 1);
            await refreshScopes(0);
            await refreshWatches();
            setDebugEmptyStates(false);
        } else {
            activeFrameId = null;
            setCurrentLine(null);
            debugVarsTree.innerHTML = '';
            setDebugEmptyStates(true);
            await refreshWatches();
        }
    }

    function renderFrames(frames: DebugStackFrame[]) {
        debugFramesList.innerHTML = '';
        frames.forEach((f, idx) => {
            const li = document.createElement('li');
            li.className = 'debug-frame' + (idx === activeFrameId ? ' active' : '');
            const name = document.createElement('span');
            name.className = 'frame-name';
            name.textContent = f.name || '<anon>';
            const loc = document.createElement('span');
            loc.className = 'frame-loc';
            loc.textContent = `${f.lineNumber + 1}:${f.colNumber + 1}`;
            li.append(name, loc);
            li.onclick = async () => {
                activeFrameId = idx;
                setCurrentLine(f.lineNumber + 1);
                renderFrames(frames);
                await refreshScopes(idx);
            };
            debugFramesList.append(li);
        });
    }

    // Expansion state: variableId → DebugScope (children) when expanded.
    const expandedVars = new Map<number, DebugScope[]>();

    async function refreshScopes(frameId: number) {
        const result = await runner.debugScopes(frameId);
        renderScopes(result.scopes ?? []);
    }

    // Per-scope collapsed state, keyed by scope name. Defaults to expanded.
    const collapsedScopes = new Set<string>();

    function renderScopes(scopes: DebugScope[]) {
        debugVarsTree.innerHTML = '';
        for (const scope of scopes) {
            const collapsed = collapsedScopes.has(scope.scopeName);

            const header = document.createElement('div');
            header.className = 'debug-scope-header' + (collapsed ? ' collapsed' : '');
            header.innerHTML = ''; // we'll append twisty + label
            const twisty = document.createElement('span');
            twisty.className = 'scope-twisty';
            twisty.textContent = collapsed ? '▸' : '▾';
            header.append(twisty);
            const label = document.createElement('span');
            label.textContent = scope.scopeName;
            header.append(label);
            debugVarsTree.append(header);

            const body = document.createElement('div');
            body.className = 'debug-scope-body';
            body.style.display = collapsed ? 'none' : '';
            for (const v of scope.variables) body.append(renderVariable(v, scope.id));
            debugVarsTree.append(body);

            header.addEventListener('click', () => {
                if (collapsedScopes.has(scope.scopeName)) collapsedScopes.delete(scope.scopeName);
                else collapsedScopes.add(scope.scopeName);
                const nowCollapsed = collapsedScopes.has(scope.scopeName);
                twisty.textContent = nowCollapsed ? '▸' : '▾';
                body.style.display = nowCollapsed ? 'none' : '';
                header.classList.toggle('collapsed', nowCollapsed);
            });
        }
    }

    // VSCode renders simple values in type-specific colors — match the
    // Monaco theme tokens so the debug pane looks consistent with the
    // editor: strings #CE9178, numbers #B5CEA8, identifiers default fg.
    function valueColorClass(type: string | null | undefined): string {
        const t = (type ?? '').toLowerCase();
        if (t === 'string') return 'string';
        if (t === 'integer' || t === 'int' || t === 'real' || t === 'double' || t === 'float' || t === 'word' || t === 'byte') return 'number';
        return '';
    }

    function renderVariable(v: DebugVariable, _parentScopeId: number, indent = 0): HTMLElement {
        const wrap = document.createElement('div');
        wrap.className = 'debug-var-wrap';

        const row = document.createElement('div');
        row.className = 'debug-var';
        row.style.paddingLeft = (0.3 + indent * 0.9) + 'rem';

        // Twisty: codicon chevron when expandable; invisible placeholder
        // otherwise (so the names line up like in VSCode).
        const twisty = document.createElement('span');
        twisty.className = 'twisty';
        const expandable = (v.fieldCount + v.elementCount) > 0;
        if (!expandable) twisty.classList.add('empty');
        twisty.textContent = expandable ? (expandedVars.has(v.id) ? '▾' : '▸') : '';
        row.append(twisty);

        // Body: `name : value` with optional type annotation. The body is
        // its own flex container so the value can grow + ellipsize while
        // the name stays at a fixed left.
        const body = document.createElement('span');
        body.className = 'debug-var-body';

        const name = document.createElement('span');
        name.className = 'debug-var-name';
        name.textContent = v.name;
        body.append(name);

        const sep = document.createElement('span');
        sep.className = 'debug-var-sep';
        sep.textContent = ':';
        body.append(sep);

        const value = document.createElement('span');
        value.className = 'debug-var-value';
        const colorClass = valueColorClass(v.type);
        if (colorClass) value.classList.add(colorClass);
        value.textContent = v.value;
        // Tooltip shows full value (handy when ellipsized) + type + edit hint.
        value.title = `${v.value}\n\n(${v.type ?? 'unknown'} — click to edit)`;
        body.append(value);

        if (v.type) {
            const typeEl = document.createElement('span');
            typeEl.className = 'debug-var-type';
            typeEl.textContent = v.type;
            body.append(typeEl);
        }
        row.append(body);

        // Children container (rendered when expanded)
        const childrenWrap = document.createElement('div');
        childrenWrap.className = 'debug-var-children';

        async function toggle() {
            if (!expandable) return;
            if (expandedVars.has(v.id)) {
                expandedVars.delete(v.id);
                childrenWrap.innerHTML = '';
                twisty.textContent = '▸';
                return;
            }
            try {
                const result = await runner.debugExpandVariable(v.id);
                expandedVars.set(v.id, result.scopes ?? []);
                childrenWrap.innerHTML = '';
                for (const subScope of result.scopes ?? []) {
                    for (const child of subScope.variables) {
                        childrenWrap.append(renderVariable(child, subScope.id, indent + 1));
                    }
                }
                twisty.textContent = '▾';
            } catch (e) {
                console.warn('[fade] variable expand failed', v.name, e);
            }
        }
        // Toggle on twisty OR on the name (matches VSCode's behavior so the
        // entire compound-variable row is a target).
        twisty.addEventListener('click', toggle);
        if (expandable) {
            name.style.cursor = 'pointer';
            name.addEventListener('click', toggle);
        }

        // Single click on the value enters edit mode (matches VSCode). We
        // use mousedown.preventDefault on dblclick so double-clicks don't
        // select text.
        value.addEventListener('mousedown', (e) => { if (e.detail >= 2) e.preventDefault(); });
        value.addEventListener('click', (e) => {
            if (activeFrameId == null) return;
            e.stopPropagation();
            enterEditMode();
        });

        function enterEditMode() {
            if (value.classList.contains('editing')) return;
            value.classList.add('editing');
            const original = v.value;
            value.textContent = '';
            const input = document.createElement('input');
            input.value = original;
            input.spellcheck = false;
            value.append(input);
            input.focus();
            input.select();
            let done = false;
            const commit = async (apply: boolean) => {
                if (done) return;
                done = true;
                value.classList.remove('editing');
                if (!apply) {
                    value.textContent = original;
                    return;
                }
                const rhs = input.value;
                try {
                    const result = await runner.debugSetVariable(activeFrameId!, v.id, rhs);
                    // DebugEvalResult signals failure with id === -1 + value
                    // carrying the error message (there is no `failed`
                    // boolean — Fade DAP uses the id-sentinel convention).
                    if (!result || result.id === -1) {
                        appendReplLine(`${v.name} = ${rhs}  → ${result?.value ?? '(no result)'}`, 'err');
                    } else {
                        appendReplLine(`${v.name} = ${rhs}  → ${result.value ?? '(no result)'}`, 'out');
                    }
                } catch (e: any) {
                    appendReplLine(`${v.name} = ${rhs}  → ${e?.message ?? e}`, 'err');
                }
                // Refresh scopes so the value reflects what the VM actually has.
                if (activeFrameId != null) await refreshScopes(activeFrameId);
            };
            input.addEventListener('keydown', (e) => {
                if (e.key === 'Enter') { e.preventDefault(); void commit(true); }
                else if (e.key === 'Escape') { e.preventDefault(); void commit(false); }
            });
            input.addEventListener('blur', () => void commit(true));
        }

        // wrap was declared at the top of this function; append row +
        // children container in order so expanded children render below.
        wrap.append(row, childrenWrap);
        return wrap;
    }

    // Reveal a panel so the user notices state changes (e.g. paused, error).
    function revealPanel(panelId: string) {
        try {
            const p = dockApi.getPanel(panelId);
            if (p) p.api.setActive();
        } catch { /* dockview may not be ready yet */ }
    }

    runner.onDebugEvent = async (event) => {
        switch (event.type) {
            case 'REV_REQUEST_BREAKPOINT':
                debugPaused = true;
                setDebugStatus('paused on breakpoint', 'paused');
                revealPanel('call-stack');
                setDebugButtons();
                await refreshDebugView();
                break;
            case 'REV_REQUEST_EXITED':
            case 'complete':
                debugSessionActive = false;
                debugPaused = false;
                setDebugStatus('program exited', 'idle');
                setCurrentLine(null);
                clearDebugInspectionPanels();
                setDebugEmptyStates(true);
                setDebugButtons();
                break;
            case 'REV_REQUEST_EXPLODE':
                debugSessionActive = false;
                debugPaused = false;
                setDebugStatus('runtime error', 'error');
                appendReplLine(event.json ?? event.message ?? 'runtime error', 'err');
                revealPanel('debug-console');
                setCurrentLine(null);
                clearDebugInspectionPanels();
                setDebugEmptyStates(true);
                setDebugButtons();
                break;
            case 'PROTO_ACK': {
                // Two kinds of PROTO_ACKs reach us:
                //   1. StepNextResponseMessage with status=1 — a step landed
                //      successfully. DebugSession only signals step completion
                //      this way (no separate stop event), so the native DAP
                //      adapter translates this ACK into a DAP "Stopped" event.
                //      We do the same here: refresh the call stack + variables
                //      and stay paused.
                //   2. Plain ACK for set-breakpoints / continue / etc. —
                //      treat as "session resumed".
                let stepLanded = false;
                if (event.json) {
                    try {
                        const parsed = JSON.parse(event.json);
                        if (parsed && parsed.status === 1 && typeof parsed.reason === 'string') {
                            stepLanded = true;
                        }
                    } catch { /* not a structured response */ }
                }
                if (stepLanded) {
                    debugPaused = true;
                    setDebugStatus('paused on step', 'paused');
                    setDebugButtons();
                    await refreshDebugView();
                } else {
                    debugPaused = false;
                    setDebugStatus('running', 'running');
                    setCurrentLine(null);
                    // While running, show a guidance message in the inspection
                    // panes instead of "No active debug session" — there IS
                    // a session, the user just can't see anything yet.
                    setDebugEmptyStates(true, 'Running — hit a breakpoint or pause to inspect');
                    setDebugButtons();
                }
                break;
            }
            case 'error':
                appendReplLine(event.message ?? 'error', 'err');
                break;
        }
    };

    const startDebug = async () => {
        const activeTab = activeName ? tabs.get(activeName) : null;
        if (!activeTab) {
            outputEl.textContent = 'No file open.';
            return;
        }
        await beginDebugSession(() => runner.debugStart(activeTab.model.getValue()));
    };

    // Shared session-start machinery, factored so both Debug-button and
    // per-test Debug share the same "prep UI → start → sync bps → continue"
    // sequence.
    async function beginDebugSession(starter: () => Promise<DebugStartResult>): Promise<boolean> {
        outputEl.textContent = '';
        debugReplOutput.textContent = '';
        setDebugStatus('starting', 'paused');
        debugBtn.disabled = true;
        const result = await starter();
        if (!result.ok) {
            setDebugStatus('failed to start', 'error');
            appendReplLine(result.error ?? 'Failed to start', 'err');
            debugBtn.disabled = false;
            return false;
        }
        debugSessionActive = true;
        debugPaused = true;
        setDebugStatus('starting', 'paused');
        setDebugButtons();
        syncBreakpointsToWorker();
        await runner.debugContinue();
        debugPaused = false;
        setDebugStatus('running', 'running');
        setDebugButtons();
        return true;
    }

    // Per-test debug entry. Compiles the current file, starts a VM at the
    // chosen test's entry point, then proceeds like a normal debug session.
    async function debugSingleTest(name: string) {
        const activeTab = activeName ? tabs.get(activeName) : null;
        if (!activeTab) {
            outputEl.textContent = 'No file open.';
            return;
        }
        appendReplLine(`▶ debug test "${name}"`, 'in');
        await beginDebugSession(() => runner.debugStartTest(activeTab.model.getValue(), name));
    }

    debugBtn.addEventListener('click', startDebug);
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyD, startDebug);

    // Resolve the test entry whose body contains a given (1-based) line. We
    // don't have end-of-test offsets in the manifest, so we treat each test
    // as spanning from its `sourceLine` up to the next test's `sourceLine`
    // (or EOF). Abstract tests aren't picked — they're not runnable.
    function testUnderLine(oneBasedLine: number): TestUiEntry | null {
        const runnable = testEntries
            .filter((t) => !t.isAbstract)
            .map((t) => ({ t, line: (t.sourceLine | 0) + 1 }))
            .sort((a, b) => a.line - b.line);
        let chosen: TestUiEntry | null = null;
        for (const { t, line } of runnable) {
            if (line <= oneBasedLine) chosen = t;
            else break;
        }
        return chosen;
    }

    // "Run Test at Cursor" / "Debug Test at Cursor" — surface in the editor
    // context menu under the navigation group. precondition keeps the items
    // hidden in non-fade files.
    editor.addAction({
        id: 'fade.runTestAtCursor',
        label: 'Run Test at Cursor',
        contextMenuGroupId: 'navigation',
        contextMenuOrder: 2.1,
        precondition: 'editorLangId == fade',
        run: (ed) => {
            const pos = ed.getPosition();
            if (!pos) return;
            const t = testUnderLine(pos.lineNumber);
            if (!t) {
                testsStatusEl.textContent = 'No test under cursor';
                return;
            }
            runSingleTest(t.name);
        },
    });
    editor.addAction({
        id: 'fade.debugTestAtCursor',
        label: 'Debug Test at Cursor',
        contextMenuGroupId: 'navigation',
        contextMenuOrder: 2.2,
        precondition: 'editorLangId == fade',
        run: (ed) => {
            const pos = ed.getPosition();
            if (!pos) return;
            const t = testUnderLine(pos.lineNumber);
            if (!t) {
                testsStatusEl.textContent = 'No test under cursor';
                return;
            }
            debugSingleTest(t.name);
        },
    });

    debugContinueBtn.addEventListener('click', async () => {
        await runner.debugContinue();
        debugPaused = false;
        setDebugStatus('running', 'running');
        setCurrentLine(null);
        setDebugButtons();
    });
    debugPauseBtn.addEventListener('click', async () => {
        await runner.debugPause();
        // The 'paused' state is asserted by the next breakpoint event.
    });
    debugStepOverBtn.addEventListener('click', async () => {
        await runner.debugStep('over');
        debugPaused = false;
        setCurrentLine(null);
        setDebugButtons();
    });
    debugStepInBtn.addEventListener('click', async () => {
        await runner.debugStep('in');
        debugPaused = false;
        setCurrentLine(null);
        setDebugButtons();
    });
    debugStepOutBtn.addEventListener('click', async () => {
        await runner.debugStep('out');
        debugPaused = false;
        setCurrentLine(null);
        setDebugButtons();
    });
    debugStopBtn.addEventListener('click', async () => {
        await runner.debugTerminate();
        debugSessionActive = false;
        debugPaused = false;
        setDebugStatus('stopped', 'idle');
        setCurrentLine(null);
        // Clear the live content so previous frame/variable data doesn't
        // linger under the empty-state message.
        clearDebugInspectionPanels();
        setDebugEmptyStates(true);
        setDebugButtons();
    });

    debugReplInput.addEventListener('keydown', async (e) => {
        if (e.key !== 'Enter') return;
        const expr = debugReplInput.value.trim();
        if (!expr || activeFrameId == null) return;
        appendReplLine(expr, 'in');
        debugReplInput.value = '';
        const result = await runner.debugRepl(activeFrameId, expr);
        const failed = !result || result.id === -1;
        appendReplLine(result?.value ?? '(no result)', failed ? 'err' : 'out');
        // Variables may have changed.
        if (activeFrameId != null) await refreshScopes(activeFrameId);
    });

    // Editor option: glyph margin must be on to show breakpoint glyphs.
    editor.updateOptions({ glyphMargin: true });

    // New-file button
    newFileBtn.addEventListener('click', async () => {
        const name = prompt('File name (e.g. helper.fbasic)');
        if (!name) return;
        if (!/^[\w.-]+$/.test(name)) {
            alert('Invalid name. Letters, digits, dot, dash, underscore only.');
            return;
        }
        try {
            // Check if already exists
            const names = await workspace.list();
            if (names.includes(name)) {
                alert('File already exists.');
                return;
            }
            await workspace.write(name, '');
            await openFile(workspace, name);
        } catch (e) {
            console.error('[fade] new-file failed:', e);
            alert('Failed to create file: ' + e);
        }
    });

    // Test probe — bypasses Monaco UI and goes straight to the worker. Used by
    // scripts/test-lsp.mjs to validate Core handlers independent of editor wiring.
    (window as any).__fadeLspProbe = async (method: string, params: any) => {
        const m = monaco.editor.getModels().find((mod) => mod.getLanguageId() === 'fade');
        if (!m) throw new Error('no fade model');
        const uri = m.uri.toString();
        switch (method) {
            case 'completion': return runner.getCompletions(uri, params.line, params.character);
            case 'hover': return runner.getHover(uri, params.line, params.character);
            case 'signature-help': return runner.getSignatureHelp(uri, params.line, params.character);
            case 'references': return runner.getReferences(uri, params.line, params.character);
            case 'goto-def': return runner.getDefinition(uri, params.line, params.character);
            case 'document-symbols': return runner.getDocumentSymbols(uri);
            case 'folding-ranges': return runner.getFoldingRanges(uri);
            case 'format': return runner.format(uri, params.options ?? { tabSize: 4, insertSpaces: true, casing: 0 });
            case 'format-range': return runner.formatRange(uri, params.options ?? { tabSize: 4, insertSpaces: true, casing: 0 }, params.range);
            case 'rename': return runner.rename(uri, params.line, params.character, params.newName);
            default: throw new Error('unknown probe method: ' + method);
        }
    };

    // Worker-direct helpers for tests that don't depend on the active model
    // (test list / run-tests take a source string explicitly).
    (window as any).__fadeRunnerHelpers = {
        listTests: ({ source }: { source: string }) => runner.listTests(source),
        runTests: ({ source, name }: { source: string; name?: string }) => runner.runTests(source, name),
        debug: {
            start: ({ source }: { source: string }) => runner.debugStart(source),
            startTest: ({ source, name }: { source: string; name: string }) =>
                runner.debugStartTest(source, name),
            terminate: () => runner.debugTerminate(),
            setBreakpoints: ({ breakpoints }: { breakpoints: BreakpointRequest[] }) => runner.debugSetBreakpoints(breakpoints),
            step: ({ kind }: { kind: 'over' | 'in' | 'out' }) => runner.debugStep(kind),
            continue: () => runner.debugContinue(),
            pause: () => runner.debugPause(),
            stackFrames: () => runner.debugStackFrames(),
            scopes: ({ frameId }: { frameId: number }) => runner.debugScopes(frameId),
            expand: ({ variableId }: { variableId: number }) => runner.debugExpandVariable(variableId),
            eval: ({ frameId, expression }: { frameId: number; expression: string }) =>
                runner.debugEval(frameId, expression),
            repl: ({ frameId, code }: { frameId: number; code: string }) =>
                runner.debugRepl(frameId, code),
            setVariable: ({ frameId, variableId, rhs }: { frameId: number; variableId: number; rhs: string }) =>
                runner.debugSetVariable(frameId, variableId, rhs),
        },
    };

    // Tests inspect the last debug event by polling `window.__debugLastEvent`.
    // Chain into our existing onDebugEvent so the UI keeps working.
    const _origOnDebugEvent = runner.onDebugEvent;
    runner.onDebugEvent = (event) => {
        (window as any).__debugLastEvent = event;
        if (_origOnDebugEvent) _origOnDebugEvent(event);
    };

    (window as any).__fadeBootstrapDone = true;
}

// Force full reload on HMR (codingame services can only init once per page).
if (import.meta.hot) {
    import.meta.hot.accept(() => location.reload());
}
if ((window as any).__fadeBootstrapStarted) {
    console.warn('[fade] bootstrap already started this page — skipping');
} else {
    (window as any).__fadeBootstrapStarted = true;
    bootstrap().catch((e) => {
        console.error('bootstrap failed', e);
        statusEl.textContent = 'Bootstrap failed: ' + (e?.message ?? e);
    });
}
