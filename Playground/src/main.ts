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
// virtualFs.registerFile() throws if called twice for the same URI. We
// track registered URIs so reopening a previously-closed tab no-ops the
// registration step instead of crashing with "file already exists".
const registeredVirtualFsUris = new Set<string>();

import EditorWorker from '@codingame/monaco-vscode-api/workers/editor.worker?worker';
import { languageForExtra, registerExtraLanguages, extraThemeRules } from './languages';
import { createMarkdownPreview, previewPanelIdFor } from './markdown-preview';
import {
    createBinaryPreview,
    BINARY_PREVIEW_PANEL_ID,
    LEGACY_BINARY_PREVIEW_ID_PREFIX,
    isBinaryFileName,
} from './binary-preview';
import { patchEffectMgfxVersionForKni, patchSoundEffectForKni } from './xnb/xnb-previews';
import { mountHelpPanel } from './help';
import { monoGameHost } from './monogame-host';
import type { CommandDocEntry as HelpCommandDocEntry } from './help';
import {
    FADE_JSON_NAME,
    defaultFadeProject,
    stringifyFadeProject,
    parseFadeProject,
    locateJsonPaths,
    offsetsToLineCol,
    type FadeProject,
    type FadeConfigError,
} from './fade-config';
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
const stopBtn = document.getElementById('stop') as HTMLElement & { disabled: boolean };
const debugBtn = document.getElementById('debug') as HTMLElement & { disabled: boolean };
const resetLayoutBtn = document.getElementById('reset-layout') as HTMLElement;
const newFileBtn = document.getElementById('new-file') as HTMLButtonElement;
const fileListEl = document.getElementById('file-list')!;
const tabsEl = document.getElementById('tabs')!;
const editorContainer = document.getElementById('editor')!;
const editorPlaceholder = document.getElementById('editor-placeholder')!;
const outputEl = document.getElementById('output')!;
// ─── OPFS workspace ─────────────────────────────────────────────────────────
// Project-aware (folder-per-project) layout:
//
//   workspace/
//     <project-name>/
//       fade.json     ← required manifest, locked from create/rename/delete
//       main.fbasic
//       util.fbasic
//       …
//
// On first init we migrate any legacy flat files at workspace/<file> into
// workspace/default/<file> and synthesize a starter fade.json listing the
// .fbasic files we found. Active project lives in localStorage so reloads
// land in the same place.

const ACTIVE_PROJECT_KEY = 'fade.activeProject';
const DEFAULT_PROJECT_NAME = 'default';

class OpfsWorkspace {
    private root!: FileSystemDirectoryHandle;       // workspace/
    private dir!: FileSystemDirectoryHandle;        // workspace/<active-project>/
    private activeProject: string = DEFAULT_PROJECT_NAME;

    async init() {
        const opfsRoot = await navigator.storage.getDirectory();
        this.root = await opfsRoot.getDirectoryHandle('workspace', { create: true });

        // Migrate any legacy flat files (workspace/<file>) into a default
        // project folder so the new layout invariant holds.
        await this.migrateLegacyFlatLayout();

        // Determine which project to open. Validated against the actual
        // folders on disk; if the stored name is gone, fall back to the
        // first project we find (creating one if none exist).
        let target = localStorage.getItem(ACTIVE_PROJECT_KEY) || DEFAULT_PROJECT_NAME;
        const projects = await this.listProjects();
        if (!projects.includes(target)) target = projects[0] ?? DEFAULT_PROJECT_NAME;
        await this.setActiveProject(target, /*seedIfEmpty*/ true);
    }

    // Promote any leaf files at the workspace root into a project folder.
    // Idempotent: if there are no flat files, this does nothing.
    private async migrateLegacyFlatLayout(): Promise<void> {
        const flat: string[] = [];
        for await (const entry of (this.root as any).values()) {
            if (entry.kind === 'file') flat.push(entry.name);
        }
        if (flat.length === 0) return;
        const dest = await this.root.getDirectoryHandle(DEFAULT_PROJECT_NAME, { create: true });
        const movedSources: string[] = [];
        for (const name of flat) {
            const srcFh = await this.root.getFileHandle(name);
            const srcText = await (await srcFh.getFile()).text();
            const dstFh = await dest.getFileHandle(name, { create: true });
            const w = await dstFh.createWritable();
            await w.write(srcText);
            await w.close();
            await this.root.removeEntry(name);
            if (/\.(fbasic|fb)$/i.test(name)) movedSources.push(name);
        }
        // Synthesize fade.json if it wasn't part of the legacy set.
        let alreadyHasManifest = false;
        for await (const entry of (dest as any).values()) {
            if (entry.kind === 'file' && entry.name === FADE_JSON_NAME) {
                alreadyHasManifest = true;
                break;
            }
        }
        if (!alreadyHasManifest) {
            const proj = defaultFadeProject(DEFAULT_PROJECT_NAME, movedSources);
            const mh = await dest.getFileHandle(FADE_JSON_NAME, { create: true });
            const mw = await mh.createWritable();
            await mw.write(stringifyFadeProject(proj));
            await mw.close();
        }
    }

    // Public API for project-level operations.
    async listProjects(): Promise<string[]> {
        const names: string[] = [];
        for await (const entry of (this.root as any).values()) {
            if (entry.kind === 'directory') names.push(entry.name);
        }
        names.sort();
        return names;
    }

    currentProject(): string { return this.activeProject; }

    async setActiveProject(name: string, seedIfEmpty: boolean = false): Promise<void> {
        this.activeProject = name;
        this.dir = await this.root.getDirectoryHandle(name, { create: true });
        localStorage.setItem(ACTIVE_PROJECT_KEY, name);
        if (seedIfEmpty) await this.seedIfEmpty();
    }

    // If the active project has no files at all, drop in a default main.fbasic
    // + fade.json so the user lands on something they can run.
    private async seedIfEmpty(): Promise<void> {
        const names = await this.list();
        if (names.length > 0) return;
        await this.write('main.fbasic', DEFAULT_SOURCE);
        const proj = defaultFadeProject(this.activeProject, ['main.fbasic']);
        await this.write(FADE_JSON_NAME, stringifyFadeProject(proj));
    }

    // Create a fresh project folder with a starter main.fbasic + fade.json.
    async createProject(name: string): Promise<void> {
        const dir = await this.root.getDirectoryHandle(name, { create: true });
        // Avoid clobbering an existing project.
        let hasAny = false;
        for await (const _ of (dir as any).values()) { hasAny = true; break; }
        if (hasAny) return;
        const mainFh = await dir.getFileHandle('main.fbasic', { create: true });
        const mainW = await mainFh.createWritable();
        await mainW.write(DEFAULT_SOURCE);
        await mainW.close();
        const proj = defaultFadeProject(name, ['main.fbasic']);
        const manifestFh = await dir.getFileHandle(FADE_JSON_NAME, { create: true });
        const manifestW = await manifestFh.createWritable();
        await manifestW.write(stringifyFadeProject(proj));
        await manifestW.close();
    }

    // File-level operations, scoped to the active project.
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

    // Binary read/write — used for uploaded assets (.xnb, .png, .wav, …).
    // The text-based read/write above is kept intact; callers route through
    // one or the other based on the file extension. The underlying OPFS
    // handle is the same; only the decode/encode shape differs.
    async readBytes(name: string): Promise<Uint8Array> {
        const fh = await this.dir.getFileHandle(name);
        const f = await fh.getFile();
        return new Uint8Array(await f.arrayBuffer());
    }

    async writeBytes(name: string, bytes: Uint8Array): Promise<void> {
        const fh = await this.dir.getFileHandle(name, { create: true });
        const w = await fh.createWritable();
        // Wrap in a Blob so the writable stream's union type doesn't
        // reject Uint8Array<ArrayBufferLike> when SharedArrayBuffer is in
        // the lib (web-worker.d.ts pulls it in for our prompt$ plumbing).
        await w.write(new Blob([bytes as BlobPart]));
        await w.close();
    }

    async delete(name: string): Promise<void> {
        if (name === FADE_JSON_NAME) {
            throw new Error('fade.json is required and cannot be deleted.');
        }
        await this.dir.removeEntry(name);
    }

    // OPFS has no atomic rename. Read → write under the new name → remove
    // the old. If write fails partway, the old file is preserved (we only
    // remove after the new file lands successfully).
    async rename(oldName: string, newName: string): Promise<void> {
        if (oldName === FADE_JSON_NAME || newName === FADE_JSON_NAME) {
            throw new Error('fade.json is required and cannot be renamed.');
        }
        if (oldName === newName) return;
        if (!/^[\w.\-]+$/.test(newName)) {
            throw new Error('Invalid name. Letters, digits, dot, dash, underscore only.');
        }
        // Collision check.
        let collision = false;
        try {
            await this.dir.getFileHandle(newName);
            collision = true;
        } catch { /* NotFoundError → free to proceed */ }
        if (collision) throw new Error(`A file named "${newName}" already exists.`);
        const content = await this.read(oldName);
        await this.write(newName, content);
        try { await this.dir.removeEntry(oldName); }
        catch (e) {
            // Best-effort cleanup. The new file already landed, so the
            // rename is effectively complete even if we couldn't remove
            // the source.
            console.warn('[fade] rename: failed to remove old file', oldName, e);
        }
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
    const extra = languageForExtra(name);
    return extra ?? 'plaintext';
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
        // The provider throws on duplicate URIs, so skip the registration
        // if we've already registered this file in a previous open.
        const uriKey = uri.toString();
        if (!registeredVirtualFsUris.has(uriKey)) {
            virtualFs.registerFile(new RegisteredMemoryFile(uri, text));
            registeredVirtualFsUris.add(uriKey);
        }
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
    // Force a semantic-token refresh for fade models. The diagnostics
    // handler also applies tokens, but it only runs after an LSP push;
    // for preloaded-but-untouched files, that may not have happened yet
    // by the time the user clicks the tab.
    if (tab.model.getLanguageId() === 'fade') {
        (window as any).__fadeRefreshSemanticTokens?.(tab.model);
    }
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
        el.append(label);

        // Markdown files get a preview-toggle button next to the label.
        // Clicking activates an existing preview panel or creates one.
        if (/\.(md|markdown)$/i.test(name)) {
            const previewBtn = document.createElement('span');
            previewBtn.className = 'tab-action';
            previewBtn.title = 'Open Markdown Preview';
            previewBtn.innerHTML = '<span class="codicon codicon-open-preview"></span>';
            previewBtn.onclick = (e) => {
                e.stopPropagation();
                openMarkdownPreview(name);
            };
            el.append(previewBtn);
        }

        const close = document.createElement('span');
        close.className = 'close';
        close.textContent = '×';
        close.onclick = (e) => {
            e.stopPropagation();
            closeTab(name);
        };
        el.append(close);
        tabsEl.append(el);
    }
}

// Open (or activate, if already present) a markdown preview panel for the
// given filename. Resolves the dockview API off the window since renderTabs
// runs at module scope, before the bootstrap closure that owns dockApi.
function openMarkdownPreview(filename: string) {
    const api = (window as any).__fadeDockview;
    if (!api) return;
    const id = previewPanelIdFor(filename);
    const existing = api.getPanel?.(id);
    if (existing) { existing.api.setActive(); return; }
    api.addPanel({
        id,
        component: 'markdown-preview',
        title: 'Preview: ' + filename,
        position: { referencePanel: 'editor', direction: 'right' },
    });
}

// Binary-file preview lives in ONE shared "Asset Preview" tab — clicking
// a different .xnb / image / sound swaps the contents of that single tab
// rather than spawning one panel per file. Mirrors VSCode preview-tab
// behavior. The component handles the actual content swap via update();
// see createBinaryPreview's update() in binary-preview.ts.
function openBinaryPreview(filename: string) {
    const api = (window as any).__fadeDockview;
    if (!api) return;
    const existing = api.getPanel?.(BINARY_PREVIEW_PANEL_ID);
    if (existing) {
        existing.api.updateParameters({ filename });
        existing.api.setTitle?.(filename);
        existing.api.setActive();
        return;
    }
    api.addPanel({
        id: BINARY_PREVIEW_PANEL_ID,
        component: 'binary-preview',
        title: filename,
        params: { filename },
        position: { referencePanel: 'editor', direction: 'within' },
    });
}

// ─── Project state surface for module-scope renderers ───────────────────
// renderFileList runs at module scope (used at boot before bootstrap()'s
// closure exists), but needs to know which sources fade.json lists today
// and how to mutate them. Bootstrap fills these in; everything reads via
// the getters so timing doesn't matter.
let currentProjectRef: FadeProject | null = null;
interface ProjectOps {
    addSourceAt(name: string, position: 'start' | 'end'): Promise<void>;
    removeSource(name: string): Promise<void>;
    revealSourceInManifest(name: string): Promise<void>;
    renameFile(name: string): Promise<void>;
    deleteFile(name: string): Promise<void>;
}
let projectOps: ProjectOps | null = null;

