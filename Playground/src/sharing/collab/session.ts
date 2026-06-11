// The collaborative editing session — the layer that knows about Yjs but
// not about which transport carries the bytes. Owns:
//   - the Y.Doc (with `files: Y.Map<Y.Text>` + `meta: Y.Map<any>`)
//   - the Awareness instance (peer identities, cursor positions)
//   - the active MonacoBinding (one at a time, swapped on tab change)
//   - the sync-protocol pump on top of `CollabRoom.send/onMessage`
//
// Two roles:
//   - host  → owns the source of truth; seeds the Y.Doc from existing OPFS
//             files; their normal autosave loop persists changes back to OPFS.
//   - guest → ephemeral; receives Y.Doc from the host; mirrors files into a
//             transient workspace; nothing persists past disconnect.

import * as Y from 'yjs';
import { Awareness } from 'y-protocols/awareness';
import * as awarenessProtocol from 'y-protocols/awareness';
import * as syncProtocol from 'y-protocols/sync';
import * as encoding from 'lib0/encoding';
import * as decoding from 'lib0/decoding';
// monaco-editor / y-monaco are heavy and pull in CSS imports that node-side
// test runners can't load. Treat them as types here; the runtime impl is
// pulled in lazily inside `rebindActiveFile` only when a Monaco model is
// actually available (which never happens in unit tests).
import type * as monaco from 'monaco-editor';
import type { MonacoBinding as MonacoBindingType } from 'y-monaco';

import type { CollabRoom, PeerIdentity, RoomStatus, Unsubscribe } from './transport';

// Message envelope: byte 0 = type, rest = type-specific body. Matches what
// y-websocket / y-webrtc use on the wire so any other Yjs provider can
// federate with us if needed later.
const MSG_SYNC = 0;
const MSG_AWARENESS = 1;
const MSG_QUERY_AWARENESS = 3;
// Phase 2 wire additions:
//   MSG_GAMEFRAME    — broadcast of a JPEG frame from whoever's running
//                       the program. Bytes follow the type byte directly.
//   MSG_RPC_REQUEST  — addressed request to another peer with a
//                       correlation ID; receiver runs the registered
//                       handler for `channel` and replies with
//                       MSG_RPC_RESPONSE bearing the same correlation ID.
//   MSG_RPC_RESPONSE — reply to a prior request. `ok=1` means
//                       `result` is the handler's return value;
//                       `ok=0` means `result` is an error message.
const MSG_GAMEFRAME = 4;
const MSG_RPC_REQUEST = 5;
const MSG_RPC_RESPONSE = 6;
const MSG_LOG_LINE = 7;
// Debug-UI envelope (Tweakpane panel state) broadcast by whoever's
// running the program. The host gets one per render frame from the
// monogame iframe; observers don't have an iframe so they need this
// relay to populate their Debug UI panel. Payload is JSON of the
// envelope: { gen, queue, autoInspector, metadata?, entities? }.
const MSG_DEBUG_UI_FRAME = 8;

export type SessionRole = 'host' | 'guest';

export interface SessionHost {
    // ── Editor / Monaco surface ──────────────────────────────────────────
    /** The single Monaco editor that the active model is shown in. */
    readonly editor: monaco.editor.IStandaloneCodeEditor;
    /** Name of the active tab, or null if no tab is open. */
    getActiveFileName(): string | null;
    /** Fires when the user switches tabs (or closes the last tab). */
    onActiveFileChange(cb: (name: string | null) => void): Unsubscribe;
    /** Look up the Monaco model for a file name. Null when the file isn't
     *  open as a tab. */
    getModelForFile(name: string): monaco.editor.ITextModel | null;
    /** Open a file as a tab and surface it in the editor. For host this
     *  is the normal openFile path; for guest the file already exists in
     *  the transient OPFS project, so this just creates the Monaco model. */
    openFile(name: string): Promise<void>;
    /** Close a tab. Used when the host removes a file. Idempotent. */
    closeFile(name: string): Promise<void>;

    // ── Workspace (OPFS) surface ─────────────────────────────────────────
    /** Every file currently in the workspace, recursively. Host calls this
     *  once at session start to seed the Y.Doc. */
    listWorkspaceFiles(): Promise<string[]>;
    /** Classify a path as binary (replicate via `assets` map) or text
     *  (replicate via `files: Y.Text` map). */
    isBinaryPath(path: string): boolean;
    /** Read a text file from the workspace. */
    readWorkspaceText(path: string): Promise<string>;
    /** Read a binary file from the workspace. */
    readWorkspaceBytes(path: string): Promise<Uint8Array>;
    /** Write a text file into the workspace. Used by guests to mirror Y.Text
     *  content into their transient OPFS project. */
    writeWorkspaceText(path: string, content: string): Promise<void>;
    /** Write a binary file into the workspace. Used by guests to mirror
     *  binary assets into their transient OPFS project. */
    writeWorkspaceBytes(path: string, bytes: Uint8Array): Promise<void>;
    /** Delete a file from the workspace. */
    deleteWorkspaceFile(path: string): Promise<void>;
    /** Re-render the file-tree UI. Called by guests after a batch of
     *  initial mirror writes so the tree picks up the new files. */
    refreshFileList(): Promise<void>;
    /** Re-pump the LSP / project-config pipeline for the active workspace.
     *  The playground's normal project-switch flow reloads the page to
     *  re-trigger this on startup; guests can't reload mid-session, so
     *  they call this after mirroring a batch of files to convince the
     *  LSP to re-parse fade.json, build the project source map, and
     *  re-push the joined doc. Without it, the LSP keeps parsing against
     *  the previous project's state and the guest sees a flood of
     *  cross-file reference errors against files the new project doesn't
     *  even have. Optional — non-guest paths don't need it. */
    refreshProjectConfig?(): Promise<void>;
}

export interface SessionMeta {
    /** Stable host peer ID inside the Y.Doc, so guests know who to expect
     *  back on reconnect. Set on session start. */
    hostId: string;
    hostName: string;
    readOnly: boolean;
    /** Optional — the host's project name, for UI labelling on the guest. */
    projectName?: string;
}

/** Active force-sync progress, replicated through `meta.sync`. Non-null
 *  iff a sync is in flight; cleared when it finishes. Used by both UI
 *  layers (progress bar) and the editor read-only enforcement (everyone
 *  goes read-only during a sync, host included, so the file bytes can't
 *  shift under the sync loop). */
export interface SyncProgress {
    /** Unix timestamp of when sync began — UI uses it to compute elapsed
     *  time and reject stale "stuck" progress messages. */
    started: number;
    /** Total number of file operations (writes + deletes) in this sync. */
    total: number;
    /** Operations completed so far. `completed === total` is the terminal
     *  state right before `meta.sync` gets cleared to null. */
    completed: number;
    /** The path currently being processed, for the UI's "syncing foo.fbasic"
     *  string. */
    currentFile?: string;
    /** Awareness clientID of the host that initiated the sync. UI uses this
     *  to label "Alice is syncing…" on the guest side. */
    initiatorClientId?: number;
}

export interface SessionState {
    role: SessionRole;
    status: RoomStatus;
    /** Peers including self. Indexed by Yjs awareness clientID. */
    peers: PeerView[];
    meta: SessionMeta | null;
    /** Non-null while a host-initiated force-sync is running. */
    sync: SyncProgress | null;
    /** Set when we've been in the room long enough without successfully
     *  connecting to the expected counterpart. Guests get this if they
     *  can't reach the host (NAT traversal failed, wrong code, host gone);
     *  the panel surfaces it as a banner. Cleared the moment a peer
     *  actually joins. */
    connectionWarning: string | null;
    /** Informational note from the transport about its current
     *  configuration (e.g. "fell back to minimal ICE config"). Different
     *  from `connectionWarning` — this is about transport setup state
     *  (lives for the whole session), not "we're stuck" trouble. */
    transportNote: string | null;
}

