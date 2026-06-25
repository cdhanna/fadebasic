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
// virtualFs.registerFile() throws if called twice for the same URI.
// `registerFile` returns an IDisposable; storing it lets us properly
// unregister later (rename → new URI, project switch → drop old URIs,
// live-session leave → wipe the transient project's URIs). Without
// this, disposing a Monaco model leaves the registration orphaned in
// virtualFs and the next openFile for the same URI throws
// "file already exists".
const registeredVirtualFsUris = new Map<string, { dispose: () => void }>();
function registerVirtualFile(uri: monaco.Uri, text: string): void {
    const key = uri.toString();
    const prior = registeredVirtualFsUris.get(key);
    if (prior) {
        try { prior.dispose(); } catch { /* ignore */ }
    }
    const disp = virtualFs.registerFile(new RegisteredMemoryFile(uri, text));
    registeredVirtualFsUris.set(key, disp);
}
function unregisterVirtualFile(uri: monaco.Uri): void {
    const key = uri.toString();
    const disp = registeredVirtualFsUris.get(key);
    if (!disp) return;
    try { disp.dispose(); } catch { /* ignore */ }
    registeredVirtualFsUris.delete(key);
}

import EditorWorker from '@codingame/monaco-vscode-api/workers/editor.worker?worker';
import { languageForExtra, registerExtraLanguages, extraThemeRules } from './languages';
import { createMarkdownPreview, previewPanelIdFor } from './markdown-preview';
import {
    createBinaryPreview,
    BINARY_PREVIEW_PANEL_ID,
    LEGACY_BINARY_PREVIEW_ID_PREFIX,
    isBinaryFileName,
} from './binary-preview';
import { CatalogClient, catalogFilename } from './catalog/catalog-client';
import { createCatalogPanel } from './catalog/catalog-panel';
import { patchXnbForKni } from './xnb/xnb-previews';
import {
    compileImageAssetsWithPlan,
    compileFontAssetsWithPlan,
    compileShaderAssetsWithPlan,
    garbageCollectAssetCache,
} from './assets/compile-assets';
import { sha256Hex } from './assets/asset-cache';
import {
    ENCODER_VERSION,
    assetNameForSourcePath,
    isImageSourcePath,
    isAudioSourcePath,
    isFontSourcePath,
    isShaderSourcePath,
} from './assets/types';
import type { MonoGameContentPlan } from './monogame-host';
import {
    captureShaderErrorLine,
    clearShaderMarkers,
    flushPending as flushShaderMarkers,
} from './shader/shader-markers';
import { attachShaderValidator } from './shader/shader-validator';

const EMPTY_CONTENT_PLAN: MonoGameContentPlan = {
    defaultCompression: 'auto',
    entries: [],
};
import { mountHelpPanel, extractCommandNameFromHover } from './help';
import { monoGameHost, parseDebugUiEnvelope } from './monogame-host';
import { mountSharedCursors, type SharedCursorHandle } from './shared-cursor';
import { createLocalDebugAdapter } from './debug/local-adapter';
import { createRemoteDebugAdapter } from './debug/remote-adapter';
import { createFacadeDebugAdapter, type FacadeDebugAdapter } from './debug/facade-adapter';
import type { DebugAdapter, StepKind } from './debug/adapter';
import { mountAiChat, mountAiModels } from './ai-chat';
import { monacoDiagnosticsProvider } from './ai/adapters/monaco-diagnostics';
import { createProjectAwareLspEditValidator } from './ai/adapters/lsp-validate-edit';
import { PLAYGROUND_VERSION } from './changelog';
import { maybeShowChangelogPopup, showFullChangelog } from './version-popup';
import {
    extractInsIndex,
    hideCrashOverlay,
    showCrashOverlay,
    summarizeCrash,
} from './crash-overlay';
import type { CommandDocEntry as HelpCommandDocEntry, HelpSnippetToken } from './help';
import {
    mountCollaboration,
    statusGlyph,
    attachGutter,
    mountConflictEditor,
    mountHistoryPanel,
    createDiffViewer,
    type DiffViewerParams,
    type FileStatus,
    type CollaborationController,
    type GutterHandle,
} from './sharing';
import {
    bootstrapLiveSession,
    type LiveSessionHandle,
    type SessionHost as CollabSessionHost,
    type PeerView,
} from './sharing/collab';
import { mountLogsPanel } from './logs-panel';
import { mountSearchPanel } from './search-panel';
import { mountDebugUiPanel } from './debug-ui-panel';
import { mountSettingsPanel } from './settings-panel';
import {
    initSettings,
    onSettingsChange,
    currentSettings,
    getEffective,
    type SettingsState,
} from './settings';
import { resolveTheme } from './themes';
import { getLogger } from './log-bus';
import {
    FADE_JSON_NAME,
    defaultFadeProject,
    stringifyFadeProject,
    parseFadeProject,
    locateJsonPaths,
    offsetsToLineCol,
    type FadeProject,
    type FadeProjectType,
    type FadeConfigError,
    type CommandDllEntry,
} from './fade-config';
import { ProjectSourceMap } from './project-source-map';

// Synthetic URI for the joined "project document" we push to the LSP whenever
// a Fade project has more than one source listed in fade.json. The LSP sees
// one .fbasic doc with every file's lines concatenated in declaration order;
// JS translates per-file positions in and out via ProjectSourceMap so the
// LSP can keep treating documents as independent compilation units (no LSP-
// side changes needed — see [FadeBasic/LSP.Core/FadeWorkspace.cs]).
const PROJECT_LSP_URI = monaco.Uri.file('/workspace/__fade_project__.fbasic').toString();
(self as any).MonacoEnvironment = {
    getWorker: () => new EditorWorker(),
};
// Expose monaco globally for diagnostic probing from Playwright
(window as any).monaco = monaco;

// Per-type starter source for `main.fbasic` when a new workspace is
// created. Picked by createProject() based on the workspace type. The
// web starter sticks to print/loop primitives that work in the
// browser-only runtime; the MonoGame starter sets up the canonical
// `set sync rate` + DO/sync/LOOP frame loop. Tweak these freely — they
// only affect the seed of brand-new workspaces, not existing ones.
const WEB_STARTER_SOURCE = [
    'print upper$("hello from the playground")',
    'for n = 1 to 3',
    '  print "tick " + str$(n)',
    '  wait ms(300)',
    'next',
    'name$ = prompt$("what is your name?")',
    'print "your name has " + str$(len(name$)) + " letters."'
].join('\n');

const MONOGAME_STARTER_SOURCE = [
    'set render size 1280, 720',
    'set background color rgb(56, 71, 107)',
    'sprite 1, render width()/2, render height()/2, 0',
    'size sprite 1, 100, 100',
    'do',
    '  sync',
    'loop',
].join('\n');

// Default starter used by recovery paths (seedIfEmpty / legacy migration)
// where the project type isn't known. We bias toward web because it's
// the simpler runtime and won't crash if MonoGame isn't bootstrapped.
const DEFAULT_SOURCE = WEB_STARTER_SOURCE;

// ─── DOM refs ───────────────────────────────────────────────────────────────
// runBtn is a <vscode-button> custom element; it accepts `disabled` as an
// attribute just like a native button, but it isn't an HTMLButtonElement.
const runBtn = document.getElementById('run') as HTMLElement & { disabled: boolean };
const stopBtn = document.getElementById('stop') as HTMLElement & { disabled: boolean };
const debugBtn = document.getElementById('debug') as HTMLElement & { disabled: boolean };
const exportBtn = document.getElementById('export') as HTMLElement & { disabled: boolean };
const viewMenuBtn = document.getElementById('view-menu-btn') as HTMLElement;
const viewMenu = document.getElementById('view-menu') as HTMLElement;
const viewMenuPanels = document.getElementById('view-menu-panels') as HTMLElement;
const viewSaveLayoutBtn = document.getElementById('view-save-layout') as HTMLButtonElement;
const viewResetLayoutBtn = document.getElementById('view-reset-layout') as HTMLButtonElement;
const viewSavedLayouts = document.getElementById('view-saved-layouts') as HTMLElement;
const viewSemanticLayouts = document.getElementById('view-semantic-layouts') as HTMLElement;
const newFileBtn = document.getElementById('new-file') as HTMLButtonElement;
const newFolderBtn = document.getElementById('new-folder') as HTMLButtonElement;
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

    /** Initialize OPFS + pick an active project if one already exists.
     *
     *  Returns `false` when there are zero projects on disk — the caller
     *  is then expected to show the "create your first workspace" UI and
     *  call createProject + setActiveProject before touching any other
     *  workspace API. We deliberately do NOT auto-create a `default`
     *  project here, because that would force the type to `web` and rob
     *  first-time users of the chance to pick `monogame`. */
    async init(): Promise<boolean> {
        const opfsRoot = await navigator.storage.getDirectory();
        this.root = await opfsRoot.getDirectoryHandle('workspace', { create: true });

        // Migrate any legacy flat files (workspace/<file>) into a default
        // project folder so the new layout invariant holds.
        await this.migrateLegacyFlatLayout();

        const projects = await this.listProjects();
        if (projects.length === 0) return false;

        // Prefer the previously-active project if it still exists on disk.
        let target = localStorage.getItem(ACTIVE_PROJECT_KEY) || projects[0];
        if (!projects.includes(target)) target = projects[0];
        await this.setActiveProject(target, /*seedIfEmpty*/ true);
        return true;
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
    // `type` controls the runtime stamped into fade.json — `web` (the
    // browser-only path) or `monogame` (iframe-hosted MonoGame). The user
    // picks this in the new-project modal; falling back to `web` keeps
    // legacy callers behaving as before.
    async createProject(name: string, type: FadeProjectType = 'web'): Promise<void> {
        const dir = await this.root.getDirectoryHandle(name, { create: true });
        // Avoid clobbering an existing project.
        let hasAny = false;
        for await (const _ of (dir as any).values()) { hasAny = true; break; }
        if (hasAny) return;
        const mainFh = await dir.getFileHandle('main.fbasic', { create: true });
        const mainW = await mainFh.createWritable();
        const starter = type === 'monogame' ? MONOGAME_STARTER_SOURCE : WEB_STARTER_SOURCE;
        await mainW.write(starter);
        await mainW.close();
        const proj = defaultFadeProject(name, ['main.fbasic'], type);
        const manifestFh = await dir.getFileHandle(FADE_JSON_NAME, { create: true });
        const manifestW = await manifestFh.createWritable();
        await manifestW.write(stringifyFadeProject(proj));
        await manifestW.close();
    }

    // ─── Path-aware helpers ──────────────────────────────────────────────
    // Paths can include forward slashes; intermediate segments are OPFS
    // subdirectories. `list()` walks the tree and returns flat paths
    // (slashes included). All file ops accept either a flat name (legacy
    // root-level file) or a slashed path; both go through `walkPath()`
    // which produces the parent directory handle + leaf name.

    /** Split a path on `/`, navigate (or create) the intermediate
     *  directories, and return `{ parent, leaf }`. `create: true` makes
     *  missing dirs; `create: false` throws on missing. */
    private async walkPath(
        path: string,
        opts: { create: boolean },
    ): Promise<{ parent: FileSystemDirectoryHandle; leaf: string }> {
        const segments = path.split('/').filter((s) => s.length > 0);
        if (segments.length === 0) throw new Error('Empty path');
        const leaf = segments.pop()!;
        let cur: FileSystemDirectoryHandle = this.dir;
        for (const seg of segments) {
            cur = await cur.getDirectoryHandle(seg, { create: opts.create });
        }
        return { parent: cur, leaf };
    }

    /** Recursive file listing. Returns slashed paths for every file in
     *  the project, depth-first sorted. Directories are NOT returned;
     *  use `listEntries()` if you need the directory structure too. */
    async list(): Promise<string[]> {
        const out: string[] = [];
        const walk = async (dir: FileSystemDirectoryHandle, prefix: string) => {
            for await (const entry of (dir as any).values() as AsyncIterable<FileSystemHandle>) {
                const childPath = prefix ? `${prefix}/${entry.name}` : entry.name;
                if (entry.kind === 'file') {
                    out.push(childPath);
                } else if (entry.kind === 'directory') {
                    await walk(entry as FileSystemDirectoryHandle, childPath);
                }
            }
        };
        await walk(this.dir, '');
        out.sort();
        return out;
    }

    /** Tree-shaped listing: every file AND every directory, with kind
     *  + slashed path. Used by the file-list renderer to draw the
     *  expandable tree. */
    async listEntries(): Promise<Array<{ path: string; kind: 'file' | 'directory' }>> {
        const out: Array<{ path: string; kind: 'file' | 'directory' }> = [];
        const walk = async (dir: FileSystemDirectoryHandle, prefix: string) => {
            for await (const entry of (dir as any).values() as AsyncIterable<FileSystemHandle>) {
                const childPath = prefix ? `${prefix}/${entry.name}` : entry.name;
                if (entry.kind === 'file') {
                    out.push({ path: childPath, kind: 'file' });
                } else if (entry.kind === 'directory') {
                    out.push({ path: childPath, kind: 'directory' });
                    await walk(entry as FileSystemDirectoryHandle, childPath);
                }
            }
        };
        await walk(this.dir, '');
        out.sort((a, b) => a.path.localeCompare(b.path));
        return out;
    }

    async read(path: string): Promise<string> {
        const { parent, leaf } = await this.walkPath(path, { create: false });
        const fh = await parent.getFileHandle(leaf);
        const f = await fh.getFile();
        return await f.text();
    }

    async write(path: string, content: string): Promise<void> {
        const { parent, leaf } = await this.walkPath(path, { create: true });
        const fh = await parent.getFileHandle(leaf, { create: true });
        const w = await fh.createWritable();
        await w.write(content);
        await w.close();
    }

    // Binary read/write — used for uploaded assets (.xnb, .png, .wav, …).
    // The text-based read/write above is kept intact; callers route through
    // one or the other based on the file extension. The underlying OPFS
    // handle is the same; only the decode/encode shape differs.
    async readBytes(path: string): Promise<Uint8Array> {
        const { parent, leaf } = await this.walkPath(path, { create: false });
        const fh = await parent.getFileHandle(leaf);
        const f = await fh.getFile();
        return new Uint8Array(await f.arrayBuffer());
    }

    async writeBytes(path: string, bytes: Uint8Array): Promise<void> {
        const { parent, leaf } = await this.walkPath(path, { create: true });
        const fh = await parent.getFileHandle(leaf, { create: true });
        const w = await fh.createWritable();
        // Wrap in a Blob so the writable stream's union type doesn't
        // reject Uint8Array<ArrayBufferLike> when SharedArrayBuffer is in
        // the lib (web-worker.d.ts pulls it in for our prompt$ plumbing).
        await w.write(new Blob([bytes as BlobPart]));
        await w.close();
    }

    async delete(path: string): Promise<void> {
        if (path === FADE_JSON_NAME) {
            throw new Error('fade.json is required and cannot be deleted.');
        }
        const { parent, leaf } = await this.walkPath(path, { create: false });
        // recursive: true so deleting a folder cleans children too. For
        // files it has no effect (kept for spec uniformity).
        await parent.removeEntry(leaf, { recursive: true });
    }

    /** Create an empty directory. No-op if already exists. */
    async mkdir(path: string): Promise<void> {
        // walkPath with create:true gives us the leaf's parent; the leaf
        // itself is the directory we want.
        const { parent, leaf } = await this.walkPath(path, { create: true });
        await parent.getDirectoryHandle(leaf, { create: true });
    }

    /** True iff `path` exists and refers to a directory. */
    async isDirectory(path: string): Promise<boolean> {
        try {
            const { parent, leaf } = await this.walkPath(path, { create: false });
            await parent.getDirectoryHandle(leaf);
            return true;
        } catch {
            return false;
        }
    }

    /** True iff `path` exists (file or directory). */
    async exists(path: string): Promise<boolean> {
        try {
            const { parent, leaf } = await this.walkPath(path, { create: false });
            try { await parent.getFileHandle(leaf); return true; }
            catch { /* fall through to directory check */ }
            try { await parent.getDirectoryHandle(leaf); return true; }
            catch { return false; }
        } catch {
            return false;
        }
    }

    /** Move a file or directory. OPFS has no atomic rename. Files are
     *  copied + the source removed. Directories are walked recursively.
     *  `newPath === oldPath` is a no-op. Refuses to overwrite an
     *  existing destination. fade.json is locked. */
    async rename(oldPath: string, newPath: string): Promise<void> {
        if (oldPath === FADE_JSON_NAME || newPath === FADE_JSON_NAME) {
            throw new Error('fade.json is required and cannot be renamed.');
        }
        if (oldPath === newPath) return;
        // Validate every path segment.
        for (const seg of newPath.split('/')) {
            if (seg.length === 0) throw new Error('Invalid path (empty segment).');
            if (!/^[\w.\-]+$/.test(seg)) {
                throw new Error(`Invalid path segment "${seg}". Letters, digits, dot, dash, underscore only.`);
            }
        }
        // Block moving a directory INTO itself or its own children.
        if (newPath === oldPath || newPath.startsWith(oldPath + '/')) {
            throw new Error(`Can't move "${oldPath}" into its own subtree.`);
        }
        if (await this.exists(newPath)) {
            throw new Error(`"${newPath}" already exists.`);
        }
        const wasDir = await this.isDirectory(oldPath);
        if (wasDir) {
            // Recursively walk + copy + delete. Build a manifest first
            // so a mid-flight error doesn't leave us with half-copied
            // half-deleted state — we copy everything, then delete the
            // source tree.
            const entries: Array<{ from: string; to: string }> = [];
            const walk = async (relFrom: string, relTo: string) => {
                const fromHandle = await this.dirHandleAt(relFrom);
                for await (const entry of (fromHandle as any).values() as AsyncIterable<FileSystemHandle>) {
                    const subFrom = `${relFrom}/${entry.name}`;
                    const subTo = `${relTo}/${entry.name}`;
                    if (entry.kind === 'file') {
                        entries.push({ from: subFrom, to: subTo });
                    } else {
                        await walk(subFrom, subTo);
                    }
                }
            };
            await this.mkdir(newPath);
            await walk(oldPath, newPath);
            for (const { from, to } of entries) {
                const bytes = await this.readBytes(from);
                await this.writeBytes(to, bytes);
            }
            try { await this.delete(oldPath); }
            catch (e) { console.warn('[fade] rename: failed to remove source dir', oldPath, e); }
            return;
        }
        // File move.
        const bytes = await this.readBytes(oldPath);
        await this.writeBytes(newPath, bytes);
        try { await this.delete(oldPath); }
        catch (e) {
            console.warn('[fade] rename: failed to remove old file', oldPath, e);
        }
    }

    /** Resolve a directory handle at a slashed path (relative to the
     *  project root). Errors if any segment is missing. */
    private async dirHandleAt(path: string): Promise<FileSystemDirectoryHandle> {
        const segments = path.split('/').filter((s) => s.length > 0);
        let cur: FileSystemDirectoryHandle = this.dir;
        for (const seg of segments) {
            cur = await cur.getDirectoryHandle(seg);
        }
        return cur;
    }
}

// ─── Tabs + model management ────────────────────────────────────────────────
interface Tab {
    name: string;
    model: monaco.editor.ITextModel;
    dirty: boolean;
    saveTimer?: number;
    /** Disposable for the sharing gutter decorator. Cleared when the tab is
     *  closed; safely no-op if sharing wasn't ready when the tab opened. */
    gutterHandle?: GutterHandle;
}

// ── Live-session transient project helpers ───────────────────────────────
// Guests join a session into a sandboxed OPFS project so the host's workspace
// can be mirrored without polluting any of the guest's real projects. The
// prefix is reserved — `cleanupLiveSessionProjects` wipes anything matching
// it on app boot to recover from a mid-session reload.
const LIVE_SESSION_PROJECT_PREFIX = '__live_session_';
function liveSessionProjectName(roomId: string): string {
    return `${LIVE_SESSION_PROJECT_PREFIX}${roomId}__`;
}
function isLiveSessionProjectName(name: string): boolean {
    return name.startsWith(LIVE_SESSION_PROJECT_PREFIX);
}
/** Remove a project directory from OPFS by reaching into navigator.storage
 *  directly — OpfsWorkspace doesn't expose deleteProject. Used on guest
 *  leave (clean exit) and on app startup (crash recovery for any
 *  `__live_session_*` projects left behind by a previous reload). */
async function deleteOpfsProject(name: string): Promise<void> {
    const opfs = await navigator.storage.getDirectory();
    const workspaceRoot = await opfs.getDirectoryHandle('workspace');
    try {
        await workspaceRoot.removeEntry(name, { recursive: true });
    } catch (e) {
        // NotFoundError is fine — the project might already be gone.
        if ((e as any)?.name !== 'NotFoundError') throw e;
    }
}

const tabs = new Map<string, Tab>();
let activeName: string | null = null;
let editor: monaco.editor.IStandaloneCodeEditor | null = null;

// Active-file change listeners — fire whenever `activeName` is mutated.
// Used by the live-session feature so its Y.Doc/MonacoBinding can follow
// the currently-edited tab. All updates to `activeName` should go through
// `setActiveName` so the listeners stay in sync.
const activeFileListeners = new Set<(name: string | null) => void>();
function setActiveName(name: string | null) {
    if (activeName === name) return;
    activeName = name;
    for (const cb of activeFileListeners) {
        try { cb(name); } catch (e) { console.warn('[fade] activeFile listener threw', e); }
    }
    // Tell the shared-cursor module the active file changed so it can
    // re-evaluate which remote cursors render in the editor vs become
    // tab badges instead.
    try { sharedCursorHandle?.notifyActiveFileChanged(); }
    catch { /* ignore — module may be torn down */ }
}
let liveSessionHandle: LiveSessionHandle | null = null;
// Module-scope state for the shared-cursor system. `sharedCursorHandle`
// is rebuilt per session; `currentPeerFilePresence` is the latest
// per-file map of { color, names } the receiver pushed in. Both the
// editor tab strip and the workspace file list consult this on each
// repaint to decorate their rows.
let sharedCursorHandle: SharedCursorHandle | null = null;
let currentPeerFilePresence: Map<string, { color: string; names: string[] }> = new Map();
function applyPeerFilePresence(m: Map<string, { color: string; names: string[] }>) {
    currentPeerFilePresence = m;
    repaintPeerPresenceDots();
}
/** Apply per-file presence dots to BOTH the editor tab strip and the
 *  workspace file list. Tab strip skips the user's currently-active
 *  file (their own cursor is shown instead). Workspace shows every
 *  file someone else is editing so the user can spot peer activity
 *  even on files they don't have open. */
function repaintPeerPresenceDots() {
    paintPresenceDotsIn(document.getElementById('tabs'), { skipActive: true });
    paintPresenceDotsIn(document.getElementById('file-list'), { skipActive: false });
}
function paintPresenceDotsIn(root: HTMLElement | null, opts: { skipActive: boolean }) {
    if (!root) return;
    const seen = new Set<HTMLElement>();
    for (const [file, entry] of currentPeerFilePresence) {
        if (opts.skipActive && file === activeName) continue;
        const row = root.querySelector<HTMLElement>(`[data-name="${cssEscape(file)}"]`);
        if (!row) continue;
        let dot = row.querySelector<HTMLElement>('.fade-tab-peer-dot');
        if (!dot) {
            dot = document.createElement('span');
            dot.className = 'fade-tab-peer-dot';
            row.appendChild(dot);
        }
        dot.style.backgroundColor = entry.color;
        dot.title = entry.names.length === 1
            ? `${entry.names[0]} is editing ${file}`
            : `${entry.names.join(', ')} are editing ${file}`;
        seen.add(dot);
    }
    root.querySelectorAll<HTMLElement>('.fade-tab-peer-dot').forEach((dot) => {
        if (!seen.has(dot)) dot.remove();
    });
}
function cssEscape(s: string): string {
    // Minimal escape for CSS attribute selector — \ and " are the
    // only meta characters relevant to `[attr="value"]`.
    return s.replace(/\\/g, '\\\\').replace(/"/g, '\\"');
}

/**
 * Force every pending 600ms-debounced autosave to land *now*. Used by the
 * commit flow to guarantee the working tree on disk reflects the editor
 * before we snapshot it — otherwise an in-flight edit could be silently
 * excluded from the commit. Takes `workspace` as a parameter to match the
 * `renderFileList` / `openFile` pattern (workspace lives in bootstrap scope).
 */
/** True iff any open Monaco tab has unflushed edits. The sharing
 *  panel's "Save" button enables on the first keystroke via this
 *  signal — without it the button stays greyed out for ~600 ms while
 *  the autosave debounce waits to write through to OPFS. */
function anyTabDirty(): boolean {
    for (const tab of tabs.values()) if (tab.dirty) return true;
    return false;
}

async function flushPendingSaves(workspace: OpfsWorkspace): Promise<void> {
    const promises: Promise<void>[] = [];
    const flushedNames: string[] = [];
    for (const tab of tabs.values()) {
        if (!tab.dirty) continue;
        if (tab.saveTimer != null) {
            clearTimeout(tab.saveTimer);
            tab.saveTimer = undefined;
        }
        const name = tab.name;
        const value = tab.model.getValue();
        flushedNames.push(name);
        promises.push(
            workspace.write(name, value).then(() => { tab.dirty = false; }),
        );
    }
    if (promises.length) {
        await Promise.all(promises);
        // Mirror the debounced-autosave path: invalidate the sharing panel's
        // hash cache for every file we just wrote. Without this the panel
        // keeps the pre-edit blob sha and a Save right after typing reports
        // the file as still "modified" against the just-captured snapshot.
        for (const name of flushedNames) {
            sharingController?.invalidateHashFor(name);
        }
        // All flushed tabs are now clean — drop the dirty-tabs signal so
        // the Save button's enabled state lines up with what's actually
        // on disk again.
        if (!anyTabDirty()) sharingController?.setHasDirtyTabs(false);
        renderTabs();
    }
}

// Resolve the requested theme id to a concrete preset (handles 'auto'), then
// push the three layers — CSS palette, Monaco, dockview — in lockstep.
function applyTheme(state: SettingsState): void {
    const requested = String(state.effective['ui.theme'] ?? 'dark');
    const preset = resolveTheme(requested);
    document.documentElement.dataset.theme = preset.id;
    try { monaco.editor.setTheme(preset.monaco); }
    catch { /* called before defineTheme on first render — onSettingsChange replays after */ }
    // Dockview ships its own theme classes; swap them in lockstep so the
    // splitters/tabs match the rest of the UI. `__fadeDockview` is set during
    // setupDockview() — guard for the early-boot call that fires before
    // dockview is constructed.
    const dock = (window as any).__fadeDockview as { updateOptions?: (o: any) => void } | undefined;
    try {
        dock?.updateOptions?.({
            theme: { name: preset.id, className: preset.dockview },
        });
    } catch { /* dockview not ready yet */ }
}

// Maps the flat settings dictionary onto Monaco's editor options. Called
// at editor.create() and again whenever settings change. tabSize /
// insertSpaces are model options (applied separately by the listener).
function editorOptionsFromSettings(state: SettingsState): monaco.editor.IEditorOptions {
    const eff = state.effective;
    const lineHeight = Number(eff['editor.lineHeight'] ?? 0);
    return {
        fontSize: Number(eff['editor.fontSize'] ?? 14),
        fontFamily: String(eff['editor.fontFamily'] ?? ''),
        lineHeight: lineHeight > 0 ? lineHeight : undefined,
        minimap: { enabled: Boolean(eff['editor.minimap'] ?? false) },
        wordWrap: (eff['editor.wordWrap'] as 'off' | 'on' | 'bounded') ?? 'off',
        renderWhitespace: (eff['editor.renderWhitespace'] as
            'none' | 'boundary' | 'selection' | 'all') ?? 'none',
        lineNumbers: (eff['editor.lineNumbers'] ?? true) ? 'on' : 'off',
    } as monaco.editor.IEditorOptions;
}

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
            registerVirtualFile(uri, text);
        }
        let model = monaco.editor.getModel(uri);
        if (!model) {
            model = monaco.editor.createModel(text, languageFor(name), uri);
            // Seed tab settings from the current effective config so the
            // brand-new model lines up with the editor's existing tabs.
            const eff = currentSettings().effective;
            model.updateOptions({
                tabSize: Number(eff['editor.tabSize'] ?? 2),
                insertSpaces: Boolean(eff['editor.insertSpaces'] ?? true),
            });
        }
        // .fx-specific live validator — runs the FX parser + HLSL
        // translator + glslang on each model change (debounced) and
        // surfaces anything they complain about as Monaco markers.
        // Hook runs unconditionally per openFile call: a previously-open
        // model that was created BEFORE the validator existed needs to
        // get hooked the next time the file's opened, otherwise editing
        // it would produce no markers. attachShaderValidator is
        // idempotent — multiple calls on the same model just trigger an
        // immediate validation pass.
        if (model.getLanguageId() === 'fadefx') {
            console.log('[shader-validator] attaching to', name, 'uri=', uri.toString());
            attachShaderValidator(model);
        }
        // Hook this model for LSP push + decoration (if available)
        (window as any).__fadeHookModel?.(model);
        tab = { name, model, dirty: false };
        // Source-control gutter: paint per-line markers showing what changed
        // vs. the last synced commit. Lives on the model (not the editor) so
        // tab-switches don't lose the decorations. Disposes itself when the
        // model is gone or via tab cleanup; safe to skip if sharing isn't set
        // up yet (gets attached lazily on next open of the same file).
        if (sharingController) {
            const handle = attachGutter({
                model,
                getSavedText: () => sharingController!.getSavedText(name),
                getPublishedText: () => sharingController!.getPublishedText(name),
                onShouldRefresh: (cb) => sharingController!.onStatusChange(() => cb()),
            });
            tab.gutterHandle = handle;
        }
        // Debounced auto-save: 600ms idle → write to OPFS
        model.onDidChangeContent(() => {
            tab!.dirty = true;
            // Surface the unflushed-edit state to the sharing panel
            // immediately so its Save button enables on the very first
            // keystroke (rather than after the 600 ms autosave debounce
            // + a refreshStatus round-trip).
            sharingController?.setHasDirtyTabs(true);
            clearTimeout(tab!.saveTimer);
            const debounceMs = Number(currentSettings().effective['autosave.debounceMs'] ?? 600);
            tab!.saveTimer = window.setTimeout(async () => {
                try {
                    await workspace.write(tab!.name, tab!.model.getValue());
                    tab!.dirty = false;
                    if (!anyTabDirty()) sharingController?.setHasDirtyTabs(false);
                    renderTabs();
                    // Lightweight per-file refresh — invalidates just this
                    // path's cached hash. The other ~N-1 files in the
                    // workspace stay cached, which is the difference
                    // between hashing every file every debounce window and
                    // hashing only the file the user just typed in.
                    void sharingController?.refreshStatusForFile(tab!.name);
                } catch (e) {
                    console.error('[fade] save failed for', tab!.name, e);
                }
            }, debounceMs);
            renderTabs();
        });
        tabs.set(name, tab);
        // Notify the live-session (if hosting) that a new file is open so
        // it can add a Y.Text for this path. Safe to call when no session
        // is running — the session method is a no-op for guests and idle.
        liveSessionHandle?.getSession()?.notifyFileOpened(name);
    }
    setActiveName(name);
    if (editor) {
        editor.setModel(tab.model);
        // Ensure the Editor dockview tab itself is active — otherwise
        // `editor.focus()` is a no-op (Monaco can't take focus while
        // the panel containing it is hidden behind another tab).
        // Common scenario: user is on Live Session / Logs / Debug UI
        // tab and clicks a file in the workspace list; we want them
        // jumped over to the editor view as part of the click.
        try {
            const dock = (window as any).__fadeDockview;
            const panel = dock?.getPanel?.('editor');
            if (panel && !panel.api.isActive) panel.api.setActive();
        } catch { /* ignore — dockview not ready or panel gone */ }
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
    // Mirror the open path: tell the live-session this file is gone so
    // hosts can drop the Y.Text and guests stop seeing it.
    liveSessionHandle?.getSession()?.notifyFileClosed(name);
    if (activeName === name) {
        // Switch to another tab or empty state
        const next = tabs.keys().next().value;
        if (next) {
            setActiveName(next);
            if (editor) editor.setModel(tabs.get(next)!.model);
        } else {
            setActiveName(null);
            if (editor) editor.setModel(null);
            editorContainer.style.display = 'none';
            editorPlaceholder.style.display = 'flex';
        }
    }
    renderTabs();
}

function renderTabs() {
    tabsEl.innerHTML = '';
    // Tab labels show the basename so deeply-nested files don't blow
    // out the tab strip; the full path is in the tooltip for
    // disambiguation when multiple files share a name across folders.
    for (const [name, tab] of tabs) {
        const basename = name.split('/').pop() ?? name;
        const el = document.createElement('div');
        el.className = 'tab' + (name === activeName ? ' active' : '');
        // Used by shared-cursor's tab-badge repaint to locate the
        // right tab element via attribute selector.
        el.dataset.name = name;
        const label = document.createElement('span');
        label.className = tab.dirty ? 'dirty' : '';
        label.textContent = (tab.dirty ? '● ' : '') + basename;
        label.title = name;
        label.onclick = () => {
            setActiveName(name);
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
        // Right-click menu: focus / close / close-others / close-to-side / close-all.
        // Mirrors the pattern used for file-list rows.
        el.addEventListener('contextmenu', (e) => {
            e.preventDefault();
            showTabContextMenu(e.clientX, e.clientY, name);
        });
        tabsEl.append(el);
    }
    // Re-apply the live-session per-tab peer dots — renderTabs() blew
    // them away with `innerHTML = ''` so we reattach from the cached
    // map. Cheap selector pass; no badges to apply in solo mode.
    repaintPeerPresenceDots();
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

// Catalog panel is a singleton — one tab, persistent across opens. The
// CatalogClient holds the manifest + IDB cache in module scope so reopening
// the panel after closing it is instant (no refetch). Panel id matches the
// component name so the View menu's openPanelById flow works uniformly.
const CATALOG_PANEL_ID = 'catalog';
const sharedCatalogClient = new CatalogClient();

function openCatalogPanel() {
    const api = (window as any).__fadeDockview;
    if (!api) return;
    const existing = api.getPanel?.(CATALOG_PANEL_ID);
    if (existing) { existing.api.setActive(); return; }
    api.addPanel({
        id: CATALOG_PANEL_ID,
        component: 'catalog',
        title: 'Catalog',
        position: { referencePanel: 'editor', direction: 'within' },
    });
}

// Binary-file preview lives in ONE shared "Asset Preview" tab — clicking
// a different .xnb / image / sound swaps the contents of that single tab
// rather than spawning one panel per file. Mirrors VSCode preview-tab
// behavior. The tab title is intentionally static ("Asset Preview") so it
// reads as "this slot is the preview" rather than "this is a tab per file";
// the actual filename is shown inside the panel's own toolbar.
const ASSET_PREVIEW_TAB_TITLE = 'Asset Preview';
function openBinaryPreview(filename: string) {
    const api = (window as any).__fadeDockview;
    if (!api) return;
    const existing = api.getPanel?.(BINARY_PREVIEW_PANEL_ID);
    if (existing) {
        existing.api.updateParameters({ filename });
        existing.api.setTitle?.(ASSET_PREVIEW_TAB_TITLE);
        existing.api.setActive();
        return;
    }
    api.addPanel({
        id: BINARY_PREVIEW_PANEL_ID,
        component: 'binary-preview',
        title: ASSET_PREVIEW_TAB_TITLE,
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
    /** Create an empty folder at the given path (root-relative,
     *  slashes allowed for nested creation). No-op if it exists. */
    createFolder(path: string): Promise<void>;
    /** Move a file or folder. Works with slashed paths; folders move
     *  recursively. Updates fade.json sources if the moved path is
     *  listed (or any descendant is listed, for folder moves). */
    renamePath(oldPath: string, newPath: string): Promise<void>;
    /** Inline-create a new file with the given extension. If
     *  `parentFolder` is set, the file lands inside it (and the
     *  folder is auto-expanded). Triggered from the folder right-
     *  click menu; the inline-edit row UI lives in the bootstrap
     *  closure, so this exposes it through the projectOps surface. */
    inlineCreateFile(ext: string, parentFolder?: string): void;
    /** Inline-create a new folder, optionally nested inside
     *  `parentFolder`. Same pattern as `inlineCreateFile`. */
    inlineCreateFolder(parentFolder?: string): void;
}
let projectOps: ProjectOps | null = null;

/** File extensions offered in the "New …" menus (both root-area and
 *  folder-row right-clicks). Module-scoped so the folder-context menu
 *  — which lives at module scope, outside the bootstrap closure —
 *  can iterate the same list as the root-area menu and the new-file
 *  button. Add new types here and they'll appear in every menu. */
const NEW_FILE_EXTENSIONS: ReadonlyArray<{ label: string; ext: string }> = [
    { label: 'Fade source (.fbasic)', ext: 'fbasic' },
    { label: 'Shader (.fx)',          ext: 'fx' },
    { label: 'JSON (.json)',          ext: 'json' },
    { label: 'Text (.txt)',           ext: 'txt' },
];

// Starter content placed in each newly-created `.fx` file. PS-only —
// MatrixTransform at file scope (the translator folds it into the
// synthetic `_TopLevelUniforms` cbuffer) and no MainVS (compile-fx
// auto-injects a default VS for any pass whose vsShaderIndex is -1,
// mirroring what `MonoGame.Effect.Compiler.exe` does). Both code paths
// route MatrixTransform to the right GL uniform via name-dedup, so the
// user gets a working pass-through sprite shader with the absolute
// minimum boilerplate.
const NEW_FX_TEMPLATE = `#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

float4x4 MatrixTransform;

struct PSInput
{
    float4 Position : POSITION;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

float4 MainPS(PSInput input) : COLOR
{
    float4 c = tex2D(SpriteTextureSampler, input.TexCoord) * input.Color;
    return float4(c.rgb, c.a);
}

technique MainTechnique
{
    pass P0
    {
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
};
`;

// Pick the seed content for a newly-created file. Returns '' for any
// extension we don't have a starter for, matching the prior behavior.
function templateForExtension(ext: string): string {
    if (ext === 'fx') return NEW_FX_TEMPLATE;
    return '';
}

// Source-control wiring: the panel mounts at bootstrap (after dockview is up)
// and publishes a status map (path → A/M/D) via onStatusChange. We mirror it
// here so renderFileList can render badges without coupling back into the
// panel module. Same idea for pendingPullPaths — paths whose remote-head
// version differs from our last synced state, surfaced as a ↓ badge.
let sharingController: CollaborationController | null = null;
let sharingStatus: Map<string, FileStatus> = new Map();
let sharingPendingPull: Set<string> = new Set();
/** Files currently in conflict (text-with-markers or binary-conflict-copy
 *  exists). Drives the red 'C' badge in the workspace file list. */
let sharingConflicts: { text: Set<string>; binary: Set<string> } = { text: new Set(), binary: new Set() };

/** Collapsed folder paths. Renders skip files whose parent (at any
 *  level) is in this set. In-memory only — folder state resets on
 *  reload, which keeps the UI predictable across sessions. */
const collapsedFolders = new Set<string>();

/** MIME type for internal drag-drop. Distinct from `text/plain` so we
 *  never confuse a workspace move with an external drop (file content
 *  from the OS, a URL string, etc.). */
const FADE_DRAG_MIME = 'application/x-fade-path';

/** Compute the destination path when `srcPath` is dropped onto
 *  `destFolder`. `destFolder === ''` means the workspace root. Returns
 *  null if the drop is a no-op (already in that folder) or invalid
 *  (would move into its own subtree). */
function computeDropTarget(srcPath: string, destFolder: string): string | null {
    const basename = srcPath.split('/').pop() ?? srcPath;
    const target = destFolder ? `${destFolder}/${basename}` : basename;
    if (target === srcPath) return null;
    // Block moving a folder into itself or a descendant.
    if (destFolder === srcPath || destFolder.startsWith(srcPath + '/')) return null;
    return target;
}

/** Clear the drop-target highlight from every row. Called on dragend,
 *  drop, and any time the user leaves a target without committing. */
function clearDropHighlights() {
    for (const el of fileListEl.querySelectorAll('.drop-target')) {
        el.classList.remove('drop-target');
    }
    fileListEl.classList.remove('drop-target');
}

/** Wire a row as a drag source. Sets the transfer data to the row's
 *  path so the drop handler knows what's being moved. fade.json is
 *  refused (it's pinned to root). */
function wireDragSource(li: HTMLElement, path: string) {
    if (path === FADE_JSON_NAME) return;
    li.draggable = true;
    li.addEventListener('dragstart', (e) => {
        if (!e.dataTransfer) return;
        e.dataTransfer.setData(FADE_DRAG_MIME, path);
        e.dataTransfer.effectAllowed = 'move';
    });
    li.addEventListener('dragend', () => clearDropHighlights());
}

/** Wire a row (or the root) as a drop target. `destFolder === ''` for
 *  the root; otherwise the folder's path. Uses computeDropTarget to
 *  decide whether the drop is valid before highlighting. */
function wireDropTarget(el: HTMLElement, destFolder: string) {
    el.addEventListener('dragover', (e) => {
        const srcPath = e.dataTransfer?.types.includes(FADE_DRAG_MIME)
            ? '' // we'll read the actual value on drop; just check the type for dragover
            : null;
        if (srcPath === null) return;
        // We can't read getData during dragover (browsers redact it for
        // security). So we accept the drag if the type is right and
        // let the drop handler do the real validation.
        e.preventDefault();
        if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
        el.classList.add('drop-target');
    });
    el.addEventListener('dragleave', (e) => {
        // dragleave fires when crossing child boundaries too. Only
        // clear if the pointer actually left the element's bounds.
        if (e.currentTarget === el && !(el as Node).contains(e.relatedTarget as Node)) {
            el.classList.remove('drop-target');
        }
    });
    el.addEventListener('drop', (e) => {
        e.preventDefault();
        // Stop the event before it bubbles to the root drop handler
        // (`fileListEl`) — otherwise dropping on a folder would also
        // trigger a second move-to-root pass with an already-moved
        // source. Safe to call on the root too; it has no parent
        // listener of consequence.
        e.stopPropagation();
        el.classList.remove('drop-target');
        clearDropHighlights();
        const srcPath = e.dataTransfer?.getData(FADE_DRAG_MIME);
        if (!srcPath) return;
        const target = computeDropTarget(srcPath, destFolder);
        if (!target) return;
        void projectOps?.renamePath(srcPath, target);
    });
}

/** True iff any ancestor of `path` is in `collapsedFolders`. */
function isHiddenByCollapsedAncestor(path: string): boolean {
    // For a/b/c.fbasic, the ancestors are 'a' and 'a/b'. We don't check
    // the full path itself — a collapsed folder still renders its own
    // row, just not children.
    const parts = path.split('/');
    if (parts.length < 2) return false;
    let prefix = '';
    for (let i = 0; i < parts.length - 1; i++) {
        prefix = prefix ? `${prefix}/${parts[i]}` : parts[i];
        if (collapsedFolders.has(prefix)) return true;
    }
    return false;
}

/** Wire the file-list root as a drop target ONCE on first render.
 *  Drops here move the file to the workspace root. Using a flag (vs
 *  binding on every render) keeps duplicate listeners from stacking. */
let fileListRootDropWired = false;

async function renderFileList(workspace: OpfsWorkspace) {
    const entries = await workspace.listEntries();
    fileListEl.innerHTML = '';
    if (!fileListRootDropWired) {
        // Drop on empty space inside the file list → move to root.
        // Folder rows stop propagation on their own drop handlers so
        // a drop on a folder doesn't also fire here.
        wireDropTarget(fileListEl, '');
        fileListRootDropWired = true;
    }
    const sources = currentProjectRef?.sources ?? [];
    for (const entry of entries) {
        const { path, kind } = entry;
        // Hide the asset cache from the file tree. It's a managed
        // implementation detail of compileImageAssets — surfacing it would
        // tempt users to edit/delete blobs by hand and confuse the GC.
        if (path === '.fade-cache' || path.startsWith('.fade-cache/')) continue;
        // Skip rows whose parent folder is collapsed. The folder itself
        // still renders (its row IS what the user expands/collapses).
        if (isHiddenByCollapsedAncestor(path)) continue;
        const depth = path.split('/').length - 1;
        const li = document.createElement('li');
        li.dataset.name = path;
        // Pad each row by depth * 14px so children sit visibly under
        // their folder. The base padding (1rem) is preserved.
        if (depth > 0) li.style.paddingLeft = `calc(1rem + ${depth * 14}px)`;

        if (kind === 'directory') {
            // ── Folder row ────────────────────────────────────────────
            li.classList.add('folder-row');
            li.dataset.folder = path;
            const collapsed = collapsedFolders.has(path);
            const chevron = document.createElement('span');
            chevron.className = 'folder-chevron';
            chevron.textContent = collapsed ? '▶' : '▼';
            const icon = document.createElement('span');
            icon.className = collapsed
                ? 'codicon codicon-folder'
                : 'codicon codicon-folder-opened';
            const label = document.createElement('span');
            label.className = 'file-label';
            // Show only the basename — the indent + position in the
            // tree communicate the parent. Tooltip has the full path
            // for disambiguation.
            label.textContent = path.split('/').pop() ?? path;
            label.title = path;
            li.append(chevron, icon, label);
            li.addEventListener('contextmenu', (e) => {
                e.preventDefault();
                showFolderContextMenu(e.clientX, e.clientY, path);
            });
            li.onclick = () => {
                if (collapsedFolders.has(path)) collapsedFolders.delete(path);
                else collapsedFolders.add(path);
                void renderFileList(workspace);
            };
            // Folders are both drag sources (move the folder) and drop
            // targets (move items into this folder).
            wireDragSource(li, path);
            wireDropTarget(li, path);
            fileListEl.append(li);
            continue;
        }

        // ── File row ──────────────────────────────────────────────────
        li.classList.add('file-row');
        const name = path; // legacy code below uses `name` for both display + sources lookup
        const indent = document.createElement('span');
        indent.className = 'file-indent';
        li.append(indent);
        const label = document.createElement('span');
        label.className = 'file-label';
        label.textContent = path.split('/').pop() ?? path;
        label.title = path;
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
        // Source-control status badge (A/M/D) when the workspace is bound to
        // a repo. Empty / unchanged files get no badge to keep the file list
        // visually quiet.
        const scStatus = sharingStatus.get(name);
        if (scStatus && scStatus !== 'unchanged') {
            const scBadge = document.createElement('span');
            scBadge.className = `sharing-status sharing-${scStatus}`;
            scBadge.textContent = statusGlyph(scStatus);
            scBadge.title = scStatus.charAt(0).toUpperCase() + scStatus.slice(1);
            li.append(scBadge);
        }
        // "Remote has changes for this file" badge — surfaced when the
        // poll loop has detected the upstream branch moved and the new
        // tree differs from our last synced base for this path.
        if (sharingPendingPull.has(name)) {
            const pull = document.createElement('span');
            pull.className = 'sharing-status sharing-pending-pull';
            pull.textContent = '↓';
            pull.title = 'Remote has changes for this file. Click Pull in the Source Control panel to fetch.';
            li.append(pull);
        }
        // Conflict badge — distinct red 'C' for files mid-merge. Covers
        // both text (markers in the file) and binary (a sibling
        // `<name>.fade-conflict.<sha>` exists). Clicking the badge opens
        // the merge editor for text conflicts; binary points at the
        // Source Control panel's binary section.
        const isTextConflict = sharingConflicts.text.has(name);
        // For binary: the set holds the full conflict-copy filename. A
        // base file has a binary conflict if ANY entry in the set starts
        // with `<name>.fade-conflict.`. Bounded by the set size (typically
        // 0–1 entries).
        const conflictCopyPrefix = `${name}.fade-conflict.`;
        let isBinaryConflict = false;
        for (const cf of sharingConflicts.binary) {
            if (cf.startsWith(conflictCopyPrefix)) { isBinaryConflict = true; break; }
        }
        if (isTextConflict || isBinaryConflict) {
            const conf = document.createElement('span');
            conf.className = 'sharing-status sharing-conflict';
            conf.textContent = 'C';
            conf.title = isTextConflict
                ? 'Merge conflict in progress. Click to open the merge editor.'
                : 'Binary conflict — a remote-version sibling file exists. Resolve via Source Control.';
            if (isTextConflict) {
                conf.style.cursor = 'pointer';
                conf.addEventListener('click', (e) => {
                    e.stopPropagation();
                    sharingController?.openConflictEditor(name);
                });
            }
            li.append(conf);
        }
        // Conflict-copy file itself (.fade-conflict.<sha> sibling) — show
        // a special "(remote copy)" label so it's visually distinct from
        // normal files in the list.
        if (/\.fade-conflict\.[a-f0-9]+$/i.test(name)) {
            li.classList.add('fade-conflict-sibling');
            const tag = document.createElement('span');
            tag.className = 'sharing-status sharing-conflict-sibling';
            tag.textContent = 'remote';
            tag.title = 'This is the REMOTE side of a binary conflict. The base file (without the .fade-conflict suffix) is your local version. Resolve via the Source Control panel.';
            li.append(tag);
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
        // File rows are drag sources only. Drop on a file isn't
        // meaningful (we don't open arbitrary file-to-file drops);
        // dropping on a folder OR the root area is how moves happen.
        wireDragSource(li, name);
        fileListEl.append(li);
    }
    // After a re-render the per-file peer dots are gone — reattach
    // from the cached presence map. Same selector pattern as the
    // editor tab strip.
    repaintPeerPresenceDots();
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

// Right-click menu for an editor tab. Reuses the source-badge-menu chrome
// for visual consistency with the file/folder context menus. The actions
// operate on the `tabs` Map insertion order — that's the same order
// renderTabs uses to lay out the tab strip, so "left of" / "right of"
// matches what the user sees.
function showTabContextMenu(x: number, y: number, name: string) {
    closeAnyFileMenu();
    if (!tabs.has(name)) return;

    const order = Array.from(tabs.keys());
    const idx = order.indexOf(name);
    const leftCount = idx;
    const rightCount = order.length - idx - 1;
    const otherCount = order.length - 1;
    const isActive = activeName === name;

    const menu = document.createElement('div');
    menu.className = 'source-badge-menu';
    // Reuse the file-context data attribute so closeAnyFileMenu tears
    // either flavor down — they're functionally the same floating popup.
    menu.dataset.menu = 'file-context';

    const addItem = (label: string, handler: () => void, opts?: { disabled?: boolean }) => {
        const item = document.createElement('button');
        item.className = 'source-badge-item';
        item.type = 'button';
        item.textContent = label;
        if (opts?.disabled) item.disabled = true;
        item.onclick = (e) => {
            e.stopPropagation();
            if (item.disabled) return;
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

    const focusTab = (target: string) => {
        const tab = tabs.get(target);
        if (!tab) return;
        setActiveName(target);
        if (editor) editor.setModel(tab.model);
        renderTabs();
        renderFileListSelection();
    };

    addItem('Focus tab', () => focusTab(name), { disabled: isActive });
    addSeparator();
    addItem(`Close "${name.split('/').pop() ?? name}"`, () => closeTab(name));
    addItem('Close others', () => {
        for (const other of order) if (other !== name) closeTab(other);
    }, { disabled: otherCount === 0 });
    addItem('Close tabs to the left', () => {
        for (const other of order.slice(0, idx)) closeTab(other);
    }, { disabled: leftCount === 0 });
    addItem('Close tabs to the right', () => {
        for (const other of order.slice(idx + 1)) closeTab(other);
    }, { disabled: rightCount === 0 });
    addSeparator();
    addItem('Close all', () => {
        for (const other of order) closeTab(other);
    });

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

/** Right-click menu for a folder row. Smaller than the file menu —
 *  folders don't participate in fade.json sources or git status the
 *  same way. Rename (drag-drop preferred, but typed-rename available
 *  for awkward characters) + Delete. */
function showFolderContextMenu(x: number, y: number, folderPath: string) {
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
    // Context header so the user can see WHERE the create actions
    // will land — keeps "New JSON file" from feeling rootless when
    // four folders share a context menu shape.
    const ctx = document.createElement('div');
    ctx.className = 'source-badge-sep-label';
    ctx.textContent = `in ${folderPath}`;
    menu.append(ctx);
    // "Create inside this folder" — full extension list, same shape
    // as the root-area menu. Mirrors NEW_FILE_EXTENSIONS so users
    // never have to learn two different menu vocabularies.
    addItem('New folder…', () => {
        projectOps!.inlineCreateFolder(folderPath);
    });
    const sepAfterFolder = document.createElement('div');
    sepAfterFolder.className = 'source-badge-sep';
    menu.append(sepAfterFolder);
    for (const { label, ext } of NEW_FILE_EXTENSIONS) {
        addItem(label, () => projectOps!.inlineCreateFile(ext, folderPath));
    }
    // Separator between create-actions and modify-actions.
    const sep = document.createElement('div');
    sep.className = 'source-badge-sep';
    menu.append(sep);
    addItem('Rename folder…', () => {
        const newPath = window.prompt(`Rename "${folderPath}" to:`, folderPath);
        if (!newPath || newPath === folderPath) return;
        void projectOps!.renamePath(folderPath, newPath.trim())
            .catch((e: unknown) => console.warn('[fade] rename folder failed', e));
    });
    addItem('Delete folder…', () => {
        if (!confirm(`Delete "${folderPath}" and ALL its contents? This can't be undone.`)) return;
        void projectOps!.deleteFile(folderPath)
            .catch((e: unknown) => console.warn('[fade] delete folder failed', e));
    });
    menu.style.position = 'fixed';
    menu.style.left = `${x}px`;
    menu.style.top = `${y}px`;
    document.body.append(menu);
    setTimeout(() => {
        const onClick = (e: MouseEvent) => {
            if (!menu.contains(e.target as Node)) closeAnyFileMenu();
        };
        const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') closeAnyFileMenu(); };
        document.addEventListener('click', onClick, true);
        document.addEventListener('keydown', onKey, true);
        (menu as any).__cleanup = () => {
            document.removeEventListener('click', onClick, true);
            document.removeEventListener('keydown', onKey, true);
        };
    }, 0);
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
    // One worker for LSP traffic — never executes user code, stays
    // responsive while the VM does its thing. The VM itself lives in
    // the active template's iframe (on the iframe's main thread); the
    // Runner posts VM-side messages to iframe.contentWindow via the
    // postVm helper. attachVmIframe() points the runner at an iframe;
    // before that, VM-side calls that need a target are skipped (only
    // LSP is meaningful pre-iframe).
    public lspWorker: Worker;
    /** Back-compat alias — old code referenced runner.worker for raw access. */
    public get worker(): Worker { return this.lspWorker; }
    private vmTarget: Window | null = null;
    private vmIframe: HTMLIFrameElement | null = null;
    private opts: RunnerOpts;
    private nextId = 0;
    private pending = new Map<number, (result: any) => void>();
    private onDiagnostics?: (uri: string, diagnostics: Diagnostic[]) => void;
    // Page-side handler registry for the cooperative pump's host-message
    // protocol. Library commands call HostBridge.PostMessage(channel, payload)
    // in C#; the runtime forwards as { type: 'host-message', channel, payload }.
    // We dispatch by channel name to a registered handler that returns
    // { resultType, value }, post that back as 'host-reply', and the runtime's
    // generic dispatcher does the placeholder swap. Plugin authors register
    // their own channels at runtime via registerHostHandler.
    private hostHandlers: Record<string, (payload: string) =>
        Promise<{ resultType: string; value?: any }> | { resultType: string; value?: any }> = {};
    onPromptRequest?: (msg: string) => Promise<string | null> | string | null;
    onDebugEvent?: (event: DebugEvent) => void;
    // Per-test progress: fires once per finalized test during a
    // runTests call, mid-run. Result shape matches a single entry
    // in TestRunResult.results — the same applyResult logic can
    // consume it directly. Streaming lets the tests panel flip
    // each row from "running" to pass/fail as tests complete,
    // instead of waiting for the terminal result.
    onTestProgress?: (result: TestResult) => void;
    ready: Promise<void>;

    constructor(opts: RunnerOpts) {
        this.opts = opts;
        this.lspWorker = new Worker('/runtime/web/worker.js', { type: 'module' });
        // worker.js's first-message contract: configure role so heartbeat
        // / log events carry the right tag. The VM-side runtime lives in
        // the iframe (no Worker), so there's no second configure call.
        this.lspWorker.postMessage({ type: 'configure', role: 'lsp' });

        // prompt$ default handler — bridged through the cooperative
        // pump's host-message protocol. The iframe itself owns the
        // prompt UI (window.prompt in the iframe's window), so this
        // hostHandler runs only when the parent receives a host-message
        // event the iframe DIDN'T consume (e.g., a future plugin
        // channel registered on the parent).
        this.hostHandlers['fade-web/prompt'] = async (payload) => {
            let answer = '';
            try {
                const cb = this.onPromptRequest;
                answer = (cb ? await cb(payload) : window.prompt(payload)) ?? '';
            } catch { answer = ''; }
            return { resultType: 'string', value: answer };
        };

        this.ready = new Promise<void>((resolve, reject) => {
            // Resolve `ready` as soon as the LSP worker boots. The VM
            // iframe boots lazily (on first ensureWebPreviewArmed) and
            // gates its own ready via `preview-armed`; the runner's
            // ready promise just means LSP is alive for the editor.
            this.lspWorker.onmessage = (e) => {
                const msg = e.data;
                if (msg.type === 'ready') { resolve(); return; }
                this.handleWorkerMessage(msg, reject);
            };
            this.lspWorker.onerror = (e: ErrorEvent) =>
                reject(new Error('lsp worker error: ' + e.message));
        });
    }

    // Post a VM-side message to the active iframe. No-op when no iframe
    // is attached (lifecycle setup happens lspWorker-side; VM ops never
    // fire before ensureWebPreviewArmed). Returns true if the message
    // was posted, false if it was dropped — the callers that need to
    // await a reply check this before registering a pending entry.
    private postVm(msg: any, transfer: Transferable[] = []): boolean {
        if (!this.vmTarget) return false;
        this.vmTarget.postMessage(msg, '*', transfer);
        return true;
    }

    // Switch VM-side traffic to flow through the given iframe. The
    // iframe must already be loaded and have posted 'preview-armed';
    // the caller is responsible for the bootstrap handshake. After
    // this, postVm targets iframe.contentWindow; future VM-side runs /
    // tests / debug all flow through the visible template iframe.
    attachVmIframe(iframe: HTMLIFrameElement): void {
        this.vmIframe = iframe;
        this.vmTarget = iframe.contentWindow;
        // Listen for messages from the iframe's window so the dispatcher
        // sees them like LSP-worker messages. We filter to messages
        // whose source is exactly the iframe's contentWindow to avoid
        // mixing up postMessages from other windows on the page.
        window.addEventListener('message', (e) => {
            if (!this.vmIframe) return;
            if (e.source !== this.vmIframe.contentWindow) return;
            const msg = e.data;
            if (!msg || typeof msg !== 'object') return;
            // 'preview-ready' / 'preview-armed' are iframe lifecycle
            // signals consumed by the bootstrap code, not VM events.
            if (msg.type === 'preview-ready' || msg.type === 'preview-armed') return;
            this.handleWorkerMessage(msg, () => { /* iframe errors surfaced via UI separately */ });
        });
    }

    // Detach the iframe. After this, postVm is a no-op until another
    // attachVmIframe call. Used when leaving a web project for a non-
    // iframe-driven mode (e.g. monogame today, until phase 2 unifies).
    detachVmIframe(): void {
        this.vmIframe = null;
        this.vmTarget = null;
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
        if (msg.type === 'lsp-check-result')          { this.resolvePending(msg.id, msg.diagnostics); return; }
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
        if (msg.type === 'set-project-type-result')            { this.resolvePending(msg.id, msg.projectType); return; }
        if (msg.type === 'register-command-assembly-result')   { this.resolvePending(msg.id, msg.result); return; }
        if (msg.type === 'load-assembly-result')               { this.resolvePending(msg.id, msg.result); return; }
        if (msg.type === 'clear-command-assemblies-result')    { this.resolvePending(msg.id, undefined); return; }
        if (msg.type === 'list-tests-result')         { this.resolvePending(msg.id, msg.tests); return; }
        if (msg.type === 'list-command-docs-result')  { this.resolvePending(msg.id, msg.docs); return; }
        if (msg.type === 'lsp-tokenize-snippet-result') { this.resolvePending(msg.id, msg.tokens); return; }
        if (msg.type === 'get-version-info-result')   { this.resolvePending(msg.id, msg.info); return; }
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
        if (msg.type === 'debug-resolve-instruction-result') { this.resolvePending(msg.id, msg.result); return; }
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
        if (msg.type === 'host-message') { this.handleHostMessage(msg); return; }
        if (msg.type === 'test-progress') { this.onTestProgress?.(msg.result); return; }
        if (msg.type === 'get-debug-test-result-result') { this.resolvePending(msg.id, msg.result); return; }
        if (msg.type === 'run-tick-result') { this.resolvePending(msg.id, msg.result); return; }
        if (msg.type === 'compile-to-bytecode-result') {
            this.resolvePending(msg.id, { status: msg.status, bytecode: msg.bytecode });
            return;
        }
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

    // No-op since the SAB-based interrupt was removed. Previously this
    // wrote into a SharedArrayBuffer the vm-worker's waitMsInterruptible
    // was Atomics.wait'ing on, so pause/terminate/breakpoint changes
    // could wake a running `wait ms` faster than the regular postMessage
    // round-trip would. In the cooperative-pump model `wait ms` is just
    // a setTimeout on the pump and never blocks the worker thread, so
    // the regular debug-pause/debug-terminate postMessages land between
    // ticks without needing a side-channel wake.
    //
    // Kept as a no-op rather than deleted because debug-flow callers
    // still invoke it for clarity. Remove the calls (and this method)
    // once we're sure no debug regression depends on the side-channel.
    interruptWait(_kind: 1 | 2 | 3): void { /* no-op — see comment */ }

    // Runs Fade source through the cooperative pump (prompt$ + wait ms
    // both work). The worker emits exactly one 'run-tick-result' as the
    // terminal event for this run. The resolved value is the JSON
    // envelope: { ok, error?, compileError? }.
    //
    // Note: this no longer uses the old synchronous CompileAndRun path
    // (which is gone). Callers should JSON.parse the resolved string;
    // there's no `printed` field — print output streams live via the
    // worker's per-line `print` messages (opts.onPrint).
    run(source: string): Promise<string> {
        const id = ++this.nextId;
        return new Promise<string>((resolve) => {
            this.pending.set(id, resolve);
            this.postVm({ type: 'run-start-source', id, source });
        });
    }

    // Terminate an in-flight run. Fire-and-forget: the pump posts its
    // own terminal `run-tick-result` to whatever id the originating
    // run() call registered, so the run() promise resolves with
    // `{ ok: false, error: 'stopped' }`. Calling this when no run is
    // active is a no-op.
    stopRun(): void {
        this.postVm({ type: 'stop-run' });
    }

    setDocument(uri: string, text: string) {
        this.worker.postMessage({ type: 'lsp-set', uri, text });
    }

    /** Synchronous LSP document check — returns diagnostics without waiting
     *  for Monaco markers. Used by the AI edit reviewer. */
    async checkDocumentDiagnostics(uri: string, text: string, timeoutMs = 8_000): Promise<Diagnostic[]> {
        const id = ++this.nextId;
        return new Promise<Diagnostic[]>((resolve, reject) => {
            const timer = setTimeout(() => {
                this.pending.delete(id);
                reject(new Error('LSP worker did not respond (rebuild runtime or reload)'));
            }, timeoutMs);
            this.pending.set(id, (diagnosticsJson: string) => {
                clearTimeout(timer);
                try {
                    const parsed = JSON.parse(diagnosticsJson);
                    resolve(Array.isArray(parsed) ? parsed : []);
                } catch { resolve([]); }
            });
            this.lspWorker.postMessage({ type: 'lsp-check', id, uri, text });
        });
    }

    // Switch both workers' LSP CommandCollection to match the active
    // fade.json type. Both workers run worker.js so we fire the message
    // to each — LSP worker needs the right commands for tokens/hover/
    // diagnostics, VM worker matters once Run/Tests for that type land
    // there (today monogame Run/Tests go through WebRuntime.MonoGame
    // directly, so the vm-worker call is a no-op for monogame but
    // harmless and keeps the two workers in sync).
    async setProjectType(projectType: string): Promise<void> {
        // LSP worker always gets the update. The VM iframe gets it only
        // if attached — pre-attach, the iframe receives the project's
        // command DLL set via the bootstrap message instead, and the
        // type isn't surfaced separately to the VM runtime.
        const awaits: Promise<unknown>[] = [];
        awaits.push(new Promise<void>((resolve) => {
            const id = ++this.nextId;
            this.pending.set(id, () => resolve());
            this.lspWorker.postMessage({ type: 'set-project-type', id, projectType });
        }));
        if (this.vmTarget) {
            awaits.push(new Promise<void>((resolve) => {
                const id = ++this.nextId;
                this.pending.set(id, () => resolve());
                this.postVm({ type: 'set-project-type', id, projectType });
            }));
        }
        await Promise.all(awaits);
    }

    // Load a sibling assembly into the LSP runtime (only) — used to
    // pre-register dependencies of a command DLL so that, when the
    // actual command-source class is Activator.CreateInstance'd, the
    // AppDomain can resolve its referenced types. Mirrors the
    // load-assembly op the static-host bootstrap uses to pre-load dep
    // DLLs before the entry assembly. Not posted to the VM iframe — the
    // iframe's runtime owns its own static references.
    async loadAssembly(dllBytes: ArrayBuffer): Promise<{ ok: boolean; error?: string }> {
        const id = ++this.nextId;
        const postLsp = new Promise<string>((resolve) => {
            this.pending.set(id, (result: string) => resolve(result));
            this.lspWorker.postMessage({ type: 'load-assembly', id, dllBytes });
        });
        const result = await postLsp;
        try { return JSON.parse(result); } catch { return { ok: false, error: 'parse failed' }; }
    }

    // Load a command DLL into both the LSP runtime and (if attached)
    // the VM iframe. dllBytes is the raw assembly content fetched from
    // /runtime/fade-libs/<x>.dll (or from OPFS for user-uploaded
    // plugins). Pre-iframe, the VM side will pick up the DLL via the
    // bootstrap commandDlls list.
    async registerCommandAssembly(dllBytes: ArrayBuffer, className: string): Promise<{ ok: boolean; error?: string }> {
        const postLsp = new Promise<string>((resolve) => {
            const id = ++this.nextId;
            this.pending.set(id, (result: string) => resolve(result));
            this.lspWorker.postMessage({ type: 'register-command-assembly', id, dllBytes, className });
        });
        const awaits: Promise<string>[] = [postLsp];
        if (this.vmTarget) {
            awaits.push(new Promise<string>((resolve) => {
                const id = ++this.nextId;
                this.pending.set(id, (result: string) => resolve(result));
                this.postVm({ type: 'register-command-assembly', id, dllBytes, className });
            }));
        }
        const results = await Promise.all(awaits);
        // Prefer the VM-side result if available (matches the previous
        // behavior of returning the VM target's parse). Fall back to
        // the LSP-side result when no iframe is attached.
        const primary = results[results.length - 1];
        try { return JSON.parse(primary); } catch { return { ok: false, error: 'parse failed' }; }
    }

    // Drop all dynamically-loaded command sources from both runtimes.
    async clearCommandAssemblies(): Promise<void> {
        const awaits: Promise<unknown>[] = [];
        awaits.push(new Promise<void>((resolve) => {
            const id = ++this.nextId;
            this.pending.set(id, () => resolve());
            this.lspWorker.postMessage({ type: 'clear-command-assemblies', id });
        }));
        if (this.vmTarget) {
            awaits.push(new Promise<void>((resolve) => {
                const id = ++this.nextId;
                this.pending.set(id, () => resolve());
                this.postVm({ type: 'clear-command-assemblies', id });
            }));
        }
        await Promise.all(awaits);
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

    // Free-floating tokenize for Help-tab code blocks — bypasses the LSP
    // workspace's _docs map so it doesn't publish diagnostics or churn the
    // open-file set. Returns the legend-classified tokens (line/col/length/
    // type) the help-side renderer wraps into spans.
    async tokenizeSnippet(source: string): Promise<HelpSnippetToken[]> {
        const id = ++this.nextId;
        return new Promise<HelpSnippetToken[]>((resolve) => {
            this.pending.set(id, (json: string) => {
                try {
                    const parsed = JSON.parse(json);
                    resolve(Array.isArray(parsed) ? parsed : []);
                } catch { resolve([]); }
            });
            this.lspWorker.postMessage({ type: 'lsp-tokenize-snippet', id, source });
        });
    }

    async getVersionInfo(): Promise<{ fadeBasic: string; dotnet: string } | null> {
        const id = ++this.nextId;
        return new Promise((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); }
                catch { resolve(null); }
            });
            this.lspWorker.postMessage({ type: 'get-version-info', id });
        });
    }

    // Resolve a VM instruction index to a joined-source location via the
    // active debug session's IndexCollection. Used by the crash overlay:
    // REV_REQUEST_EXPLODE messages carry `ins=[N]` in their formatted
    // text, but the line/char lives in DebugData on the iframe side. Round
    // trip is cheap (one C# binary search). Returns null when no session
    // is active or the index falls outside the program's statement tokens.
    async resolveInstruction(insIndex: number): Promise<{ insIndex: number; lineNumber: number; charNumber: number } | null> {
        if (!this.vmTarget) return null;
        const id = ++this.nextId;
        return new Promise((resolve) => {
            this.pending.set(id, (json: string) => {
                try {
                    const parsed = JSON.parse(json);
                    resolve(parsed === null ? null : parsed);
                } catch { resolve(null); }
            });
            this.postVm({ type: 'debug-resolve-instruction', id, insIndex });
        });
    }

    async runTests(source: string, testName?: string): Promise<TestRunResult> {
        const id = ++this.nextId;
        return new Promise<TestRunResult>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); }
                catch { resolve({ passed: 0, failed: 0, duration: 0, results: [], printed: '', error: 'parse failed' }); }
            });
            this.postVm({ type: 'run-tests', id, source, testName: testName || '' });
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
            this.postVm({ type: 'debug-start', id, source });
        });
    }
    async debugStartTest(source: string, testName: string): Promise<DebugStartResult> {
        const id = ++this.nextId;
        return new Promise<DebugStartResult>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); }
                catch { resolve({ ok: false, error: 'parse failed', statementLines: [] }); }
            });
            this.postVm({ type: 'debug-start-test', id, source, testName });
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
            this.postVm({ type: 'debug-step', id, kind });
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
            this.postVm({
                type: 'debug-set-breakpoints', id,
                linesJson: JSON.stringify(breakpoints),
            });
        });
    }
    // Snapshot the in-flight debug-test session's pass/fail state. Returns
    // null when the session isn't a test debug (or when there's no live
    // session at all). The Playground calls this when a debug-test
    // session emits 'complete' so it can flip the test row from
    // 'running' → 'pass'/'fail' before the session is torn down.
    debugGetTestResult(): Promise<TestResult | null> {
        const id = ++this.nextId;
        return new Promise<TestResult | null>((resolve) => {
            this.pending.set(id, (json: string) => {
                if (!json || json === 'null') { resolve(null); return; }
                try { resolve(JSON.parse(json) as TestResult); }
                catch { resolve(null); }
            });
            this.postVm({ type: 'get-debug-test-result', id });
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
            this.postVm({ type: 'debug-stack-frames', id });
        });
    }
    debugScopes(frameId: number): Promise<DebugScopesResult> {
        const id = ++this.nextId;
        return new Promise<DebugScopesResult>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); } catch { resolve({ scopes: [] }); }
            });
            this.postVm({ type: 'debug-scopes', id, frameId });
        });
    }
    debugExpandVariable(variableId: number): Promise<DebugScopesResult> {
        const id = ++this.nextId;
        return new Promise<DebugScopesResult>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); } catch { resolve({ scopes: [] }); }
            });
            this.postVm({ type: 'debug-variable-expansion', id, variableId });
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
            this.postVm({ type, id });
        });
    }
    private debugTextCall(type: string, payload: object): Promise<DebugEvalResult | null> {
        const id = ++this.nextId;
        return new Promise<DebugEvalResult | null>((resolve) => {
            this.pending.set(id, (json: string) => {
                try { resolve(JSON.parse(json)); } catch { resolve(null); }
            });
            this.postVm({ type, id, ...payload });
        });
    }

    // Called when the worker emits a host-message (HostBridge.PostMessage
    // on the C# side). Dispatches by channel name, awaits the handler,
    // and posts the typed reply back to the worker. Plugins extend the
    // handler set via registerHostHandler.
    private async handleHostMessage(msg: { channel: string; payload: string }): Promise<void> {
        const handler = this.hostHandlers[msg.channel];
        if (!handler) {
            console.warn('[fade] no host handler for channel:', msg.channel);
            this.postVm({ type: 'host-reply', resultType: 'string', value: '' });
            return;
        }
        try {
            const reply = await handler(msg.payload);
            this.postVm({ type: 'host-reply', ...reply });
        } catch (e) {
            console.error('[fade] host handler for', msg.channel, 'threw:', e);
            this.postVm({ type: 'host-reply', resultType: 'string', value: '' });
        }
    }

    // Register (or replace) a page-side handler for a HostBridge channel.
    // Library authors document which channel their cooperative commands
    // use; consumers plug in handlers here. The handler returns (or
    // resolves to) { resultType, value }; see worker.js's host-reply
    // dispatcher for the supported resultType strings.
    registerHostHandler(
        channel: string,
        fn: (payload: string) =>
            Promise<{ resultType: string; value?: any }> | { resultType: string; value?: any },
    ): void {
        this.hostHandlers[channel] = fn;
    }

    // Compile Fade source to a raw bytecode blob. The Playground uses
    // this for the export download and to feed the preview iframe. The
    // returned ArrayBuffer is transferable; status carries the compile
    // diagnostics envelope on failure.
    async compileToBytecode(source: string): Promise<{ ok: boolean; compileError?: string; bytecode?: ArrayBuffer }> {
        const id = ++this.nextId;
        const p = new Promise<{ status: string; bytecode: ArrayBuffer | null }>((resolve) => {
            this.pending.set(id, resolve);
            this.postVm({ type: 'compile-to-bytecode', id, source });
        });
        const r = await p;
        let parsed: any = {}; try { parsed = JSON.parse(r.status); } catch { /* */ }
        if (!parsed.ok) return { ok: false, compileError: parsed.compileError ?? parsed.error };
        return { ok: true, bytecode: r.bytecode ?? undefined };
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
// `FadeBridge.ListCommandDocs()` emits in [FadeBasic.Export.Web/FadeBridge.cs].
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
    // Matches FadeBasic.Export.Web's BreakpointRequestDto (camelCase JSON via
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

// ─── First-run workspace picker ─────────────────────────────────────────────
// Shown when OpfsWorkspace.init() reports zero existing projects. Reuses
// the regular project-overlay markup but hides the list + close button so
// the user has to commit to a workspace type before the editor mounts.
// On submit we createProject + setActiveProject and then reload — the
// second boot pass then takes the normal happy path.
async function runFirstRunFlow(workspace: OpfsWorkspace): Promise<never> {
    const overlay = document.getElementById('project-overlay')!;
    const titleEl = document.getElementById('project-modal-title')!;
    const headingEl = document.getElementById('project-new-heading')!;
    const nameInput = document.getElementById('project-new-input') as HTMLInputElement;
    const errorEl = document.getElementById('project-new-error')!;
    const createBtn = document.getElementById('project-new-create') as HTMLButtonElement;
    const cards = Array.from(
        document.querySelectorAll<HTMLButtonElement>('.project-type-card'),
    );

    overlay.classList.add('first-run');
    overlay.hidden = false;
    titleEl.textContent = 'Welcome';
    headingEl.textContent = 'Create your first workspace';

    let selected: FadeProjectType | null = null;
    const updateBtn = () => {
        createBtn.disabled =
            nameInput.value.trim().length === 0 || selected === null;
    };
    const showError = (msg: string) => {
        errorEl.textContent = msg;
        errorEl.hidden = false;
    };
    const clearError = () => {
        errorEl.textContent = '';
        errorEl.hidden = true;
    };

    for (const card of cards) {
        card.addEventListener('click', () => {
            const t = card.dataset.type as FadeProjectType | undefined;
            if (t !== 'web' && t !== 'monogame') return;
            selected = t;
            for (const c of cards) {
                const match = c === card;
                c.classList.toggle('selected', match);
                c.setAttribute('aria-checked', match ? 'true' : 'false');
            }
            updateBtn();
        });
    }
    nameInput.addEventListener('input', () => { clearError(); updateBtn(); });

    const submit = async () => {
        const name = nameInput.value.trim();
        if (!name || !selected) return;
        if (!/^[\w.-]+$/.test(name)) {
            showError('Invalid name. Letters, digits, dot, dash, underscore only.');
            return;
        }
        try {
            await workspace.createProject(name, selected);
            await workspace.setActiveProject(name);
        } catch (e: any) {
            showError('Create failed: ' + (e?.message ?? e));
            return;
        }
        // Reload: lets the post-init boot path run cleanly with the
        // freshly-created project active.
        location.reload();
    };

    createBtn.addEventListener('click', submit);
    nameInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !createBtn.disabled) {
            e.preventDefault();
            submit();
        }
    });

    setTimeout(() => nameInput.focus(), 0);
    // Never resolves — the only way out is the reload above. Caller
    // awaits this so it can stay `async` without an unreachable return.
    return new Promise<never>(() => { /* intentional */ });
}

// ─── bootstrap ──────────────────────────────────────────────────────────────
async function bootstrap() {
    const pgSplash = (window as any).__pgSplash as
        { setStatus(t: string, e?: boolean): void; hide(): void } | undefined;
    pgSplash?.setStatus('Initializing editor…');
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

    // Language configuration mirrors VsCode/basicscript/language.configuration.json
    // so editor.action.commentLine (Cmd+/) and editor.action.blockComment
    // (Shift+Alt+A) toggle the right delimiters when the user hits those
    // hotkeys. Without this Monaco has no idea how to comment Fade source
    // and the commands no-op silently. The other fields (brackets,
    // autoClosingPairs, etc.) are bonus quality-of-life — they make Monaco
    // auto-close parens/quotes the same way VSCode does for `.fbasic` files.
    monaco.languages.setLanguageConfiguration('fade', {
        comments: {
            lineComment: '`',
            blockComment: ['remstart', 'remend'],
        },
        // `.`, `(`, `)` deliberately excluded — `.` is the member-
        // access operator (struct field), not a word char, and treating
        // it as one made Monaco's matcher glue `ballPos.` together as
        // the current word, then filter every Field item out of the
        // dropdown because `x`/`y` don't match a `ballPos.` prefix.
        // Standard convention across languages: `$#` are name sigils,
        // letters/digits/underscores form the identifier, `.()` are
        // syntax.
        wordPattern: /[a-zA-Z_][a-zA-Z0-9_$#]*/,
        brackets: [['(', ')']],
        autoClosingPairs: [
            { open: '(', close: ')' },
            { open: '"', close: '"', notIn: ['string'] },
        ],
        surroundingPairs: [
            { open: '(', close: ')' },
            { open: '"', close: '"' },
        ],
        onEnterRules: [
            {
                beforeText: /^\s*(?:for|if).*?:\s*$/,
                action: { indentAction: monaco.languages.IndentAction.Indent },
            },
        ],
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

    // Semantic-token type legend — must match FadeBasic.Export.Web/FadeLsp.cs's
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
    // Light counterpart — VSCode Light+ palette. Same semantic token list so
    // the editor switches themes without reloading.
    monaco.editor.defineTheme('fade-light', {
        base: 'vs',
        inherit: true,
        rules: [
            { token: 'comment',   foreground: '008000', fontStyle: 'italic' },
            { token: 'keyword',   foreground: 'AF00DB' },
            { token: 'function',  foreground: '795E26' },
            { token: 'method',    foreground: '795E26' },
            { token: 'macro',     foreground: 'AF00DB' },
            { token: 'parameter', foreground: '001080' },
            { token: 'struct',    foreground: '267F99' },
            { token: 'type',      foreground: '267F99' },
            { token: 'operator',  foreground: '000000' },
            { token: 'number',    foreground: '098658' },
            { token: 'string',    foreground: 'A31515' },
            ...extraThemeRules('light'),
        ],
        colors: {},
    });

    // ── Fun themes ────────────────────────────────────────────────────────
    // Each preset replaces the per-token foreground colors; the CSS palette
    // for the rest of the UI lives in index.html under matching
    // html[data-theme="<id>"] blocks. Token sets mirror the dark theme so
    // semantic-highlighting picks them up unchanged.
    monaco.editor.defineTheme('fade-dracula', {
        base: 'vs-dark', inherit: true,
        rules: [
            { token: 'comment',   foreground: '6272A4', fontStyle: 'italic' },
            { token: 'keyword',   foreground: 'FF79C6' },
            { token: 'function',  foreground: '50FA7B' },
            { token: 'method',    foreground: '50FA7B' },
            { token: 'macro',     foreground: 'FF79C6' },
            { token: 'parameter', foreground: 'FFB86C' },
            { token: 'struct',    foreground: '8BE9FD' },
            { token: 'type',      foreground: '8BE9FD' },
            { token: 'operator',  foreground: 'F8F8F2' },
            { token: 'number',    foreground: 'BD93F9' },
            { token: 'string',    foreground: 'F1FA8C' },
            ...extraThemeRules(),
        ],
        colors: {
            'editor.background': '#282A36',
            'editor.foreground': '#F8F8F2',
        },
    });
    monaco.editor.defineTheme('fade-solarized-dark', {
        base: 'vs-dark', inherit: true,
        rules: [
            { token: 'comment',   foreground: '586E75', fontStyle: 'italic' },
            { token: 'keyword',   foreground: '859900' },
            { token: 'function',  foreground: 'B58900' },
            { token: 'method',    foreground: 'B58900' },
            { token: 'macro',     foreground: '859900' },
            { token: 'parameter', foreground: 'CB4B16' },
            { token: 'struct',    foreground: '2AA198' },
            { token: 'type',      foreground: '2AA198' },
            { token: 'operator',  foreground: '93A1A1' },
            { token: 'number',    foreground: 'D33682' },
            { token: 'string',    foreground: '2AA198' },
            ...extraThemeRules(),
        ],
        colors: {
            'editor.background': '#002B36',
            'editor.foreground': '#93A1A1',
        },
    });
    monaco.editor.defineTheme('fade-monokai', {
        base: 'vs-dark', inherit: true,
        rules: [
            { token: 'comment',   foreground: '75715E', fontStyle: 'italic' },
            { token: 'keyword',   foreground: 'F92672' },
            { token: 'function',  foreground: 'A6E22E' },
            { token: 'method',    foreground: 'A6E22E' },
            { token: 'macro',     foreground: 'F92672' },
            { token: 'parameter', foreground: 'FD971F' },
            { token: 'struct',    foreground: '66D9EF' },
            { token: 'type',      foreground: '66D9EF' },
            { token: 'operator',  foreground: 'F8F8F2' },
            { token: 'number',    foreground: 'AE81FF' },
            { token: 'string',    foreground: 'E6DB74' },
            ...extraThemeRules(),
        ],
        colors: {
            'editor.background': '#272822',
            'editor.foreground': '#F8F8F2',
        },
    });
    monaco.editor.defineTheme('fade-nord', {
        base: 'vs-dark', inherit: true,
        rules: [
            { token: 'comment',   foreground: '4C566A', fontStyle: 'italic' },
            { token: 'keyword',   foreground: '81A1C1' },
            { token: 'function',  foreground: '88C0D0' },
            { token: 'method',    foreground: '88C0D0' },
            { token: 'macro',     foreground: 'B48EAD' },
            { token: 'parameter', foreground: 'D08770' },
            { token: 'struct',    foreground: '8FBCBB' },
            { token: 'type',      foreground: '8FBCBB' },
            { token: 'operator',  foreground: 'ECEFF4' },
            { token: 'number',    foreground: 'B48EAD' },
            { token: 'string',    foreground: 'A3BE8C' },
            ...extraThemeRules(),
        ],
        colors: {
            'editor.background': '#2E3440',
            'editor.foreground': '#D8DEE9',
        },
    });
    monaco.editor.defineTheme('fade-high-contrast', {
        base: 'hc-black', inherit: true,
        rules: [
            { token: 'comment',   foreground: '7CA668', fontStyle: 'italic' },
            { token: 'keyword',   foreground: '569CD6' },
            { token: 'function',  foreground: 'DCDCAA' },
            { token: 'method',    foreground: 'DCDCAA' },
            { token: 'macro',     foreground: 'C586C0' },
            { token: 'parameter', foreground: '9CDCFE' },
            { token: 'struct',    foreground: '4EC9B0' },
            { token: 'type',      foreground: '4EC9B0' },
            { token: 'operator',  foreground: 'FFFFFF' },
            { token: 'number',    foreground: 'B5CEA8' },
            { token: 'string',    foreground: 'CE9178' },
            ...extraThemeRules(),
        ],
        colors: {},
    });
    // DBP — tribute to the original DarkBASIC Professional editor. Reference
    // screenshot: pure-blue commands (`load`, `sync`, `set`, `make`,
    // `position`, …), grey-italic REM comments, maroon strings, and the
    // rest in plain black on a white canvas. We map keyword/function/method
    // → blue so both control flow (for, next, if) AND built-in command
    // tokens land in the same hue, since DBP didn't distinguish them.
    monaco.editor.defineTheme('fade-dbp', {
        base: 'vs', inherit: true,
        rules: [
            { token: 'comment',   foreground: '808080', fontStyle: 'italic' },
            { token: 'keyword',   foreground: '0000FF' },
            { token: 'function',  foreground: '0000FF' },
            { token: 'method',    foreground: '0000FF' },
            { token: 'macro',     foreground: '0000FF' },
            { token: 'parameter', foreground: '000000' },
            { token: 'struct',    foreground: '000000' },
            { token: 'type',      foreground: '000000' },
            { token: 'operator',  foreground: '000000' },
            { token: 'number',    foreground: '2E8B57' },
            { token: 'string',    foreground: '800080' },
            ...extraThemeRules('light'),
        ],
        colors: {
            'editor.background':        '#FFFFFF',
            'editor.foreground':        '#000000',
            'editorLineNumber.foreground':       '#A0A0A0',
            'editorLineNumber.activeForeground': '#000000',
            'editor.selectionBackground':        '#316AC5',
            'editor.inactiveSelectionBackground':'#C2D5F2',
            'editor.lineHighlightBackground':    '#F4F4F4',
        },
    });

    monaco.editor.setTheme('fade-dark');

    pgSplash?.setStatus('Loading language server…');

    // Heartbeat indicator — displayed in the Diagnostics panel.
    // The LSP worker posts a beat every 500ms; the dot pulses while alive
    // and turns red when we haven't heard from it in >1.2s.
    // Note: there is no separate VM worker beat. The web-template VM runs
    // inside a same-origin iframe (runtime.js sends 'vm' heartbeats that
    // flow through attachVmIframe → handleWorkerMessage), but the MonoGame
    // runtime uses Blazor and sends no heartbeats. The VM row was removed
    // from the Diagnostics panel to avoid a permanently-stalled indicator.
    type BeatState = { lastAt: number; tick: number };
    const lspBeat: BeatState = { lastAt: Date.now(), tick: 0 };
    const dotLsp    = document.getElementById('diag-dot-lsp') as HTMLElement;
    const detailLsp = document.getElementById('diag-lsp-detail') as HTMLElement;
    function paintHeartbeat() {
        const dt = Date.now() - lspBeat.lastAt;
        const busy = dt > 1200;
        dotLsp.dataset.state = busy ? 'busy' : (lspBeat.tick % 2 === 0 ? 'on' : 'off');
        if (busy) {
            dotLsp.title = `LSP worker busy — last beat ${(dt / 1000).toFixed(1)}s ago`;
            detailLsp.textContent = `stalled ${(dt / 1000).toFixed(1)}s ago`;
        } else {
            dotLsp.title = `LSP worker alive — beat ${lspBeat.tick}`;
            detailLsp.textContent = `alive — beat #${lspBeat.tick}`;
        }
    }
    setInterval(paintHeartbeat, 250);

    // Program-output forwarding. `print`, stdout, and stderr from the
    // user's running program land in the Output panel (where users
    // expect to see them). When a live session is active, each line
    // is ALSO broadcast to peers so an observer sees the host's output
    // appear in their own Output panel — without it, observers stream
    // the game canvas but can't tell what the program logged.
    const broadcastLogLine = (channel: string, level: 'info' | 'warn' | 'error', message: string) => {
        const session = liveSessionHandle?.getSession();
        if (!session) return;
        try { session.sendLogLine({ channel, level, message }); }
        catch (e) { console.warn('[fade-collab] sendLogLine failed', e); }
    };
    const handleProgramPrint = (line: string) => {
        appendOutputLine(line);
        broadcastLogLine('program', 'info', line);
    };
    const handleProgramStderr = (line: string) => {
        appendOutputLine(line, 'error');
        broadcastLogLine('program-err', 'error', line);
    };

    const runner = new FadeRunner({
        onPrint: handleProgramPrint,
        onAlert: (msg) => window.alert(msg),
        onHeartbeat: (role, tick, t) => {
            if (role !== 'lsp') return;
            lspBeat.tick = tick;
            lspBeat.lastAt = t;
            paintHeartbeat();
        },
    });
    await runner.ready;
    pgSplash?.setStatus('Configuring language features…');
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
        const fileName = projectFileNameFromUri(uri);
        // In-project files share a single token stream from the joined doc.
        // applyProjectSemanticTokens fans it out to every in-project model
        // in one pass, so for an in-project model we route there instead of
        // duplicating the work N times across N tabs.
        if (fileName) {
            await applyProjectSemanticTokens();
            return;
        }
        const tokens = await lsp.getTokens(uri);
        // The model param was captured before the await above. By the
        // time getTokens resolves, the user may have closed the file or
        // reloaded the project — calling deltaDecorations on a disposed
        // model throws "Model is disposed!" and breaks the rest of
        // boot. Bail silently; the next refresh will retokenize.
        if (model.isDisposed()) return;
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

    // Fetch the joined doc's token stream once, decode it, then bin tokens
    // by the file each line belongs to and apply per-file decorations. Called
    // when any in-project file's tokens need a refresh — much cheaper than
    // calling getTokens once per tab when N tabs all map to the same project
    // URI on the worker side.
    async function applyProjectSemanticTokens(): Promise<void> {
        const map = projectSourceMap;
        if (!map) return;
        const tokens = await lsp.getTokens(PROJECT_LSP_URI);
        // Bucket decorations by file name first; apply them to each file's
        // Monaco model at the end so a single LSP round-trip covers the
        // whole project.
        const byFile = new Map<string, monaco.editor.IModelDeltaDecoration[]>();
        for (const r of map.ranges) byFile.set(r.name, []);
        let joinedLine = 0;
        let ch = 0;
        for (let i = 0; i + 4 < tokens.length; i += 5) {
            const dLine = tokens[i];
            const dChar = tokens[i + 1];
            const len = tokens[i + 2];
            const typeIdx = tokens[i + 3];
            if (dLine > 0) { joinedLine += dLine; ch = dChar; }
            else { ch += dChar; }
            const mapped = map.fromProject(joinedLine, ch);
            if (!mapped) continue;
            const list = byFile.get(mapped.name);
            if (!list) continue;
            const tokenName = tokenTypes[typeIdx] ?? 'unknown';
            list.push({
                range: new monaco.Range(mapped.line + 1, mapped.character + 1, mapped.line + 1, mapped.character + 1 + len),
                options: { inlineClassName: 'fade-token-' + tokenName },
            });
        }
        for (const r of map.ranges) {
            const fileUri = monaco.Uri.file(`/workspace/${r.name}`);
            const model = monaco.editor.getModel(fileUri);
            // getModel can return a model that's mid-dispose (race between
            // workspace teardown and the LSP getTokens we just awaited).
            // isDisposed() catches that case; without this check Monaco
            // throws BugIndicatingError("Model is disposed!") and the
            // bootstrap promise rejects.
            if (!model || model.isDisposed()) continue;
            const uriKey = fileUri.toString();
            const next = model.deltaDecorations(decorationsByUri.get(uriKey) ?? [], byFile.get(r.name) ?? []);
            decorationsByUri.set(uriKey, next);
        }
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

            const mappedPos = toLspPosition(uri, position.lineNumber - 1, position.column - 1);
            const hover = await runner.getHover(lspUriFor(uri), mappedPos.line, mappedPos.character);
            if (hover) {
                let value = hover.contents;
                // BuildCommandMarkdown emits `### commandname\n...` as
                // the first non-blank line for command hovers. When we
                // see that shape, append a deep-link to the Help tab
                // (markdown link with a Monaco command URI). Trusted
                // markdown lets Monaco invoke our registered command.
                // The same helper backs the right-click "Help" action
                // and Ctrl/Cmd-click resolver so all three editor
                // affordances parse the hover the same way (tested in
                // help.test.ts → extractCommandNameFromHover).
                const cmdName = extractCommandNameFromHover(value);
                if (cmdName) {
                    const args = encodeURIComponent(JSON.stringify(cmdName));
                    value = value + `\n\n[View in Help →](command:fade.openHelp?${args})`;
                    contents.push({ value, isTrusted: true });
                } else {
                    contents.push({ value });
                }
                if (!range) {
                    const localRange = rangeFromLsp(uri, hover.range);
                    range = new monaco.Range(
                        localRange.start.line + 1,
                        localRange.start.character + 1,
                        localRange.end.line + 1,
                        localRange.end.character + 1,
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
            // Force a synchronous flush of the current model's text to the
            // LSP before we ask for completions. The 250ms polling loop
            // means the LSP can be one keystroke behind by the time
            // Monaco's auto-trigger fires (especially with a space-trigger
            // right after a command word) — completions then run against
            // a stale AST that doesn't yet contain the token the user
            // just typed, and the right items don't surface until the
            // user pauses long enough for the poll to catch up.
            // Worker postMessage is FIFO, so the setDocument below lands
            // in the worker queue ahead of getCompletions and the
            // computation sees fresh source.
            if (model.getLanguageId() === 'fade') {
                if (projectFileNameFromUri(uri)) {
                    rebuildAndPushProjectDoc();
                } else {
                    const value = model.getValue();
                    if (lastPushedByUri.get(uri) !== value) {
                        lastPushedByUri.set(uri, value);
                        runner.setDocument(uri, value);
                    }
                }
            }
            const mapped = toLspPosition(uri, position.lineNumber - 1, position.column - 1);
            const items = await runner.getCompletions(lspUriFor(uri), mapped.line, mapped.character);
            const word = model.getWordUntilPosition(position);
            // Default insertion range — the current word at the cursor.
            const wordRange = new monaco.Range(
                position.lineNumber, word.startColumn,
                position.lineNumber, word.endColumn,
            );
            // Struct-field-after-dot: the fade `wordPattern` includes `.`,
            // so `getWordUntilPosition` at `ballPos.|` returns `ballPos.`
            // as the "current word". Monaco then fuzzy-matches the typed
            // word against each item label — `x` and `y` don't match
            // `ballPos.` so every Field item is silently filtered out
            // and Monaco falls back to its built-in word-from-document
            // suggestions. Detect the trailing dot and clamp the range
            // to an empty span at the cursor so accepting `x` produces
            // `ballPos.x` (not `x` replacing `ballPos.`); also set
            // filterText to label so the matcher sees the empty post-
            // dot prefix and admits every field. We override for ALL
            // items returned by the LSP in this position — when the
            // previous char is `.`, the AST guarantees the response is
            // field-only (see TryGetStructFieldCompletionsAfterDot).
            const colBeforeCursor = position.column - 2; // 0-based char index
            const lineRaw = model.getLineContent(position.lineNumber);
            const prevChar = colBeforeCursor >= 0 && colBeforeCursor < lineRaw.length
                ? lineRaw[colBeforeCursor]
                : '';
            const afterDot = prevChar === '.';
            const dotRange = afterDot
                ? new monaco.Range(
                    position.lineNumber, position.column,
                    position.lineNumber, position.column,
                )
                : null;
            // For multi-word command completions (label contains a space),
            // accepting the suggestion must replace the ENTIRE typed
            // prefix, not just the last word. Otherwise `set sprite|` +
            // accepting `set sprite render target` yields `set set sprite
            // render target`. Compute the longest suffix of the line text
            // before the cursor that's a case-insensitive prefix of the
            // label and extend the range start back over it.
            const lineText = model.getLineContent(position.lineNumber);
            const cursorCol = position.column;             // 1-based
            const textBeforeCursor = lineText.slice(0, cursorCol - 1);
            const rangeForLabel = (label: string): monaco.IRange => {
                if (!label.includes(' ')) return wordRange;
                const lowerLabel = label.toLowerCase();
                const lowerText = textBeforeCursor.toLowerCase();
                // Try the longest suffix first, walking inward. Stop on
                // the first match — that's the user-typed prefix we want
                // to replace.
                for (let start = 0; start < lowerText.length; start++) {
                    if (lowerLabel.startsWith(lowerText.slice(start))) {
                        const matchLen = lowerText.length - start;
                        if (matchLen <= 0) break;
                        return new monaco.Range(
                            position.lineNumber, start + 1,
                            position.lineNumber, cursorCol,
                        );
                    }
                }
                return wordRange;
            };
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
                    // LSPUtil's GetSymbolCompletions hardcodes FilterText
                    // to the empty string. OmniSharp/VSCode treats that
                    // as "no filter text, fall back to label"; Monaco
                    // treats it as "I have no filterable text, hide this
                    // item." Coalesce the empty string back to undefined
                    // so Monaco's matcher uses `label` instead, which
                    // restores all the variable/function suggestions
                    // that were silently disappearing.
                    filterText: afterDot
                        ? it.label
                        : (it.filterText ? it.filterText : undefined),
                    insertTextRules: it.insertTextFormat === 2
                        ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet
                        : monaco.languages.CompletionItemInsertTextRule.None,
                    range: dotRange ?? rangeForLabel(it.label),
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
            const mapped = toLspPosition(uri, position.lineNumber - 1, position.column - 1);
            const sig = await runner.getSignatureHelp(lspUriFor(uri), mapped.line, mapped.character);
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
            const mapped = toLspPosition(uri, position.lineNumber - 1, position.column - 1);
            const refs = await runner.getReferences(lspUriFor(uri), mapped.line, mapped.character);
            return refs.map((r) => {
                // Each reference may live in a different in-project file;
                // fromLspLocation rewrites the URI per-file based on the
                // joined-line range.
                const start = fromLspLocation(uri, r.range.start.line, r.range.start.character);
                const end   = fromLspLocation(uri, r.range.end.line,   r.range.end.character);
                return {
                    uri: monaco.Uri.parse(start.uri),
                    range: new monaco.Range(
                        start.line + 1, start.character + 1,
                        end.line + 1,   end.character + 1,
                    ),
                };
            });
        },
    });

    // Goto-definition provider.
    monaco.languages.registerDefinitionProvider('fade', {
        provideDefinition: async (model, position) => {
            const uri = model.uri.toString();
            const mapped = toLspPosition(uri, position.lineNumber - 1, position.column - 1);
            const def = await runner.getDefinition(lspUriFor(uri), mapped.line, mapped.character);
            if (!def) return null;
            const start = fromLspLocation(uri, def.range.start.line, def.range.start.character);
            const end   = fromLspLocation(uri, def.range.end.line,   def.range.end.character);
            return {
                uri: monaco.Uri.parse(start.uri),
                range: new monaco.Range(
                    start.line + 1, start.character + 1,
                    end.line + 1,   end.character + 1,
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
            const syms = await runner.getDocumentSymbols(lspUriFor(uri));
            // In multi-source mode the LSP returns symbols across the
            // entire joined doc; filter to only those whose range starts
            // inside the requested file's slice so the outline shows just
            // this file's symbols.
            const filtered = projectFileNameFromUri(uri)
                ? syms.filter((s) => joinedLineBelongsTo(uri, s.range.start.line))
                : syms;
            return filtered.map((s) => toMonacoSymbol(uri, s));
        },
    });

    function toMonacoSymbol(uri: string, s: DocSymbol): monaco.languages.DocumentSymbol {
        const r = rangeFromLsp(uri, s.range);
        const sr = rangeFromLsp(uri, s.selectionRange);
        return {
            name: s.name,
            detail: s.detail ?? '',
            kind: lspSymKindToMonaco(s.kind),
            tags: [],
            range: new monaco.Range(
                r.start.line + 1, r.start.character + 1,
                r.end.line + 1, r.end.character + 1,
            ),
            selectionRange: new monaco.Range(
                sr.start.line + 1, sr.start.character + 1,
                sr.end.line + 1, sr.end.character + 1,
            ),
            children: s.children?.map((c) => toMonacoSymbol(uri, c)) ?? [],
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
            const ranges = await runner.getFoldingRanges(lspUriFor(uri));
            // Filter to ranges whose start line falls inside the requested
            // file's slice, then translate. Otherwise file B would show
            // foldable regions belonging to file A.
            const inFile = projectFileNameFromUri(uri)
                ? ranges.filter((r) => joinedLineBelongsTo(uri, r.startLine))
                : ranges;
            return inFile.map((r) => {
                const localStart = rangeFromLsp(uri, {
                    start: { line: r.startLine, character: 0 },
                    end:   { line: r.endLine,   character: 0 },
                });
                return {
                    start: localStart.start.line + 1,
                    end:   localStart.end.line + 1,
                    kind: r.kind === 1
                        ? monaco.languages.FoldingRangeKind.Comment
                        : r.kind === 2
                            ? monaco.languages.FoldingRangeKind.Imports
                            : monaco.languages.FoldingRangeKind.Region,
                };
            });
        },
    });

    // Formatting helpers: convert Monaco's FormattingOptions to our DTO.
    const buildFormattingOptions = (opts: monaco.languages.FormattingOptions): FormattingOptions => ({
        tabSize: opts.tabSize,
        insertSpaces: opts.insertSpaces,
        casing: 0, // Ignore — could be wired to a user setting later
    });

    // Translate an edit from joined-doc coords back to per-file coords.
    // For format requests on file B, the LSP may return edits across the
    // entire joined doc; filter to edits that start inside B's slice and
    // subtract its startLine.
    const toMonacoEdit = (uri: string, e: TextEdit): monaco.languages.TextEdit => {
        const local = rangeFromLsp(uri, e.range);
        return {
            range: new monaco.Range(
                local.start.line + 1, local.start.character + 1,
                local.end.line + 1, local.end.character + 1,
            ),
            text: e.newText,
        };
    };
    const filterEditsForFile = (uri: string, edits: TextEdit[]): TextEdit[] => {
        if (!projectFileNameFromUri(uri)) return edits;
        return edits.filter((e) => joinedLineBelongsTo(uri, e.range.start.line));
    };

    // extensionId / displayName let VSCode treat this as a "named" formatter
    // — matched by the user config setting `[fade].editor.defaultFormatter`.
    const docFormatter: monaco.languages.DocumentFormattingEditProvider = {
        displayName: 'Fade Basic',
        provideDocumentFormattingEdits: async (model, opts) => {
            const uri = model.uri.toString();
            const edits = await runner.format(lspUriFor(uri), buildFormattingOptions(opts));
            return filterEditsForFile(uri, edits).map((e) => toMonacoEdit(uri, e));
        },
    };
    (docFormatter as any).extensionId = 'fade-basic';
    monaco.languages.registerDocumentFormattingEditProvider('fade', docFormatter);

    const rangeFormatter: monaco.languages.DocumentRangeFormattingEditProvider = {
        displayName: 'Fade Basic',
        provideDocumentRangeFormattingEdits: async (model, range, opts) => {
            const uri = model.uri.toString();
            const mappedStart = toLspPosition(uri, range.startLineNumber - 1, range.startColumn - 1);
            const mappedEnd   = toLspPosition(uri, range.endLineNumber - 1,   range.endColumn - 1);
            const edits = await runner.formatRange(lspUriFor(uri), buildFormattingOptions(opts), {
                startLine: mappedStart.line,
                startCh:   mappedStart.character,
                endLine:   mappedEnd.line,
                endCh:     mappedEnd.character,
            });
            return filterEditsForFile(uri, edits).map((e) => toMonacoEdit(uri, e));
        },
    };
    (rangeFormatter as any).extensionId = 'fade-basic';
    monaco.languages.registerDocumentRangeFormattingEditProvider('fade', rangeFormatter);

    monaco.languages.registerOnTypeFormattingEditProvider('fade', {
        autoFormatTriggerCharacters: ['(', ')', ',', '\n', ' '],
        provideOnTypeFormattingEdits: async (model, position, _ch, opts) => {
            const uri = model.uri.toString();
            const mapped = toLspPosition(uri, position.lineNumber - 1, position.column - 1);
            const edits = await runner.formatOnType(
                lspUriFor(uri),
                buildFormattingOptions(opts),
                mapped.line, mapped.character,
            );
            return filterEditsForFile(uri, edits).map((e) => toMonacoEdit(uri, e));
        },
    });

    // Rename provider — F2 in the editor.
    monaco.languages.registerRenameProvider('fade', {
        provideRenameEdits: async (model, position, newName) => {
            const uri = model.uri.toString();
            const mapped = toLspPosition(uri, position.lineNumber - 1, position.column - 1);
            const result = await runner.rename(lspUriFor(uri), mapped.line, mapped.character, newName);
            if (!result?.changes) {
                return { edits: [] };
            }
            const edits: monaco.languages.IWorkspaceTextEdit[] = [];
            for (const [resourceUri, textEdits] of Object.entries(result.changes)) {
                for (const e of textEdits) {
                    // When the rename was routed through PROJECT_LSP_URI,
                    // resourceUri will be PROJECT_LSP_URI for every edit;
                    // fan each edit out to the file its range lives in.
                    const start = fromLspLocation(uri, e.range.start.line, e.range.start.character);
                    const end   = fromLspLocation(uri, e.range.end.line,   e.range.end.character);
                    const targetUri = resourceUri === PROJECT_LSP_URI ? start.uri : resourceUri;
                    edits.push({
                        resource: monaco.Uri.parse(targetUri),
                        textEdit: {
                            range: new monaco.Range(
                                start.line + 1, start.character + 1,
                                end.line + 1,   end.character + 1,
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
    // Tracks whether the last diagnostics round disabled Run/Debug, so the
    // diagnostics handler can re-trigger test discovery on the
    // has-errors → clean transition (otherwise the test panel would stay
    // stale until the user typed another keystroke).
    let lastBlockedByErrors = false;
    // Activity flags read by refreshRunButtons + refreshStopButton.
    // Declared up here (instead of next to the run/debug bindings further
    // down) so refreshFadeProject — awaited mid-bootstrap before the rest
    // of the run/debug UI is wired — can safely call refreshRunButtons.
    // Without this, the early call hits TDZ on the let-bindings.
    let debugSessionActive = false;
    let debugPaused = false;
    // Sticky flag set when the VM hit a fatal exception (divide-by-zero,
    // invalid-address, unhandled .NET exception, etc). The session stays
    // alive in a paused state for post-mortem inspection — locals, call
    // stack, REPL — but Continue / Step / Pause MUST be disabled because
    // the VM can't actually resume from a fatal fault; clicking any of
    // them locks the whole UI thread waiting for a response that won't
    // come. Only Stop / Abort are valid actions in this state. Cleared
    // when a new debug session begins.
    let debugFatalException = false;
    let runActive = false;
    let testsBusy = false;
    let exportBusy = false;
    // True when SOME OTHER peer is actively running or debugging in this
    // live session. Updated by the awareness onStateChange handler that
    // also drives the game-stream overlay. Read by refreshRunButtons /
    // refreshStopButton so observers' Run goes grey (host has control)
    // and their Stop becomes available (they can end the shared session).
    let remoteActivityInProgress = false;

    // Translate a single Diagnostic from joined-doc coords (start.line +
    // end.line in the project URI's space) to per-file coords. Returns
    // null when the diagnostic doesn't belong to a known project source —
    // we drop those rather than misattribute them to a random file.
    function splitProjectDiagnostic(d: Diagnostic): { name: string; diagnostic: Diagnostic } | null {
        const map = projectSourceMap;
        if (!map) return null;
        const startMap = map.fromProject(d.range.start.line, d.range.start.character);
        if (!startMap) return null;
        const endMap = map.fromProject(d.range.end.line, d.range.end.character);
        // A diagnostic that straddles a file boundary clamps to the start
        // file's end-of-text. That's a pathological case (the LSP would
        // need to have reported a range across files) — keep something
        // visible rather than silently dropping.
        const end = endMap && endMap.name === startMap.name
            ? endMap
            : { name: startMap.name, line: startMap.line, character: startMap.character };
        const local: Diagnostic = {
            severity: d.severity,
            message: d.message,
            code: d.code,
            source: d.source,
            range: {
                start: { line: startMap.line, character: startMap.character },
                end:   { line: end.line,      character: end.character },
            },
        };
        return { name: startMap.name, diagnostic: local };
    }

    const markerFor = (d: Diagnostic): monaco.editor.IMarkerData => ({
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
    });

    lsp.setDiagnosticsHandler((uri, diagnostics) => {
        // Multi-source fan-out: when the LSP pushed diagnostics for the
        // joined project doc, bucket each entry by origin file (via
        // projectSourceMap.fromProject) and apply markers per-file. Files
        // listed in fade.json:sources but with NO diagnostics still get
        // their markers cleared — pre-seed empty buckets for every member.
        if (uri === PROJECT_LSP_URI && projectSourceMap) {
            const map = projectSourceMap;
            const byFile = new Map<string, Diagnostic[]>();
            for (const name of map.fileNames()) byFile.set(name, []);
            for (const d of diagnostics) {
                const split = splitProjectDiagnostic(d);
                if (!split) continue;
                byFile.get(split.name)!.push(split.diagnostic);
            }
            // Refresh semantic tokens once for the whole project — see
            // applyProjectSemanticTokens for the same fan-out shape.
            void applyProjectSemanticTokens();
            for (const [name, diags] of byFile) {
                const fileUri = monaco.Uri.file(`/workspace/${name}`).toString();
                const allModels = monaco.editor.getModels().filter((m) => m.uri.toString() === fileUri);
                const markers = diags.map(markerFor);
                for (const m of allModels) {
                    monaco.editor.setModelMarkers(m, 'fade', markers);
                }
                diagnosticsByUri.set(fileUri, diags);
            }
            renderProblems();
            const wasBlocked = lastBlockedByErrors;
            refreshRunButtons();
            if (wasBlocked && !lastBlockedByErrors) refreshDebounce();
            return;
        }

        // Single-file (orphan) path — unchanged from pre-multi-source.
        const allModels = monaco.editor.getModels().filter((m) => m.uri.toString() === uri);
        if (!allModels.length) return;
        for (const m of allModels) {
            void applySemanticTokens(m);
        }
        const markers = diagnostics.map(markerFor);
        for (const m of allModels) {
            monaco.editor.setModelMarkers(m, 'fade', markers);
        }

        diagnosticsByUri.set(uri, diagnostics);
        renderProblems();
        // Compile-error gate for Run / Debug / Export. Also re-trigger
        // test discovery when errors clear so the panel un-stalls without
        // the user having to type another keystroke.
        const wasBlocked = lastBlockedByErrors;
        refreshRunButtons();
        if (wasBlocked && !lastBlockedByErrors) refreshDebounce();
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

    // Shader diagnostics arrive via monaco.editor.setModelMarkers (not via
    // the FBasic LSP path that drives diagnosticsByUri), so renderProblems
    // doesn't see them without an explicit nudge. Subscribe to Monaco's
    // global marker-change event and re-render whenever a .fx file's
    // markers move. Debounced via microtask so a batch of setModelMarkers
    // calls in the same tick coalesces into a single re-render.
    let problemsRenderQueued = false;
    monaco.editor.onDidChangeMarkers((uris) => {
        const touched = uris.some(u => {
            const s = u.toString();
            return s.endsWith('.fx');
        });
        if (!touched) return;
        if (problemsRenderQueued) return;
        problemsRenderQueued = true;
        queueMicrotask(() => {
            problemsRenderQueued = false;
            renderProblems();
        });
    });

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

        // Shader diagnostics: the .fx validator (shader-validator.ts) and the
        // runtime KNI error marker (shader-markers.ts) publish via
        // monaco.editor.setModelMarkers under their own owner strings.
        // diagnosticsByUri only tracks FBasic LSP diagnostics, so without
        // this loop the Problems panel stays empty for .fx errors even
        // though squiggles appear in the editor. Read markers directly by
        // owner so we don't need a parallel state mirror.
        const SHADER_OWNERS = ['shader-static', 'shader-runtime'] as const;
        for (const model of monaco.editor.getModels()) {
            for (const owner of SHADER_OWNERS) {
                const markers = monaco.editor.getModelMarkers({ resource: model.uri, owner });
                for (const mk of markers) {
                    total++;
                    const li = document.createElement('li');
                    li.className = 'problem-item';

                    const sevName =
                        mk.severity === monaco.MarkerSeverity.Error   ? 'error'
                      : mk.severity === monaco.MarkerSeverity.Warning ? 'warning'
                      : 'info';
                    const icon = document.createElement('vscode-icon');
                    icon.setAttribute('name', sevName);
                    icon.className = sevName;

                    const msg = document.createElement('span');
                    msg.className = 'problem-message';
                    msg.textContent = mk.message;
                    if (mk.source) {
                        const code = document.createElement('span');
                        code.className = 'code';
                        code.textContent = mk.source;
                        msg.append(code);
                    }

                    const loc = document.createElement('span');
                    loc.className = 'problem-location';
                    loc.textContent = `${uriToName(model.uri.toString())}:${mk.startLineNumber}:${mk.startColumn}`;

                    li.append(icon, msg, loc);
                    li.onclick = () => {
                        const name = uriToName(model.uri.toString());
                        const tab = tabs.get(name);
                        if (tab && editor) {
                            editor.setModel(tab.model);
                            activeName = name;
                            renderTabs();
                            renderFileListSelection();
                            editor.revealPositionInCenter({ lineNumber: mk.startLineNumber, column: mk.startColumn });
                            editor.setPosition({ lineNumber: mk.startLineNumber, column: mk.startColumn });
                            editor.focus();
                        }
                    };
                    problemsList.append(li);
                }
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
        // Five-state indicator: idle (never run / will not be run),
        // queued (will run in this batch, waiting its turn), running
        // (live now), pass / fail (terminal), stopped (cancelled
        // mid-batch before a result was collected).
        status: 'idle' | 'queued' | 'running' | 'pass' | 'fail' | 'stopped';
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

    // Test discovery and failure frames carry positions in JOINED-doc
    // coordinates because runner.listTests / runner.runTests compile the
    // project's source through getProjectSource (the same joined buffer
    // the LSP sees). Click-to-jump callers need to reverse-map that into
    // (file, localLine) and tab-switch when the test or frame lives in a
    // file other than the one currently open. Falls back to plain
    // jumpEditorTo for single-file projects (projectSourceMap is null).
    async function jumpEditorToJoined(lineOneBased: number, columnOneBased: number = 1): Promise<void> {
        if (projectSourceMap) {
            const m = projectSourceMap.fromProject(lineOneBased - 1, columnOneBased - 1);
            if (m) {
                if (m.name !== activeName) {
                    try { await openFile(workspace, m.name); }
                    catch { /* fall through and jump on whatever's active */ }
                }
                jumpEditorTo(m.line + 1, m.character + 1);
                return;
            }
        }
        jumpEditorTo(lineOneBased, columnOneBased);
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
            appendOutputLine(text, kind, () => { void jumpEditorToJoined(ln, col); });
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
    let lastCommandDllsKey: string | null = null;

    // ─── Multi-source joined-doc state ───────────────────────────────────
    // The LSP only sees ONE document — a synthetic file whose text is every
    // in-project source's content concatenated in fade.json order. Providers
    // translate per-file Monaco positions through projectSourceMap before
    // talking to the LSP; diagnostics + tokens get reverse-translated and
    // fanned out per file. See [project-source-map.ts](src/project-source-map.ts).
    //
    // projectSourceMap is null until the first successful build (no fade.json,
    // or fade.json with no resolvable sources). Providers fall back to the
    // per-file LSP path in that case so a fresh / orphan-only workspace
    // behaves identically to the pre-multi-source code.
    let projectSourceMap: ProjectSourceMap | null = null;
    let lastPushedProjectText: string | null = null;

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

    // Synchronously read every in-project source's current text from its
    // Monaco model (no OPFS round-trip — every workspace file gets a model
    // at bootstrap; new files are created with a model up front). Skips
    // sources whose model is missing so a stale fade.json entry doesn't
    // break the rest of the build.
    function readProjectSourcesSync(): { name: string; text: string }[] {
        if (!currentProject) return [];
        const out: { name: string; text: string }[] = [];
        for (const name of currentProject.sources) {
            const uri = monaco.Uri.file(`/workspace/${name}`);
            const model = monaco.editor.getModel(uri);
            if (!model) continue;
            out.push({ name, text: model.getValue() });
        }
        return out;
    }

    // Returns true iff the given Monaco model URI maps to a file currently
    // listed in fade.json:sources. Providers gate on this to decide whether
    // to translate-and-route through the project doc or fall through to the
    // standalone per-file LSP path.
    function projectFileNameFromUri(uri: string): string | null {
        if (!projectSourceMap) return null;
        // Monaco URIs come back as file:///workspace/<name>; strip the prefix.
        const m = /^file:\/\/\/workspace\/(.+)$/.exec(uri);
        if (!m) return null;
        const name = m[1];
        return projectSourceMap.hasFile(name) ? name : null;
    }

    // Rebuild the joined project text from current model contents and push
    // it to the LSP. Idempotent: if the joined text matches what we last
    // pushed, skip the postMessage round-trip entirely. Returns true when a
    // push happened (callers can use this to know diagnostics will follow).
    function rebuildAndPushProjectDoc(force = false): boolean {
        const inputs = readProjectSourcesSync();
        if (inputs.length === 0) {
            projectSourceMap = null;
            // Clear the stale-push cache so the next non-empty build always
            // re-pushes (even if its joined text accidentally matches a
            // previous push).
            lastPushedProjectText = null;
            return false;
        }
        const map = ProjectSourceMap.build(inputs);
        projectSourceMap = map;
        if (!force && lastPushedProjectText === map.joined) return false;
        lastPushedProjectText = map.joined;
        runner.setDocument(PROJECT_LSP_URI, map.joined);
        return true;
    }

    // ─── Project-aware LSP wrappers ──────────────────────────────────────
    // Translate per-file (uri, line, char) into joined-doc coords when the
    // file is in fade.json:sources; pass through otherwise. Orphan files
    // still talk to the LSP under their own URI so they keep getting
    // per-file diagnostics + hover + goto-def the way they always did.
    //
    // These mostly just sit between the Monaco providers and `runner.getX`
    // — the LSP itself stays unaware that multiple "files" are sharing a
    // single document. See [project-source-map.ts](src/project-source-map.ts)
    // for the translation math.

    // Build the LSP URI a request should target. PROJECT_LSP_URI for any
    // file currently listed in fade.json:sources; the file's own URI for
    // orphans + fade.json itself.
    function lspUriFor(uri: string): string {
        return projectFileNameFromUri(uri) ? PROJECT_LSP_URI : uri;
    }

    // Translate a per-file (line, char) into joined-doc coords for an
    // outgoing LSP call. Pass-through for orphans.
    function toLspPosition(uri: string, line: number, character: number): { line: number; character: number } {
        const name = projectFileNameFromUri(uri);
        if (!name || !projectSourceMap) return { line, character };
        return projectSourceMap.toProject(name, line, character) ?? { line, character };
    }

    // Reverse-translate a (joinedLine, char) returned by the LSP into the
    // file it belongs to. `originalUri` is the URI the request came in on
    // — when the LSP response refers to a *different* file (cross-file
    // goto-def / references), this returns the URI of the *target* file
    // and the local line within it. For orphans, returns originalUri
    // unchanged.
    function fromLspLocation(
        originalUri: string,
        joinedLine: number,
        character: number,
    ): { uri: string; line: number; character: number } {
        if (!projectFileNameFromUri(originalUri) || !projectSourceMap) {
            return { uri: originalUri, line: joinedLine, character };
        }
        const mapped = projectSourceMap.fromProject(joinedLine, character);
        if (!mapped) return { uri: originalUri, line: joinedLine, character };
        return {
            uri: monaco.Uri.file(`/workspace/${mapped.name}`).toString(),
            line: mapped.line,
            character: mapped.character,
        };
    }

    // Translate a range whose start + end are both expected to belong to
    // the SAME file as `originalUri` (hover ranges, completion ranges,
    // selectionRange of a symbol entry). When the file is in-project this
    // just subtracts the file's startLine from both endpoints.
    function rangeFromLsp(
        originalUri: string,
        range: { start: { line: number; character: number }; end: { line: number; character: number } },
    ): { start: { line: number; character: number }; end: { line: number; character: number } } {
        const name = projectFileNameFromUri(originalUri);
        if (!name || !projectSourceMap) return range;
        const r = projectSourceMap.ranges.find((x) => x.name === name);
        if (!r) return range;
        return {
            start: { line: range.start.line - r.startLine, character: range.start.character },
            end:   { line: range.end.line   - r.startLine, character: range.end.character   },
        };
    }

    // Is this joined-line within the originalUri's slice of the project?
    // Used to filter document-symbol / folding / format edits so a request
    // on file B doesn't return entries that belong to file A.
    function joinedLineBelongsTo(originalUri: string, joinedLine: number): boolean {
        const name = projectFileNameFromUri(originalUri);
        if (!name || !projectSourceMap) return true;
        const r = projectSourceMap.ranges.find((x) => x.name === name);
        return !!r && joinedLine >= r.startLine && joinedLine < r.endLine;
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
                // Evict any stale owner='fade' markers from non-fade models
                // (e.g. fade.json) so squiggles left by an earlier mis-push
                // don't linger. Fade models are NOT re-pushed here — the
                // wantedDllsKey branch below always fires on a type change
                // (because the type is part of the key) and does the
                // authoritative push after command DLLs are registered.
                // Pushing fade models here, before DLLs load, causes a
                // transient flash of "unknown command" errors that clears
                // once the DLLs arrive.
                for (const model of monaco.editor.getModels()) {
                    if (model.getLanguageId() !== 'fade') {
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

        // Sync command DLLs with both workers. Re-run whenever the project
        // type OR commandDlls list changes. Type-defaults (e.g. FadeBasic.Lib.Web
        // for 'web' projects) are injected automatically here — same intent as
        // the auto-injection in FadeBasic.Export.Web.targets for csproj consumers.
        const wantedDllsKey = JSON.stringify([wantedType, currentProject?.commandDlls ?? []]);
        if (wantedDllsKey !== lastCommandDllsKey) {
            lastCommandDllsKey = wantedDllsKey;
            try {
                await runner.clearCommandAssemblies();
                // Per-type LSP setup. `preloadAssemblies` are loaded into
                // the LSP's AppDomain *without* being registered as command
                // sources — they exist to satisfy referenced-assembly
                // resolution when the actual command class is
                // Activator.CreateInstance'd. typeDefaults are the command
                // classes themselves.
                //
                // Monogame: Fade.MonoGame.Lib has ProjectReferences to
                // Fade.MonoGame.Game + Fade.MonoGame.Contracts. Pre-loading
                // them is cheap insurance against AppDomain resolution
                // hiccups during type init. MonoGame.Framework + KNI are
                // intentionally NOT preloaded — they're huge and the LSP
                // only enumerates command metadata (method bodies aren't
                // JITed until called, which never happens in the LSP).
                const typeDefaults: CommandDllEntry[] = wantedType === 'web'
                    ? [{ assembly: 'FadeBasic.Lib.Web', class: 'FadeBasic.Lib.Web.WebCommands' }]
                    : wantedType === 'monogame'
                    ? [{ assembly: 'Fade.MonoGame.Lib', class: 'Fade.MonoGame.Lib.FadeMonoGameCommands' }]
                    : [];
                const preloadAssemblies: string[] = wantedType === 'monogame'
                    ? ['Fade.MonoGame.Contracts', 'Fade.MonoGame.Game']
                    : [];
                for (const name of preloadAssemblies) {
                    try {
                        const resp = await fetch(`/runtime/fade-libs/${name}.dll`);
                        if (!resp.ok) {
                            console.warn(`[fade] preload DLL not found: /runtime/fade-libs/${name}.dll (${resp.status})`);
                            continue;
                        }
                        const bytes = await resp.arrayBuffer();
                        const result = await runner.loadAssembly(bytes);
                        if (!result.ok) console.warn(`[fade] preload ${name} failed: ${result.error}`);
                    } catch (e) {
                        console.warn(`[fade] failed to preload ${name}`, e);
                    }
                }
                const allEntries = [...typeDefaults, ...(currentProject?.commandDlls ?? [])];
                for (const entry of allEntries) {
                    try {
                        const resp = await fetch(`/runtime/fade-libs/${entry.assembly}.dll`);
                        if (!resp.ok) {
                            console.warn(`[fade] DLL not found: /runtime/fade-libs/${entry.assembly}.dll (${resp.status})`);
                            continue;
                        }
                        const bytes = await resp.arrayBuffer();
                        const result = await runner.registerCommandAssembly(bytes, entry.class);
                        if (!result.ok) console.warn(`[fade] registerCommandAssembly failed: ${result.error}`);
                    } catch (e) {
                        console.warn(`[fade] failed to load ${entry.assembly}`, e);
                    }
                }
                // Re-push documents so LSP picks up the new command surface.
                // In-project sources go through the joined project doc;
                // orphan fade files (not in fade.json:sources) keep their
                // standalone push path so they still get diagnostics.
                rebuildAndPushProjectDoc(true);
                for (const model of monaco.editor.getModels()) {
                    if (model.getLanguageId() !== 'fade') continue;
                    const uri = model.uri.toString();
                    if (projectFileNameFromUri(uri)) continue;
                    runner.setDocument(uri, model.getValue());
                }
                renderProblems();
                refreshHelpEntriesFromWorker?.();
            } catch (e) {
                console.warn('[fade] commandDlls sync failed', e);
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
        // fade.json schema errors count toward projectHasCompileErrors so
        // Run/Debug stay locked on a broken manifest too.
        refreshRunButtons();
        // Republish for module-scope renderers (file list badges) and
        // re-render so the source-order indicators update immediately.
        currentProjectRef = currentProject;
        renderFileList(workspace).catch(() => { /* ignore */ });
        // Source-control panel needs to re-bind to the new project's sync
        // index (different repo, different baseTree, different status).
        sharingController?.setActiveProject(workspace.currentProject());
        // Title bar reflects the resolved project name.
        if (currentProject?.name) {
            const hasErrors = currentProjectErrors.some((e) => e.severity === 'error');
            setProjectStatus(hasErrors ? `${currentProject.name} (fade.json invalid)` : currentProject.name);
        } else {
            setProjectStatus(workspace.currentProject() + ' (fade.json invalid)');
        }

        // Rebuild + push the joined project doc whenever fade.json's source
        // list changes (or its order does). The DLL-reload branch above
        // already force-pushes when type/commandDlls flip; this catches the
        // far more common case of editing just `sources[]`.
        rebuildAndPushProjectDoc();
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
            // Pre-fill the prompt with just the basename so a rename inside a
            // folder doesn't accidentally move the file to root. If the user
            // wants to move while renaming, they can still type a full
            // slashed path — that takes precedence.
            const lastSlash = oldName.lastIndexOf('/');
            const dirPrefix = lastSlash >= 0 ? oldName.slice(0, lastSlash + 1) : '';
            const oldBase = lastSlash >= 0 ? oldName.slice(lastSlash + 1) : oldName;
            const input = prompt(`Rename "${oldName}" to:`, oldBase);
            if (!input || input === oldBase) return;
            const newName = input.includes('/') ? input : dirPrefix + input;
            if (newName === oldName) return;
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
            unregisterVirtualFile(oldUri);
            // Recreate at new URI.
            const newModel = monaco.editor.createModel(text, languageFor(newName), newUri);
            // Move tab entry if open.
            const oldTab = tabs.get(oldName);
            if (oldTab) {
                tabs.delete(oldName);
                const newTab: Tab = { name: newName, model: newModel, dirty: false };
                newTab.model.onDidChangeContent(() => {
                    newTab.dirty = true;
                    sharingController?.setHasDirtyTabs(true);
                    clearTimeout(newTab.saveTimer);
                    newTab.saveTimer = window.setTimeout(async () => {
                        try {
                            await workspace.write(newTab.name, newTab.model.getValue());
                            newTab.dirty = false;
                            if (!anyTabDirty()) sharingController?.setHasDirtyTabs(false);
                            sharingController?.invalidateHashFor(newTab.name);
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
            unregisterVirtualFile(uri);
            try {
                await workspace.delete(name);
            } catch (e: any) {
                alert('Delete failed: ' + (e?.message ?? e));
                return;
            }
            // If the deleted file was a listed source, remove it from
            // fade.json so we don't trip the missing-source error. For
            // folder deletes, drop every source under the folder too.
            await mutateManifest((p) => {
                const folderPrefix = name + '/';
                const filtered = p.sources.filter(
                    (s) => s !== name && !s.startsWith(folderPrefix),
                );
                if (filtered.length === p.sources.length) return null;
                return { ...p, sources: filtered };
            });
            await refreshFadeProject();
            renderTabs();
            await renderFileList(workspace);
        },
        createFolder: async (path) => {
            // Validate per-segment so any nesting depth is fine. The
            // workspace.mkdir helper happily creates intermediate dirs
            // on demand; we just guard against bad characters here.
            for (const seg of path.split('/')) {
                if (!seg) continue;
                if (!/^[\w.\-]+$/.test(seg)) {
                    alert(`Invalid folder name segment "${seg}". Letters, digits, dot, dash, underscore only.`);
                    return;
                }
            }
            try { await workspace.mkdir(path); }
            catch (e: any) { alert('Create folder failed: ' + (e?.message ?? e)); return; }
            await renderFileList(workspace);
        },
        inlineCreateFile: (ext, parentFolder) => {
            void startInlineCreate(ext, parentFolder);
        },
        inlineCreateFolder: (parentFolder) => {
            void startInlineFolderCreate(parentFolder);
        },
        renamePath: async (oldPath, newPath) => {
            if (oldPath === newPath) return;
            // Collect every tab whose path lives under the moved tree —
            // we'll reopen them at their new paths after the rename so
            // the user doesn't lose editor state. (For now we just
            // close them — phase 5 wires the reopen path through the
            // tab system's path-aware identity.)
            const folderPrefix = oldPath + '/';
            const affectedTabs: string[] = [];
            for (const [tabName] of tabs) {
                if (tabName === oldPath || tabName.startsWith(folderPrefix)) {
                    affectedTabs.push(tabName);
                }
            }
            for (const tabName of affectedTabs) closeTab(tabName);
            try { await workspace.rename(oldPath, newPath); }
            catch (e: any) { alert('Rename failed: ' + (e?.message ?? e)); return; }
            // Update fade.json: any source matching oldPath exactly, or
            // sitting under oldPath/, gets rewritten to its new home.
            await mutateManifest((p) => {
                let touched = false;
                const updated = p.sources.map((s) => {
                    if (s === oldPath) { touched = true; return newPath; }
                    if (s.startsWith(folderPrefix)) {
                        touched = true;
                        return newPath + '/' + s.slice(folderPrefix.length);
                    }
                    return s;
                });
                return touched ? { ...p, sources: updated } : null;
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
    //
    // Goes through ProjectSourceMap.build so the joined text the runtime/
    // debugger compiles is byte-identical to what the LSP sees via
    // rebuildAndPushProjectDoc. parts.join('\n') used to differ when a
    // file ended with '\n' (it added a phantom blank line between files)
    // which shifted all subsequent line numbers and made LSP-reported
    // ranges disagree with runtime stack frames.
    async function getProjectSource(): Promise<string> {
        if (!currentProject) return getActiveSource();
        const inputs: { name: string; text: string }[] = [];
        for (const name of currentProject.sources) {
            inputs.push({ name, text: await readFile(name) });
        }
        return ProjectSourceMap.build(inputs).joined;
    }

    async function refreshTests() {
        const source = await getProjectSource();
        if (!source) {
            testEntries = [];
            renderTests();
            return;
        }
        // Skip discovery when the project has compile errors. For monogame
        // this prevents the Blazor iframe from throwing on unparseable source
        // (and force-booting the 8MB WASM before the user hits Run). For web
        // projects it avoids spamming the console with mid-keystroke compile
        // errors from the worker's listTests call. The existing test list stays
        // visible until errors clear; the error-clear path in the diagnostics
        // handler re-triggers the debounce so discovery resumes automatically.
        if (projectHasCompileErrors()) {
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

    // Any error-severity diagnostic in any current-project source file (or
    // a schema error in fade.json) gates the Run / Debug / Export buttons
    // and the monogame background test-discovery round-trip. Source-file
    // diagnostics come from the LSP push poll into diagnosticsByUri;
    // fade.json schema errors live in currentProjectErrors.
    function projectHasCompileErrors(): boolean {
        if (currentProjectErrors.some((e) => e.severity === 'error')) return true;
        if (!currentProject) return false;
        for (const name of currentProject.sources) {
            const uri = monaco.Uri.file(`/workspace/${name}`).toString();
            const diags = diagnosticsByUri.get(uri);
            if (diags?.some((d) => d.severity === 1)) return true;
        }
        return false;
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
            name.onclick = () => { void jumpEditorToJoined((t.sourceLine | 0) + 1, ((t.sourceChar | 0) + 1) || 1); };
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
                    const joinedLn = (frame.lineNumber | 0) + 1;
                    const joinedCol = ((frame.charNumber | 0) + 1) || 1;
                    // Reverse-map for display so the label shows the file +
                    // local line the user expects, not the joined-doc line.
                    const mapped = projectSourceMap?.fromProject(joinedLn - 1, joinedCol - 1);
                    const displayLn = mapped ? mapped.line + 1 : joinedLn;
                    const fn = frame.functionName ? frame.functionName + '() ' : '';
                    const where = mapped && mapped.name !== activeName
                        ? `${mapped.name}:${displayLn}` : `line ${displayLn}`;
                    loc.textContent = `↳ ${fn}${where}`;
                    loc.onclick = (e) => { e.stopPropagation(); void jumpEditorToJoined(joinedLn, joinedCol); };
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
            case 'stopped': return 'debug-stop';
            // The pulsing for `queued` and `running` comes from CSS;
            // the glyph itself is a solid dot so the pulse reads as
            // opacity-on-color rather than as a moving shape.
            case 'queued':  return 'circle-filled';
            case 'running': return 'circle-filled';
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
        testsBusy = busy;
        testsRunAllBtn.disabled = busy;
        testsRefreshBtn.disabled = busy;
        refreshStopButton();
        // Mirror run/debug — broadcast the test-running state to peers.
        // refreshRunButtons isn't called here because tests don't gate
        // the Run/Debug buttons, so we call the activity broadcaster
        // directly. Guarded against the live-session helper not being
        // defined yet (early-boot test panels can run before bootstrap
        // finishes mounting the live-session UI).
        try { broadcastLiveActivity(); }
        catch { /* helper not in scope yet during early boot */ }
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
        // Test Mode semantic layout — focus Tests + Game. Skipped when a
        // debug session is active (debugSingleTest already applied Debug
        // Mode and we don't want to fight it).
        if (!debugSessionActive) {
            try { applySemanticLayout('test'); } catch (e) { console.warn('[fade] applySemanticLayout(test) failed', e); }
        }
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
            } else {
                // Surface the test run in the Game tab — same iframe the
                // user sees prints/prompts in during a Run. Ensures debug
                // controls behave identically across run/test/debug.
                showGameSurface('web');
                revealPanel('game');
                await ensureWebPreviewArmed();
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
        // Test Mode semantic layout — focus Tests + Game. Skipped when a
        // debug session is active (Debug Mode takes precedence).
        if (!debugSessionActive) {
            try { applySemanticLayout('test'); } catch (e) { console.warn('[fade] applySemanticLayout(test) failed', e); }
        }
        // Mark every runnable test as queued (grey pulse). As the
        // cooperative pump advances, each test flips to 'running'
        // (yellow pulse) via test-progress's neighbor signal, then
        // 'pass'/'fail' as it completes.
        for (const t of testEntries) {
            if (!t.isAbstract) {
                t.status = 'queued';
                t.failure = null;
                t.failureFrames = undefined;
                t.duration = undefined;
            }
        }
        // The first queued test is about to execute — flip it to
        // 'running' now so the user immediately sees the yellow pulse
        // on the row that's live. Subsequent transitions ride on the
        // test-progress event in runner.onTestProgress below.
        const firstRunnable = testEntries.find((t) => !t.isAbstract);
        if (firstRunnable) firstRunnable.status = 'running';
        renderTests();
        setTestsBusy(true);
        testsStatusEl.textContent = 'Running…';
        clearOutput();
        revealPanel('output');
        appendTestLog(`▶ Run all`, 'dim');
        try {
            if (currentProject?.type === 'monogame') {
                await syncAssetsToRuntime();
            } else {
                showGameSurface('web');
                revealPanel('game');
                await ensureWebPreviewArmed();
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
        if (r.error === 'Stopped') {
            // User cancelled mid-batch. Any test that already finalized
            // (in r.results) gets its real pass/fail below; tests that
            // were queued or in-flight when Stop fired flip to 'stopped'
            // (purple steady) so the UI shows what didn't get to run.
            for (const res of r.results) {
                const e = testEntries.find((t) => t.name === res.name);
                if (!e) continue;
                e.status = res.passed ? 'pass' : 'fail';
                e.duration = res.duration;
                e.failure = res.passed ? null : (res.failureMessage || res.failureReason || 'Failed');
                e.failureFrames = res.passed ? undefined : (res.failureFrames || []);
            }
            for (const t of testEntries) {
                if (t.status === 'queued' || t.status === 'running') {
                    t.status = 'stopped';
                }
            }
            testsStatusEl.textContent = 'Stopped';
            appendTestLog('Test run stopped.', 'dim');
            renderTests();
            return;
        }
        if (r.error) {
            // r.error is typically a compile-failure dump. Those same
            // errors already streamed through LSP → Problems with proper
            // line/col pins; we'd just be duplicating text here. Show a
            // short pointer instead and reveal Problems.
            appendTestLog('Test compile failed. See Problems panel.', 'fail');
            revealPanel('problems');
            for (const t of testEntries) {
                if (t.status === 'running' || t.status === 'queued') t.status = 'idle';
            }
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
            // 1500ms: long enough that LSP diagnostics (which gate the
            // compile-error check inside refreshTests) have time to arrive
            // from the worker before we attempt a listTests compile. The
            // 250ms doc-push poll + 400ms was too short — listTests fired
            // mid-keystroke before the LSP had a chance to report errors.
            timer = window.setTimeout(refreshTests, 1500);
        };
    })();

    // Surface synchronous prompt$ via window.prompt for now. A nicer modal
    // can replace this later.
    runner.onPromptRequest = (msg) => window.prompt(msg, '');

    // Per-test streaming: flip each testEntry row from "running" to
    // pass/fail as soon as the test finalizes (mid-run), instead of
    // waiting for the terminal envelope at the end of the batch.
    // applyResult (called with the terminal envelope) handles the
    // same fields plus the headline + appendTestLog spam, so this
    // listener is a strict subset — just enough to update the row.
    //
    // Also advances the queued→running transition for the NEXT non-
    // abstract queued test. The cooperative pump has already started
    // executing it (AdvanceTest set _runVm before the progress event
    // for the prior test fired); we just mirror that state in the UI.
    runner.onTestProgress = (result) => {
        const i = testEntries.findIndex((t) => t.name === result.name);
        if (i < 0) return;
        const e = testEntries[i];
        e.status = result.passed ? 'pass' : 'fail';
        e.duration = result.duration;
        e.failure = result.passed
            ? null
            : (result.failureMessage || result.failureReason || 'Failed');
        e.failureFrames = result.passed ? undefined : (result.failureFrames || []);
        // Promote the next queued runnable to 'running'. Walking
        // forward from the just-finalized index is correct because
        // the worker runs tests in manifest order (same order
        // testEntries is built from).
        for (let j = i + 1; j < testEntries.length; j++) {
            const n = testEntries[j];
            if (!n.isAbstract && n.status === 'queued') {
                n.status = 'running';
                break;
            }
        }
        renderTests();
    };

    pgSplash?.setStatus('Loading workspace…');

    // `?reset=1` (or `?fresh=1`) wipes OPFS + workspace localStorage
    // BEFORE init runs, so each reload puts you back at the first-run
    // picker. Strictly a dev/test affordance — the param is stripped
    // from the URL after the wipe so a refresh doesn't re-wipe.
    const urlParams = new URLSearchParams(location.search);
    if (urlParams.get('reset') === '1' || urlParams.get('fresh') === '1') {
        try {
            const root = await navigator.storage.getDirectory();
            try { await root.removeEntry('workspace', { recursive: true }); }
            catch { /* nothing to remove */ }
        } catch (e) {
            console.error('[fade] ?reset wipe failed', e);
        }
        try {
            // LAYOUT_STORAGE_KEY is declared later in bootstrap; inline the
            // literal so this guard runs before it's in scope.
            localStorage.removeItem('fade.dockview.layout.v8');
            localStorage.removeItem(ACTIVE_PROJECT_KEY);
        } catch { /* ignore */ }
        urlParams.delete('reset');
        urlParams.delete('fresh');
        const qs = urlParams.toString();
        history.replaceState(null, '', location.pathname + (qs ? '?' + qs : ''));
        console.info('[fade] ?reset processed — first-run picker will show next');
    }

    const workspace = new OpfsWorkspace();
    // Wipe any stale live-session sandboxes from a previous reload BEFORE
    // workspace.init() picks an active project — otherwise we could end
    // up "active" on a `__live_session_*` folder that's about to be deleted.
    try {
        const opfs = await navigator.storage.getDirectory();
        const workspaceRoot = await opfs.getDirectoryHandle('workspace', { create: true });
        const stale: string[] = [];
        for await (const entry of (workspaceRoot as any).values()) {
            if (entry.kind === 'directory' && isLiveSessionProjectName(entry.name)) {
                stale.push(entry.name);
            }
        }
        if (stale.length) {
            // If the previously-active project was a stale live-session,
            // drop the localStorage pointer so workspace.init() picks a
            // real project. (workspace.init falls back to the first
            // project alphabetically if the stored name isn't present.)
            const prevActive = localStorage.getItem(ACTIVE_PROJECT_KEY);
            if (prevActive && isLiveSessionProjectName(prevActive)) {
                localStorage.removeItem(ACTIVE_PROJECT_KEY);
            }
            for (const name of stale) {
                try { await workspaceRoot.removeEntry(name, { recursive: true }); }
                catch (e) { console.warn('[fade-collab] failed to clean stale live-session project', name, e); }
            }
            console.info(`[fade-collab] cleaned ${stale.length} stale live-session project(s)`);
        }
    } catch (e) {
        console.warn('[fade-collab] live-session startup cleanup failed', e);
    }
    const hadExistingProject = await workspace.init();
    if (!hadExistingProject) {
        // Brand-new install (or post-reset). Hide the splash, show the
        // first-run modal, and stop booting. The submit handler reloads
        // the page so the next pass finds a project on disk and skips
        // this branch entirely.
        pgSplash?.hide();
        await runFirstRunFlow(workspace);
        return;
    }

    // Settings: load user (localStorage) + workspace (<project>/.fade/settings.json).
    // Must complete before the editor is created so initial font/tab/etc.
    // match what the user configured. Re-fired on project switch.
    pgSplash?.setStatus('Loading settings…');
    await initSettings({
        read: (p) => workspace.read(p),
        write: (p, c) => workspace.write(p, c),
        mkdir: (p) => workspace.mkdir(p),
        currentProject: () => workspace.currentProject(),
    });
    // Set the [data-theme] attribute as early as possible so the splash and
    // first paint already match the user's choice (avoids a dark→light flash
    // for users on the light theme). Monaco-side theme switch happens later
    // when the editor mounts — see the onSettingsChange listener.
    applyTheme(currentSettings());

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
    // v5 added: source-control, logs, history. Users on v4 don't have
    // these panels in their saved layout; bumping the key forces a clean
    // rebuild so the new tabs appear. healLayout also lists them as
    // missing-defaults, but bumping is the simpler guarantee.
    // v6 renamed 'source-control' panel id → 'collaboration'. Stored
    // v5 layouts still reference the old id; bump again so they get a
    // default rebuild instead of dockview discarding the renamed panel.
    // v7 dropped Collaboration / Logs / History from the default tab strip
    // (they now open into the editor tab group on demand), folded Debug
    // into the Workspace tab group, and changed which tabs are focused on
    // startup. Bump forces a clean rebuild for users on v6.
    // v8 moved Tests from the bottom tab group into the Workspace tab
    // group (so the left column tabs are Workspace / Debug / Tests).
    // Bump again so existing v7 users get the rebuild.
    // v9 added the Debug UI panel (Tweakpane-driven counterpart to the
    // desktop ImGui dev UI) to the right-column tab group beside Game
    // and Help. Existing v8 users don't have it; bumping the key forces
    // a clean rebuild so the new tab appears.
    // v10 added the Inspector panel (provider-driven live state browser)
    // next to Debug UI. Existing v9 users would have a missing tab
    // without this bump — healLayout's addMissing would patch it but
    // the explicit version bump is cleaner.
    // v11 removed the standalone Debug UI tab — Inspector now subsumes
    // its fbasic-widgets functionality. v10 layouts with a Debug UI
    // tab still present would be patched by healLayout (which drops
    // unknown components), but the explicit version bump avoids the
    // intermediate state.
    // v13: collapsed everything back into a single "Debug UI" tab.
    // Each fbasic `begin debug window` is a top-level folder; the
    // Inspector becomes a top-level "Inspector" folder gated on
    // `enable debug inspector`. The separate inspector tab and the
    // per-window fbasic-window:* tabs from v12 are removed.
    const LAYOUT_STORAGE_KEY = 'fade.dockview.layout.v13';

    function setupDockview(): DockviewApi {
        const dockRoot = document.getElementById('dock-root')!;
        const panelCells = document.getElementById('panel-cells')!;

        const dock = createDockview(dockRoot, {
            // Built-in VSCode-like dark theme — matches the rest of the
            // playground's styling (vs-dark Monaco theme, vscode-elements).
            theme: { name: 'vs', className: 'dockview-theme-vs' },
            disableFloatingGroups: false,
            // Right-click menu on dockview tabs (Output, Tests, Debug
            // Console, etc.). Built-ins ('close' / 'closeOthers' / 'closeAll')
            // are shipped by dockview-core; the focus + left/right
            // variants are custom entries that map to panel.api calls.
            // group.panels is in tab order, so idx gives left/right.
            getTabContextMenuItems: ({ panel, group }) => {
                const list = group.panels;
                const idx = list.indexOf(panel);
                const isActive = group.activePanel === panel;
                const leftCount = idx;
                const rightCount = list.length - idx - 1;
                return [
                    {
                        label: 'Focus tab',
                        disabled: isActive,
                        action: () => panel.api.setActive(),
                    },
                    'separator',
                    'close',
                    'closeOthers',
                    {
                        label: 'Close tabs to the left',
                        disabled: leftCount === 0,
                        action: () => {
                            for (const p of list.slice(0, idx)) p.api.close();
                        },
                    },
                    {
                        label: 'Close tabs to the right',
                        disabled: rightCount === 0,
                        action: () => {
                            for (const p of list.slice(idx + 1)) p.api.close();
                        },
                    },
                    'separator',
                    'closeAll',
                ];
            },
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
                if (name === 'catalog') {
                    // Singleton dockview panel. The shared CatalogClient holds
                    // the manifest + IDB-backed indices in module scope, so
                    // reopening the panel after a close is instant. Imports
                    // land under catalog-imports/ and trigger the same
                    // syncAssetsToRuntime path that Run/Test use, so the
                    // imported file is registered immediately.
                    return createCatalogPanel({
                        client: sharedCatalogClient,
                        writeBytes: (p, b) => workspace.writeBytes(p, b),
                        exists: (p) => workspace.exists(p),
                        onImported: async () => {
                            await renderFileList(workspace);
                            try { await syncAssetsToRuntime(); } catch (e) {
                                console.error('[fade] catalog import: syncAssetsToRuntime failed', e);
                            }
                        },
                    });
                }
                if (name === 'diff-viewer') {
                    // Read-only Monaco diff editor. Params arrive via
                    // init({ params }) — caller is responsible for
                    // fetching the before/after strings before opening
                    // (see openDiffViewer below) since dockview's
                    // createComponent only gets {id, name} synchronously.
                    return createDiffViewer();
                }
                if (name === 'conflict-editor') {
                    // id encodes the path: `conflict-editor:<path>`.
                    const path = id.startsWith('conflict-editor:')
                        ? id.slice('conflict-editor:'.length)
                        : '';
                    const element = document.createElement('div');
                    element.style.height = '100%';
                    element.style.width = '100%';
                    let handle: { dispose(): void } | null = null;
                    return {
                        element,
                        init() {
                            if (!path) {
                                element.textContent = 'conflict-editor missing path in panel id';
                                return;
                            }
                            // Read the file fresh from OPFS so the conflict
                            // editor has the on-disk content as its starting
                            // point. The conflict editor creates its own
                            // throwaway Monaco model — autosave only fires
                            // for the regular tab's model, which we never
                            // touch from here.
                            (async () => {
                                let initialContent = '';
                                try {
                                    initialContent = await workspace.read(path);
                                } catch (e) {
                                    element.textContent = `conflict-editor: cannot read ${path}: ${(e as Error).message}`;
                                    return;
                                }
                                handle = mountConflictEditor({
                                    container: element,
                                    path,
                                    initialContent,
                                    languageId: languageFor(path),
                                    onSave: async (resolvedPath, content) => {
                                        try {
                                            await workspace.write(resolvedPath, content);
                                            // Update the regular tab's model
                                            // (if open) so it reflects the
                                            // resolved content immediately.
                                            const uri = monaco.Uri.file(`/workspace/${resolvedPath}`);
                                            const existingModel = monaco.editor.getModel(uri);
                                            if (existingModel && existingModel.getValue() !== content) {
                                                existingModel.setValue(content);
                                            }
                                            // Reflect change in the visible tab list.
                                            const tab = tabs.get(resolvedPath);
                                            if (tab) tab.dirty = false;
                                            renderTabs();
                                        } catch (e) {
                                            console.error('[fade] conflict-editor save failed', e);
                                        }
                                        try { dock.getPanel(`conflict-editor:${resolvedPath}`)?.api.close(); } catch { /* ignore */ }
                                        await sharingController?.refreshStatus();
                                    },
                                    onClose: () => {
                                        try { dock.getPanel(`conflict-editor:${path}`)?.api.close(); } catch { /* ignore */ }
                                    },
                                });
                            })();
                        },
                        dispose() {
                            handle?.dispose();
                            handle = null;
                        },
                    };
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
        'workspace', 'editor', 'debug', 'search', 'settings',
        'output', 'problems', 'tests', 'debug-console',
        'game', 'help', 'diagnostics',
        // Dynamic — created on demand by the markdown preview button.
        'markdown-preview',
        // Dynamic — created on demand when a binary file is opened
        // from the workspace tree (XNB, PNG, WAV, …).
        'binary-preview',
        'ai-chat',
        'ai-models',
        'collaboration',
        'logs',
        'history',
        'live-session',
        // Unified browser debug UI. Single tab "Debug UI" that hosts:
        //   - One folder per fbasic-emitted `begin debug window` block
        //   - An optional "Inspector" folder (gated on `enable debug
        //     inspector`) with Metadata + IDebugProvider entity browsers
        'debug-ui',
        // Dynamic — one per conflict file; created when the collaboration
        // panel's "Resolve in editor →" button opens a file.
        'conflict-editor',
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
                position: { referencePanel: dock.getPanel('workspace')?.id ?? 'editor', direction: 'within' },
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
                position: { referencePanel: dock.getPanel('workspace')?.id ?? 'workspace', direction: 'within' },
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
            addMissing('debug-ui', {
                position: { referencePanel: helpRef, direction: 'within' },
                renderer: RENDER_ALWAYS, title: 'Debug UI',
            });
            // Collaboration / Logs / History are no longer part of the
            // default tab strip — they open into the editor tab group on
            // demand via openPanelById. If a restored layout already
            // contained them (e.g. the user opened them previously and
            // dockview persisted the position), we leave them where they
            // are. If absent, we deliberately do NOT re-add them here.
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
        // Workspace tab group: Workspace / Debug / Tests. Single left
        // column with the file tree, the debugger, and the test list as
        // tabs. Users flip between them with one click instead of losing
        // vertical real estate to stacked sub-panes.
        dock.addPanel({
            id: 'debug',
            component: 'debug',
            title: 'Debug',
            position: { referencePanel: workspacePanel.id, direction: 'within' },
            renderer: RENDER_ALWAYS,
        });
        dock.addPanel({
            id: 'tests',
            component: 'tests',
            title: 'Tests',
            position: { referencePanel: workspacePanel.id, direction: 'within' },
            renderer: RENDER_ALWAYS,
        });
        // Bottom tab group: Output / Problems / Debug Console.
        // Default height kept modest — the editor + game canvas should
        // dominate the viewport, with the bottom panel showing a few lines
        // of output by default. Users can drag the splitter taller when
        // they want to dig in. Note: Collaboration / Logs / History are
        // NOT added here — they open into the editor tab group on demand
        // via openPanelById.
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
        // Debug UI: unified Tweakpane panel — one folder per fbasic
        // `begin debug window`, plus an optional Inspector folder
        // when the program calls `enable debug inspector`.
        dock.addPanel({
            id: 'debug-ui',
            component: 'debug-ui',
            title: 'Debug UI',
            position: { referencePanel: gamePanel.id, direction: 'within' },
            renderer: RENDER_ALWAYS,
        });
        // Default-focused tabs in each group: Workspace, Editor, Help, Problems.
        // setActive() on a panel activates it within its own group, so calling
        // it on one panel per group gives the user the intended startup view.
        try { dock.getPanel('workspace')?.api?.setActive(); } catch { /* ignore */ }
        try { dock.getPanel('editor')?.api?.setActive(); } catch { /* ignore */ }
        try { dock.getPanel('help')?.api?.setActive(); } catch { /* ignore */ }
        try { dock.getPanel('problems')?.api?.setActive(); } catch { /* ignore */ }

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
    pgSplash?.setStatus('Mounting layout…');
    const dockApi = setupDockview();
    // Expose for tests + future "Reset layout" command.
    (window as any).__fadeDockview = dockApi;

    // Logs panel: subscribes to the app-wide LogBus and renders a filterable
    // terminal-style log feed. Mounted once at boot; survives panel
    // tab-switches because the dockview component renderer is 'always'.
    const logsHost = document.getElementById('logs-host');
    if (logsHost) {
        mountLogsPanel({ container: logsHost });
        // Surface a couple of app-lifecycle events to the bus so the panel
        // isn't empty on first open. Future: pipe LSP / monogame events too.
        const bootLog = getLogger('app');
        bootLog.info('Playground booted');
    }

    // Unified Debug UI panel. One Tweakpane root contains both the
    // user's custom debug windows (from fbasic `begin debug window`)
    // as top-level folders + an "Inspector" folder when the program
    // calls `enable debug inspector`.
    const debugUiHost = document.getElementById('debug-ui-host');
    // Resolve the peer ID of whoever's currently running/debugging in
    // the live session, if anyone. Used to RPC debug-UI operations to
    // the host when this peer is observing — otherwise the calls go to
    // the local monoGameHost (which has no live iframe on the observer
    // side and returns nothing useful).
    function getRemoteRunnerPeerId(): string | null {
        const session = liveSessionHandle?.getSession();
        if (!session) return null;
        const peers = session.getState().peers;
        const runner = peers.find((p) => !p.isSelf
            && (p.activity === 'running' || p.activity === 'debugging'));
        return runner?.peerId ?? null;
    }
    // RPC dispatch helper for the debug-UI panel callbacks. Each panel
    // callback is one method on monoGameHost; when we're observing, we
    // route to the host's `debugUi:*` handler instead. The 8s timeout
    // matches typical debug RPC budget — schema/list calls round-trip
    // quickly but a busy host can take a beat.
    async function rpcDebugUi<T>(
        channel: string,
        payload: unknown,
        localFallback: () => T | Promise<T>,
    ): Promise<T> {
        const peerId = getRemoteRunnerPeerId();
        if (!peerId) return await localFallback();
        const session = liveSessionHandle!.getSession()!;
        try {
            const result = await session.request(peerId, channel, payload, { timeoutMs: 8_000 }) as T;
            // Diagnostic: log inspector RPCs so we can tell whether the
            // host returned null/empty (no fields render) vs returned
            // real data (panel-side issue). Cheap — only fires on user
            // interaction (folder expand / per-entity refresh).
            const summary = (result == null)
                ? 'null'
                : (typeof result === 'object')
                    ? `object(${Object.keys(result as object).length} keys)`
                    : String(result).slice(0, 40);
            getLogger('debug-ui-collab').info(
                `${channel} ← ${summary}  payload=${JSON.stringify(payload).slice(0, 80)}`,
            );
            return result;
        } catch (e) {
            getLogger('debug-ui-collab').error(
                `${channel} RPC failed: ${e instanceof Error ? e.message : String(e)}`,
            );
            throw e;
        }
    }
    // dockview-core attaches each panel to a `dv-render-overlay` div
    // initialised with `style.visibility = 'hidden'` for one-frame flash
    // prevention (dockview-core.js: `// Hide until the first RAF-based
    // position is applied`). The overlay is supposed to flip to visible
    // once dockview's RAF runs AND `panel.api.isVisible` is true. In
    // practice, on the observer side after a layout restore + late
    // attach (the panel-cell pool pattern we use), the RAF can fire
    // before the panel is actually active in its tab group, and dockview
    // never re-clears the inline `visibility:hidden` afterwards. The
    // observer sees their Debug UI tab as active but the overlay
    // ancestor stays hidden — confirmed via the DOM probe in `debug-ui-
    // collab` logs (`#debug-ui-host[0] ... visibility=hidden ancestors=...
    // dv-render-overlay`).
    //
    // Per CSS spec, `visibility: visible` on a child element overrides
    // `visibility: hidden` inherited from an ancestor. Force-set it on
    // #debug-ui-host AND keep it in sync with the dockview panel api's
    // own isVisible signal so tab-switching still hides us correctly.
    if (debugUiHost) {
        // dockview-core attaches each panel to a dv-render-overlay
        // initialised with `style.visibility = 'hidden'` (anti-flicker;
        // see dockview-core.js:11465-11467). The overlay clears to
        // visible inside a RAF once `panel.api.isVisible === true`.
        // In production we've seen this RAF window miss the
        // visibility transition — observer's Debug UI tab is visually
        // active but the overlay stays inline `visibility:hidden`
        // forever. The Pane renders correctly into #debug-ui-host but
        // inherits hidden and looks blank.
        //
        // Two-part fix:
        //   1. Force `visibility: visible` on #debug-ui-host. CSS spec
        //      says a child's `visibility: visible` overrides an
        //      ancestor's `visibility: hidden` — content shows even
        //      when dockview's overlay is hidden.
        //   2. MutationObserver on the overlay ancestor: when dockview
        //      DOES correctly set visibility:hidden (user switched to
        //      a sibling tab in the same group), the inner element
        //      flips to display:none to fully hide. When dockview
        //      clears it (we're active again), the inner switches
        //      back to display:flex.
        debugUiHost.style.visibility = 'visible';
        const overlayParent = (() => {
            let p: HTMLElement | null = debugUiHost.parentElement;
            while (p && !p.classList.contains('dv-render-overlay')) p = p.parentElement;
            return p;
        })();
        if (overlayParent) {
            const sync = () => {
                // dockview sets pointerEvents=none AND visibility=hidden
                // when the panel is inactive. Use pointerEvents as the
                // signal because it's actually toggled correctly even
                // when visibility gets stuck.
                const pe = overlayParent.style.pointerEvents;
                const v = overlayParent.style.visibility;
                const dockviewWantsHidden = pe === 'none' && v === 'hidden';
                debugUiHost.style.display = dockviewWantsHidden ? 'none' : 'flex';
            };
            new MutationObserver(sync).observe(overlayParent, {
                attributes: true, attributeFilter: ['style'],
            });
            sync();
        }
        // Nudge dockview to recompute layout once the panel and the
        // mount have settled. The RAF inside dockview's `resize()` only
        // fires when something invalidates the position cache; an
        // explicit dock.layout() call invalidates it, so the next
        // resize callback runs and (hopefully) clears the anti-flicker
        // visibility:hidden on the overlay. Belt-and-suspenders with
        // the inline override above — if dockview behaves, both work
        // together; if dockview stays stuck, the override wins.
        setTimeout(() => {
            try {
                const root = document.querySelector('.dv-shell') as HTMLElement | null;
                if (root) (dockApi as any).layout?.(root.clientWidth, root.clientHeight);
            } catch (e) { console.warn('[debug-ui] dock layout nudge failed', e); }
        }, 50);
    }
    const debugUiHandle = debugUiHost ? mountDebugUiPanel({
        container: debugUiHost,
        getSchema: (t) => rpcDebugUi(
            'debugUi:getSchema', { typeName: t },
            () => monoGameHost.debugGetSchema(t),
        ),
        getEntitySchema: (t, id) => rpcDebugUi(
            'debugUi:getEntitySchema', { typeName: t, entityId: id },
            () => monoGameHost.debugGetEntitySchema(t, id),
        ),
        listEntities: (t) => rpcDebugUi(
            'debugUi:listEntities', { typeName: t },
            () => monoGameHost.debugListEntities(t),
        ),
        getLabels: (t) => rpcDebugUi(
            'debugUi:getLabels', { typeName: t },
            () => monoGameHost.debugGetLabels(t),
        ),
        getEntity: (t, id) => rpcDebugUi(
            'debugUi:getEntity', { typeName: t, entityId: id },
            () => monoGameHost.debugGetEntity(t, id),
        ),
        setField: (t, id, p, v) => rpcDebugUi(
            'debugUi:setField', { typeName: t, entityId: id, path: p, valueJson: v },
            () => monoGameHost.debugSetField(t, id, p, v),
        ),
        sendFbasicChange: (ctrlId, kind, value) => {
            // sendDebugUiChange is fire-and-forget locally; mirror that
            // for the observer path so the slider doesn't block on a
            // round-trip. The RPC response is discarded.
            const peerId = getRemoteRunnerPeerId();
            getLogger('debug-ui-collab').info(
                `sendFbasicChange ctrl=${ctrlId} kind=${kind} value=${value} remotePeer=${peerId ? peerId.slice(0, 8) : 'none(local)'}`,
            );
            if (peerId) {
                const session = liveSessionHandle!.getSession()!;
                session.request(peerId, 'debugUi:sendFbasicChange',
                    { ctrlId, kind, value }, { timeoutMs: 8_000 })
                    .then(() => getLogger('debug-ui-collab').info(`sendFbasicChange RPC ack ctrl=${ctrlId}`))
                    .catch((e) => getLogger('debug-ui-collab').error(`sendFbasicChange RPC failed: ${e instanceof Error ? e.message : String(e)}`));
                return;
            }
            monoGameHost.sendDebugUiChange(ctrlId, kind, value);
        },
    }) : null;

    const debugUiCollabLog = getLogger('debug-ui-collab');
    let debugUiIframeCount = 0;
    let debugUiBroadcastCount = 0;
    let debugUiLastQueueLen = -1;
    let debugUiLastGen = -1;
    let debugUiBroadcastSkipReason: string | null = null;
    // Expose the panel handle for Playwright probes (see
    // scripts/probe-debug-ui-visibility.mjs). The probe needs to drive
    // applyFrameEnvelope directly because the monogame postMessage
    // bridge isn't wired up in 'web'-type projects.
    if (debugUiHandle) {
        (window as any).__fadeDebugUiHandle = debugUiHandle;
    }

    // Tell the iframe (and via window.fadeDebugUi.isSubscribed(), the
    // C# DebugUISystem) when our Debug UI dock tab is actually visible.
    // When hidden — user is looking at another tab in the same group —
    // the iframe short-circuits fadeDebugUi.frame() to avoid the
    // postMessage + parent parse, and C# can skip the snapshot work
    // entirely. Big win for breakout-style games where the snapshot
    // reflection cost dominates frame time.
    //
    // The dock panel may not be adopted yet at this point (layout
    // restore runs slightly later). Try once now, retry inside a
    // setTimeout for the post-layout-restore case.
    function wireDebugUiSubscription(): boolean {
        const panel = dockApi.getPanel('debug-ui');
        if (!panel) return false;
        const sync = () => {
            try { monoGameHost.setDebugUiSubscribed(panel.api.isVisible); }
            catch (e) { console.warn('[debug-ui-subscribe] sync failed', e); }
        };
        panel.api.onDidVisibilityChange(sync);
        sync();
        return true;
    }
    if (!wireDebugUiSubscription()) {
        // Dock layout hasn't placed the panel yet — try after the
        // current microtask + a tick to let dockview finish its
        // adoption pass.
        setTimeout(() => { wireDebugUiSubscription(); }, 100);
    }

    monoGameHost.onDebugUiFrame = (env, rawJson) => {
        debugUiIframeCount++;
        // Log whenever the queue length OR gen changes — captures the
        // initial empty frames, the first non-empty frame after Run,
        // and program-restart resets, without spamming at 60 fps.
        const qLen = env.queue?.length ?? 0;
        if (qLen !== debugUiLastQueueLen || env.gen !== debugUiLastGen) {
            debugUiCollabLog.info(
                `iframe frame #${debugUiIframeCount}: queue ${debugUiLastQueueLen}→${qLen}, gen ${debugUiLastGen}→${env.gen}, autoInspector=${env.autoInspector}`,
            );
            debugUiLastQueueLen = qLen;
            debugUiLastGen = env.gen;
        }
        // CRITICAL: do NOT apply our own iframe's frames to the panel
        // when we're observing someone else's runtime. Our iframe is
        // idle (no program loaded) and pumps empty envelopes at gen=0;
        // applying them races against the relayed real envelopes from
        // the host and constantly wipes the rendered Pane back to the
        // idle hint. The relay handler in installCollabRuntimeListeners
        // is the only thing that should drive the panel when observing.
        const observingRemote = getRemoteRunnerPeerId() != null;
        if (!observingRemote) {
            try { debugUiHandle?.applyFrameEnvelope(env); }
            catch (e) { console.warn('[debug-ui] applyFrameEnvelope threw', e); }
        }
        // Relay the raw envelope to live-session observers so their
        // Debug UI panel mirrors ours. Skip broadcasting when WE are
        // observing — we have nothing useful to send (our iframe is
        // idle); broadcasting empty envelopes from observers would
        // overwrite the host's content for everyone.
        if (observingRemote) return;
        const session = liveSessionHandle?.getSession();
        if (!session) {
            // Tell the user once if we're dropping broadcasts because
            // there's no session yet — common when the iframe boots
            // before the user shares.
            if (debugUiBroadcastSkipReason !== 'no-session') {
                debugUiBroadcastSkipReason = 'no-session';
                debugUiCollabLog.info(`broadcast skipped — no live session yet`);
            }
            return;
        }
        if (!rawJson) {
            if (debugUiBroadcastSkipReason !== 'no-json') {
                debugUiBroadcastSkipReason = 'no-json';
                debugUiCollabLog.warn(`broadcast skipped — rawJson is empty (frame #${debugUiIframeCount})`);
            }
            return;
        }
        if (debugUiBroadcastSkipReason) {
            debugUiCollabLog.info(`broadcast resumed (was: ${debugUiBroadcastSkipReason})`);
            debugUiBroadcastSkipReason = null;
        }
        try {
            session.sendDebugUiFrame(rawJson);
            debugUiBroadcastCount++;
            // Log first 3 frames + every 200 thereafter. First 3 makes
            // it cheap to verify "broadcasts are flowing" without
            // waiting 3 seconds for the 200-counter.
            if (debugUiBroadcastCount <= 3 || debugUiBroadcastCount % 200 === 0) {
                debugUiCollabLog.info(
                    `host broadcast frame #${debugUiBroadcastCount} (queue ${qLen}, bytes ${rawJson.length})`,
                );
                console.log(`[debug-ui-collab] host broadcast frame #${debugUiBroadcastCount} qlen=${qLen}`);
            }
        }
        catch (e) {
            // Surface broadcast failures to Logs so they don't hide in
            // the browser console — if the relay is silently dying after
            // frame 1, this is where it shows up.
            debugUiCollabLog.error(`sendDebugUiFrame failed at frame #${debugUiBroadcastCount + 1}: ${e instanceof Error ? e.message : String(e)}`);
        }
    };

    // Find-in-Files panel. Mounted once at boot into the offscreen
    // #search-host; dockview reparents the host into whichever tab group
    // hosts the 'search' panel. Click a result → open the file + reveal the
    // line, mirroring the Problems panel's navigation behavior.
    const settingsHost = document.getElementById('settings-host');
    let settingsPanelHandle: { focus(): void; dispose(): void } | undefined;
    if (settingsHost) {
        settingsPanelHandle = mountSettingsPanel({
            container: settingsHost,
            getProjectName: () => workspace.currentProject(),
        });
    }

    const searchHost = document.getElementById('search-host');
    let searchPanelHandle: { focus(): void; dispose(): void } | undefined;
    if (searchHost) {
        searchPanelHandle = mountSearchPanel({
            container: searchHost,
            workspace,
            getExcludeGlobs: () => {
                const v = currentSettings().effective['search.exclude'];
                return Array.isArray(v) ? (v as string[]) : [];
            },
            openMatch: async ({ path, lineNumber, column, length }) => {
                try {
                    await openFile(workspace, path);
                    if (editor) {
                        editor.revealLineInCenter(lineNumber, monaco.editor.ScrollType.Smooth);
                        editor.setSelection({
                            startLineNumber: lineNumber,
                            startColumn: column,
                            endLineNumber: lineNumber,
                            endColumn: column + length,
                        });
                        editor.focus();
                    }
                } catch (e) {
                    console.warn('[fade] search openMatch failed', e);
                }
            },
        });
    }

    // Source Control panel: mounts into the offscreen #collaboration-host
    // that dockview moves into the workspace tab group. Bound to the active
    // project; flushPendingSaves is bound here so the panel doesn't need a
    // direct reference to the editor's tabs map.
    const scHost = document.getElementById('collaboration-host');
    if (scHost) {
        sharingController = mountCollaboration({
            container: scHost,
            workspace,
            getActiveProject: () => workspace.currentProject(),
            flushPendingSaves: () => flushPendingSaves(workspace),
            onAfterPull: async (changedPaths) => {
                // Reflect pulled bytes in any open Monaco editor whose file
                // is among the changed set. Text files only; binary tabs
                // don't have a model.
                for (const path of changedPaths) {
                    const tab = tabs.get(path);
                    if (!tab) continue;
                    try {
                        const fresh = await workspace.read(path);
                        if (tab.model.getValue() !== fresh) tab.model.setValue(fresh);
                    } catch { /* binary or deleted — skip */ }
                }
                await renderFileList(workspace);
            },
            // Open the dedicated conflict-resolution editor in its own
            // dockview tab. The editor reads the file from OPFS into a
            // throwaway Monaco model — independent of any regular tab — so
            // edits stay in-memory until the user clicks Save & close.
            onOpenConflict: async (path) => {
                const panelId = `conflict-editor:${path}`;
                const existing = dockApi.getPanel(panelId);
                if (existing) { existing.api.setActive(); return; }
                dockApi.addPanel({
                    id: panelId,
                    component: 'conflict-editor',
                    title: `⚠ ${path}`,
                    position: { referencePanel: 'editor', direction: 'within' },
                    renderer: 'always',
                });
            },
            // Open a read-only Monaco diff for "Show diff" buttons in the
            // Collaboration + History panels. Controller resolves
            // before/after content; host just owns the dockview tab.
            onOpenDiff: (args) => {
                openDiffViewer({
                    id: args.id,
                    params: {
                        title: args.title,
                        path: args.path,
                        languageId: args.languageId,
                        beforeText: args.beforeText,
                        afterText: args.afterText,
                        beforeLabel: args.beforeLabel,
                        afterLabel: args.afterLabel,
                    },
                });
            },
        });
        sharingController.onStatusChange((map) => {
            sharingStatus = map;
            void renderFileList(workspace);
            renderSharingChips();
            renderSharingStatusIcon();
        });
        sharingController.onPendingPullChange((paths) => {
            sharingPendingPull = paths;
            void renderFileList(workspace);
            renderSharingChips();
            renderSharingStatusIcon();
        });
        sharingController.onConflictChange((state) => {
            sharingConflicts = state;
            void renderFileList(workspace);
            renderSharingChips();
            renderSharingStatusIcon();
        });
        sharingController.onSavesChange(() => {
            renderSharingChips();
            renderSharingStatusIcon();
        });
        // Header chips — paint once at mount, then re-render every time a
        // sharing signal changes. Each chip is a button that focuses the
        // Collaboration tab so the user can drill in.
        renderSharingChips();
        // Persistent status icon — wire the one-time click handler now,
        // then keep its badge state in sync via the listeners above.
        wireSharingStatusIcon();
        renderSharingStatusIcon();
        // Make sure the Live Session chip paints its idle state even
        // before any session has been started — otherwise the chip
        // doesn't exist as a clickable affordance until a session is
        // already in flight. Deferred via queueMicrotask because the
        // `let liveSessionChipScheduled` binding lives further down in
        // this bootstrap closure; calling synchronously here hits TDZ.
        queueMicrotask(() => { try { renderLiveSessionChip(); } catch (e) { console.warn('[fade] initial chip render failed', e); } });

        // History panel binds to the same controller. Mounting here (after
        // sharing is up) guarantees the controller is non-null when the
        // panel subscribes for history updates.
        const historyHost = document.getElementById('history-host');
        if (historyHost) {
            mountHistoryPanel({
                container: historyHost,
                controller: sharingController,
            });
        }

        // ── Live Session panel ──────────────────────────────────────────
        // Mount last so the session adapter can see the fully-wired editor
        // + sharing setup. The adapter is a thin shim that exposes the
        // editor's tab system + the OPFS workspace through one interface;
        // the session itself owns Y.Doc / awareness / MonacoBinding.
        const liveSessionHost = document.getElementById('live-session-host');
        if (liveSessionHost) {
            const sessionAdapter: CollabSessionHost = {
                get editor() {
                    if (!editor) throw new Error('[fade-collab] editor not ready');
                    return editor;
                },
                getActiveFileName: () => activeName,
                onActiveFileChange: (cb) => {
                    activeFileListeners.add(cb);
                    return () => activeFileListeners.delete(cb);
                },
                getModelForFile: (name) => tabs.get(name)?.model ?? null,
                openFile: async (name) => {
                    // Re-uses the existing module-scope openFile which
                    // reads from OPFS, creates the Monaco model, wires
                    // autosave, etc. By the time guests call this, their
                    // workspace has been switched to the transient
                    // project + the file's bytes have already been
                    // written there by writeWorkspaceText/Bytes.
                    await openFile(workspace, name);
                },
                closeFile: async (name) => { closeTab(name); },
                listWorkspaceFiles: () => workspace.list(),
                isBinaryPath: (path) => isBinaryFileName(path),
                readWorkspaceText: (path) => workspace.read(path),
                readWorkspaceBytes: (path) => workspace.readBytes(path),
                writeWorkspaceText: async (path, content) => {
                    await workspace.write(path, content);
                    sharingController?.invalidateHashFor(path);
                },
                writeWorkspaceBytes: async (path, bytes) => {
                    await workspace.writeBytes(path, bytes);
                    sharingController?.invalidateHashFor(path);
                },
                deleteWorkspaceFile: async (path) => {
                    try { await workspace.delete(path); } catch { /* may not exist */ }
                },
                refreshFileList: async () => {
                    await renderFileList(workspace);
                },
                refreshProjectConfig: async () => {
                    // Bring Monaco's model registry in sync with the new
                    // project's files. The bootstrap path does this once
                    // by listing workspace files + creating models; we
                    // mirror that here so the LSP push loop sees real,
                    // current content for every file in the active project.
                    const names = await workspace.list();
                    const expected = new Set(names);
                    // Drop models for files that no longer exist in this
                    // project — leaving them around makes the LSP see
                    // ghost source content from the previous project.
                    for (const m of monaco.editor.getModels()) {
                        const path = m.uri.path; // /workspace/<name>
                        const match = /^\/workspace\/(.+)$/.exec(path);
                        if (!match) continue;
                        if (!expected.has(match[1])) {
                            try { m.dispose(); } catch { /* ignore */ }
                            unregisterVirtualFile(m.uri);
                        }
                    }
                    // Ensure every file in the new project has a model
                    // with current OPFS content. openFile path normally
                    // creates these; this catches files that haven't been
                    // tabbed open yet (LSP still needs them for the
                    // joined project doc).
                    for (const name of names) {
                        const uri = monaco.Uri.file(`/workspace/${name}`);
                        const existing = monaco.editor.getModel(uri);
                        const text = await workspace.read(name);
                        if (!existing) {
                            const m = monaco.editor.createModel(text, languageFor(name), uri);
                            const eff = currentSettings().effective;
                            m.updateOptions({
                                tabSize: Number(eff['editor.tabSize'] ?? 2),
                                insertSpaces: Boolean(eff['editor.insertSpaces'] ?? true),
                            });
                            if (!registeredVirtualFsUris.has(uri.toString())) {
                                registerVirtualFile(uri, text);
                            }
                        } else if (existing.getValue() !== text) {
                            // Same URI but stale content from a prior
                            // project — refresh.
                            existing.setValue(text);
                        }
                    }
                    // Re-parse fade.json + push the joined project doc
                    // to the LSP. This is the actual fix for the
                    // "joiner sees parser errors" symptom — without this
                    // call, the LSP keeps the previous project's source
                    // map and parses against stale state.
                    try { await refreshFadeProject(); }
                    catch (e) { console.warn('[fade-collab] refreshFadeProject failed', e); }
                },
            };

            liveSessionHandle = bootstrapLiveSession({
                container: liveSessionHost,
                sessionHost: sessionAdapter,
                getProjectName: () => workspace.currentProject(),
                getGithubLogin: () => null,
                // "Force sync debug data" button in the Live Session
                // panel. Recovery affordance for the intermittent
                // observer-doesn't-see-scopes problem (RPC drops while
                // the host's iframe is busy). On the host: re-fetch
                // frames from the runtime and re-broadcast the snapshot.
                // On a guest: clear any cached call stack on the remote
                // adapter and re-run refreshDebugView, which pulls a
                // fresh stack/scopes RPC from the host.
                forceDebugSync: async () => {
                    const session = liveSessionHandle?.getSession();
                    // eslint-disable-next-line no-console
                    console.log('[fade-collab] forceDebugSync clicked', {
                        hasSession: !!session,
                        debugSessionActive,
                        isLocalDebugInitiator: session ? isLocalDebugInitiator() : null,
                        dbgKind: dbg.kind,
                        currentDebugState: session
                            ? Object.fromEntries(Array.from(session.debugState.entries()))
                            : null,
                    });
                    if (!session) return;
                    if (!debugSessionActive) return;
                    if (isLocalDebugInitiator()) {
                        const frames = await fetchPausedFramesAndBroadcast();
                        // eslint-disable-next-line no-console
                        console.log('[fade-collab] forceDebugSync (host) re-broadcast', {
                            frameCount: frames.length,
                            firstLine: frames[0]?.lineNumber,
                        });
                        await refreshDebugView(frames);
                    } else {
                        // eslint-disable-next-line no-console
                        console.log('[fade-collab] forceDebugSync (observer) refreshing');
                        await refreshDebugView();
                    }
                },
                guestLifecycle: {
                    onGuestJoinStart: async (roomId) => {
                        // Snapshot the current project so we can swap
                        // back on disconnect, then create + activate a
                        // sandboxed project for the session. Guests
                        // never see their host's files in their normal
                        // workspace — only inside this temporary folder
                        // that gets nuked when they leave.
                        const previousProjectName = workspace.currentProject() || null;
                        const transientProjectName = liveSessionProjectName(roomId);
                        // createProject is idempotent if the dir already
                        // exists (e.g. a stale run for the same roomId).
                        await workspace.createProject(transientProjectName);
                        // Wipe any leftover bytes so the mirror starts
                        // from a clean slate.
                        try {
                            await workspace.setActiveProject(transientProjectName);
                            const existing = await workspace.list();
                            for (const p of existing) {
                                try { await workspace.delete(p); } catch { /* ignore */ }
                            }
                        } catch (e) {
                            console.warn('[fade-collab] failed to clean transient project', e);
                        }
                        // Close any tabs that were open against the
                        // previous project — their models point at
                        // file:///workspace/<name> which is shared with
                        // the transient project's namespace, so leaving
                        // them open would let stale text from a
                        // different project leak into the session.
                        for (const name of Array.from(tabs.keys())) {
                            closeTab(name);
                        }
                        // Also dispose every Monaco model whose URI is
                        // under /workspace/. The previous project's
                        // models share that namespace with the transient
                        // project; without this, the LSP push loop and
                        // refreshFadeProject see leftover models with
                        // stale content from the previous project and
                        // emit a flood of cross-file parser errors.
                        for (const m of monaco.editor.getModels()) {
                            if (m.uri.scheme === 'file' && m.uri.path.startsWith('/workspace/')) {
                                try { m.dispose(); } catch { /* ignore */ }
                                unregisterVirtualFile(m.uri);
                            }
                        }
                        await renderFileList(workspace);
                        renderLiveSessionChip();
                        return { transientProjectName, previousProjectName };
                    },
                    onGuestLeaveEnd: async ({ transientProjectName, previousProjectName }) => {
                        // Close any tabs that were open in the transient
                        // project so their Monaco models don't keep
                        // pointing at deleted OPFS files.
                        for (const name of Array.from(tabs.keys())) {
                            closeTab(name);
                        }
                        // Dispose every Monaco model under /workspace/ —
                        // their content is the transient project's
                        // files, which are about to be deleted. Without
                        // this, openFile in the previous project sees
                        // existing models with stale transient content
                        // and reuses them. refreshProjectConfig below
                        // will recreate models for the previous project
                        // from OPFS.
                        for (const m of monaco.editor.getModels()) {
                            if (m.uri.scheme === 'file' && m.uri.path.startsWith('/workspace/')) {
                                try { m.dispose(); } catch { /* ignore */ }
                                unregisterVirtualFile(m.uri);
                            }
                        }
                        // Switch back to the previous project (or the
                        // default if there wasn't one — shouldn't
                        // happen, but guard against it).
                        try {
                            if (previousProjectName) {
                                await workspace.setActiveProject(previousProjectName);
                            }
                        } catch (e) {
                            console.warn('[fade-collab] failed to restore previous project', e);
                        }
                        // Rebuild Monaco models + re-pump the LSP for the
                        // previous project. Mirrors what bootstrap does
                        // on cold start; without it the user's regular
                        // editor would come back with no models and the
                        // LSP would still be configured for the transient
                        // project that no longer exists.
                        try { await sessionAdapter.refreshProjectConfig?.(); }
                        catch (e) { console.warn('[fade-collab] refreshProjectConfig on leave failed', e); }
                        // Nuke the transient project off disk.
                        try {
                            await deleteOpfsProject(transientProjectName);
                        } catch (e) {
                            console.warn('[fade-collab] failed to delete transient project', e);
                        }
                        await renderFileList(workspace);
                        renderLiveSessionChip();
                    },
                },
            });

            liveSessionHandle.onSessionChange(() => renderLiveSessionChip());
            // Per-session listeners for game frame streaming + debug
            // state replication. Re-subscribed on every session change
            // because the previous session's handlers got disposed.
            liveSessionHandle.onSessionChange((session) => {
                installCollabRuntimeListeners(session);
                // Expose for devtools debugging — `window.__fadeCollab.debugState`
                // dumps the current Y.Map state; `__fadeCollab.peerId` shows
                // our Trystero peer ID; `__fadeCollab.initiatorPeerId` shows
                // who we'd RPC for debug commands. `__fadeCollab.forceSync()`
                // mirrors the Live Session panel button so the user can
                // trigger it from devtools when the button doesn't appear
                // to fire.
                (window as any).__fadeCollab = session
                    ? {
                        get session() { return session; },
                        get debugState() {
                            return Object.fromEntries(Array.from(session.debugState.entries()));
                        },
                        get peerId() { return (session as any).room?.selfId; },
                        get initiatorPeerId() { return session.debugState.get('initiatorPeerId'); },
                        get peers() {
                            return Array.from(session.awareness.getStates().keys());
                        },
                        get debugSessionActive() { return debugSessionActive; },
                        get debugPaused() { return debugPaused; },
                        get dbgKind() { return dbg.kind; },
                        forceSync: async () => {
                            if (!debugSessionActive) return 'no active debug session';
                            if (isLocalDebugInitiator()) {
                                const frames = await fetchPausedFramesAndBroadcast();
                                await refreshDebugView(frames);
                                return `host re-broadcast (${frames.length} frames, line ${frames[0]?.lineNumber})`;
                            } else {
                                await refreshDebugView();
                                return 'observer refresh complete';
                            }
                        },
                        // Self-test that walks through the observer flow
                        // and reports where it breaks. Run in devtools on
                        // either peer to diagnose intermittent issues.
                        diagnose: async () => {
                            const report: Record<string, unknown> = {
                                role: isLocalDebugInitiator() ? 'host' : 'observer',
                                selfPeerId: (session as any).room?.selfId,
                                selfClientId: session.awareness.clientID,
                                debugSessionActive,
                                debugPaused,
                                dbgKind: dbg.kind,
                                debugState: Object.fromEntries(Array.from(session.debugState.entries())),
                                peers: Array.from(session.awareness.getStates().entries()).map(
                                    ([id, state]) => ({ clientId: id, state }),
                                ),
                            };
                            // For an observer: test that the host's RPC
                            // round-trip actually works. Send a stackFrames
                            // RPC with a short timeout — if it hangs,
                            // we've identified the transport-level cause
                            // of the missing-data symptom.
                            if (!isLocalDebugInitiator()) {
                                const start = Date.now();
                                try {
                                    const peerId = session.debugState.get('initiatorPeerId') as string;
                                    if (!peerId) {
                                        report.rpcProbe = { error: 'no initiatorPeerId' };
                                    } else {
                                        const res = await session.request(peerId, 'debug:stackFrames', null, { timeoutMs: 3000 });
                                        report.rpcProbe = {
                                            ok: true,
                                            elapsedMs: Date.now() - start,
                                            frameCount: Array.isArray(res) ? res.length : '<not-array>',
                                        };
                                    }
                                } catch (e) {
                                    report.rpcProbe = {
                                        ok: false,
                                        elapsedMs: Date.now() - start,
                                        error: e instanceof Error ? e.message : String(e),
                                    };
                                }
                                // Also test scopes — the RPC the user
                                // sees timing out.
                                const scopesStart = Date.now();
                                try {
                                    const peerId = session.debugState.get('initiatorPeerId') as string;
                                    if (!peerId) {
                                        report.scopesProbe = { error: 'no initiatorPeerId' };
                                    } else {
                                        const res = await session.request(peerId, 'debug:scopes', { frameId: 0 }, { timeoutMs: 3000 });
                                        report.scopesProbe = {
                                            ok: true,
                                            elapsedMs: Date.now() - scopesStart,
                                            scopeCount: Array.isArray((res as any)?.scopes) ? (res as any).scopes.length : '<not-array>',
                                        };
                                    }
                                } catch (e) {
                                    report.scopesProbe = {
                                        ok: false,
                                        elapsedMs: Date.now() - scopesStart,
                                        error: e instanceof Error ? e.message : String(e),
                                    };
                                }
                            }
                            // eslint-disable-next-line no-console
                            console.log('[fade-collab diagnose]', report);
                            return report;
                        },
                    }
                    : undefined;
            });
        }
    }

    // ── Phase 2A / 2B: per-session runtime listeners ─────────────────────
    // Track current unsubscribe functions so the next session change can
    // tear them down before installing the new set.
    let collabRuntimeUnsubs: Array<() => void> = [];
    // Receive-side game-frame diagnostics. Mirrors `gameFrameStats` on the
    // capture/send side so we can correlate "host says it sent N frames"
    // vs. "guest says it received M frames" when streaming misbehaves.
    let gameFrameReceiveStats = { received: 0, rendered: 0, decodeErrors: 0, lastByteSize: 0 };
    let lastGameFrameReceiveLog = 0;

    function installCollabRuntimeListeners(session: ReturnType<NonNullable<typeof liveSessionHandle>['getSession']>): void {
        for (const u of collabRuntimeUnsubs) { try { u(); } catch { /* ignore */ } }
        collabRuntimeUnsubs = [];
        // Tear down any previous-session cursor surface before we
        // (maybe) build a fresh one. dispose() clears overlay DOM,
        // removes the awareness listener, and broadcasts focus=null
        // so peers stop showing us.
        if (sharedCursorHandle) {
            try { sharedCursorHandle.dispose(); }
            catch (e) { console.warn('[fade-collab] sharedCursor dispose threw', e); }
            sharedCursorHandle = null;
        }
        if (!session) {
            // Session ended — hide observer overlays and stop streaming.
            stopGameFrameStreaming();
            hideGameStreamOverlay();
            updateDebugObserverBanner(null);
            // Clear any lingering peer-presence dots from the previous session.
            try { applyPeerFilePresence(new Map()); }
            catch { /* ignore */ }
            return;
        }

        // Mount the shared-cursor system on every fresh session. Editor
        // is the same long-lived Monaco instance for the lifetime of
        // the page, so we always have a target for the in-editor
        // cursors. Tab badges flow into the workspace tab strip via
        // applyEditorTabBadges defined alongside the file list render.
        if (editor) {
            try {
                sharedCursorHandle = mountSharedCursors({
                    session,
                    editor,
                    getActiveFile: () => activeName,
                    setPeerFilePresence: (m) => applyPeerFilePresence(m),
                });
            } catch (e) {
                console.warn('[fade-collab] mountSharedCursors failed', e);
            }
        }

        // Game frames from any other peer.
        gameFrameReceiveStats = { received: 0, rendered: 0, decodeErrors: 0, lastByteSize: 0 };
        collabRuntimeUnsubs.push(session.onGameFrame((peerId, bytes) => {
            // Skip our own echo — sendGameFrame goes to everyone
            // including ourselves on some transports. The transport's
            // selfId matches our local awareness clientID via the
            // adapter; checking room.selfId is the robust comparison.
            if (peerId === (session as any).room?.selfId) return;
            gameFrameReceiveStats.received++;
            gameFrameReceiveStats.lastByteSize = bytes.byteLength;
            // Periodic log so the receive side is visible when frames
            // aren't appearing — symmetric with the host's capture
            // stats log. Fires on the first frame (lastReceiveLog === 0)
            // and then ~every 2 sec.
            const now = performance.now();
            if (lastGameFrameReceiveLog === 0 || now - lastGameFrameReceiveLog > 2000) {
                lastGameFrameReceiveLog = now;
                console.log('[fade-collab] game frame receive stats', { ...gameFrameReceiveStats });
            }
            void renderGameFrame(bytes);
        }));

        // Program-output forwarding: when another peer broadcasts a log
        // line (their program's `print` / stdout / stderr), pipe it into
        // our local Output panel — same surface where our OWN program's
        // print lines go, so observers see the host's output alongside
        // anything they print locally. Drops the loopback so peers don't
        // see their own broadcasts.
        collabRuntimeUnsubs.push(session.onLogLine((peerId, line) => {
            if (peerId === (session as any).room?.selfId) return;
            appendOutputLine(line.message, line.level === 'error' ? 'error' : 'plain');
        }));

        // Apply Debug UI envelopes broadcast by whoever's running the
        // program. Observers don't have a monogame iframe, so this relay
        // is the only path their debug-ui-panel sees envelopes. Self-
        // broadcasts are skipped to avoid double-applying.
        let debugUiReceiveCount = 0;
        let observerLastQueueLen = -1;
        let observerLastGen = -1;
        collabRuntimeUnsubs.push(session.onDebugUiFrame((peerId, json) => {
            console.log(`[debug-ui-collab] observer onDebugUiFrame fired peerId=${peerId.slice(0,8)} self=${(session as any).room?.selfId?.slice(0,8)} jsonLen=${json.length}`);
            if (peerId === (session as any).room?.selfId) return;
            if (!debugUiHandle) {
                debugUiCollabLog.warn(`relayed debug-ui frame received but debugUiHandle is null — panel not mounted`);
                return;
            }
            try {
                const env = parseDebugUiEnvelope(json);
                const qLenBefore = env.queue?.length ?? 0;
                debugUiHandle.applyFrameEnvelope(env);
                debugUiReceiveCount++;
                if (debugUiReceiveCount <= 3) {
                    console.log(`[debug-ui-collab] observer applied frame #${debugUiReceiveCount} qlen=${qLenBefore} gen=${env.gen}`);
                }
                // One-shot DOM probe right after we apply the first
                // non-empty queue: if the panel mounted Tweakpane
                // correctly the host element will have child nodes; if
                // it's still empty, the apply path silently did nothing.
                if (qLenBefore > 0 && observerLastQueueLen === 0) {
                    // Find every #debug-ui-host AND every panel-cell with
                    // data-panel="debug-ui" — the user reports a visible
                    // idle-hint text alongside a hidden Pane, which only
                    // makes sense if dockview duplicated the cell.
                    const allHosts = Array.from(document.querySelectorAll('#debug-ui-host')) as HTMLElement[];
                    const allCells = Array.from(document.querySelectorAll('.panel-cell[data-panel="debug-ui"]')) as HTMLElement[];
                    // Find every element whose textContent contains the
                    // idle-hint string. Catches an orphaned host where the
                    // user can see the text but the Pane is elsewhere.
                    const idleHintEls: HTMLElement[] = [];
                    document.querySelectorAll<HTMLElement>('*').forEach((el) => {
                        if (el.children.length === 0
                            && (el.textContent ?? '').includes('Run your program to see custom debug windows')) {
                            idleHintEls.push(el);
                        }
                    });
                    debugUiCollabLog.info(
                        `observer DOM census: #debug-ui-host count=${allHosts.length}, .panel-cell[data-panel=debug-ui] count=${allCells.length}, idleHintEls=${idleHintEls.length}`,
                    );
                    const reportEl = (label: string, el: HTMLElement) => {
                        const rect = el.getBoundingClientRect();
                        const cs = getComputedStyle(el);
                        let p: HTMLElement | null = el.parentElement;
                        let depth = 0;
                        const ancestors: { tag: string; cls: string; vis: string; disp: string }[] = [];
                        while (p && depth < 8) {
                            const ps = getComputedStyle(p);
                            ancestors.push({
                                tag: p.tagName.toLowerCase(),
                                cls: p.className?.toString?.().slice(0, 60) ?? '',
                                vis: ps.visibility,
                                disp: ps.display,
                            });
                            p = p.parentElement;
                            depth++;
                        }
                        debugUiCollabLog.info(
                            `${label} size=${Math.round(rect.width)}x${Math.round(rect.height)} display=${cs.display} visibility=${cs.visibility} attached=${el.isConnected}`,
                        );
                        ancestors.forEach((a, i) => {
                            debugUiCollabLog.info(`  ↑[${i}] ${a.tag}.${a.cls} display=${a.disp} visibility=${a.vis}`);
                        });
                    };
                    allHosts.forEach((host, i) => reportEl(`#debug-ui-host[${i}]`, host));
                    allCells.forEach((cell, i) => {
                        const hosts = cell.querySelectorAll('#debug-ui-host').length;
                        const sliders = cell.querySelectorAll('.tp-sldv').length;
                        reportEl(`panel-cell[${i}] (containedHosts=${hosts} sliders=${sliders})`, cell);
                    });
                    idleHintEls.forEach((el, i) => reportEl(`idleHintEl[${i}]`, el));
                }
                // Log first 3 + every queue-length change so we see when
                // (and whether) a real queue arrives from the host.
                const qLen = env.queue?.length ?? 0;
                const shouldLog = debugUiReceiveCount <= 3
                    || qLen !== observerLastQueueLen
                    || env.gen !== observerLastGen
                    || debugUiReceiveCount % 200 === 0;
                if (shouldLog) {
                    const types = (env.queue ?? []).map(c => c.t);
                    const hist = types.reduce<Record<number, number>>((m, t) => {
                        m[t] = (m[t] ?? 0) + 1; return m;
                    }, {});
                    const sample = (env.queue ?? []).slice(0, 4);
                    debugUiCollabLog.info(
                        `observer frame #${debugUiReceiveCount}: gen=${env.gen} queue=${qLen} autoInspector=${env.autoInspector} hist=${JSON.stringify(hist)} sample=${JSON.stringify(sample)}`,
                    );
                    observerLastQueueLen = qLen;
                    observerLastGen = env.gen;
                }
            }
            catch (e) {
                debugUiCollabLog.error(`applyFrameEnvelope (relayed) failed: ${e instanceof Error ? e.message : String(e)}`);
            }
        }));

        // Debug state: when host writes pause info, render the observer
        // banner on guest panels. Cleared when the initiator drops the
        // session or ends debugging.
        const dbgObserver = () => updateDebugObserverBanner(snapshotDebugState(session));
        session.debugState.observe(dbgObserver);
        collabRuntimeUnsubs.push(() => session.debugState.unobserve(dbgObserver));
        // Initial render in case state was already populated.
        dbgObserver();

        // Phase 2C smoke test: register a ping handler so the host can
        // be probed from a guest. Returns `{ pong: payload }` so the
        // round-trip is verifiable.
        const offPing = session.onRequest('ping', (_peerId, payload) => ({ pong: payload }));
        collabRuntimeUnsubs.push(offPing);

        // Observer-initiated stop. The observer's Stop button reaches us
        // here when this peer is the active runner/debugger. Just call
        // stopAll — same code path the host's own Stop click follows.
        // Idempotent on the host side (stopAll is safe when nothing is
        // actually running, which can happen if the activity flag was
        // stale by the time the RPC arrived).
        const offRunStop = session.onRequest('run:stop', async () => {
            await stopAll();
            return { ok: true };
        });
        collabRuntimeUnsubs.push(offRunStop);

        // Debug UI dispatch — observers' Tweakpane panel routes its
        // get/set/list calls here when we're the active runner. Each
        // handler is a thin proxy to the corresponding monoGameHost
        // method; payloads mirror the panel callback shapes 1:1.
        const debugUiHandlers: Array<[string, (p: any) => unknown | Promise<unknown>]> = [
            ['debugUi:getSchema',
                (p: { typeName: string }) => monoGameHost.debugGetSchema(p.typeName)],
            ['debugUi:getEntitySchema',
                (p: { typeName: string; entityId: number }) =>
                    monoGameHost.debugGetEntitySchema(p.typeName, p.entityId)],
            ['debugUi:listEntities',
                (p: { typeName: string }) => monoGameHost.debugListEntities(p.typeName)],
            ['debugUi:getLabels',
                (p: { typeName: string }) => monoGameHost.debugGetLabels(p.typeName)],
            ['debugUi:getEntity',
                (p: { typeName: string; entityId: number }) =>
                    monoGameHost.debugGetEntity(p.typeName, p.entityId)],
            ['debugUi:setField',
                (p: { typeName: string; entityId: number; path: string; valueJson: string }) =>
                    monoGameHost.debugSetField(p.typeName, p.entityId, p.path, p.valueJson)],
            ['debugUi:sendFbasicChange',
                (p: { ctrlId: number; kind: number; value: string }) => {
                    getLogger('debug-ui-collab').info(
                        `host received sendFbasicChange ctrl=${p.ctrlId} kind=${p.kind} value=${p.value}`,
                    );
                    monoGameHost.sendDebugUiChange(p.ctrlId, p.kind, p.value);
                    return { ok: true };
                }],
        ];
        for (const [channel, handler] of debugUiHandlers) {
            collabRuntimeUnsubs.push(session.onRequest(channel, (_peerId, payload) => handler(payload as any)));
        }

        // ── Phase 2C: host-side debug RPC handlers ───────────────────────
        // Each handler forwards to localDebugAdapter — the LOCAL adapter,
        // not the facade. We always want to act on this peer's actual
        // debug runtime, even if some other peer somehow became
        // initiator. (The session-layer's `setDebugState` guard already
        // ensures only the active initiator can mutate debugState, but
        // we double-check the channel can only run when we have a live
        // debug session — refusing the RPC with a clear error
        // otherwise.)
        const debugRpcCheck = (): string | null => {
            if (!debugSessionActive) return 'no active debug session on host';
            if (session.debugState.get('initiatorClientId') !== session.awareness.clientID) {
                return 'this peer is not the active debug initiator';
            }
            return null;
        };
        // Observer-driven control RPCs (continue/step/pause) need to do
        // MORE than just call the local adapter — they must also mirror
        // the side-effects that the host's own button handlers do:
        //   * Update the host's `debugPaused` flag + status pill + button
        //     enable-states so the host's Debug panel doesn't look stuck.
        //   * Broadcast the transient state-change to the Y.Map so OTHER
        //     observers (and the triggering observer's RemoteAdapter)
        //     see `paused: false` immediately, before the runtime lands
        //     at the next pause. Without this, the observer's UI looks
        //     dead between click and the next BREAKPOINT.
        // Observer-driven control RPCs. Mirror the host's local UI flags
        // so the host's Debug panel doesn't look stuck while the observer
        // drives the debugger. Only `continue` broadcasts proactively —
        // see the matching reasoning on the local button handlers. The
        // BREAKPOINT case in onAnyDebugEvent is the single source of
        // truth for {paused:true, currentFile, currentLine, callStack}
        // broadcasts; double-broadcasting them from here would race the
        // runtime's own broadcast.
        const onObserverContinue = async () => {
            await localDebugAdapter.continue();
            debugPaused = false;
            setDebugStatus('running', 'running');
            setCurrentLine(null);
            setDebugButtons();
            broadcastDebugState({ paused: false });
        };
        const onObserverPause = async () => {
            await localDebugAdapter.pause();
            debugPaused = true;
            setDebugStatus('paused', 'paused');
            setDebugButtons();
            // Same reasoning as the local pauseBtn handler: REQUEST_PAUSE
            // doesn't fire REV_REQUEST_BREAKPOINT, so we have to push the
            // {paused, currentFile, currentLine, callStack, topFrameScopes}
            // snapshot ourselves, otherwise the observer's debugState never
            // flips to paused:true and their UI looks dead.
            const frames = await fetchPausedFramesAndBroadcast();
            await refreshDebugView(frames);
        };
        const onObserverStep = async (p: { kind: StepKind }) => {
            await localDebugAdapter.step(p.kind);
            debugPaused = false;
            setCurrentLine(null);
            setDebugButtons();
        };
        const debugRpcRoutes: Array<[string, (payload: any) => Promise<unknown>]> = [
            ['debug:continue',           () => onObserverContinue()],
            ['debug:pause',              () => onObserverPause()],
            ['debug:step',               (p: { kind: StepKind }) => onObserverStep(p)],
            ['debug:terminate',          () => localDebugAdapter.terminate()],
            ['debug:setBreakpoints',     (p: { payload: unknown }) => localDebugAdapter.setBreakpoints(p.payload)],
            ['debug:stackFrames',        () => localDebugAdapter.stackFrames()],
            ['debug:scopes',             (p: { frameId: number }) => localDebugAdapter.scopes(p.frameId)],
            ['debug:expandVariable',     (p: { variableId: number }) => localDebugAdapter.expandVariable(p.variableId)],
            ['debug:eval',               (p: { frameId: number; expression: string }) => localDebugAdapter.eval(p.frameId, p.expression)],
            ['debug:repl',               (p: { frameId: number; code: string }) => localDebugAdapter.repl(p.frameId, p.code)],
            ['debug:setVariable',        (p: { frameId: number; variableId: number; rhs: string }) => localDebugAdapter.setVariable(p.frameId, p.variableId, p.rhs)],
            ['debug:resolveInstruction', (p: { insIndex: number }) => Promise.resolve(localDebugAdapter.resolveInstruction(p.insIndex))],
        ];
        // Channels that MUTATE debug state — after running them the
        // host's local panels show stale values until the user refreshes
        // manually. Re-fetch scopes for the currently-active frame so the
        // host sees observer-driven changes immediately, AND re-broadcast
        // topFrameScopes so the requesting observer (and anyone else
        // watching) reads the new value from the shared cache rather than
        // pulling stale data and triggering a follow-up RPC. Without the
        // re-broadcast, the observer's refreshScopes() falls back to a
        // second RPC for fresh data — that round-trip is what made the
        // post-edit UI feel janky and racy.
        const debugMutatingChannels = new Set(['debug:setVariable', 'debug:eval', 'debug:repl']);
        for (const [channel, handler] of debugRpcRoutes) {
            const off = session.onRequest(channel, async (_peerId, payload) => {
                const err = debugRpcCheck();
                if (err) throw new Error(err);
                const result = await handler(payload as any);
                if (debugMutatingChannels.has(channel) && activeFrameId != null && debugSessionActive) {
                    // Fire-and-forget — we've already got `result` for
                    // the observer; the host's scope refresh + observer
                    // re-broadcast are independent UI hygiene that
                    // shouldn't block the RPC response.
                    void (async () => {
                        await refreshScopes(activeFrameId!);
                        await rebroadcastTopFrameScopes();
                    })();
                }
                return result;
            });
            collabRuntimeUnsubs.push(off);
        }

        // ── Phase 2B: adapter swap on remote initiator changes ───────────
        // Whenever `debugState.initiatorClientId` toggles between "us /
        // empty" and "some other peer", swap the facade to the matching
        // adapter and mirror the UI flags so the observer's Debug panel
        // surfaces as if their own debugger were live.
        let currentRemoteAdapter: ReturnType<typeof createRemoteDebugAdapter> | null = null;
        // Track the last observed `paused` value so we can detect the
        // host's resume edge (paused: true → false). Local code paths
        // handle this by manually clearing the current-line decoration
        // and updating the status pill after each step/continue; on the
        // observer side those calls don't run, so without an explicit
        // edge-handler the status stays stuck on "paused on breakpoint"
        // and the line highlight lingers after the host resumes.
        let lastObservedPaused = false;
        // Track the previous topFrameScopes reference so we can detect
        // a mutation re-broadcast (the host re-publishes scopes after a
        // setVariable/eval/repl). On a fresh reference, re-render the
        // observer's variables panel without waiting for the next pause —
        // otherwise the new value lands in the Y.Map but the UI keeps
        // showing the value from the BREAKPOINT snapshot.
        let lastObservedTopFrameScopes: unknown = undefined;
        const swapOnDebugStateChange = () => {
            const initiator = session.debugState.get('initiatorClientId') as number | undefined;
            const observingRemote = initiator != null && initiator !== session.awareness.clientID;

            if (observingRemote) {
                // Build (or keep) the remote adapter; point facade at it.
                const justAttached = !currentRemoteAdapter;
                if (!currentRemoteAdapter) {
                    currentRemoteAdapter = createRemoteDebugAdapter({ session });
                    dbg.setInner(currentRemoteAdapter);
                }
                // Mirror the host's session state into our UI flags so
                // the debug panel + step buttons enable. We're not
                // running our own debugger; these flags are about
                // "is the UI in debug mode".
                const newPaused = Boolean(session.debugState.get('paused'));
                debugSessionActive = true;
                debugPaused = newPaused;
                // Resume edge: host just hit Continue / Step. Update the
                // status pill, clear the current-line highlight in our
                // editor (otherwise it sticks at the previous pause
                // point), and let the synthesized REV_REQUEST_BREAKPOINT
                // event re-set everything on the next pause.
                if (lastObservedPaused && !newPaused) {
                    setDebugStatus('running', 'running');
                    setCurrentLine(null);
                }
                // Re-render scopes when paused AND the topFrameScopes
                // reference changed (mutation re-broadcast). renderScopes
                // is cheap (DOM rebuild from cached data), no RPC.
                const newTopFrameScopes = session.debugState.get('topFrameScopes');
                if (newPaused
                    && newTopFrameScopes !== lastObservedTopFrameScopes
                    && newTopFrameScopes
                    && typeof newTopFrameScopes === 'object'
                ) {
                    try { renderScopes(((newTopFrameScopes as any).scopes ?? []) as DebugScope[]); }
                    catch (e) { console.warn('[fade-collab] observer scope re-render failed', e); }
                }
                lastObservedTopFrameScopes = newTopFrameScopes;
                // Pause edge: status text gets set by onAnyDebugEvent's
                // REV_REQUEST_BREAKPOINT case (which fires via the
                // RemoteDebugAdapter's synthesized event), so we don't
                // need to duplicate that here.
                lastObservedPaused = newPaused;
                refreshRunButtons();
                refreshStopButton();
                setDebugButtons();
                // First time we swapped to remote — the RemoteAdapter's
                // constructor primed BEFORE `dbg.setInner()` attached the
                // facade fanout, so any synthesized REV_REQUEST_BREAKPOINT
                // for the current paused state was emitted to an empty
                // subscriber set and lost. Replay it now by directly
                // running refreshDebugView() if we're already paused AND
                // have a usable call stack cached.
                //
                // Without the callStack gate, refreshDebugView calls
                // dbg.stackFrames(), the cache is empty, and a 15-second
                // timeout RPC fires against a host whose iframe is mid-
                // `await dbg.continue()` (the host's startDebug path
                // broadcasts the initial {paused:true} before its
                // program has hit any breakpoint, so the host has no
                // frames to return yet). If we don't have frames, do
                // nothing — the subsequent BREAKPOINT broadcast carries
                // the call stack and will trigger a real refresh.
                if (justAttached && newPaused) {
                    const callStack = session.debugState.get('callStack');
                    if (Array.isArray(callStack) && callStack.length > 0) {
                        void refreshDebugView();
                    }
                }
            } else {
                // No remote initiator (or it's us). If we were observing,
                // tear down and revert to local.
                if (currentRemoteAdapter) {
                    dbg.setInner(localDebugAdapter);
                    try { currentRemoteAdapter.destroy(); } catch { /* ignore */ }
                    currentRemoteAdapter = null;
                    // Clear the mirrored flags ONLY if we set them while
                    // observing — guarded against clobbering a real
                    // local debug session that's underway on this peer.
                    if (initiator !== session.awareness.clientID) {
                        debugSessionActive = false;
                        debugPaused = false;
                        debugFatalException = false;
                        setDebugStatus('program exited', 'idle');
                        setCurrentLine(null);
                        // Clear the inspection panels so the observer
                        // doesn't see stale frames/scopes/watches from
                        // the now-ended remote session.
                        clearDebugInspectionPanels();
                        setDebugEmptyStates(true);
                        refreshRunButtons();
                        refreshStopButton();
                        setDebugButtons();
                    }
                }
                lastObservedPaused = false;
                lastObservedTopFrameScopes = undefined;
            }
        };
        session.debugState.observe(swapOnDebugStateChange);
        collabRuntimeUnsubs.push(() => {
            session.debugState.unobserve(swapOnDebugStateChange);
            // Make sure we leave the facade pointing at LOCAL when the
            // session ends — otherwise the next session-less debug
            // attempt would route through a stale remote adapter that
            // can't reach anyone.
            if (currentRemoteAdapter) {
                dbg.setInner(localDebugAdapter);
                try { currentRemoteAdapter.destroy(); } catch { /* ignore */ }
                currentRemoteAdapter = null;
            }
        });
        // Initial pass — handle the case where we joined a session that
        // already has an active debug initiator.
        swapOnDebugStateChange();

        // ── Shared breakpoints ──────────────────────────────────────────
        // Mirror the session's breakpoints Y.Map into the local
        // `remoteBreakpointsByUri` view (own entries are excluded; they
        // live in `breakpointsByUri`). After every change, repaint the
        // gutter and refresh the per-peer CSS so each peer's
        // breakpoints render in their identity colour.
        const reapplyBreakpointsFromSession = () => {
            remoteBreakpointsByUri.clear();
            const selfClientId = session.awareness.clientID;
            for (const entry of session.breakpoints.values()) {
                const e = entry as { file: string; line: number; ownerClientId: number };
                if (e.ownerClientId === selfClientId) continue;
                const uri = monaco.Uri.file(`/workspace/${e.file}`).toString();
                let m = remoteBreakpointsByUri.get(uri);
                if (!m) { m = new Map<number, number>(); remoteBreakpointsByUri.set(uri, m); }
                m.set(e.line, e.ownerClientId);
            }
            refreshBreakpointDecorations();
            updatePeerBreakpointStyles(session.getState().peers);
            // If THIS peer is the active local debugger, push the
            // updated union of local + remote breakpoints to the
            // runtime so observer-set breakpoints actually break.
            // (Observers reach this path too, but their dbg points at
            // the remote adapter, so setBreakpoints would just RPC back
            // to the host — which is wasteful but harmless. The
            // `isLocalDebugInitiator` guard avoids the round-trip.)
            if (debugSessionActive && isLocalDebugInitiator()) {
                syncBreakpointsToWorker();
            }
        };
        session.breakpoints.observe(reapplyBreakpointsFromSession);
        collabRuntimeUnsubs.push(() => {
            session.breakpoints.unobserve(reapplyBreakpointsFromSession);
            remoteBreakpointsByUri.clear();
            // Drop the per-peer breakpoint CSS that was injected for
            // this session — leaving stale `fade-breakpoint-peer-<id>`
            // rules behind would tint any future identical clientID
            // (rare but possible across reconnects) with the wrong
            // colour.
            updatePeerBreakpointStyles([]);
            refreshBreakpointDecorations();
        });
        // Initial pass + push any breakpoints we already have locally
        // into the session map so peers see them immediately.
        reapplyBreakpointsFromSession();
        for (const [uri, lines] of breakpointsByUri) {
            const m = /^file:\/\/\/workspace\/(.+)$/.exec(uri);
            if (!m) continue;
            const file = m[1];
            for (const line of lines) {
                const key = `${file}:${line}`;
                if (session.breakpoints.has(key)) continue;
                session.breakpoints.set(key, { file, line, ownerClientId: session.awareness.clientID });
            }
        }

        // Show / hide the observer game overlay based on whether any
        // OTHER peer is currently running or debugging. Driven by
        // awareness state, not by frame arrival, so the "watching Alice
        // run" banner appears even if frames haven't started flowing yet
        // (e.g. she's on monogame and frames are unavailable).
        const onState = session.onStateChange((st) => {
            const activeRunner = st.peers.find((p) => !p.isSelf
                && (p.activity === 'running' || p.activity === 'debugging'));
            // Diagnostic — fires every onStateChange. Filter on
            // [collab-state] in DevTools to watch awareness flow.
            console.info('[collab-state] activeRunner=%s remoteActivityInProgress=%s peers=%d',
                activeRunner ? `${activeRunner.identity.displayName}(${activeRunner.activity})` : 'null',
                remoteActivityInProgress, st.peers.length);
            if (activeRunner) {
                showGameStreamOverlay(activeRunner.identity.displayName, activeRunner.activity);
            } else {
                // Host stopped, but the live session is still alive —
                // hide just the banner and let the last frame linger
                // on the canvas (same UX the host gets locally; they
                // keep their last frame until Reset). hideGameStreamOverlay
                // is reserved for full session teardown above.
                clearGameStreamBanner();
            }
            // Drive observer-side button state: if someone else owns the
            // runtime, our Run/Reset goes grey ("host has control") and
            // our Stop becomes the way to end the shared session.
            const newRemote = !!activeRunner;
            if (newRemote !== remoteActivityInProgress) {
                remoteActivityInProgress = newRemote;
                refreshRunButtons();
                refreshStopButton();
                console.info('[collab-state] remoteActivityInProgress flipped → %s; stopBtn.disabled=%s banner.hidden=%s',
                    newRemote,
                    (document.getElementById('stop-btn') as HTMLButtonElement | null)?.disabled,
                    (document.getElementById('game-stream-banner') as HTMLElement | null)?.hidden);
            }
            // Live-session arrival can race the editor's focus state: if
            // the editor already had focus when the peer joined, the
            // monogame tick is paused right now — kick it back on so the
            // game-frame stream and Debug UI relay resume for the new
            // observer. Same condition pauseMgTick checks (someone else
            // is in the room).
            if (st.peers.some((p) => !p.isSelf)) resumeMgTick();
        });
        collabRuntimeUnsubs.push(onState);
    }

    function snapshotDebugState(session: NonNullable<ReturnType<NonNullable<typeof liveSessionHandle>['getSession']>>): {
        initiatorClientId: number;
        initiatorName: string;
        paused: boolean;
        currentFile: string | null;
        currentLine: number | null;
    } | null {
        const initiatorClientId = session.debugState.get('initiatorClientId');
        if (typeof initiatorClientId !== 'number') return null;
        // Resolve initiator name from awareness — falls back to 'Someone'
        // if their state hasn't propagated yet.
        const peers = session.getState().peers;
        const initiator = peers.find((p) => p.clientId === initiatorClientId);
        return {
            initiatorClientId,
            initiatorName: initiator?.identity.displayName ?? 'Someone',
            paused: Boolean(session.debugState.get('paused')),
            currentFile: (session.debugState.get('currentFile') as string | null) ?? null,
            currentLine: (session.debugState.get('currentLine') as number | null) ?? null,
        };
    }

    // ── Phase 2A overlay rendering (observer side) ────────────────────────
    // The overlay is a div in index.html (#game-stream-overlay) with a
    // banner + canvas. We draw incoming JPEG frames into the canvas via
    // an off-DOM <img> as the decoder. Frame URL strings are kept in a
    // throwaway scope so the GC handles cleanup; no manual revoke needed.

    // dockview-core wraps each panel-cell in a `dv-render-overlay` div
    // initialised with style.visibility:hidden for one-frame anti-flicker
    // (dockview-core.js:11465-11467). The wrapper is supposed to clear
    // visibility once dockview's RAF runs AND the panel is active. In
    // practice on the static panel-cells pool + restored layouts we use,
    // the RAF can fire BEFORE the panel becomes active and dockview
    // never re-clears the inline visibility:hidden. The canvas inside
    // happily renders incoming JPEG frames (gameFrameReceiveStats.rendered
    // increments correctly) but the GPU never composites them — the
    // observer sees a blank panel until they hover/click the game tab,
    // which finally nudges dockview to re-evaluate.
    //
    // Same root cause + fix as the debug-ui panel: force visibility:visible
    // on the overlay so a stuck-hidden ancestor doesn't keep frames off-
    // screen (CSS spec — child visibility:visible overrides ancestor
    // visibility:hidden). Mirror dockview's CORRECT hide signal via a
    // MutationObserver so tab-switching still hides the overlay.
    //
    // applyGameOverlayState is the single source of truth for what the
    // overlay's inline display/visibility should be. show/hide just flip
    // overlay.hidden and call it; the observer also calls it on every
    // wrapper-style mutation.
    let gameOverlayDvSyncInstalled = false;
    function applyGameOverlayState(): void {
        const overlay = document.getElementById('game-stream-overlay');
        if (!overlay) return;
        // Always assert visibility:visible to defeat the wrapper's stuck
        // anti-flicker visibility:hidden. Harmless when display is 'none'
        // (display:none wins regardless).
        overlay.style.visibility = 'visible';
        // Walk to the dockview wrapper and check whether dockview is
        // correctly signaling "panel is inactive" (pointer-events:none
        // + visibility:hidden together — visibility alone is the stuck
        // anti-flicker state we want to ignore).
        let p: HTMLElement | null = overlay.parentElement;
        while (p && !p.classList.contains('dv-render-overlay')) p = p.parentElement;
        const dockviewWantsHidden = !!p
            && p.style.pointerEvents === 'none'
            && p.style.visibility === 'hidden';
        const shouldHide = overlay.hidden || dockviewWantsHidden;
        // Force display inline rather than relying on the [hidden] +
        // CSS cascade — the visibility:visible we leave inline above
        // can otherwise keep the canvas's compositor layer pinned even
        // after [hidden] kicks in, so the GPU keeps painting the last
        // frame's cached pixels until the user hovers / nudges a
        // repaint.
        overlay.style.display = shouldHide ? 'none' : '';
    }
    function ensureGameOverlayVisibilitySync(overlay: HTMLElement): void {
        if (gameOverlayDvSyncInstalled) return;
        let p: HTMLElement | null = overlay.parentElement;
        while (p && !p.classList.contains('dv-render-overlay')) p = p.parentElement;
        if (!p) return;  // dockview hasn't adopted the panel yet — retry next call
        const observer = new MutationObserver(applyGameOverlayState);
        observer.observe(p, { attributes: true, attributeFilter: ['style'] });
        gameOverlayDvSyncInstalled = true;
    }

    function showGameStreamOverlay(initiatorName: string, activity: string): void {
        const overlay = document.getElementById('game-stream-overlay');
        const banner = document.getElementById('game-stream-banner');
        if (!overlay || !banner) return;
        overlay.hidden = false;
        banner.hidden = false;
        ensureGameOverlayVisibilitySync(overlay);
        applyGameOverlayState();
        const verb = activity === 'debugging' ? 'is debugging' : 'is running the program';
        banner.textContent = `${initiatorName} ${verb}. Your view is a low-FPS stream — input control stays with them.`;
    }

    /** Host stopped running, but the live session is still active. Hides
     *  ONLY the "X is running…" banner — the canvas keeps its last frame
     *  on screen, intentionally, to mirror what the host sees on their
     *  side (their own iframe also keeps the last frame until they hit
     *  Reset). When the next Run kicks off, the banner shows again and
     *  the canvas's first new frame overwrites the lingering one. */
    function clearGameStreamBanner(): void {
        const banner = document.getElementById('game-stream-banner');
        if (!banner) return;
        banner.hidden = true;
    }


    /** Full teardown — used only when the live session itself ends. The
     *  overlay disappears entirely (canvas included) so the local panel
     *  surfaces underneath (web-preview / monogame iframe) can show
     *  through again. Don't use this on the host-stop transition; use
     *  clearGameStreamBanner() for that. */
    function hideGameStreamOverlay(): void {
        const overlay = document.getElementById('game-stream-overlay');
        if (!overlay) return;
        overlay.hidden = true;
        applyGameOverlayState();
        // Clear the canvas so a future re-show doesn't briefly flash
        // the previous session's last frame before the first new
        // frame arrives.
        const canvas = document.getElementById('game-stream-canvas') as HTMLCanvasElement | null;
        if (canvas) {
            const ctx = canvas.getContext('2d');
            if (ctx) ctx.clearRect(0, 0, canvas.width, canvas.height);
        }
    }

    async function renderGameFrame(bytes: Uint8Array): Promise<void> {
        const canvas = document.getElementById('game-stream-canvas') as HTMLCanvasElement | null;
        if (!canvas) {
            console.debug('[fade-collab] renderGameFrame: no #game-stream-canvas — overlay maybe not mounted');
            return;
        }
        // Build a Blob → object URL → <img>. We could decode directly via
        // createImageBitmap for speed; sticking with <img> for now because
        // it's universally supported and we're at 5 fps either way.
        const blob = new Blob([bytes as BlobPart], { type: 'image/jpeg' });
        const url = URL.createObjectURL(blob);
        try {
            const img = new Image();
            await new Promise<void>((resolve, reject) => {
                img.onload = () => resolve();
                img.onerror = () => reject(new Error('frame decode failed'));
                img.src = url;
            });
            // Resize the canvas's bitmap to match the source so we don't
            // double-scale (CSS scales the display, the bitmap stays
            // 1:1 with the source). Avoid re-sizing on every frame if
            // the dimensions match.
            if (canvas.width !== img.naturalWidth || canvas.height !== img.naturalHeight) {
                canvas.width = img.naturalWidth;
                canvas.height = img.naturalHeight;
            }
            const ctx = canvas.getContext('2d');
            if (ctx) ctx.drawImage(img, 0, 0);
            gameFrameReceiveStats.rendered++;
        } catch (e) {
            gameFrameReceiveStats.decodeErrors++;
            console.debug('[fade-collab] frame decode failed', e);
        }
        finally {
            URL.revokeObjectURL(url);
        }
    }

    // ── Phase 2B: host writes debug state ────────────────────────────────
    /** Idempotently push a partial debug-state update over the live
     *  session. Only the active initiator should call this. Observers
     *  also reach this path via their RemoteDebugAdapter's synthesised
     *  REV_REQUEST_BREAKPOINT event — onAnyDebugEvent runs identically
     *  on both peers, and without this gate the observer would
     *  overwrite the host's debugState (currentFile, callStack, etc.)
     *  with their own (empty / wrong-file) values, breaking the locals
     *  view for everyone. */
    function isLocalDebugInitiator(): boolean {
        const session = liveSessionHandle?.getSession();
        if (!session) return true; // no session — local-only is implicitly initiator
        const initiator = session.debugState.get('initiatorClientId') as number | undefined;
        // First write (claiming the role) is allowed; subsequent writes
        // require the existing initiator to match us.
        return initiator === undefined || initiator === session.awareness.clientID;
    }
    function broadcastDebugState(patch: Record<string, unknown>): void {
        const session = liveSessionHandle?.getSession();
        if (!session) return;
        // Allow this write if either: the patch is staking the initiator
        // claim (it sets initiatorClientId to us, or clears it), OR the
        // existing initiator is already us. Reject otherwise.
        const localClientId = session.awareness.clientID;
        const patchInitiator = (patch as { initiatorClientId?: number | null }).initiatorClientId;
        const claimingOrClearing = patchInitiator === localClientId || patchInitiator === null;
        if (!claimingOrClearing && !isLocalDebugInitiator()) return;
        try { session.setDebugState(patch); }
        catch (e) { console.warn('[fade-collab] setDebugState failed', e); }
    }

    /** Re-broadcast just the top-frame scope payload. Called after an
     *  observer-driven mutation (setVariable/eval/repl) lands so observers
     *  read the fresh value from the shared cache instead of falling back
     *  to a second RPC. Cheaper than fetchPausedFramesAndBroadcast (which
     *  also re-fetches stack frames) since the call stack is unchanged by
     *  these mutations. */
    async function rebroadcastTopFrameScopes(): Promise<void> {
        if (!isLocalDebugInitiator()) return;
        if (!debugSessionActive) return;
        try {
            const scopes = await localDebugAdapter.scopes(0);
            broadcastDebugState({ topFrameScopes: scopes ?? null });
        } catch (e) {
            console.warn('[fade-collab] rebroadcastTopFrameScopes failed', e);
        }
    }

    /** Wipe shared debug state on session end. Only valid from the peer
     *  that owns the active initiator role — observers that reach this
     *  path via REV_REQUEST_EXITED on a synthesised remote event should
     *  not clear the host's state out from under them. */
    function clearBroadcastDebugState(): void {
        const session = liveSessionHandle?.getSession();
        if (!session) return;
        if (!isLocalDebugInitiator()) return;
        try { session.clearDebugState(); }
        catch (e) { console.warn('[fade-collab] clearDebugState failed', e); }
    }

    // ── Phase 2B observer banner ─────────────────────────────────────────
    // For now we just surface "Alice is paused at foo.fbasic:42" in the
    // game-stream banner (when there's an active observer overlay) and
    // in the chip. A future iteration will replicate the call stack +
    // locals into the actual Debug panel; that's a bigger refactor of
    // the existing debug-render code path.
    function updateDebugObserverBanner(snapshot: ReturnType<typeof snapshotDebugState>): void {
        const banner = document.getElementById('game-stream-banner');
        if (!banner) return;
        if (!snapshot) return;
        // If the local peer initiated the debug session, the banner
        // shouldn't talk in the third person — leave it alone.
        const session = liveSessionHandle?.getSession();
        if (!session || snapshot.initiatorClientId === session.awareness.clientID) return;
        const loc = snapshot.currentFile
            ? ` at ${snapshot.currentFile}${snapshot.currentLine ? `:${snapshot.currentLine}` : ''}`
            : '';
        const state = snapshot.paused ? `paused${loc}` : `running`;
        banner.textContent = `${snapshot.initiatorName} is debugging — ${state}. (You're observing; debugger controls are read-only for now.)`;
    }

    /** Open (or re-focus) a read-only Monaco diff editor in its own
     *  dockview tab. `id` identifies the panel uniquely — clicking
     *  "Show diff" twice for the same context just re-activates the
     *  existing tab. Caller is responsible for fetching before/after
     *  text up front; this helper doesn't know about the controller. */
    function openDiffViewer(args: {
        id: string;
        params: DiffViewerParams;
    }) {
        try {
            const existing = dockApi.getPanel(args.id);
            if (existing) {
                existing.api.updateParameters(args.params);
                existing.api.setActive();
                return;
            }
            dockApi.addPanel({
                id: args.id,
                component: 'diff-viewer',
                title: args.params.title,
                position: { referencePanel: 'editor', direction: 'within' },
                renderer: 'always',
                params: args.params as unknown as Record<string, any>,
            });
        } catch (e) {
            console.warn('[fade] openDiffViewer failed', e);
        }
    }

    /** Paint the live-session pill into the header: hidden when no
     *  session is running, otherwise shows one dot per peer (your own
     *  ring-highlighted) + the peer count. Clicking opens the Live
     *  Session panel. Subscribes to both the session-start hook and the
     *  session-state hook so the chip updates as peers join/leave. */
    let liveSessionStateUnsub: (() => void) | null = null;
    // Coalesce rapid render requests into the next animation frame. State
    // events fire frequently (every cursor move, every meta change) and
    // each chip render writes ~10 DOM nodes — without this, typing in a
    // session pegs the main thread on chip rebuilds.
    let liveSessionChipScheduled = false;
    function renderLiveSessionChip() {
        if (liveSessionChipScheduled) return;
        liveSessionChipScheduled = true;
        requestAnimationFrame(() => {
            liveSessionChipScheduled = false;
            renderLiveSessionChipNow();
        });
    }
    function renderLiveSessionChipNow() {
        const host = document.getElementById('live-session-chip');
        if (!host) return;
        const session = liveSessionHandle?.getSession() ?? null;
        // Strip is always visible. Rebuild children every paint —
        // children are: one `.sharing-status-icon` button (the live-
        // share affordance) + one peer pill per participant when a
        // session is active. The strip itself is transparent; each
        // child carries its own visual treatment so we never get a
        // pill-inside-a-pill look.
        host.replaceChildren();
        host.onclick = null;
        host.title = '';

        // Live-share icon button — visually identical to the git
        // `#sharing-status-icon` button (same class) so hover states
        // match across the top-bar. Click always opens the Live
        // Session panel, regardless of session state.
        const iconBtn = document.createElement('button');
        iconBtn.type = 'button';
        iconBtn.className = 'sharing-status-icon';
        iconBtn.setAttribute('aria-label', 'Open Live Session panel');
        const iconGlyph = document.createElement('span');
        iconGlyph.className = 'codicon codicon-vm-connect';
        iconBtn.appendChild(iconGlyph);
        iconBtn.addEventListener('click', focusLiveSessionPanel);
        host.appendChild(iconBtn);

        if (!session) {
            host.classList.remove('live-session-chip-syncing', 'live-session-chip-warning');
            iconBtn.title = 'Live Session: not started. Click to open Live Session panel.';
            updatePeerCursorStyles([]);
            if (liveSessionStateUnsub) { liveSessionStateUnsub(); liveSessionStateUnsub = null; }
            return;
        }
        // Subscribe once per session (the listener is replaced whenever
        // the session changes via the onSessionChange hook below).
        if (!liveSessionStateUnsub) {
            liveSessionStateUnsub = session.onStateChange(() => renderLiveSessionChip());
        }
        const state = session.getState();
        host.classList.toggle('live-session-chip-syncing', state.sync != null);
        host.classList.toggle('live-session-chip-warning', state.connectionWarning != null);

        if (state.connectionWarning && !state.sync) {
            iconBtn.title = state.connectionWarning;
            const note = document.createElement('span');
            note.textContent = state.role === 'host' ? '⚠ no peers' : '⚠ unreachable';
            host.appendChild(note);
            updatePeerCursorStyles(state.peers);
            return;
        }

        if (state.sync) {
            const pct = state.sync.total > 0
                ? Math.min(100, Math.round((state.sync.completed / state.sync.total) * 100))
                : 0;
            const note = document.createElement('span');
            note.textContent = `syncing ${state.sync.completed}/${state.sync.total} · ${pct}%`;
            iconBtn.title = state.sync.currentFile
                ? `Syncing ${state.sync.currentFile}`
                : 'Workspace sync in progress';
            host.appendChild(note);
            updatePeerCursorStyles(state.peers);
            return;
        }

        const otherCount = state.peers.filter((p) => !p.isSelf).length;
        const total = state.peers.length;
        iconBtn.title = state.role === 'host'
            ? `Live Session: hosting · ${total} ${total === 1 ? 'participant' : 'participants'}${otherCount > 0 ? ` (${otherCount} other)` : ''}. Click to open Live Session panel.`
            : `Live Session: joined · ${total} ${total === 1 ? 'participant' : 'participants'}. Click to open Live Session panel.`;

        // One pill per participant — self first (so "you" is anchored
        // left, immediately after the icon), then others alphabetically.
        // Each pill is a direct child of the strip, NOT nested inside
        // an outer chip. The user explicitly asked for this layout so
        // peers don't appear to be contained inside the host's pill.
        const sorted = [...state.peers].sort((a, b) => {
            if (a.isSelf !== b.isSelf) return a.isSelf ? -1 : 1;
            return a.identity.displayName.localeCompare(b.identity.displayName);
        });
        for (const peer of sorted) {
            host.appendChild(renderPeerPill(peer));
        }

        // Refresh the per-peer cursor color stylesheet — the awareness
        // states (which carry user color + clientID) are what y-monaco's
        // decoration CSS class names key on.
        updatePeerCursorStyles(state.peers);
    }

    /** One peer pill in the header: colored swatch + name + optional
     *  activity badge (`▶ run`, `🐞 debug`, etc.). Tooltip carries the
     *  active file. Clicking opens the Live Session panel for the full
     *  collaborator list / actions. */
    function renderPeerPill(peer: PeerView): HTMLElement {
        const pill = document.createElement('span');
        pill.className = 'live-session-chip-pill';
        pill.style.setProperty('--peer-color', peer.identity.color);
        if (peer.isSelf) pill.dataset.self = 'true';
        // Use a tinted background of the peer's color via CSS variable;
        // the actual rule lives in index.html so a single class can be
        // styled per-peer without re-emitting CSS for each pill.

        const swatch = document.createElement('span');
        swatch.className = 'live-session-chip-pill-swatch';
        swatch.style.backgroundColor = peer.identity.color;
        pill.appendChild(swatch);

        const name = document.createElement('span');
        name.className = 'live-session-chip-pill-name';
        name.textContent = peer.identity.displayName + (peer.isSelf ? ' (you)' : '');
        pill.appendChild(name);

        // Focus context — where the peer's pointer currently is.
        // Renders a faint "in editor.fbasic" / "in game" / etc.
        // suffix; clearer at-a-glance than just a name. Idle peers
        // (no focus) drop this segment.
        const focusText = peerFocusLabel(peer);
        if (focusText) {
            const focus = document.createElement('span');
            focus.className = 'live-session-chip-pill-focus';
            focus.textContent = focusText;
            pill.appendChild(focus);
        }

        // Activity badge — only when the peer is doing something
        // noteworthy. Idle is the default and isn't worth a glyph.
        const a = peer.activity ?? 'idle';
        if (a !== 'idle') {
            const badge = document.createElement('span');
            badge.className = `live-session-chip-pill-activity activity-${a}`;
            badge.textContent = activityLabel(a);
            pill.appendChild(badge);
        }

        const tooltipFocus = focusText ? ` — ${focusText}` : '';
        pill.title = `${peer.identity.displayName}${peer.isSelf ? ' (you)' : ''}${tooltipFocus}${a !== 'idle' ? ` (${a})` : ''}`;
        // Pill click: jump to wherever the peer is looking. If their
        // focus is on a file we have open, switch to that tab so the
        // user can see the same content; if focus is on the game
        // panel, activate it. Pulse their cursor for ~1.4s either way
        // so the user can spot it. Self-pill just opens the panel.
        pill.addEventListener('click', () => focusPeer(peer));
        return pill;
    }

    /** Action handler for clicking a peer's chip pill — focuses the
     *  surface they're currently on (file tab / game) and pulses
     *  their cursor for visibility. Falls back to opening the Live
     *  Session panel when there's no concrete focus to follow. */
    function focusPeer(peer: PeerView) {
        if (peer.isSelf) { focusLiveSessionPanel(); return; }
        // Game wins if their pointer is currently there.
        if (peer.focus?.scope === 'game') {
            try { dockApi.getPanel('game')?.api.setActive(); }
            catch { /* ignore */ }
            window.setTimeout(() => sharedCursorHandle?.pulseCursor(peer.clientId), 30);
            return;
        }
        // Editor focus → switch to the peer's file. If their cursor
        // hasn't moved yet (focus is null) we still want the click to
        // do something useful, so fall back to `peer.activeFile` —
        // that's broadcast as soon as they open the file, before any
        // mouse movement. Last resort: open the Live Session panel.
        const file = (peer.focus?.scope === 'editor' ? peer.focus.file : null)
            ?? peer.activeFile;
        if (!file) { focusLiveSessionPanel(); return; }

        // Activate the editor dockview panel before swapping tabs —
        // otherwise editor.focus() inside openFile won't take.
        try {
            const editorPanel = dockApi.getPanel('editor');
            if (editorPanel && !editorPanel.api.isActive) editorPanel.api.setActive();
        } catch { /* ignore */ }

        const tab = tabs.get(file);
        if (tab && editor) {
            setActiveName(file);
            editor.setModel(tab.model);
            renderTabs();
            renderFileListSelection();
            editor.focus();
        } else if (!isBinaryFileName(file)) {
            // File isn't open as a tab yet. Open it from the workspace
            // — same path the file-list click uses. openFile is async
            // (workspace.read), so the pulse delay must clear it.
            void openFile(workspace, file);
        }
        // 80ms delay — longer than the openFile sync case to cover the
        // async workspace.read + model creation that runs when the
        // file wasn't open. Cursor render needs to have re-evaluated
        // peer-in-active-file for the pulse to find an element.
        window.setTimeout(() => sharedCursorHandle?.pulseCursor(peer.clientId), 80);
    }

    /** Short focus label for the chip pill: "in main.fbasic" /
     *  "in game" / "" when the peer's pointer isn't on a shared
     *  surface. Falls back to peer.activeFile if no live focus is set
     *  so the pill always shows SOMETHING useful when the peer is
     *  active in the editor but hasn't moved their mouse yet. */
    function peerFocusLabel(peer: PeerView): string {
        if (peer.focus?.scope === 'editor') {
            const basename = peer.focus.file.split('/').pop() ?? peer.focus.file;
            return `in ${basename}`;
        }
        if (peer.focus?.scope === 'game') return 'in game';
        if (peer.activeFile) {
            const basename = peer.activeFile.split('/').pop() ?? peer.activeFile;
            return `editing ${basename}`;
        }
        return '';
    }

    function activityLabel(activity: string): string {
        // Plain-text badges — no glyphs. The activity-* CSS class
        // already color-codes the badge so the meaning is conveyed
        // without dropping emoji into the strip (user feedback: the
        // emoji+text combo looked noisy and unprofessional).
        switch (activity) {
            case 'running':   return 'run';
            case 'debugging': return 'debug';
            case 'testing':   return 'tests';
            case 'syncing':   return 'sync';
            default:          return activity;
        }
    }

    function focusLiveSessionPanel() {
        try {
            let panel = dockApi.getPanel('live-session');
            if (!panel) {
                const ref = dockApi.getPanel('editor')?.id;
                dockApi.addPanel({
                    id: 'live-session',
                    component: 'live-session',
                    title: 'Live Session',
                    renderer: 'always',
                    position: ref ? { referencePanel: ref, direction: 'within' } : undefined,
                });
                panel = dockApi.getPanel('live-session');
            }
            panel?.api.setActive();
        } catch (e) {
            console.warn('[fade-collab] focus live-session failed', e);
        }
    }

    /** Inject a per-peer cursor-color stylesheet into <head>. y-monaco
     *  decorates remote selections with class names like
     *  `yRemoteSelection-<clientID>` and `yRemoteSelectionHead-<clientID>`,
     *  so we write one CSS rule per active peer with their color. Also
     *  attaches a `::after` pseudo-element to the head with the peer's
     *  display name so cursors are self-labelling. */
    function updatePeerCursorStyles(peers: ReadonlyArray<{ clientId: number; isSelf: boolean; identity: { color: string; displayName: string } }>) {
        const STYLE_ID = 'fade-collab-peer-cursors';
        let style = document.getElementById(STYLE_ID) as HTMLStyleElement | null;
        if (peers.length === 0) {
            if (style) style.remove();
            return;
        }
        if (!style) {
            style = document.createElement('style');
            style.id = STYLE_ID;
            document.head.appendChild(style);
        }
        const escape = (s: string) => s.replace(/[\\"]/g, '\\$&');
        const lines: string[] = [];
        // Background palette for the floating name labels: a softened
        // version of the user's color (RGBA with alpha) so when the
        // decoration is in its quiet state the label doesn't shout. Done
        // via a wrapping div in CSS — we can't change the alpha on the
        // peer's color string at runtime, so we use opacity on the
        // pseudo-element instead.
        for (const peer of peers) {
            if (peer.isSelf) continue;  // we don't render our own cursor decorations
            const id = peer.clientId;
            const color = peer.identity.color;
            // Selection background. Quiet by default (alpha-ish via
            // opacity), brighter when the user explicitly hovers the
            // editor line containing it.
            lines.push(`.yRemoteSelection-${id} { background-color: ${color}; opacity: 0.10; transition: opacity 120ms ease; }`);
            // Caret line. Reasonably visible but thinner — full opacity
            // would draw the eye every time a remote peer twitches their
            // cursor. The transition makes the hover brighten subtle.
            lines.push(`.yRemoteSelectionHead-${id} { border-left: 2px solid ${color}; position: relative; height: 100%; opacity: 0.45; transition: opacity 120ms ease; }`);
            // Floating name label attached to the head. Same quiet
            // baseline as the caret so the labels don't dominate the
            // editor when many peers are present.
            lines.push(
                `.yRemoteSelectionHead-${id}::after {`
                + ` content: "${escape(peer.identity.displayName)}";`
                + ` position: absolute; top: -1.2em; left: -2px;`
                + ` background: ${color}; color: white;`
                + ` font-size: 10px; padding: 0 4px;`
                + ` border-radius: 3px 3px 3px 0;`
                + ` white-space: nowrap;`
                + ` font-family: -apple-system, sans-serif;`
                + ` pointer-events: none;`
                + ` z-index: 10;`
                + ` opacity: 0.40;`
                + ` transition: opacity 120ms ease;`
                + ` }`
            );
            // Hover: bring everything to full visibility. Monaco lines
            // are inside `.view-line`; hovering a line surfaces every
            // remote decoration on it. Using the parent line as the
            // hover trigger (rather than the decoration itself) means
            // the small caret line doesn't have to be the target — the
            // whole row works.
            lines.push(`.view-line:hover .yRemoteSelection-${id} { opacity: 0.25; }`);
            lines.push(`.view-line:hover .yRemoteSelectionHead-${id} { opacity: 1; }`);
            lines.push(`.view-line:hover .yRemoteSelectionHead-${id}::after { opacity: 1; }`);
        }
        style.textContent = lines.join('\n');
    }

    /** Inject per-peer breakpoint glyph colors. Mirrors the cursor-style
     *  injector above. The default `.fade-breakpoint` is red (host's
     *  own / pre-session colour). Each peer gets their own class
     *  `fade-breakpoint-peer-<clientId>` tinted with their awareness
     *  color so the gutter glyph identifies who set the breakpoint.
     *  refreshBreakpointDecorations uses these class names when
     *  rendering remote breakpoints; this function just keeps the CSS
     *  rules in sync with the current peer list. */
    function updatePeerBreakpointStyles(peers: ReadonlyArray<{ clientId: number; isSelf: boolean; identity: { color: string; displayName: string } }>) {
        const STYLE_ID = 'fade-collab-peer-breakpoints';
        let style = document.getElementById(STYLE_ID) as HTMLStyleElement | null;
        if (peers.length === 0) {
            if (style) style.remove();
            return;
        }
        if (!style) {
            style = document.createElement('style');
            style.id = STYLE_ID;
            document.head.appendChild(style);
        }
        const lines: string[] = [];
        for (const peer of peers) {
            if (peer.isSelf) continue;
            // The codicon glyph is rendered via the ::before pseudo on
            // the .fade-breakpoint element, and the default red rule
            // is `.fade-breakpoint::before { color: #e51400 !important }`
            // — pseudo-element selectors carry an extra specificity
            // step that a plain class selector can't beat, !important or
            // not. So we have to target BOTH .fade-breakpoint-peer-<id>
            // AND .fade-breakpoint-peer-<id>::before to win the cascade
            // on the actual glyph.
            const c = peer.identity.color;
            lines.push(
                `.fade-breakpoint-peer-${peer.clientId},\n` +
                `.fade-breakpoint-peer-${peer.clientId}::before { color: ${c} !important; }`,
            );
        }
        style.textContent = lines.join('\n');
    }

    /** Paint the four sharing-status pills into the app header. Counts
     *  come straight off the controller's getters so we don't have to
     *  cache state at module scope. Clicking any pill focuses the
     *  Collaboration tab — the panel itself has the detailed view. */
    function renderSharingChips() {
        const host = document.getElementById('sharing-chips');
        if (!host || !sharingController) return;
        host.replaceChildren();
        // Chips only make sense in the context of a remote — they're
        // about save/publish/pull state vs. that remote. When the user
        // is disconnected (or the project never had a remote), there's
        // nothing to surface. Local saves persist across disconnect but
        // showing "↑ N unpublished" without a target is more confusing
        // than helpful.
        if (!sharingController.getRepoInfo()) return;
        const statusMap = sharingController.getStatusMap();
        let unsaved = 0;
        for (const status of statusMap.values()) if (status !== 'unchanged') unsaved++;
        const savesCount = sharingController.getPendingSaves().length;
        const pullCount = sharingController.getPendingPullPaths().size;
        const conflicts = sharingController.getConflictPaths();
        const conflictCount = conflicts.text.size + conflicts.binary.size;
        const addChip = (label: string, variant: string, title: string) => {
            const b = document.createElement('button');
            b.type = 'button';
            b.className = `sharing-chip sharing-chip-${variant}`;
            b.textContent = label;
            b.title = title;
            b.addEventListener('click', focusCollaborationPanel);
            host.append(b);
        };
        if (unsaved > 0)        addChip(`● ${unsaved} unsaved`,              'unsaved',     'Working-tree changes not yet snapshotted. Click to open Collaboration.');
        if (savesCount > 0)     addChip(`↑ ${savesCount} unpublished`,        'unpublished', 'Local saves not yet pushed to the remote. Click to open Collaboration.');
        if (pullCount > 0)      addChip(`↓ ${pullCount} remote`,              'remote',      'Remote branch has changes you haven\'t pulled. Click to open Collaboration.');
        if (conflictCount > 0)  addChip(`⚠ ${conflictCount} conflict${conflictCount === 1 ? '' : 's'}`, 'conflict', 'Unresolved merge conflicts. Click to open Collaboration.');
    }

    /** Shared "open the Collaboration panel" action used by both the
     *  header chips and the persistent status icon. Re-adds the panel
     *  to the dockview if the user previously closed it, so the click
     *  is reliable regardless of layout history. */
    function focusCollaborationPanel() {
        try {
            let panel = dockApi.getPanel('collaboration');
            if (!panel) {
                const workspaceRef = dockApi.getPanel('workspace')?.id
                    ?? dockApi.getPanel('editor')?.id;
                dockApi.addPanel({
                    id: 'collaboration',
                    component: 'collaboration',
                    title: 'Collaboration',
                    renderer: 'always',
                    position: workspaceRef ? { referencePanel: workspaceRef, direction: 'within' } : undefined,
                });
                panel = dockApi.getPanel('collaboration');
            }
            panel?.api.setActive();
        } catch (e) {
            console.warn('[fade] failed to focus Collaboration panel', e);
        }
    }

    /** Bind the header status icon's click handler once. The badge
     *  state is repainted by `renderSharingStatusIcon` whenever the
     *  controller emits a relevant change. */
    function wireSharingStatusIcon() {
        const btn = document.getElementById('sharing-status-icon');
        if (!btn) return;
        btn.addEventListener('click', focusCollaborationPanel);
    }


    /** Update the header status icon's badge to reflect the current
     *  GitHub connection state. Three states:
     *    - connected    → green dot (signed in + repo bound)
     *    - disconnected → grey dot  (signed in, no repo)
     *    - signedout    → darker grey (no token at all)
     *  Loading state is the initial CSS class; it persists until the
     *  controller is up and renderSharingStatusIcon runs at least
     *  once.  */
    function renderSharingStatusIcon() {
        const btn = document.getElementById('sharing-status-icon');
        const badge = document.getElementById('sharing-status-badge');
        if (!btn || !badge) return;
        const repo = sharingController?.getRepoInfo() ?? null;
        // Heuristic for "signed in": the controller exposes signed-in
        // identity via getStatusMap and getRepoInfo, but not a direct
        // "is user signed in" flag. If there's a repo, they're
        // definitely signed in. Otherwise we don't know with certainty
        // from this surface — but a non-empty status map means the
        // controller has done a refreshStatus, which requires a token.
        // Good enough for the icon.
        const signedIn = repo !== null || (sharingController?.getStatusMap().size ?? 0) > 0;
        const variant = repo ? 'connected' : (signedIn ? 'disconnected' : 'signedout');
        badge.className = `sharing-status-badge sharing-status-badge-${variant}`;
        btn.title = repo
            ? `GitHub: connected to ${repo.owner}/${repo.name} · ${repo.branch}. Click to open Collaboration.`
            : signedIn
                ? 'GitHub: signed in but no repo connected for this project. Click to open Collaboration.'
                : 'GitHub: not signed in. Click to open Collaboration.';
    }

    // Mount the Help panel's TOC + search + reader. Populated below
    // once the LSP worker is ready (no source needed — it reads from
    // the bridge's loaded CommandCollection).
    const helpCtl = mountHelpPanel({
        tokenizeSnippet: (source) => runner.tokenizeSnippet(source),
    });
    // Make sure the Help dockview panel is present and active. If the
    // user closed the tab entirely, openPanelById falls back through
    // healLayout which re-adds it with its default position. Returns
    // synchronously — getPanel('help') is available either way.
    function ensureHelpPanelOpen(): void {
        const panel = dockApi.getPanel('help');
        if (panel) {
            try { panel.api.setActive(); } catch { /* ignore */ }
            return;
        }
        // Panel missing — re-add via the same path the View menu uses.
        try { openPanelById('help'); } catch (e) { console.warn('[fade] failed to open help panel', e); }
        try { dockApi.getPanel('help')?.api?.setActive(); } catch { /* ignore */ }
    }

    // Open the Help tab + focus a specific command. Used by the hover
    // provider's "View in Help →" link, the right-click context menu
    // action, the Ctrl/Cmd-click handler in the editor, and external
    // probes via window.__fadeHelp.openCommand(name).
    //
    // fbasic source is case-insensitive but the help index is keyed by
    // the LSP's single canonical casing, which can be lower (`print`),
    // mixed (`setColor`), or anything else the C# command descriptor
    // emits. Resolving the user-typed word to that canonical name
    // requires a case-insensitive lookup against the index — a plain
    // toLowerCase() fallback misses camelCase names. helpCtl.
    // findCommandName does that resolution; selectCommand then runs
    // against the canonical name so the TOC item actually highlights
    // and scrolls into view.
    function openHelpForCommand(name: string): boolean {
        ensureHelpPanelOpen();
        const canonical = helpCtl.findCommandName(name);
        if (!canonical) return false;
        return helpCtl.selectCommand(canonical);
    }

    // Fallback for editor clicks on tokens that aren't commands —
    // language keywords like `if`, `for`, `dim`, `function`. The help
    // index has no entry for those (it lists commands only), but the
    // language docs (FadeBook/Language.md etc.) describe them. We don't
    // have a hard-coded keyword→anchor map; instead we route through
    // the help panel's global search, which spans both the command
    // reference and all loaded docs. The user sees ranked matches and
    // picks the right one (initGlobalSearch already auto-selects the
    // top result so it's one keystroke away when it's the obvious hit).
    //
    // Returns true when we filled the search box (regardless of whether
    // results were found — searching is the right action even when it
    // turns up empty, so the user sees that there's no doc coverage
    // rather than the click silently dropping).
    function searchHelpForKeyword(word: string): boolean {
        const q = word.trim();
        if (!q) return false;
        ensureHelpPanelOpen();
        helpCtl.searchFor(q);
        return true;
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

    // Mount AI panels (chat + model manager). Both reference the same
    // module-level engine state; chat needs workspace access for file tools.
    //
    // The adapter wraps OpfsWorkspace so reads/writes go through Monaco
    // models when a tab is open:
    //   read  — prefer the live model (covers unsaved edits not yet flushed
    //           to OPFS by the 600 ms debounce).
    //   write — write to OPFS *and* mirror the change into the live Monaco
    //           model via pushEditOperations, so the editor reflects what
    //           the LLM just wrote. Without this, apply_edit succeeded but
    //           the editor kept showing stale content — the user's
    //           keystroke would overwrite the agent's write 600 ms later.
    const aiWorkspaceAdapter = {
        list: () => workspace.list(),
        read: async (path: string) => {
            const tab = tabs.get(path);
            if (tab) return tab.model.getValue();
            return workspace.read(path);
        },
        write: async (path: string, content: string) => {
            await workspace.write(path, content);
            const tab = tabs.get(path);
            if (tab && tab.model.getValue() !== content) {
                tab.model.pushEditOperations(
                    [],
                    [{ range: tab.model.getFullModelRange(), text: content }],
                    () => null,
                );
                // We just persisted to OPFS, so the model isn't dirty.
                // (onDidChangeContent will flip it back to dirty if the
                // user types — that's fine.)
                tab.dirty = false;
            }
        },
        currentProject: () => workspace.currentProject(),
    };
    // Agent access to the asset Catalog — search + import, sharing the
    // singleton client and the same import path the Catalog tab uses
    // (catalog-imports/, then renderFileList + syncAssetsToRuntime).
    const aiCatalogApi = {
        async search(query: string, opts: { kind?: 'asset' | 'pack'; category?: 'image' | 'audio' | 'font'; tags?: string[]; limit?: number } = {}) {
            await sharedCatalogClient.load();
            return sharedCatalogClient.search(query, opts).map(e => ({
                id: e.id,
                name: e.name,
                kind: e.kind,
                mime: e.mime,
                tags: e.tags,
                description: e.description,
                bytes: e.bytes,
                license: e.license,
            }));
        },
        async import(id: number) {
            await sharedCatalogClient.load();
            const entry = sharedCatalogClient.getEntry(id);
            if (!entry) throw new Error(`No catalog entry with id ${id}`);
            if (entry.kind === 'pack') {
                throw new Error('Packs must be imported from the Catalog tab so you can pick which files to include.');
            }
            const path = `catalog-imports/${catalogFilename(entry)}`;
            const bytes = await sharedCatalogClient.fetchBytes(entry);
            await workspace.writeBytes(path, bytes);
            await renderFileList(workspace);
            try { await syncAssetsToRuntime(); } catch (e) {
                console.error('[fade] catalog import (agent): syncAssetsToRuntime failed', e);
            }
            return { name: entry.name, paths: [path] };
        },
    };

    mountAiChat(
        document.getElementById('ai-chat-pane')!.parentElement!,
        aiWorkspaceAdapter,
        {
            diagnostics: monacoDiagnosticsProvider,
            catalog: aiCatalogApi,
            // Re-read on every retrieval so a project-type switch mid-chat
            // (web → monogame or back) is picked up without a remount.
            getProjectType: () => currentProject?.type,
            tokenizeSnippet: (source) => runner.tokenizeSnippet(source),
            openDocCitation: async (source, heading) => {
                try { dockApi.getPanel('help')?.api?.setActive(); } catch { /* ignore */ }
                const opened = await helpCtl.openDocCitation(source, heading);
                if (!opened) {
                    const { externalDocUrl } = await import('./ai/rag/doc-citation-links');
                    const url = externalDocUrl(source);
                    if (url) window.open(url, '_blank', 'noopener');
                }
            },
            // Clicking/right-clicking a command or keyword in a chat snippet
            // opens its docs in the Help panel: command first, then language
            // keyword, then a docs search so the click is never a dead end.
            openSymbolDocs: async (symbol) => {
                try { dockApi.getPanel('help')?.api?.setActive(); } catch { /* ignore */ }
                const name = helpCtl.findCommandName(symbol);
                if (name) { helpCtl.selectCommand(name); return; }
                const jumped = await helpCtl.jumpToKeyword(symbol);
                if (!jumped) helpCtl.searchFor(symbol);
            },
            validateEditContent: createProjectAwareLspEditValidator({
                projectLspUri: PROJECT_LSP_URI,
                readProjectSources: readProjectSourcesSync,
                isProjectSource: (path) =>
                    (currentProject?.sources.includes(path) ?? false)
                    || (projectSourceMap?.hasFile(path) ?? false),
                checkDiagnostics: (uri, text) => runner.checkDocumentDiagnostics(uri, text),
                restoreProjectDoc: () => { rebuildAndPushProjectDoc(true); },
            }),
            getCommandNames: () => runner.listCommandDocs().then(
                docs => docs.map(d => d.name).filter(Boolean).sort(),
            ),
            // A command returns a value iff its signature isn't `void…` (the sig
            // format is "<ReturnType>R<params>"). Used by the review pass to flag
            // value-returning commands called without parentheses.
            getValueReturningCommands: () => runner.listCommandDocs().then(
                docs => docs.filter(d => d.signature && !/^\s*void/i.test(d.signature))
                    .map(d => d.name).filter(Boolean),
            ),
            // Full per-command docs (name + raw sig + rendered markdown) so the
            // coder node can inject the EXACT signatures/params for the commands
            // a given program will use — not just the bare name list.
            getCommandDocs: () => runner.listCommandDocs().then(
                docs => docs.filter(d => d.name).map(d => ({
                    name: d.name,
                    signature: d.signature ?? '',
                    markdown: d.markdown ?? '',
                })),
            ),
        },
    );
    mountAiModels(document.getElementById('ai-models-pane')!.parentElement!);

    // Playground app version — surfaced in Diagnostics and used to
    // drive the "What's new" popup. The version row is clickable so
    // the user can re-open the full changelog without waiting for the
    // next bump.
    {
        const versionEl = document.getElementById('diag-playground-version');
        if (versionEl) {
            versionEl.textContent = PLAYGROUND_VERSION;
            versionEl.addEventListener('click', () => showFullChangelog());
        }
        maybeShowChangelogPopup();
    }

    // Populate the Diagnostics panel version rows from the worker runtime.
    void runner.getVersionInfo().then((info) => {
        if (!info) return;
        const el = (id: string) => document.getElementById(id);
        const short = (v: string) => v.split('+')[0]; // strip git hash suffix
        const wFade = el('diag-w-fade');
        const wDot  = el('diag-w-dotnet');
        if (wFade) wFade.textContent = short(info.fadeBasic);
        if (wDot)  wDot.textContent  = info.dotnet;
    }).catch(() => { /* diagnostics are best-effort */ });

    // MonoGame runtime versions — polled until the iframe is ready. The
    // iframe boots lazily on first run, so we keep polling without
    // forcing a boot ourselves.
    {
        let mgVersionFetched = false;
        const mgPollHandle = setInterval(async () => {
            if (mgVersionFetched || !monoGameHost.isReady()) return;
            mgVersionFetched = true;
            clearInterval(mgPollHandle);
            try {
                const info = await monoGameHost.getVersionInfo();
                if (!info) return;
                const el = (id: string) => document.getElementById(id);
                const short = (v: string) => v.split('+')[0];
                const mgFade = el('diag-mg-fade');
                const mgKni  = el('diag-mg-kni');
                const mgDot  = el('diag-mg-dotnet');
                if (mgFade) mgFade.textContent = short(info.fadeBasic);
                if (mgKni)  mgKni.textContent  = info.kni;
                if (mgDot)  mgDot.textContent  = info.dotnet;
            } catch { /* diagnostics are best-effort */ }
        }, 1000);
    }

    // Test probe / public API surface.
    (window as any).__fadeHelp = {
        openCommand: (name: string) => openHelpForCommand(name),
        getController: () => helpCtl,
    };

    // ── View menu ─────────────────────────────────────────────────────────────
    // All static panels the user can open/close via the View menu.
    const VIEW_PANELS: Array<{ id: string; label: string }> = [
        { id: 'editor',        label: 'Editor' },
        { id: 'workspace',     label: 'Workspace' },
        { id: 'search',        label: 'Search' },
        { id: 'settings',      label: 'Settings' },
        { id: 'collaboration', label: 'Collaboration' },
        { id: 'logs',          label: 'Logs' },
        { id: 'history',       label: 'History' },
        { id: 'live-session',  label: 'Live Session' },
        { id: 'debug',         label: 'Debug' },
        { id: 'output',        label: 'Output' },
        { id: 'problems',      label: 'Problems' },
        { id: 'tests',         label: 'Tests' },
        { id: 'debug-console', label: 'Debug Console' },
        { id: 'game',          label: 'Game' },
        { id: 'help',          label: 'Help' },
        { id: 'diagnostics',   label: 'Diagnostics' },
        { id: 'ai-chat',       label: 'AI Chat' },
        { id: 'ai-models',     label: 'AI Models' },
        { id: 'catalog',       label: 'Catalog' },
    ];
    const SAVED_LAYOUTS_KEY = 'fade.dockview.savedLayouts';

    function loadSavedLayouts(): Array<{ name: string; layout: object }> {
        try {
            const raw = localStorage.getItem(SAVED_LAYOUTS_KEY);
            if (raw) return JSON.parse(raw) as Array<{ name: string; layout: object }>;
        } catch { /* ignore */ }
        return [];
    }

    function saveSavedLayouts(layouts: Array<{ name: string; layout: object }>) {
        try { localStorage.setItem(SAVED_LAYOUTS_KEY, JSON.stringify(layouts)); } catch { /* ignore */ }
    }

    // ─── Semantic layouts ────────────────────────────────────────────────
    // Named layouts associated with a mode (Debug, Test). Built-in defaults
    // ensure the relevant panels exist and are focused. Users can override
    // a slot by saving the current dock state to it via the View menu;
    // overrides live in localStorage and persist across reloads.
    type SemanticLayoutId = 'debug' | 'test';
    interface SemanticLayoutDef {
        id: SemanticLayoutId;
        label: string;
        icon: string;       // codicon class (e.g. 'codicon-debug-alt')
        focus: string[];    // panel ids to activate in their groups
    }
    const SEMANTIC_LAYOUTS: SemanticLayoutDef[] = [
        { id: 'debug', label: 'Debug Mode', icon: 'codicon-debug-alt',
          focus: ['debug', 'game', 'debug-console'] },
        { id: 'test',  label: 'Test Mode',  icon: 'codicon-beaker',
          focus: ['tests', 'game'] },
    ];
    const SEMANTIC_LAYOUTS_KEY = 'fade.dockview.semanticLayouts';

    function loadSemanticLayouts(): Partial<Record<SemanticLayoutId, object>> {
        try {
            const raw = localStorage.getItem(SEMANTIC_LAYOUTS_KEY);
            if (raw) return JSON.parse(raw) as Partial<Record<SemanticLayoutId, object>>;
        } catch { /* ignore */ }
        return {};
    }

    function saveSemanticLayouts(layouts: Partial<Record<SemanticLayoutId, object>>) {
        try { localStorage.setItem(SEMANTIC_LAYOUTS_KEY, JSON.stringify(layouts)); } catch { /* ignore */ }
    }

    // Focus the panels associated with a semantic layout. Re-adds missing
    // panels via openPanelById so e.g. Debug Mode still works after the
    // user closed the Debug tab.
    function focusSemanticPanels(id: SemanticLayoutId) {
        const def = SEMANTIC_LAYOUTS.find((s) => s.id === id);
        if (!def) return;
        for (const panelId of def.focus) {
            let p = dockApi.getPanel(panelId);
            if (!p) {
                openPanelById(panelId);
                p = dockApi.getPanel(panelId);
            }
            try { p?.api?.setActive(); } catch { /* ignore */ }
        }
    }

    function applySemanticLayout(id: SemanticLayoutId) {
        // Debug Mode auto-restore: stash the pre-apply layout so we can
        // snap back when the session ends — unless the user has made
        // structural view changes (opened tabs, redocked, etc) in the
        // meantime. The post-apply fingerprint below is the comparator;
        // if it differs at end-of-session, the user has touched things
        // and we leave their layout alone.
        if (id === 'debug') {
            try { preDebugLayoutSnapshot = dockApi.toJSON() as object; }
            catch { preDebugLayoutSnapshot = null; }
        }
        const stored = loadSemanticLayouts();
        const saved = stored[id];
        let applied = false;
        if (saved) {
            try {
                dockApi.fromJSON(saved as any);
                healLayout(dockApi);
                focusSemanticPanels(id);
                applied = true;
            } catch (e) {
                console.warn(`[fade] failed to restore semantic layout ${id} — falling back to focus-only`, e);
            }
        }
        if (!applied) {
            // No saved override → don't reshape the grid, just activate
            // the relevant tabs in their groups.
            focusSemanticPanels(id);
        }
        if (id === 'debug') {
            try { postDebugLayoutFingerprint = JSON.stringify(dockApi.toJSON()); }
            catch { postDebugLayoutFingerprint = null; }
        }
    }

    // Debug-session view stash. Captured on entry to Debug Mode; consulted
    // on exit. We restore the pre-debug layout only when the current dock
    // fingerprint matches what we wrote on apply — that's our proxy for
    // "the user didn't open tabs or redock during the session." If they
    // did anything, the fingerprint diverges and we leave their layout
    // alone so we don't undo their work.
    let preDebugLayoutSnapshot: object | null = null;
    let postDebugLayoutFingerprint: string | null = null;

    function restorePreDebugLayoutIfUnchanged() {
        const pre = preDebugLayoutSnapshot;
        const post = postDebugLayoutFingerprint;
        preDebugLayoutSnapshot = null;
        postDebugLayoutFingerprint = null;
        if (!pre || !post) return;
        let currentJson: string;
        try { currentJson = JSON.stringify(dockApi.toJSON()); }
        catch { return; }
        if (currentJson !== post) return; // user changed something — keep their layout
        try { dockApi.fromJSON(pre as any); healLayout(dockApi); }
        catch (e) { console.warn('[fade] failed to restore pre-debug layout', e); }
    }

    // Open a named panel that is currently absent from the dock.
    // Core panels use healLayout (which knows their default positions).
    // Panels that are hidden by default (e.g. diagnostics) are added
    // directly into a sensible group rather than through healLayout,
    // so they don't get injected into every restored layout.
    function openPanelById(id: string) {
        const RENDER_ALWAYS = 'always' as const;
        if (id === 'ai-chat' || id === 'ai-models') {
            const ref = dockApi.getPanel('game')?.id
                     ?? dockApi.getPanel('editor')?.id;
            try {
                dockApi.addPanel({
                    id,
                    component: id,
                    title: id === 'ai-chat' ? 'AI Chat' : 'AI Models',
                    position: ref ? { referencePanel: ref, direction: 'within' } : undefined,
                    renderer: 'always' as const,
                });
                dockApi.getPanel(id)?.api?.setActive();
            } catch (e) { console.warn(`[fade] failed to open ${id} panel`, e); }
        } else if (id === 'diagnostics') {
            const ref = dockApi.getPanel('output')?.id
                     ?? dockApi.getPanel('problems')?.id
                     ?? dockApi.getPanel('editor')?.id;
            try {
                dockApi.addPanel({
                    id: 'diagnostics',
                    component: 'diagnostics',
                    title: 'Diagnostics',
                    position: ref ? { referencePanel: ref, direction: 'within' } : undefined,
                    renderer: RENDER_ALWAYS,
                });
                dockApi.getPanel('diagnostics')?.api?.setActive();
            } catch (e) { console.warn('[fade] failed to open diagnostics panel', e); }
        } else if (id === 'settings') {
            // Settings opens into the editor tab group — it's a full-width
            // page (form + JSON view), not a sidebar. Falls back to the
            // workspace group only if the editor isn't around.
            const ref = dockApi.getPanel('editor')?.id ?? dockApi.getPanel('workspace')?.id;
            try {
                dockApi.addPanel({
                    id: 'settings',
                    component: 'settings',
                    title: 'Settings',
                    position: ref ? { referencePanel: ref, direction: 'within' } : undefined,
                    renderer: RENDER_ALWAYS,
                });
                dockApi.getPanel('settings')?.api?.setActive();
                settingsPanelHandle?.focus();
            } catch (e) { console.warn('[fade] failed to open settings panel', e); }
        } else if (id === 'search') {
            // Search slots into the left column (Workspace tab group) so the
            // user keeps a clear view of code in the main editor area while
            // browsing results. Fall back to the editor group only if the
            // workspace panel got closed.
            const ref = dockApi.getPanel('workspace')?.id ?? dockApi.getPanel('editor')?.id;
            try {
                dockApi.addPanel({
                    id: 'search',
                    component: 'search',
                    title: 'Search',
                    position: ref ? { referencePanel: ref, direction: 'within' } : undefined,
                    renderer: RENDER_ALWAYS,
                });
                dockApi.getPanel('search')?.api?.setActive();
                searchPanelHandle?.focus();
            } catch (e) { console.warn('[fade] failed to open search panel', e); }
        } else if (id === 'catalog') {
            // Catalog is a singleton tab. openCatalogPanel handles the
            // "already-open → just activate" path; View menu just re-uses it.
            openCatalogPanel();
        } else if (id === 'collaboration' || id === 'history' || id === 'logs' || id === 'live-session') {
            // These panels aren't part of the default tab strip — opening
            // them via the View menu drops them into the editor tab group
            // so they share screen space with code rather than crowding
            // the bottom panel or splitting the workspace column.
            const ref = dockApi.getPanel('editor')?.id;
            const titleFor = (k: string) => k === 'collaboration' ? 'Collaboration'
                : k === 'history' ? 'History'
                : k === 'live-session' ? 'Live Session' : 'Logs';
            try {
                dockApi.addPanel({
                    id,
                    component: id,
                    title: titleFor(id),
                    position: ref ? { referencePanel: ref, direction: 'within' } : undefined,
                    renderer: RENDER_ALWAYS,
                });
                dockApi.getPanel(id)?.api?.setActive();
            } catch (e) { console.warn(`[fade] failed to open ${id} panel`, e); }
        } else {
            // For standard panels, healLayout handles re-adding with the
            // right default position.
            healLayout(dockApi);
        }
    }

    function renderViewMenu() {
        // ── Panels list ───────────────────────────────────────────────────
        viewMenuPanels.innerHTML = '';
        const openIds = new Set(dockApi.panels.map((p) => p.id));
        for (const { id, label } of VIEW_PANELS) {
            const isOpen = openIds.has(id);
            const btn = document.createElement('button');
            btn.className = 'view-menu-item';
            btn.innerHTML = `<span class="check-col">${isOpen ? '✓' : ''}</span><span class="item-label">${label}</span>`;
            btn.addEventListener('click', () => {
                closeViewMenu();
                if (isOpen) {
                    // Panel is open — activate it.
                    try { dockApi.getPanel(id)?.api?.setActive(); } catch { /* ignore */ }
                } else {
                    openPanelById(id);
                }
            });
            viewMenuPanels.appendChild(btn);
        }

        // ── Semantic (mode) layouts ───────────────────────────────────────
        viewSemanticLayouts.innerHTML = '';
        const semanticOverrides = loadSemanticLayouts();
        for (const def of SEMANTIC_LAYOUTS) {
            const isCustomized = semanticOverrides[def.id] != null;
            const row = document.createElement('div');
            row.className = 'view-saved-layout-row';

            const nameBtn = document.createElement('button');
            nameBtn.className = 'layout-name-btn';
            const badgeTitle = isCustomized
                ? 'Customized — built-in default overridden'
                : 'Semantic layout (built-in default)';
            nameBtn.title = `Apply ${def.label}`;
            nameBtn.innerHTML =
                `<span class="codicon ${def.icon}"></span>` +
                `<span>${def.label}</span>` +
                `<span class="semantic-badge${isCustomized ? ' custom' : ''}" title="${badgeTitle}">●</span>`;
            nameBtn.addEventListener('click', () => {
                closeViewMenu();
                applySemanticLayout(def.id);
            });

            const saveBtn = document.createElement('button');
            saveBtn.className = 'layout-save-btn';
            saveBtn.title = `Save current layout as ${def.label}`;
            saveBtn.innerHTML = '<span class="codicon codicon-save"></span>';
            saveBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                const overrides = loadSemanticLayouts();
                overrides[def.id] = dockApi.toJSON() as object;
                saveSemanticLayouts(overrides);
                renderViewMenu();
            });

            row.appendChild(nameBtn);
            row.appendChild(saveBtn);
            if (isCustomized) {
                const resetBtn = document.createElement('button');
                resetBtn.className = 'layout-del-btn';
                resetBtn.title = `Restore built-in ${def.label}`;
                resetBtn.textContent = '↺';
                resetBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const overrides = loadSemanticLayouts();
                    delete overrides[def.id];
                    saveSemanticLayouts(overrides);
                    renderViewMenu();
                });
                row.appendChild(resetBtn);
            }
            viewSemanticLayouts.appendChild(row);
        }

        // ── Saved layouts ─────────────────────────────────────────────────
        viewSavedLayouts.innerHTML = '';
        const saved = loadSavedLayouts();
        for (let i = 0; i < saved.length; i++) {
            const { name, layout } = saved[i];
            const row = document.createElement('div');
            row.className = 'view-saved-layout-row';

            const nameBtn = document.createElement('button');
            nameBtn.className = 'layout-name-btn';
            nameBtn.innerHTML = `<span class="codicon codicon-versions"></span><span>${name}</span>`;
            nameBtn.addEventListener('click', () => {
                closeViewMenu();
                try {
                    dockApi.fromJSON(layout as any);
                } catch (e) {
                    console.warn('[fade] failed to restore layout', e);
                    healLayout(dockApi);
                }
            });

            const delBtn = document.createElement('button');
            delBtn.className = 'layout-del-btn';
            delBtn.title = `Delete "${name}"`;
            delBtn.textContent = '✕';
            delBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                const layouts = loadSavedLayouts();
                layouts.splice(i, 1);
                saveSavedLayouts(layouts);
                renderViewMenu();
            });

            row.appendChild(nameBtn);
            row.appendChild(delBtn);
            viewSavedLayouts.appendChild(row);
        }
    }

    function openViewMenu() {
        renderViewMenu();
        viewMenu.removeAttribute('hidden');
        // Close on outside click.
        setTimeout(() => document.addEventListener('click', onOutsideClick), 0);
    }

    function closeViewMenu() {
        viewMenu.setAttribute('hidden', '');
        document.removeEventListener('click', onOutsideClick);
    }

    function onOutsideClick(e: MouseEvent) {
        if (!viewMenu.contains(e.target as Node) && e.target !== viewMenuBtn) {
            closeViewMenu();
        }
    }

    viewMenuBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        if (viewMenu.hasAttribute('hidden')) openViewMenu();
        else closeViewMenu();
    });

    viewSaveLayoutBtn.addEventListener('click', () => {
        closeViewMenu();
        const name = prompt('Layout name:');
        if (!name?.trim()) return;
        const layouts = loadSavedLayouts();
        // Replace existing layout with the same name if present.
        const idx = layouts.findIndex((l) => l.name === name.trim());
        const entry = { name: name.trim(), layout: dockApi.toJSON() as object };
        if (idx >= 0) layouts[idx] = entry;
        else layouts.push(entry);
        saveSavedLayouts(layouts);
    });

    viewResetLayoutBtn.addEventListener('click', () => {
        closeViewMenu();
        if (!confirm('Reset all panels to the default layout?')) return;
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

    // Targeted asset-cache wipe — deletes the active project's
    // `.fade-cache/` (compiled XNB blobs + index.json) without touching
    // user source files or other projects. ENCODER_VERSION bumps already
    // invalidate the cache automatically; this is the escape hatch for
    // cases where the browser is serving a stale JS bundle and the
    // version bump hasn't actually taken effect, or for manually testing
    // a clean encode. Call from devtools: `forceAssetCacheClear()`.
    (window as any).forceAssetCacheClear = async () => {
        try {
            await workspace.delete('.fade-cache');
            console.warn('[fade] asset cache cleared for project', workspace.currentProject());
        } catch (e: any) {
            // Most likely "directory not found" — cache wasn't populated
            // yet. Either way the cache is now in the state the caller
            // wanted, so log + move on.
            console.warn('[fade] asset cache wipe:', e?.message ?? e);
        }
        location.reload();
    };

    // ─── Project viewer overlay ──────────────────────────────────────────
    // Modal dialog that lists OPFS projects and hosts the "new workspace"
    // form (name + web/monogame type picker). Switching reloads the page
    // — simplest way to ensure all dock panels, Monaco models, and the
    // polling loop pick up the new project cleanly (the dockview layout
    // is global and persists across switches).
    //
    // First-run mode (.first-run class) hides the existing-project list
    // and the × button so a brand-new user can't bypass the type pick.
    const projectOverlay = document.getElementById('project-overlay')!;
    const projectListEl = document.getElementById('project-list')!;
    const projectOverlayCloseBtn = document.getElementById('project-overlay-close')!;
    const projectModalTitleEl = document.getElementById('project-modal-title')!;
    const projectNewHeadingEl = document.getElementById('project-new-heading')!;
    const projectNewInput = document.getElementById('project-new-input') as HTMLInputElement;
    const projectNewError = document.getElementById('project-new-error')!;
    const projectNewCreateBtn = document.getElementById('project-new-create') as HTMLButtonElement;
    const projectTypeCards = Array.from(
        document.querySelectorAll<HTMLButtonElement>('.project-type-card'),
    );
    const openProjectsBtn = document.getElementById('open-projects')!;

    let selectedProjectType: FadeProjectType | null = null;
    let firstRunMode = false;

    function setSelectedProjectType(type: FadeProjectType | null) {
        selectedProjectType = type;
        for (const card of projectTypeCards) {
            const match = card.dataset.type === type;
            card.classList.toggle('selected', match);
            card.setAttribute('aria-checked', match ? 'true' : 'false');
        }
        updateCreateButtonState();
    }

    function updateCreateButtonState() {
        const name = projectNewInput.value.trim();
        projectNewCreateBtn.disabled =
            name.length === 0 || selectedProjectType === null;
    }

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

    async function createNewProject(rawName: string, type: FadeProjectType | null) {
        const name = rawName.trim();
        if (!name) return;
        if (!type) {
            showProjectError('Pick a workspace type before creating.');
            return;
        }
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
            await workspace.createProject(name, type);
        } catch (e: any) {
            showProjectError('Create failed: ' + (e?.message ?? e));
            return;
        }
        clearProjectError();
        await switchToProject(name);
    }

    function openProjectOverlay(opts: { firstRun?: boolean } = {}) {
        firstRunMode = !!opts.firstRun;
        projectOverlay.classList.toggle('first-run', firstRunMode);
        projectModalTitleEl.textContent = firstRunMode ? 'Welcome' : 'Projects';
        projectNewHeadingEl.textContent = firstRunMode
            ? 'Create your first workspace'
            : 'New workspace';
        clearProjectError();
        projectNewInput.value = '';
        setSelectedProjectType(null);
        projectOverlay.hidden = false;
        if (!firstRunMode) {
            renderProjectList().catch((e) =>
                console.error('[fade] project list render failed', e),
            );
        } else {
            projectListEl.innerHTML = '';
        }
        // Focus the new-project input on a microtask so screen readers + the
        // browser's focus ring land correctly after the show animation.
        setTimeout(() => projectNewInput.focus(), 0);
    }
    function closeProjectOverlay() {
        // First-run mode is required: the editor below is unmounted/blank
        // until a project exists, so we refuse to close.
        if (firstRunMode) return;
        projectOverlay.hidden = true;
    }

    for (const card of projectTypeCards) {
        card.addEventListener('click', () => {
            const t = card.dataset.type as FadeProjectType | undefined;
            if (t === 'web' || t === 'monogame') setSelectedProjectType(t);
        });
    }
    projectNewCreateBtn.addEventListener('click', () => {
        createNewProject(projectNewInput.value, selectedProjectType);
    });

    openProjectsBtn.addEventListener('click', () => openProjectOverlay());
    projectNameEl.addEventListener('click', () => openProjectOverlay());
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
        } else if ((e.metaKey || e.ctrlKey) && e.shiftKey && e.key.toLowerCase() === 'f') {
            // ⌘⇧F / Ctrl+⇧F opens the workspace Search panel. Browsers
            // leave this combo unbound (Cmd+F is in-page find, not this),
            // so we get the VSCode-equivalent shortcut for free.
            e.preventDefault();
            const existing = dockApi.getPanel('search');
            if (existing) {
                existing.api?.setActive();
                searchPanelHandle?.focus();
            } else {
                openPanelById('search');
                // openPanelById already calls focus(), but only synchronously
                // after addPanel — re-focus after a tick so the input wins
                // out over any focus reshuffling dockview does on attach.
                setTimeout(() => searchPanelHandle?.focus(), 0);
            }
        } else if ((e.metaKey || e.ctrlKey) && e.key === ',' && !e.shiftKey) {
            // ⌘, / Ctrl+, opens the Settings panel — matches VSCode's
            // shortcut. Browsers don't bind this combo.
            e.preventDefault();
            const existing = dockApi.getPanel('settings');
            if (existing) {
                existing.api?.setActive();
                settingsPanelHandle?.focus();
            } else {
                openPanelById('settings');
                setTimeout(() => settingsPanelHandle?.focus(), 0);
            }
        }
    });
    projectNewInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
            e.preventDefault();
            // Enter is a shortcut for clicking Create — same gating, so
            // it's a no-op until a type has been picked.
            if (!projectNewCreateBtn.disabled) {
                createNewProject(projectNewInput.value, selectedProjectType);
            }
        }
        if (e.key === 'Escape') {
            e.stopPropagation();
            closeProjectOverlay();
        }
    });
    projectNewInput.addEventListener('input', () => {
        clearProjectError();
        updateCreateButtonState();
    });

    pgSplash?.setStatus('Mounting editor…');
    editor = monaco.editor.create(editorContainer, {
        value: '',
        language: 'fade',
        theme: 'fade-dark',
        automaticLayout: true,
        hover: { enabled: 'on', delay: 200, sticky: true },
        'semanticHighlighting.enabled': true,
        // Reparent overflow widgets (suggest popup, hover, signature help)
        // to document.body. Doesn't cover the right-click context menu —
        // that one ships via the IContextViewService and uses its own
        // container; see the reparent-on-mutation observer below.
        fixedOverflowWidgets: true,
        // The rest (font, minimap, tabs, wrap, …) come from settings —
        // applySettingsToEditor below seeds them and re-applies on change.
        ...editorOptionsFromSettings(currentSettings()),
    } as monaco.editor.IStandaloneEditorConstructionOptions);

    // Reactively re-apply editor settings whenever they change (user toggles
    // a value, or the workspace switches projects).
    onSettingsChange((state) => {
        applyTheme(state);
        if (!editor) return;
        editor.updateOptions(editorOptionsFromSettings(state) as monaco.editor.IEditorOptions);
        // tabSize / insertSpaces are model options, not editor options —
        // apply to every open model so toggling propagates to all tabs.
        const eff = state.effective;
        const tabSize = Number(eff['editor.tabSize'] ?? 2);
        const insertSpaces = Boolean(eff['editor.insertSpaces'] ?? true);
        for (const m of monaco.editor.getModels()) {
            m.updateOptions({ tabSize, insertSpaces });
        }
    });
    // Follow OS dark/light flips when ui.theme is 'auto'. The listener pulls
    // the current setting each time so toggling away from 'auto' just stops
    // doing anything.
    try {
        const mq = window.matchMedia('(prefers-color-scheme: light)');
        mq.addEventListener('change', () => applyTheme(currentSettings()));
    } catch { /* matchMedia missing on very old browsers; harmless */ }

    // ─── Game panel toolbar ──────────────────────────────────────────────
    // The in-panel toolbar (status dot + text, mute, fullscreen) drives all
    // game-state communication to the user. Status lives here rather than in
    // the dockview tab title so it survives tab switches and has room for
    // the control buttons alongside it.

    type GameStatus = 'idle' | 'booting' | 'running' | 'paused' | 'stopped';
    const mgStatusDot  = document.getElementById('mg-status-dot')  as HTMLElement;
    const mgGameStatus = document.getElementById('mg-game-status') as HTMLElement;
    const STATUS_LABELS: Record<GameStatus, string> = {
        idle:    'Not started',
        booting: 'Booting…',
        running: 'Running',
        paused:  'Paused',
        stopped: 'Stopped',
    };
    function updateGameStatus(state: GameStatus) {
        if (mgStatusDot)  mgStatusDot.dataset.state  = (state === 'idle' || state === 'stopped') ? '' : state;
        if (mgGameStatus) mgGameStatus.textContent    = STATUS_LABELS[state];
    }

    // Mute
    let mgMuted = false;
    const mgMuteBtn  = document.getElementById('mg-mute-btn')  as HTMLButtonElement | null;
    const mgMuteIcon = document.getElementById('mg-mute-icon') as HTMLElement | null;
    mgMuteBtn?.addEventListener('click', () => {
        mgMuted = !mgMuted;
        monoGameHost.setMuted(mgMuted);
        mgMuteIcon?.classList.toggle('codicon-mute',   mgMuted);
        mgMuteIcon?.classList.toggle('codicon-unmute', !mgMuted);
        if (mgMuteBtn) {
            mgMuteBtn.classList.toggle('is-active', mgMuted);
            mgMuteBtn.title = mgMuted ? 'Unmute' : 'Mute / Unmute';
        }
    });

    // Fullscreen
    const mgFullscreenBtn  = document.getElementById('mg-fullscreen-btn')  as HTMLButtonElement | null;
    const mgFullscreenIcon = document.getElementById('mg-fullscreen-icon') as HTMLElement | null;
    mgFullscreenBtn?.addEventListener('click', () => {
        const container = document.getElementById('mg-blazor-root');
        if (!container) return;
        if (!document.fullscreenElement) {
            container.requestFullscreen?.().catch(() => {/* denied */});
        } else {
            document.exitFullscreen?.();
        }
    });
    document.addEventListener('fullscreenchange', () => {
        const inFs = !!document.fullscreenElement;
        mgFullscreenIcon?.classList.toggle('codicon-screen-full',   !inFs);
        mgFullscreenIcon?.classList.toggle('codicon-screen-normal',  inFs);
        if (mgFullscreenBtn) mgFullscreenBtn.title = inFs ? 'Exit fullscreen' : 'Toggle fullscreen';
    });

    // Pause the MonoGame rAF loop while the editor has focus so MonoGame's
    // TickDotNet() stops competing with Monaco's own rAF work (cursor blink,
    // decorations, hover). The in-panel status label shows "Paused". Resume
    // fires on blur (when focus moves back to the game canvas or anywhere else).
    let mgTickPaused = false;
    function pauseMgTick() {
        if (currentProject?.type !== 'monogame') return;
        if (!runActive && !debugSessionActive) return;
        if (mgTickPaused) return;
        // Live-session guard: halting the iframe also halts the game-
        // frame stream and the Debug UI envelope relay that observers
        // depend on. If anyone else is in the room, keep ticking even
        // when the editor has focus — Monaco vs KNI rAF contention is a
        // smaller cost than freezing observers' view of the session.
        const session = liveSessionHandle?.getSession();
        if (session) {
            const hasOtherPeers = session.getState().peers.some((p) => !p.isSelf);
            if (hasOtherPeers) return;
        }
        mgTickPaused = true;
        monoGameHost.pauseTick();
        updateGameStatus('paused');
    }
    function resumeMgTick() {
        if (!mgTickPaused) return;
        mgTickPaused = false;
        monoGameHost.resumeTick();
        updateGameStatus('running');
    }
    editor.onDidFocusEditorWidget(pauseMgTick);
    editor.onDidBlurEditorWidget(resumeMgTick);

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
    //
    // Multi-source projects push ONE joined document under PROJECT_LSP_URI
    // (rebuilt via rebuildAndPushProjectDoc) instead of one document per
    // file. Files that aren't listed in fade.json:sources still get their
    // standalone per-file push so orphan .fbasic files keep getting
    // diagnostics — same behavior as before this branch.
    const lastPushedByUri = new Map<string, string>();
    setInterval(() => {
        let anyInProjectChanged = false;
        let anyOrphanChanged = false;
        let manifestChanged = false;
        for (const m of monaco.editor.getModels()) {
            const lang = m.getLanguageId();
            const uri = m.uri.toString();
            const value = m.getValue();
            if (lastPushedByUri.get(uri) === value) continue;
            lastPushedByUri.set(uri, value);
            if (lang === 'fade') {
                if (projectFileNameFromUri(uri)) {
                    anyInProjectChanged = true;
                } else {
                    lsp.setDocument(uri, value);
                    anyOrphanChanged = true;
                }
            } else if (uri.endsWith('/' + FADE_JSON_NAME)) {
                manifestChanged = true;
            }
        }
        if (anyInProjectChanged) {
            // Rebuild the joined doc from the current set of in-project
            // model contents and push once. Cheap to call; idempotent when
            // joined text matches what we last pushed.
            rebuildAndPushProjectDoc();
        }
        // Re-discover tests in the background whenever the active file moves.
        if (anyInProjectChanged || anyOrphanChanged) refreshDebounce();
        if (manifestChanged) {
            // fade.json edit landed — re-validate + refresh derived state.
            // refreshFadeProject calls rebuildAndPushProjectDoc itself.
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
    // Stamp every model as "already seen" so the polling interval's first
    // tick doesn't push documents to the LSP before command DLLs are
    // registered. The DLL-registration branch inside refreshFadeProject is
    // always the authoritative first push (it runs after DLLs are loaded).
    for (const m of monaco.editor.getModels()) {
        lastPushedByUri.set(m.uri.toString(), m.getValue());
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

    pgSplash?.hide();
    monoGameHost.notifyPgSplashHidden();
    // Enable Run / Debug / Export through refreshRunButtons so a project
    // with diagnostics already in (e.g. fade.json errors loaded at boot)
    // boots with the buttons in the correct disabled state.
    refreshRunButtons();

    // Walk the active project's OPFS folder for assets and push them
    // into the MonoGame runtime's BrowserContentManager. Called before
    // each loadProgram/debugStart so any `texture`/`load sfx clip`/`font`
    // commands fbasic runs can resolve via stock Content.Load<T>(name).
    //
    // Two source kinds feed in:
    //   • Pre-built `.xnb` files (user uploads, or the dev's old assets) —
    //     read raw + routed through patchXnbForKni so SoundEffects get a
    //     loopLength fix for KNI Blazor, and Effects get their MGFX
    //     version downgraded from v11 to v10 (see xnb-previews.ts).
    //   • Image source files (.png / .jpg / .gif / .webp / .bmp) — passed
    //     through compileImageAssets, which decodes them in the browser
    //     and serialises a Texture2D XNB into an OPFS-backed cache keyed
    //     by content hash + format. Lets users drop a PNG into the file
    //     tree and reference it from `texture 1, "MyPic"` without having
    //     to run a desktop content build first.
    //
    // Asset name = relative path minus extension. So `images/Catfish.png`
    // registers as "images/Catfish" — matching what fbasic code passes
    // to `texture 1, ...`. clearAssets() pre-empties the runtime dict so
    // deletions in OPFS take effect on the next Run.
    // Diff-based sync state. Persists across Runs within a single page
    // load: the iframe's window.fadeAudio keeps decoded AudioBuffers,
    // BrowserContentManager keeps cached Texture2D/SpriteFonts, and
    // this map tells us what's already there. A Run that doesn't change
    // any asset bytes touches nothing on the iframe side — no decode,
    // no postMessage, no GPU upload. Wiped if the user calls
    // forceAssetCacheClear() or switches projects (page reload).
    type SyncedAssetKind = 'image' | 'audio' | 'font' | 'shader' | 'xnb';
    interface SyncedAssetState { kind: SyncedAssetKind; hash: string; }
    const lastSyncedAssets = new Map<string, SyncedAssetState>();

    async function syncAssetsToRuntime(
        plan: MonoGameContentPlan = EMPTY_CONTENT_PLAN,
    ): Promise<void> {
        const allNames = await workspace.list();
        // Exclude the asset cache from registration — its blobs are XNB
        // files keyed by hash, not user-addressable assets. The cache is
        // managed by the compile passes + the shared GC below.
        const names = allNames.filter((n) => !n.startsWith('.fade-cache/'));

        // Phase 1a: compile image sources via the asset cache, then push.
        // We swallow errors at the per-asset level so one bad PNG doesn't
        // take down the rest of the project's assets. The plan tells us
        // per-asset compression overrides from the macro pass; an empty
        // plan (the default) means "use default compression for every
        // source we find" — useful for test/debug paths where the macro
        // pass hasn't run yet.
        const imageSources = names.filter(isImageSourcePath);
        const audioSources = names.filter(isAudioSourcePath);

        // All per-asset compilation diagnostics flow through the Logs
        // panel ('asset' channel). The Output panel only sees errors —
        // surface those there too so a broken upload is visible even if
        // the user doesn't have the Logs tab open. The Logs panel is the
        // canonical place to inspect every cache hit / encode pick / etc.
        const assetLog = getLogger('asset');
        const reportDiagnostic = (d: { severity: 'info' | 'warn' | 'error'; assetName?: string; sourcePath?: string; message: string }) => {
            const label = d.assetName ?? d.sourcePath ?? '<asset>';
            const line = `${label}: ${d.message}`;
            if (d.severity === 'error') {
                assetLog.error(line);
                appendOutputLine(`[asset ${label}] ${d.message}`, 'error');
            } else if (d.severity === 'warn') {
                assetLog.warn(line);
            } else {
                assetLog.info(line);
            }
        };

        const fontSources = names.filter(isFontSourcePath);
        const shaderSources = names.filter(isShaderSourcePath);
        const xnbSources = names.filter(
            (n) => /\.xnb$/i.test(n) && !n.startsWith('.fade-cache/'),
        );

        // Build the target asset set first — collect bytes + a content
        // hash for every asset that should be loaded after this sync.
        // Then diff against lastSyncedAssets and only push the changes.
        // This makes repeat Runs essentially free for unchanged audio
        // (no `decodeAudioData`) and textures (no GPU re-upload).
        interface PendingAsset {
            name: string;
            kind: SyncedAssetKind;
            hash: string;
            bytes: Uint8Array;
        }
        const target = new Map<string, PendingAsset>();

        // Phase 1a: images — compile via the OPFS-backed cache then
        // hash the resulting XNB bytes. Cache hits avoid the
        // decode+encode entirely; the hash gate then skips re-sending
        // bytes to the iframe when the XNB is identical to last Run.
        if (imageSources.length > 0) {
            try {
                const { assets, diagnostics } =
                    await compileImageAssetsWithPlan(workspace, imageSources, plan);
                for (const d of diagnostics) reportDiagnostic(d);
                for (const a of assets) {
                    const hash = await sha256Hex(a.bytes);
                    target.set(a.assetName, {
                        name: a.assetName, kind: 'image', hash, bytes: a.bytes,
                    });
                }
            } catch (e) {
                assetLog.error(`image-asset compile pass failed: ${(e as any)?.message ?? e}`);
            }
        }

        // Phase 1b: audio — the iframe's Web Audio host decodes raw
        // source bytes (MP3/OGG/WAV/FLAC/AAC). Hash is computed over
        // the raw source bytes: if they're unchanged from last Run, the
        // diff below skips the `register-audio` postMessage and the
        // previously-decoded AudioBuffer stays cached in JS memory.
        if (audioSources.length > 0) {
            for (const path of audioSources) {
                const assetName = assetNameForSourcePath(path);
                try {
                    const bytes = await workspace.readBytes(path);
                    const hash = await sha256Hex(bytes);
                    target.set(assetName, {
                        name: assetName, kind: 'audio', hash, bytes,
                    });
                } catch (e: any) {
                    assetLog.error(`${assetName}: read failed: ${e?.message ?? e}`);
                }
            }
        }

        // Phase 1c: fonts — TTF/OTF compiled to SpriteFont XNBs via
        // the same OPFS cache as images. Hash is the XNB output.
        if (fontSources.length > 0) {
            try {
                const { assets, diagnostics } =
                    await compileFontAssetsWithPlan(workspace, fontSources, plan);
                for (const d of diagnostics) reportDiagnostic(d);
                for (const a of assets) {
                    const hash = await sha256Hex(a.bytes);
                    target.set(a.assetName, {
                        name: a.assetName, kind: 'font', hash, bytes: a.bytes,
                    });
                }
            } catch (e) {
                assetLog.error(`font compile pass failed: ${(e as any)?.message ?? e}`);
            }
        }

        // Phase 1d: shaders — `.fx` source files compiled to MGFX v10
        // effect XNBs via the FX framing parser + WASM shader compiler.
        // Hash is computed over the XNB output so changing the source
        // (or the compiler version) re-syncs; unchanged shaders skip
        // re-registration entirely.
        //
        // Clear any KNI compile-error markers on .fx files before starting
        // the new sync. If the new compile fails again, the stderr
        // capture below will set fresh markers; if it succeeds we leave
        // the editor clean.
        if (shaderSources.length > 0) clearShaderMarkers();
        if (shaderSources.length > 0) {
            try {
                const { assets, diagnostics } =
                    await compileShaderAssetsWithPlan(workspace, shaderSources, plan);
                for (const d of diagnostics) reportDiagnostic(d);
                for (const a of assets) {
                    const hash = await sha256Hex(a.bytes);
                    // Audit trail in the Logs panel ('asset' channel) —
                    // shader staleness was a real bug we had to chase, so
                    // keep the hash visible. Comparing the xnbHash across
                    // two Runs tells you whether the source edit actually
                    // produced different compiled bytes.
                    assetLog.info(
                        `shader ${a.assetName}: ${a.cached ? 'cache-hit' : 'compiled'} ` +
                        `(${a.bytes.length} B, xnbHash=${hash.slice(0, 8)})`,
                    );
                    target.set(a.assetName, {
                        name: a.assetName, kind: 'shader', hash, bytes: a.bytes,
                    });
                }
            } catch (e) {
                assetLog.error(`shader compile pass failed: ${(e as any)?.message ?? e}`);
            }
        }

        // Phase 1e: pre-built `.xnb` uploads (legacy assets the user
        // brought in directly). Run through the KNI patcher then hash.
        for (const name of xnbSources) {
            try {
                const raw = await workspace.readBytes(name);
                const bytes = patchXnbForKni(raw);
                const assetName = name.replace(/\.xnb$/i, '');
                const hash = await sha256Hex(bytes);
                target.set(assetName, {
                    name: assetName, kind: 'xnb', hash, bytes,
                });
            } catch (e) {
                console.error('[fade] asset push failed for', name, e);
            }
        }

        // ─── Diff vs last Run ────────────────────────────────────────
        // For each tracked asset, decide one of: skip (hash match),
        // re-register (hash changed), unregister (no longer present).
        // Audio routes through unregisterAudio + registerAudio; the
        // other kinds share the unregisterAsset / registerAsset path.
        const toUnregister: SyncedAssetState[] = [];
        const toRegister: PendingAsset[] = [];
        let skipped = 0;

        for (const [name, last] of lastSyncedAssets) {
            const next = target.get(name);
            if (!next) {
                toUnregister.push({ kind: last.kind, hash: last.hash });
                // recorded with name in the iteration above
                (toUnregister[toUnregister.length - 1] as any).name = name;
            } else if (next.hash !== last.hash) {
                toUnregister.push({ kind: last.kind, hash: last.hash });
                (toUnregister[toUnregister.length - 1] as any).name = name;
                toRegister.push(next);
            } else {
                skipped++;
            }
        }
        for (const [name, next] of target) {
            if (!lastSyncedAssets.has(name)) toRegister.push(next);
        }

        for (const old of toUnregister as Array<SyncedAssetState & { name: string }>) {
            if (old.kind === 'audio') await monoGameHost.unregisterAudio(old.name);
            else await monoGameHost.unregisterAsset(old.name);
        }
        for (const a of toRegister) {
            try {
                if (a.kind === 'audio') {
                    const ok = await monoGameHost.registerAudio(a.name, a.bytes);
                    if (!ok) {
                        appendOutputLine(
                            `[asset ${a.name}] audio decode failed — browser couldn't decode ` +
                            `this source (unsupported codec, corrupt file, or Safari + OGG)`,
                            'error',
                        );
                    } else {
                        assetLog.info(
                            `${a.name}: decoded (${(a.bytes.length / 1024).toFixed(1)} KB)`,
                        );
                    }
                } else {
                    await monoGameHost.registerAsset(a.name, a.bytes);
                    assetLog.info(
                        `${a.name}: registered ${a.kind} (${(a.bytes.length / 1024).toFixed(1)} KB) enc=${ENCODER_VERSION}`,
                    );
                }
            } catch (e) {
                assetLog.error(`${a.name}: register failed: ${(e as any)?.message ?? e}`);
            }
        }
        if (skipped > 0) {
            assetLog.info(`${skipped} asset${skipped === 1 ? '' : 's'} unchanged — kept from previous Run`);
        }

        // Commit the new state.
        lastSyncedAssets.clear();
        for (const [name, p] of target) {
            lastSyncedAssets.set(name, { kind: p.kind, hash: p.hash });
        }

        // Shared cache GC — runs once all compile passes have populated
        // liveSourcePaths so neither kind's entries get wrongly evicted.
        const liveSources = new Set<string>([
            ...imageSources, ...audioSources, ...fontSources, ...shaderSources,
        ]);
        try { await garbageCollectAssetCache(workspace, liveSources); }
        catch (e) { console.warn('[fade] asset cache GC failed', e); }
    }

    // Build the list of command-DLL entries for the active project.
    // Mirrors the logic at the LSP-sync site above: web projects auto-
    // include Lib.Web; everything in currentProject.commandDlls layers
    // on top. Used by the preview iframe to pre-load the same surface
    // the vm-worker has registered, so the bytecode's CALL_HOST indices
    // resolve identically.
    const collectCommandDllEntries = (): CommandDllEntry[] => {
        const type = currentProject?.type ?? 'web';
        // Type-defaults must mirror the LSP-sync site above so that
        // command IDs resolve consistently between the LSP's compile
        // pass and any iframe that also re-compiles from source.
        // Monogame's iframe bakes FadeMonoGameCommands in statically
        // (and ignores its bootstrap commandDlls list), so this entry
        // is here purely for symmetry + so user-uploaded plugins layer
        // on top of the same base.
        const defaults: CommandDllEntry[] = type === 'web'
            ? [{ assembly: 'FadeBasic.Lib.Web', class: 'FadeBasic.Lib.Web.WebCommands' }]
            : type === 'monogame'
            ? [{ assembly: 'Fade.MonoGame.Lib', class: 'Fade.MonoGame.Lib.FadeMonoGameCommands' }]
            : [];
        return [...defaults, ...(currentProject?.commandDlls ?? [])];
    };

    // Swap the Game panel sub-surface. The panel hosts two iframes —
    // one for the web template, one for the monogame template — and
    // only one is visible at a time. Splash element inside
    // #mg-blazor-root is shown when monogame surface is active and the
    // iframe hasn't booted (monoGameHost lazily creates the iframe on
    // first ensureBooted).
    const showGameSurface = (which: 'web' | 'monogame' | 'splash'): void => {
        const webHost = document.getElementById('web-preview-host');
        const mgRoot = document.getElementById('mg-blazor-root');
        if (!webHost || !mgRoot) return;
        if (which === 'web') {
            webHost.style.display = 'block';
            mgRoot.style.display = 'none';
        } else {
            webHost.style.display = 'none';
            mgRoot.style.display = 'flex';  // preserve CSS flex-direction: column
        }
    };

    // Boot the preview iframe once per session for web projects, send
    // it the initial command-DLL set, and attach it as the Runner's
    // vm target. Subsequent run / debug / tests all flow through the
    // iframe (which forwards to its own .NET runtime) so the user sees
    // print output, prompts, debug stop events, and test results all
    // happening in the Game panel — same surface as a deployed export.
    //
    // The Playground's existing fade.json sync (registerCommandAssembly
    // /clearCommandAssemblies on commandDlls change) routes through
    // postVm automatically once attached, so future DLL changes
    // reach the iframe with no extra wiring.
    let webPreviewArmed: Promise<void> | null = null;
    const ensureWebPreviewArmed = (): Promise<void> => {
        if (webPreviewArmed) return webPreviewArmed;
        webPreviewArmed = (async () => {
            const frame = document.getElementById('web-preview-frame') as HTMLIFrameElement | null;
            if (!frame) throw new Error('web-preview-frame not in DOM');

            // Phase 1: wait for the iframe's runtime to report ready.
            const readyPromise = new Promise<void>((resolve) => {
                const onReady = (e: MessageEvent) => {
                    if (e.source !== frame.contentWindow) return;
                    if (e.data?.type !== 'preview-ready') return;
                    window.removeEventListener('message', onReady);
                    resolve();
                };
                window.addEventListener('message', onReady);
            });
            frame.src = '/runtime/web/index.html?preview=1';
            await readyPromise;

            // Phase 2: bootstrap with the project's command DLLs. The
            // iframe registers each one, then signals 'preview-armed'.
            // We use the project's current DLL set; future changes flow
            // through the regular registerCommandAssembly sync path.
            const entries = collectCommandDllEntries();
            const commandDlls: { assembly: string; class: string; bytes: ArrayBuffer }[] = [];
            for (const e of entries) {
                try {
                    const resp = await fetch(`/runtime/fade-libs/${e.assembly}.dll`);
                    if (resp.ok) {
                        commandDlls.push({
                            assembly: e.assembly,
                            class: e.class,
                            bytes: await resp.arrayBuffer(),
                        });
                    }
                } catch { /* warn quietly — sync path retries */ }
            }
            const armedPromise = new Promise<void>((resolve) => {
                const onArmed = (e: MessageEvent) => {
                    if (e.source !== frame.contentWindow) return;
                    if (e.data?.type !== 'preview-armed') return;
                    window.removeEventListener('message', onArmed);
                    resolve();
                };
                window.addEventListener('message', onArmed);
            });
            frame.contentWindow!.postMessage(
                { type: 'bootstrap', commandDlls },
                '*',
                commandDlls.map((c) => c.bytes),
            );
            await armedPromise;

            // Phase 3: attach as the VM target so future debug / tests /
            // run / compile-to-bytecode messages flow through the iframe.
            runner.attachVmIframe(frame);
        })();
        return webPreviewArmed;
    };

    const runOnce = async () => {
        // Clear any crash overlay from a previous run before we kick a
        // new one off — the red decoration shouldn't bleed across runs.
        hideCrashOverlay();
        const source = await getProjectSource();
        if (!source) {
            clearOutput();
            appendOutputLine('No file open.', 'dim');
            return;
        }
        // Defensive: refreshRunButtons disables runBtn while errors exist,
        // but a stale click that races the keyboard shortcut could still
        // arrive here. Bail loudly rather than calling LoadProgram with a
        // broken source (which surfaces as Blazor's error overlay).
        if (projectHasCompileErrors()) {
            clearOutput();
            appendOutputLine('Fix compile errors before running. See Problems panel.', 'error');
            revealPanel('problems');
            return;
        }
        runActive = true;
        refreshRunButtons();
        clearOutput();

        // ─── 'monogame' branch ──────────────────────────────────────────
        // Route to the canvas-side runtime (WebRuntime.MonoGame). First
        // call boots ~8 MB of WASM lazily; subsequent calls hot-reload via
        // Game1.LoadProgram. Reveal the Game panel so the canvas is visible.
        if (currentProject?.type === 'monogame') {
            showGameSurface('monogame');
            try {
                revealPanel('game');
                appendOutputLine('Booting MonoGame runtime…', 'dim');
                updateGameStatus('booting');

                // Two-phase compile/run:
                //   1. compileForRun runs the macro pass and stashes a
                //      pending FadeRuntimeContext on the iframe side, but
                //      does NOT start ticking. The returned plan reflects
                //      whatever `# push asset` / `# texture compression`
                //      calls did during the macro VM.
                //   2. syncAssetsToRuntime executes the plan: each
                //      referenced PNG/JPG is decoded + encoded into an
                //      XNB (cache-hits on second run) and registered with
                //      Game1.BrowserContentManager. Pre-built `.xnb`
                //      files in OPFS are also pushed during this step.
                //   3. beginPendingProgram unblocks the stashed context
                //      so the next tick runs the user's program — with
                //      every `texture`/`load sfx clip` call already able
                //      to resolve.
                const compile = await monoGameHost.compileForRun(source);
                if (!compile.ok) {
                    appendOutputLine(
                        compile.error ?? 'Compile failed. See Problems panel.',
                        'error',
                    );
                    revealPanel('problems');
                    runActive = false;
                    updateGameStatus('stopped');
                    refreshRunButtons();
                    refreshStopButton();
                    return;
                }
                await syncAssetsToRuntime(compile.plan);
                const started = await monoGameHost.beginPendingProgram();
                if (started) {
                    // No "Running…" message — user `print` output now
                    // streams into the Output panel directly. The first
                    // few lines (or the game canvas itself) tell them
                    // the program is alive. Game stays running until
                    // stopAll(); runActive remains true so Reset is
                    // available.
                    updateGameStatus('running');
                    refreshStopButton();
                } else {
                    appendOutputLine(
                        'MonoGame: program ready, but the runtime refused to start it.',
                        'error',
                    );
                    runActive = false;
                    updateGameStatus('stopped');
                    refreshRunButtons();
                    refreshStopButton();
                }
            } catch (e: any) {
                appendOutputLine('MonoGame runtime error: ' + (e?.message ?? String(e)), 'error');
                runActive = false;
                updateGameStatus('stopped');
                refreshRunButtons();
                refreshStopButton();
            }
            return;
        }

        // ─── 'web' branch ─────────────────────────────────────────────
        // Route through the preview iframe so the Playground's run
        // experience matches what an exported bundle looks like — and
        // so debug / tests / run all share the same visible surface.
        // After ensureWebPreviewArmed the Runner's VM target IS the
        // iframe; runner.run / runner.runTests / runner.debugStart all
        // flow through it. Output, prompts, and host-messages render
        // inside the iframe (the user sees them in the Game panel).
        try {
            showGameSurface('web');
            revealPanel('game');
            await ensureWebPreviewArmed();
            // Stop is already enabled by runActive (set at runOnce entry).
            refreshStopButton();
            const result = await runner.run(source);
            let env: { ok?: boolean; error?: string | null; compileError?: string | null } | null = null;
            try { env = JSON.parse(result); } catch { env = null; }
            if (env?.compileError) {
                appendOutputLine('Compile failed. See Problems panel.', 'error');
                revealPanel('problems');
            } else if (env?.error === 'stopped') {
                appendOutputLine('Stopped.', 'dim');
            } else if (env?.error) {
                appendOutputLine(env.error, 'error');
            }
        } catch (e: any) {
            appendOutputLine(e?.message ?? String(e), 'error');
        } finally {
            runActive = false;
            refreshRunButtons();
            refreshStopButton();
        }
    };

    // Click handler that respects the Run / Reset duality. When a run
    // is in flight (and no debug session blocks us), tear it down
    // before starting a fresh one — single click instead of Stop-then-
    // Run. ⌘R does the same.
    const runOrReset = async () => {
        if (runActive && !debugSessionActive) {
            try { await stopAll(); } catch { /* best effort */ }
        }
        await runOnce();
    };
    runBtn.addEventListener('click', runOrReset);
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyR, runOrReset);

    // Export: build a static zip containing the runtime + the user's
    // compiled bytecode + the project's command DLLs + a synthesized
    // fade-manifest.json. Drop it on any static host (itch.io, GitHub
    // Pages, Netlify, …) and it runs identically to the preview iframe.
    //
    // Inputs:
    //   - runner.compileToBytecode(source)        → game/program.fbytecode
    //   - collectCommandDllEntries() → fetch each → game/<assembly>.dll
    //   - /runtime/web/runtime-manifest.json      → list of static files
    //   - synthesized fade-manifest.json          → tells index.html
    //                                                to take the bytecode
    //                                                branch instead of
    //                                                the ILaunchable one
    const exportOnce = async (): Promise<void> => {
        const source = await getProjectSource();
        if (!source) { appendOutputLine('No file open.', 'dim'); return; }
        if (projectHasCompileErrors()) {
            appendOutputLine('Export aborted — fix compile errors first.', 'error');
            revealPanel('problems');
            return;
        }
        // ─── 'monogame' branch ──────────────────────────────────────────
        // Bundles the WebRuntime.MonoGame WASM runtime + the user's
        // .fbasic source + every .xnb asset into a static-host zip.
        // The exported index.html (standalone, no ?preview) self-boots
        // and calls LoadProgram(source) — same JSInvokable the Playground
        // already drives via postMessage.
        //
        // Source-bundling (vs bytecode) lets the runtime's existing
        // LoadProgram(string) path do the work. No C# changes; the same
        // export shape works for both the Playground download here and
        // a future dotnet-publish MSBuild target.
        if (currentProject?.type === 'monogame') {
            exportBusy = true;
            refreshRunButtons();
            exportBtn.classList.add('is-exporting');
            appendOutputLine('Building monogame export…', 'dim');
            try {
                const manifest = await fetch('/runtime/monogame/runtime-manifest.json')
                    .then((r) => {
                        if (!r.ok) throw new Error('runtime-manifest.json missing — rebuild monogame runtime');
                        return r.json() as Promise<{ files: string[] }>;
                    });

                const runtimeFiles = manifest.files.filter((rel) => rel !== 'runtime-manifest.json');
                const assetFetches = runtimeFiles.map(async (rel) => {
                    try {
                        const buf = await fetch(`/runtime/monogame/${rel}`).then((r) => r.arrayBuffer());
                        return [rel, new Uint8Array(buf)] as const;
                    } catch (err: any) {
                        appendOutputLine(`[warn] runtime asset missing: ${rel}`, 'dim');
                        return null;
                    }
                });
                const assetResults = await Promise.all(assetFetches);

                // Gather assets the same way syncAssetsToRuntime does:
                // run the macro pass to get a plan, then compile image
                // and audio sources through it. This produces XNBs with
                // proper asset names (e.g. "Pufferfish") instead of the
                // cache blob filenames (".fade-cache/blobs/<hash>.color.v12")
                // that an OPFS-wide `*.xnb` filter used to scoop up.
                //
                // The standalone export's BrowserContentManager registers
                // each `game/<name>.xnb` under `<name>` — matching what
                // fbasic passes to `texture` at runtime.
                const wsNames = await workspace.list();
                const xnbEntries: { name: string; bytes: Uint8Array }[] = [];

                // 1a. Compile image sources (PNG/JPG/...) via the same
                //     plan-driven pipeline syncAssetsToRuntime uses.
                let compilePlan: MonoGameContentPlan = EMPTY_CONTENT_PLAN;
                try {
                    const compile = await monoGameHost.compileForRun(source);
                    if (compile.ok) compilePlan = compile.plan;
                    else appendOutputLine(`[warn] export compile-for-plan: ${compile.error}`, 'dim');
                } catch (e: any) {
                    appendOutputLine(`[warn] export compile-for-plan failed: ${e?.message ?? e}`, 'dim');
                }
                const imageSources = wsNames.filter(isImageSourcePath);
                const audioSources = wsNames.filter(isAudioSourcePath);
                const fontSources = wsNames.filter(isFontSourcePath);
                const shaderSources = wsNames.filter(isShaderSourcePath);
                if (imageSources.length > 0) {
                    try {
                        const { assets, diagnostics } =
                            await compileImageAssetsWithPlan(workspace, imageSources, compilePlan);
                        for (const d of diagnostics) {
                            if (d.severity === 'error') appendOutputLine(`[export ${d.assetName ?? ''}] ${d.message}`, 'error');
                        }
                        for (const a of assets) xnbEntries.push({ name: a.assetName, bytes: a.bytes });
                    } catch (e: any) {
                        appendOutputLine(`[warn] export image compile failed: ${e?.message ?? e}`, 'dim');
                    }
                }
                if (fontSources.length > 0) {
                    try {
                        const { assets, diagnostics } =
                            await compileFontAssetsWithPlan(workspace, fontSources, compilePlan);
                        for (const d of diagnostics) {
                            if (d.severity === 'error') appendOutputLine(`[export ${d.assetName ?? ''}] ${d.message}`, 'error');
                        }
                        for (const a of assets) xnbEntries.push({ name: a.assetName, bytes: a.bytes });
                    } catch (e: any) {
                        appendOutputLine(`[warn] export font compile failed: ${e?.message ?? e}`, 'dim');
                    }
                }
                if (shaderSources.length > 0) {
                    try {
                        const { assets, diagnostics } =
                            await compileShaderAssetsWithPlan(workspace, shaderSources, compilePlan);
                        for (const d of diagnostics) {
                            if (d.severity === 'error') appendOutputLine(`[export ${d.assetName ?? ''}] ${d.message}`, 'error');
                        }
                        for (const a of assets) xnbEntries.push({ name: a.assetName, bytes: a.bytes });
                    } catch (e: any) {
                        appendOutputLine(`[warn] export shader compile failed: ${e?.message ?? e}`, 'dim');
                    }
                }
                // Audio assets ship as raw source bytes — the
                // standalone export's window.fadeAudio decodes them at
                // runtime via Web Audio (matching the playground's
                // monogame iframe). No XNB encoding, just preserve the
                // original extension so KNI's audio path knows what it's
                // decoding. The audio files end up under `audio/<name>.<ext>`
                // in the zip; the standalone bootstrap script reads them
                // and pushes via fadeAudio.register.
                const audioEntries: { name: string; ext: string; bytes: Uint8Array }[] = [];
                for (const path of audioSources) {
                    try {
                        const bytes = await workspace.readBytes(path);
                        const assetName = assetNameForSourcePath(path);
                        const dot = path.lastIndexOf('.');
                        const ext = dot >= 0 ? path.slice(dot).toLowerCase() : '.wav';
                        audioEntries.push({ name: assetName, ext, bytes });
                    } catch (e: any) {
                        appendOutputLine(`[warn] export audio read failed: ${path}: ${e?.message ?? e}`, 'dim');
                    }
                }

                // 1b. Pre-built `.xnb` files the user uploaded directly
                //     (e.g. legacy assets). Skip the asset cache — its
                //     blobs are stored under `.fade-cache/blobs/<hash>...`
                //     and aren't user-addressable assets.
                const prebuiltXnbs = wsNames.filter(
                    (n) => /\.xnb$/i.test(n) && !n.startsWith('.fade-cache/'),
                );
                for (const name of prebuiltXnbs) {
                    try {
                        const raw = await workspace.readBytes(name);
                        const bytes = patchXnbForKni(raw);
                        xnbEntries.push({ name: name.replace(/\.xnb$/i, ''), bytes });
                    } catch (e) {
                        appendOutputLine(`[warn] asset read failed: ${name}`, 'dim');
                    }
                }

                const { zip, strToU8 } = await import('fflate');
                const files: Record<string, Uint8Array> = {};
                for (const r of assetResults) if (r) files[r[0]] = r[1];

                files['game/program.fbasic'] = strToU8(source);
                for (const a of xnbEntries) {
                    files[`game/${a.name}.xnb`] = a.bytes;
                }
                // Audio source bytes go into a separate audio/ subdir
                // with their original extensions preserved. The
                // standalone runtime knows to load them via Web Audio
                // (see fadeAudio.register) rather than the content
                // pipeline.
                for (const a of audioEntries) {
                    files[`audio/${a.name}${a.ext}`] = a.bytes;
                }

                const fadeManifest = {
                    fadeBasic: 'playground-export',
                    exportFormat: '1',
                    type: 'monogame',
                    source: 'program.fbasic',
                    assets: xnbEntries.map((a) => a.name),
                    audio: audioEntries.map((a) => ({ name: a.name, file: `${a.name}${a.ext}` })),
                };
                files['fade-manifest.json'] = strToU8(JSON.stringify(fadeManifest, null, 2));

                const zipBytes = await new Promise<Uint8Array>((resolve, reject) => {
                    zip(files, { level: 6 }, (err, data) => {
                        if (err) reject(err); else resolve(data);
                    });
                });

                const blob = new Blob([zipBytes.slice().buffer], { type: 'application/zip' });
                const url = URL.createObjectURL(blob);
                const name = (currentProject?.name ?? 'fade-monogame-export') + '.zip';
                const a = document.createElement('a');
                a.href = url; a.download = name;
                document.body.appendChild(a);
                a.click();
                document.body.removeChild(a);
                setTimeout(() => URL.revokeObjectURL(url), 1000);
                appendOutputLine(`Exported: ${name} (${(zipBytes.length / 1024).toFixed(0)} KB)`, 'info');
            } catch (e: any) {
                appendOutputLine('Export failed: ' + (e?.message ?? String(e)), 'error');
            } finally {
                exportBusy = false;
                refreshRunButtons();
                exportBtn.classList.remove('is-exporting');
            }
            return;
        }
        exportBusy = true;
        refreshRunButtons();
        exportBtn.classList.add('is-exporting');
        appendOutputLine('Building export…', 'dim');
        try {
            const compile = await runner.compileToBytecode(source);
            if (!compile.ok || !compile.bytecode) {
                appendOutputLine('Export aborted — compile failed.', 'error');
                if (compile.compileError) revealPanel('problems');
                return;
            }

            // Manifest first; everything else can race against each other.
            const manifest = await fetch('/runtime/web/runtime-manifest.json')
                .then((r) => r.json() as Promise<{ files: string[] }>);

            // Parallel I/O: kick off every fetch at once instead of an
            // awaited for-loop. Browsers handle hundreds of concurrent
            // same-origin requests fine, and most of these hit Vite's
            // dev-server cache anyway. Cuts wall time noticeably for
            // the 500+ runtime assets, and removes the long sequence
            // of micro-tasks that was contributing to the perceived
            // click hitch.
            const cmdEntries = collectCommandDllEntries();
            const dllFetches = cmdEntries.map(async (e) => {
                try {
                    const buf = await fetch(`/runtime/fade-libs/${e.assembly}.dll`)
                        .then((r) => r.arrayBuffer());
                    return [e.assembly, new Uint8Array(buf)] as const;
                } catch (err: any) {
                    appendOutputLine(`[warn] ${e.assembly} DLL fetch failed: ${err?.message ?? err}`, 'dim');
                    return null;
                }
            });
            const runtimeFiles = manifest.files.filter((rel) => rel !== 'runtime-manifest.json');
            const assetFetches = runtimeFiles.map(async (rel) => {
                try {
                    const buf = await fetch(`/runtime/web/${rel}`).then((r) => r.arrayBuffer());
                    return [rel, new Uint8Array(buf)] as const;
                } catch (err: any) {
                    appendOutputLine(`[warn] runtime asset missing: ${rel}`, 'dim');
                    return null;
                }
            });

            const [dllResults, assetResults] = await Promise.all([
                Promise.all(dllFetches),
                Promise.all(assetFetches),
            ]);

            const { zip, strToU8 } = await import('fflate');
            const files: Record<string, Uint8Array> = {};
            for (const r of assetResults) if (r) files[r[0]] = r[1];

            // game/ contents: bytecode + each command DLL.
            files['game/program.fbytecode'] = new Uint8Array(compile.bytecode);
            for (const r of dllResults) {
                if (r) files[`game/${r[0]}.dll`] = r[1];
            }

            // Synthesized fade-manifest.json — bytecode flavor (see
            // FadeBasic.Export.Web/wwwroot/index.html for the loader
            // contract). entryAssembly is unused for this flavor but
            // we set it to a placeholder so downstream tooling that
            // checks for the field doesn't choke.
            const fadeManifest = {
                fadeBasic: 'playground-export',
                exportFormat: '1',
                bytecode: 'program.fbytecode',
                entryAssembly: 'program.fbytecode',
                commandDlls: cmdEntries.map((e) => ({ assembly: e.assembly, class: e.class })),
            };
            files['fade-manifest.json'] = strToU8(JSON.stringify(fadeManifest, null, 2));

            // fflate.zip (async, callback API) delegates each file's
            // deflate to its own pool of internal Web Workers. The
            // main thread stays responsive — the button can pulse,
            // the user can keep typing, the iframe runtime can keep
            // running. zipSync did all that work synchronously on
            // the UI thread which was the source of the hitch.
            const zipBytes = await new Promise<Uint8Array>((resolve, reject) => {
                zip(files, { level: 6 }, (err, data) => {
                    if (err) reject(err); else resolve(data);
                });
            });

            // Copy into a regular ArrayBuffer so the Blob constructor's
            // type-checker is happy — zip hands us a Uint8Array backed
            // by a possibly-shared buffer in some environments.
            const blob = new Blob([zipBytes.slice().buffer], { type: 'application/zip' });
            const url = URL.createObjectURL(blob);
            const name = (currentProject?.name ?? 'fade-export') + '.zip';
            const a = document.createElement('a');
            a.href = url; a.download = name;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            // Revoke after a tick so the click has fully dispatched.
            setTimeout(() => URL.revokeObjectURL(url), 1000);
            appendOutputLine(`Exported: ${name} (${(zipBytes.length / 1024).toFixed(0)} KB)`, 'info');
        } catch (e: any) {
            appendOutputLine('Export failed: ' + (e?.message ?? String(e)), 'error');
        } finally {
            exportBusy = false;
            refreshRunButtons();
            exportBtn.classList.remove('is-exporting');
        }
    };
    exportBtn.addEventListener('click', exportOnce);

    // Header Stop button. Delegates to stopAll() so debug sessions get
    // a clean terminate + the canvas pauses uniformly. For 'web'
    // projects there's no canvas to pause and no header-level stop for
    // a CompileAndRun-in-flight; the floating debug-toolbar Stop is
    // still the right way to end a debug session there.
    stopBtn.addEventListener('click', async () => {
        try {
            // If a remote peer owns the runtime (host is running/debugging
            // and we're an observer), Stop ends the shared session by
            // RPC'ing the active runner. We don't have local runtime state
            // to tear down — stopAll would be a no-op here.
            if (remoteActivityInProgress
                && !runActive && !testsBusy && !debugSessionActive) {
                await requestRemoteStop();
                appendOutputLine('Stopped (requested by you).', 'dim');
                return;
            }
            await stopAll();
            appendOutputLine('Stopped.', 'dim');
        } catch (e: any) {
            appendOutputLine('Stop failed: ' + (e?.message ?? String(e)), 'error');
        }
    });

    /** Observer-side: ask whichever peer is currently running/debugging
     *  to stop. Uses the awareness `activity` field to find the target,
     *  then sends a `run:stop` RPC. The host's matching handler calls
     *  stopAll on its side. Falls through silently if no active runner
     *  is visible — the button shouldn't have been enabled in that case. */
    async function requestRemoteStop(): Promise<void> {
        const session = liveSessionHandle?.getSession();
        if (!session) return;
        const peers = session.getState().peers;
        const runner = peers.find((p) => !p.isSelf
            && (p.activity === 'running' || p.activity === 'debugging'));
        if (!runner || !runner.peerId) return;
        await session.request(runner.peerId, 'run:stop', null, { timeoutMs: 10_000 });
    }

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
            // Leave existing inline flex sizes alone — wiping them here
            // made user-resized sections "pop" back to equal thirds the
            // moment any *other* section was collapsed. The collapsed
            // section's own `:has(.collapsed)` rule already shrinks it
            // to header height via `flex: 0 0 auto !important`; sized
            // siblings keep their drag-set size; un-sized siblings keep
            // their `flex: 1 1 0` distribution and naturally absorb the
            // freed space.
            refreshDebugSplitters();
        });
    }

    // ─── Resizable section splitters ──────────────────────────────────────
    // Insert a drag handle between each pair of consecutive *expanded*
    // sections. The handle stretches the section above by however many
    // pixels the user drags it, taking those pixels from the section
    // below. Collapsing a section drops all splitters and re-flexes the
    // rest (see the toggle handler above).
    function debugSections(): HTMLElement[] {
        return Array.from(document.querySelectorAll<HTMLElement>('.debug-pane-host .debug-section'));
    }

    function refreshDebugSplitters() {
        const host = document.querySelector<HTMLElement>('.debug-pane-host');
        if (!host) return;
        for (const s of Array.from(host.querySelectorAll('.debug-splitter'))) s.remove();
        // Pair consecutive *expanded* sections, ignoring any collapsed
        // ones between them. The previous algorithm paired DOM-adjacent
        // sections and skipped if either was collapsed — which left no
        // splitter between Variables and CallStack when Watch was
        // collapsed in the middle, even though they had become visual
        // neighbors.
        const expanded = debugSections().filter((sec) => {
            const body = sec.querySelector('.debug-section-body');
            return !body?.classList.contains('collapsed');
        });
        for (let i = 0; i < expanded.length - 1; i++) {
            const above = expanded[i];
            const below = expanded[i + 1];
            // Insert right *after* `above` so the splitter sits at the
            // bottom edge of the upper section's body. With any collapsed
            // sections in between, this lands the handle visually above
            // the first collapsed header — which is where the user's
            // muscle memory expects it to be, since the handle was there
            // before the section was collapsed.
            host.insertBefore(makeSplitter(above, below, host), above.nextSibling);
        }
    }

    const MIN_SECTION_PX = 32;

    function makeSplitter(above: HTMLElement, below: HTMLElement, host: HTMLElement): HTMLElement {
        const split = document.createElement('div');
        split.className = 'debug-splitter';
        split.setAttribute('role', 'separator');
        split.setAttribute('aria-orientation', 'horizontal');
        split.addEventListener('pointerdown', (e) => {
            e.preventDefault();
            split.classList.add('dragging');
            host.classList.add('debug-resizing');
            split.setPointerCapture(e.pointerId);
            const aRect = above.getBoundingClientRect();
            const bRect = below.getBoundingClientRect();
            const startY = e.clientY;
            const combined = aRect.height + bRect.height;

            const onMove = (ev: PointerEvent) => {
                const delta = ev.clientY - startY;
                let newA = aRect.height + delta;
                let newB = bRect.height - delta;
                if (newA < MIN_SECTION_PX) { newA = MIN_SECTION_PX; newB = combined - newA; }
                if (newB < MIN_SECTION_PX) { newB = MIN_SECTION_PX; newA = combined - newB; }
                above.style.flex = `0 0 ${newA}px`;
                below.style.flex = `0 0 ${newB}px`;
            };
            const onUp = (ev: PointerEvent) => {
                split.classList.remove('dragging');
                host.classList.remove('debug-resizing');
                try { split.releasePointerCapture(ev.pointerId); } catch { /* ignore */ }
                split.removeEventListener('pointermove', onMove);
                split.removeEventListener('pointerup', onUp);
                split.removeEventListener('pointercancel', onUp);
            };
            split.addEventListener('pointermove', onMove);
            split.addEventListener('pointerup', onUp);
            split.addEventListener('pointercancel', onUp);
        });
        return split;
    }

    refreshDebugSplitters();

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
    // When the active debug session targets a specific test (started via
    // dbg.startTest), remember the name so the test panel can update
    // that row's status when the session ends. Null for plain Debug
    // sessions where no test row is involved. Set in debugSingleTest;
    // cleared in stopAll, the 'complete' handler, and the explosion
    // handler — anywhere the session ends.
    let currentDebugTestName: string | null = null;
    // Activity flags (debugSessionActive, debugPaused, runActive,
    // testsBusy, exportBusy) are declared near diagnosticsByUri so the
    // mid-bootstrap refreshFadeProject call can safely use them. The
    // refreshStopButton + refreshRunButtons below read them.
    function refreshStopButton() {
        // Observer side: enable Stop while another peer owns the runtime,
        // so the observer can end the shared session even though they
        // have no local activity flags set.
        stopBtn.disabled = !(runActive || testsBusy || debugSessionActive || remoteActivityInProgress);
    }
    // Single source of truth for Run / Debug / Export enablement. Gates
    // on (a) compile errors in any current-project source — clicking Run
    // with a broken source surfaces as Blazor's error overlay in the
    // monogame iframe — and (b) any VM work already in flight. setDebug-
    // Buttons() and runOnce/exportOnce/startDebug call this instead of
    // toggling the buttons directly so the four signals don't fight.
    function refreshRunButtons() {
        const hasErr = projectHasCompileErrors();
        // Run button morphs into "Reset" while a run is in flight and
        // nothing else (debug, compile errors) is blocking — one click
        // tears down the live run and starts a fresh one. Stop stays
        // available separately so users can still halt without
        // re-running.
        const isReset = runActive && !debugSessionActive && !hasErr;
        if (isReset) {
            runBtn.disabled = false;
            runBtn.textContent = 'Reset';
            runBtn.setAttribute('icon', 'refresh');
        } else {
            // Observer side: also disable Run while a remote peer owns
            // the runtime — the host has control, double-launching would
            // be a no-op locally (we'd run our copy independently) which
            // confuses the shared-session narrative.
            runBtn.disabled = hasErr || debugSessionActive || runActive || remoteActivityInProgress;
            runBtn.textContent = 'Run (⌘R)';
            runBtn.setAttribute('icon', 'play');
        }
        debugBtn.disabled = hasErr || debugSessionActive || runActive || remoteActivityInProgress;
        exportBtn.disabled = exportBusy || hasErr;
        lastBlockedByErrors = hasErr;
        // Broadcast this peer's current runtime activity to the live
        // session so others' top-bar pills can show "Alice is running"
        // / "Bob is debugging" without waiting on actual frame/debug
        // data streaming (which is the Phase 2 work). Order matters —
        // debug wins over run wins over tests, matching how the local
        // UI itself disables the conflicting buttons.
        broadcastLiveActivity();
    }
    /** Map the local run/debug/test flags onto the awareness `activity`
     *  field. No-op if there's no live session in flight. Called
     *  whenever any of the flags change (refreshRunButtons covers
     *  run/debug; setTestsBusy and the debug start/stop paths fold in
     *  via the same call). Also drives the Phase-2A game-frame
     *  streaming: starts captureLoop when this peer is the one running
     *  the program (or debugging) and a live session exists; stops it
     *  otherwise. */
    function broadcastLiveActivity(): void {
        const session = liveSessionHandle?.getSession();
        if (!session) return;
        // CRUCIAL: only count `debugSessionActive` toward our own
        // activity if we're the LOCAL debugger driving it. Observers
        // also flip `debugSessionActive = true` (so their Debug panel
        // enables and mirrors the host's state), but they're NOT
        // running a debug runtime — broadcasting 'debugging' from them
        // would make every other peer treat them as a debug initiator,
        // start showing the observer overlay, and start trying to stream
        // their (non-existent) game canvas back. That's the bug where
        // the host's own game view turned into a "so-and-so is
        // debugging" black overlay.
        const initiatorClientId = session.debugState.get('initiatorClientId') as number | undefined;
        const isLocalDebugger = debugSessionActive && (
            initiatorClientId === undefined || initiatorClientId === session.awareness.clientID
        );
        // Priority: local-debug > run > test > idle. Run+test typically
        // don't co-occur (run uses the same runtime).
        const activity: 'idle' | 'running' | 'debugging' | 'testing' =
            isLocalDebugger ? 'debugging'
            : runActive ? 'running'
            : testsBusy ? 'testing'
            : 'idle';
        try { session.setActivity(activity); }
        catch (e) { console.warn('[fade-collab] setActivity failed', e); }
        // Frame streaming follows the activity: capture only while this
        // peer is actually running or debugging something. Observer-mode
        // is excluded by the `isLocalDebugger` gate above.
        if (activity === 'running' || activity === 'debugging') {
            startGameFrameStreaming(session);
        } else {
            stopGameFrameStreaming();
        }
    }

    // ── Phase 2A: game-frame streaming ────────────────────────────────────
    // Capture loop runs at ~5 FPS — fast enough to feel live, slow enough
    // that JPEG encoding + WebRTC throughput aren't a concern. Each frame
    // ends up around 5-30 KB depending on canvas content; at 5 fps that's
    // well under the ~10 Mbps the data channel can sustain.
    // FPS + JPEG quality are user-tunable via the Settings panel
    // (collab.gameFrameFps / collab.gameFrameQuality). Read at stream-
    // start time. Changing while a stream is in flight requires
    // restart — captureStream pipelines the FPS into MediaStream
    // creation, so changing live wouldn't take effect anyway.
    function currentGameFrameFps(): number {
        const v = Number(getEffective('collab.gameFrameFps')) || 12;
        return Math.max(1, Math.min(30, v));
    }
    function currentGameFrameQuality(): number {
        const v = Number(getEffective('collab.gameFrameQuality')) || 0.55;
        return Math.max(0.1, Math.min(1.0, v));
    }
    let activeGameFrameFps = 12;
    let gameFrameInterval: ReturnType<typeof setInterval> | null = null;

    function startGameFrameStreaming(session: ReturnType<NonNullable<typeof liveSessionHandle>['getSession']>): void {
        if (gameFrameInterval) return;
        if (!session) return;
        activeGameFrameFps = currentGameFrameFps();
        console.log('[fade-collab] starting game frame streaming @ ' + activeGameFrameFps + ' FPS');
        // Kick an immediate capture so the very first frame doesn't
        // wait for the setInterval tick (one interval = up to a full
        // second at low FPS). The first call usually skips because the
        // captureStream's video element hasn't composited yet; that's
        // also why we additionally wire a `requestVideoFrameCallback`
        // inside ensureCaptureStreamFor so the FIRST real frame fires
        // as soon as the browser produces it, instead of waiting for
        // the next interval boundary.
        void captureAndSendFrame(session);
        gameFrameInterval = setInterval(() => {
            // The session reference can become stale if the user leaves
            // the session while a run is in flight. Re-resolve every tick.
            const live = liveSessionHandle?.getSession();
            if (!live) { stopGameFrameStreaming(); return; }
            void captureAndSendFrame(live);
        }, Math.floor(1000 / activeGameFrameFps));
    }

    // Listen for user changes to fps/quality. Restart the stream so
    // the new fps takes effect without waiting for the next session
    // start.
    onSettingsChange(() => {
        if (!gameFrameInterval) return;
        const newFps = currentGameFrameFps();
        if (newFps === activeGameFrameFps) return;
        const session = liveSessionHandle?.getSession();
        if (!session) return;
        stopGameFrameStreaming();
        startGameFrameStreaming(session);
    });

    function stopGameFrameStreaming(): void {
        if (gameFrameInterval) {
            console.log('[fade-collab] stopping game frame streaming');
            clearInterval(gameFrameInterval);
            gameFrameInterval = null;
        }
        // Drop the captureStream pipeline so the next run starts clean.
        // Keeping it across stop/start is risky because the source
        // canvas may have been disposed by the iframe (project switch,
        // run reset) and the MediaStreamTrack tied to a dead canvas
        // emits silent empty frames forever.
        teardownCaptureStream();
    }

    /** Grab the current contents of whichever game-surface canvas this
     *  peer is rendering into and broadcast it as a JPEG. Both the web
     *  iframe (#web-preview-frame) and the monogame iframe
     *  (#mg-preview-frame) are same-origin with the playground; the
     *  monogame iframe is sandboxed but with `allow-same-origin` set,
     *  which keeps its document accessible to the parent.
     *
     *  Looks at both iframes every tick (whichever one has a canvas
     *  wins) rather than gating on `currentProject?.type`. That gates
     *  on a value that can be stale during runtime swaps, and just
     *  checking both is no measurable cost — it's a single
     *  querySelector each. The first iframe with both a canvas and a
     *  non-zero drawing-buffer wins. */
    let gameFrameStats = { captures: 0, sent: 0, skippedNoCanvas: 0, skippedEmpty: 0, skippedError: 0, skippedVideoNotReady: 0 };
    let lastGameFrameStatsLog = 0;
    // captureStream pipeline state. We don't capture the source canvas
    // directly with `canvas.toBlob()` — for WebGL canvases (Blazor /
    // monogame / Kni renderers use WebGL) the default
    // preserveDrawingBuffer=false means the drawing buffer has been
    // cleared by the time toBlob runs, producing valid-but-empty JPEGs
    // (~2-3 KB headers with no pixels). The fix is to read the
    // compositor's output via `canvas.captureStream(fps)` → <video> →
    // drawImage onto a separate 2D canvas → toBlob. The 2D working
    // canvas is plain, so toBlob captures its pixels reliably.
    let captureSourceCanvas: HTMLCanvasElement | null = null;
    let captureMediaStream: MediaStream | null = null;
    let captureVideo: HTMLVideoElement | null = null;
    let captureWorkingCanvas: HTMLCanvasElement | null = null;

    function ensureCaptureStreamFor(source: HTMLCanvasElement): boolean {
        if (captureSourceCanvas === source && captureVideo && captureWorkingCanvas && captureMediaStream) {
            return true;
        }
        // Source canvas changed (or first time) — rebuild the pipeline.
        teardownCaptureStream();
        try {
            const stream = (source as HTMLCanvasElement & { captureStream?: (fps: number) => MediaStream })
                .captureStream?.(activeGameFrameFps);
            if (!stream) {
                console.warn('[fade-collab] source canvas does not support captureStream — frames may be empty for WebGL canvases');
                return false;
            }
            captureMediaStream = stream;
            captureVideo = document.createElement('video');
            captureVideo.muted = true;
            captureVideo.playsInline = true;
            captureVideo.autoplay = true;
            captureVideo.srcObject = stream;
            void captureVideo.play().catch((e) => {
                console.warn('[fade-collab] capture video play() failed', e);
            });
            captureWorkingCanvas = document.createElement('canvas');
            captureSourceCanvas = source;
            // requestVideoFrameCallback fires as soon as the browser
            // produces the first composited frame for the video element.
            // Catching that lets us send the first real frame instantly
            // instead of waiting for the next setInterval tick (could be
            // up to 1000ms at low FPS). Without this, the observer sees
            // an empty canvas for up to one full frame interval after
            // the host clicks Run — which felt like "the stream takes
            // forever to start."
            const v = captureVideo as HTMLVideoElement & { requestVideoFrameCallback?: (cb: () => void) => void };
            v.requestVideoFrameCallback?.(() => {
                const live = liveSessionHandle?.getSession();
                if (live) void captureAndSendFrame(live);
            });
            console.log(`[fade-collab] capture stream wired (source canvas ${source.width}x${source.height})`);
            return true;
        } catch (e) {
            console.warn('[fade-collab] captureStream setup failed', e);
            teardownCaptureStream();
            return false;
        }
    }

    function teardownCaptureStream(): void {
        if (captureMediaStream) {
            try { captureMediaStream.getTracks().forEach((t) => t.stop()); }
            catch { /* ignore */ }
            captureMediaStream = null;
        }
        if (captureVideo) {
            try { captureVideo.srcObject = null; }
            catch { /* ignore */ }
            captureVideo = null;
        }
        captureWorkingCanvas = null;
        captureSourceCanvas = null;
    }

    async function captureAndSendFrame(session: NonNullable<ReturnType<NonNullable<typeof liveSessionHandle>['getSession']>>): Promise<void> {
        gameFrameStats.captures++;
        // Periodic capture-stats log so the user can see what's
        // happening when frames aren't flowing. First log fires on the
        // very first capture (lastGameFrameStatsLog === 0); subsequent
        // ones every ~2 sec.
        const now = performance.now();
        if (lastGameFrameStatsLog === 0 || now - lastGameFrameStatsLog > 2000) {
            lastGameFrameStatsLog = now;
            console.log('[fade-collab] game frame stats', { ...gameFrameStats });
        }

        const iframeIds = ['mg-preview-frame', 'web-preview-frame'] as const;
        let canvas: HTMLCanvasElement | null = null;
        for (const id of iframeIds) {
            const iframe = document.getElementById(id) as HTMLIFrameElement | null;
            if (!iframe) continue;
            let doc: Document | null = null;
            try { doc = iframe.contentDocument; }
            catch { continue; }
            if (!doc) continue;
            // Some iframes mount multiple canvases (e.g. an offscreen
            // worker canvas alongside the visible one). Prefer the
            // monogame-specific id when present, then any canvas with
            // non-zero size.
            const tagged = doc.getElementById('theCanvas') as HTMLCanvasElement | null;
            if (tagged && tagged.width > 0 && tagged.height > 0) {
                canvas = tagged; break;
            }
            const generic = Array.from(doc.querySelectorAll('canvas'))
                .find((c) => (c as HTMLCanvasElement).width > 0 && (c as HTMLCanvasElement).height > 0) as HTMLCanvasElement | undefined;
            if (generic) { canvas = generic; break; }
        }
        if (!canvas) {
            gameFrameStats.skippedNoCanvas++;
            return;
        }
        // Wire (or rewire) the captureStream pipeline to this canvas.
        if (!ensureCaptureStreamFor(canvas)) {
            gameFrameStats.skippedError++;
            return;
        }
        const video = captureVideo;
        const working = captureWorkingCanvas;
        if (!video || !working) {
            gameFrameStats.skippedError++;
            return;
        }
        const w = video.videoWidth;
        const h = video.videoHeight;
        if (w === 0 || h === 0) {
            // The captureStream hasn't produced its first composited
            // frame yet — common during the first ~2 ticks after the
            // pipeline arms.
            gameFrameStats.skippedVideoNotReady++;
            return;
        }
        try {
            if (working.width !== w || working.height !== h) {
                working.width = w;
                working.height = h;
            }
            const ctx = working.getContext('2d');
            if (!ctx) {
                gameFrameStats.skippedError++;
                return;
            }
            ctx.drawImage(video, 0, 0, w, h);
            const blob: Blob | null = await new Promise((resolve) =>
                working.toBlob(resolve, 'image/jpeg', currentGameFrameQuality()),
            );
            if (!blob || blob.size === 0) {
                gameFrameStats.skippedEmpty++;
                return;
            }
            const buf = await blob.arrayBuffer();
            session.sendGameFrame(new Uint8Array(buf));
            gameFrameStats.sent++;
        } catch (e) {
            // Tainted canvas, OOM, etc. Best-effort — drop this frame and
            // try again on the next tick.
            gameFrameStats.skippedError++;
            console.debug('[fade-collab] game frame capture skipped', e);
        }
    }
    let activeFrameId: number | null = null;
    // Decoration IDs the editor uses to draw breakpoint glyphs + the
    // "current line" highlight when paused.
    let bpDecorations: string[] = [];
    let currentLineDecorations: string[] = [];
    // Which model the current-line decoration is on. When the debugger
    // steps across a file boundary we tab-switch to the new file, which
    // changes editor.getModel() — but the old file's decoration is still
    // attached to the old model. Track the owner so we can clear it
    // explicitly instead of leaving a ghost arrow in the previous file.
    let currentLineModel: monaco.editor.ITextModel | null = null;

    function setDebugButtons() {
        const hasSession = debugSessionActive;
        const paused = hasSession && debugPaused;
        // After a fatal VM exception the session is paused for post-mortem
        // inspection only — Continue / Step / Pause are not valid because
        // the VM can't actually resume past the fault. Clicking them
        // freezes the page (the bridge sends a request the runtime will
        // never answer). Only Stop is allowed. The crash overlay's Abort
        // button also remains clickable since it lives outside this bar.
        const canResume = paused && !debugFatalException;
        debugContinueBtn.disabled = !canResume;
        debugPauseBtn.disabled = !hasSession || paused || debugFatalException;
        debugStepOverBtn.disabled = !canResume;
        debugStepInBtn.disabled = !canResume;
        debugStepOutBtn.disabled = !canResume;
        debugStopBtn.disabled = !hasSession;
        // REPL stays usable in the fatal-paused state — locals, watch
        // expressions, and printing variables are exactly the kind of
        // post-mortem inspection we keep the session alive for.
        debugReplInput.disabled = !paused;
        // Header Run/Debug enablement now lives in refreshRunButtons —
        // it folds together hasSession, in-flight run, and compile errors
        // so a broken source disables the buttons even when nothing else
        // is happening.
        refreshRunButtons();
        // Header Stop mirrors the floating debug toolbar's Stop while
        // a session is active. refreshStopButton folds debug, run, and
        // test activity together so the three sources don't stomp on
        // each other (each used to flip stopBtn.disabled directly).
        refreshStopButton();
        // The whole control bar is hidden until a session starts (mirrors
        // VSCode's floating debug toolbar — it doesn't exist when nothing
        // is debugging).
        debugControlBar.toggleAttribute('hidden', !hasSession);
    }
    setDebugButtons();

    // Breakpoints set by OTHER peers via the live session. Maintained
    // alongside `breakpointsByUri` so the gutter render can show both
    // sets with distinct per-owner tints. Local breakpoints never
    // appear here (they're in breakpointsByUri); only remote ones.
    // Map<uri, Map<line, ownerClientId>>.
    const remoteBreakpointsByUri = new Map<string, Map<number, number>>();
    function refreshBreakpointDecorations() {
        const model = editor?.getModel();
        if (!model) return;
        const uri = model.uri.toString();
        const localLines = breakpointsByUri.get(uri) ?? new Set<number>();
        const remoteForFile = remoteBreakpointsByUri.get(uri) ?? new Map<number, number>();
        const decos: monaco.editor.IModelDeltaDecoration[] = [];
        // Our own breakpoints — keep the default red icon (the existing
        // `.fade-breakpoint` CSS class). On the host this is the actual
        // executable breakpoint set; on the observer side these are
        // ones THIS peer set in the gutter, which the host runtime
        // picks up via the Y.Map mirror.
        for (const ln of localLines) {
            decos.push({
                range: new monaco.Range(ln, 1, ln, 1),
                options: {
                    isWholeLine: false,
                    glyphMarginClassName: 'fade-breakpoint codicon codicon-circle-filled',
                    glyphMarginHoverMessage: { value: 'Breakpoint' },
                },
            });
        }
        // Peer breakpoints — only render lines we don't already have
        // locally (own colour wins over remote on collision). Per-peer
        // class is `fade-breakpoint-peer-<clientId>` so the dynamic
        // stylesheet can tint them with the owner's awareness colour.
        for (const [ln, ownerClientId] of remoteForFile) {
            if (localLines.has(ln)) continue;
            const session = liveSessionHandle?.getSession();
            const peer = session?.getState().peers.find((p) => p.clientId === ownerClientId);
            const ownerName = peer?.identity.displayName ?? 'remote peer';
            decos.push({
                range: new monaco.Range(ln, 1, ln, 1),
                options: {
                    isWholeLine: false,
                    glyphMarginClassName: `fade-breakpoint fade-breakpoint-peer-${ownerClientId} codicon codicon-circle-filled`,
                    glyphMarginHoverMessage: { value: `Breakpoint set by ${ownerName}` },
                },
            });
        }
        bpDecorations = model.deltaDecorations(bpDecorations, decos);
    }

    function setCurrentLine(line: number | null) {
        if (line == null) {
            if (currentLineModel) {
                currentLineModel.deltaDecorations(currentLineDecorations, []);
            }
            currentLineDecorations = [];
            currentLineModel = null;
            return;
        }
        const model = editor?.getModel();
        if (!model) return;
        // Switching models (debugger stepped from file A to file B): clear
        // the decoration on the previous owner so an arrow doesn't linger
        // in the file the user just stepped *out* of.
        if (currentLineModel && currentLineModel !== model) {
            currentLineModel.deltaDecorations(currentLineDecorations, []);
            currentLineDecorations = [];
        }
        currentLineDecorations = model.deltaDecorations(currentLineDecorations, [{
            range: new monaco.Range(line, 1, line, 1),
            options: {
                isWholeLine: true,
                className: 'fade-current',
                glyphMarginClassName: 'codicon codicon-debug-stackframe fade-current',
            },
        }]);
        currentLineModel = model;
        // Scroll the editor so the current execution line is in view. Use
        // revealLineInCenterIfOutsideViewport so we don't jitter when the
        // line is already visible.
        try {
            editor?.revealLineInCenterIfOutsideViewport(line, monaco.editor.ScrollType.Smooth);
        } catch { /* editor may not be ready */ }
    }

    // Translate a joined-doc line (as reported by the debugger / a stack
    // frame) into the originating file + local line, switch the editor to
    // that file if it's not already active, then paint the current-line
    // decoration. Falls back to plain setCurrentLine when there's no
    // multi-source project (single-file workspaces still pass through this
    // helper but skip the tab-switch).
    async function focusJoinedDebugLine(joinedLine: number): Promise<void> {
        // If we're observing another peer's debug session, prefer the
        // per-file (name, 1-based-line) the host already broadcast in
        // debugState. The observer's projectSourceMap may be stale, may
        // map to a different joined-line layout than the host's, or
        // may not exist at all if the observer hasn't compiled — any
        // of which makes the fromProject() result wrong. The host's
        // broadcast is the source of truth here.
        const session = liveSessionHandle?.getSession();
        if (session) {
            const initiator = session.debugState.get('initiatorClientId') as number | undefined;
            const observing = initiator != null && initiator !== session.awareness.clientID;
            if (observing) {
                const file = session.debugState.get('currentFile') as string | null;
                const line = session.debugState.get('currentLine') as number | null;
                if (file != null && line != null) {
                    if (file !== activeName) {
                        try { await openFile(workspace, file); }
                        catch { /* fall through to setCurrentLine on whatever's active */ }
                    }
                    setCurrentLine(line); // already 1-based from host's broadcast
                    return;
                }
            }
        }
        if (projectSourceMap) {
            const m = projectSourceMap.fromProject(joinedLine, 0);
            if (m) {
                if (m.name !== activeName) {
                    try { await openFile(workspace, m.name); }
                    catch { /* fall through to setCurrentLine on whatever's active */ }
                }
                setCurrentLine(m.line + 1);
                return;
            }
        }
        setCurrentLine(joinedLine + 1);
    }

    function syncBreakpointsToWorker() {
        const model = editor?.getModel();
        if (!model) return;
        const uri = model.uri.toString();
        // Union of local + remote (live-session) breakpoints. The host's
        // runtime needs to know about both so an observer-set breakpoint
        // actually pauses execution. Without this, remote glyphs show up
        // in the gutter but execution sails right past them.
        const local = breakpointsByUri.get(uri) ?? new Set<number>();
        const remote = remoteBreakpointsByUri.get(uri) ?? new Map<number, number>();
        const allLines = new Set<number>([...local, ...remote.keys()]);
        // Monaco lines are 1-based; the lexer/token lineNumber the bridge
        // expects is 0-based. Drop one.
        const payload: BreakpointRequest[] = [...allLines].map((ln) => ({ line: ln - 1, column: 0 }));
        void dbg.setBreakpoints(payload);
    }

    // Click in the glyph margin toggles a breakpoint on that line.
    // Only Fade source files (.fbasic, languageId='fade') can hold
    // breakpoints — the debug runtime steps Fade VM instructions, so
    // there's nothing to break on in shader (.fx) or config (.json)
    // files. Centralized predicate so the click handler, the hover
    // preview, and any future affordances stay consistent.
    function modelSupportsBreakpoints(model: monaco.editor.ITextModel | null): boolean {
        return model?.getLanguageId() === 'fade';
    }

    editor.onMouseDown((e) => {
        if (e.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN) return;
        const line = e.target.position?.lineNumber;
        if (line == null) return;
        const model = editor!.getModel();
        if (!modelSupportsBreakpoints(model)) return;
        const uri = model!.uri.toString();
        let set = breakpointsByUri.get(uri);
        if (!set) { set = new Set(); breakpointsByUri.set(uri, set); }
        const wasSet = set.has(line);
        if (wasSet) set.delete(line);
        else set.add(line);
        refreshBreakpointDecorations();
        renderBreakpoints();
        if (debugSessionActive) syncBreakpointsToWorker();
        // Mirror to the live session's shared breakpoint map so other
        // peers see our gutter glyphs. Keyed by `${file}:${line}` so
        // the entries dedup naturally. The `file` we store is the
        // workspace-relative name (e.g. "main.fbasic"), not the
        // full URI, because the URI scheme is identical on every peer
        // (`file:///workspace/<name>`) and stripping the prefix here
        // means future renames don't need to rewrite Y.Map keys.
        const session = liveSessionHandle?.getSession();
        if (session) {
            const fileMatch = /^file:\/\/\/workspace\/(.+)$/.exec(uri);
            const file = fileMatch?.[1];
            if (file) {
                const key = `${file}:${line}`;
                if (wasSet) {
                    session.breakpoints.delete(key);
                } else {
                    session.breakpoints.set(key, {
                        file,
                        line,
                        ownerClientId: session.awareness.clientID,
                    });
                }
            }
        }
        // The line we just toggled now has the opposite breakpoint state,
        // which means the phantom-vs-not-allowed cursor signal should
        // flip too. Recompute the preview against the new state.
        updateBreakpointPreview(line);
    });

    // Hover preview for breakpoints. Two effects fold into one mousemove
    // handler:
    //   1. A faded-red phantom .cgmr decoration on the hovered gutter row
    //      (when no real breakpoint is set there yet) — visual "click here
    //      to add a breakpoint" affordance.
    //   2. A class on the editor's outer DOM flagging glyph-margin hover
    //      so CSS can flip cursor between `pointer` (would-add) and
    //      `not-allowed` (would-remove). The phantom .cgmr already
    //      naturally carries `cursor: pointer` from the .cgmr rule, but
    //      this covers the brief gap between mouse-enter and the
    //      decoration landing AND the fallback path when monaco draws
    //      the line overlay above the widgets layer.
    let previewBpDecorations: string[] = [];
    let previewBpLine: number | null = null;

    function clearBreakpointPreview() {
        previewBpLine = null;
        const m = editor?.getModel();
        if (m && previewBpDecorations.length > 0) {
            previewBpDecorations = m.deltaDecorations(previewBpDecorations, []);
        }
        const dom = editor?.getDomNode();
        dom?.classList.remove('fade-gutter-hover', 'fade-gutter-hover-bp');
    }

    function updateBreakpointPreview(line: number | null) {
        const dom = editor?.getDomNode();
        const model = editor?.getModel();
        if (!dom || !model || line == null) {
            clearBreakpointPreview();
            return;
        }
        // Non-Fade models (e.g. .fx shaders, .json config) can't hold
        // breakpoints, so suppress the phantom affordance and cursor
        // class on them entirely. Without this guard the user sees a
        // pointer cursor + click-to-add hint over a .fx gutter and
        // gets no response when they click.
        if (!modelSupportsBreakpoints(model)) {
            clearBreakpointPreview();
            return;
        }
        const uri = model.uri.toString();
        const hasReal = breakpointsByUri.get(uri)?.has(line) ?? false;
        dom.classList.add('fade-gutter-hover');
        dom.classList.toggle('fade-gutter-hover-bp', hasReal);
        // Only draw the phantom on rows that DON'T already have a real
        // breakpoint — otherwise we'd stack two glyphs on the same line.
        if (hasReal) {
            if (previewBpDecorations.length > 0) {
                previewBpDecorations = model.deltaDecorations(previewBpDecorations, []);
                previewBpLine = null;
            }
            return;
        }
        if (previewBpLine === line) return;
        previewBpLine = line;
        previewBpDecorations = model.deltaDecorations(previewBpDecorations, [{
            range: new monaco.Range(line, 1, line, 1),
            options: {
                isWholeLine: false,
                glyphMarginClassName: 'fade-breakpoint-preview codicon codicon-circle-filled',
                glyphMarginHoverMessage: { value: 'Click to add a breakpoint' },
            },
        }]);
    }

    editor.onMouseMove((e) => {
        if (e.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN) {
            clearBreakpointPreview();
            return;
        }
        updateBreakpointPreview(e.target.position?.lineNumber ?? null);
    });
    editor.onMouseLeave(() => clearBreakpointPreview());

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
    async function refreshDebugView(prefetchedFrames?: DebugStackFrame[]): Promise<DebugStackFrame[]> {
        // Accept frames from the caller to avoid a redundant
        // dbg.stackFrames() round-trip when the caller already fetched
        // them — the BREAKPOINT-case broadcast needs the SAME frames it
        // hands to the local render, otherwise the iframe can move past
        // them between the two calls and Y.Map ends up with state from
        // a different VM instant than the editor's line decoration.
        const frames = prefetchedFrames ?? await dbg.stackFrames();
        // Guard against the teardown race: if the user clicked Stop
        // while we were awaiting stackFrames, stopAll has already
        // cleared the panels and flipped debugSessionActive. Bail
        // before we re-render stale frames on top of the cleared state.
        if (!debugSessionActive) return frames;
        renderFrames(frames);
        if (frames.length > 0) {
            activeFrameId = 0;
            await focusJoinedDebugLine(frames[0].lineNumber);
            if (!debugSessionActive) return frames;
            await refreshScopes(0);
            if (!debugSessionActive) return frames;
            await refreshWatches();
            if (!debugSessionActive) return frames;
            setDebugEmptyStates(false);
        } else {
            activeFrameId = null;
            setCurrentLine(null);
            debugVarsTree.innerHTML = '';
            setDebugEmptyStates(true);
            await refreshWatches();
        }
        return frames;
    }

    /** Shared between REV_REQUEST_BREAKPOINT and PROTO_ACK(stepLanded).
     *  Both events mean "the VM has stopped at a new instruction" and
     *  need to:
     *    (a) fetch the new call stack ONCE (multiple fetches race with
     *        the iframe's pumpDebugTick — successive calls can land at
     *        different VM instants),
     *    (b) translate the top frame's joined-source line into the
     *        per-file (name, line) the editor displays,
     *    (c) broadcast {paused, currentFile, currentLine, callStack}
     *        atomically so observers' debugState matches the host's
     *        editor.
     *  Returns the frames so the caller can pass them to
     *  refreshDebugView without re-fetching. */
    async function fetchPausedFramesAndBroadcast(): Promise<DebugStackFrame[]> {
        let frames: DebugStackFrame[] = [];
        try {
            const res = await (dbg.kind === 'remote'
                ? dbg.stackFrames()
                : localDebugAdapter.stackFrames());
            frames = Array.isArray(res)
                ? (res as DebugStackFrame[])
                : ((res as any)?.stackFrames as DebugStackFrame[]) ?? [];
        } catch (e) {
            console.warn('[fade-collab] stackFrames fetch failed', e);
        }
        if (isLocalDebugInitiator()) {
            // ALSO fetch + broadcast the top frame's scopes so observers
            // can render the variables panel without an additional RPC.
            // Previously the observer's refreshDebugView called dbg.scopes
            // which routes through the remote adapter — that's an RPC
            // back to the host whose iframe is often busy with the host's
            // own refresh, so it intermittently times out. Bundling
            // scopes into the same Y.Doc snapshot eliminates that RPC.
            let scopesPayload: unknown = null;
            try {
                const scopes = await localDebugAdapter.scopes(0);
                scopesPayload = scopes ?? null;
            } catch (e) {
                console.warn('[fade-collab] scopes snapshot failed', e);
            }
            const lineNumber = frames[0]?.lineNumber;
            let perFileName: string | null = activeName;
            let perFileLine: number | null =
                lineNumber != null ? lineNumber + 1 : null;
            if (lineNumber != null && projectSourceMap) {
                const m = projectSourceMap.fromProject(lineNumber, 0);
                if (m) {
                    perFileName = m.name;
                    perFileLine = m.line + 1;
                }
            }
            broadcastDebugState({
                paused: true,
                currentFile: perFileName,
                currentLine: perFileLine,
                callStack: frames,
                topFrameScopes: scopesPayload,
            });
        }
        return frames;
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
                await focusJoinedDebugLine(f.lineNumber);
                renderFrames(frames);
                await refreshScopes(idx);
            };
            debugFramesList.append(li);
        });
    }

    // Expansion state: variableId → DebugScope (children) when expanded.
    const expandedVars = new Map<number, DebugScope[]>();

    async function refreshScopes(frameId: number) {
        // Observer-side optimisation: for the top frame, prefer the
        // scopes payload the host already broadcast on the BREAKPOINT/
        // PROTO_ACK snapshot. Bypasses an RPC that was the dominant
        // cause of intermittent "observer doesn't see variables" — the
        // host's iframe is often busy with its own refresh and the
        // observer's RPC ends up waiting behind it. Falls back to the
        // RPC for non-top frames (which the broadcast doesn't include)
        // and for the host (whose dbg.kind is 'local' so the cache
        // doesn't apply).
        let result: any = null;
        if (frameId === 0 && dbg.kind === 'remote') {
            const session = liveSessionHandle?.getSession();
            const cached = session?.debugState.get('topFrameScopes');
            if (cached && typeof cached === 'object') {
                result = cached;
            }
        }
        if (!result) result = await dbg.scopes(frameId);
        // Same teardown-race guard as refreshDebugView: if Stop landed
        // while dbg.scopes was in flight, the panels are already gone.
        if (!debugSessionActive) return;
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
                // eslint-disable-next-line no-console
                console.log('[DBG-EV] setVariable click',
                    { frameId: activeFrameId, vId: v.id, vName: v.name, vType: v.type, vValue: v.value, rhs });
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
                // If we're the active debug initiator, re-broadcast the
                // top-frame scopes so observers see the new value.
                // (Observer-driven mutations get re-broadcast by the host's
                // RPC wrapper; host-driven mutations have no such wrapper,
                // so they need to do it explicitly.)
                await rebroadcastTopFrameScopes();
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

    // Debug-event subscription used to assign `runner.onDebugEvent` and
    // `monoGameHost.onDebugEvent` directly here. Both slots are now owned
    // by the DebugAdapter (created further down once
    // ensureWebVmReadyForDebug + syncAssetsToRuntime are in scope); the
    // adapter forwards every event to all subscribers, and we subscribe
    // `onAnyDebugEvent` to it via `dbg.onDebugEvent(...)` right after
    // construction. Keep this block as a marker so the search for "where
    // are debug events wired up" still lands somewhere sensible.

    // Fatal monogame tick errors. The iframe's rAF loop has already
    // halted before this fires — we log the full message to the Output
    // panel (revealing it so the user sees it) and drop runActive so
    // the Run button flips back from Reset → Run.
    monoGameHost.onGameError = (message) => {
        appendOutputLine('Runtime error: ' + message, 'error');
        revealPanel('output');
        if (runActive) {
            runActive = false;
            refreshRunButtons();
            refreshStopButton();
        }
        // Iframe rAF is already dead (tickHalted); clear our pause state too.
        mgTickPaused = false;
        updateGameStatus('stopped');
    };

    // Pipe iframe-side Console.WriteLine output into the Logs panel.
    // User `print` lines, runtime status messages, and asset-load
    // warnings all land here so the user doesn't have to crack the
    // browser dev console to see what their game is doing — and
    // they're filterable by channel/level. The Output panel stays
    // reserved for editor/runtime status messages.
    monoGameHost.onStdout = handleProgramPrint;
    monoGameHost.onStderr = (line) => {
        handleProgramStderr(line);
        // KNI's `Console.Error.WriteLine` of a multi-line error message
        // arrives here as ONE stderr event with embedded newlines (the
        // iframe's console-forwarding bridge does join+post per call,
        // not per logical line). Split before feeding into the shader-
        // error capture so the header line and each subsequent `ERROR:`
        // line are seen as separate inputs.
        const subLines = line.split(/\r?\n/);
        for (const sub of subLines) {
            if (sub.trim().length > 0) captureShaderErrorLine(sub);
        }
        // Force-flush at end of message so the marker lands even when no
        // non-ERROR line follows in this stderr block.
        flushShaderMarkers();
    };

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
            case 'REV_REQUEST_BREAKPOINT': {
                debugPaused = true;
                setDebugStatus('paused on breakpoint', 'paused');
                revealPanel('call-stack');
                setDebugButtons();
                const frames = await fetchPausedFramesAndBroadcast();
                await refreshDebugView(frames);
                // eslint-disable-next-line no-console
                console.log('[DBG-EV] BREAKPOINT refreshDebugView done', frames[0]?.lineNumber);
                break;
            }
            case 'REV_REQUEST_EXITED':
            case 'complete':
                // For a debug-test session, snapshot the test's result
                // BEFORE we let stopAll-style teardown run. The C# side
                // builds the result from the still-live _debugSession.
                // Once we await terminate (later), the session is gone.
                if (currentDebugTestName) {
                    const finishedName = currentDebugTestName;
                    currentDebugTestName = null;
                    // For monogame, the test-result fetch goes through
                    // monoGameHost; for web, through the LSP/VM iframe via
                    // runner. The pre-multi-source code only called runner,
                    // which silently hangs for monogame (no vmTarget) and
                    // is the reason debug-tests never finalized their rows.
                    const result = currentProject?.type === 'monogame'
                        ? await monoGameHost.debugGetTestResult()
                        : await runner.debugGetTestResult();
                    console.log('[DBG-EV] complete → test result for', finishedName, result);
                    const e = testEntries.find((t) => t.name === finishedName);
                    if (e && result) {
                        e.status = result.passed ? 'pass' : 'fail';
                        e.duration = result.duration;
                        e.failure = result.passed
                            ? null
                            : (result.failureMessage || result.failureReason || 'Failed');
                        e.failureFrames = result.passed ? undefined : (result.failureFrames || []);
                        renderTests();
                    } else if (e) {
                        // No result came back but the session exited — flag
                        // the row as 'stopped' rather than leaving it stuck
                        // on 'running' forever.
                        if (e.status === 'running' || e.status === 'queued') {
                            e.status = 'stopped';
                            renderTests();
                        }
                    }
                }
                debugSessionActive = false;
                debugPaused = false;
                debugFatalException = false;
                setDebugStatus('program exited', 'idle');
                setCurrentLine(null);
                clearDebugInspectionPanels();
                setDebugEmptyStates(true);
                setDebugButtons();
                restorePreDebugLayoutIfUnchanged();
                // Live session: tell observers the debug session is
                // over. They drop back to "Alice is idle" in the chip.
                clearBroadcastDebugState();
                break;
            case 'REV_REQUEST_EXPLODE': {
                // Filter the synthetic "explode" the bridge throws when
                // the user clicks Stop mid-`wait ms`: the VM's exception
                // catch wraps our OperationCanceledException as a runtime
                // error. It's not actually an error — surface it as a
                // clean stop instead.
                const expMsg = (event.json ?? event.message ?? '') as string;
                const isTerminateUnwind = /interrupted by terminate/i.test(expMsg);
                if (isTerminateUnwind) {
                    // Treat as terminal stop (existing teardown path).
                    debugSessionActive = false;
                    debugPaused = false;
                    debugFatalException = false;
                    setDebugStatus('stopped', 'idle');
                    if (currentDebugTestName) {
                        const finishedName = currentDebugTestName;
                        currentDebugTestName = null;
                        const e = testEntries.find((t) => t.name === finishedName);
                        if (e) {
                            e.status = 'stopped';
                            renderTests();
                        }
                    }
                    setCurrentLine(null);
                    clearDebugInspectionPanels();
                    setDebugEmptyStates(true);
                    setDebugButtons();
                    restorePreDebugLayoutIfUnchanged();
                    break;
                }

                // Real runtime error. The C# DebugSession already flips
                // pauseRequestedByMessageId before sending this event
                // (DebugSession.cs:1919 + 2106), so the VM is paused at
                // the failing instruction with frame/stack intact. Keep
                // the session alive in a paused state so the user can
                // poke around locals + call stack before deciding to
                // abort. Abort tears down via stopAll() on the zone
                // widget's button.
                debugPaused = true;
                // Sticky flag so Continue / Step / Pause are disabled
                // for the rest of this session. The VM can't actually
                // resume past the fault — sending continue/step here
                // hangs the bridge waiting for a reply that never
                // comes, locking the whole UI thread.
                debugFatalException = true;
                setDebugStatus('runtime error', 'error');

                // expMsg arrives as the full debug-envelope JSON (e.g.
                // `{"id":-2,"type":6,"message":"invalid-address. ins=[240]
                // …"}`). summarizeCrash parses it, picks out the inner
                // message, classifies the error kind, and returns a
                // human-readable title + structured detail. The REPL,
                // test-row failure text, and crash overlay all share
                // the cleaned-up view — no caller ever has to look at
                // the raw envelope.
                const summary = summarizeCrash(expMsg);
                // Prefix system errors with "[Internal]" in the REPL so the
                // user can immediately tell the fault was an unhandled .NET
                // exception in the VM host rather than a normal Fade runtime
                // error like divide-by-zero or out-of-bounds access.
                const baseText = summary.detail
                    ? `${summary.title} — ${summary.detail}`
                    : summary.title;
                const replText = summary.isSystem
                    ? `[Internal] ${baseText}`
                    : baseText;
                appendReplLine(replText, 'err');
                revealPanel('debug-console');

                // Finalize a debug-test row immediately — the test has
                // failed regardless of whether the user lingers in the
                // paused session for inspection. Clear the name so the
                // eventual stopAll/terminate doesn't double-process it.
                if (currentDebugTestName) {
                    const finishedName = currentDebugTestName;
                    currentDebugTestName = null;
                    const e = testEntries.find((t) => t.name === finishedName);
                    if (e) {
                        e.status = 'fail';
                        e.failure = replText;
                        renderTests();
                    }
                }

                setDebugButtons();

                // Hydrate frames/scopes/watches just like a breakpoint
                // hit, then paint the red crash overlay on the failing
                // line. refreshDebugView calls focusJoinedDebugLine,
                // which paints the yellow .fade-current decoration — we
                // clear it right before the crash overlay's red one so
                // the two don't visually overlap on the same line.
                await refreshDebugView();

                const insIndex = extractInsIndex(summary.inner);
                if (insIndex !== null && editor) {
                    const resolved = await dbg.resolveInstruction(insIndex);
                    if (resolved) {
                        const target = projectSourceMap
                            ? projectSourceMap.fromProject(resolved.lineNumber, resolved.charNumber)
                            : { name: activeName, line: resolved.lineNumber, character: resolved.charNumber };
                        if (target) {
                            if (target.name && target.name !== activeName) {
                                try { await openFile(workspace, target.name); }
                                catch { /* leave overlay on whatever's active */ }
                            }
                            setCurrentLine(null);
                            if (editor) {
                                showCrashOverlay({
                                    editor,
                                    line: target.line + 1,
                                    kind: summary.kind,
                                    title: summary.title,
                                    detail: summary.detail,
                                    // Carry the system flag through so the
                                    // overlay switches to its internal-error
                                    // chrome (bug icon, "Internal error" chip).
                                    isSystem: summary.isSystem,
                                    onAbort: () => { void stopAll(); },
                                });
                            }
                        }
                    }
                }
                break;
            }
            case 'PROTO_ACK': {
                // Two kinds of PROTO_ACKs require UI updates:
                //   1. StepNextResponseMessage with status=1 — a step landed.
                //      DebugSession signals this only via PROTO_ACK (no separate
                //      stop event), so we treat it like a "Stopped" event:
                //      refresh the call stack + variables and stay paused.
                //   2. Breakpoints-resync ACK — carries a `breakpoints` array.
                //      Fired whenever syncBreakpointsToWorker() sends
                //      REQUEST_BREAKPOINTS. No state change; ignore.
                //
                // All other PROTO_ACKs (continue, pause, initial-pause) are
                // handled by their button/call-site handlers, which already
                // update debugPaused synchronously. Acting on them here would
                // race or override that state.
                let stepLanded = false;
                let isBreakpointsAck = false;
                if (event.json) {
                    try {
                        const parsed = JSON.parse(event.json);
                        if (parsed && parsed.status === 1 && typeof parsed.reason === 'string') {
                            stepLanded = true;
                        } else if (parsed && Array.isArray(parsed.breakpoints)) {
                            isBreakpointsAck = true;
                        }
                    } catch { /* not a structured response */ }
                }
                // eslint-disable-next-line no-console
                console.log('[DBG-EV] PROTO_ACK stepLanded=', stepLanded, 'isBreakpointsAck=', isBreakpointsAck);
                if (stepLanded) {
                    debugPaused = true;
                    setDebugStatus('paused on step', 'paused');
                    setDebugButtons();
                    // SAME flow as REV_REQUEST_BREAKPOINT: fetch frames
                    // once, broadcast the per-file snapshot, then
                    // refresh the local panel from those frames. The
                    // previous code path only called refreshDebugView,
                    // which updated the local editor but NEVER wrote
                    // to debugState — so after the initial breakpoint,
                    // every subsequent step left debugState stuck at
                    // the original line. That's the lag the user saw.
                    const frames = await fetchPausedFramesAndBroadcast();
                    await refreshDebugView(frames);
                    // eslint-disable-next-line no-console
                    console.log('[DBG-EV] STEP refreshDebugView done', frames[0]?.lineNumber);
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
    // Ensures the preview iframe is the active VM target before web
    // debug starts. Wrapped here (not added to every dbg.* method)
    // because once the iframe is attached, ongoing debug ops just flow
    // through it — only the entry-point start calls need to wait.
    const ensureWebVmReadyForDebug = async (): Promise<void> => {
        if (currentProject?.type !== 'monogame') {
            showGameSurface('web');
            revealPanel('game');
            await ensureWebPreviewArmed();
        }
    };

    // ── Debug adapter ────────────────────────────────────────────────────
    // The active debug surface. Currently always the local adapter (web
    // runner + monogame iframe dispatch). Phase B will swap this for a
    // RemoteDebugAdapter when another peer in a live session starts
    // debugging — the rest of the code path (commands, event handlers)
    // doesn't need to know which variant is active.
    //
    // Kept under the `dbg` name so existing call sites (~30 sites
    // throughout the bootstrap) don't churn. `localDebugAdapter` is
    // exposed alongside so the future adapter swap can fall back to
    // it for host-side RPC handlers that always execute locally.
    const localDebugAdapter: DebugAdapter = createLocalDebugAdapter({
        runner,
        monoGameHost,
        getProjectType: () => (currentProject?.type as 'web' | 'monogame' | null) ?? null,
        ensureWebVmReady: ensureWebVmReadyForDebug,
        syncMonoGameAssets: syncAssetsToRuntime,
    });

    // ── Shader hot-reload ─────────────────────────────────────────────────
    //
    // On every `.fx` edit, debounce ~400ms then push the changed shader's
    // compiled XNB into the running MonoGame iframe. The .NET-side wiring
    // (BrowserContentManager.RegisterAsset → ConsumeReloadedAssets →
    // RenderSystem.RefreshEffects) already swaps the new bytes into the
    // active Effect on the next frame; this just adds the edit-time
    // trigger. No-ops when:
    //   - the runtime isn't booted (monoGameHost.isReady() === false)
    //   - the project is a web project (no MonoGame iframe to push to)
    //   - the model's URI doesn't map to a `.fx` workspace file
    // Compilation errors are silent here — the shader-validator already
    // shows squiggles + the Problems panel entry for the same source.
    const shaderReloadTimers = new WeakMap<monaco.editor.ITextModel, number>();
    const SHADER_RELOAD_DEBOUNCE_MS = 400;

    function shouldHotReloadShader(model: monaco.editor.ITextModel): boolean {
        if (model.getLanguageId() !== 'fadefx') return false;
        if (!monoGameHost.isReady()) return false;
        if (currentProject?.type !== 'monogame') return false;
        return true;
    }

    function scheduleShaderHotReload(model: monaco.editor.ITextModel): void {
        if (!shouldHotReloadShader(model)) return;
        const prev = shaderReloadTimers.get(model);
        if (prev !== undefined) window.clearTimeout(prev);
        const timer = window.setTimeout(async () => {
            shaderReloadTimers.delete(model);
            if (!shouldHotReloadShader(model)) return;
            // Persist the editor buffer to the workspace first — the
            // compile path reads the file from `workspace.list()` /
            // `workspace.read()`, not the live Monaco model. Without
            // this the iframe would re-load the *previous* content.
            const uri = model.uri.toString();
            const name = uriToName(uri);
            if (!name) return;
            try {
                await workspace.write(name, model.getValue());
            } catch (e) {
                console.warn('[shader-hot-reload] failed to persist before reload:', e);
                return;
            }
            try {
                await syncAssetsToRuntime();
            } catch (e) {
                // syncAssetsToRuntime already routes per-asset errors
                // into the Logs panel; we only catch the top-level
                // exception to keep the timer chain alive.
                console.warn('[shader-hot-reload] sync failed:', e);
            }
        }, SHADER_RELOAD_DEBOUNCE_MS);
        shaderReloadTimers.set(model, timer);
    }

    function attachShaderHotReload(model: monaco.editor.ITextModel): void {
        if (model.getLanguageId() !== 'fadefx') return;
        const sub = model.onDidChangeContent(() => scheduleShaderHotReload(model));
        model.onWillDispose(() => {
            const t = shaderReloadTimers.get(model);
            if (t !== undefined) {
                window.clearTimeout(t);
                shaderReloadTimers.delete(model);
            }
            sub.dispose();
        });
    }

    for (const m of monaco.editor.getModels()) attachShaderHotReload(m);
    monaco.editor.onDidCreateModel((m) => attachShaderHotReload(m));
    // `dbg` is a facade — the rest of the bootstrap calls dbg.X() and the
    // facade forwards to whichever adapter is currently active. Local
    // by default; swapped to a RemoteDebugAdapter while another peer is
    // driving a shared debug session, then swapped back when they stop.
    // Subscribers (next line) attach to the facade, so they keep
    // receiving events through swaps.
    const dbg: FacadeDebugAdapter = createFacadeDebugAdapter(localDebugAdapter);
    dbg.onDebugEvent((event) => { void onAnyDebugEvent(event); });

    const startDebug = async () => {
        const source = await getProjectSource();
        if (!source) {
            clearOutput();
            appendOutputLine('No file open.', 'dim');
            return;
        }
        if (projectHasCompileErrors()) {
            clearOutput();
            appendOutputLine('Fix compile errors before debugging. See Problems panel.', 'error');
            revealPanel('problems');
            return;
        }
        await beginDebugSession(() => dbg.start(source));
    };

    // Shared session-start machinery, factored so both Debug-button and
    // per-test Debug share the same "prep UI → start → sync bps → continue"
    // sequence.
    async function beginDebugSession(starter: () => Promise<DebugStartResult>): Promise<boolean> {
        // Clear any crash overlay from a previous run before booting a
        // fresh session.
        hideCrashOverlay();
        // Debug Mode semantic layout: focus Debug, Game, and Debug Console
        // (or apply the user's saved Debug Mode override). Called once per
        // session start so a per-test debug also opts into Debug Mode —
        // user requirement: "running a test in debug mode should opt to
        // Debug Mode".
        try { applySemanticLayout('debug'); } catch (e) { console.warn('[fade] applySemanticLayout(debug) failed', e); }
        clearOutput();
        debugReplOutput.textContent = '';
        setDebugStatus('starting', 'paused');
        // Mark a session as starting so refreshRunButtons disables Run +
        // Debug. Cleared on the failure path below; on success the
        // debugSessionActive flag below takes over.
        runActive = true;
        refreshRunButtons();
        const result = await starter();
        if (!result.ok) {
            setDebugStatus('failed to start', 'error');
            appendReplLine(result.error ?? 'Failed to start', 'err');
            runActive = false;
            refreshRunButtons();
            // Roll back the Debug Mode layout we applied above — there's
            // no session to keep, and the user shouldn't be stuck looking
            // at Debug Mode after a failed start.
            restorePreDebugLayoutIfUnchanged();
            return false;
        }
        runActive = false;
        debugSessionActive = true;
        debugPaused = true;
        // New session — clear any sticky fatal-exception state left over
        // from a previous session whose crash overlay was dismissed.
        debugFatalException = false;
        setDebugStatus('starting', 'paused');
        setDebugButtons();
        // Live-session: mark this peer as the debug initiator. Both the
        // awareness clientID and the transport peer ID are published —
        // the clientID is used by observers' UI to label "Alice is
        // debugging" via the awareness state lookup; the peer ID is
        // what observers' RemoteDebugAdapter targets when sending RPC
        // commands (step / continue / eval).
        const liveSession = liveSessionHandle?.getSession();
        broadcastDebugState({
            initiatorClientId: liveSession?.awareness.clientID ?? null,
            initiatorPeerId: (liveSession as any)?.room?.selfId ?? null,
            paused: true,
        });
        syncBreakpointsToWorker();
        await dbg.continue();
        debugPaused = false;
        setDebugStatus('running', 'running');
        setDebugButtons();
        broadcastDebugState({ paused: false });
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
        // Flip the test row to 'running' immediately and remember the
        // name so the 'complete' / 'explode' / stopAll paths can finalize
        // the row when the session ends.
        const idx = testEntries.findIndex((t) => t.name === name);
        if (idx >= 0) {
            testEntries[idx].status = 'running';
            testEntries[idx].failure = null;
            testEntries[idx].failureFrames = undefined;
            testEntries[idx].duration = undefined;
            renderTests();
        }
        currentDebugTestName = name;
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

    // Resolve the position to a canonical fbasic command name by
    // going through the LSP's hover. The LSP knows about multi-word
    // commands (`position sprite`, `set color`, etc.) and returns the
    // full canonical phrase regardless of which sub-word the cursor
    // is on. Monaco's model.getWordAtPosition alone can't do this —
    // it would only yield ONE word, missing every multi-word command.
    // The hover provider above already uses this same path; we route
    // through the shared extractCommandNameFromHover helper so the
    // parse stays consistent (and unit-tested in help.test.ts).
    async function resolveCommandAtPosition(
        model: monaco.editor.ITextModel,
        position: monaco.Position,
    ): Promise<string | null> {
        const uri = model.uri.toString();
        const mapped = toLspPosition(uri, position.lineNumber - 1, position.column - 1);
        try {
            const hover = await runner.getHover(lspUriFor(uri), mapped.line, mapped.character);
            if (!hover) return null;
            return extractCommandNameFromHover(hover.contents);
        } catch { return null; }
    }

    // Right-click → "Help". Three-tier resolution:
    //   1. Ask the LSP for the canonical command name at this position.
    //      Handles multi-word commands (`position sprite`) which
    //      getWordAtPosition can't see. If hit → openHelpForCommand
    //      navigates the Help tab to that command entry.
    //   2. Otherwise check the cursor's word against the keyword→
    //      Language.md heading map. If it matches a known fbasic
    //      keyword (`if`, `for`, `dim`, `function`, ...) we jump
    //      straight to the documented section in the language guide.
    //   3. Final fallback: drop the word into the help panel's global
    //      search. Lets the user surface anything we haven't mapped
    //      explicitly (e.g. doc names, user-coined identifiers).
    // The Help panel re-opens itself if the user had closed it, so
    // right-click always produces visible feedback.
    editor.addAction({
        id: 'fade.helpForCommand',
        label: 'Help',
        contextMenuGroupId: 'navigation',
        contextMenuOrder: 2.5,
        precondition: 'editorLangId == fade',
        run: async (ed) => {
            const pos = ed.getPosition();
            if (!pos) return;
            const model = ed.getModel();
            if (!model) return;
            const name = await resolveCommandAtPosition(model, pos);
            if (name && openHelpForCommand(name)) return;
            const word = model.getWordAtPosition(pos)?.word;
            if (!word) return;
            ensureHelpPanelOpen();
            if (await helpCtl.jumpToKeyword(word)) return;
            searchHelpForKeyword(word);
        },
    });

    // Ctrl-click (Cmd-click on Mac) on a command → open help. Monaco's
    // built-in Ctrl-click triggers go-to-definition via the registered
    // DefinitionProvider, which for BUILT-IN commands returns null
    // (they have no source location) — the built-in path is a no-op
    // there and this handler fills the gap. For USER-DEFINED symbols
    // (functions, labels), go-to-definition succeeds AND this
    // handler also fires, but resolveCommandAtPosition returns null
    // for non-commands (the LSP doesn't emit a `### name` hover for
    // user-defined symbols) so nothing extra happens. Net effect:
    // Ctrl-click on `position sprite` opens help, Ctrl-click on
    // `myFunction` still jumps to source.
    //
    // We listen on mousedown so we react around the same time as the
    // built-in go-to-definition. Filter for actual content (not the
    // gutter / overview ruler / etc.) and require Ctrl or Meta
    // without Alt (Alt-click is column-selection).
    editor.onMouseDown(async (e) => {
        const evt = e.event;
        if (evt.altKey) return;
        if (!evt.ctrlKey && !evt.metaKey) return;
        if (e.target?.type !== monaco.editor.MouseTargetType.CONTENT_TEXT) return;
        const pos = e.target.position;
        if (!pos) return;
        // Same `editor!` pattern as the gutter-glyph onMouseDown above
        // — TS can't narrow the outer let-binding through the closure
        // but at runtime addAction etc. already established editor is
        // non-null in this scope.
        const model = editor!.getModel();
        if (!model) return;
        const name = await resolveCommandAtPosition(model, pos);
        if (name && openHelpForCommand(name)) return;
        // Same three-tier fallback as the right-click action:
        //   command → keyword map → free-text search.
        const word = model.getWordAtPosition(pos)?.word;
        if (!word) return;
        ensureHelpPanelOpen();
        if (await helpCtl.jumpToKeyword(word)) return;
        searchHelpForKeyword(word);
    });

    debugContinueBtn.addEventListener('click', async () => {
        await dbg.continue();
        debugPaused = false;
        setDebugStatus('running', 'running');
        setCurrentLine(null);
        setDebugButtons();
        // For host: tell observers we're running immediately so they
        // see "running" feedback before the next BREAKPOINT. For observer:
        // gated out by isLocalDebugInitiator() inside broadcastDebugState
        // (their RPC handler on the host already wrote paused:false).
        // currentFile/currentLine/callStack are left untouched — clearing
        // them races the BREAKPOINT broadcast for any program that
        // immediately hits another bp. Stale location data is harmless
        // while paused=false; the next BREAKPOINT will overwrite it.
        broadcastDebugState({ paused: false });
    });
    debugPauseBtn.addEventListener('click', async () => {
        await dbg.pause();
        debugPaused = true;
        setDebugStatus('paused', 'paused');
        setDebugButtons();
        // REQUEST_PAUSE doesn't fire REV_REQUEST_BREAKPOINT (the C# runtime
        // only emits BREAKPOINT for real bp hits, not the manual pause
        // request) — so onAnyDebugEvent's broadcast branch never runs and
        // observers stay stuck on `paused:false`. Fetch frames + broadcast
        // explicitly, then pass them to refreshDebugView so the local panel
        // and the Y.Doc snapshot are from the SAME VM instant.
        const frames = await fetchPausedFramesAndBroadcast();
        await refreshDebugView(frames);
    });
    debugStepOverBtn.addEventListener('click', async () => {
        await dbg.step('over');
        debugPaused = false;
        setCurrentLine(null);
        setDebugButtons();
        // No broadcast — the step lands at a new pause point almost
        // immediately; the runtime's BREAKPOINT broadcast carries the
        // new state. The observer's RemoteAdapter synthesises BREAKPOINT
        // on location-change-while-paused so it refreshes the panel
        // even though paused stays true throughout.
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
        // Tear down any active crash overlay first — both the Abort
        // button (which calls stopAll) and external Stop clicks should
        // clear the red line decoration + content widget before the
        // session goes away.
        hideCrashOverlay();
        // Snapshot whether a debug session was running BEFORE we flip
        // the flag, so the conditional teardown still runs. We flip
        // debugSessionActive synchronously (rather than after awaiting
        // dbg.terminate) so any in-flight refreshDebugView / refresh-
        // Scopes calls see the session as gone when their dbg.* await
        // resolves and bail out — otherwise they'd re-render stale
        // frames/vars on top of the panels we're about to clear.
        const wasDebugActive = debugSessionActive;
        debugSessionActive = false;
        debugPaused = false;
        debugFatalException = false;
        if (wasDebugActive) {
            // If a debug-test session is in flight, flag the test row
            // as 'stopped' BEFORE we tear down (the explosion handler
            // will also catch this if the unwind comes through that
            // path; whichever fires first wins — both lead to the
            // same row state).
            if (currentDebugTestName) {
                const e = testEntries.find((t) => t.name === currentDebugTestName);
                if (e && (e.status === 'running' || e.status === 'queued')) {
                    e.status = 'stopped';
                    renderTests();
                }
                currentDebugTestName = null;
            }
            await dbg.terminate();
            setDebugStatus('stopped', 'idle');
            setCurrentLine(null);
            clearDebugInspectionPanels();
            setDebugEmptyStates(true);
            restorePreDebugLayoutIfUnchanged();
            // Tell observers the session is over. The `complete` /
            // REV_REQUEST_EXITED case in onAnyDebugEvent also clears
            // this, but the runtime doesn't always emit those events
            // after an explicit terminate (e.g., test mode early-exit,
            // monogame's debug terminate path), so this is the reliable
            // "host stopped" signal on the Y.Map. If the event also
            // fires later, the second clearDebugState is a no-op
            // (it bails when debugState.size === 0).
            clearBroadcastDebugState();
        }
        if (currentProject?.type === 'monogame') {
            // Pause the canvas regardless of debug state — even after debug
            // terminate, the VM is left running, so this halts ticks too.
            try { await monoGameHost.stop(); } catch { /* best effort */ }
        } else {
            // Web projects: tell the cooperative pump in the iframe to
            // tear down whatever's running. No-op if nothing's in flight.
            // The originating runner.run / runTests / debugStart promise
            // will resolve with { ok:false, error:'stopped' } so its
            // caller can react and tear its own UI down.
            try { runner.stopRun(); } catch { /* best effort */ }
        }
        // Clear every activity flag — stopAll is the explicit "nothing
        // is running" signal, regardless of which source had been
        // keeping Stop enabled.
        runActive = false;
        testsBusy = false;
        mgTickPaused = false;
        updateGameStatus('stopped');
        setDebugButtons(); // also calls refreshStopButton
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
        // Re-broadcast for observers (host-driven mutation has no RPC
        // wrapper, so it has to push topFrameScopes itself).
        await rebroadcastTopFrameScopes();
    });

    // Editor option: glyph margin must be on to show breakpoint glyphs.
    editor.updateOptions({ glyphMargin: true });

    // New-file flow: click the +-button OR right-click in the workspace
    // pane's empty area → small dropdown of allowed extensions → an
    // inline edit row appears in the file list, pre-filled with a
    // suggested name (base portion selected). Enter saves; Escape /
    // blur / invalid name silently discards.
    function showNewFileMenu(x: number, y: number, parentFolder?: string) {
        closeAnyFileMenu();
        const menu = document.createElement('div');
        menu.className = 'source-badge-menu';
        menu.dataset.menu = 'file-context';
        // Folder context appears as a faint header so it's obvious WHERE
        // the new item will land. Omitted at the root.
        if (parentFolder) {
            const ctx = document.createElement('div');
            ctx.className = 'source-badge-sep-label';
            ctx.textContent = `in ${parentFolder}`;
            menu.append(ctx);
        }
        // New folder — first item since "create a place to put things"
        // is the action users come for when right-clicking.
        const folderItem = document.createElement('button');
        folderItem.className = 'source-badge-item';
        folderItem.type = 'button';
        folderItem.textContent = 'New folder…';
        folderItem.onclick = (e) => {
            e.stopPropagation();
            closeAnyFileMenu();
            startInlineFolderCreate(parentFolder);
        };
        menu.append(folderItem);
        const sepAfterFolder = document.createElement('div');
        sepAfterFolder.className = 'source-badge-sep';
        menu.append(sepAfterFolder);
        for (const { label, ext } of NEW_FILE_EXTENSIONS) {
            const item = document.createElement('button');
            item.className = 'source-badge-item';
            item.type = 'button';
            item.textContent = label;
            item.onclick = (e) => {
                e.stopPropagation();
                closeAnyFileMenu();
                startInlineCreate(ext, parentFolder);
            };
            menu.append(item);
        }
        // Separator + upload action. Opens a file picker; selected files
        // land in OPFS under their original names (collision-renamed) and
        // the first one is auto-previewed. Upload always targets the
        // root for now — extending it to drop inside `parentFolder`
        // would be straightforward; deferred until someone asks.
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

        const browseCatalog = document.createElement('button');
        browseCatalog.className = 'source-badge-item';
        browseCatalog.type = 'button';
        browseCatalog.textContent = 'Browse catalog…';
        browseCatalog.onclick = (e) => {
            e.stopPropagation();
            closeAnyFileMenu();
            openCatalogPanel();
        };
        menu.append(browseCatalog);
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
    // `parentFolder` (optional) scopes the new file to that folder's
    // path; auto-expands it on success so the new row is visible.
    let inlineCreateRow: HTMLLIElement | null = null;
    async function startInlineCreate(ext: string, parentFolder?: string) {
        // If a previous row is hanging, kill it first.
        inlineCreateRow?.remove();
        inlineCreateRow = null;

        // Find a name that doesn't collide. Uniqueness is computed in
        // the parentFolder's namespace — `untitled.fb` at the root is
        // distinct from `src/untitled.fb`.
        const allPaths = new Set(await workspace.list());
        const inFolder = (n: string) => parentFolder ? `${parentFolder}/${n}` : n;
        const base = 'untitled';
        let candidate = `${base}.${ext}`;
        let n = 1;
        while (allPaths.has(inFolder(candidate))) candidate = `${base}${++n}.${ext}`;

        const li = document.createElement('li');
        li.className = 'file-edit-row';
        if (parentFolder) {
            // Indent the edit row to match where the new file will sit
            // in the tree once committed. Matches the depth math used
            // in renderFileList.
            const depth = parentFolder.split('/').length;
            li.style.paddingLeft = `calc(1rem + ${depth * 14}px)`;
        }
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
            const fullPath = inFolder(name);
            if ((await workspace.list()).includes(fullPath)) return;
            try {
                // Seed the file with an extension-specific starter so new
                // .fx files compile out of the box (a faint UV grid over
                // the sprite texture). Other extensions stay empty.
                const fileExt = name.split('.').pop()?.toLowerCase() ?? '';
                await workspace.write(fullPath, templateForExtension(fileExt));
                // Make sure the parent folder is expanded so the new
                // file is visible. Without this, creating a file in a
                // collapsed folder feels like nothing happened.
                if (parentFolder) collapsedFolders.delete(parentFolder);
                await openFile(workspace, fullPath);
                if (/\.(fbasic|fb)$/i.test(fullPath)) {
                    await projectOps?.addSourceAt(fullPath, 'end');
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

    /** Inline-create row for a folder. Mirrors startInlineCreate's
     *  shape: same edit row UX, same Enter-to-commit / Escape-to-
     *  cancel. Creates the directory via workspace.mkdir and triggers
     *  a re-render so the new folder appears in place. */
    async function startInlineFolderCreate(parentFolder?: string) {
        inlineCreateRow?.remove();
        inlineCreateRow = null;

        const allPaths = new Set(await workspace.list());
        // Also exclude existing directory paths so a name collision
        // with another folder is caught. workspace.listEntries is the
        // authoritative source for both.
        const allEntries = await workspace.listEntries();
        const dirPaths = new Set(allEntries.filter((e) => e.kind === 'directory').map((e) => e.path));
        const inFolder = (n: string) => parentFolder ? `${parentFolder}/${n}` : n;
        const exists = (n: string) => allPaths.has(inFolder(n)) || dirPaths.has(inFolder(n));

        const base = 'new-folder';
        let candidate = base;
        let n = 1;
        while (exists(candidate)) candidate = `${base}${++n}`;

        const li = document.createElement('li');
        li.className = 'file-edit-row';
        if (parentFolder) {
            const depth = parentFolder.split('/').length;
            li.style.paddingLeft = `calc(1rem + ${depth * 14}px)`;
        }
        const input = document.createElement('input');
        input.type = 'text';
        input.spellcheck = false;
        input.autocomplete = 'off';
        input.value = candidate;
        input.placeholder = 'folder name';
        li.append(input);
        fileListEl.prepend(li);
        inlineCreateRow = li;
        input.focus();
        input.select();

        let settled = false;
        const finish = async (commit: boolean) => {
            if (settled) return;
            settled = true;
            const name = input.value.trim();
            li.remove();
            inlineCreateRow = null;
            if (!commit) return;
            if (!name) return;
            if (!/^[\w.\-]+$/.test(name)) return;
            const fullPath = inFolder(name);
            if (exists(name)) return;
            try {
                await workspace.mkdir(fullPath);
                if (parentFolder) collapsedFolders.delete(parentFolder);
                await renderFileList(workspace);
            } catch (e) {
                console.error('[fade] new-folder failed:', e);
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
    // New-folder button: prompt for a path (slashes allowed → nested
    // creation), validate per-segment, call workspace.mkdir via the
    // projectOps surface. Folders auto-expand once created (default
    // state for any folder not in `collapsedFolders`).
    newFolderBtn.addEventListener('click', () => {
        if (!projectOps) return;
        const raw = window.prompt(
            'New folder name (use / for nested):',
            'assets',
        );
        if (!raw) return;
        const path = raw.trim().replace(/^\/+|\/+$/g, '');
        if (!path) return;
        void projectOps.createFolder(path);
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
    // Routes through the same project-aware wrappers the Monaco providers
    // use so multi-source fade.json projects behave identically here.
    (window as any).__fadeLspProbe = async (method: string, params: any) => {
        const m = monaco.editor.getModels().find((mod) => mod.getLanguageId() === 'fade');
        if (!m) throw new Error('no fade model');
        const uri = m.uri.toString();
        const lspUri = lspUriFor(uri);
        const p = params?.line != null
            ? toLspPosition(uri, params.line, params.character)
            : { line: 0, character: 0 };
        switch (method) {
            case 'completion': return runner.getCompletions(lspUri, p.line, p.character);
            case 'hover': return runner.getHover(lspUri, p.line, p.character);
            case 'signature-help': return runner.getSignatureHelp(lspUri, p.line, p.character);
            case 'references': return runner.getReferences(lspUri, p.line, p.character);
            case 'goto-def': return runner.getDefinition(lspUri, p.line, p.character);
            case 'document-symbols': return runner.getDocumentSymbols(lspUri);
            case 'folding-ranges': return runner.getFoldingRanges(lspUri);
            case 'format': return runner.format(lspUri, params.options ?? { tabSize: 4, insertSpaces: true, casing: 0 });
            case 'format-range': return runner.formatRange(lspUri, params.options ?? { tabSize: 4, insertSpaces: true, casing: 0 }, params.range);
            case 'rename': return runner.rename(lspUri, p.line, p.character, params.newName);
            default: throw new Error('unknown probe method: ' + method);
        }
    };

    // Worker-direct helpers for tests that don't depend on the active model
    // (test list / run-tests take a source string explicitly).
    (window as any).__fadeRunnerHelpers = {
        listTests: ({ source }: { source: string }) => runner.listTests(source),
        runTests: ({ source, name }: { source: string; name?: string }) => runner.runTests(source, name),
        // Direct LSP completion query — used by probes to verify the
        // C# side returns the expected items independent of Monaco's
        // filter/sorting pipeline. Pass the same URI Monaco uses on the
        // model (it's mapped to the LSP-side uri internally by callers).
        getCompletions: ({ uri, line, character }: { uri: string; line: number; character: number }) =>
            runner.getCompletions(uri, line, character),
        // Force-push a doc snapshot to the LSP. Probes can use this to
        // make sure the worker's view matches the model before querying.
        setDocument: ({ uri, source }: { uri: string; source: string }) => {
            runner.setDocument(uri, source);
        },
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
    // Subscribe via the adapter so the adapter remains the sole owner
    // of `runner.onDebugEvent` / `monoGameHost.onDebugEvent` — a direct
    // assignment here would silently shadow the adapter's forwarder and
    // break the main UI's event handler.
    dbg.onDebugEvent((event) => {
        (window as any).__debugLastEvent = event;
    });

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
        (window as any).__pgSplash?.setStatus('Failed to start — see browser console.', true);
    });
}
