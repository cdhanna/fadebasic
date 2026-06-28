// Unified Debug UI panel for the browser MonoGame runtime.
//
// Lives in a single dockview tab ("Debug UI"). Inside the tab is a
// vertical stack of independent Tweakpane instances — ONE Pane per
// debug window:
//
//   - One Pane titled "Inspector" — only present when the user's
//     fbasic source has called `enable debug inspector` (sets
//     DebugUISystem.autoInspectorEnabled on the C# side). Contains
//     Metadata + per-provider entity browsers (sprites, transforms,
//     etc.) driven by IDebugProvider RPC.
//
//   - One Pane per fbasic-emitted `begin debug window "name"`. Each
//     Pane is titled with the user's window name and contains the
//     imperative widgets (button/slider/checkbox/…) the user pushes
//     via DebugUICommand.
//
// Why N Panes instead of one Pane with N folders: the user explicitly
// asked for "a tweakpane per debug window" — visually each section
// gets its own border + title bar, which makes the boundaries between
// the inspector and user-defined windows obvious.
//
// Per-frame data flow: the C# side ships an envelope each frame with
//   { gen, queue, autoInspector, metadata?, entities? }
// and the panel consumes it through applyFrameEnvelope():
//   - gen change → wipe ALL Panes (program restarted)
//   - queue      → render/refresh fbasic-window Panes
//   - autoInspector flip → build/dispose Inspector Pane
//   - metadata   → refresh Metadata fields directly (no RPC)
//   - entities   → diff against open type folders, add/remove
//                  entity sub-folders (per-entity DATA still
//                  fetched on demand via entityRefreshTimer)
//
// The only remaining timer is entityRefreshTimer (4 Hz, expanded
// entity folders only). Metadata + entity-list polling is gone —
// they ride along inside the per-frame envelope instead.

import { Pane, type FolderApi } from 'tweakpane';
import type { DebugFieldSchema, DebugUiFrameEnvelope, DebugUiCommand } from './monogame-host';

const ENTITY_REFRESH_MS = 250;           // 4 Hz refresh of open entity folders (data only)

// DebugControlType (browser ordering — must match
// Fade.MonoGame.Game/DebugUISystem.Browser.cs).
const CT = {
    WINDOW_START: 0, WINDOW_END: 1, SEPARATOR: 3,
    TREE_START: 4, TREE_END: 5,
    BUTTON: 10, CHECKBOX: 11,
    FLOAT_SLIDER: 15, INT_SLIDER: 16,
    LABEL: 17, TEXT: 18, TEXTFIELD: 19,
    // Composite types — these refer to higher-level UI surfaces
    // (the auto-inspector, the REPL console). The Inspector Pane
    // renders those separately; we silently skip the queue entries
    // here so the auto-inspector "Debug" window injected by
    // SyncCommands doesn't double up.
    CONSOLE: 20, INSPECTOR: 21,
    ARG_FLOAT: 22, ARG_INT: 23, ARG_STRING: 24,
} as const;

const KIND_BOOL = 0, KIND_INT = 1, KIND_FLOAT = 2, KIND_STRING = 3;

interface FbasicWindowPane {
    pane: Pane;
    container: HTMLElement;
    structHash: string;
    bindings: Map<number, { obj: { v: unknown }; blade: any; type: number }>;
}

interface BoundField {
    blade: any;
    bound: Record<string, unknown>;
    field: DebugFieldSchema;
    /** For vec2/vec3 fields we render one scalar binding per
     *  component (X/Y/Z) instead of Tweakpane's draggable 2D/3D
     *  point picker, which is visually too aggressive for the
     *  inspector. `component` tells pushEdit which sub-path to
     *  POST (`<path>.X` etc.) and tells applySnapshotValue which
     *  index of the snapshot array to read. Undefined for scalar
     *  fields and the packed-int color binding. */
    component?: 'X' | 'Y' | 'Z';
    imageEl?: HTMLImageElement;
    selectEl?: HTMLSelectElement;
    /** performance.now() of the last user-driven change. Snapshot
     *  refreshes skip the binding while this is fresh so they don't
     *  trample a popup the user is dragging through. */
    lastInteractedAt?: number;
}

interface EntityFolder {
    folder: FolderApi;
    fields: BoundField[];
    expanded: boolean;
}

// Snapshot refreshes within this window after a user change are
// skipped — gives the user time to release a color drag or finish
// typing without snapshot stomping their input. Also keeps Tweakpane
// color picker popups from being rebuilt out from under them (which
// is what prevented dismissal).
const REFRESH_LOCKOUT_MS = 1500;

// Set true while applySnapshotValue is mutating a bound object and
// calling blade.refresh(). Tweakpane's refresh() → value.fetch()
// fires an internal `change` event synchronously when the value
// differs from the controller's last-known state — which would
// otherwise re-enter our user-change handler and round-trip the
// value back to C#, creating a feedback loop that fights the popup
// (text vanishes during interaction; popup never dismisses). The
// handler checks this flag and ignores echoed changes.
let applyingSnapshot = false;

export interface DebugUiPanelOptions {
    container: HTMLElement;
    // ── Inspector (provider) callbacks ────────────────────────────
    getSchema: (typeName: string) => Promise<DebugFieldSchema[] | null>;
    getEntitySchema?: (typeName: string, id: number) => Promise<DebugFieldSchema[] | null>;
    listEntities: (typeName: string) => Promise<number[]>;
    /** Optional per-id display labels — e.g. the texture provider
     *  returns asset paths like "Images/Player". Missing keys fall
     *  back to the generic `<type> #<id>` form. */
    getLabels?: (typeName: string) => Promise<Record<string, string>>;
    getEntity: (typeName: string, id: number) => Promise<Record<string, unknown> | null>;
    setField: (typeName: string, id: number, path: string, valueJson: string) => Promise<boolean>;
    // ── fbasic widget callback ────────────────────────────────────
    sendFbasicChange: (ctrlId: number, kind: number, value: string) => void;
}