/** What kind of runtime work a peer is currently driving. Surfaces in the
 *  top-bar peer pills so collaborators know who's running the code,
 *  debugging, etc. Independent of who's editing — multiple peers may be
 *  typing while one of them runs.
 *
 *  `idle` is the default and isn't usually surfaced in the UI.
 *  Other values are the start of the eventual "shared run/debug/test"
 *  feature: when peer A flips to `running`, others see "Alice is running"
 *  immediately even before any frame data flows. */
export type PeerActivity = 'idle' | 'running' | 'debugging' | 'testing' | 'syncing';

/** A program-output log line that one peer broadcasts to all others.
 *  Used for sharing `print`/stdout/stderr across collaborators so an
 *  observer's Logs panel mirrors the host's. */
export interface SessionLogLine {
    /** Logger channel — e.g. 'program', 'program-stderr'. */
    channel: string;
    level: 'debug' | 'info' | 'warn' | 'error';
    message: string;
}

/** Where a peer's pointer is hovering, broadcast via awareness so we
 *  can render their cursor on shared surfaces.
 *
 *  For the EDITOR scope: anchored to a Monaco text position
 *  (lineNumber + column, both 1-based) plus a fractional sub-cell
 *  offset (dx, dy in [0,1]) for smooth in-character placement. Tying
 *  to text content means scrolling and word-wrap changes don't drift
 *  the cursor away from the character the sender's mouse is actually
 *  over.
 *
 *  For the GAME scope: normalised (nx, ny) in [0,1] against the game
 *  panel's bounding box, since the canvas content has no scroll. */
export type PeerFocus =
    | { scope: 'editor'; file: string; line: number; column: number; dx: number; dy: number; ts: number }
    | { scope: 'game';   nx: number; ny: number; ts: number }
    | null;

export interface PeerView {
    clientId: number;
    /** Transport-level peer ID (Trystero room peer ID for the live case,
     *  mock-* for the mock transport). Null for our own entry when we
     *  haven't received awareness back yet, but normally always present.
     *  Use this for `session.request(peerId, ...)` targeting. */
    peerId: string | null;
    isSelf: boolean;
    identity: PeerIdentity;
    role: SessionRole;
    activeFile: string | null;
    activity: PeerActivity;
    /** Latest broadcast mouse-cursor position + scope. Null when the
     *  peer's pointer isn't on a shared surface (game tab or editor). */
    focus: PeerFocus;
}

export interface StartOptions {
    role: SessionRole;
    identity: PeerIdentity;
    /** Host-only — populates `meta` on the Y.Doc so guests can name the
     *  session in their UI. */
    projectName?: string;
}