async function renderFileList(workspace: OpfsWorkspace) {
    const names = await workspace.list();
    fileListEl.innerHTML = '';
    const sources = currentProjectRef?.sources ?? [];
    for (const name of names) {
        const li = document.createElement('li');
        li.dataset.name = name;
        const label = document.createElement('span');
        label.textContent = name;
        li.append(label);
        if (name === FADE_JSON_NAME) {
            // Visible cue that the manifest is locked from delete/rename
            // (creation is blocked at the New-File prompt).
            const lock = document.createElement('span');
            lock.className = 'file-lock codicon codicon-lock-small';
            lock.title = 'Project manifest — required, cannot be deleted or renamed.';
            li.append(lock);
            li.classList.add('manifest');
        } else {
            // Right-click → rename / delete. fade.json is locked above
            // so it gets no menu and falls through to the browser default.
            li.addEventListener('contextmenu', (e) => {
                e.preventDefault();
                showFileContextMenu(e.clientX, e.clientY, name);
            });
        }
        // Source-membership badge for .fbasic files: numeric index when
        // listed in fade.json:sources, dash when orphaned. Informational
        // only — the matching add / remove / jump actions live on the
        // file row's right-click menu (handled below).
        if (/\.(fbasic|fb)$/i.test(name)) {
            const sourceIdx = sources.indexOf(name);
            const badge = document.createElement('span');
            if (sourceIdx >= 0) {
                badge.className = 'source-badge listed';
                badge.textContent = String(sourceIdx + 1);
                badge.title = `Source #${sourceIdx + 1} in fade.json`;
            } else {
                badge.className = 'source-badge orphan';
                badge.textContent = '–';
                badge.title = 'Not listed in fade.json:sources';
            }
            li.append(badge);
        }
        if (name === activeName) li.classList.add('active');
        li.onclick = () => {
            // Binary files (.xnb, images, audio) go straight to the
            // preview panel — they aren't opened in Monaco. Text files
            // (incl. .fbasic, .fx, .json, .txt, .md) keep the existing
            // model-open path.
            if (isBinaryFileName(name)) {
                openBinaryPreview(name);
            } else {
                void openFile(workspace, name);
            }
        };
        fileListEl.append(li);
    }
}

// File-list right-click context menu. Source-membership actions (add /
// remove / reveal-in-manifest) live here alongside Rename + Delete so
// every file operation is reachable from one consistent gesture.
function showFileContextMenu(x: number, y: number, fileName: string) {
    closeAnyFileMenu();
    if (!projectOps) return;
    const menu = document.createElement('div');
    menu.className = 'source-badge-menu';
    menu.dataset.menu = 'file-context';
    const addItem = (label: string, handler: () => void) => {
        const item = document.createElement('button');
        item.className = 'source-badge-item';
        item.type = 'button';
        item.textContent = label;
        item.onclick = (e) => {
            e.stopPropagation();
            closeAnyFileMenu();
            handler();
        };
        menu.append(item);
    };
    const addSeparator = () => {
        const sep = document.createElement('div');
        sep.className = 'source-badge-sep';
        menu.append(sep);
    };

    const ops = projectOps;
    // Source-membership actions for .fbasic files. Mirrors what used to
    // live on the badge click, now bundled with rename/delete.
    if (/\.(fbasic|fb)$/i.test(fileName)) {
        const sources = currentProjectRef?.sources ?? [];
        const sourceIdx = sources.indexOf(fileName);
        if (sourceIdx >= 0) {
            addItem(`Go to fade.json (source #${sourceIdx + 1})`,
                () => ops.revealSourceInManifest(fileName));
            addItem('Remove from sources', () => ops.removeSource(fileName));
        } else {
            addItem('Add to sources (end)', () => ops.addSourceAt(fileName, 'end'));
            addItem('Add to sources (start)', () => ops.addSourceAt(fileName, 'start'));
        }
        addSeparator();
    }
    addItem(`Rename "${fileName}"…`, () => ops.renameFile(fileName));
    addItem(`Delete "${fileName}"`, () => ops.deleteFile(fileName));
    document.body.append(menu);
    menu.style.left = `${x}px`;
    menu.style.top = `${y}px`;
    // Flip into viewport if we'd overflow.
    const r = menu.getBoundingClientRect();
    if (r.right > window.innerWidth) menu.style.left = `${window.innerWidth - r.width - 4}px`;
    if (r.bottom > window.innerHeight) menu.style.top = `${window.innerHeight - r.height - 4}px`;
    setTimeout(() => {
        const onClick = (e: MouseEvent) => {
            if (!(e.target as HTMLElement).closest('.source-badge-menu')) closeAnyFileMenu();
        };
        const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') closeAnyFileMenu(); };
        document.addEventListener('mousedown', onClick, true);
        document.addEventListener('keydown', onKey, true);
        (menu as any).__cleanup = () => {
            document.removeEventListener('mousedown', onClick, true);
            document.removeEventListener('keydown', onKey, true);
        };
    }, 0);
}
function closeAnyFileMenu() {
    for (const m of document.querySelectorAll('[data-menu="file-context"]')) {
        (m as any).__cleanup?.();
        m.remove();
    }
}