export interface DebugUiPanelHandle {
    /** Feed one debug-ui-frame envelope from the C# side. */
    applyFrameEnvelope(env: DebugUiFrameEnvelope): void;
    dispose(): void;
}

export function mountDebugUiPanel(opts: DebugUiPanelOptions): DebugUiPanelHandle {
    const root = opts.container;
    root.replaceChildren();
    root.style.padding = '6px';
    root.style.overflowY = 'auto';
    root.style.height = '100%';
    root.style.boxSizing = 'border-box';
    root.style.display = 'flex';
    root.style.flexDirection = 'column';
    root.style.gap = '8px';

    let disposed = false;

    // ── fbasic-window panes ─────────────────────────────────────────
    // One Pane per `begin debug window "name"`. Keyed by name; the
    // Pane persists for the panel's lifetime (or until a gen reset
    // wipes it).
    const fbasicWindows = new Map<string, FbasicWindowPane>();

    // ── inspector pane ──────────────────────────────────────────────
    // One Pane for the autoInspector. Holds the Metadata folder + one
    // sub-folder per IDebugProvider type (sprites, transforms, etc.).
    let inspectorEnabled = false;
    let inspectorPane: Pane | null = null;
    let inspectorContainer: HTMLElement | null = null;
    let metadataFolder: FolderApi | null = null;
    let metadataFields: BoundField[] = [];
    let metadataSchemaPending = false;
    const inspectorTypeRoots = new Map<string, FolderApi>();
    const inspectorEntities = new Map<string, Map<number, EntityFolder>>();
    const inspectorTypePending = new Set<string>();
    const schemaCache = new Map<string, DebugFieldSchema[]>();

    // Per-type sync lock + last-applied id signature. Without these,
    // a 60Hz envelope stream fires syncTypeFolder before the previous
    // call's awaited schema/snapshot fetches have returned, and the
    // same entity gets added over and over (the "ever-growing text"
    // bug). The lock serializes work per type; the signature lets us
    // skip the call entirely when the id set hasn't moved.
    const lastIdsSignature = new Map<string, string>();
    const syncInFlight = new Map<string, Promise<void>>();
    // Tracks "in the middle of being added" entity ids for one more
    // layer of safety inside syncTypeFolder's awaits — even with the
    // per-type lock, this guards against re-adding an id whose folder
    // was disposed and re-added in the same envelope batch.
    const entityAddPending = new Map<string, Set<number>>();

    // The generation id of the program that produced the most recent
    // frame envelope. When it changes, every program-derived bit of
    // state (fbasic Panes + Inspector Pane) has to go. null sentinel
    // so the first real envelope (gen=0) doesn't get confused for a
    // no-op match.
    let lastGen: number | null = null;

    // Sticky expansion state: every Pane / folder we create looks up
    // its expanded flag from this map (falling back to its natural
    // default), and registers a 'fold' listener to keep the map in
    // sync. The map survives gen changes so the user's drilldown
    // context is preserved across Stop/Run cycles.
    //
    // Keys are structural paths so they're stable across program
    // restarts:
    //   win:<name>             fbasic window Pane
    //   win:<name>/tree:<path> a TREE_START folder inside that window
    //   insp                   the Inspector Pane
    //   insp/metadata          the Metadata folder
    //   insp/type:<typeName>   one provider-type rollup folder
    //   insp/type:<t>/ent:<id> one entity folder inside a type folder
    const expandedState = new Map<string, boolean>();

    function recallExpand(key: string, fallback: boolean): boolean {
        const stored = expandedState.get(key);
        return stored === undefined ? fallback : stored;
    }
    function attachFoldTracker(folder: { on?: (ev: string, cb: (e: any) => void) => void } | Pane | FolderApi, key: string) {
        try {
            (folder as any).on?.('fold', (ev: { expanded: boolean }) => {
                expandedState.set(key, ev.expanded);
            });
        } catch { /* ignore — Pane vs FolderApi event surface drift */ }
    }

    // Per-entity-data refresh timer for expanded folders. Metadata +
    // entity-list polling moved into the envelope; this is the last
    // poll because per-entity field data isn't shipped per-frame.
    let entityRefreshTimer: ReturnType<typeof setInterval> | null = null;

    // ── idle hint ────────────────────────────────────────────────────
    let idleHint: HTMLElement | null = null;
    showIdleHint('Run your program to see custom debug windows + the Inspector here.');

    function applyFrameEnvelope(env: DebugUiFrameEnvelope): void {
        if (disposed) return;

        // Generation change = program restarted. Wipe the world.
        if (lastGen !== null && env.gen !== lastGen) {
            wipeAllProgramState();
        }
        lastGen = env.gen;

        // autoInspector flag flip → build / tear down the Inspector
        // Pane. Has to happen before we touch metadata/entities
        // because those write into it.
        if (env.autoInspector && !inspectorEnabled) {
            inspectorEnabled = true;
            buildInspectorPane();
        } else if (!env.autoInspector && inspectorEnabled) {
            inspectorEnabled = false;
            disposeInspector();
        }

        // fbasic windows + widgets.
        if (env.queue.length > 0) {
            clearIdleHint();
            const grouped = groupByWindow(env.queue);
            for (const [name, windowCmds] of grouped) {
                ensureWindow(name);
                renderWindow(name, windowCmds);
            }
        }

        // Inspector — metadata + entity list both come straight from
        // the envelope. We still defer entity-data snapshots to the
        // refresh timer (one RPC per expanded entity).
        if (inspectorEnabled && env.autoInspector) {
            if (env.metadata) applyMetadataFromEnvelope(env.metadata);
            if (env.entities) applyEntityIdsFromEnvelope(env.entities);
            clearIdleHint();
        }
    }

    function wipeAllProgramState() {
        for (const w of fbasicWindows.values()) {
            try { w.pane.dispose(); } catch { /* ignore */ }
            w.container.remove();
        }
        fbasicWindows.clear();
        disposeInspector();
        inspectorEnabled = false;
        showIdleHint('Run your program to see custom debug windows + the Inspector here.');
    }

    // ─── fbasic windows ───────────────────────────────────────────

    function makeSectionContainer(): HTMLElement {
        const div = document.createElement('div');
        // Section spacing is handled by root's flex gap.
        root.appendChild(div);
        return div;
    }

    function ensureWindow(name: string) {
        if (fbasicWindows.has(name)) return;
        const container = makeSectionContainer();
        const key = `win:${name}`;
        const pane = new Pane({ container, title: name, expanded: recallExpand(key, true) });
        attachFoldTracker(pane, key);
        fbasicWindows.set(name, { pane, container, structHash: '', bindings: new Map() });
    }

    function renderWindow(name: string, cmds: DebugUiCommand[]) {
        const entry = fbasicWindows.get(name);
        if (!entry) return;
        const hash = computeStructHash(cmds);
        if (hash !== entry.structHash) {
            entry.structHash = hash;
            rebuildWindow(entry, name, cmds);
        } else {
            refreshWindow(entry, cmds);
        }
    }

    function rebuildWindow(entry: FbasicWindowPane, name: string, cmds: DebugUiCommand[]) {
        // Dispose + re-create the Pane in place (keeps the container).
        try { entry.pane.dispose(); } catch { /* ignore */ }
        entry.bindings.clear();
        entry.container.replaceChildren();
        const paneKey = `win:${name}`;
        entry.pane = new Pane({ container: entry.container, title: name, expanded: recallExpand(paneKey, true) });
        attachFoldTracker(entry.pane, paneKey);

        // Tweakpane root Pane behaves as a folder for addBinding/etc.
        // TREE_START opens a nested folder on top of the root.
        const stack: (Pane | FolderApi)[] = [entry.pane];
        const treePathStack: string[] = [];
        const here = () => stack[stack.length - 1];

        for (let i = 0; i < cmds.length; i++) {
            const c = cmds[i];
            switch (c.t) {
                case CT.TREE_START: {
                    const label = c.l || 'tree';
                    treePathStack.push(label);
                    const treeKey = `${paneKey}/tree:${treePathStack.join('/')}`;
                    const f = here().addFolder({ title: label, expanded: recallExpand(treeKey, false) });
                    attachFoldTracker(f, treeKey);
                    stack.push(f);
                    break;
                }
                case CT.TREE_END:
                    if (stack.length > 1) {
                        stack.pop();
                        treePathStack.pop();
                    }
                    break;
                case CT.SEPARATOR:
                    try { here().addBlade({ view: 'separator' }); } catch { /* ignore */ }
                    break;
                case CT.LABEL: {
                    const obj = { v: c.s ?? '' };
                    here().addBinding(obj, 'v', { label: c.l || '', readonly: true });
                    break;
                }
                case CT.TEXT: {
                    const obj = { v: c.s ?? '' };
                    here().addBinding(obj, 'v', { label: '', readonly: true });
                    break;
                }
                case CT.BUTTON: {
                    const btn = here().addButton({ title: c.l || 'button' });
                    (btn as any).on('click', () => opts.sendFbasicChange(c.id, KIND_BOOL, 'true'));
                    break;
                }
                case CT.CHECKBOX: {
                    const obj = { v: !!c.i };
                    const blade = here().addBinding(obj, 'v', { label: c.l || 'checkbox' });
                    (blade as any).on('change', (ev: any) =>
                        opts.sendFbasicChange(c.id, KIND_INT, ev.value ? '1' : '0'));
                    entry.bindings.set(c.id, { obj, blade, type: c.t });
                    break;
                }
                case CT.INT_SLIDER: {
                    const min = peekArg(cmds, i, CT.ARG_INT)?.i ?? 0;
                    const max = peekArg(cmds, i + 1, CT.ARG_INT)?.i ?? 100;
                    const obj = { v: c.i };
                    const blade = here().addBinding(obj, 'v', { label: c.l || 'int', min, max, step: 1 });
                    (blade as any).on('change', (ev: any) =>
                        opts.sendFbasicChange(c.id, KIND_INT, String(Math.round(ev.value))));
                    entry.bindings.set(c.id, { obj, blade, type: c.t });
                    break;
                }
                case CT.FLOAT_SLIDER: {
                    const min = peekArg(cmds, i, CT.ARG_FLOAT)?.f ?? 0;
                    const max = peekArg(cmds, i + 1, CT.ARG_FLOAT)?.f ?? 100;
                    const obj = { v: c.f ?? 0 };
                    const blade = here().addBinding(obj, 'v', { label: c.l || 'float', min, max });
                    (blade as any).on('change', (ev: any) =>
                        opts.sendFbasicChange(c.id, KIND_FLOAT, String(ev.value)));
                    entry.bindings.set(c.id, { obj, blade, type: c.t });
                    break;
                }
                case CT.TEXTFIELD: {
                    const obj = { v: c.s ?? '' };
                    const blade = here().addBinding(obj, 'v', { label: c.l || 'text' });
                    (blade as any).on('change', (ev: any) =>
                        opts.sendFbasicChange(c.id, KIND_STRING, ev.value ?? ''));
                    entry.bindings.set(c.id, { obj, blade, type: c.t });
                    break;
                }
                case CT.ARG_FLOAT:
                case CT.ARG_INT:
                case CT.ARG_STRING:
                case CT.WINDOW_START:
                case CT.WINDOW_END:
                case CT.INSPECTOR:
                case CT.CONSOLE:
                    // Silently skip — INSPECTOR/CONSOLE are rendered
                    // by the dedicated Inspector Pane, not here.
                    break;
                default:
                    try {
                        here().addBlade({
                            view: 'text', label: c.l || `type ${c.t}`,
                            parse: (s: string) => s, value: `(type ${c.t} not bridged)`,
                        });
                    } catch { /* ignore */ }
                    break;
            }
        }
    }

    function refreshWindow(entry: FbasicWindowPane, cmds: DebugUiCommand[]) {
        for (const c of cmds) {
            const b = entry.bindings.get(c.id);
            if (!b) continue;
            // Don't trample the user's in-flight typing/drag — if the
            // widget's input element holds focus, skip this tick.
            if (isBladeFocused(b.blade)) continue;
            let next: unknown = b.obj.v;
            switch (b.type) {
                case CT.CHECKBOX: next = !!c.i; break;
                case CT.INT_SLIDER: next = c.i; break;
                case CT.FLOAT_SLIDER: next = c.f ?? 0; break;
                case CT.TEXTFIELD: next = c.s ?? ''; break;
                default: continue;
            }
            if (b.obj.v !== next) {
                b.obj.v = next;
                try { b.blade.refresh?.(); } catch { /* ignore */ }
            }
        }
    }

    function computeStructHash(cmds: DebugUiCommand[]): string {
        let h = '';
        for (const c of cmds) {
            if (c.t === CT.ARG_FLOAT || c.t === CT.ARG_INT || c.t === CT.ARG_STRING) continue;
            h += c.id + '|' + c.t + '|' + (c.l || '') + ';';
        }
        return h;
    }

    function peekArg(cmds: DebugUiCommand[], from: number, type: number): DebugUiCommand | undefined {
        for (let i = from + 1; i < cmds.length; i++) {
            if (cmds[i].t === type) return cmds[i];
            if (cmds[i].t !== CT.ARG_FLOAT && cmds[i].t !== CT.ARG_INT && cmds[i].t !== CT.ARG_STRING) return undefined;
        }
        return undefined;
    }

    function groupByWindow(cmds: DebugUiCommand[]): Map<string, DebugUiCommand[]> {
        const result = new Map<string, DebugUiCommand[]>();
        let current: string | null = null;
        let depth = 0;
        for (const cmd of cmds) {
            if (cmd.t === CT.WINDOW_START) {
                if (depth === 0) {
                    current = cmd.l || 'window';
                    if (!result.has(current)) result.set(current, []);
                }
                depth++;
            } else if (cmd.t === CT.WINDOW_END) {
                depth--;
                if (depth === 0) current = null;
            } else if (current !== null) {
                result.get(current)!.push(cmd);
            }
        }
        // Filter out windows whose only content is INSPECTOR/CONSOLE
        // (and ARG_*) — that's the auto-inspector pattern that
        // SyncCommands injects when `enable debug inspector` is on.
        // The dedicated Inspector Pane renders that, so skipping
        // here keeps the panel clean.
        const ONLY_INSPECTOR = new Set<number>([CT.INSPECTOR, CT.CONSOLE, CT.ARG_FLOAT, CT.ARG_INT, CT.ARG_STRING]);
        for (const [name, list] of Array.from(result)) {
            if (list.length === 0) { result.delete(name); continue; }
            if (list.every(c => ONLY_INSPECTOR.has(c.t))) result.delete(name);
        }
        return result;
    }

    // ─── Inspector Pane ──────────────────────────────────────────

    function buildInspectorPane() {
        if (inspectorPane) return;
        clearIdleHint();
        inspectorContainer = makeSectionContainer();
        inspectorPane = new Pane({
            container: inspectorContainer,
            title: 'Inspector',
            expanded: recallExpand('insp', true),
        });
        attachFoldTracker(inspectorPane, 'insp');
        startEntityRefreshPolling();
    }

    function disposeInspector() {
        stopEntityRefreshPolling();
        try { inspectorPane?.dispose(); } catch { /* ignore */ }
        if (inspectorContainer) inspectorContainer.remove();
        inspectorPane = null;
        inspectorContainer = null;
        metadataFolder = null;
        metadataFields = [];
        metadataSchemaPending = false;
        inspectorTypeRoots.clear();
        inspectorEntities.clear();
        inspectorTypePending.clear();
        lastIdsSignature.clear();
        syncInFlight.clear();
        entityAddPending.clear();
    }

    // Bind metadata fields the first time we see a metadata payload,
    // then on every subsequent envelope push values into the bound
    // objects without rebuilding.
    async function ensureMetadataFolder(snapshot: Record<string, unknown>) {
        if (!inspectorPane || metadataFolder || metadataSchemaPending) return;
        metadataSchemaPending = true;
        try {
            const schema = await ensureSchema('metadata');
            if (!schema || disposed || !inspectorPane) return;
            metadataFolder = inspectorPane.addFolder({
                title: 'Metadata',
                expanded: recallExpand('insp/metadata', true),
            });
            attachFoldTracker(metadataFolder, 'insp/metadata');
            metadataFields = buildBindingsFor(metadataFolder, 'metadata', 0, schema, snapshot);
        } finally {
            metadataSchemaPending = false;
        }
    }

    function applyMetadataFromEnvelope(snapshot: Record<string, unknown>) {
        if (!inspectorPane) return;
        if (!metadataFolder) { void ensureMetadataFolder(snapshot); return; }
        for (const f of metadataFields) applySnapshotValue(f, snapshot[f.field.path]);
    }

    function applyEntityIdsFromEnvelope(byType: Record<string, number[]>) {
        if (!inspectorPane) return;
        for (const [typeName, idsRaw] of Object.entries(byType)) {
            const ids = Array.isArray(idsRaw) ? idsRaw : [];
            const signature = ids.join(',');
            if (lastIdsSignature.get(typeName) === signature) continue;

            // First-time type → build the parent folder. We still gate
            // through inspectorTypePending so we don't kick off two
            // builds in the same frame if envelopes are bursting.
            if (!inspectorTypeRoots.has(typeName)) {
                if (inspectorTypePending.has(typeName)) continue;
                inspectorTypePending.add(typeName);
                void buildTypeFolder(typeName, ids)
                    .then(() => { lastIdsSignature.set(typeName, signature); })
                    .finally(() => { inspectorTypePending.delete(typeName); });
                continue;
            }

            // Existing type → serialize syncs per type. While one is in
            // flight we DON'T queue another; instead we wait for it to
            // resolve, then check the latest signature and skip if
            // nothing actually changed. This collapses bursts.
            const previous = syncInFlight.get(typeName) ?? Promise.resolve();
            const next = previous.then(async () => {
                if (disposed) return;
                if (lastIdsSignature.get(typeName) === signature) return;
                await syncTypeFolder(typeName, ids);
                lastIdsSignature.set(typeName, signature);
            }).catch((e) => {
                console.warn('[debug-ui] syncTypeFolder threw', typeName, e);
            });
            syncInFlight.set(typeName, next);
        }
    }

    async function buildTypeFolder(typeName: string, initialIds: number[]) {
        if (!inspectorPane) return;
        const typeKey = `insp/type:${typeName}`;
        const folder = inspectorPane.addFolder({
            title: `${capitalize(typeName)}s (${initialIds.length})`,
            expanded: recallExpand(typeKey, false),
        });
        attachFoldTracker(folder, typeKey);
        inspectorTypeRoots.set(typeName, folder);
        inspectorEntities.set(typeName, new Map());
        await syncTypeFolder(typeName, initialIds);
    }

    async function syncTypeFolder(typeName: string, ids: number[]) {
        const parent = inspectorTypeRoots.get(typeName);
        if (!parent) return;
        const existing = inspectorEntities.get(typeName)!;
        const pending = entityAddPending.get(typeName)
            ?? (entityAddPending.set(typeName, new Set()), entityAddPending.get(typeName)!);

        (parent as { title?: string }).title = `${capitalize(typeName)}s (${ids.length})`;

        // Drop entities that no longer exist.
        const wantSet = new Set(ids);
        for (const [id, ef] of existing) {
            if (!wantSet.has(id)) {
                try { ef.folder.dispose(); } catch { /* ignore */ }
                existing.delete(id);
            }
        }

        // Fetch friendly labels once per sync — same getLabels endpoint
        // the reference-type dropdowns use. Best-effort; if it hangs or
        // rejects, new folder titles fall back to the numeric form.
        let labels: Record<string, string> = {};
        if (opts.getLabels) {
            try {
                labels = await Promise.race([
                    opts.getLabels(typeName),
                    new Promise<Record<string, string>>((_, reject) =>
                        setTimeout(() => reject(new Error('getLabels timeout')), 1500)),
                ]);
            } catch { /* fall back to "<type> #<id>" */ }
        }
        const entityTitle = (id: number) => {
            const named = labels[String(id)];
            return named && named.length > 0 ? named : `${typeName} #${id}`;
        };

        // Refresh titles on EXISTING folders too — an asset path can
        // change (texture re-registered, descriptor updated) without
        // the id set changing, which wouldn't otherwise trigger a
        // visible refresh.
        for (const [id, ef] of existing) {
            const desired = entityTitle(id);
            if ((ef.folder as { title?: string }).title !== desired) {
                (ef.folder as { title?: string }).title = desired;
            }
        }

        // Add new entities. Snapshot the first frame's data so the
        // folder doesn't render with stale placeholders. We hold an
        // entityAddPending guard across the awaits so concurrent
        // re-entries (or a fast restart) don't double-add the same id.
        for (const id of ids) {
            if (existing.has(id) || pending.has(id)) continue;
            pending.add(id);
            try {
                const schema = await ensureEntitySchema(typeName, id);
                if (!schema || disposed) continue;
                // Re-check after the await — the entity could have been
                // disposed by a gen reset, or the type folder itself
                // dropped, or another caller raced us in.
                if (existing.has(id)) continue;
                const snap = await opts.getEntity(typeName, id);
                if (!snap || disposed) continue;
                const parentNow = inspectorTypeRoots.get(typeName);
                if (!parentNow) return;
                if (existing.has(id)) continue;
                const entKey = `insp/type:${typeName}/ent:${id}`;
                const entInitiallyExpanded = recallExpand(entKey, false);
                const sub = parentNow.addFolder({ title: entityTitle(id), expanded: entInitiallyExpanded });
                const ef: EntityFolder = { folder: sub, fields: [], expanded: entInitiallyExpanded };
                sub.on('fold', (ev: { expanded: boolean }) => {
                    ef.expanded = ev.expanded;
                    expandedState.set(entKey, ev.expanded);
                });
                ef.fields = buildBindingsFor(sub, typeName, id, schema, snap);
                existing.set(id, ef);
            } finally {
                pending.delete(id);
            }
        }
    }

    async function ensureSchema(typeName: string): Promise<DebugFieldSchema[] | null> {
        const cached = schemaCache.get(typeName);
        if (cached) return cached;
        const fresh = await opts.getSchema(typeName);
        if (fresh) schemaCache.set(typeName, fresh);
        return fresh;
    }
    async function ensureEntitySchema(typeName: string, id: number) {
        if (opts.getEntitySchema) {
            const perEntity = await opts.getEntitySchema(typeName, id);
            if (perEntity) return perEntity;
        }
        return ensureSchema(typeName);
    }

    function buildBindingsFor(folder: FolderApi, typeName: string, id: number,
                              schema: DebugFieldSchema[], snapshot: Record<string, unknown>): BoundField[] {
        const out: BoundField[] = [];
        for (const field of schema) {
            const built = buildOneBinding(folder, typeName, id, field, snapshot);
            for (const b of built) out.push(b);
        }
        return out;
    }

    function buildOneBinding(folder: FolderApi, typeName: string, id: number,
                             field: DebugFieldSchema, snapshot: Record<string, unknown>): BoundField[] {
        const initial = snapshot[field.path];
        const fieldOpts: Record<string, unknown> = {
            label: field.label || field.path,
            readonly: !!field.readOnly,
        };
        if (typeof field.min === 'number') fieldOpts.min = field.min;
        if (typeof field.max === 'number') fieldOpts.max = field.max;
        if (field.type === 'int') fieldOpts.step = 1;

        if (field.type === 'image') {
            const src = typeof initial === 'string' ? initial : '';
            const img = createImagePreview(folder, field, src);
            return img ? [{ blade: {} as any, bound: { v: src }, field, imageEl: img }] : [];
        }
        if (field.type === 'int' && field.referenceType) {
            const sel = createRefSelect(folder, typeName, id, field, Number(initial ?? 0));
            return sel ? [{ blade: {} as any, bound: { v: Number(initial ?? 0) }, field, selectEl: sel }] : [];
        }

        // Vec2 / Vec3 → one plain scalar binding per component. We
        // skip Tweakpane's `{x,y}` / `{x,y,z}` bindings on purpose:
        // they render a draggable 2D/3D point picker that's visually
        // way too aggressive next to the rest of the inspector rows.
        // Component bindings each POST to `<path>.X` / `.Y` / `.Z`,
        // matching what the C# providers' Apply expects.
        if (field.type === 'vec2' || field.type === 'vec3') {
            const arr = Array.isArray(initial) ? initial as number[] : [];
            const components: Array<'X' | 'Y' | 'Z'> = field.type === 'vec2' ? ['X', 'Y'] : ['X', 'Y', 'Z'];
            const out: BoundField[] = [];
            for (let i = 0; i < components.length; i++) {
                const comp = components[i];
                const compInitial = Number(arr[i] ?? 0);
                const compBound: { v: number } = { v: compInitial };
                // Deliberately omit min/max — schema bounds on
                // position/scale (e.g. ±10000) would turn this into a
                // huge-range slider, which is what the user explicitly
                // asked us to drop. A plain draggable number input is
                // calmer and lets the user type exact values.
                const compOpts: Record<string, unknown> = {
                    label: `${field.label || field.path}.${comp.toLowerCase()}`,
                    readonly: !!field.readOnly,
                };
                const blade = folder.addBinding(compBound, 'v', compOpts);
                const bf: BoundField = { blade, bound: compBound, field, component: comp };
                (blade as any).on('change', (ev: any) => {
                    if (applyingSnapshot) return;
                    bf.lastInteractedAt = performance.now();
                    pushEdit(typeName, id, field, ev.value, comp);
                });
                out.push(bf);
            }
            return out;
        }

        let bound: Record<string, unknown>;
        try {
            switch (field.type) {
                case 'color': {
                    // C# ships color as a single packed RGBA int — the
                    // same format `rgb(r,g,b,a)` produces in fbasic (see
                    // DebugColor.Pack on the C# side). fbasic packs
                    // bytes [R,G,B,A] from low to high; Tweakpane's
                    // number-color binding uses the opposite byte order
                    // (`0xRRGGBBAA`), so we byte-reverse between the
                    // wire and the bound value.
                    //
                    // We use the number-color binding (not the {r,g,b,a}
                    // object binding) because Tweakpane's IntColor model
                    // hard-clamps the alpha component to [0,1] even with
                    // `type: 'int'` (see constrainColorComponents in
                    // @tweakpane/core color-model.js). The object form
                    // loses 8-bit alpha; the number form's writer
                    // multiplies the internal 0-1 alpha by 255 when
                    // packing, so 8-bit alpha survives the round-trip.
                    const fbasicInt = typeof initial === 'number' ? initial : 0xffffffff | 0;
                    bound = { v: fbasicToTweakpaneColor(fbasicInt) };
                    fieldOpts.color = { type: 'int', alpha: true };
                    break;
                }
                case 'bool': bound = { v: !!initial }; break;
                case 'int':
                case 'float': bound = { v: Number(initial ?? 0) }; break;
                case 'string':
                default: bound = { v: String(initial ?? '') }; break;
            }
            const blade = folder.addBinding(bound, 'v', fieldOpts);
            const out: BoundField = { blade, bound, field };
            (blade as any).on('change', (ev: any) => {
                if (applyingSnapshot) return;
                out.lastInteractedAt = performance.now();
                pushEdit(typeName, id, field, ev.value);
            });
            return [out];
        } catch (e) {
            console.warn('[debug-ui] failed to bind', typeName, id, field.path, e);
            return [];
        }
    }

    function pushEdit(typeName: string, id: number, field: DebugFieldSchema, value: unknown, component?: 'X' | 'Y' | 'Z') {
        try {
            switch (field.type) {
                case 'vec2':
                case 'vec3': {
                    // Vec components are now rendered as individual
                    // scalar bindings; each fires its own pushEdit and
                    // POSTs only the changed channel.
                    if (!component) return;
                    if (typeof value !== 'number' || !Number.isFinite(value)) return;
                    void opts.setField(typeName, id, `${field.path}.${component}`, JSON.stringify(value));
                    return;
                }
                case 'color': {
                    // Tweakpane hands us its `0xRRGGBBAA` int; flip the
                    // byte order to fbasic's `[R,G,B,A]` low-to-high
                    // packing and ship the whole color in one setField
                    // call (the C# Apply does the same UnpackColor that
                    // fbasic's `rgb()` round-trips through).
                    if (typeof value !== 'number' || !Number.isFinite(value)) return;
                    const fbasicInt = tweakpaneToFbasicColor(value as number);
                    void opts.setField(typeName, id, field.path, JSON.stringify(fbasicInt | 0));
                    return;
                }
                case 'int':
                    void opts.setField(typeName, id, field.path, JSON.stringify(Math.round(value as number)));
                    return;
                default:
                    void opts.setField(typeName, id, field.path, JSON.stringify(value));
                    return;
            }
        } catch (e) {
            console.warn('[debug-ui] setField failed', typeName, id, field.path, e);
        }
    }

    function createImagePreview(folder: FolderApi, field: DebugFieldSchema, initialSrc: string): HTMLImageElement {
        const row = document.createElement('div');
        row.style.cssText = 'padding: 4px 6px; display: flex; flex-direction: column; gap: 4px;';
        const lbl = document.createElement('div');
        lbl.style.cssText = 'font-size: 11px; opacity: 0.7;';
        lbl.textContent = field.label || field.path;
        const img = document.createElement('img');
        img.style.cssText = 'max-width: 100%; max-height: 128px; image-rendering: pixelated; background: #00000022; border: 1px solid #ffffff14; align-self: flex-start;';
        if (initialSrc) img.src = initialSrc;
        row.append(lbl, img);
        folderContentEl(folder).appendChild(row);
        return img;
    }

    function createRefSelect(folder: FolderApi, typeName: string, id: number, field: DebugFieldSchema, initial: number): HTMLSelectElement {
        const row = document.createElement('div');
        row.style.cssText = 'padding: 4px 6px; display: flex; align-items: center; gap: 8px;';
        const lbl = document.createElement('label');
        lbl.style.cssText = 'font-size: 11px; opacity: 0.7; min-width: 80px;';
        lbl.textContent = field.label || field.path;
        const sel = document.createElement('select');
        sel.style.cssText = 'flex: 1; background: #0008; color: #ddd; border: 1px solid #ffffff14; padding: 2px 4px; font-size: 11px;';
        sel.disabled = !!field.readOnly;
        void refreshRefSelectOptions(sel, field, initial);
        sel.addEventListener('change', () => {
            const v = Number(sel.value);
            void opts.setField(typeName, id, field.path, JSON.stringify(v));
        });
        row.append(lbl, sel);
        folderContentEl(folder).appendChild(row);
        return sel;
    }

    // Tweakpane's FolderApi.element is the outer `.tp-fldv` wrapper —
    // it contains the title button, the indent guide, AND the foldable
    // `.tp-fldv_c` content panel. Appending custom DOM straight onto
    // `.element` puts the row OUTSIDE the foldable panel: it stays
    // visible even when the folder is collapsed and isn't visually
    // inside the entity. The content panel is the direct `.tp-fldv_c`
    // child; we target it explicitly with `:scope >` so a nested
    // sub-folder's container isn't picked up by accident.
    function folderContentEl(folder: FolderApi): HTMLElement {
        const outer = folder.element as HTMLElement;
        const inner = outer.querySelector(':scope > .tp-fldv_c') as HTMLElement | null;
        return inner ?? outer;
    }

    async function refreshRefSelectOptions(sel: HTMLSelectElement, field: DebugFieldSchema, currentValue: number) {
        if (!field.referenceType) return;
        const refType = field.referenceType;
        // listEntities IS the critical path — without an id list there's
        // nothing to show. getLabels is best-effort: if the host hangs
        // or rejects (older runtimes without the DebugGetLabels relay,
        // for example, never reply), we still render numeric fallbacks
        // so the dropdown remains usable.
        let ids: number[] = [];
        try { ids = await opts.listEntities(refType); }
        catch (e) { console.warn('[debug-ui] listEntities failed', refType, e); }
        let labels: Record<string, string> = {};
        if (opts.getLabels) {
            try {
                labels = await Promise.race([
                    opts.getLabels(refType),
                    new Promise<Record<string, string>>((_, reject) =>
                        setTimeout(() => reject(new Error('getLabels timeout')), 1500)),
                ]);
            } catch (e) { /* labels stays empty → fall back to "type #id" */ }
        }
        const want = new Set<number>([0, currentValue, ...ids]);
        const sorted = Array.from(want).sort((a, b) => a - b);
        // Signature includes both the id list and the visible label
        // for each id — otherwise renaming an asset (or registering a
        // new one with the same id) wouldn't refresh the dropdown
        // text. Plain id-list equality misses label changes.
        const labelText = (id: number) => {
            if (id === 0) return '(none)';
            const named = labels[String(id)];
            return named && named.length > 0 ? named : `${refType} #${id}`;
        };
        const wantSignature = sorted.map((id) => `${id}|${labelText(id)}`).join(',');
        const existingSignature = Array.from(sel.options).map((o) => `${o.value}|${o.textContent ?? ''}`).join(',');
        if (existingSignature === wantSignature) return;
        sel.replaceChildren();
        for (const id of sorted) {
            const opt = document.createElement('option');
            opt.value = String(id);
            opt.textContent = labelText(id);
            if (id === currentValue) opt.selected = true;
            sel.appendChild(opt);
        }
    }

    function startEntityRefreshPolling() {
        stopEntityRefreshPolling();
        entityRefreshTimer = setInterval(async () => {
            if (disposed || !inspectorEnabled) return;
            // Pause the entire refresh tick while any Tweakpane popup
            // is open inside the panel. Tweakpane's color picker
            // dismisses on blur from picker focusables — `value.fetch()`
            // triggered by blade.refresh() reads the snapshot, fires
            // an internal change event, and the resulting controller
            // churn was both stomping the user's mid-drag color
            // (vanishing the text) and preventing the popup from
            // closing. The class name `tp-popv-v` is the "visible"
            // modifier that Tweakpane's PopupView toggles.
            if (anyPopupOpen()) return;
            for (const [typeName, map] of inspectorEntities) {
                for (const [id, ef] of map) {
                    if (!ef.expanded) continue;
                    const snap = await opts.getEntity(typeName, id);
                    if (!snap) continue;
                    for (const f of ef.fields) {
                        applySnapshotValue(f, snap[f.field.path]);
                        if (f.selectEl && f.field.referenceType) {
                            void refreshRefSelectOptions(f.selectEl, f.field, Number(snap[f.field.path] ?? 0));
                        }
                    }
                }
            }
        }, ENTITY_REFRESH_MS);
    }

    function anyPopupOpen(): boolean {
        return !!root.querySelector('.tp-popv-v');
    }
    function stopEntityRefreshPolling() {
        if (entityRefreshTimer) { clearInterval(entityRefreshTimer); entityRefreshTimer = null; }
    }

    function applySnapshotValue(f: BoundField, value: unknown) {
        if (value === undefined || value === null) return;
        // Lockout window after a user-driven change — keeps snapshot
        // refreshes from stomping mid-drag color values (which made
        // the text vanish into alpha-0) and from rebuilding the
        // Tweakpane color picker DOM out from under an open popup
        // (which prevented dismissal).
        if (f.lastInteractedAt && performance.now() - f.lastInteractedAt < REFRESH_LOCKOUT_MS) return;
        // Focus guard — if the user is actively typing into this
        // binding's input element (or interacting with its picker
        // popup), don't trample their in-flight value.
        if (isBladeFocused(f.blade)) return;
        if (f.field.type === 'image' && f.imageEl) {
            const s = typeof value === 'string' ? value : '';
            if (s && f.imageEl.src !== s) f.imageEl.src = s;
            if (!s) f.imageEl.removeAttribute('src');
            return;
        }
        if (f.field.type === 'int' && f.field.referenceType && f.selectEl) {
            const s = String(Number(value));
            if (f.selectEl.value !== s) f.selectEl.value = s;
            return;
        }
        // For object-shaped values (vec2/vec3/color) we MUTATE the
        // existing bound.v rather than replacing it. Tweakpane's
        // writer mutates target properties in place via writeProperty,
        // so its internal value model and our bound object share the
        // same reference — replacing the object out from under it
        // causes the controller's value to be considered "changed"
        // even when the data is equivalent, which then fires an
        // internal `change` event chain that interferes with active
        // popups.
        switch (f.field.type) {
            case 'vec2':
            case 'vec3': {
                // One BoundField per component. Read the snapshot array
                // at the index that matches this binding's component.
                const arr = Array.isArray(value) ? value as number[] : [];
                const idx = f.component === 'Y' ? 1 : f.component === 'Z' ? 2 : 0;
                f.bound.v = Number(arr[idx] ?? 0);
                break;
            }
            case 'color': {
                // Snapshot value is a fbasic-packed int — byte-reverse
                // into Tweakpane's `0xRRGGBBAA` layout.
                const fbasicInt = typeof value === 'number' ? value : 0;
                f.bound.v = fbasicToTweakpaneColor(fbasicInt);
                break;
            }
            case 'bool': f.bound.v = !!value; break;
            case 'int':
            case 'float': f.bound.v = Number(value); break;
            case 'string':
            default: f.bound.v = String(value); break;
        }
        applyingSnapshot = true;
        try { f.blade.refresh?.(); } catch { /* ignore */ }
        finally { applyingSnapshot = false; }
    }

    // Returns true if a Tweakpane blade's DOM subtree currently
    // contains document.activeElement — used to skip refreshes that
    // would clobber an input the user is typing into. We also treat
    // an open Tweakpane popup as "focused" so the refresh doesn't
    // rebuild the picker DOM mid-interaction.
    //
    // Popup visibility is toggled by adding `tp-popv-v` to the popup
    // container (the `-v` is the "visible" modifier on the `tp-popv`
    // base class). Earlier code looked for `tp-popv_` (trailing
    // underscore) which doesn't exist in Tweakpane v4.
    function isBladeFocused(blade: unknown): boolean {
        if (!blade || typeof blade !== 'object') return false;
        const el = (blade as { element?: unknown }).element;
        if (!(el instanceof HTMLElement)) return false;
        const active = document.activeElement;
        if (active && el.contains(active)) return true;
        if (el.querySelector('.tp-popv-v')) return true;
        return false;
    }

    function capitalize(s: string) { return s ? s[0].toUpperCase() + s.slice(1) : s; }

    // Convert between fbasic's packed color (bytes [R,G,B,A] from low
    // to high — same int `rgb(r,g,b,a)` produces) and Tweakpane's
    // number-color format (`0xRRGGBBAA`). The two formats are just
    // byte-reversed from each other, so one helper does both
    // directions.
    function reverseColorBytes(n: number): number {
        const v = n | 0;
        return (
            ((v & 0xff) << 24) |
            (((v >>> 8) & 0xff) << 16) |
            (((v >>> 16) & 0xff) << 8) |
            ((v >>> 24) & 0xff)
        ) >>> 0;
    }
    const fbasicToTweakpaneColor = reverseColorBytes;
    const tweakpaneToFbasicColor = reverseColorBytes;

    // ── idle hint helpers ─────────────────────────────────────────
    function showIdleHint(text: string) {
        clearIdleHint();
        idleHint = document.createElement('div');
        idleHint.style.padding = '8px';
        idleHint.style.opacity = '0.6';
        idleHint.style.fontSize = '12px';
        idleHint.textContent = text;
        root.appendChild(idleHint);
    }
    function clearIdleHint() {
        if (idleHint) { idleHint.remove(); idleHint = null; }
    }

    function dispose() {
        if (disposed) return;
        disposed = true;
        stopEntityRefreshPolling();
        for (const w of fbasicWindows.values()) {
            try { w.pane.dispose(); } catch { /* ignore */ }
            w.container.remove();
        }
        fbasicWindows.clear();
        try { inspectorPane?.dispose(); } catch { /* ignore */ }
        inspectorContainer?.remove();
        inspectorPane = null;
        inspectorContainer = null;
        root.replaceChildren();
    }

    return { applyFrameEnvelope, dispose };
}