export class CollabSession {
    readonly doc = new Y.Doc();
    readonly awareness: Awareness;
    private readonly files: Y.Map<Y.Text>;
    private readonly assets: Y.Map<Uint8Array>;
    private readonly meta: Y.Map<any>;
    /** Shared debug state — the peer who started a debug session writes
     *  to it; everyone reads. Keys are open-ended (paused, currentFile,
     *  currentLine, callStack, locals, …) so consumers can extend the
     *  surface without bumping a schema. Null `initiatorClientId` means
     *  no session is in flight; observers should fall back to local
     *  state. */
    readonly debugState: Y.Map<any>;
    /** Shared breakpoints — persist across debug sessions (unlike
     *  debugState which clears on debug exit). Keys are `${file}:${line}`.
     *  Values include the owner's Yjs awareness clientID so consumers
     *  can tint the gutter glyph by who set the breakpoint. Both host
     *  and observers may write here, but only the host's runtime
     *  actually breaks on them — observer breakpoints become "visible
     *  hints" until the host runtime picks them up via its breakpoint
     *  sync. */
    readonly breakpoints: Y.Map<{ file: string; line: number; ownerClientId: number; condition?: string }>;
    /** Public read-only view of game-frame subscribers' callbacks. Each
     *  registered handler receives `(peerId, jpegBytes)` per frame. */
    private readonly gameFrameCbs = new Set<(peerId: string, bytes: Uint8Array) => void>();
    /** Subscribers for broadcast log/print lines (e.g. the host's program
     *  `print` output forwarded so observers can see it in their Logs
     *  panel). Each handler receives `(peerId, line)`. */
    private readonly logLineCbs = new Set<(peerId: string, line: SessionLogLine) => void>();
    /** Subscribers for relayed Debug UI envelopes (Tweakpane panel state
     *  snapshots). The active runner broadcasts one per frame; observers
     *  apply them to their Debug UI panel. */
    private readonly debugUiFrameCbs = new Set<(peerId: string, json: string) => void>();
    /** Registered RPC handlers, keyed by channel name. A `request()`
     *  from a peer routes to the channel's handler; the handler's
     *  return value (or thrown error) is shipped back as a response. */
    private readonly rpcHandlers = new Map<string, (peerId: string, payload: unknown) => unknown | Promise<unknown>>();
    /** Pending outbound RPC requests waiting for their response. Keyed
     *  by correlation ID; cleared on response, on `request()` timeout,
     *  or on session destroy. */
    private readonly pendingRpc = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void; timeoutId: ReturnType<typeof setTimeout> | null }>();
    /** Monotonic counter for the local end of RPC correlation IDs. We
     *  encode `(clientID << 24) | localCounter` on the wire so the
     *  remote peer's correlation ID can't collide with ours — they
     *  reflect ours back in their response, but if they happened to be
     *  issuing their OWN requests at the same time, theirs would use a
     *  different prefix. */
    private rpcCounter = 0;
    private readonly room: CollabRoom;
    private readonly host: SessionHost;
    private role: SessionRole = 'host';
    private identity!: PeerIdentity;
    private binding: MonacoBindingType | null = null;
    private boundFileName: string | null = null;
    /** The path our most-recent `rebindActiveFile` call was aiming at. Guards
     *  against duplicate-binding races: every entry to rebindActiveFile
     *  bumps this; the async y-monaco import resolves to a doBind that
     *  bails if `bindingRequestedFor` has moved on. Without it, the
     *  files.observe → rebindActiveFile recursion triggered by the
     *  host's own `this.files.set` would queue a second import + doBind
     *  and leak a second `MonacoBinding` instance bound to the same
     *  model, doubling every Y.Text ↔ Monaco event. */
    private bindingRequestedFor: string | null = null;
    // Loaded lazily on first rebind so test environments that never bind
    // (no Monaco model) don't pull in the y-monaco / monaco-editor bundle.
    private monacoBindingCtor: typeof MonacoBindingType | null = null;
    /** Last `readOnly` flag we pushed into Monaco. Used to skip the
     *  `editor.updateOptions` call when the value hasn't changed —
     *  awareness fires on every keystroke / cursor move, so making this
     *  a no-op for the common case avoids the Monaco-internal dispatch
     *  cost. */
    private lastAppliedReadOnly: boolean | null = null;
    /** Coalesce rapid `emitState` calls (awareness updates fire one per
     *  cursor move) into at most one notification per microtask. Stops
     *  subscribers' synchronous DOM-rewrite work from blocking the
     *  typing path. */
    private emitStateScheduled = false;
    /** Timer that flips `connectionWarning` on if we never see another
     *  peer connect. Cleared the moment a peer arrives. */
    private connectionWatchdog: ReturnType<typeof setTimeout> | null = null;
    private connectionWarning: string | null = null;
    private readonly unsubs: Unsubscribe[] = [];
    private readonly stateCbs = new Set<(s: SessionState) => void>();
    private destroyed = false;
    /** Tracks which paths the guest has materialised in OPFS so a Y.Text
     *  update for an already-known file doesn't re-fire the initial
     *  mirror logic. Y.Text content changes are followed up via the
     *  per-Y.Text observers below. */
    private mirroredPaths = new Set<string>();
    /** Per-file Y.Text observers we install on guests so off-screen file
     *  changes get written through to OPFS. Cleaned up in destroy(). */
    private ytextUnobservers = new Map<string, () => void>();
    /** Per-file debounce timers for guest-side OPFS writes — without
     *  this, every keystroke from the host would fire a writable-stream
     *  cycle in OPFS. */
    private ytextWriteTimers = new Map<string, number>();

    constructor(host: SessionHost, room: CollabRoom) {
        this.host = host;
        this.room = room;
        this.files = this.doc.getMap('files');
        this.assets = this.doc.getMap('assets');
        this.meta = this.doc.getMap('meta');
        this.debugState = this.doc.getMap('debugState');
        this.breakpoints = this.doc.getMap('breakpoints');
        this.awareness = new Awareness(this.doc);
    }

    /** Wire everything up. Host seeds the doc; guest waits for sync. */
    async start(opts: StartOptions): Promise<void> {
        this.role = opts.role;
        this.identity = opts.identity;

        if (this.role === 'host') {
            // Seed meta first (cheap, synchronous) so guests can render the
            // "joining @ <project>" UI as soon as their sync handshake
            // lands. File seeding is async (reads OPFS) so it follows.
            this.doc.transact(() => {
                this.meta.set('hostId', this.room.selfId);
                this.meta.set('hostName', this.identity.displayName);
                this.meta.set('readOnly', false);
                if (opts.projectName) this.meta.set('projectName', opts.projectName);
            });
            await this.seedHostWorkspace();
        }

        // Stamp our local awareness state so peers can render our presence
        // immediately on connect. `peerId` is the transport-level ID
        // (room.selfId) — published here so RPC callers can look up the
        // target peer's transport ID by clientId in PeerView, without
        // each call having to re-fish it out of debugState/runState.
        this.awareness.setLocalState({
            user: { ...this.identity },
            role: this.role,
            activeFile: this.host.getActiveFileName(),
            peerId: this.room.selfId,
        });

        // Wire incoming room messages → sync/awareness handlers.
        this.unsubs.push(this.room.onMessage((peerId, bytes) => this.onWireMessage(peerId, bytes)));
        this.unsubs.push(this.room.onPeerJoin((peerId) => {
            // First real peer connected — clear the no-peers watchdog.
            this.clearConnectionWatchdog();
            this.onPeerJoin(peerId);
        }));
        this.unsubs.push(this.room.onPeerLeave((peerId) => {
            // Locate the departing peer's awareness clientID via the
            // peerId we publish in their state. Without explicitly
            // removing their awareness entry, the Yjs default keeps
            // it around for ~30s before its outlive timer fires, so
            // chips and cursors linger after a clean disconnect.
            try {
                const toRemove: number[] = [];
                for (const [clientId, state] of this.awareness.getStates()) {
                    if (clientId === this.doc.clientID) continue;
                    if ((state as any)?.peerId === peerId) toRemove.push(clientId);
                }
                if (toRemove.length > 0) {
                    awarenessProtocol.removeAwarenessStates(this.awareness, toRemove, 'peer-leave');
                }
            } catch (e) {
                console.warn('[fade-collab] onPeerLeave cleanup failed', e);
            }
            this.emitState();
        }));
        this.unsubs.push(this.room.onStatusChange(() => this.emitState()));

        // Watchdog — if no peer connects within the timeout, surface a
        // helpful banner. Hosts get a softer "still waiting" message
        // since being alone is a valid state at first; guests get a
        // harder "couldn't reach the host" since they specifically
        // expected to find someone. Skipped entirely for the mock
        // transport (BroadcastChannel sync is instantaneous when it
        // works, and same-tab tests don't need a network grace period).
        if (!this.room.selfId.startsWith('mock-')) {
            this.armConnectionWatchdog();
        }

        // Pump local Y.Doc updates to the room. Origin === this filters out
        // updates we applied ourselves (from incoming sync messages) so we
        // don't echo them back.
        const onYUpdate = (update: Uint8Array, origin: unknown) => {
            if (origin === this) return;
            const enc = encoding.createEncoder();
            encoding.writeVarUint(enc, MSG_SYNC);
            syncProtocol.writeUpdate(enc, update);
            this.room.broadcast(encoding.toUint8Array(enc));
        };
        this.doc.on('update', onYUpdate);
        this.unsubs.push(() => this.doc.off('update', onYUpdate));

        // Pump local awareness updates → room.
        const onAwarenessUpdate = (
            { added, updated, removed }: { added: number[]; updated: number[]; removed: number[] },
            origin: unknown,
        ) => {
            if (origin === 'remote') return;
            const changed = added.concat(updated).concat(removed);
            const enc = encoding.createEncoder();
            encoding.writeVarUint(enc, MSG_AWARENESS);
            encoding.writeVarUint8Array(enc, awarenessProtocol.encodeAwarenessUpdate(this.awareness, changed));
            this.room.broadcast(encoding.toUint8Array(enc));
            this.emitState();
        };
        this.awareness.on('update', onAwarenessUpdate);
        this.unsubs.push(() => this.awareness.off('update', onAwarenessUpdate));

        // Track tab switches so the binding follows the active editor.
        this.unsubs.push(this.host.onActiveFileChange((name) => {
            this.awareness.setLocalStateField('activeFile', name);
            this.rebindActiveFile();
        }));

        // Guest: when files appear/disappear in the Y.Doc, mirror them
        // into the transient OPFS project so the rest of the playground
        // (file list, LSP, Run, sharing controller) sees a "real"
        // workspace and doesn't need to know a session is in flight.
        const onFilesChange = (event: Y.YMapEvent<Y.Text>) => {
            this.applyReadOnlyToEditor();
            if (this.role === 'guest') {
                void this.handleGuestFilesEvent(event);
            }
            this.rebindActiveFile();
        };
        this.files.observe(onFilesChange);
        this.unsubs.push(() => this.files.unobserve(onFilesChange));

        // Guest: binary assets — write each new/changed entry into OPFS.
        // Assets are static-ish (no CRDT editing), so a simple "on change,
        // overwrite the file" model is fine.
        const onAssetsChange = (event: Y.YMapEvent<Uint8Array>) => {
            if (this.role !== 'guest') return;
            void this.handleGuestAssetsEvent(event);
        };
        this.assets.observe(onAssetsChange);
        this.unsubs.push(() => this.assets.unobserve(onAssetsChange));

        const onMetaChange = () => {
            this.applyReadOnlyToEditor();
            this.emitState();
        };
        this.meta.observe(onMetaChange);
        this.unsubs.push(() => this.meta.unobserve(onMetaChange));

        // Initial bind. For host this is "active file" → "existing Y.Text".
        // For guest, the active file may not exist yet — rebind will retry
        // once the file arrives via the files-observer above.
        this.rebindActiveFile();

        // Trigger initial sync handshake with any peers already in the room.
        for (const peerId of this.room.getPeers()) {
            this.sendSyncStep1(peerId);
            this.sendInitialAwareness(peerId);
        }

        this.emitState();
    }

    /** Permanently tear down the session. Idempotent. */
    async destroy(): Promise<void> {
        if (this.destroyed) return;
        this.destroyed = true;
        // Kill the connection watchdog first so it can't fire after the
        // doc/awareness are gone.
        if (this.connectionWatchdog != null) {
            clearTimeout(this.connectionWatchdog);
            this.connectionWatchdog = null;
        }
        try {
            // Awareness "removeAwarenessStates" — tell peers we're gone.
            awarenessProtocol.removeAwarenessStates(this.awareness, [this.doc.clientID], 'local');
        } catch { /* ignore */ }
        for (const u of this.unsubs) {
            try { u(); } catch { /* ignore */ }
        }
        this.unsubs.length = 0;
        // Drop per-Y.Text observers and any pending debounce timers we
        // installed for guest-side OPFS mirroring. Done before doc.destroy
        // so unobserve doesn't race a torn-down doc.
        for (const off of this.ytextUnobservers.values()) {
            try { off(); } catch { /* ignore */ }
        }
        this.ytextUnobservers.clear();
        for (const timer of this.ytextWriteTimers.values()) {
            try { clearTimeout(timer); } catch { /* ignore */ }
        }
        this.ytextWriteTimers.clear();
        this.mirroredPaths.clear();
        // Reject any RPC requests that are still in flight — without
        // this, callers' promises hang forever.
        for (const [id, pending] of this.pendingRpc) {
            if (pending.timeoutId != null) clearTimeout(pending.timeoutId);
            try { pending.reject(new Error('session destroyed before RPC response')); }
            catch { /* ignore */ }
            this.pendingRpc.delete(id);
        }
        this.rpcHandlers.clear();
        this.gameFrameCbs.clear();
        this.logLineCbs.clear();
        this.debugUiFrameCbs.clear();
        try { this.binding?.destroy(); } catch { /* ignore */ }
        this.binding = null;
        this.boundFileName = null;
        try { await this.room.leave(); } catch { /* ignore */ }
        try { this.awareness.destroy(); } catch { /* ignore */ }
        try { this.doc.destroy(); } catch { /* ignore */ }
        this.emitState();
    }

    onStateChange(cb: (s: SessionState) => void): Unsubscribe {
        this.stateCbs.add(cb);
        // Fire once on subscribe so consumers don't have to chase the
        // initial value via a separate getState call.
        cb(this.getState());
        return () => this.stateCbs.delete(cb);
    }

    getState(): SessionState {
        return {
            role: this.role,
            status: this.room.status,
            peers: this.collectPeers(),
            meta: this.readMeta(),
            sync: this.getSyncProgress(),
            connectionWarning: this.connectionWarning,
            transportNote: this.room.note ?? null,
        };
    }

    /** Current sync state replicated via `meta.sync`, or null when no
     *  sync is running. Reads straight off the Y.Doc so host + guest see
     *  the same value. */
    getSyncProgress(): SyncProgress | null {
        const raw = this.meta.get('sync');
        if (!raw || typeof raw !== 'object') return null;
        return raw as SyncProgress;
    }

    /** Host-only: snapshot the current OPFS workspace state and push it
     *  over the wire, overwriting every guest's mirror. Used when the
     *  host has made out-of-band changes (file create/delete, binary
     *  upload, etc.) and wants to reset everyone to "what's on my disk
     *  right now."
     *
     *  All peers (including the host) are forced read-only for the
     *  duration so file bytes can't shift while we're snapshotting them.
     *  Progress is replicated via `meta.sync` so each peer can render a
     *  "syncing X of N — foo.fbasic" indicator.
     *
     *  Idempotent against concurrent calls — a second invocation while
     *  the first is still running is a no-op. */
    async forceSync(): Promise<void> {
        if (this.role !== 'host') throw new Error('only the host can force a sync');
        if (this.destroyed) return;
        if (this.getSyncProgress() != null) return;  // already running

        let workspacePaths: string[];
        try { workspacePaths = await this.host.listWorkspaceFiles(); }
        catch (e) {
            console.warn('[fade-collab] forceSync listWorkspaceFiles failed', e);
            return;
        }
        // What's in the Y.Doc right now that ISN'T on the host's disk
        // anymore? Those are orphans — delete from Y.Doc so guests drop
        // them from their mirror.
        const liveSet = new Set(workspacePaths);
        const toDelete: string[] = [];
        for (const p of this.files.keys()) if (!liveSet.has(p)) toDelete.push(p);
        for (const p of this.assets.keys()) if (!liveSet.has(p)) toDelete.push(p);

        const total = workspacePaths.length + toDelete.length;
        const started = Date.now();
        const initiatorClientId = this.doc.clientID;

        // Set the initial progress in a transaction so the read-only lock
        // and the visible progress UI flip on together for everyone.
        this.meta.set('sync', {
            started, total, completed: 0, initiatorClientId,
        } satisfies SyncProgress);

        let completed = 0;
        // Throttle the meta.set('sync', …) writes — without this, syncing
        // 100 files fires 100 Y.Doc updates, each broadcast over the
        // wire AND each fanned out to every state subscriber (chip
        // render, panel render). Once-per-frame is enough resolution for
        // a progress bar. Last-write-wins is enforced by `setProgress`
        // always reading the current `completed` counter.
        let lastEmit = 0;
        const PROGRESS_MIN_INTERVAL = 100; // ms
        const setProgress = (currentFile?: string, force = false) => {
            const now = Date.now();
            if (!force && now - lastEmit < PROGRESS_MIN_INTERVAL) return;
            lastEmit = now;
            this.meta.set('sync', {
                started, total, completed, initiatorClientId, currentFile,
            } satisfies SyncProgress);
        };

        try {
            for (const path of workspacePaths) {
                if (this.destroyed) return;
                try {
                    if (this.host.isBinaryPath(path)) {
                        const bytes = await this.host.readWorkspaceBytes(path);
                        // Skip the network round-trip if the bytes match what
                        // we already have. Length check first (cheap), then
                        // a byte-by-byte compare for plausible matches.
                        const existing = this.assets.get(path);
                        if (!existing || !uint8ArraysEqual(existing, bytes)) {
                            this.assets.set(path, bytes);
                        }
                    } else {
                        const text = await this.host.readWorkspaceText(path);
                        const existing = this.files.get(path);
                        if (!existing) {
                            const ytext = new Y.Text();
                            ytext.insert(0, text);
                            this.files.set(path, ytext);
                        } else if (existing.toString() !== text) {
                            // Replace content in one transaction so guests
                            // and our own y-monaco binding apply a single
                            // delta instead of an empty-then-fill flicker.
                            this.doc.transact(() => {
                                existing.delete(0, existing.length);
                                existing.insert(0, text);
                            });
                        }
                    }
                } catch (e) {
                    console.warn('[fade-collab] forceSync failed for', path, e);
                }
                completed++;
                setProgress(path);
            }
            for (const path of toDelete) {
                if (this.destroyed) return;
                if (this.files.has(path)) this.files.delete(path);
                if (this.assets.has(path)) this.assets.delete(path);
                completed++;
                setProgress(path);
            }
            // Force the final progress emit so the UI always shows 100%
            // before the lock lifts — otherwise the throttle might
            // suppress the last update.
            setProgress(undefined, /*force*/ true);
        } finally {
            // Clear the lock + progress whether we finished cleanly or
            // bailed mid-stream. Without this any failure leaves everyone
            // permanently read-only.
            this.meta.set('sync', null);
        }
    }

    /** Host-only: toggle the room into / out of read-only mode. Guests
     *  observe this via the meta map and apply Monaco's `readOnly` option
     *  to their editor. Mode A (cosmetic) — see plan in conversation. */
    setReadOnly(readOnly: boolean): void {
        if (this.role !== 'host') return;
        this.meta.set('readOnly', readOnly);
    }

    isReadOnly(): boolean {
        return Boolean(this.meta.get('readOnly'));
    }

    /** Host-only: a tab was just opened locally — make sure it's in the
     *  Y.Doc so guests see it. Called from the tab-open path in main.ts. */
    notifyFileOpened(name: string): void {
        if (this.role !== 'host') return;
        if (this.files.has(name)) return;
        const model = this.host.getModelForFile(name);
        if (!model) return;
        const text = new Y.Text();
        text.insert(0, model.getValue());
        this.files.set(name, text);
    }

    /** Host-only: a tab was just closed locally. Intentionally a no-op
     *  for the Y.Doc — closing a tab on the host doesn't mean the file is
     *  gone from their workspace, and the whole workspace is what guests
     *  mirror. File DELETIONS happen via the workspace file-tree path,
     *  which is a separate hook (TODO when delete actions are surfaced
     *  to the live-session feature). Kept for symmetry with
     *  `notifyFileOpened` so the call sites can stay matched. */
    notifyFileClosed(_name: string): void {
        // Reserved for future per-tab presence updates.
    }

    /** Host-only: a workspace file was deleted (via the file tree's delete
     *  action, rename, etc.). Pull it from the Y.Doc + assets so guests
     *  drop their copy too. */
    notifyFileDeleted(path: string): void {
        if (this.role !== 'host') return;
        if (this.files.has(path)) this.files.delete(path);
        if (this.assets.has(path)) this.assets.delete(path);
    }

    // ── private ────────────────────────────────────────────────────────

    private collectPeers(): PeerView[] {
        const out: PeerView[] = [];
        const states = this.awareness.getStates();
        for (const [clientId, state] of states.entries()) {
            const user = (state as any)?.user as PeerIdentity | undefined;
            if (!user) continue;
            out.push({
                clientId,
                peerId: ((state as any)?.peerId as string | undefined) ?? null,
                isSelf: clientId === this.doc.clientID,
                identity: user,
                role: ((state as any)?.role as SessionRole) ?? 'guest',
                activeFile: ((state as any)?.activeFile as string | null) ?? null,
                activity: ((state as any)?.activity as PeerActivity) ?? 'idle',
                focus: sanitizeFocus((state as any)?.focus),
            });
        }
        return out;
    }

    /** Broadcast that this peer just started (or stopped) a runtime
     *  activity — running the program, debugging, running tests, etc.
     *  Travels via awareness so all peers' UIs can label the relevant
     *  participant ("Alice is debugging") without waiting for actual
     *  frame / debug-state streaming to land. */
    /** Update our broadcast cursor focus — see PeerFocus. Pass null to
     *  clear (e.g. mouseleave from shared surfaces, browser blur). The
     *  awareness machinery diffs against the previous value so writing
     *  the same value repeatedly is essentially free. */
    setFocus(focus: PeerFocus): void {
        this.awareness.setLocalStateField('focus', focus);
    }

    setActivity(activity: PeerActivity): void {
        this.awareness.setLocalStateField('activity', activity);
    }

    // ── Game frame broadcast (Phase 2A) ──────────────────────────────────

    /** Broadcast a game-frame snapshot to every peer in the room. Used
     *  by the peer currently running the program to share their canvas
     *  view at low FPS. Bytes should be a compressed image (JPEG/WebP) —
     *  the session doesn't care about the format, just ships them. */
    sendGameFrame(bytes: Uint8Array): void {
        if (this.destroyed) return;
        const enc = encoding.createEncoder();
        encoding.writeVarUint(enc, MSG_GAMEFRAME);
        encoding.writeVarUint8Array(enc, bytes);
        this.room.broadcast(encoding.toUint8Array(enc));
    }

    /** Subscribe to incoming game frames from any peer. The handler runs
     *  for every frame; consumers throttle / debounce / drop their own
     *  rendering as needed. */
    onGameFrame(cb: (peerId: string, bytes: Uint8Array) => void): Unsubscribe {
        this.gameFrameCbs.add(cb);
        return () => this.gameFrameCbs.delete(cb);
    }

    /** Broadcast a program-output line to every peer. Used for sharing
     *  `print` / stdout / stderr from the host's running program so
     *  observers see the same output in their Logs panel. */
    sendLogLine(line: SessionLogLine): void {
        if (this.destroyed) return;
        const enc = encoding.createEncoder();
        encoding.writeVarUint(enc, MSG_LOG_LINE);
        encoding.writeVarString(enc, line.channel);
        encoding.writeVarString(enc, line.level);
        encoding.writeVarString(enc, line.message);
        this.room.broadcast(encoding.toUint8Array(enc));
    }

    /** Subscribe to log lines broadcast by any peer. Returns an unsub fn. */
    onLogLine(cb: (peerId: string, line: SessionLogLine) => void): Unsubscribe {
        this.logLineCbs.add(cb);
        return () => this.logLineCbs.delete(cb);
    }

    /** Broadcast a Debug UI envelope (JSON-encoded) to every peer. The
     *  host calls this on every iframe-emitted `debug-ui-frame` so
     *  observers can mirror the Tweakpane panel state. Payload is the
     *  envelope JSON; we ship it as a string rather than parsing here so
     *  observers can apply via their existing
     *  `applyFrameEnvelope(parseDebugUiEnvelope(json))` pipeline without
     *  this layer caring about the shape. */
    sendDebugUiFrame(json: string): void {
        if (this.destroyed) return;
        const enc = encoding.createEncoder();
        encoding.writeVarUint(enc, MSG_DEBUG_UI_FRAME);
        encoding.writeVarString(enc, json);
        this.room.broadcast(encoding.toUint8Array(enc));
    }

    /** Subscribe to relayed Debug UI envelopes. Receives the raw JSON
     *  string — caller parses with whatever envelope parser they have.
     *  Returns an unsub fn. */
    onDebugUiFrame(cb: (peerId: string, json: string) => void): Unsubscribe {
        this.debugUiFrameCbs.add(cb);
        return () => this.debugUiFrameCbs.delete(cb);
    }

    // ── Peer-to-peer RPC (Phase 2C foundation) ────────────────────────────

    /** Register a handler for incoming RPC requests on `channel`. The
     *  handler can be async; whatever it returns (or throws) is shipped
     *  back to the requester. Only ONE handler per channel — re-registering
     *  replaces the previous one. */
    onRequest(
        channel: string,
        handler: (peerId: string, payload: unknown) => unknown | Promise<unknown>,
    ): Unsubscribe {
        this.rpcHandlers.set(channel, handler);
        return () => {
            if (this.rpcHandlers.get(channel) === handler) {
                this.rpcHandlers.delete(channel);
            }
        };
    }

    /** Send a request to `peerId` on `channel` and await their response.
     *  `payload` and the return value go through `JSON.stringify` —
     *  keep them to JSON-safe shapes (no functions, no Uint8Array). Use
     *  `sendGameFrame` for binary payloads. Default timeout 10 s. */
    request(
        peerId: string,
        channel: string,
        payload: unknown,
        opts: { timeoutMs?: number } = {},
    ): Promise<unknown> {
        if (this.destroyed) return Promise.reject(new Error('session destroyed'));
        const timeoutMs = opts.timeoutMs ?? 10_000;
        // Encode the correlation ID as (clientID-low-bits << 24) | local
        // counter so concurrent in/out requests from this peer and the
        // remote peer can't collide on the same numeric ID.
        const localId = ++this.rpcCounter & 0xffffff;
        const correlationId = ((this.doc.clientID & 0xff) << 24) | localId;
        const enc = encoding.createEncoder();
        encoding.writeVarUint(enc, MSG_RPC_REQUEST);
        encoding.writeVarUint(enc, correlationId);
        // Target peer ID is now embedded in the message body so we can
        // dispatch via room.broadcast() instead of room.sendTo(). In the
        // field, Trystero's per-peer addressing has been observed to hang
        // while broadcasts arrive normally — observers can see the host's
        // Y.Map updates but session.request() to that same peer times
        // out. Broadcasting the RPC request + filtering at the receiver
        // (and broadcasting the response too — correlationId already
        // disambiguates) sidesteps the unicast path entirely.
        encoding.writeVarString(enc, peerId);
        encoding.writeVarString(enc, channel);
        encoding.writeVarString(enc, safeJsonStringify(payload));
        return new Promise<unknown>((resolve, reject) => {
            const timeoutId = setTimeout(() => {
                if (this.pendingRpc.has(correlationId)) {
                    this.pendingRpc.delete(correlationId);
                    reject(new Error(`RPC '${channel}' to ${peerId} timed out after ${timeoutMs}ms`));
                }
            }, timeoutMs);
            this.pendingRpc.set(correlationId, { resolve, reject, timeoutId });
            try { this.room.broadcast(encoding.toUint8Array(enc)); }
            catch (e) {
                this.pendingRpc.delete(correlationId);
                clearTimeout(timeoutId);
                reject(e as Error);
            }
        });
    }

    // ── Debug state replication (Phase 2B) ────────────────────────────────

    /** Convenience for the host: stash a debug-state update in the Y.Map.
     *  Observers see the change via `debugState.observe(...)` and can
     *  re-render their UI to match. Callers are responsible for naming
     *  consistency (paused, currentFile, currentLine, callStack, locals).
     *  Pass `null` to clear a key. */
    setDebugState(updates: Record<string, unknown>): void {
        if (this.destroyed) return;
        this.doc.transact(() => {
            for (const [k, v] of Object.entries(updates)) {
                if (v === undefined) continue;
                if (v === null) this.debugState.delete(k);
                else this.debugState.set(k, v);
            }
        });
    }

    /** Wipe every key in debugState. Called when the debug session ends. */
    clearDebugState(): void {
        if (this.destroyed) return;
        if (this.debugState.size === 0) return;
        this.doc.transact(() => {
            const keys = Array.from(this.debugState.keys());
            for (const k of keys) this.debugState.delete(k);
        });
    }

    private readMeta(): SessionMeta | null {
        const hostId = this.meta.get('hostId');
        if (!hostId) return null;
        return {
            hostId: String(hostId),
            hostName: String(this.meta.get('hostName') ?? ''),
            readOnly: Boolean(this.meta.get('readOnly')),
            projectName: this.meta.get('projectName') ?? undefined,
        };
    }

    private emitState() {
        // Coalesce — multiple events in the same tick (a cursor move
        // firing both awareness and meta paths, say) become a single
        // notification. Without this, every keystroke fans out into
        // chip renders and DOM work on the main thread.
        if (this.emitStateScheduled) return;
        this.emitStateScheduled = true;
        queueMicrotask(() => {
            this.emitStateScheduled = false;
            if (this.destroyed) return;
            const s = this.getState();
            for (const cb of this.stateCbs) {
                try { cb(s); } catch { /* ignore listener errors */ }
            }
        });
    }

    private applyReadOnlyToEditor() {
        // Two ways the editor goes read-only:
        //   1. Mode A "host toggled read-only for guests" — guests only.
        //   2. Force-sync in flight — EVERYONE (host included), so the
        //      file bytes can't shift under the snapshot loop.
        // Mode A is cosmetic — a determined guest can flip Monaco's
        // option back in DevTools — but their edits still flow into the
        // Y.Doc, so a future Mode B (host drops incoming guest updates
        // while readOnly) is the stronger version.
        const guestReadOnly = this.role === 'guest' && this.isReadOnly();
        const syncing = this.getSyncProgress() != null;
        const shouldReadOnly = syncing || guestReadOnly;
        // Awareness updates fire on every cursor move and route through
        // emitState → applyReadOnlyToEditor; calling Monaco's
        // updateOptions on every one of those is unnecessary churn that
        // shows up as jank during typing. Only push when the flag
        // actually transitions.
        if (this.lastAppliedReadOnly === shouldReadOnly) return;
        this.lastAppliedReadOnly = shouldReadOnly;
        try { this.host.editor.updateOptions({ readOnly: shouldReadOnly }); }
        catch { /* editor may be disposed */ }
    }

    /** Host side: walk the workspace once at session start and seed the
     *  `files` (text) + `assets` (binary) Y.Maps. Each text file gets its
     *  own Y.Text so guests can collaboratively edit it; binaries land in
     *  the simple bytes map (no CRDT semantics needed). */
    private async seedHostWorkspace(): Promise<void> {
        let paths: string[];
        try { paths = await this.host.listWorkspaceFiles(); }
        catch (e) {
            console.warn('[fade-collab] listWorkspaceFiles failed', e);
            return;
        }
        // Each read is async; do them in parallel and apply via a single
        // Y.Doc transaction at the end so peers see one batched update.
        const textEntries: Array<[string, string]> = [];
        const binaryEntries: Array<[string, Uint8Array]> = [];
        await Promise.all(paths.map(async (p) => {
            try {
                if (this.host.isBinaryPath(p)) {
                    const bytes = await this.host.readWorkspaceBytes(p);
                    binaryEntries.push([p, bytes]);
                } else {
                    const text = await this.host.readWorkspaceText(p);
                    textEntries.push([p, text]);
                }
            } catch (e) {
                console.warn('[fade-collab] failed to read', p, e);
            }
        }));
        this.doc.transact(() => {
            for (const [p, text] of textEntries) {
                if (this.files.has(p)) continue;
                const ytext = new Y.Text();
                ytext.insert(0, text);
                this.files.set(p, ytext);
            }
            for (const [p, bytes] of binaryEntries) {
                if (this.assets.has(p)) continue;
                this.assets.set(p, bytes);
            }
        });
    }

    /** Guest side: react to additions/removals in the `files` map. Adds get
     *  written to OPFS as the initial mirror; removals delete from OPFS and
     *  close the tab. */
    private async handleGuestFilesEvent(event: Y.YMapEvent<Y.Text>): Promise<void> {
        const adds: string[] = [];
        const dels: string[] = [];
        for (const [path, change] of event.changes.keys) {
            if (change.action === 'add') adds.push(path);
            else if (change.action === 'delete') dels.push(path);
            else if (change.action === 'update') adds.push(path);
        }
        for (const path of dels) {
            this.unobserveYText(path);
            this.mirroredPaths.delete(path);
            try { await this.host.deleteWorkspaceFile(path); }
            catch (e) { console.warn('[fade-collab] guest deleteWorkspaceFile failed for', path, e); }
            try { await this.host.closeFile(path); }
            catch (e) { console.warn('[fade-collab] guest closeFile failed for', path, e); }
        }
        for (const path of adds) {
            const ytext = this.files.get(path);
            if (!ytext) continue;
            await this.mirrorYTextToOpfs(path, ytext);
            this.installYTextObserver(path, ytext);
        }
        if (adds.length || dels.length) {
            try { await this.host.refreshFileList(); }
            catch { /* ignore */ }
            try { await this.host.refreshProjectConfig?.(); }
            catch (e) { console.warn('[fade-collab] refreshProjectConfig failed', e); }
        }
    }

    /** Guest side: react to changes in the `assets` map. Each entry is
     *  overwritten on every change (binaries are small enough in this
     *  codebase that a full rewrite per change is fine). */
    private async handleGuestAssetsEvent(event: Y.YMapEvent<Uint8Array>): Promise<void> {
        const adds: string[] = [];
        const dels: string[] = [];
        for (const [path, change] of event.changes.keys) {
            if (change.action === 'add' || change.action === 'update') adds.push(path);
            else if (change.action === 'delete') dels.push(path);
        }
        for (const path of dels) {
            try { await this.host.deleteWorkspaceFile(path); }
            catch (e) { console.warn('[fade-collab] guest deleteWorkspaceFile failed for', path, e); }
        }
        for (const path of adds) {
            const bytes = this.assets.get(path);
            if (!bytes) continue;
            try { await this.host.writeWorkspaceBytes(path, bytes); }
            catch (e) { console.warn('[fade-collab] guest writeWorkspaceBytes failed for', path, e); }
        }
        if (adds.length || dels.length) {
            try { await this.host.refreshFileList(); }
            catch { /* ignore */ }
            try { await this.host.refreshProjectConfig?.(); }
            catch (e) { console.warn('[fade-collab] refreshProjectConfig failed', e); }
        }
    }

    /** Write Y.Text content into OPFS once on first arrival. The per-file
     *  observer set up by `installYTextObserver` keeps it in sync from
     *  there. */
    private async mirrorYTextToOpfs(path: string, ytext: Y.Text): Promise<void> {
        try {
            await this.host.writeWorkspaceText(path, ytext.toString());
            this.mirroredPaths.add(path);
        } catch (e) {
            console.warn('[fade-collab] guest writeWorkspaceText failed for', path, e);
        }
    }

    /** Per-Y.Text observer for guests. When the host (or another guest)
     *  edits a file the local guest doesn't currently have open, we still
     *  need to keep OPFS in sync so a future Run/LSP/file-tree action sees
     *  fresh bytes. Debounced so a fast typist doesn't fan out into N OPFS
     *  writes per second. */
    private installYTextObserver(path: string, ytext: Y.Text): void {
        // Skip if we already have one for this path.
        if (this.ytextUnobservers.has(path)) return;
        const cb = () => {
            // Skip writes for the currently-bound file — the y-monaco
            // binding feeds the Monaco model, and the existing autosave
            // loop in main.ts persists model.getValue() to OPFS on a
            // 600ms debounce. Writing here too would race that path.
            if (this.boundFileName === path) return;
            const prev = this.ytextWriteTimers.get(path);
            if (prev != null) clearTimeout(prev);
            // Use the global setTimeout (not window.setTimeout) so this
            // works in both browser and node test environments. The
            // returned id is a number in DOM and a Timeout in Node;
            // either way clearTimeout accepts it.
            const timer = setTimeout(() => {
                this.ytextWriteTimers.delete(path);
                this.host.writeWorkspaceText(path, ytext.toString()).catch((e) => {
                    console.warn('[fade-collab] guest debounced write failed for', path, e);
                });
            }, 600) as unknown as number;
            this.ytextWriteTimers.set(path, timer);
        };
        ytext.observe(cb);
        this.ytextUnobservers.set(path, () => ytext.unobserve(cb));
    }

    private unobserveYText(path: string): void {
        const off = this.ytextUnobservers.get(path);
        if (off) {
            try { off(); } catch { /* ignore */ }
            this.ytextUnobservers.delete(path);
        }
        const timer = this.ytextWriteTimers.get(path);
        if (timer != null) {
            clearTimeout(timer);
            this.ytextWriteTimers.delete(path);
        }
    }

    private rebindActiveFile() {
        if (this.destroyed) return;
        const name = this.host.getActiveFileName();
        // Already bound to the right file? Nothing to do.
        if (name === this.boundFileName && this.binding) return;
        // Already in flight for this same file (the y-monaco import is
        // resolving)? Don't queue another import + doBind — that's what
        // produced the doubled MonacoBinding on the same model: the
        // host's own `this.files.set(name, ytext)` below fires
        // files.observe, which calls rebindActiveFile again, which races
        // the outer pending import.
        if (this.bindingRequestedFor === name) return;
        this.bindingRequestedFor = name;

        if (this.binding) {
            try { this.binding.destroy(); } catch { /* ignore */ }
            this.binding = null;
        }
        this.boundFileName = null;
        if (!name) return;

        const model = this.host.getModelForFile(name);
        if (!model) return;

        let ytext = this.files.get(name);
        if (!ytext) {
            if (this.role === 'host') {
                ytext = new Y.Text();
                ytext.insert(0, model.getValue());
                // This .set fires files.observe → rebindActiveFile, but
                // the guard above means it's a no-op since
                // bindingRequestedFor === name.
                this.files.set(name, ytext);
            } else {
                // Guest is on a file that isn't in the session doc — let
                // them edit locally with no binding. The files-observer
                // will rebind once the file arrives via sync.
                return;
            }
        }

        const doBind = (Ctor: typeof MonacoBindingType) => {
            // Active may have changed during the async import; abort if so.
            if (this.bindingRequestedFor !== name) return;
            if (this.destroyed) return;
            try {
                this.binding = new Ctor(
                    ytext!,
                    model,
                    new Set([this.host.editor]),
                    this.awareness,
                );
                this.boundFileName = name;
                this.applyReadOnlyToEditor();
            } catch (e) {
                console.error('[fade-collab] MonacoBinding failed', e);
            }
        };
        if (this.monacoBindingCtor) {
            doBind(this.monacoBindingCtor);
            return;
        }
        void import('y-monaco').then((mod) => {
            if (this.destroyed) return;
            this.monacoBindingCtor = mod.MonacoBinding;
            doBind(mod.MonacoBinding);
        }).catch((e) => {
            console.error('[fade-collab] failed to load y-monaco', e);
        });
    }

    private onWireMessage(peerId: string, bytes: Uint8Array): void {
        try {
            const dec = decoding.createDecoder(bytes);
            const messageType = decoding.readVarUint(dec);
            switch (messageType) {
                case MSG_SYNC: {
                    const enc = encoding.createEncoder();
                    encoding.writeVarUint(enc, MSG_SYNC);
                    const reply = syncProtocol.readSyncMessage(dec, enc, this.doc, this);
                    if (encoding.length(enc) > 1) {
                        // length 1 = only the type byte → no payload to reply with
                        this.room.sendTo(peerId, encoding.toUint8Array(enc));
                    }
                    void reply;
                    break;
                }
                case MSG_AWARENESS: {
                    awarenessProtocol.applyAwarenessUpdate(
                        this.awareness,
                        decoding.readVarUint8Array(dec),
                        'remote',
                    );
                    break;
                }
                case MSG_QUERY_AWARENESS: {
                    const enc = encoding.createEncoder();
                    encoding.writeVarUint(enc, MSG_AWARENESS);
                    encoding.writeVarUint8Array(
                        enc,
                        awarenessProtocol.encodeAwarenessUpdate(
                            this.awareness,
                            Array.from(this.awareness.getStates().keys()),
                        ),
                    );
                    this.room.sendTo(peerId, encoding.toUint8Array(enc));
                    break;
                }
                case MSG_GAMEFRAME: {
                    const frame = decoding.readVarUint8Array(dec);
                    for (const cb of this.gameFrameCbs) {
                        try { cb(peerId, frame); }
                        catch (e) { console.warn('[fade-collab] gameFrame listener threw', e); }
                    }
                    break;
                }
                case MSG_LOG_LINE: {
                    const channel = decoding.readVarString(dec);
                    const level = decoding.readVarString(dec) as SessionLogLine['level'];
                    const message = decoding.readVarString(dec);
                    const line: SessionLogLine = { channel, level, message };
                    for (const cb of this.logLineCbs) {
                        try { cb(peerId, line); }
                        catch (e) { console.warn('[fade-collab] logLine listener threw', e); }
                    }
                    break;
                }
                case MSG_DEBUG_UI_FRAME: {
                    const json = decoding.readVarString(dec);
                    for (const cb of this.debugUiFrameCbs) {
                        try { cb(peerId, json); }
                        catch (e) { console.warn('[fade-collab] debugUiFrame listener threw', e); }
                    }
                    break;
                }
                case MSG_RPC_REQUEST: {
                    const correlationId = decoding.readVarUint(dec);
                    const targetPeerId = decoding.readVarString(dec);
                    const channel = decoding.readVarString(dec);
                    const payload = safeJsonParse(decoding.readVarString(dec));
                    // RPC requests are sent via broadcast (workaround for
                    // a Trystero unicast issue — see request() above).
                    // Every peer in the room receives every RPC; only the
                    // peer whose selfId matches `targetPeerId` should
                    // dispatch the handler. The others ignore.
                    if (targetPeerId !== this.room.selfId) break;
                    const handler = this.rpcHandlers.get(channel);
                    const respond = (ok: boolean, result: unknown) => {
                        const enc = encoding.createEncoder();
                        encoding.writeVarUint(enc, MSG_RPC_RESPONSE);
                        encoding.writeVarUint(enc, correlationId);
                        encoding.writeVarUint(enc, ok ? 1 : 0);
                        encoding.writeVarString(enc, safeJsonStringify(result));
                        // Responses also go via broadcast — same reason.
                        // The originator's correlationId is unique
                        // enough that other peers will simply have no
                        // matching pendingRpc entry and ignore the
                        // message.
                        try { this.room.broadcast(encoding.toUint8Array(enc)); }
                        catch (e) { console.warn('[fade-collab] failed to send RPC response', e); }
                    };
                    if (!handler) {
                        respond(false, `no handler registered for channel "${channel}"`);
                        break;
                    }
                    // Handler may be sync or async. Both end up sending a
                    // response; uncaught throws become error responses.
                    try {
                        const out = handler(peerId, payload);
                        Promise.resolve(out)
                            .then((value) => respond(true, value))
                            .catch((e: unknown) => respond(false, e instanceof Error ? e.message : String(e)));
                    } catch (e) {
                        respond(false, e instanceof Error ? e.message : String(e));
                    }
                    break;
                }
                case MSG_RPC_RESPONSE: {
                    const correlationId = decoding.readVarUint(dec);
                    const ok = decoding.readVarUint(dec) === 1;
                    const result = safeJsonParse(decoding.readVarString(dec));
                    const pending = this.pendingRpc.get(correlationId);
                    if (!pending) {
                        // Either we already timed out or someone replied
                        // to a request we never issued. Nothing to do.
                        break;
                    }
                    this.pendingRpc.delete(correlationId);
                    if (pending.timeoutId != null) clearTimeout(pending.timeoutId);
                    if (ok) pending.resolve(result);
                    else pending.reject(new Error(typeof result === 'string' ? result : 'RPC failed'));
                    break;
                }
                default:
                    console.warn('[fade-collab] unknown message type', messageType);
            }
        } catch (e) {
            console.warn('[fade-collab] message handler failed', e);
        }
    }

    private onPeerJoin(peerId: string): void {
        this.sendSyncStep1(peerId);
        this.sendInitialAwareness(peerId);
        this.emitState();
    }

    /** Watchdog timer that flips `connectionWarning` if no peer is
     *  reachable after the grace period. The host's message is softer
     *  (could legitimately be alone) and longer (waiting for guests is
     *  normal); the guest's is sharper because joining a room and seeing
     *  nobody means the host is gone or NAT traversal failed. */
    private armConnectionWatchdog(): void {
        this.clearConnectionWatchdog();
        // Hosts: 30s without any peer connecting is enough to suspect
        // something's off (typically: nobody's tried to join yet OR
        // TURN is failing for whoever did try). Originally 60s but that
        // was way too patient — the host stares at a "discovering" pill
        // with no idea whether they should poke their invitee, switch
        // networks, or just keep waiting.
        // Guests: 25s without finding the host strongly suggests something
        // is wrong — wrong room ID, host left, or ICE traversal failed.
        const ms = this.role === 'host' ? 30_000 : 25_000;
        this.connectionWatchdog = setTimeout(() => {
            this.connectionWatchdog = null;
            if (this.destroyed) return;
            if (this.room.getPeers().length > 0) return; // raced — peer arrived
            this.connectionWarning = this.role === 'host'
                ? 'No one has joined yet. If you\'ve already shared the link, ICE may be failing on their end — check the browser console for "Ice connection failed" errors and consider trying a different network.'
                : 'Couldn\'t reach the host. They may have left, the share code may be wrong, or your network is blocking WebRTC. Try leaving and rejoining, or switch networks.';
            this.emitState();
        }, ms);
    }

    private clearConnectionWatchdog(): void {
        if (this.connectionWatchdog != null) {
            clearTimeout(this.connectionWatchdog);
            this.connectionWatchdog = null;
        }
        if (this.connectionWarning != null) {
            this.connectionWarning = null;
            this.emitState();
        }
    }

    private sendSyncStep1(peerId: string): void {
        const enc = encoding.createEncoder();
        encoding.writeVarUint(enc, MSG_SYNC);
        syncProtocol.writeSyncStep1(enc, this.doc);
        this.room.sendTo(peerId, encoding.toUint8Array(enc));
    }

    private sendInitialAwareness(peerId: string): void {
        // Send our entire awareness state to the new peer in one shot so
        // they know about everyone we know about. They'll learn about us
        // independently via their own peers and the on('update') handler.
        const clientIds = Array.from(this.awareness.getStates().keys());
        if (clientIds.length === 0) return;
        const enc = encoding.createEncoder();
        encoding.writeVarUint(enc, MSG_AWARENESS);
        encoding.writeVarUint8Array(enc, awarenessProtocol.encodeAwarenessUpdate(this.awareness, clientIds));
        this.room.sendTo(peerId, encoding.toUint8Array(enc));
    }
}