function renderFileListSelection() {
    for (const li of Array.from(fileListEl.children) as HTMLElement[]) {
        li.classList.toggle('active', li.dataset.name === activeName);
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
    onHeartbeat?: (role: 'lsp' | 'vm', tick: number, t: number) => void;
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
    // Two workers boot in parallel:
    //   • lspWorker — set-document, hover, completion, semantic tokens,
    //     symbols, format, rename, references, definition, folding,
    //     list-tests. Never executes user code — stays responsive even
    //     while the VM is sync-blocked.
    //   • vmWorker — run, run-tests, and the entire debug-* surface.
    //     May get blocked by `wait ms` (Thread.Sleep) or other blocking
    //     commands; that's by design and isolated from the page.
    public lspWorker: Worker;
    public vmWorker: Worker;
    /** Back-compat alias — old code referenced runner.worker for raw access. */
    public get worker(): Worker { return this.lspWorker; }
    private opts: RunnerOpts;
    private nextId = 0;
    private pending = new Map<number, (result: any) => void>();
    private onDiagnostics?: (uri: string, diagnostics: Diagnostic[]) => void;
    // Shared-buffer protocol for synchronous prompt$ from worker → main thread.
    // promptSync[0] = sync slot (Atomics.wait), promptSync[1] = response length.
    private promptSab: SharedArrayBuffer | null = null;
    private promptSync: Int32Array | null = null;
    private promptBytes: Uint8Array | null = null;
    // Second SAB used to interrupt the vm-worker's `wait ms` early. The C#
    // call (StandardCommands.Wait) routes through JS's Atomics.wait on
    // this buffer; the page calls Atomics.notify on pause/terminate.
    private waitInterruptSab: SharedArrayBuffer | null = null;
    private waitInterruptView: Int32Array | null = null;
    onPromptRequest?: (msg: string) => Promise<string | null> | string | null;
    onDebugEvent?: (event: DebugEvent) => void;
    ready: Promise<void>;

    constructor(opts: RunnerOpts) {
        this.opts = opts;
        this.lspWorker = new Worker('/runtime/worker.js', { type: 'module' });
        this.vmWorker = new Worker('/runtime/worker.js', { type: 'module' });
        // First message each worker receives. The role flips behavior at
        // dispatch time (LSP ops are rejected on the vm worker and
        // vice-versa, surfacing as a `worker-misroute` event).
        this.lspWorker.postMessage({ type: 'configure', role: 'lsp' });
        this.vmWorker.postMessage({ type: 'configure', role: 'vm' });

        // Shared buffer for synchronous prompt$. Only the vm worker needs
        // it — prompt$ fires from user code, which only runs there.
        if (typeof SharedArrayBuffer !== 'undefined') {
            try {
                this.promptSab = new SharedArrayBuffer(4096);
                this.promptSync = new Int32Array(this.promptSab, 0, 2);
                this.promptBytes = new Uint8Array(this.promptSab, 8);
                this.vmWorker.postMessage({ type: 'prompt-sab', buffer: this.promptSab });
                // Second SAB: interrupt slot for `wait ms`. Single Int32;
                // page Atomics.notifies it to wake an in-flight wait early.
                this.waitInterruptSab = new SharedArrayBuffer(4);
                this.waitInterruptView = new Int32Array(this.waitInterruptSab);
                this.vmWorker.postMessage({
                    type: 'wait-interrupt-sab',
                    buffer: this.waitInterruptSab,
                });
            } catch (e) {
                console.warn('[fade] SharedArrayBuffer unavailable — prompt$ + wait-interrupt disabled:', e);
            }
        }

        this.ready = new Promise<void>((resolve, reject) => {
            // Resolve `ready` only after BOTH workers report ready. Each
            // hosts its own .NET runtime, so booting in parallel halves
            // the wall-clock startup vs. sequential.
            let lspReady = false, vmReady = false;
            const dispatch = (e: MessageEvent) => {
                const msg = e.data;
                if (msg.type === 'ready') {
                    if (msg.role === 'vm') vmReady = true; else lspReady = true;
                    if (lspReady && vmReady) resolve();
                    return;
                }
                this.handleWorkerMessage(msg, reject);
            };
            this.lspWorker.onmessage = dispatch;
            this.vmWorker.onmessage = dispatch;
            const handleErr = (label: string) => (e: ErrorEvent) =>
                reject(new Error(`${label} worker error: ${e.message}`));
            this.lspWorker.onerror = handleErr('lsp');
            this.vmWorker.onerror = handleErr('vm');
        });
    }

    // Dispatches a single message from either worker. The `ready` event
    // is intercepted before this runs (so it can resolve the boot promise),
    // and every other message is one of: heartbeat, print/alert, an
    // *-result reply matching a pending id, a debug event, a prompt
    // request, a streamed LSP diagnostic, a log line, a boot error, or a
    // misroute warning.
    private handleWorkerMessage(msg: any, reject: (err: Error) => void): void {
        if (msg.type === 'heartbeat') { this.opts.onHeartbeat?.(msg.role ?? 'lsp', msg.tick, msg.t); return; }
        if (msg.type === 'print') { this.opts.onPrint(msg.line); return; }
        if (msg.type === 'alert') { this.opts.onAlert(msg.msg); return; }
        if (msg.type === 'result') {
            const r = this.pending.get(msg.id);
            this.pending.delete(msg.id);
            if (r) r(msg.result);
            return;
        }
        if (msg.type === 'lsp-tokens-result')         { this.resolvePending(msg.id, msg.tokens); return; }
        if (msg.type === 'lsp-hover-result')          { this.resolvePending(msg.id, msg.hover); return; }
        if (msg.type === 'lsp-completion-result')     { this.resolvePending(msg.id, msg.items); return; }
        if (msg.type === 'lsp-signature-help-result') { this.resolvePending(msg.id, msg.sig); return; }
        if (msg.type === 'lsp-references-result')     { this.resolvePending(msg.id, msg.refs); return; }
        if (msg.type === 'lsp-definition-result')     { this.resolvePending(msg.id, msg.def); return; }
        if (msg.type === 'lsp-document-symbols-result') { this.resolvePending(msg.id, msg.symbols); return; }
        if (msg.type === 'lsp-folding-ranges-result') { this.resolvePending(msg.id, msg.ranges); return; }
        if (msg.type === 'lsp-format-result'
            || msg.type === 'lsp-format-range-result'
            || msg.type === 'lsp-format-on-type-result') {
            this.resolvePending(msg.id, msg.edits); return;
        }
        if (msg.type === 'lsp-rename-result')         { this.resolvePending(msg.id, msg.edit); return; }
        if (msg.type === 'set-project-type-result')   { this.resolvePending(msg.id, msg.projectType); return; }
        if (msg.type === 'list-tests-result')         { this.resolvePending(msg.id, msg.tests); return; }
        if (msg.type === 'list-command-docs-result')  { this.resolvePending(msg.id, msg.docs); return; }
        if (msg.type === 'run-tests-result')          { this.resolvePending(msg.id, msg.result); return; }
        if (msg.type === 'debug-start-result')        { this.resolvePending(msg.id, msg.result); return; }
        if (msg.type === 'debug-terminate-result'
            || msg.type === 'debug-set-breakpoints-result'
            || msg.type === 'debug-step-result'
            || msg.type === 'debug-continue-result'
            || msg.type === 'debug-pause-result') {
            this.resolvePending(msg.id, true); return;
        }
        if (msg.type === 'debug-stack-frames-result') { this.resolvePending(msg.id, msg.frames); return; }
        if (msg.type === 'debug-scopes-result'
            || msg.type === 'debug-variable-expansion-result') {
            this.resolvePending(msg.id, msg.scopes); return;
        }
        if (msg.type === 'debug-eval-result'
            || msg.type === 'debug-repl-result'
            || msg.type === 'debug-set-variable-result') {
            this.resolvePending(msg.id, msg.result); return;
        }
        if (msg.type === 'debug-event') {
            if (this.onDebugEvent) this.onDebugEvent(msg.event);
            return;
        }
        if (msg.type === 'prompt-request') { this.handlePromptRequest(msg.msg); return; }
        if (msg.type === 'lsp-diagnostics') {
            if (this.onDiagnostics) {
                const parsed: Diagnostic[] = JSON.parse(msg.diagnostics);
                this.onDiagnostics(msg.uri, parsed);
            }
            return;
        }
        if (msg.type === 'log') { console.log('[runtime worker]', msg.message); return; }
        if (msg.type === 'boot-error') { reject(new Error(msg.message)); return; }
        if (msg.type === 'worker-misroute') {
            console.warn('[fade] worker misroute', msg);
            // Resolve any pending id with null so callers don't hang.
            if (msg.id != null) this.resolvePending(msg.id, null);
            return;
        }
    }

    private resolvePending(id: number, value: any): void {
        const r = this.pending.get(id);
        this.pending.delete(id);
        if (r) r(value);
    }

    // Wake an in-flight `wait ms` in the vm worker. `kind` tells the C#
    // side why we're waking it:
    //   1 = pause request — WaitImpl enqueues REQUEST_PAUSE so the VM
    //       stops on the next instruction step.
    //   2 = terminate request — WaitImpl flips the session's requestedExit
    //       so StartDebugging unwinds cleanly (no exception path).
    //   3 = wake-only — used for breakpoint updates and other page→VM
    //       state changes. WaitImpl returns without doing anything else;
    //       the wake just lets DebugTick yield sooner so the worker's
    //       JS event loop can drain the queued message (the breakpoint
    //       update itself) and have the next tick see the new state.
    interruptWait(kind: 1 | 2 | 3): void {
        if (!this.waitInterruptView) return;
        Atomics.store(this.waitInterruptView, 0, kind);
        Atomics.notify(this.waitInterruptView, 0);
    }

    run(source: string): Promise<string> {
        const id = ++this.nextId;
        return new Promise<string>((resolve) => {
            this.pending.set(id, resolve);
            this.vmWorker.postMessage({ type: 'run', id, source });
        });
    }

    setDocument(uri: string, text: string) {
        this.worker.postMessage({ type: 'lsp-set', uri, text });
    }

    // Switch both workers' LSP CommandCollection to match the active
    // fade.json type. Both workers run worker.js so we fire the message
    // to each — LSP worker needs the right commands for tokens/hover/
    // diagnostics, VM worker matters once Run/Tests for that type land
    // there (today monogame Run/Tests go through WebRuntime.MonoGame
    // directly, so the vm-worker call is a no-op for monogame but
    // harmless and keeps the two workers in sync).
    async setProjectType(projectType: string): Promise<void> {
        const post = (w: Worker) => new Promise<void>((resolve) => {
            const id = ++this.nextId;
            this.pending.set(id, () => resolve());
            w.postMessage({ type: 'set-project-type', id, projectType });
        });
        await Promise.all([post(this.lspWorker), post(this.vmWorker)]);
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

    // Fetches a flat list of every loaded command with its hover-style
    // markdown. The Help tab calls this once on bootstrap (and again on
    // any future command-set change). Lives on the LSP worker — pure
    // metadata read, doesn't touch the VM.
    async listCommandDocs(): Promise<CommandDocEntry[]> {
        const id = ++this.nextId;
        return new Promise<CommandDocEntry[]>((resolve) => {
            this.pending.set(id, (json: string) => {
                try {
                    const parsed = JSON.parse(json);
                    resolve(Array.isArray(parsed) ? parsed : []);
                } catch { resolve([]); }
            });
            this.lspWorker.postMessage({ type: 'list-command-docs', id });
        });
    }

    async runTests(source: string, testName?: string): Promise<TestRunResult> {
        const id = ++this.nextId;
        return new Promise<TestRunResult>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); }
                catch { resolve({ passed: 0, failed: 0, duration: 0, results: [], printed: '', error: 'parse failed' }); }
            });
            this.vmWorker.postMessage({ type: 'run-tests', id, source, testName: testName || '' });
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
            this.vmWorker.postMessage({ type: 'debug-start', id, source });
        });
    }
    async debugStartTest(source: string, testName: string): Promise<DebugStartResult> {
        const id = ++this.nextId;
        return new Promise<DebugStartResult>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); }
                catch { resolve({ ok: false, error: 'parse failed', statementLines: [] }); }
            });
            this.vmWorker.postMessage({ type: 'debug-start-test', id, source, testName });
        });
    }
    debugTerminate(): Promise<boolean> {
        this.interruptWait(2);
        return this.simpleDebugCall('debug-terminate');
    }
    debugContinue(): Promise<boolean> { return this.simpleDebugCall('debug-continue'); }
    debugPause(): Promise<boolean> {
        // Wake any in-flight `wait ms` early AND tell C# this was a pause
        // request — WaitImpl will enqueue REQUEST_PAUSE synchronously so
        // the next VM instruction check pauses, instead of waiting for
        // the worker JS event loop to drain the debug-pause postMessage
        // (which can take up to a full DebugTick budget).
        this.interruptWait(1);
        return this.simpleDebugCall('debug-pause');
    }
    debugStep(kind: 'over' | 'in' | 'out'): Promise<boolean> {
        const id = ++this.nextId;
        return new Promise<boolean>((resolve) => {
            this.pending.set(id, () => resolve(true));
            this.vmWorker.postMessage({ type: 'debug-step', id, kind });
        });
    }
    debugSetBreakpoints(breakpoints: BreakpointRequest[]): Promise<boolean> {
        const id = ++this.nextId;
        // Wake any in-flight `wait ms` so the worker's JS event loop yields
        // and picks up this breakpoint update without waiting out the rest
        // of the sleep. Without this, adding/removing a breakpoint mid-
        // `wait ms(3000)` only takes effect on the next loop iteration.
        this.interruptWait(3);
        return new Promise<boolean>((resolve) => {
            this.pending.set(id, () => resolve(true));
            this.vmWorker.postMessage({
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
            this.vmWorker.postMessage({ type: 'debug-stack-frames', id });
        });
    }
    debugScopes(frameId: number): Promise<DebugScopesResult> {
        const id = ++this.nextId;
        return new Promise<DebugScopesResult>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); } catch { resolve({ scopes: [] }); }
            });
            this.vmWorker.postMessage({ type: 'debug-scopes', id, frameId });
        });
    }
    debugExpandVariable(variableId: number): Promise<DebugScopesResult> {
        const id = ++this.nextId;
        return new Promise<DebugScopesResult>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); } catch { resolve({ scopes: [] }); }
            });
            this.vmWorker.postMessage({ type: 'debug-variable-expansion', id, variableId });
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
            this.vmWorker.postMessage({ type, id });
        });
    }
    private debugTextCall(type: string, payload: object): Promise<DebugEvalResult | null> {
        const id = ++this.nextId;
        return new Promise<DebugEvalResult | null>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); } catch { resolve(null); }
            });
            this.vmWorker.postMessage({ type, id, ...payload });
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
// One entry per uniquely-named command, shape matches what
// `FadeBridge.ListCommandDocs()` emits in [WebRuntime/FadeBridge.cs].
// The markdown field is the same text the hover provider renders; the
// Help tab reuses it verbatim so both surfaces stay in sync.
interface CommandDocEntry {
    name: string;
    signature: string;
    group: string;
    markdown: string;
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
    registerExtraLanguages();
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
            ...extraThemeRules(),
        ],
        colors: {},
    });
    monaco.editor.setTheme('fade-dark');

    statusEl.textContent = 'Booting Fade runtime worker…';

    // Heartbeat indicators. Each worker (lsp + vm) posts a beat every
    // 500ms; the corresponding dot pulses while alive and turns red when
    // we haven't heard from it in >1.2s. The vm dot going red while the
    // lsp dot stays green is the signature of a Thread.Sleep / `wait ms`
    // blocking the VM worker — the page itself stays responsive because
    // the lsp worker keeps draining messages.
    const heartbeatEl = document.getElementById('worker-heartbeat')!;
    type BeatState = { lastAt: number; tick: number };
    const beats: { lsp: BeatState; vm: BeatState } = {
        lsp: { lastAt: Date.now(), tick: 0 },
        vm:  { lastAt: Date.now(), tick: 0 },
    };
    // Render two dots inside the heartbeat span — lsp on the left, vm
    // on the right. Tooltip carries the freeze hint for the busy state.
    heartbeatEl.innerHTML = `
        <span class="hb-dot" data-role="lsp" data-state="off"></span>
        <span class="hb-dot" data-role="vm"  data-state="off"></span>
    `;
    const dotLsp = heartbeatEl.querySelector<HTMLElement>('.hb-dot[data-role="lsp"]')!;
    const dotVm  = heartbeatEl.querySelector<HTMLElement>('.hb-dot[data-role="vm"]')!;
    function paintHeartbeat() {
        for (const [role, el] of [['lsp', dotLsp], ['vm', dotVm]] as const) {
            const b = beats[role];
            const dt = Date.now() - b.lastAt;
            el.dataset.state = dt > 1200 ? 'busy' : (b.tick % 2 === 0 ? 'on' : 'off');
            el.title = dt > 1200
                ? `${role} worker is busy — last beat ${(dt / 1000).toFixed(1)}s ago.`
                    + (role === 'vm'
                        ? ' Likely Thread.Sleep / wait ms inside user code.'
                        : '')
                : `${role} worker alive — beat ${b.tick}`;
        }
    }
    setInterval(paintHeartbeat, 250);

    const runner = new FadeRunner({
        onPrint: (line) => appendOutputLine(line),
        onAlert: (msg) => window.alert(msg),
        onHeartbeat: (role, tick, t) => {
            beats[role].tick = tick;
            beats[role].lastAt = t;
            paintHeartbeat();
        },
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

    // openFile (module scope) calls into this on every tab activation so
    // preloaded-but-never-displayed models get tokenized before they're
    // first shown. Fire-and-forget — applySemanticTokens deduplicates via
    // deltaDecorations so duplicate calls are cheap.
    (window as any).__fadeRefreshSemanticTokens = (model: monaco.editor.ITextModel) => {
        void applySemanticTokens(model);
    };

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
    // Hover markdown is rendered with isTrusted=true so `command:` URIs
    // are clickable. We register a single `fade.openHelp` command that
    // takes the command name as its argument and routes through the
    // Help controller. See the "View in Help" link appended below.
    monaco.editor.registerCommand('fade.openHelp', (_accessor: unknown, name?: string) => {
        if (typeof name === 'string' && name) {
            (window as any).__fadeHelp?.openCommand?.(name);
        }
    });

    monaco.languages.registerHoverProvider('fade', {
        provideHover: async (model, position) => {
            const uri = model.uri.toString();
            const word = model.getWordAtPosition(position);

            // Try debug-eval first when paused.
            const contents: { value: string; isTrusted?: boolean }[] = [];
            let range: monaco.IRange | undefined;
            if (debugSessionActive && debugPaused && word && activeFrameId != null) {
                try {
                    const evalResult = await dbg.eval(activeFrameId, word.word);
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
                let value = hover.contents;
                // BuildCommandMarkdown emits `### commandname\n...` as
                // the first non-blank line for command hovers. When we
                // see that shape, append a deep-link to the Help tab
                // (markdown link with a Monaco command URI). Trusted
                // markdown lets Monaco invoke our registered command.
                const m = /^\s*###\s+([^\n]+)/.exec(value);
                if (m) {
                    const cmdName = m[1].trim();
                    const args = encodeURIComponent(JSON.stringify(cmdName));
                    value = value + `\n\n[View in Help →](command:fade.openHelp?${args})`;
                    contents.push({ value, isTrusted: true });
                } else {
                    contents.push({ value });
                }
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
        // Refresh semantic-token decorations on EVERY model with this URI,
        // not just the editor's active one. Decorations are stored on the
        // model itself, so doing this for inactive tabs means switching to
        // them later shows them already highlighted (no edit needed).
        for (const m of allModels) {
            void applySemanticTokens(m);
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

        // fade.json schema problems get their own header row so the user
        // recognizes them as project-level (not source-file) issues.
        for (const e of currentProjectErrors) {
            total++;
            const li = document.createElement('li');
            li.className = 'problem-item';
            const icon = document.createElement('vscode-icon');
            icon.setAttribute('name', e.severity);
            icon.className = e.severity;
            const msg = document.createElement('span');
            msg.className = 'problem-message';
            msg.textContent = e.message;
            if (e.path) {
                const code = document.createElement('span');
                code.className = 'code';
                code.textContent = e.path;
                msg.append(code);
            }
            const loc = document.createElement('span');
            loc.className = 'problem-location';
            const where = e.range
                ? `${FADE_JSON_NAME}:${e.range.startLineNumber}:${e.range.startColumn}`
                : FADE_JSON_NAME;
            loc.textContent = where;
            li.append(icon, msg, loc);
            li.onclick = async () => {
                try {
                    await openFile(workspace, FADE_JSON_NAME);
                    if (e.range && editor) {
                        editor.revealLineInCenter(e.range.startLineNumber, monaco.editor.ScrollType.Smooth);
                        editor.setPosition({ lineNumber: e.range.startLineNumber, column: e.range.startColumn });
                        editor.focus();
                    }
                } catch { /* ignore */ }
            };
            problemsList.append(li);
        }

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
    // Inline test-log region was replaced by streaming directly into the
    // Output panel. The HTML for the old region is gone; we keep `appendTestLog`
    // as a thin wrapper so call sites stay readable + can still highlight
    // failure frames as click-to-jump lines.

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

    // Test-run output streams directly into the Output panel now. Lines
    // tagged with a frame become click-to-jump links to the offending
    // source location, same as the failure-row link in the Tests panel.
    function appendTestLog(text: string, cls?: 'pass' | 'fail' | 'dim', frame?: FailureFrame) {
        if (!text) return;
        const kind: OutputKind = cls === 'pass' ? 'pass'
            : cls === 'fail' ? 'error'
            : cls === 'dim' ? 'dim'
            : 'plain';
        if (frame) {
            const ln = (frame.lineNumber | 0) + 1;
            const col = ((frame.charNumber | 0) + 1) || 1;
            appendOutputLine(text, kind, () => jumpEditorTo(ln, col));
        } else {
            appendOutputLine(text, kind);
        }
    }

    function getActiveSource(): string {
        const activeTab = activeName ? tabs.get(activeName) : null;
        return activeTab?.model.getValue() ?? '';
    }

    const projectNameEl = document.getElementById('project-name')!;
    function setProjectStatus(label: string) {
        projectNameEl.textContent = label;
        projectNameEl.hidden = !label;
    }

    // ─── Project (fade.json) state ───────────────────────────────────────
    // Re-read whenever fade.json's OPFS contents OR its open model changes.
    // The compile/run/test/debug paths read from `currentProject` to build
    // their source string instead of just the active tab; ordering follows
    // fade.json sources[] exactly, matching the native SDK behavior.
    let currentProject: FadeProject | null = null;
    let currentProjectErrors: FadeConfigError[] = [];
    // Last project type we told the worker LSP about. Updated in lock-step
    // with the call so we only fire setProjectType on actual transitions —
    // refreshFadeProject runs on every fade.json edit, but most of those
    // edits don't change the type.
    let lastWorkerProjectType: string | null = null;

    // Set after the Help panel is mounted (further down in bootstrap). The
    // project-type-change branch below uses this to re-fetch the command
    // doc list so Help reflects whichever CommandCollection the LSP just
    // swapped to. Null while bootstrap is still wiring things up — the
    // initial population happens unconditionally after helpCtl mounts.
    let refreshHelpEntriesFromWorker: (() => void) | null = null;

    // Live-content lookup: prefer Monaco's model (so dirty edits compile
    // even before the save timer fires), fall back to the OPFS-persisted
    // copy. Models exist for every workspace file because bootstrap
    // preloads them; we still tolerate a missing model gracefully.
    async function readFile(name: string): Promise<string> {
        const uri = monaco.Uri.file(`/workspace/${name}`);
        const model = monaco.editor.getModel(uri);
        if (model) return model.getValue();
        const tab = tabs.get(name);
        if (tab) return tab.model.getValue();
        try { return await workspace.read(name); }
        catch { return ''; }
    }

    async function refreshFadeProject(): Promise<void> {
        let text = '';
        try {
            text = await readFile(FADE_JSON_NAME);
        } catch {
            currentProject = null;
            currentProjectErrors = [{
                path: '', severity: 'error',
                message: 'fade.json is missing from this project.',
            }];
            applyFadeJsonMarkers(text, currentProjectErrors);
            renderProblems();
            return;
        }
        const r = parseFadeProject(text);
        currentProject = r.ok ? (r.project ?? null) : null;

        // Sync the worker LSP's CommandCollection with fade.json's type so
        // syntax highlighting / hover / signature help reflect the right
        // command surface ('web' → WebCommands; 'monogame' → FadeMonoGameCommands).
        // Idempotent inside setProjectType, but we still gate here to avoid
        // re-tokenizing every model on every fade.json refresh.
        const wantedType = currentProject?.type ?? 'web';
        if (wantedType !== lastWorkerProjectType) {
            lastWorkerProjectType = wantedType;
            try {
                await runner.setProjectType(wantedType);
                // Re-push every open fbasic model so tokens + diagnostics
                // recompute against the new command set. The next edit
                // would do this anyway, but we'd rather not leave stale
                // highlights/squiggles sitting until then.
                //
                // FILTER by language: fade.json + other non-fade files must
                // NOT go through the Fade LSP — the parser would treat the
                // JSON's `$schema` as a substitution and flag [0158] errors.
                // Also evict any stale owner='fade' markers from non-fade
                // models that an earlier (buggy) push left behind, so the
                // squiggles disappear on the next paint instead of waiting
                // for the user to re-edit. Self-healing if a future code
                // path mis-pushes a non-fade model again.
                for (const model of monaco.editor.getModels()) {
                    if (model.getLanguageId() === 'fade') {
                        runner.setDocument(model.uri.toString(), model.getValue());
                    } else {
                        monaco.editor.setModelMarkers(model, 'fade', []);
                        diagnosticsByUri.delete(model.uri.toString());
                    }
                }
                renderProblems();
                // The LSP worker just swapped its CommandCollection (web →
                // monogame or vice-versa). Re-fetch the command-doc list
                // so the Help tab reflects the new surface. Null-safe for
                // the first refresh that runs before helpCtl is mounted.
                refreshHelpEntriesFromWorker?.();
            } catch (e) {
                console.warn('[fade] setProjectType failed', e);
            }
        }

        // Attach source ranges to schema errors so the renderer + Monaco
        // markers can highlight the offending key/value precisely.
        const paths = locateJsonPaths(text);
        const decorate = (e: FadeConfigError): FadeConfigError => {
            if (!e.path) return e;
            const rng = paths.get(e.path);
            if (!rng) return e;
            return { ...e, range: offsetsToLineCol(text, rng.start, rng.end) };
        };
        const errors: FadeConfigError[] = r.errors.map(decorate);

        // Cross-check against the actual workspace: every entry in
        // `sources` must point at a real file. We intentionally do NOT
        // warn about unlisted .fbasic files in the workspace — keeping
        // extra source files lying around (linked/unlinked as you iterate)
        // is a normal workflow, not a misconfiguration. The dash badge
        // in the file list already surfaces the "not part of the build"
        // signal without an inline diagnostic.
        // Sub-folder paths (containing "/") are skipped — OpfsWorkspace
        // is flat for now, so a "/" path is by definition unresolvable
        // and would always false-positive.
        if (currentProject) {
            try {
                const workspaceFiles = await workspace.list();
                const fileSet = new Set(workspaceFiles);
                currentProject.sources.forEach((src, idx) => {
                    if (src.includes('/')) return;
                    if (!fileSet.has(src)) {
                        errors.push(decorate({
                            path: `sources[${idx}]`,
                            severity: 'error',
                            message: `Source "${src}" not found in this project. Create the file or remove the entry from fade.json.`,
                        }));
                    }
                });
            } catch (e) {
                console.warn('[fade] sources cross-check failed', e);
            }
        }

        currentProjectErrors = errors;
        applyFadeJsonMarkers(text, currentProjectErrors);
        // Idempotent guard installs the read-only protection for fade.json's
        // `$schema` line. Safe to call on every refresh — the WeakSet keeps
        // it from re-binding listeners.
        protectFadeJsonSchemaLine();
        renderProblems();
        // Republish for module-scope renderers (file list badges) and
        // re-render so the source-order indicators update immediately.
        currentProjectRef = currentProject;
        renderFileList(workspace).catch(() => { /* ignore */ });
        // Title bar reflects the resolved project name.
        if (currentProject?.name) {
            const hasErrors = currentProjectErrors.some((e) => e.severity === 'error');
            setProjectStatus(hasErrors ? `${currentProject.name} (fade.json invalid)` : currentProject.name);
        } else {
            setProjectStatus(workspace.currentProject() + ' (fade.json invalid)');
        }
    }

    // Mutations triggered from the file-list source badges. They edit
    // the live Monaco model for fade.json (rather than the OPFS copy
    // directly) so the polling loop's refreshFadeProject + LSP push
    // chain reacts naturally; the model's saveTimer persists to OPFS.
    async function mutateManifest(
        mutate: (project: FadeProject) => FadeProject | null,
    ): Promise<void> {
        const uri = monaco.Uri.file(`/workspace/${FADE_JSON_NAME}`);
        const model = monaco.editor.getModel(uri);
        if (!model) return;
        const current = parseFadeProject(model.getValue());
        if (!current.ok || !current.project) {
            // We don't try to fix invalid manifests; the user is best
            // positioned to resolve schema problems first.
            return;
        }
        const next = mutate(current.project);
        if (!next) return;
        const newText = stringifyFadeProject(next);
        if (newText === model.getValue()) return;
        model.applyEdits([{ range: model.getFullModelRange(), text: newText }]);
        // Immediate state refresh so the file list redraws without waiting
        // on the 250ms polling loop.
        await refreshFadeProject();
    }

    projectOps = {
        addSourceAt: async (name, position) => {
            await mutateManifest((p) => {
                if (p.sources.includes(name)) return null; // already listed
                const updated = position === 'start'
                    ? [name, ...p.sources]
                    : [...p.sources, name];
                return { ...p, sources: updated };
            });
        },
        removeSource: async (name) => {
            await mutateManifest((p) => {
                if (!p.sources.includes(name)) return null;
                return { ...p, sources: p.sources.filter((s) => s !== name) };
            });
        },
        revealSourceInManifest: async (name) => {
            try { await openFile(workspace, FADE_JSON_NAME); } catch { /* ignore */ }
            const uri = monaco.Uri.file(`/workspace/${FADE_JSON_NAME}`);
            const model = monaco.editor.getModel(uri);
            if (!model || !editor) return;
            const text = model.getValue();
            const ranges = locateJsonPaths(text);
            // Walk indices until we find the entry that contains the
            // file name (string compare against the located value).
            const proj = currentProject;
            if (!proj) return;
            const idx = proj.sources.indexOf(name);
            if (idx < 0) return;
            const r = ranges.get(`sources[${idx}]`);
            if (!r) return;
            const lc = offsetsToLineCol(text, r.start, r.end);
            editor.revealLineInCenter(lc.startLineNumber, monaco.editor.ScrollType.Smooth);
            editor.setPosition({ lineNumber: lc.startLineNumber, column: lc.startColumn });
            editor.focus();
        },
        renameFile: async (oldName) => {
            if (oldName === FADE_JSON_NAME) {
                alert('fade.json is the project manifest and cannot be renamed.');
                return;
            }
            const newName = prompt(`Rename "${oldName}" to:`, oldName);
            if (!newName || newName === oldName) return;
            try {
                await workspace.rename(oldName, newName);
            } catch (e: any) {
                alert('Rename failed: ' + (e?.message ?? e));
                return;
            }
            // Monaco models are immutable on URI — swap by disposing the
            // old model and creating a new one with the new URI. Any
            // decorations + markers reattach via the polling loop's next
            // LSP push.
            const oldUri = monaco.Uri.file(`/workspace/${oldName}`);
            const newUri = monaco.Uri.file(`/workspace/${newName}`);
            const oldModel = monaco.editor.getModel(oldUri);
            const text = oldModel ? oldModel.getValue() : await workspace.read(newName);
            const wasActive = activeName === oldName;
            const wasInEditor = editor?.getModel() === oldModel;
            // Drop old model + virtualFs registration.
            if (oldModel) oldModel.dispose();
            registeredVirtualFsUris.delete(oldUri.toString());
            // Recreate at new URI.
            const newModel = monaco.editor.createModel(text, languageFor(newName), newUri);
            // Move tab entry if open.
            const oldTab = tabs.get(oldName);
            if (oldTab) {
                tabs.delete(oldName);
                const newTab: Tab = { name: newName, model: newModel, dirty: false };
                newTab.model.onDidChangeContent(() => {
                    newTab.dirty = true;
                    clearTimeout(newTab.saveTimer);
                    newTab.saveTimer = window.setTimeout(async () => {
                        try {
                            await workspace.write(newTab.name, newTab.model.getValue());
                            newTab.dirty = false;
                            renderTabs();
                        } catch (e) {
                            console.error('[fade] save failed for', newTab.name, e);
                        }
                    }, 600);
                    renderTabs();
                });
                tabs.set(newName, newTab);
                if (wasActive) activeName = newName;
                if (wasInEditor && editor) editor.setModel(newTab.model);
            }
            // If the renamed file was listed in fade.json:sources, rewrite
            // the manifest so the build keeps working. Preserves position.
            await mutateManifest((p) => {
                const idx = p.sources.indexOf(oldName);
                if (idx < 0) return null;
                const updated = [...p.sources];
                updated[idx] = newName;
                return { ...p, sources: updated };
            });
            await refreshFadeProject();
            renderTabs();
            await renderFileList(workspace);
        },
        deleteFile: async (name) => {
            if (name === FADE_JSON_NAME) {
                alert('fade.json is the project manifest and cannot be deleted.');
                return;
            }
            if (!confirm(`Delete "${name}"? This cannot be undone.`)) return;
            // Close any open tab for this file first.
            if (tabs.has(name)) closeTab(name);
            // Dispose the Monaco model so it doesn't linger after the file
            // is gone — otherwise the polling loop keeps pushing stale
            // content to LSP under a now-orphan URI.
            const uri = monaco.Uri.file(`/workspace/${name}`);
            const model = monaco.editor.getModel(uri);
            if (model) model.dispose();
            registeredVirtualFsUris.delete(uri.toString());
            try {
                await workspace.delete(name);
            } catch (e: any) {
                alert('Delete failed: ' + (e?.message ?? e));
                return;
            }
            // If the deleted file was a listed source, remove it from
            // fade.json so we don't trip the missing-source error.
            await mutateManifest((p) => {
                if (!p.sources.includes(name)) return null;
                return { ...p, sources: p.sources.filter((s) => s !== name) };
            });
            await refreshFadeProject();
            renderTabs();
            await renderFileList(workspace);
        },
    };

    // Keep the `$schema` line of fade.json read-only. The page emits this
    // line via stringifyFadeProject so external editors can resolve the
    // JSON Schema; if the user edits it, they likely break that linkage
    // by accident. We protect by:
    //  - snapshotting the current `$schema` line whenever fade.json's
    //    model is clean,
    //  - on every content change, checking whether any edit overlapped
    //    that line, and if so, splicing the snapshot back in (with a
    //    reentry guard so our revert doesn't trigger itself).
    // Also drops a non-editable read-only decoration so the line visibly
    // hints "you can't edit this".
    const schemaGuards = new WeakSet<monaco.editor.ITextModel>();
    function protectFadeJsonSchemaLine() {
        const uri = monaco.Uri.file(`/workspace/${FADE_JSON_NAME}`);
        const m = monaco.editor.getModel(uri);
        if (!m) return;
        if (schemaGuards.has(m)) return;
        schemaGuards.add(m);
        // Pin to a non-null local so the closures below don't trip TS's
        // possibly-null analysis on the field.
        const model: monaco.editor.ITextModel = m;

        let suppressing = false;
        let protectedLine = -1;
        let protectedText = '';
        let decorationIds: string[] = [];

        function findSchemaLine(): number {
            for (let i = 1; i <= model.getLineCount(); i++) {
                if (/"\$schema"\s*:/.test(model.getLineContent(i))) return i;
            }
            return -1;
        }
        function snapshot() {
            protectedLine = findSchemaLine();
            protectedText = protectedLine > 0 ? model.getLineContent(protectedLine) : '';
            // Visual cue — non-editable shading + a tooltip.
            if (protectedLine > 0) {
                const maxCol = model.getLineMaxColumn(protectedLine);
                decorationIds = model.deltaDecorations(decorationIds, [{
                    range: new monaco.Range(protectedLine, 1, protectedLine, maxCol),
                    options: {
                        inlineClassName: 'fade-readonly-line',
                        isWholeLine: true,
                        hoverMessage: { value: 'Read-only — links fade.json to the published JSON Schema.' },
                    },
                }]);
            } else if (decorationIds.length) {
                decorationIds = model.deltaDecorations(decorationIds, []);
            }
        }
        snapshot();

        model.onDidChangeContent((e) => {
            if (suppressing) return;
            if (protectedLine < 0) { snapshot(); return; }
            // A whole-document replacement (e.g. formatter, paste-over-all,
            // or programmatic set-value from a probe) is intentional. Let
            // it through, then re-snapshot so the new schema line — if any
            // — gets locked again.
            const wholeReplace = e.changes.length === 1
                && e.changes[0].range.startLineNumber === 1
                && e.changes[0].range.startColumn === 1;
            if (wholeReplace) { snapshot(); return; }
            const touched = e.changes.some((c) =>
                c.range.startLineNumber <= protectedLine
                && c.range.endLineNumber >= protectedLine,
            );
            if (!touched) { snapshot(); return; }
            suppressing = true;
            try {
                // Restore the protected line's exact original text. If the
                // change shifted the line number (insert/remove above), the
                // line may now be off — re-find by content match before
                // patching.
                const currentLineNow = findSchemaLine();
                const target = currentLineNow > 0 ? currentLineNow : protectedLine;
                const maxLines = model.getLineCount();
                if (target > maxLines) {
                    // The user removed the line outright. Re-insert it.
                    model.applyEdits([{
                        range: new monaco.Range(maxLines, model.getLineMaxColumn(maxLines), maxLines, model.getLineMaxColumn(maxLines)),
                        text: '\n' + protectedText,
                    }]);
                } else {
                    const lineLen = model.getLineMaxColumn(target);
                    const live = model.getLineContent(target);
                    if (live !== protectedText) {
                        model.applyEdits([{
                            range: new monaco.Range(target, 1, target, lineLen),
                            text: protectedText,
                        }]);
                    }
                }
            } finally {
                suppressing = false;
            }
            // After the revert, the schema line position may have settled
            // somewhere different; resnapshot to track that.
            snapshot();
        });
    }

    // Push schema errors as Monaco markers on the fade.json model so the
    // user sees red/yellow squiggles inline. Owner string scopes them so
    // they don't fight with LSP-emitted markers on other models.
    function applyFadeJsonMarkers(text: string, errors: FadeConfigError[]) {
        const uri = monaco.Uri.file(`/workspace/${FADE_JSON_NAME}`);
        const model = monaco.editor.getModel(uri);
        if (!model) return;
        // If we have no useful text, clear and bail.
        if (!text) {
            monaco.editor.setModelMarkers(model, 'fade-config', []);
            return;
        }
        const markers: monaco.editor.IMarkerData[] = [];
        for (const e of errors) {
            const r = e.range ?? {
                // Whole-document fallback for root-level errors (bad JSON).
                startLineNumber: 1, startColumn: 1,
                endLineNumber: Math.max(1, model.getLineCount()),
                endColumn: Math.max(1, model.getLineMaxColumn(model.getLineCount())),
            };
            markers.push({
                severity: e.severity === 'warning'
                    ? monaco.MarkerSeverity.Warning
                    : monaco.MarkerSeverity.Error,
                message: e.message,
                source: 'fade.json',
                startLineNumber: r.startLineNumber,
                startColumn: r.startColumn,
                endLineNumber: r.endLineNumber,
                endColumn: r.endColumn,
            });
        }
        monaco.editor.setModelMarkers(model, 'fade-config', markers);
    }

    // Concatenate the project's .fbasic sources in fade.json order. Falls
    // back to the active tab's contents when fade.json is missing/invalid
    // so the playground still runs *something* for new users mid-edit.
    async function getProjectSource(): Promise<string> {
        if (!currentProject) return getActiveSource();
        const parts: string[] = [];
        for (const name of currentProject.sources) {
            const text = await readFile(name);
            parts.push(text);
        }
        return parts.join('\n');
    }

    async function refreshTests() {
        const source = await getProjectSource();
        if (!source) {
            testEntries = [];
            renderTests();
            return;
        }
        // monogame compiles against FadeMonoGameCommands+StandardCommands;
        // the worker's command surface may or may not match, depending on
        // whether the LSP has swapped command sets. Route through the
        // canvas-side bridge for monogame so the test manifest reflects
        // the exact compile env the run will use.
        const list = currentProject?.type === 'monogame'
            ? await monoGameHost.listTests(source)
            : await runner.listTests(source);
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

    type OutputKind = 'plain' | 'dim' | 'error' | 'warning' | 'info' | 'pass';

    // First write clears the "(not yet run)" placeholder.
    let outputPrimed = false;
    function primeOutput() {
        if (outputPrimed) return;
        outputEl.innerHTML = '';
        outputPrimed = true;
    }
    function clearOutput() {
        outputEl.innerHTML = '';
        outputPrimed = true;
    }
    function appendOutputLine(text: string, kind: OutputKind = 'plain', onClick?: () => void) {
        if (text == null) return;
        primeOutput();
        // Split on newlines so each rendered <div> is one logical line — keeps
        // colored lines distinct and lets long text wrap inside the block.
        const lines = String(text).split('\n');
        if (lines.length > 1 && lines[lines.length - 1] === '') lines.pop();
        for (const line of lines) {
            const div = document.createElement('div');
            div.className = 'output-line' + (kind !== 'plain' ? ' ' + kind : '');
            div.textContent = line;
            if (onClick) {
                div.classList.add('clickable');
                div.onclick = onClick;
            }
            outputEl.append(div);
        }
        outputEl.scrollTop = outputEl.scrollHeight;
    }
    const outputClearBtn = document.getElementById('output-clear');
    outputClearBtn?.addEventListener('click', clearOutput);

    function setTestsBusy(busy: boolean) {
        testsRunAllBtn.disabled = busy;
        testsRefreshBtn.disabled = busy;
    }

    // Adapt the canvas-side test-run envelope to the worker-side shape
    // applyResult/applyResultAll consume. The canvas-side result is missing
    // the `printed` aggregator (test-print streams are best-effort on the
    // canvas) and may omit `duration` on the all-tests path; fill defaults
    // so the consumer doesn't need to branch.
    function mgToTestRunResult(mg: import('./monogame-host').MonoGameRunTestsResult): TestRunResult {
        return {
            passed: mg.passed,
            failed: mg.failed,
            duration: mg.duration ?? 0,
            // Reuse the per-test result shape — fields align 1:1 modulo
            // failureFrames/failureInstructionIndex which the canvas
            // bridge doesn't populate yet (FailureFrames support is a
            // worker-only thing today; logic-test failures still surface
            // via failureMessage + failureSourceText).
            results: (mg.results || []).map((r) => ({
                name: r.name,
                passed: r.passed,
                duration: r.duration,
                failureMessage: r.failureMessage,
                failureReason: r.failureReason,
                failureSourceText: r.failureSourceText,
            })),
            printed: '',
            error: mg.error,
        };
    }

    async function runSingleTest(name: string) {
        const source = await getProjectSource();
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
        clearOutput();
        revealPanel('output');
        appendTestLog(`▶ ${name}`, 'dim');
        try {
            // monogame tests run on the canvas runtime so MonoGame
            // commands resolve against the live Game1; web tests run
            // through the LSP/VM worker. Same shape returned either way,
            // modulo `printed` + `duration` which the canvas bridge
            // doesn't include — fill them so applyResult can be agnostic.
            //
            // For monogame: push assets first. The test's bytecode may
            // call `texture`/`load sfx clip`/etc. which look the asset
            // up via Game1.Content (BrowserContentManager). Without this
            // the asset cache is empty and the asset commands log
            // "asset 'X' is not registered" — exactly the failure mode
            // the user reported before the host-driven test path landed.
            if (currentProject?.type === 'monogame') {
                await syncAssetsToRuntime();
            }
            const r: TestRunResult = currentProject?.type === 'monogame'
                ? mgToTestRunResult(await monoGameHost.runTests(source, name))
                : await runner.runTests(source, name);
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
        const source = await getProjectSource();
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
        clearOutput();
        revealPanel('output');
        appendTestLog(`▶ Run all`, 'dim');
        try {
            if (currentProject?.type === 'monogame') {
                await syncAssetsToRuntime();
            }
            const r: TestRunResult = currentProject?.type === 'monogame'
                ? mgToTestRunResult(await monoGameHost.runTests(source))
                : await runner.runTests(source);
            applyResult(r);
        } finally {
            setTestsBusy(false);
        }
    }

    function applyResult(r: TestRunResult) {
        if (r.error) {
            // r.error is typically a compile-failure dump. Those same
            // errors already streamed through LSP → Problems with proper
            // line/col pins; we'd just be duplicating text here. Show a
            // short pointer instead and reveal Problems.
            appendTestLog('Test compile failed. See Problems panel.', 'fail');
            revealPanel('problems');
            for (const t of testEntries) if (t.status === 'running') t.status = 'idle';
            // Keep the status bar wording short so the Tests panel stays
            // readable even with a long backend message.
            testsStatusEl.textContent = 'Compile failed';
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
        const headline = `Tests: ${r.passed} passed, ${r.failed} failed (${Math.round(r.duration)} ms)`;

        // All test-side output streams through appendTestLog (which now
        // writes to the Output panel). Failure frames render as click-to-
        // jump lines via the optional onClick on appendOutputLine.
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
    // Bumped v3 → v4 when the bottom-panel default height shrank and
    // Help moved into that group's tab strip. Old v3 layouts persisted
    // 240+ px for the bottom group; v4 starts users on the new 180 px
    // default so the Help tab doesn't feel oversized.
    const LAYOUT_STORAGE_KEY = 'fade.dockview.layout.v4';

    function setupDockview(): DockviewApi {
        const dockRoot = document.getElementById('dock-root')!;
        const panelCells = document.getElementById('panel-cells')!;

        const dock = createDockview(dockRoot, {
            // Built-in VSCode-like dark theme — matches the rest of the
            // playground's styling (vs-dark Monaco theme, vscode-elements).
            theme: { name: 'vs', className: 'dockview-theme-vs' },
            disableFloatingGroups: false,
            createComponent: ({ name, id }) => {
                // Dynamic components (one element per panel instance) —
                // resolved before the static `panel-cell` pool. Each name
                // here builds its own DOM on demand and owns its lifecycle.
                // dockview's createComponent contract gives us only id+name,
                // not panel params — so we encode the filename into the
                // panel id (e.g. `md-preview:doc.md`) and parse it back here.
                if (name === 'markdown-preview') {
                    const filename = id.startsWith('md-preview:') ? id.slice('md-preview:'.length) : '';
                    if (!filename) {
                        const err = document.createElement('div');
                        err.textContent = 'markdown-preview missing filename in panel id';
                        return { element: err, init() {}, dispose() {} };
                    }
                    return createMarkdownPreview(filename);
                }
                if (name === 'binary-preview') {
                    // dockview's createComponent only hands us {id, name} —
                    // params arrive via the component's init(parameters)
                    // a moment later. Hand an empty initial filename in;
                    // init() picks up the real params and calls setFilename.
                    return createBinaryPreview('', {
                        readBytes: (n) => workspace.readBytes(n),
                    });
                }
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
        'help',
        // Dynamic — created on demand by the markdown preview button.
        'markdown-preview',
        // Dynamic — created on demand when a binary file is opened
        // from the workspace tree (XNB, PNG, WAV, …).
        'binary-preview',
    ]);

    function healLayout(dock: DockviewApi) {
        try {
            // Pass 1: remove any restored panel whose component is unknown
            // (e.g. an old `debug` panel from a previous version), AND any
            // legacy per-file binary-preview panel id (`binary-preview:foo.xnb`)
            // — those are dead now that all binary previews share a single
            // `asset-preview` tab. Saved layouts from before the refactor
            // may still reference them.
            for (const panel of dock.panels.slice()) {
                const name = (panel as any).view?.contentComponent
                    ?? (panel as any).contentComponent
                    ?? (panel as any).component;
                const isLegacyBinaryPreview = panel.id.startsWith(LEGACY_BINARY_PREVIEW_ID_PREFIX);
                if (isLegacyBinaryPreview ||
                    (typeof name === 'string' && !KNOWN_COMPONENTS.has(name))) {
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
            addMissing('game', {
                position: { referencePanel: ref?.id ?? 'editor', direction: 'right' },
                renderer: RENDER_ALWAYS, title: 'Game',
            });
            // Help shares the right-column tab group with Game (matches
            // buildDefaultLayout). Fall back to the bottom group if Game
            // isn't around — that keeps Help reachable even in
            // weirdly-broken restored layouts.
            const helpRef = dock.getPanel('game')?.id ?? bottomRef;
            addMissing('help', {
                position: { referencePanel: helpRef, direction: 'within' },
                renderer: RENDER_ALWAYS, title: 'Help',
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
        // Bottom tab group: Output / Problems / Tests / Debug Console / Help.
        // Default height kept modest — the editor + game canvas should
        // dominate the viewport, with the bottom panel showing a few lines
        // of output by default. Users can drag the splitter taller when
        // they want to dig into Tests / Help / etc.
        const outputPanel = dock.addPanel({
            id: 'output',
            component: 'output',
            title: 'Output',
            position: { referencePanel: editorPanel.id, direction: 'below' },
            initialHeight: 180,
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
        // Game panel — only meaningful for fade.json type='monogame', but we
        // add it eagerly so users can preview the empty splash and the runOnce
        // branch always has somewhere to reveal. Initial size budget biased
        // toward the editor — game is a peek-on-Run thing for v1.
        const gamePanel = dock.addPanel({
            id: 'game',
            component: 'game',
            title: 'Game',
            position: { referencePanel: editorPanel.id, direction: 'right' },
            initialWidth: 360,
            renderer: RENDER_ALWAYS,
        });
        // Help: command reference. Shares the right-column tab group with
        // Game so users can flip from the running game to the docs in one
        // click without losing horizontal real estate to the editor or
        // bottom panel.
        dock.addPanel({
            id: 'help',
            component: 'help',
            title: 'Help',
            position: { referencePanel: gamePanel.id, direction: 'within' },
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
                if (o) o.api.setSize({ height: 180 });
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

    // Mount the Help panel's TOC + search + reader. Populated below
    // once the LSP worker is ready (no source needed — it reads from
    // the bridge's loaded CommandCollection).
    const helpCtl = mountHelpPanel();
    // Open the Help tab + focus a specific command. Used by the hover
    // provider's "View in Help →" link and by external probes via
    // window.__fadeHelp.openCommand(name).
    function openHelpForCommand(name: string): boolean {
        try { dockApi.getPanel('help')?.api?.setActive(); } catch { /* ignore */ }
        return helpCtl.selectCommand(name);
    }
    // Fire-and-forget on ready — the LSP worker has the workspace
    // populated by the time runner.ready resolves, so this returns
    // every loaded command (Standard + Web + Standard+MonoGame depending
    // on which CommandCollection the worker currently has loaded).
    //
    // Stash the fetcher in the closure-scoped slot so refreshFadeProject
    // (defined earlier in bootstrap) can re-invoke it whenever the LSP's
    // CommandCollection swaps because fade.json's `type` changed.
    refreshHelpEntriesFromWorker = () => {
        void runner.listCommandDocs().then((entries: HelpCommandDocEntry[]) => {
            helpCtl.setEntries(entries);
        }).catch((e) => console.warn('[fade] help: list-command-docs failed', e));
    };
    refreshHelpEntriesFromWorker();

    // Test probe / public API surface.
    (window as any).__fadeHelp = {
        openCommand: (name: string) => openHelpForCommand(name),
        getController: () => helpCtl,
    };

    // Reset Layout: nuke the persisted layout and reload. Useful when
    // something puts the dock into an awkward state we can't recover via
    // healLayout alone.
    resetLayoutBtn.addEventListener('click', () => {
        if (!confirm('Reset all panel layout to defaults?')) return;
        try { localStorage.removeItem(LAYOUT_STORAGE_KEY); } catch { /* ignore */ }
        location.reload();
    });

    // Console-only escape hatch — no UI button, intentionally. Wipes
    // workspace/ from OPFS and clears Playground state in localStorage,
    // then reloads. Useful when an existing workspace lacks fade.json (so
    // the manifest lock keeps you from regenerating it) or when migrations
    // leave things in a confused state.
    (window as any).forceHardReset = async () => {
        if (!confirm('forceHardReset(): wipe OPFS workspace + reset state? This deletes every file in every project.')) return;
        try {
            const root = await navigator.storage.getDirectory();
            try { await root.removeEntry('workspace', { recursive: true }); } catch { /* ignore */ }
        } catch (e) {
            console.error('[fade] OPFS wipe failed', e);
        }
        try {
            localStorage.removeItem(LAYOUT_STORAGE_KEY);
            localStorage.removeItem(ACTIVE_PROJECT_KEY);
        } catch { /* ignore */ }
        console.warn('[fade] forceHardReset complete — reloading');
        location.reload();
    };

    // ─── Project viewer overlay ──────────────────────────────────────────
    // Modal dialog that lists OPFS projects and offers a "new project"
    // input. Switching reloads the page — simplest way to ensure all
    // dock panels, Monaco models, and the polling loop pick up the new
    // project cleanly (the dockview layout is global and persists
    // across switches).
    const projectOverlay = document.getElementById('project-overlay')!;
    const projectListEl = document.getElementById('project-list')!;
    const projectOverlayCloseBtn = document.getElementById('project-overlay-close')!;
    const projectNewInput = document.getElementById('project-new-input') as HTMLInputElement;
    const projectNewError = document.getElementById('project-new-error')!;
    const openProjectsBtn = document.getElementById('open-projects')!;

    function showProjectError(msg: string) {
        projectNewError.textContent = msg;
        projectNewError.hidden = false;
    }
    function clearProjectError() {
        projectNewError.textContent = '';
        projectNewError.hidden = true;
    }

    // Pull a short summary line from a project's fade.json so the list
    // shows something useful (name + source count or an invalid marker).
    async function summarizeProject(name: string): Promise<{ label: string; meta: string; invalid: boolean }> {
        try {
            const dir = await (await navigator.storage.getDirectory())
                .getDirectoryHandle('workspace')
                .then((w) => w.getDirectoryHandle(name));
            let manifestText: string | null = null;
            try {
                const fh = await dir.getFileHandle(FADE_JSON_NAME);
                manifestText = await (await fh.getFile()).text();
            } catch { /* no manifest */ }
            if (!manifestText) return { label: name, meta: 'no fade.json', invalid: true };
            const r = parseFadeProject(manifestText);
            if (!r.ok || !r.project) return { label: name, meta: 'fade.json invalid', invalid: true };
            const count = r.project.sources.length;
            const label = r.project.name && r.project.name !== name
                ? `${r.project.name} (${name})`
                : name;
            return { label, meta: `${count} source${count === 1 ? '' : 's'}`, invalid: false };
        } catch {
            return { label: name, meta: 'unreadable', invalid: true };
        }
    }

    async function renderProjectList() {
        projectListEl.innerHTML = '';
        const projects = await workspace.listProjects();
        const active = workspace.currentProject();
        for (const name of projects) {
            const li = document.createElement('li');
            li.className = 'project-row' + (name === active ? ' active' : '');
            li.dataset.name = name;

            const icon = document.createElement('span');
            icon.className = 'codicon ' + (name === active ? 'codicon-folder-opened' : 'codicon-folder');
            li.append(icon);

            const label = document.createElement('span');
            label.className = 'project-row-name';
            label.textContent = name;
            li.append(label);

            const meta = document.createElement('span');
            meta.className = 'project-row-meta';
            meta.textContent = name === active ? 'active' : '…';
            li.append(meta);

            li.onclick = () => {
                if (name === active) { closeProjectOverlay(); return; }
                switchToProject(name);
            };
            projectListEl.append(li);

            // Resolve the meta line lazily so the list renders fast even
            // with many projects.
            summarizeProject(name).then((s) => {
                if (name !== active) meta.textContent = s.meta;
                if (s.invalid) li.classList.add('invalid');
                label.textContent = s.label;
            });
        }
    }

    async function switchToProject(name: string) {
        try {
            await workspace.setActiveProject(name);
        } catch (e: any) {
            showProjectError('Failed to switch: ' + (e?.message ?? e));
            return;
        }
        // Reload so all in-memory state (models, tabs, dockview) rebinds
        // to the new project. Layout in localStorage survives.
        location.reload();
    }

    async function createNewProject(rawName: string) {
        const name = rawName.trim();
        if (!name) return;
        if (!/^[\w.-]+$/.test(name)) {
            showProjectError('Invalid name. Letters, digits, dot, dash, underscore only.');
            return;
        }
        const existing = await workspace.listProjects();
        if (existing.includes(name)) {
            showProjectError(`A project named "${name}" already exists.`);
            return;
        }
        try {
            await workspace.createProject(name);
        } catch (e: any) {
            showProjectError('Create failed: ' + (e?.message ?? e));
            return;
        }
        clearProjectError();
        await switchToProject(name);
    }

    function openProjectOverlay() {
        clearProjectError();
        projectNewInput.value = '';
        projectOverlay.hidden = false;
        renderProjectList().catch((e) => console.error('[fade] project list render failed', e));
        // Focus the new-project input on a microtask so screen readers + the
        // browser's focus ring land correctly after the show animation.
        setTimeout(() => projectNewInput.focus(), 0);
    }
    function closeProjectOverlay() { projectOverlay.hidden = true; }

    openProjectsBtn.addEventListener('click', openProjectOverlay);
    projectNameEl.addEventListener('click', openProjectOverlay);
    projectOverlayCloseBtn.addEventListener('click', closeProjectOverlay);
    projectOverlay.addEventListener('click', (e) => {
        if (e.target === projectOverlay) closeProjectOverlay();
    });
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && !projectOverlay.hidden) {
            e.preventDefault();
            closeProjectOverlay();
        } else if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'p' && !e.shiftKey) {
            // ⌘P / Ctrl+P opens the project switcher. Override the browser
            // print shortcut since users are editing code, not paper docs.
            e.preventDefault();
            openProjectOverlay();
        }
    });
    projectNewInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
            e.preventDefault();
            createNewProject(projectNewInput.value);
        }
        if (e.key === 'Escape') {
            e.stopPropagation();
            closeProjectOverlay();
        }
    });
    projectNewInput.addEventListener('input', clearProjectError);

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
        // Reparent overflow widgets (suggest popup, hover, signature help)
        // to document.body. Doesn't cover the right-click context menu —
        // that one ships via the IContextViewService and uses its own
        // container; see the reparent-on-mutation observer below.
        fixedOverflowWidgets: true,
    } as monaco.editor.IStandaloneEditorConstructionOptions);

    // (Earlier attempt to reparent the context-menu container lived here
    // — turned out vscode-vscode-api creates the menu inside a shadow root
    // attached to the editor, so a MutationObserver on document.body
    // couldn't see it. Real fix is the CSS override in index.html that
    // removes `transform: translate3d(0,0,0)` from .dv-render-overlay so
    // `position: fixed` once again anchors to the viewport.)

    // Watch ALL fade models in the registry and push when any change. Picks
    // up changes regardless of which model object the live editor uses (we
    // can have duplicates with the same URI under codingame's services).
    // Also watches fade.json so the project manifest stays in sync with
    // the live editor (drives source-list concat + Problems entries).
    const lastPushedByUri = new Map<string, string>();
    setInterval(() => {
        let anyChanged = false;
        let manifestChanged = false;
        for (const m of monaco.editor.getModels()) {
            const lang = m.getLanguageId();
            const uri = m.uri.toString();
            const value = m.getValue();
            if (lastPushedByUri.get(uri) === value) continue;
            lastPushedByUri.set(uri, value);
            if (lang === 'fade') {
                lsp.setDocument(uri, value);
                anyChanged = true;
            } else if (uri.endsWith('/' + FADE_JSON_NAME)) {
                manifestChanged = true;
            }
        }
        // Re-discover tests in the background whenever the active file moves.
        if (anyChanged) refreshDebounce();
        if (manifestChanged) {
            // fade.json edit landed — re-validate + refresh derived state.
            refreshFadeProject().then(() => refreshDebounce());
        }
    }, 250);

    // Preload every workspace file's Monaco model first so refreshFadeProject
    // (and the project source concat) can read live content without an OPFS
    // round-trip per file. Tabs aren't opened for these — they just sit in
    // monaco.editor.getModels() until the user clicks them.
    const names = await workspace.list();
    for (const name of names) {
        const uri = monaco.Uri.file(`/workspace/${name}`);
        if (!monaco.editor.getModel(uri)) {
            const text = await workspace.read(name);
            monaco.editor.createModel(text, languageFor(name), uri);
        }
    }

    // Now that the fade.json model exists, resolve currentProject. We
    // *must* await this before picking the default-opened file — otherwise
    // currentProject is null and the preferred logic falls back to
    // alphabetical, opening the wrong file when fade.json reorders sources.
    await refreshFadeProject();
    setTimeout(refreshTests, 200);

    // Open a sensible default. Prefer the first .fbasic listed in fade.json
    // (so the user lands in source code, not the manifest); fall back to any
    // .fbasic in the workspace; fall back to whatever's there.
    if (names.length > 0) {
        const manifestSources: string[] = (currentProject as FadeProject | null)?.sources ?? [];
        const preferred =
            manifestSources.find((s) => names.includes(s))
            ?? names.find((n) => /\.(fbasic|fb)$/i.test(n))
            ?? names[0];
        await openFile(workspace, preferred);
    } else {
        editorContainer.style.display = 'none';
        editorPlaceholder.style.display = 'flex';
        await renderFileList(workspace);
    }

    statusEl.textContent = 'Ready.';
    runBtn.disabled = false;
    debugBtn.disabled = false;

    // Walk the active project's OPFS folder for `.xnb` files and push their
    // bytes into the MonoGame runtime's BrowserContentManager. Called before
    // each loadProgram/debugStart so any `texture`/`load sfx clip`/`font`
    // commands fbasic runs can resolve via stock Content.Load<T>(name).
    //
    // Asset name = filename minus the `.xnb` extension. So `Catfish.xnb`
    // registers under "Catfish" — matching what fbasic code passes to
    // `texture 1, "Catfish"`. Pre-clears the runtime dict so deletions in
    // OPFS take effect on the next Run.
    //
    // SoundEffect XNBs get a loopLength patch on the way through — see
    // patchSoundEffectForKni for the KNI Blazor bug it works around. Effect
    // XNBs from modern MGCB (MGFX v11) get the version byte downgraded to v10
    // so KNI 4.2.9001's Effect ctor doesn't reject them; see
    // patchEffectMgfxVersionForKni.
    async function syncAssetsToRuntime(): Promise<void> {
        await monoGameHost.clearAssets();
        const names = await workspace.list();
        for (const name of names) {
            if (!/\.xnb$/i.test(name)) continue;
            try {
                const raw = await workspace.readBytes(name);
                const bytes = patchEffectMgfxVersionForKni(patchSoundEffectForKni(raw));
                const assetName = name.replace(/\.xnb$/i, '');
                await monoGameHost.registerAsset(assetName, bytes);
            } catch (e) {
                console.error('[fade] asset push failed for', name, e);
            }
        }
    }

    const runOnce = async () => {
        const source = await getProjectSource();
        if (!source) {
            clearOutput();
            appendOutputLine('No file open.', 'dim');
            return;
        }
        runBtn.disabled = true;
        clearOutput();

        // ─── 'monogame' branch ──────────────────────────────────────────
        // Route to the canvas-side runtime (WebRuntime.MonoGame). First
        // call boots ~8 MB of WASM lazily; subsequent calls hot-reload via
        // Game1.LoadProgram. Reveal the Game panel so the canvas is visible.
        if (currentProject?.type === 'monogame') {
            try {
                revealPanel('game');
                appendOutputLine('Booting MonoGame runtime…', 'dim');
                await syncAssetsToRuntime();
                const ok = await monoGameHost.loadProgram(source);
                if (ok) {
                    appendOutputLine('Running on canvas.', 'info');
                    // Game is alive — let the user kill it via Stop.
                    stopBtn.disabled = false;
                } else {
                    appendOutputLine('Compile failed. See Problems panel.', 'error');
                    revealPanel('problems');
                }
            } catch (e: any) {
                appendOutputLine('MonoGame runtime error: ' + (e?.message ?? String(e)), 'error');
            } finally {
                runBtn.disabled = false;
            }
            return;
        }

        try {
            const result = await runner.run(source);
            // CompileAndRun returns a JSON envelope { compileError, runtimeError, printed }.
            // Tolerate the legacy plain-string form too — useful if an older
            // worker is still in cache mid-deploy.
            let env: { compileError?: string | null; runtimeError?: string | null; printed?: string } | null = null;
            try { env = JSON.parse(result); } catch { env = null; }
            if (env && (typeof env.compileError !== 'undefined' || typeof env.runtimeError !== 'undefined')) {
                // `env.printed` would duplicate what already streamed via the
                // worker's per-line `print` messages (onPrint → appendOutputLine).
                // We intentionally ignore it here and only render errors + the
                // empty-state hint.
                //
                // `env.compileError` would also duplicate what the LSP already
                // surfaced in the Problems panel (lex/parse/symbol errors all
                // flow through both paths). Replace the verbose text dump with
                // a short hint and reveal Problems so the user sees the rich
                // entries with line/col pins.
                if (env.compileError) {
                    appendOutputLine('Compile failed. See Problems panel.', 'error');
                    revealPanel('problems');
                }
                if (env.runtimeError) appendOutputLine(env.runtimeError, 'error');
                if (!env.compileError && !env.runtimeError && !env.printed) {
                    appendOutputLine('(no output)', 'dim');
                }
            } else {
                appendOutputLine(result);
            }
        } catch (e: any) {
            appendOutputLine(e?.message ?? String(e), 'error');
        } finally {
            runBtn.disabled = false;
        }
    };

    runBtn.addEventListener('click', runOnce);
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyR, runOnce);

    // Header Stop button. Delegates to stopAll() so debug sessions get
    // a clean terminate + the canvas pauses uniformly. For 'web'
    // projects there's no canvas to pause and no header-level stop for
    // a CompileAndRun-in-flight; the floating debug-toolbar Stop is
    // still the right way to end a debug session there.
    stopBtn.addEventListener('click', async () => {
        try {
            await stopAll();
            appendOutputLine('Stopped.', 'dim');
        } catch (e: any) {
            appendOutputLine('Stop failed: ' + (e?.message ?? String(e)), 'error');
        }
    });

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
        // Header Stop mirrors the floating debug toolbar's Stop while a
        // session is active; runOnce also flips this on after a plain
        // (non-debug) monogame Run. Both buttons delegate to stopAll().
        if (hasSession) stopBtn.disabled = false;
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
        void dbg.setBreakpoints(payload);
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
                const result = await dbg.eval(activeFrameId, expr);
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
        const frames = await dbg.stackFrames();
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
        const result = await dbg.scopes(frameId);
        // [DEBUG-LOGGING — remove once scope-after-step issue is resolved]
        try {
            const summary = (result?.scopes ?? []).map((s: any) => ({
                name: s?.scopeName,
                vars: (s?.variables ?? []).map((v: any) => `${v.name}=${v.value}`),
            }));
            // eslint-disable-next-line no-console
            console.log('[DBG-EV] refreshScopes(' + frameId + ') →', JSON.stringify(summary));
        } catch { /* logging is best-effort */ }
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
                const result = await dbg.expandVariable(v.id);
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
                    const result = await dbg.setVariable(activeFrameId!, v.id, rhs);
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

    // Same handler runs for both backends — events from the canvas-side
    // monogame DebugSession come through monoGameHost's rAF drain rather
    // than the worker postMessage path, but the event shape is identical.
    monoGameHost.onDebugEvent = (event) => { void onAnyDebugEvent(event); };
    runner.onDebugEvent = (event) => { void onAnyDebugEvent(event); };

    async function onAnyDebugEvent(event: any) {
        // [DEBUG-LOGGING — remove once scope-after-step issue is resolved]
        // Dump every event with its parsed json so we can see (a) ordering of
        // REV_REQUEST_BREAKPOINT vs PROTO_ACK, (b) what status/reason each
        // PROTO_ACK carries, and (c) whether two events race the refresh.
        try {
            let parsedDbg: any = null;
            if (event?.json) { try { parsedDbg = JSON.parse(event.json); } catch { /* opaque */ } }
            // eslint-disable-next-line no-console
            console.log('[DBG-EV]', event?.type, {
                id: event?.id,
                status: parsedDbg?.status,
                reason: parsedDbg?.reason,
                rawJsonPreview: typeof event?.json === 'string' ? event.json.slice(0, 160) : null,
            });
        } catch { /* logging is best-effort */ }
        switch (event.type) {
            case 'REV_REQUEST_BREAKPOINT':
                debugPaused = true;
                setDebugStatus('paused on breakpoint', 'paused');
                revealPanel('call-stack');
                setDebugButtons();
                await refreshDebugView();
                // eslint-disable-next-line no-console
                console.log('[DBG-EV] BREAKPOINT refreshDebugView done');
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
            case 'REV_REQUEST_EXPLODE': {
                debugSessionActive = false;
                debugPaused = false;
                // Filter the synthetic "explode" the bridge throws when
                // the user clicks Stop mid-`wait ms`: the VM's exception
                // catch wraps our OperationCanceledException as a runtime
                // error. It's not actually an error — surface it as a
                // clean stop instead.
                const expMsg = (event.json ?? event.message ?? '') as string;
                const isTerminateUnwind = /interrupted by terminate/i.test(expMsg);
                if (isTerminateUnwind) {
                    setDebugStatus('stopped', 'idle');
                } else {
                    setDebugStatus('runtime error', 'error');
                    appendReplLine(expMsg || 'runtime error', 'err');
                    revealPanel('debug-console');
                }
                setCurrentLine(null);
                clearDebugInspectionPanels();
                setDebugEmptyStates(true);
                setDebugButtons();
                break;
            }
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
                // eslint-disable-next-line no-console
                console.log('[DBG-EV] PROTO_ACK stepLanded=', stepLanded);
                if (stepLanded) {
                    debugPaused = true;
                    setDebugStatus('paused on step', 'paused');
                    setDebugButtons();
                    await refreshDebugView();
                    // eslint-disable-next-line no-console
                    console.log('[DBG-EV] STEP refreshDebugView done');
                } else {
                    debugPaused = false;
                    setDebugStatus('running', 'running');
                    setCurrentLine(null);
                    // While running, show a guidance message in the inspection
                    // panes instead of "No active debug session" — there IS
                    // a session, the user just can't see anything yet.
                    setDebugEmptyStates(true, 'Running — hit a breakpoint or pause to inspect');
                    setDebugButtons();
                    // eslint-disable-next-line no-console
                    console.log('[DBG-EV] PROTO_ACK fell into RUNNING branch — var panel cleared');
                }
                break;
            }
            case 'error':
                appendReplLine(event.message ?? 'error', 'err');
                break;
        }
    }

    // Dispatch debug ops to the right backend based on the active fade.json
    // type. 'web' uses the existing worker debug API (runner.debugX);
    // 'monogame' uses the canvas-side bridge (monoGameHost.debugX).
    // Same logical operations on both sides — the result shapes match
    // so call sites consume them identically. Parses JSON strings
    // returned by monoGameHost so callers see the same shape as
    // runner's pre-parsed responses.
    const dbg = {
        start: (source: string): Promise<any> =>
            currentProject?.type === 'monogame'
                // Push assets first; the canvas runtime needs the dict
                // populated *before* the user program's `texture`/`sfx`
                // commands run inside Game1.LoadProgram (the very next call).
                ? syncAssetsToRuntime()
                    .then(() => monoGameHost.debugStart(source))
                    .then((s) => JSON.parse(s))
                : runner.debugStart(source),
        startTest: (source: string, testName: string): Promise<any> =>
            currentProject?.type === 'monogame'
                // Test debug on the canvas needs Game1 not actively
                // running the user program — sync assets, then ask the
                // canvas runtime to swap in a fresh test-VM. See
                // Index.Debug.cs's DebugStartTest.
                ? syncAssetsToRuntime()
                    .then(() => monoGameHost.debugStartTest(source, testName))
                    .then((s) => JSON.parse(s))
                : runner.debugStartTest(source, testName),
        continue: (): Promise<any> =>
            currentProject?.type === 'monogame'
                ? monoGameHost.debugContinue()
                : runner.debugContinue(),
        pause: (): Promise<any> =>
            currentProject?.type === 'monogame'
                ? monoGameHost.debugPause()
                : runner.debugPause(),
        step: (kind: 'over' | 'in' | 'out'): Promise<any> =>
            currentProject?.type === 'monogame'
                ? monoGameHost.debugStep(kind)
                : runner.debugStep(kind),
        terminate: (): Promise<any> =>
            currentProject?.type === 'monogame'
                ? monoGameHost.debugTerminate()
                : runner.debugTerminate(),
        setBreakpoints: (payload: any): Promise<any> =>
            currentProject?.type === 'monogame'
                ? monoGameHost.debugSetBreakpoints(JSON.stringify(payload))
                : runner.debugSetBreakpoints(payload),
        stackFrames: (): Promise<any> =>
            currentProject?.type === 'monogame'
                ? monoGameHost.debugStackFrames().then((s) => JSON.parse(s))
                : runner.debugStackFrames(),
        scopes: (frameId: number): Promise<any> =>
            currentProject?.type === 'monogame'
                ? monoGameHost.debugScopes(frameId).then((s) => JSON.parse(s))
                : runner.debugScopes(frameId),
        expandVariable: (variableId: number): Promise<any> =>
            currentProject?.type === 'monogame'
                ? monoGameHost.debugVariableExpansion(variableId).then((s) => JSON.parse(s))
                : runner.debugExpandVariable(variableId),
        eval: (frameId: number, expression: string): Promise<any> =>
            currentProject?.type === 'monogame'
                ? monoGameHost.debugEval(frameId, expression).then((s) => JSON.parse(s))
                : runner.debugEval(frameId, expression),
        repl: (frameId: number, code: string): Promise<any> =>
            currentProject?.type === 'monogame'
                ? monoGameHost.debugRepl(frameId, code).then((s) => JSON.parse(s))
                : runner.debugRepl(frameId, code),
        setVariable: (frameId: number, variableId: number, rhs: string): Promise<any> =>
            currentProject?.type === 'monogame'
                ? monoGameHost.debugSetVariable(frameId, variableId, rhs).then((s) => JSON.parse(s))
                : runner.debugSetVariable(frameId, variableId, rhs),
    };

    const startDebug = async () => {
        const source = await getProjectSource();
        if (!source) {
            clearOutput();
            appendOutputLine('No file open.', 'dim');
            return;
        }
        await beginDebugSession(() => dbg.start(source));
    };

    // Shared session-start machinery, factored so both Debug-button and
    // per-test Debug share the same "prep UI → start → sync bps → continue"
    // sequence.
    async function beginDebugSession(starter: () => Promise<DebugStartResult>): Promise<boolean> {
        clearOutput();
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
        await dbg.continue();
        debugPaused = false;
        setDebugStatus('running', 'running');
        setDebugButtons();
        return true;
    }

    // Per-test debug entry. Compiles the current file, starts a VM at the
    // chosen test's entry point, then proceeds like a normal debug session.
    async function debugSingleTest(name: string) {
        const source = await getProjectSource();
        if (!source) {
            clearOutput();
            appendOutputLine('No file open.', 'dim');
            return;
        }
        appendReplLine(`▶ debug test "${name}"`, 'in');
        await beginDebugSession(() => dbg.startTest(source, name));
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
        await dbg.continue();
        debugPaused = false;
        setDebugStatus('running', 'running');
        setCurrentLine(null);
        setDebugButtons();
    });
    debugPauseBtn.addEventListener('click', async () => {
        await dbg.pause();
        // The 'paused' state is asserted by the next breakpoint event.
    });
    debugStepOverBtn.addEventListener('click', async () => {
        await dbg.step('over');
        debugPaused = false;
        setCurrentLine(null);
        setDebugButtons();
    });
    debugStepInBtn.addEventListener('click', async () => {
        await dbg.step('in');
        debugPaused = false;
        setCurrentLine(null);
        setDebugButtons();
    });
    debugStepOutBtn.addEventListener('click', async () => {
        await dbg.step('out');
        debugPaused = false;
        setCurrentLine(null);
        setDebugButtons();
    });
    // Single point of truth for "stop everything" — terminates an active
    // debug session AND pauses any running monogame canvas. Both Stop
    // buttons (header + floating debug toolbar) call this.
    async function stopAll() {
        if (debugSessionActive) {
            await dbg.terminate();
            debugSessionActive = false;
            debugPaused = false;
            setDebugStatus('stopped', 'idle');
            setCurrentLine(null);
            clearDebugInspectionPanels();
            setDebugEmptyStates(true);
        }
        // Pause the canvas regardless of debug state — even after debug
        // terminate, the VM is left running, so this halts ticks too.
        if (currentProject?.type === 'monogame') {
            try { await monoGameHost.stop(); } catch { /* best effort */ }
        }
        stopBtn.disabled = true;
        setDebugButtons();
    }
    debugStopBtn.addEventListener('click', stopAll);

    debugReplInput.addEventListener('keydown', async (e) => {
        if (e.key !== 'Enter') return;
        const expr = debugReplInput.value.trim();
        if (!expr || activeFrameId == null) return;
        appendReplLine(expr, 'in');
        debugReplInput.value = '';
        const result = await dbg.repl(activeFrameId, expr);
        const failed = !result || result.id === -1;
        appendReplLine(result?.value ?? '(no result)', failed ? 'err' : 'out');
        // Variables may have changed.
        if (activeFrameId != null) await refreshScopes(activeFrameId);
    });

    // Editor option: glyph margin must be on to show breakpoint glyphs.
    editor.updateOptions({ glyphMargin: true });

    // New-file flow: click the +-button OR right-click in the workspace
    // pane's empty area → small dropdown of allowed extensions → an
    // inline edit row appears in the file list, pre-filled with a
    // suggested name (base portion selected). Enter saves; Escape /
    // blur / invalid name silently discards.
    const NEW_FILE_EXTENSIONS: Array<{ label: string; ext: string }> = [
        { label: 'Fade source (.fbasic)', ext: 'fbasic' },
        { label: 'Shader (.fx)',          ext: 'fx' },
        { label: 'JSON (.json)',          ext: 'json' },
        { label: 'Text (.txt)',           ext: 'txt' },
    ];

    function showNewFileMenu(x: number, y: number) {
        closeAnyFileMenu();
        const menu = document.createElement('div');
        menu.className = 'source-badge-menu';
        menu.dataset.menu = 'file-context';
        for (const { label, ext } of NEW_FILE_EXTENSIONS) {
            const item = document.createElement('button');
            item.className = 'source-badge-item';
            item.type = 'button';
            item.textContent = label;
            item.onclick = (e) => {
                e.stopPropagation();
                closeAnyFileMenu();
                startInlineCreate(ext);
            };
            menu.append(item);
        }
        // Separator + upload action. Opens a file picker; selected files
        // land in OPFS under their original names (collision-renamed) and
        // the first one is auto-previewed.
        const sep = document.createElement('div');
        sep.className = 'source-badge-sep';
        menu.append(sep);
        const upload = document.createElement('button');
        upload.className = 'source-badge-item';
        upload.type = 'button';
        upload.textContent = 'Upload file…';
        upload.onclick = (e) => {
            e.stopPropagation();
            closeAnyFileMenu();
            triggerUploadPicker();
        };
        menu.append(upload);
        document.body.append(menu);
        menu.style.left = `${x}px`;
        menu.style.top = `${y}px`;
        const r = menu.getBoundingClientRect();
        if (r.right > window.innerWidth) menu.style.left = `${window.innerWidth - r.width - 4}px`;
        if (r.bottom > window.innerHeight) menu.style.top = `${window.innerHeight - r.height - 4}px`;
        setTimeout(() => {
            const onClick = (e: MouseEvent) => {
                if (!(e.target as HTMLElement).closest('.source-badge-menu')) closeAnyFileMenu();
            };
            const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') closeAnyFileMenu(); };
            document.addEventListener('mousedown', onClick, true);
            document.addEventListener('keydown', onKey, true);
            (menu as any).__cleanup = () => {
                document.removeEventListener('mousedown', onClick, true);
                document.removeEventListener('keydown', onKey, true);
            };
        }, 0);
    }

    // Insert an inline-edit row at the top of the file list and focus
    // its input. Commit on Enter; cancel on Escape/blur/invalid.
    let inlineCreateRow: HTMLLIElement | null = null;
    async function startInlineCreate(ext: string) {
        // If a previous row is hanging, kill it first.
        inlineCreateRow?.remove();
        inlineCreateRow = null;

        // Find a name that doesn't collide.
        const existing = new Set(await workspace.list());
        let base = 'untitled';
        let candidate = `${base}.${ext}`;
        let n = 1;
        while (existing.has(candidate)) candidate = `${base}${++n}.${ext}`;

        const li = document.createElement('li');
        li.className = 'file-edit-row';
        const input = document.createElement('input');
        input.type = 'text';
        input.spellcheck = false;
        input.autocomplete = 'off';
        input.value = candidate;
        li.append(input);
        // Insert at the top of the list so it's obvious.
        fileListEl.prepend(li);
        inlineCreateRow = li;
        // Focus + select the base name so users can type-replace it.
        input.focus();
        const dot = candidate.lastIndexOf('.');
        input.setSelectionRange(0, dot >= 0 ? dot : candidate.length);

        let settled = false;
        const finish = async (commit: boolean) => {
            if (settled) return;
            settled = true;
            const name = input.value.trim();
            li.remove();
            inlineCreateRow = null;
            if (!commit) return;
            // Silently discard on invalid name — by design, no alerts.
            if (!name) return;
            if (!/^[\w.-]+$/.test(name)) return;
            if (name === FADE_JSON_NAME) return;
            const names = await workspace.list();
            if (names.includes(name)) return;
            try {
                await workspace.write(name, '');
                await openFile(workspace, name);
                if (/\.(fbasic|fb)$/i.test(name)) {
                    await projectOps?.addSourceAt(name, 'end');
                }
            } catch (e) {
                console.error('[fade] new-file failed:', e);
            }
        };
        input.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') { e.preventDefault(); void finish(true); }
            else if (e.key === 'Escape') { e.preventDefault(); void finish(false); }
        });
        input.addEventListener('blur', () => { void finish(true); });
    }

    // ─── Upload + drag-drop binary files into the workspace ──────────────
    // Both the menu's "Upload file…" item and the file-list drag-drop
    // handlers route through ingestFiles() so the OPFS write + open-preview
    // + collision-rename behavior stays in one place.
    async function ingestFiles(files: FileList | File[]): Promise<void> {
        const list = Array.from(files);
        if (list.length === 0) return;
        const existing = new Set(await workspace.list());
        let firstUploaded: string | null = null;
        for (const file of list) {
            const name = uniqueName(file.name, existing);
            existing.add(name);
            try {
                const bytes = new Uint8Array(await file.arrayBuffer());
                await workspace.writeBytes(name, bytes);
                if (!firstUploaded) firstUploaded = name;
            } catch (e) {
                console.error('[fade] upload failed:', file.name, e);
            }
        }
        await renderFileList(workspace);
        if (firstUploaded) {
            if (isBinaryFileName(firstUploaded)) {
                openBinaryPreview(firstUploaded);
            } else {
                await openFile(workspace, firstUploaded);
            }
        }
    }

    function uniqueName(original: string, taken: Set<string>): string {
        // Collapse anything OPFS doesn't accept into safe chars so a wild
        // upload name doesn't ENOENT downstream. Same character class the
        // inline-create rename validator enforces (letters, digits, dot,
        // dash, underscore).
        const sanitized = original.replace(/[^\w.\-]+/g, '_');
        if (!taken.has(sanitized) && sanitized !== FADE_JSON_NAME) return sanitized;
        const dot = sanitized.lastIndexOf('.');
        const base = dot > 0 ? sanitized.slice(0, dot) : sanitized;
        const ext = dot > 0 ? sanitized.slice(dot) : '';
        let n = 1;
        while (true) {
            const candidate = `${base}-${n}${ext}`;
            if (!taken.has(candidate) && candidate !== FADE_JSON_NAME) return candidate;
            n++;
        }
    }

    function triggerUploadPicker(): void {
        const input = document.createElement('input');
        input.type = 'file';
        input.multiple = true;
        input.style.display = 'none';
        input.addEventListener('change', () => {
            if (input.files && input.files.length > 0) {
                void ingestFiles(input.files);
            }
            input.remove();
        });
        document.body.append(input);
        input.click();
    }

    // Drag-drop on the file list. Highlights the list during dragover;
    // drop hands the FileList off to ingestFiles. Skip drags that don't
    // carry files (e.g. internal text drags within Monaco) so we don't
    // steal them.
    function hasFilesDrag(e: DragEvent): boolean {
        const types = e.dataTransfer?.types;
        if (!types) return false;
        for (let i = 0; i < types.length; i++) {
            if (types[i] === 'Files') return true;
        }
        return false;
    }
    fileListEl.addEventListener('dragover', (e) => {
        if (!hasFilesDrag(e)) return;
        e.preventDefault();
        fileListEl.classList.add('file-list-drop-target');
    });
    fileListEl.addEventListener('dragleave', (e) => {
        // Only clear when the drag actually leaves the container — not
        // when it crosses between child rows.
        if (e.target === fileListEl) {
            fileListEl.classList.remove('file-list-drop-target');
        }
    });
    fileListEl.addEventListener('drop', (e) => {
        if (!hasFilesDrag(e)) return;
        e.preventDefault();
        fileListEl.classList.remove('file-list-drop-target');
        if (e.dataTransfer?.files && e.dataTransfer.files.length > 0) {
            void ingestFiles(e.dataTransfer.files);
        }
    });

    newFileBtn.addEventListener('click', (e) => {
        const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
        showNewFileMenu(r.left, r.bottom + 2);
    });
    // Right-click on the workspace pane's empty area (file rows handle
    // their own contextmenu and stop propagation via preventDefault +
    // showFileContextMenu).
    fileListEl.parentElement?.addEventListener('contextmenu', (e) => {
        // Skip if the right-click landed inside a file row — those have
        // their own context menu.
        if ((e.target as HTMLElement).closest('#file-list li')) return;
        e.preventDefault();
        showNewFileMenu(e.clientX, e.clientY);
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
        project: {
            // Refresh fade.json state synchronously and report the resolved
            // source concat. Used by playwright probes to validate ordering
            // without racing the 250ms polling loop.
            getSource: async () => {
                await refreshFadeProject();
                return await getProjectSource();
            },
            getProject: async () => {
                await refreshFadeProject();
                return currentProject;
            },
            getErrors: async () => {
                await refreshFadeProject();
                return currentProjectErrors;
            },
        },
        debug: {
            start: ({ source }: { source: string }) => dbg.start(source),
            startTest: ({ source, name }: { source: string; name: string }) =>
                dbg.startTest(source, name),
            terminate: (): Promise<any> => dbg.terminate(),
            setBreakpoints: ({ breakpoints }: { breakpoints: BreakpointRequest[] }) => dbg.setBreakpoints(breakpoints),
            step: ({ kind }: { kind: 'over' | 'in' | 'out' }) => dbg.step(kind),
            continue: (): Promise<any> => dbg.continue(),
            pause: (): Promise<any> => dbg.pause(),
            stackFrames: (): Promise<any> => dbg.stackFrames(),
            scopes: ({ frameId }: { frameId: number }) => dbg.scopes(frameId),
            expand: ({ variableId }: { variableId: number }) => dbg.expandVariable(variableId),
            eval: ({ frameId, expression }: { frameId: number; expression: string }) =>
                dbg.eval(frameId, expression),
            repl: ({ frameId, code }: { frameId: number; code: string }) =>
                dbg.repl(frameId, code),
            setVariable: ({ frameId, variableId, rhs }: { frameId: number; variableId: number; rhs: string }) =>
                dbg.setVariable(frameId, variableId, rhs),
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