/** Tight-loop byte compare used by `forceSync` to skip uploads of binary
 *  assets that already match what's in the Y.Doc. Worth the cost vs.
 *  blind-re-set because asset replication ships the whole byte buffer
 *  through the data channel. */
function uint8ArraysEqual(a: Uint8Array, b: Uint8Array): boolean {
    if (a.byteLength !== b.byteLength) return false;
    for (let i = 0; i < a.byteLength; i++) if (a[i] !== b[i]) return false;
    return true;
}

/** JSON.stringify guarded against non-serialisable inputs. Used as the
 *  wire format for RPC payloads + responses — keep payloads simple. */
function safeJsonStringify(value: unknown): string {
    try { return JSON.stringify(value ?? null); }
    catch { return 'null'; }
}

/** JSON.parse that returns `null` on bad input instead of throwing. */
function safeJsonParse(s: string): unknown {
    if (!s) return null;
    try { return JSON.parse(s); }
    catch { return null; }
}

/** Validate an incoming awareness focus payload — peers can send
 *  anything, so we defensively normalise the shape before exposing it
 *  to receivers. Returns null for anything malformed. */
function sanitizeFocus(raw: unknown): PeerFocus {
    if (raw == null || typeof raw !== 'object') return null;
    const r = raw as Record<string, unknown>;
    const ts = typeof r.ts === 'number' ? r.ts : 0;
    if (r.scope === 'game') {
        const nx = typeof r.nx === 'number' ? r.nx : NaN;
        const ny = typeof r.ny === 'number' ? r.ny : NaN;
        if (!Number.isFinite(nx) || !Number.isFinite(ny)) return null;
        return { scope: 'game', nx, ny, ts };
    }
    if (r.scope === 'editor' && typeof r.file === 'string') {
        const line = typeof r.line === 'number' ? r.line : NaN;
        const column = typeof r.column === 'number' ? r.column : NaN;
        const dx = typeof r.dx === 'number' ? r.dx : 0;
        const dy = typeof r.dy === 'number' ? r.dy : 0;
        if (!Number.isFinite(line) || !Number.isFinite(column)) return null;
        return { scope: 'editor', file: r.file, line, column, dx, dy, ts };
    }
    return null;
}

