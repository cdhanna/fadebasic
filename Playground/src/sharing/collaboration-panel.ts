// The Collaboration dockview panel — every share/save/publish/pull
// affordance for the active workspace.
//
// State machine (rendered top-to-bottom):
//
//   1. Not signed in        → "Sign in to GitHub" button (opens PAT dialog)
//   2. Signed in, no repo   → "Publish to GitHub" + "Connect to existing repo"
//   3. Connected            → repo link, unsaved changes, save action,
//                              publish action + preview, conflicts, pull box
//
// Lives in a single DOM root. `mountCollaboration(...)` returns a small
// controller the host can use to:
//   - tell the panel which project is active (project switches)
//   - tell the panel to re-read the working tree (after edits, autosave flush)
//   - subscribe to status changes for file-list badging
//
// All state is held in module-level closure vars per instance — one panel per
// playground window, like every other dockview component here.

import { GitHubAdapter, GitHubApiError, type CreateRepoOptions } from './github-adapter';
import { Repo, type ProgressFn, type ProgressEvent } from './repo';
import { getLogger } from '../log-bus';
import { OpfsWorkingTree, isHiddenFromCommits, type OpfsWorkspaceLike } from './opfs-working-tree';
import { gitBlobSha } from './hash';
import { diffGitTrees, type GitTree, type TreeDiff } from './git-types';
import { diff3Merge, hasConflictMarkers } from './diff3';
import {
    isConnected,
    loadSyncIndex,
    saveSyncIndex,
    type ProjectSyncIndex,
} from './sync-index';
import { computeStatus, HashCache, statusGlyph, type FileStatus, type FileStatusEntry } from './file-status';
import { clearSaves, createSave, dropSave, loadSaves, migrateLegacyLocalStorageSaves, revertToSave, upgradeSave, type LocalSave } from './local-saves';
import { openSignInDialog } from './auth-ui';
import {
    refreshAccessToken,
    validateToken,
    type ValidatedToken,
} from './github-auth';
import { GITHUB_APP_CLIENT_ID } from './github-auth-config';
import {
    SessionTokenStore,
    isAccessExpired,
    isRefreshUsable,
    tokenSetToStored,
} from './token-store';
import { HeadConflictError } from './adapter';

const CSS_PREFIX = 'fade-collab';
const STYLE_ID = `${CSS_PREFIX}-styles`;

export interface SharingCommitInfo {
    /** Git commit SHA. */
    id: string;
    /** First-parent SHA, or null for the root commit. */
    parent: string | null;
    message: string;
    author: string;
    /** ISO-8601 timestamp string from the git commit object. */
    time: string;
}

export interface CollaborationController {
    /** Tell the panel which project is now active. Triggers a re-render and a status refresh. */
    setActiveProject(projectName: string): void;
    /** Re-read the working tree and refresh staged-changes / badges. Hits
     *  the per-path hash cache, so files that haven't been invalidated
     *  since the last refresh are essentially free. */
    refreshStatus(): Promise<void>;
    /** Lightweight refresh for the autosave hot path: invalidates the
     *  cached hash for one path so the next pass re-reads only that file.
     *  Use when an external writer (autosave, restore-from-tab, etc.)
     *  modified the file outside the panel's own actions. */
    refreshStatusForFile(path: string): Promise<void>;
    /** Drop the cached hash for one path WITHOUT triggering a refresh.
     *  Used by `flushPendingSaves` so a single bulk flush doesn't fan out
     *  into N status passes — the immediate next `refreshStatus` (run by
     *  doSave/doPublish/etc.) picks up the fresh bytes. */
    invalidateHashFor(path: string): void;
    /** Host tells us whether there are dirty Monaco buffers waiting on
     *  the debounced autosave. Used by the Save button to enable
     *  instantly when the user types — without this it would stay greyed
     *  out for ~600 ms because `staged` is computed from OPFS bytes that
     *  haven't been written yet. */
    setHasDirtyTabs(hasDirty: boolean): void;
    /** Subscribe to status changes — fires whenever the per-file status map updates. */
    onStatusChange(listener: (map: Map<string, FileStatus>) => void): () => void;
    /** Current per-file status map. Always non-null; empty when not connected. */
    getStatusMap(): Map<string, FileStatus>;
    /** Subscribe to changes in the "remote has changes for these paths" set.
     *  Fires whenever polling detects the branch moved and computes which
     *  paths differ from what we last synced. */
    onPendingPullChange(listener: (paths: Set<string>) => void): () => void;
    /** Paths whose remote head differs from our last synced state. Empty
     *  when up-to-date. */
    getPendingPullPaths(): Set<string>;
    /** Subscribe to local-save changes — fires whenever the saves chain
     *  is updated (save / drop / clear / publish). Used by the History
     *  panel to render the saves section. */
    onSavesChange(listener: (saves: LocalSave[]) => void): () => void;
    /** Current pending-saves snapshot, newest first. */
    getPendingSaves(): LocalSave[];
    /** Drop a save by id (delegates to the local-saves store). */
    dropLocalSave(id: string): Promise<void>;
    /** Revert the working tree to a save by id (delegates internally). */
    revertToLocalSave(id: string): Promise<void>;
    /** File-level diff between a save and its predecessor (or baseTree if
     *  it's the oldest save). Returns null when the save can't be found. */
    getSaveDiff(id: string): Promise<import('./git-types').TreeDiff | null>;
    /** Subscribe to changes in the conflict state — fires whenever the
     *  text-marker set or binary-conflict-copy set changes. */
    onConflictChange(listener: (state: { text: Set<string>; binary: Set<string> }) => void): () => void;
    /** Paths currently in conflict — split into text (file has diff3
     *  markers) and binary (sibling `.fade-conflict.<sha>` copy exists). */
    getConflictPaths(): { text: Set<string>; binary: Set<string> };
    /** Open the dedicated conflict-resolution editor for `path`. Returns
     *  false if the host didn't wire `onOpenConflict`. */
    openConflictEditor(path: string): boolean;
    /** Open a read-only diff tab for `path` in the given context.
     *  Fetches the appropriate before/after texts itself (caller just
     *  passes context + path). Returns false if the host didn't wire
     *  `onOpenDiff` or the context can't be satisfied (e.g. requesting
     *  a save diff for an id that no longer exists).
     *
     *  - `unsaved`: latest save (or published baseTree, if no saves)
     *               → working tree. "What have I changed since my
     *               last save?" — the diff that drives the orange
     *               gutter and the "unsaved" chip.
     *  - `publish`: published baseTree → working tree. What Publish
     *               would push.
     *  - `save`:    predecessor save (or baseTree) → this save.
     *  - `commit`:  parent commit → this commit.
     *  - `pull`:    working tree → remote HEAD. A "what's coming if I
     *               click Pull" preview that bundles every pending
     *               remote commit since our last sync (the engine
     *               fast-forwards to remote HEAD in one shot, so the
     *               preview matches what actually lands). */
    openDiffViewer(args:
        | { kind: 'unsaved'; path: string }
        | { kind: 'publish'; path: string }
        | { kind: 'save'; saveId: string; path: string }
        | { kind: 'commit'; commitSha: string; path: string }
        | { kind: 'pull'; path: string }
    ): Promise<boolean>;
    /** Return the base (last-synced) text content of a path, or null if the
     *  path has no base (file was added since last sync) or the workspace
     *  isn't connected. Used by the gutter decorator.
     *
     *  Deprecated alias for `getPublishedText` — kept so existing callers
     *  (the gutter pre-tri-state) don't break. New code should use the
     *  paired `getSavedText` / `getPublishedText` for the tri-state diff. */
    getBaseText(path: string): Promise<string | null>;
    /** Text content of `path` at the user's latest local save, or null if
     *  the path isn't in any save. Used by the gutter to compute the
     *  "unsaved changes" diff (current vs latest save). */
    getSavedText(path: string): Promise<string | null>;
    /** Text content of `path` at the last published commit, or null if
     *  the path isn't on the remote yet. Used by the gutter to compute
     *  the "saved-but-not-yet-published" diff (latest save vs published). */
    getPublishedText(path: string): Promise<string | null>;

    // ─── history surface (consumed by the history dockview panel) ──────────

    /** Current recent-commits list. Returns a copy. */
    getRecentCommits(): SharingCommitInfo[];
    /** Fire whenever recentCommits changes (after pull/commit/restore/connect). */
    onHistoryChange(listener: (commits: SharingCommitInfo[]) => void): () => void;
    /** Per-commit file diff against its parent. Cached. Returns null if
     *  the commit isn't available or not connected. */
    getCommitDiff(sha: string): Promise<import('./git-types').TreeDiff | null>;
    /** Restore the working tree to a commit's contents and commit the
     *  result as a NEW commit on top of HEAD (no history rewrite). */
    restoreCommit(targetSha: string): Promise<void>;
    /** Owner / name / branch for the currently bound repo, or null. */
    getRepoInfo(): { owner: string; name: string; branch: string } | null;
}

export interface CollaborationOptions {
    container: HTMLElement;
    workspace: OpfsWorkspaceLike;
    /** Resolves to the active project name; called whenever the panel needs to consult it. */
    getActiveProject: () => string;
    /** Force any pending OPFS autosaves to land before snapshotting for commit. */
    flushPendingSaves?: () => Promise<void>;
    /** Optional: hook for when the user pulls remote changes — host may need to refresh open editors. */
    onAfterPull?: (changedPaths: string[]) => void | Promise<void>;
    /** Optional: open the conflict-resolution editor for a path. The host
     *  registers a dynamic dockview panel and calls `mountConflictEditor`
     *  inside it. Without this hook the panel falls back to inline-only
     *  resolution via the legacy "Mark resolved" button. */
    onOpenConflict?: (path: string) => void | Promise<void>;
    /** Optional: open a read-only diff tab for a file. Used by "Show
     *  diff" buttons in the publish preview and history panels. The
     *  host owns dockview registration; the panel just hands over the
     *  pre-fetched before/after strings + display labels. */
    onOpenDiff?: (args: {
        id: string;
        title: string;
        path: string;
        languageId: string;
        beforeText: string | null;
        afterText: string | null;
        beforeLabel?: string;
        afterLabel?: string;
    }) => void;
}

export function mountCollaboration(opts: CollaborationOptions): CollaborationController {
    injectStylesOnce();

    const tokenStore = new SessionTokenStore();

    // ─── state ──────────────────────────────────────────────────────────────
    let activeProject = opts.getActiveProject();
    let user: ValidatedToken | null = null;     // null until we validate the stored token
    let index: ProjectSyncIndex = loadSyncIndex(activeProject);
    // Last branch head we observed from polling. When this differs from
    // `index.syncedCommitSha`, the remote has new commits and the Pull
    // button gets a "new upstream" affordance.
    let remoteHeadSha: string | null = null;
    /** Paths whose remote-head version differs from `baseTree[path]`.
     *  Populated by the poll loop when it sees a new remote head and
     *  fetches the new tree to diff against. Cleared after pull/commit. */
    let pendingPullPaths: Set<string> = new Set();
    const pendingPullListeners = new Set<(paths: Set<string>) => void>();
    function emitPendingPull() {
        const snap = new Set(pendingPullPaths);
        for (const l of pendingPullListeners) {
            try { l(snap); } catch (e) { console.error('[sharing] pendingPull listener threw', e); }
        }
    }
    let pollTimer: number | null = null;
    const POLL_INTERVAL_MS = 30_000;

    // History-related caches. `commitTreeCache` and `commitDiffCache`
    // short-circuit re-fetches when the history panel toggles a commit
    // open/closed repeatedly. Expanded-state lives in the history panel
    // itself (per-panel UI state, not shared).
    const commitTreeCache = new Map<string, GitTree>();
    const commitDiffCache = new Map<string, TreeDiff>();

    // Per-panel logger — every action this module fires (commit, pull,
    // conflict-resolve, restore) emits structured entries that the Logs
    // dockview panel surfaces. Channel 'sharing' so filters can isolate.
    const log = getLogger('sharing');

    /** Path → git-blob-sha cache. Bypasses re-hashing every file on every
     *  autosave (the previous hot path). Invalidated by every workspace
     *  write the panel knows about + by `refreshStatusForFile` when the
     *  host (autosave) tells us about an external write. */
    const hashCache = new HashCache();

    /** Workspace-write helpers — every internal write to OPFS must go
     *  through these so the hash cache stays coherent. External writes
     *  (autosave from the editor tab) come through
     *  `refreshStatusForFile` which invalidates separately. */
    async function writeFile(path: string, bytes: Uint8Array): Promise<void> {
        await opts.workspace.writeBytes(path, bytes);
        hashCache.invalidate(path);
    }
    async function deleteFile(path: string): Promise<void> {
        await opts.workspace.delete(path);
        hashCache.invalidate(path);
    }

    /** Host-supplied "any Monaco buffer dirty?" flag. Edits set this
     *  true on keystroke (before autosave fires); the panel ORs it with
     *  `hasUnsaved` so Save enables immediately. */
    let hasDirtyTabs = false;
    let staged: FileStatusEntry[] = [];
    /** Per-file diff between the current working tree and the *published*
     *  baseTree — i.e. the set of changes a Publish would roll up. Different
     *  from `staged`, which compares against the latest local save. Computed
     *  in `refreshStatus`; cheap because it shares the same HashCache. */
    let publishStaged: FileStatusEntry[] = [];
    /** Persisted save-message draft. Survives panel re-renders so the
     *  user doesn't lose what they typed when a status refresh fires. */
    let saveMessageDraft = '';
    let publishMessageDraft = '';
    /** Cached list of local saves for the active project (newest first).
     *  Re-read from localStorage by `refreshSaves()`. */
    let pendingSaves: LocalSave[] = [];
    const savesListeners = new Set<(saves: LocalSave[]) => void>();
    function emitSaves(): void {
        const snap = [...pendingSaves];
        for (const l of savesListeners) {
            try { l(snap); } catch (e) { console.error('[sharing] saves listener threw', e); }
        }
    }
    /** Async-resolved load + upgrade pipeline. refreshSaves kicks off an
     *  OPFS read and (if needed) legacy treeHashes upgrade; consumers
     *  await this promise before reading `pendingSaves` if they need
     *  guaranteed-current data. */
    let savesUpgrade: Promise<void> = Promise.resolve();
    function refreshSaves(): void {
        savesUpgrade = (async () => {
            pendingSaves = await loadSaves(activeProject);
            emitSaves();
            const needsUpgrade = pendingSaves.some((s) => !s.treeHashes);
            if (!needsUpgrade) return;
            // Lazy-upgrade legacy saves (no treeHashes). Re-render after
            // the batch so the unsaved chip flips correctly once hashes
            // land.
            for (let i = 0; i < pendingSaves.length; i++) {
                if (pendingSaves[i].treeHashes) continue;
                pendingSaves[i] = await upgradeSave(pendingSaves[i]);
            }
            emitSaves();
            render();
        })();
    }

    /** Reference tree for the "unsaved" comparison: the latest local
     *  save's tree if any exist, otherwise the published baseTree.
     *
     *  Concretely: "unsaved changes" means "differs from my latest
     *  checkpoint." After clicking Save, the working tree matches the
     *  save → no unsaved diff. The saves-vs-published gap shows up as
     *  the separate "unpublished" chip. */
    function referenceTree(): Record<string, string> {
        if (pendingSaves.length > 0 && pendingSaves[0].treeHashes) {
            return pendingSaves[0].treeHashes;
        }
        return index.baseTree;
    }
    /** Conflict-copy files in the workspace ('<path>.fade-conflict.<sha>'),
     *  populated by refreshStatus from the unfiltered workspace listing.
     *  Only binary files end up here now — text files get diff3-merged
     *  with markers written in-place. */
    let conflictFiles: string[] = [];
    /** Text files that contain `<<<<<<< / ======= / >>>>>>>` markers and
     *  must be resolved before the next Publish. Populated by
     *  `mergeFromRemote` (when diff3 leaves markers) and re-detected on
     *  every refreshStatus scan so reloads can pick conflicts back up. */
    let textConflicts: Set<string> = new Set();
    const conflictListeners = new Set<(state: { text: Set<string>; binary: Set<string> }) => void>();
    function emitConflicts() {
        const snap = {
            text: new Set(textConflicts),
            binary: new Set(conflictFiles),
        };
        for (const l of conflictListeners) {
            try { l(snap); } catch (e) { console.error('[sharing] conflict listener threw', e); }
        }
    }
    let recentCommits: SharingCommitInfo[] = [];
    const historyListeners = new Set<(commits: SharingCommitInfo[]) => void>();
    function emitHistory() {
        const snap = [...recentCommits];
        for (const l of historyListeners) {
            try { l(snap); } catch (e) { console.error('[sharing] history listener threw', e); }
        }
    }
    /** Current long-running operation's user-facing status, if any. The
     *  label updates as the engine emits progress events; the optional
     *  progress field drives the inline bar. */
    let busy: { label: string; progress?: { current: number; total: number } } | null = null;
    /** Hard failures: thrown exceptions, validation errors, "Not
     *  connected" guards. Shows in a red banner. Cleared explicitly on
     *  retry or replaced by a new error. */
    let errorBanner: string | null = null;
    /** Transient status from a multi-step flow: "auto-merged N files,
     *  click Publish to push", "Pull once more before Publishing", etc.
     *  Shows in a neutral banner. Auto-clears when the next-action
     *  state changes (e.g. all conflicts resolved, or a fresh runBusy
     *  begins). Never used for errors. */
    let infoBanner: string | null = null;

    // Hash → bytes cache for base content. Blobs are content-addressed so
    // dedup is automatic across paths. Unbounded for now — typical playground
    // projects fit in tens of MB.
    const baseContentCache = new Map<string, Uint8Array>();

    const statusListeners = new Set<(m: Map<string, FileStatus>) => void>();
    function statusMapFromStaged(): Map<string, FileStatus> {
        const m = new Map<string, FileStatus>();
        for (const e of staged) m.set(e.path, e.status);
        return m;
    }
    function emitStatus() {
        const m = statusMapFromStaged();
        for (const l of statusListeners) l(m);
    }

    // ─── adapter / repo (lazy, rebuilt whenever the binding changes) ───────
    // buildRepo is async because it may transparently refresh an
    // expired access token before producing an adapter. Without this,
    // a long-running session (>8h default) silently starts hitting 401s
    // on every API call.
    async function buildRepo(): Promise<{ adapter: GitHubAdapter; repo: Repo } | null> {
        if (!index.remoteRepo) return null;
        const accessToken = await ensureFreshAccessToken();
        if (!accessToken) return null;
        const adapter = GitHubAdapter.open({
            owner: index.remoteRepo.owner,
            repo: index.remoteRepo.name,
            branch: index.remoteRepo.branch,
            token: accessToken,
        });
        const repo = new Repo(adapter);
        // Rehydrate the engine's synced state from our persisted sync-index
        // so a freshly-built Repo doesn't see itself as "nothing synced." The
        // engine uses syncedHead.treeSha as `base_tree` on createTree (sends
        // only the diff, not the full tree) and syncedTree to detect when a
        // working-tree change should mark a file as modified.
        if (index.syncedCommitSha && index.syncedTreeSha) {
            const tree: GitTree = {};
            for (const [path, blobSha] of Object.entries(index.baseTree)) {
                tree[path] = { blobSha };
            }
            repo.setSyncedHead(
                { commitSha: index.syncedCommitSha, treeSha: index.syncedTreeSha },
                tree,
            );
        }
        return { adapter, repo };
    }

    /** Load the stored TokenSet, refresh the access token if it's near
     *  expiry, persist the result, and return a usable access token.
     *  Returns null when:
     *    - no token stored (user never signed in)
     *    - access expired AND no usable refresh token (user has to
     *      sign in again — token store is cleared)
     *    - refresh request failed (treated the same as no refresh)
     *  Callers should treat null as "not authenticated"; the next
     *  user-initiated action will show the sign-in dialog. */
    async function ensureFreshAccessToken(): Promise<string | null> {
        const stored = tokenStore.load();
        if (!stored) return null;
        if (!isAccessExpired(stored)) return stored.accessToken;
        if (!isRefreshUsable(stored)) {
            // Access expired AND we can't refresh — wipe so the panel's
            // signed-in indicator updates and the user gets prompted.
            tokenStore.clear();
            user = null;
            return null;
        }
        try {
            const fresh = await refreshAccessToken({
                clientId: GITHUB_APP_CLIENT_ID,
                refreshToken: stored.refreshToken!,
            });
            const updated = tokenSetToStored(fresh);
            tokenStore.save(updated);
            log.info('Refreshed GitHub access token.');
            return updated.accessToken;
        } catch (e) {
            log.warn(`Token refresh failed — clearing stored credentials: ${errMsg(e)}`);
            tokenStore.clear();
            user = null;
            return null;
        }
    }

    // ─── upstream polling ───────────────────────────────────────────────────

    /** One poll tick — checks remote head and, if changed, refreshes
     *  pendingPullPaths. Pulled out so both the background timer and the
     *  manual "Check now" button can fire it. `force=true` bypasses the
     *  hidden-tab skip; callers that are user-initiated should pass true.
     */
    let checkingRemote = false;
    async function checkRemote(force: boolean): Promise<void> {
        if (!user || !isConnected(index)) return;
        if (!force && typeof document !== 'undefined' && document.visibilityState === 'hidden') return;
        if (checkingRemote) return;          // single-flight
        // Skip polling while a long-running op is mid-flight. The "phantom
        // pull after publish" bug was this race: commit() updates the
        // remote branch ref, then yields for getCommit(); a poll that
        // fires during that yield sees the new remote SHA but our local
        // index.syncedCommitSha is still the pre-publish one, so it
        // populates pendingPullPaths against the stale baseTree — i.e.
        // with our own just-published commit's paths. persistSyncedFrom
        // later clears it, but the chip flickers on for the user.
        if (!force && busy !== null) return;
        checkingRemote = true;
        try {
            const built = await buildRepo();
            if (!built) return;
            const sha = await built.adapter.branchHead();
            if (sha === remoteHeadSha) {
                // Even when sha hasn't changed, the user might want feedback
                // that the check ran (manual click). Re-render either way.
                if (force) render();
                return;
            }
            remoteHeadSha = sha;
            const ahead = sha !== null && sha !== index.syncedCommitSha;
            if (ahead) {
                try {
                    const remoteTree = await built.adapter.getTree(sha!);
                    const next = new Set<string>();
                    const baseTree = index.baseTree;
                    for (const [path, entry] of Object.entries(remoteTree)) {
                        if (baseTree[path] !== entry.blobSha) next.add(path);
                    }
                    for (const path of Object.keys(baseTree)) {
                        if (!(path in remoteTree)) next.add(path);
                    }
                    pendingPullPaths = next;
                } catch (e) {
                    log.warn(`Could not fetch remote tree for pending-pull diff: ${errMsg(e)}`);
                    pendingPullPaths = new Set();
                }
            } else {
                pendingPullPaths = new Set();
            }
            emitPendingPull();
            render();
        } catch (e) {
            console.warn('[sharing] poll failed', e);
            if (force) log.warn(`Check-now failed: ${errMsg(e)}`);
        } finally {
            checkingRemote = false;
        }
    }

    /** Start a background poll. Idempotent — calling while a timer is
     *  running stops the old one first. */
    function startPolling() {
        stopPolling();
        if (!user || !isConnected(index)) return;
        pollTimer = window.setInterval(() => { void checkRemote(false); }, POLL_INTERVAL_MS);
        // Fire immediately so the "ahead" indicator appears without waiting
        // a full interval after sign-in.
        void checkRemote(false);
    }

    function stopPolling() {
        if (pollTimer !== null) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    // The tab might be backgrounded for hours — re-fire a poll as soon as
    // we're visible again so the "ahead" indicator isn't stale.
    const visibilityHandler = () => {
        if (typeof document === 'undefined') return;
        if (document.visibilityState === 'visible' && pollTimer !== null) {
            void checkRemote(true);
        }
    };
    if (typeof document !== 'undefined') {
        document.addEventListener('visibilitychange', visibilityHandler);
    }

    /** After any flow that changes the synced commit (commit, pull, clone),
     *  push the new state back into the sync-index. Centralizes the
     *  field-mapping so we don't duplicate it across doCommit / pull /
     *  mergeFromRemote / connectExisting. Also clears the polled-remote
     *  indicator since local just caught up. */
    function persistSyncedFrom(repo: Repo) {
        const synced = repo.getSyncedHead();
        if (!synced) {
            index.syncedCommitSha = null;
            index.syncedTreeSha = null;
            index.baseTree = {};
        } else {
            index.syncedCommitSha = synced.commitSha;
            index.syncedTreeSha = synced.treeSha;
            index.baseTree = repo.syncedTreeToBlobShas();
            // Local caught up to (or moved past) what the poll last saw.
            // Mirror that so the "ahead" indicator turns off immediately.
            remoteHeadSha = synced.commitSha;
            if (pendingPullPaths.size > 0) {
                pendingPullPaths = new Set();
                emitPendingPull();
            }
        }
        saveSyncIndex(activeProject, index);
    }

    // ─── render ─────────────────────────────────────────────────────────────
    function render() {
        opts.container.replaceChildren();
        opts.container.classList.add(`${CSS_PREFIX}-root`);

        // Header — sign-in + active project context.
        const header = el('div', `${CSS_PREFIX}-header`);
        const who = el('div', `${CSS_PREFIX}-who`);
        if (user) {
            who.append(text(`@${user.login}`), el('span', `${CSS_PREFIX}-dim`, ` · project: ${activeProject}`));
        } else {
            who.append(text('not signed in'), el('span', `${CSS_PREFIX}-dim`, ` · project: ${activeProject}`));
        }
        const headerActions = el('div', `${CSS_PREFIX}-row`);
        if (user) {
            headerActions.append(button('Sign out', 'ghost-small', signOut));
        } else {
            headerActions.append(button('Sign in', 'primary-small', signIn));
        }
        header.append(who, headerActions);
        opts.container.append(header);

        // Status chips live in the app header now (see main.ts —
        // `mountSharingChips`). Keeping them out of the panel itself means
        // the user can glance at sync state without focusing the
        // Collaboration tab.

        // Body — depends on (signed in?) × (connected?)
        const body = el('div', `${CSS_PREFIX}-body`);

        if (busy) {
            body.append(renderBusyBanner(busy));
        }
        if (errorBanner) {
            body.append(banner(errorBanner, 'err'));
        }
        if (infoBanner) {
            body.append(banner(infoBanner, 'info'));
        }

        if (!user) {
            body.append(p('Connect your GitHub account.'));
        } else if (!isConnected(index)) {
            body.append(renderNotConnected());
        } else {
            body.append(renderConnected());
        }
        opts.container.append(body);
    }

    function renderNotConnected(): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-section`);
        wrap.append(
            heading('Workspace not connected'),
            p(`The "${activeProject}" project isn't backed by a GitHub repo yet. Pick one of:`),
            row(
                button('Publish to GitHub…', 'primary', publishNew),
                button('Connect existing repo…', 'ghost', connectExisting),
            ),
        );
        return wrap;
    }

    // Status chips moved to the app header — see main.ts
    // `renderSharingChips`. The panel no longer owns this surface, but
    // it still exposes the underlying state via getStatusMap /
    // getPendingSaves / getPendingPullPaths / getConflictPaths so the
    // header can paint its pills.

    function renderConnected(): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-section`);
        const repo = index.remoteRepo!;
        const link = document.createElement('a');
        link.href = `https://github.com/${repo.owner}/${repo.name}`;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.textContent = `${repo.owner}/${repo.name}`;
        link.className = `${CSS_PREFIX}-link`;

        // Conflict banner sits ABOVE the connected-repo heading so it's
        // impossible to miss. Always visible while any conflict exists,
        // regardless of how the rest of the panel renders.
        const totalConflicts = textConflicts.size + conflictFiles.length;
        if (totalConflicts > 0) {
            const firstText = [...textConflicts][0];
            const banner = el('div', `${CSS_PREFIX}-conflict-banner`);
            const msg = el('div', `${CSS_PREFIX}-conflict-banner-msg`);
            const parts: string[] = [];
            if (textConflicts.size > 0) parts.push(`${textConflicts.size} text`);
            if (conflictFiles.length > 0) parts.push(`${conflictFiles.length} binary`);
            msg.textContent = `${totalConflicts} unresolved conflict${totalConflicts === 1 ? '' : 's'} (${parts.join(', ')}). Commits are blocked until resolved.`;
            banner.append(msg);
            if (firstText && opts.onOpenConflict) {
                const openBtn = button('Open merge editor →', 'primary-small',
                    () => { void opts.onOpenConflict!(firstText); });
                banner.append(openBtn);
            }
            wrap.append(banner);
        }

        // Repo header — link + branch + inline Disconnect. The old top-row
        // of Pull/Refresh/Revert-all/Disconnect buttons is gone; Pull is
        // surfaced contextually via the polling chip when ahead, the
        // background poll covers Refresh, and Revert-all moved next to
        // the unsaved-changes list it acts on.
        const repoHeader = el('div', `${CSS_PREFIX}-repo-header`);
        const repoLine = el('div', `${CSS_PREFIX}-repo-line`);
        repoLine.append(link, el('span', `${CSS_PREFIX}-dim`, ` · branch ${repo.branch}`));
        // Check now — bypasses the 30s background poll. The background
        // poll skips itself while a long-running op is in flight (to
        // avoid the phantom-pull race during commit), so an explicit
        // user click is the way to force an immediate refresh.
        const checkNowBtn = button(
            checkingRemote ? '⟳ Checking…' : '⟳ Check now',
            'ghost-small',
            () => { void checkRemote(true); },
        );
        checkNowBtn.title = checkingRemote
            ? 'A check is already in flight.'
            : 'Ask the remote whether anything has changed since the last poll.';
        checkNowBtn.disabled = busy !== null || checkingRemote;
        const disconnectBtn = button('Disconnect', 'ghost-small', disconnect);
        disconnectBtn.title = 'Disconnect this project from the remote (local files kept).';
        if (busy !== null) disconnectBtn.disabled = true;
        repoHeader.append(repoLine, checkNowBtn, disconnectBtn);
        wrap.append(heading('Connected repo'));
        wrap.append(repoHeader);

        const conflictBlocked = textConflicts.size > 0;
        const changed = staged.filter((e) => e.status !== 'unchanged');
        // `hasUnsaved` reflects the OPFS-vs-save diff *plus* any
        // not-yet-flushed Monaco edits. Without the dirty-tabs OR, the
        // Save button stays greyed out for ~600 ms after each
        // keystroke (until autosave fires + refreshStatus runs).
        const hasUnsaved = changed.length > 0 || hasDirtyTabs;
        const hasUnpublished = pendingSaves.length > 0;
        const hasRemoteToPull = pendingPullPaths.size > 0;
        const busyNow = busy !== null;
        const isSaving = busyNow && busy!.label.startsWith('Saving');
        const isPublishing = busyNow && busy!.label.startsWith('Publishing');
        const isPulling = busyNow && busy!.label.startsWith('Pulling');

        // ── Pull section (contextual) ──────────────────────────────────
        // Renders only when the background poll has detected the remote
        // moved past our last synced commit. The header chip + this
        // button together are the only Pull surface — no permanent top-
        // row button anymore.
        if (hasRemoteToPull) {
            const pullSection = el('div', `${CSS_PREFIX}-pullbox`);
            // Header row: title + Pull button.
            const pullHeadRow = el('div', `${CSS_PREFIX}-pullbox-headrow`);
            const pullHead = el('div', `${CSS_PREFIX}-pullbox-h`);
            pullHead.textContent = `↓ Remote has ${pendingPullPaths.size} change${pendingPullPaths.size === 1 ? '' : 's'} for you`;
            const pullBtn = button(
                isPulling ? '⟳ Pulling…' : 'Pull',
                'primary-small',
                () => { void pull(); },
            );
            pullBtn.disabled = busyNow;
            if (busyNow) pullBtn.title = 'Busy…';
            else pullBtn.title = hasUnsaved || hasUnpublished
                ? 'Pull merges remote changes into your working tree (3-way merge against your local saves + edits).'
                : 'Fast-forward to the latest remote commit.';
            pullHeadRow.append(pullHead, pullBtn);
            pullSection.append(pullHeadRow);

            // Per-file list with Show-diff buttons. The diff is
            // "working tree → remote HEAD"; multiple pending remote
            // commits collapse into one fast-forward to remote HEAD,
            // so the preview matches what Pull would actually land.
            const pullList = el('ul', `${CSS_PREFIX}-pullbox-list`);
            const sortedPullPaths = [...pendingPullPaths].sort();
            for (const path of sortedPullPaths) {
                const li = document.createElement('li');
                li.className = `${CSS_PREFIX}-pullbox-row`;
                const pathEl = el('span', `${CSS_PREFIX}-path`);
                pathEl.textContent = path;
                li.append(pathEl);
                if (opts.onOpenDiff) {
                    const diffBtn = button('Show diff', 'revert-mini', () => {
                        void openDiffViewerImpl({ kind: 'pull', path });
                    });
                    diffBtn.title = `Preview ${path} (working tree → remote HEAD).`;
                    li.append(diffBtn);
                }
                pullList.append(li);
            }
            pullSection.append(pullList);
            wrap.append(pullSection);
        }

        // ── Unsaved changes section ──────────────────────────────────────
        //   Header row: title + Revert-all (only when there are changes).
        //   File list with per-row revert.
        //   Save action: button anchored to THIS section, since Save's
        //   target is the working-tree-vs-latest-save diff.
        const unsavedHeader = el('div', `${CSS_PREFIX}-section-header`);
        unsavedHeader.append(heading('Unsaved changes', 2));
        if (hasUnsaved) {
            const revertAllBtn = button(`Revert all (${changed.length})`, 'ghost-small', () => { void revertAll(); });
            revertAllBtn.title = `Overwrite ${changed.length} changed file${changed.length === 1 ? '' : 's'} with the last-synced version.`;
            revertAllBtn.disabled = busyNow;
            unsavedHeader.append(revertAllBtn);
        }
        wrap.append(unsavedHeader);

        if (!hasUnsaved) {
            wrap.append(p('Working tree matches the latest save.', 'dim'));
        } else {
            const list = el('ul', `${CSS_PREFIX}-stagedlist`);
            for (const e of changed) {
                const li = document.createElement('li');
                li.className = `${CSS_PREFIX}-staged ${CSS_PREFIX}-staged-${e.status}`;
                const g = el('span', `${CSS_PREFIX}-glyph`);
                g.textContent = statusGlyph(e.status);
                const path = el('span', `${CSS_PREFIX}-path`);
                path.textContent = e.path;
                li.append(g, path);
                // Show-diff lands the diff editor on the same reference
                // the unsaved chip uses: latest save (or baseTree) vs
                // working tree. Hidden when the host didn't wire
                // onOpenDiff — keeps the row tight in that case.
                if (opts.onOpenDiff) {
                    const diffBtn = button('Show diff', 'revert-mini', () => {
                        void openDiffViewerImpl({ kind: 'unsaved', path: e.path });
                    });
                    diffBtn.disabled = busyNow;
                    diffBtn.title = `Open a read-only diff of ${e.path} (latest save → working tree).`;
                    li.append(diffBtn);
                }
                const revertBtn = button('Revert', 'revert-mini', () => { void revertFile(e.path); });
                revertBtn.disabled = busyNow;
                revertBtn.title = e.status === 'added'
                    ? `Delete ${e.path} (it has no base content)`
                    : `Overwrite ${e.path} with the last-synced version`;
                li.append(revertBtn);
                list.append(li);
            }
            wrap.append(list);
        }

        // Save action — anchored to the unsaved-changes section. Quick
        // checkpoint; the inline textbox holds an optional message.
        const saveMsgInput = document.createElement('input');
        saveMsgInput.type = 'text';
        saveMsgInput.className = `${CSS_PREFIX}-msg-inline`;
        saveMsgInput.placeholder = 'Optional save message…';
        saveMsgInput.value = saveMessageDraft;
        saveMsgInput.disabled = busyNow || !hasUnsaved;
        saveMsgInput.addEventListener('input', () => { saveMessageDraft = saveMsgInput.value; });

        const saveBtn = button(
            isSaving ? '⟳ Saving…' : 'Save',
            'primary-small',
            async () => {
                const typed = saveMsgInput.value.trim();
                const msg = typed || defaultSaveMessage();
                await runBusy('Saving…', async () => { await doSave(msg); });
                saveMessageDraft = '';
                render();
            },
        );
        const saveBlocked = !hasUnsaved || conflictBlocked || busyNow;
        saveBtn.disabled = saveBlocked;
        if (busyNow) saveBtn.title = 'Busy…';
        else if (!hasUnsaved) saveBtn.title = 'No changes to save.';
        else if (conflictBlocked) saveBtn.title = `Resolve ${textConflicts.size} text conflict${textConflicts.size > 1 ? 's' : ''} first.`;
        else saveBtn.title = 'Snapshot the current working tree locally.';
        wrap.append(rowInline(saveMsgInput, saveBtn));

        // ── Publish section ──────────────────────────────────────────────
        // Show only when there are actual changes to push. Saves alone
        // aren't a reason to display Publish — if the working tree
        // matches the remote (e.g. user reverted away from a save), the
        // engine's commit() throws "nothing to commit" and the click is
        // a silent no-op. Hiding the section in that case keeps the
        // affordance honest: visible == clickable == "will publish".
        const publishChanged = publishStaged.filter((e) => e.status !== 'unchanged');
        const hasChangesToPublish = publishChanged.length > 0;

        if (hasUnpublished && hasChangesToPublish) {
            wrap.append(heading('Publish', 2));

            const pubMsgInput = document.createElement('input');
            pubMsgInput.type = 'text';
            pubMsgInput.className = `${CSS_PREFIX}-msg-inline`;
            pubMsgInput.placeholder = 'Optional commit message…';
            pubMsgInput.value = publishMessageDraft;
            pubMsgInput.disabled = busyNow;
            pubMsgInput.addEventListener('input', () => { publishMessageDraft = pubMsgInput.value; });

            const publishBtn = button(
                isPublishing
                    ? '⟳ Publishing…'
                    : `Publish ${pendingSaves.length} save${pendingSaves.length === 1 ? '' : 's'}`,
                'primary',
                async () => {
                    const msg = pubMsgInput.value.trim() || defaultPublishMessage();
                    await runBusy('Publishing…', (progress) => doPublish(msg, progress));
                    publishMessageDraft = '';
                },
            );
            // Pull-before-Publish: when the remote has new commits we
            // haven't fetched, block Publish entirely. The user must
            // click Pull explicitly so the merge result lands in their
            // working tree (visible, inspectable, conflict-resolvable)
            // before anything goes back to GitHub. Without this gate,
            // commit() would still race-detect HeadConflictError and
            // auto-merge — but only AFTER the user tried to push, and
            // some users (rightly) want pull-then-publish to be an
            // explicit two-step workflow.
            const publishBlocked = !hasChangesToPublish || conflictBlocked || hasRemoteToPull || busyNow;
            publishBtn.disabled = publishBlocked;
            if (busyNow) publishBtn.title = 'Busy…';
            else if (hasRemoteToPull) publishBtn.title = `Remote has ${pendingPullPaths.size} change${pendingPullPaths.size === 1 ? '' : 's'} you haven't pulled. Click Pull first to merge them locally — then Publish.`;
            else if (conflictBlocked) publishBtn.title = `Resolve ${textConflicts.size} text conflict${textConflicts.size > 1 ? 's' : ''} first.`;
            else publishBtn.title = `Squash ${pendingSaves.length} local save${pendingSaves.length === 1 ? '' : 's'} into one commit on the remote.`;

            wrap.append(rowInline(pubMsgInput, publishBtn));

            // Publish preview — what would actually land on the remote.
            // Union of saved-but-not-yet-published changes and any
            // uncommitted edits since the last save.
            const addedN = publishChanged.filter((e) => e.status === 'added').length;
            const modN   = publishChanged.filter((e) => e.status === 'modified').length;
            const delN   = publishChanged.filter((e) => e.status === 'deleted').length;
            const summary = el('div', `${CSS_PREFIX}-pubprev`);
            const h = el('div', `${CSS_PREFIX}-pubprev-h`);
            h.textContent = `Preview · ${publishChanged.length} file${publishChanged.length === 1 ? '' : 's'}`;
            const counts = el('span', `${CSS_PREFIX}-pubprev-counts`);
            const parts: string[] = [];
            if (addedN) parts.push(`+${addedN}`);
            if (modN)   parts.push(`~${modN}`);
            if (delN)   parts.push(`-${delN}`);
            counts.textContent = parts.join(' · ');
            h.append(text(' '), counts);
            summary.append(h);
            const plist = el('ul', `${CSS_PREFIX}-pubprev-list`);
            for (const e of publishChanged) {
                const li = document.createElement('li');
                li.className = `${CSS_PREFIX}-pubprev-row ${CSS_PREFIX}-staged-${e.status}`;
                const g = el('span', `${CSS_PREFIX}-glyph`);
                g.textContent = statusGlyph(e.status);
                const pathEl = el('span', `${CSS_PREFIX}-path`);
                pathEl.textContent = e.path;
                li.append(g, pathEl);
                // Show-diff affordance — hidden when the host didn't
                // wire onOpenDiff (older callers) so the row stays
                // clean. Even deletions get a button — the diff editor
                // renders one-sided changes cleanly.
                if (opts.onOpenDiff) {
                    const diffBtn = button('Show diff', 'revert-mini', () => {
                        void openDiffViewerImpl({ kind: 'publish', path: e.path });
                    });
                    diffBtn.title = `Open a read-only diff of ${e.path} (published → working tree).`;
                    li.append(diffBtn);
                }
                plist.append(li);
            }
            summary.append(plist);
            wrap.append(summary);
        } else if (hasUnpublished && !hasChangesToPublish) {
            // Edge case: user has saves but reverted the working tree
            // back to the published state, so there's nothing to push.
            // Surface this rather than disabling the button silently.
            wrap.append(p(
                `${pendingSaves.length} local save${pendingSaves.length === 1 ? '' : 's'} exist but the working tree matches the remote — nothing to publish. Drop saves from the History tab if you don't need them.`,
                'dim',
            ));
        }

        // Conflicts — split into two categories with different affordances.
        //   Text conflicts:  diff3 left `<<<<<<< / ======= / >>>>>>>` markers
        //                    in the live file. User edits in Monaco, then
        //                    clicks Mark resolved.
        //   Binary conflicts: a `<path>.fade-conflict.<sha>` copy was written.
        //                     User picks Use mine / Use theirs.
        const textConflictList = [...textConflicts].sort();
        if (textConflictList.length > 0) {
            wrap.append(heading('Conflicts (text — merge in editor)', 2));
            wrap.append(p('These files have `<<<<<<<` / `=======` / `>>>>>>>` markers from the 3-way merge. Open each file, keep the lines you want, delete the markers, then click "Mark resolved". The commit button will stay disabled until every text conflict is resolved.', 'dim'));
            const tList = el('ul', `${CSS_PREFIX}-stagedlist`);
            for (const path of textConflictList) {
                const li = document.createElement('li');
                li.className = `${CSS_PREFIX}-conflict`;
                const top = el('div', `${CSS_PREFIX}-conflict-row`);
                const g = el('span', `${CSS_PREFIX}-glyph`);
                g.textContent = 'C';
                g.style.color = '#f88';
                const pathEl = el('span', `${CSS_PREFIX}-path`);
                pathEl.textContent = path;
                top.append(g, pathEl);
                const actions = el('div', `${CSS_PREFIX}-row`);
                if (opts.onOpenConflict) {
                    const openBtn = button('Resolve in editor →', 'primary-small', () => { void opts.onOpenConflict!(path); });
                    const markBtn = button('Mark resolved', 'ghost-small', () => { void resolveTextConflict(path); });
                    if (busyNow) { openBtn.disabled = true; markBtn.disabled = true; }
                    actions.append(openBtn, markBtn);
                } else {
                    const markBtn = button('Mark resolved', 'primary-small', () => { void resolveTextConflict(path); });
                    if (busyNow) markBtn.disabled = true;
                    actions.append(markBtn);
                }
                li.append(top, actions);
                tList.append(li);
            }
            wrap.append(tList);
        }
        if (conflictFiles.length > 0) {
            wrap.append(heading('Conflicts (binary — pick a side)', 2));
            wrap.append(p('Binary files can\'t be line-merged. We saved their remote version as a sibling `.fade-conflict.<sha>` copy. "Use mine" keeps your local edits; "Use theirs" overwrites your file with the remote version. Either way the conflict copy is deleted.', 'dim'));
            const cList = el('ul', `${CSS_PREFIX}-stagedlist`);
            for (const cf of conflictFiles) {
                const original = cf.replace(/\.fade-conflict\.[a-f0-9]+$/, '');
                const li = document.createElement('li');
                li.className = `${CSS_PREFIX}-conflict`;
                const top = el('div', `${CSS_PREFIX}-conflict-row`);
                const g = el('span', `${CSS_PREFIX}-glyph`);
                g.textContent = 'C';
                g.style.color = '#f88';
                const pathEl = el('span', `${CSS_PREFIX}-path`);
                pathEl.textContent = original;
                top.append(g, pathEl);
                const actions = el('div', `${CSS_PREFIX}-row`);
                const mineBtn = button('Use mine',   'ghost-small', () => { void resolveConflict(cf, 'mine'); });
                const theirsBtn = button('Use theirs', 'ghost-small', () => { void resolveConflict(cf, 'theirs'); });
                if (busyNow) { mineBtn.disabled = true; theirsBtn.disabled = true; }
                actions.append(mineBtn, theirsBtn);
                li.append(top, actions);
                cList.append(li);
            }
            wrap.append(cList);
        }

        return wrap;
    }

    // ─── actions ────────────────────────────────────────────────────────────

    async function signIn() {
        errorBanner = null;
        infoBanner = null;
        try {
            const tokenSet = await openSignInDialog();
            tokenStore.save(tokenSetToStored(tokenSet));
            user = await validateToken(tokenSet.accessToken);
            await refreshStatus();
            await refreshHistory();
            startPolling();
        } catch (e) {
            if (!(e instanceof DOMException && e.name === 'AbortError')) {
                errorBanner = errMsg(e);
            }
        }
        render();
    }

    function signOut() {
        tokenStore.clear();
        user = null;
        staged = [];
        publishStaged = [];
        recentCommits = [];
        remoteHeadSha = null;
        pendingPullPaths = new Set();
        emitPendingPull();
        stopPolling();
        emitStatus();
        render();
    }

    async function publishNew() {
        if (!user) return;
        const token = await ensureFreshAccessToken();
        if (!token) { errorBanner = 'Session expired — please sign in again.'; render(); return; }
        const defaultName = activeProject.toLowerCase().replace(/[^a-z0-9-_]+/g, '-');
        const name = window.prompt(
            'Name for the new GitHub repo:',
            defaultName,
        );
        if (!name) return;
        await runBusy(`Creating ${name}…`, async (progress) => {
            const createOpts: CreateRepoOptions = { name, token, private: false };
            const adapter = await GitHubAdapter.createRepo(createOpts);
            log.info(`Created GitHub repo ${user!.login}/${name}`);
            index = {
                ...index,
                remoteRepo: { owner: user!.login, name, branch: 'main' },
                syncedCommitSha: null,
                syncedTreeSha: null,
                baseTree: {},
            };
            saveSyncIndex(activeProject, index);
            // Initial commit so the repo isn't a bare auto_init.
            await doInitialCommit(adapter, progress);
            await refreshHistory();
            startPolling();
        });
    }

    async function connectExisting() {
        if (!user) return;
        const input = window.prompt(
            'Enter the repo as owner/name (you must already be a collaborator):',
            `${user.login}/${activeProject}`,
        );
        if (!input) return;
        const m = input.match(/^([^/\s]+)\s*\/\s*([^/\s]+)$/);
        if (!m) { errorBanner = 'Expected owner/name format.'; render(); return; }
        const [, owner, name] = m;

        await runBusy(`Connecting to ${owner}/${name}…`, async (progress) => {
            const token = await ensureFreshAccessToken();
            if (!token) { errorBanner = 'Session expired — please sign in again.'; return; }
            const adapter = GitHubAdapter.open({ owner, repo: name, branch: 'main', token });
            // Probe — 404 here means the repo doesn't exist or we lack access.
            try {
                await adapter.branchHead();
            } catch (e) {
                if (e instanceof GitHubApiError && e.status === 404) {
                    throw new Error(`${owner}/${name} not found, or you don't have access.`);
                }
                throw e;
            }
            index = {
                ...index,
                remoteRepo: { owner, name, branch: 'main' },
            };
            saveSyncIndex(activeProject, index);

            // Clone if remote has content.
            const wt = new OpfsWorkingTree(opts.workspace);
            const repo = new Repo(adapter);
            const remoteSha = await adapter.branchHead();
            if (remoteSha) {
                log.info(`Cloning ${owner}/${name} @ ${remoteSha.slice(0, 8)}`);
                await repo.checkout(wt, remoteSha, { onProgress: progress });
                persistSyncedFrom(repo);
                const paths = Object.keys(repo.getSyncedTree());
                if (opts.onAfterPull) await opts.onAfterPull(paths);
            }
            await refreshHistory();
            startPolling();
        });
    }

    async function disconnect() {
        if (!confirm(`Disconnect "${activeProject}" from ${index.remoteRepo?.owner}/${index.remoteRepo?.name}? Local files are kept; the remote is untouched.`)) return;
        index = { remoteRepo: null, syncedCommitSha: null, syncedTreeSha: null, baseTree: {} };
        saveSyncIndex(activeProject, index);
        staged = [];
        publishStaged = [];
        recentCommits = [];
        remoteHeadSha = null;
        pendingPullPaths = new Set();
        emitPendingPull();
        stopPolling();
        emitStatus();
        render();
    }

    async function doInitialCommit(adapter: GitHubAdapter, progress?: ProgressFn) {
        if (opts.flushPendingSaves) await opts.flushPendingSaves();
        const wt = new OpfsWorkingTree(opts.workspace);
        const repo = new Repo(adapter);
        // A freshly-created repo with auto_init has a root commit (just the
        // README). Rebase our engine onto that as the parent — otherwise
        // commit() would emit a root commit and updateBranch would reject
        // it as a non-FF.
        await repo.refreshSyncedHead();
        await repo.commit(wt, {
            author: user?.login ?? 'unknown',
            message: `Initial commit from Fade playground`,
            onProgress: progress,
        });
        persistSyncedFrom(repo);
        await refreshStatus();
    }

    /** Local save: snapshot the working tree to OPFS, no network.
     *  Returns the created save record (handy for "save & publish" to
     *  reuse the message). */
    async function doSave(message: string): Promise<LocalSave | null> {
        if (opts.flushPendingSaves) await opts.flushPendingSaves();
        try {
            const save = await createSave(activeProject, opts.workspace, message);
            log.info(`Saved locally: ${save.id} — "${message}"`);
            refreshSaves();
            // refreshStatus internally awaits `savesUpgrade`, which
            // refreshSaves() just reassigned to the in-flight OPFS load.
            // So by the time computeStatus sees referenceTree(),
            // pendingSaves is up-to-date with the new snapshot.
            await refreshStatus();
            return save;
        } catch (e) {
            errorBanner = `Save failed: ${errMsg(e)}`;
            log.error(`Save failed: ${errorBanner}`);
            return null;
        }
    }

    /** Publish: squash everything in the working tree into a single git
     *  commit on the remote branch. Accrued local saves (if any) are
     *  cleared on success — they were always just local checkpoints, and
     *  the new published commit supersedes them.
     *
     *  Default message is generated from the pending saves if the user
     *  didn't supply one. */
    async function doPublish(message: string, progress?: ProgressFn) {
        const built = await buildRepo();
        if (!built) { errorBanner = 'Not connected.'; return; }
        if (opts.flushPendingSaves) await opts.flushPendingSaves();
        const wt = new OpfsWorkingTree(opts.workspace);
        try {
            await built.repo.commit(wt, {
                author: user?.login ?? 'unknown',
                message,
                onProgress: progress,
            });
            persistSyncedFrom(built.repo);
            // Saves are obsolete — the published commit captures their net
            // result. Clear the chain so the chips reset.
            await clearSaves(activeProject);
            refreshSaves();
            await refreshStatus();
            await refreshHistory();
            // Successful publish — wipe any stale info banner left over
            // from a prior auto-merge ("click Publish to push your
            // merged state"), since that action is now complete.
            infoBanner = null;
        } catch (e) {
            if (e instanceof HeadConflictError) {
                // The remote moved between our last poll and this Publish —
                // a genuine race. The safer flow is Pull-then-merge-then-
                // Publish (so the merged result is in the WT and the user
                // can inspect it before pushing). Fall through to a Pull
                // automatically so the user just sees "we pulled the new
                // changes for you, resolve and Publish again."
                log.warn('Publish raced with remote — auto-pulling to merge first.');
                await mergeFromRemote(built, 'publish-race');
            } else if (e instanceof Error && e.message === 'nothing to commit') {
                infoBanner = null;
            } else {
                errorBanner = errMsg(e);
            }
        }
    }

    /** Default label for a quick save when the user didn't type one.
     *  Auto-describes the change set so the history list is scannable
     *  ("3 files changed: main.fbasic, fade.json …") instead of a wall
     *  of identical "Quick save · HH:MM:SS" rows. Falls back to a
     *  timestamp if there are no changes to describe. */
    function defaultSaveMessage(): string {
        const changed = staged.filter((e) => e.status !== 'unchanged');
        const t = new Date();
        const hh = String(t.getHours()).padStart(2, '0');
        const mm = String(t.getMinutes()).padStart(2, '0');
        const ss = String(t.getSeconds()).padStart(2, '0');
        if (changed.length === 0) {
            return `Save · ${hh}:${mm}:${ss}`;
        }
        const added = changed.filter((e) => e.status === 'added').length;
        const modified = changed.filter((e) => e.status === 'modified').length;
        const deleted = changed.filter((e) => e.status === 'deleted').length;
        const counts: string[] = [];
        if (added)    counts.push(`+${added}`);
        if (modified) counts.push(`~${modified}`);
        if (deleted)  counts.push(`-${deleted}`);
        // Show first 3 paths inline so the row is scannable; clamp the
        // tail with an ellipsis. Path-only (no leading directory) so
        // long nested paths don't dominate the line.
        const headPaths = changed.slice(0, 3).map((e) => e.path.split('/').pop() ?? e.path);
        const more = changed.length > 3 ? ` +${changed.length - 3} more` : '';
        return `Save ${hh}:${mm} · ${counts.join(' ')} · ${headPaths.join(', ')}${more}`;
    }

    /** Build a default publish message from the pending-saves chain.
     *  Single save → its own message. Multiple → a header + bullet list. */
    function defaultPublishMessage(): string {
        if (pendingSaves.length === 0) return 'Update from playground';
        if (pendingSaves.length === 1) return pendingSaves[0].message;
        // pendingSaves is newest-first; reverse for chronological order in
        // the commit body.
        const chronological = [...pendingSaves].reverse();
        return [
            `Publish ${pendingSaves.length} saves`,
            '',
            ...chronological.map((s) => `- ${s.message}`),
        ].join('\n');
    }

    /** Extension-based binary classifier — used to decide diff3 (text) vs
     *  conflict-copy (binary) at merge time. Matches the playground's own
     *  binary list (sprites, audio, packed assets). Conservative — anything
     *  not matched is treated as text and given a diff3 attempt. */
    const BINARY_EXT = /\.(png|jpe?g|gif|webp|bmp|ico|tiff|wav|mp3|ogg|m4a|aac|flac|mp4|webm|mov|xnb|wasm|zip|exe|dll|pdf)$/i;
    function isLikelyBinary(path: string): boolean {
        return BINARY_EXT.test(path);
    }

    /** UTF-8 decode that returns null on invalid sequences. We refuse to
     *  diff3 anything that isn't clean UTF-8 — falling back to conflict-copy
     *  keeps binary files (with surprise extensions) from getting line-merged. */
    function decodeUtf8Strict(bytes: Uint8Array): string | null {
        try {
            return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
        } catch {
            return null;
        }
    }

    /**
     * Three-way merge between baseTree (last sync), local OPFS, and the new
     * remote HEAD. This is the merge entry point for Pull when local
     * divergence blocks a fast-forward, and a recovery path for Publish if
     * the remote moved between our last poll and the push attempt.
     *
     *   - Both-side-changed text → diff3 merge written in-place; markers
     *     surface as text-conflicts the user resolves in the editor.
     *   - Both-side-changed binary → `<path>.fade-conflict.<remote-sha-prefix>`
     *     written alongside; user picks via Use mine / Use theirs.
     *   - Remote-only → applied to OPFS straight up.
     *   - Local-only → kept intact.
     * Updates the sync-index to track the new remote HEAD as our base — the
     * user's next Publish will go cleanly once conflicts are resolved.
     *
     * `trigger` shapes the user-facing status message ("Pulled and merged…"
     * vs "Remote moved during publish…") but doesn't change the merge logic.
     */
    async function mergeFromRemote(
        built: { adapter: GitHubAdapter; repo: Repo },
        trigger: 'pull' | 'publish-race',
    ) {
        log.info(`Merging remote into working tree (trigger: ${trigger})`);
        const remoteSha = await built.adapter.branchHead();
        if (!remoteSha) {
            errorBanner = 'Remote branch has no HEAD. Try Pull.';
            log.warn('mergeFromRemote: remote branch has no HEAD');
            return;
        }
        if (remoteSha === index.syncedCommitSha) {
            // Nothing to merge — remote hasn't moved past our synced head.
            // The engine's stale-state edge case from before the
            // setSyncedHead rehydrate fix; defensive no-op here.
            log.warn(`mergeFromRemote: remote not actually ahead (${remoteSha.slice(0,8)} == syncedCommitSha)`);
            return;
        }
        // Flush in-flight Monaco edits to OPFS BEFORE we start writing
        // merged bytes. Without this, a keystroke that lands between the
        // localBytes snapshot below and the merge's writeFile would get
        // clobbered when the next autosave fires (file already on disk
        // from merge, autosave overwrites with the pre-merge editor
        // buffer). pull() and doPublish both already flush, but they do
        // so before the network round-trip — by the time we reach here
        // the user has had seconds to type more.
        if (opts.flushPendingSaves) await opts.flushPendingSaves();
        log.info(`Remote moved: ${index.syncedCommitSha?.slice(0,8) ?? '(none)'} → ${remoteSha.slice(0,8)}`);
        const remoteTree = await built.adapter.getTree(remoteSha);
        const baseTree = index.baseTree;
        const localPaths = await opts.workspace.list();
        const localSet = new Set(localPaths.filter((p) => !isHiddenFromCommits(p)));
        log.info(`Merge inputs: ${localSet.size} local file(s), ${Object.keys(remoteTree).length} remote, ${Object.keys(baseTree).length} base`);

        // Categorize per path. The same loop both detects conflicts and
        // applies the easy cases (remote-only, both-same-content).
        const textConflictPaths: string[] = [];     // diff3 emitted markers
        const binaryConflictPaths: string[] = [];   // conflict-copy written
        const autoMerged: string[] = [];            // diff3 merged cleanly
        const remoteOnly: string[] = [];
        const localOnly: string[] = [];

        const allPaths = new Set<string>([
            ...localSet,
            ...Object.keys(remoteTree),
            ...Object.keys(baseTree),
        ]);

        const shortRemote = remoteSha.slice(0, 8);

        for (const path of allPaths) {
            const baseHash = baseTree[path] ?? null;
            const remoteHash = remoteTree[path]?.blobSha ?? null;
            const localBytes = localSet.has(path)
                ? await opts.workspace.readBytes(path).catch(() => null)
                : null;
            const localHash = localBytes ? await gitBlobSha(localBytes) : null;

            const localChanged = (localHash ?? null) !== baseHash;
            const remoteChanged = remoteHash !== baseHash;

            if (!localChanged && !remoteChanged) continue;

            if (localChanged && remoteChanged) {
                if (localHash === remoteHash) {
                    // Both sides converged on the same content — no real conflict.
                    continue;
                }
                // Both-side change.
                const remoteBytes = remoteHash ? await safeGetBlob(built.adapter, remoteHash) : null;
                // If the path is binary or any side fails UTF-8, fall back
                // to conflict-copy — diff3 is line-level and would mangle
                // bytes that aren't legitimately newline-delimited text.
                const oursText = localBytes ? decodeUtf8Strict(localBytes) : null;
                const theirsText = remoteBytes ? decodeUtf8Strict(remoteBytes) : null;
                const baseBytes = baseHash ? await safeGetBlob(built.adapter, baseHash) : null;
                const baseText = baseBytes ? decodeUtf8Strict(baseBytes) : null;

                if (
                    !isLikelyBinary(path) &&
                    oursText !== null && theirsText !== null && baseText !== null
                ) {
                    const merge = diff3Merge(baseText, oursText, theirsText, {
                        oursLabel: 'ours',
                        theirsLabel: `theirs (${shortRemote})`,
                    });
                    const mergedBytes = new TextEncoder().encode(merge.merged);
                    try {
                        await writeFile(path, mergedBytes);
                    } catch (e) {
                        log.error(`Merge write failed for ${path}: ${errMsg(e)}`);
                        continue;
                    }
                    if (merge.hasConflicts) {
                        textConflictPaths.push(path);
                        textConflicts.add(path);
                        log.warn(`Merged ${path}: ${merge.conflicts.length} conflict region(s) — needs user resolution`);
                    } else {
                        autoMerged.push(path);
                        textConflicts.delete(path);
                        log.info(`Auto-merged ${path} (no overlapping changes)`);
                    }
                    continue;
                }

                // Binary fallback: write the remote bytes as a sibling
                // conflict copy and leave the live file as the user's.
                binaryConflictPaths.push(path);
                log.warn(`Binary conflict on ${path} — saved remote as sibling`);
                if (remoteBytes) {
                    const conflictPath = `${path}.fade-conflict.${shortRemote}`;
                    try { await writeFile(conflictPath, remoteBytes); }
                    catch (e) { log.error(`Could not write conflict copy for ${path}: ${errMsg(e)}`); }
                }
                continue;
            }
            if (remoteChanged && !localChanged) {
                if (remoteHash === null) {
                    if (localSet.has(path)) {
                        try { await deleteFile(path); } catch { /* fade.json guard etc — leave it */ }
                    }
                } else {
                    const remoteBytes = await safeGetBlob(built.adapter, remoteHash);
                    if (remoteBytes) {
                        try { await writeFile(path, remoteBytes); }
                        catch (e) { console.warn('[sharing] could not pull', path, e); }
                    }
                }
                remoteOnly.push(path);
                continue;
            }
            // localChanged && !remoteChanged → keep local as-is.
            localOnly.push(path);
        }

        // Advance our base to the EXACT remote HEAD we merged against —
        // not whatever the branch points at now. Using
        // `refreshSyncedHead()` (which re-fetches) would race: a fresh
        // third-party push between our merge and the refetch would land
        // an updated SHA into syncedHead while our merged working tree
        // only incorporated the older one. setSyncedHead writes through
        // without a network round-trip.
        const mergedCommit = await built.adapter.getCommit(remoteSha);
        built.repo.setSyncedHead(
            { commitSha: remoteSha, treeSha: mergedCommit.treeSha },
            remoteTree,
        );
        persistSyncedFrom(built.repo);

        // Race check: did the remote move AGAIN while we were merging?
        // If so the user needs another Pull before Publishing — otherwise
        // Publish will hit HeadConflictError and the auto-merger will
        // chain a second merge against an even-newer tree. Cheap to
        // detect with one extra branchHead() call.
        const remoteAfter = await built.adapter.branchHead();
        const racedAgain = remoteAfter && remoteAfter !== remoteSha;
        if (racedAgain) {
            log.warn(`Remote moved during merge: ${remoteSha.slice(0,8)} → ${remoteAfter.slice(0,8)}. Pull again before Publishing.`);
        }

        const totalConflicts = textConflictPaths.length + binaryConflictPaths.length;
        const parts: string[] = [];
        if (totalConflicts > 0) {
            parts.push(`${totalConflicts} file${totalConflicts > 1 ? 's' : ''} need resolution — see Conflicts below`);
        }
        if (autoMerged.length > 0) {
            parts.push(`${autoMerged.length} text file${autoMerged.length > 1 ? 's' : ''} auto-merged`);
        }
        if (remoteOnly.length > 0) {
            parts.push(`${remoteOnly.length} remote-only change${remoteOnly.length > 1 ? 's' : ''} applied`);
        }
        // Status banner: only surface when the user actually needs to do
        // something. Conflicts → block on resolution. Publish-race during
        // a Publish click → reroute to Publish-after-resolve. Remote
        // moved again mid-merge → tell user to Pull once more. Anything
        // else (clean auto-merge from Pull) stays silent; the log
        // channel already records what happened.
        const racedSuffix = racedAgain
            ? ` Remote moved again during merge (now at ${remoteAfter!.slice(0,8)}) — Pull once more before Publishing.`
            : '';
        if (totalConflicts > 0) {
            const verb = trigger === 'pull' ? 'Pulled' : 'Publish raced with remote';
            infoBanner = `${verb} — ${parts.join('; ')}.${racedSuffix}`;
        } else if (trigger === 'publish-race') {
            // Auto-pulled mid-publish with no conflicts — point the user
            // back at the Publish button so they finish the action they
            // started.
            infoBanner = `Remote moved during publish — auto-merged${parts.length ? ' (' + parts.join('; ') + ')' : ''}. Click Publish to push your merged state.${racedSuffix}`;
        } else if (racedAgain) {
            // Pull from a chip click, no conflicts, but remote moved
            // again — surface the staleness so the user pulls once more.
            infoBanner = `Pulled, but remote moved again during merge (now at ${remoteAfter!.slice(0,8)}) — Pull once more before Publishing.`;
        } else {
            // Clean pull, no conflicts, no race. Nothing to say.
            infoBanner = null;
        }
        void localOnly; // future: surface "your-only changes" count

        await refreshStatus();
        await refreshHistory();
        if (opts.onAfterPull) {
            const refreshed = [
                ...remoteOnly,
                ...autoMerged,
                ...textConflictPaths,
                ...binaryConflictPaths,
                ...binaryConflictPaths.map((p) => `${p}.fade-conflict.${shortRemote}`),
            ];
            if (refreshed.length > 0) await opts.onAfterPull(refreshed);
        }

        // UX: when there's exactly one text conflict, jump the user straight
        // into the conflict editor — by far the most common case, and the
        // friction of "where's the resolve button?" was the original
        // complaint that motivated this whole flow.  Multi-conflict cases
        // need the user to pick which to start with, so we leave it manual.
        if (textConflictPaths.length === 1 && opts.onOpenConflict) {
            const target = textConflictPaths[0];
            log.info(`Auto-opening conflict editor for ${target}`);
            // Defer one tick so the panel re-render lands before the dock
            // panel registration runs — avoids a flash of unstyled state.
            setTimeout(() => { void opts.onOpenConflict!(target); }, 50);
        }
    }

    /** Helper used by mergeFromRemote: swallow getBlob failures so a single
     *  bad blob can't abort the entire merge pass. */
    async function safeGetBlob(adapter: GitHubAdapter, blobSha: string): Promise<Uint8Array | null> {
        try { return await adapter.getBlob(blobSha); }
        catch (e) { console.warn('[sharing] getBlob failed', blobSha, e); return null; }
    }

    /** UTF-8 decode workspace bytes for the diff viewer. Null if the
     *  file isn't readable (deleted / binary path that failed decode). */
    async function readWorkspaceText(path: string): Promise<string | null> {
        try {
            const bytes = await opts.workspace.readBytes(path);
            return new TextDecoder('utf-8', { fatal: false }).decode(bytes);
        } catch {
            return null;
        }
    }

    /** Decode a save's base64-stored bytes into a UTF-8 string. Null
     *  on decode failure (binary save content that wasn't actually
     *  text). */
    function decodeBase64Text(b64: string): string | null {
        try {
            return new TextDecoder('utf-8', { fatal: false }).decode(base64ToBytes(b64));
        } catch {
            return null;
        }
    }

    /** Very small extension → Monaco language map. Defaults to
     *  plaintext so the diff editor still renders something readable
     *  even for unknown types. */
    function guessLanguageId(path: string): string {
        const lower = path.toLowerCase();
        if (lower.endsWith('.fbasic') || lower.endsWith('.fb')) return 'fade';
        if (lower.endsWith('.json'))      return 'json';
        if (lower.endsWith('.md'))        return 'markdown';
        if (lower.endsWith('.ts'))        return 'typescript';
        if (lower.endsWith('.js'))        return 'javascript';
        if (lower.endsWith('.html'))      return 'html';
        if (lower.endsWith('.css'))       return 'css';
        return 'plaintext';
    }

    /** Shared "build a diff viewer payload for context X, hand it to
     *  the host" logic. Used by the controller's `openDiffViewer`
     *  method AND by the inline Show-diff buttons in the publish
     *  preview + history saves/commits. */
    async function openDiffViewerImpl(args:
        | { kind: 'unsaved'; path: string }
        | { kind: 'publish'; path: string }
        | { kind: 'save'; saveId: string; path: string }
        | { kind: 'commit'; commitSha: string; path: string }
        | { kind: 'pull'; path: string }
    ): Promise<boolean> {
        if (!opts.onOpenDiff) return false;
        const language = guessLanguageId(args.path);
        if (args.kind === 'unsaved') {
            // Latest save → working tree. If there are no saves, fall
            // back to the published baseTree (same reference the panel
            // uses to compute `staged`).
            const topSave = pendingSaves[0];
            let beforeText: string | null;
            if (topSave) {
                const b64 = topSave.files[args.path];
                beforeText = b64 !== undefined ? decodeBase64Text(b64) : null;
            } else {
                beforeText = await fetchPublishedText(args.path);
            }
            const afterText = await readWorkspaceText(args.path);
            const beforeLabel = topSave ? `Save ${topSave.id.slice(0, 6)}` : 'Published';
            opts.onOpenDiff({
                id: `diff-viewer:unsaved:${args.path}`,
                title: `${args.path} (unsaved)`,
                path: args.path,
                languageId: language,
                beforeText,
                afterText,
                beforeLabel,
                afterLabel: 'Working tree',
            });
            return true;
        }
        if (args.kind === 'publish') {
            const beforeText = await fetchPublishedText(args.path);
            const afterText = await readWorkspaceText(args.path);
            opts.onOpenDiff({
                id: `diff-viewer:publish:${args.path}`,
                title: `${args.path} (publish preview)`,
                path: args.path,
                languageId: language,
                beforeText,
                afterText,
                beforeLabel: 'Published',
                afterLabel: 'Working tree',
            });
            return true;
        }
        if (args.kind === 'save') {
            const target = pendingSaves.find((s) => s.id === args.saveId);
            if (!target) return false;
            const idx = pendingSaves.indexOf(target);
            // pendingSaves is newest-first → the predecessor (older
            // save) sits at idx+1. Fall back to the published baseTree
            // if this is the oldest save in the chain.
            const prior = pendingSaves[idx + 1];
            const afterB64 = target.files[args.path];
            const afterText = afterB64 !== undefined ? decodeBase64Text(afterB64) : null;
            let beforeText: string | null;
            if (prior) {
                const priorB64 = prior.files[args.path];
                beforeText = priorB64 !== undefined ? decodeBase64Text(priorB64) : null;
            } else {
                beforeText = await fetchPublishedText(args.path);
            }
            opts.onOpenDiff({
                id: `diff-viewer:save:${args.saveId}:${args.path}`,
                title: `${args.path} (save: ${target.message.slice(0, 40)})`,
                path: args.path,
                languageId: language,
                beforeText,
                afterText,
                beforeLabel: prior ? `Save ${prior.id.slice(0, 6)}` : 'Published',
                afterLabel: `Save ${target.id.slice(0, 6)}`,
            });
            return true;
        }
        if (args.kind === 'commit') {
            const built = await buildRepo();
            if (!built) return false;
            const commit = await built.adapter.getCommit(args.commitSha);
            const tree = await built.adapter.getTree(args.commitSha);
            const afterSha = tree[args.path]?.blobSha;
            let afterText: string | null = null;
            if (afterSha) {
                const bytes = await safeGetBlob(built.adapter, afterSha);
                if (bytes) afterText = new TextDecoder('utf-8', { fatal: false }).decode(bytes);
            }
            let beforeText: string | null = null;
            if (commit.parents.length > 0) {
                const parentTree = await built.adapter.getTree(commit.parents[0]);
                const beforeSha = parentTree[args.path]?.blobSha;
                if (beforeSha) {
                    const bytes = await safeGetBlob(built.adapter, beforeSha);
                    if (bytes) beforeText = new TextDecoder('utf-8', { fatal: false }).decode(bytes);
                }
            }
            opts.onOpenDiff({
                id: `diff-viewer:commit:${args.commitSha}:${args.path}`,
                title: `${args.path} (commit ${args.commitSha.slice(0, 8)})`,
                path: args.path,
                languageId: language,
                beforeText,
                afterText,
                beforeLabel: commit.parents.length > 0 ? `Parent ${commit.parents[0].slice(0, 8)}` : '(empty)',
                afterLabel: `Commit ${args.commitSha.slice(0, 8)}`,
            });
            return true;
        }
        if (args.kind === 'pull') {
            // Preview "if I click Pull, what will this file become?".
            // Multiple pending remote commits collapse into one fast-
            // forward (or one 3-way merge) to remote HEAD, so the
            // after-side is whatever remoteHead's tree says — exactly
            // what `tryFastForward` would materialise in the clean
            // case. We re-fetch the tree on demand instead of caching;
            // the poll's tree is typically fresh and another fetch is
            // cheap (and avoids staleness if the user lingered).
            const built = await buildRepo();
            if (!built || remoteHeadSha === null) return false;
            const beforeText = await readWorkspaceText(args.path);
            let afterText: string | null = null;
            try {
                const remoteTree = await built.adapter.getTree(remoteHeadSha);
                const afterSha = remoteTree[args.path]?.blobSha;
                if (afterSha) {
                    const bytes = await safeGetBlob(built.adapter, afterSha);
                    if (bytes) afterText = new TextDecoder('utf-8', { fatal: false }).decode(bytes);
                }
            } catch (e) {
                console.warn('[sharing] pull-diff fetch failed', e);
            }
            opts.onOpenDiff({
                id: `diff-viewer:pull:${remoteHeadSha}:${args.path}`,
                title: `${args.path} (pull preview)`,
                path: args.path,
                languageId: language,
                beforeText,
                afterText,
                beforeLabel: 'Working tree',
                afterLabel: `Remote ${remoteHeadSha.slice(0, 8)}`,
            });
            return true;
        }
        return false;
    }

    /**
     * Overwrite a single locally-changed file with its last-synced base
     * content (effectively `git checkout -- <path>`). For locally-added
     * files (no base entry), removes the file instead. fade.json's delete
     * guard is honored — we just leave it if delete fails.
     */
    async function revertFile(path: string) {
        const built = await buildRepo();
        if (!built) { errorBanner = 'Not connected.'; render(); return; }
        if (opts.flushPendingSaves) await opts.flushPendingSaves();
        try {
            const baseHash = index.baseTree[path];
            if (baseHash) {
                const bytes = await built.adapter.getBlob(baseHash);
                await writeFile(path, bytes);
                log.info(`Reverted ${path} to base (${baseHash.slice(0, 8)})`);
            } else {
                try {
                    await deleteFile(path);
                    log.info(`Reverted ${path} (locally-added → deleted)`);
                } catch (e) {
                    log.warn(`Revert delete refused for ${path}: ${errMsg(e)}`);
                }
            }
            if (opts.onAfterPull) await opts.onAfterPull([path]);
            await refreshStatus();
        } catch (e) {
            errorBanner = `Revert failed: ${errMsg(e)}`;
            log.error(`Revert ${path} failed: ${errorBanner}`);
            render();
        }
    }

    /**
     * Revert *every* path that differs from the synced base. Confirms first
     * (this is destructive). Iterates through staged changes — added files
     * are deleted, modified/deleted are restored from the base blobs.
     */
    async function revertAll() {
        const changedPaths = staged
            .filter((e) => e.status !== 'unchanged')
            .map((e) => e.path);
        if (changedPaths.length === 0) return;
        if (!confirm(
            `Revert ALL ${changedPaths.length} local change${changedPaths.length === 1 ? '' : 's'}? ` +
            `Files will be overwritten with the last-synced content. This cannot be undone.`,
        )) return;
        await runBusy(`Reverting ${changedPaths.length} file${changedPaths.length === 1 ? '' : 's'}…`, async (progress) => {
            const built = await buildRepo();
            if (!built) { errorBanner = 'Not connected.'; return; }
            if (opts.flushPendingSaves) await opts.flushPendingSaves();
            for (let i = 0; i < changedPaths.length; i++) {
                const path = changedPaths[i];
                progress({ phase: 'apply', path, current: i + 1, total: changedPaths.length });
                try {
                    const baseHash = index.baseTree[path];
                    if (baseHash) {
                        const bytes = await built.adapter.getBlob(baseHash);
                        await writeFile(path, bytes);
                    } else {
                        try { await deleteFile(path); }
                        catch { /* fade.json guard etc */ }
                    }
                } catch (e) {
                    log.warn(`Revert ${path} failed: ${errMsg(e)}`);
                }
            }
            if (opts.onAfterPull) await opts.onAfterPull(changedPaths);
            await refreshStatus();
        });
    }

    /**
     * Mark a text file's conflict as resolved. Reads the live file, verifies
     * no `<<<<<<< / ======= / >>>>>>>` markers remain, and removes it from
     * the tracked-conflicts set. Refuses if markers are still present so the
     * user can't accidentally commit half-merged content.
     */
    async function resolveTextConflict(path: string) {
        try {
            const bytes = await opts.workspace.readBytes(path);
            const text = new TextDecoder('utf-8', { fatal: false }).decode(bytes);
            if (hasConflictMarkers(text)) {
                errorBanner = `${path} still has conflict markers — open it and finish merging first.`;
                render();
                return;
            }
            textConflicts.delete(path);
            errorBanner = null;
            await refreshStatus();
        } catch (e) {
            errorBanner = `Resolve failed: ${errMsg(e)}`;
            render();
        }
    }

    /**
     * User picked a side for a binary conflict file. `mine` = drop the
     * .fade-conflict copy and keep the live file as-is. `theirs` =
     * overwrite the live file with the conflict-copy bytes, then drop the
     * copy.
     */
    async function resolveConflict(conflictPath: string, choice: 'mine' | 'theirs') {
        const originalPath = conflictPath.replace(/\.fade-conflict\.[a-f0-9]+$/, '');
        try {
            if (choice === 'theirs') {
                const bytes = await opts.workspace.readBytes(conflictPath);
                await writeFile(originalPath, bytes);
            }
            await deleteFile(conflictPath);
            await refreshStatus();
            if (opts.onAfterPull) {
                // Refresh open editors for the original path so they pick up
                // any "use theirs" overwrite.
                await opts.onAfterPull([originalPath]);
            }
        } catch (e) {
            errorBanner = `Resolve failed: ${errMsg(e)}`;
            render();
        }
    }

    async function pull() {
        const built = await buildRepo();
        if (!built) { errorBanner = 'Not connected.'; return; }
        await runBusy('Pulling…', async (progress) => {
            if (opts.flushPendingSaves) await opts.flushPendingSaves();
            const wt = new OpfsWorkingTree(opts.workspace);
            const result = await built.repo.tryFastForward(wt, { onProgress: progress });
            if (result.applied) {
                // Clean fast-forward — remote moved, we had no local
                // divergence, the engine materialised the new tree.
                persistSyncedFrom(built.repo);
                if (opts.onAfterPull) {
                    await opts.onAfterPull(Object.keys(built.repo.getSyncedTree()));
                }
                infoBanner = null;
                await refreshStatus();
                await refreshHistory();
                return;
            }
            if (result.dirty) {
                // Local divergence (unsaved edits OR unpublished saves
                // OR both) — Pull does the 3-way merge so the user gets
                // remote changes before Publishing, the safer direction.
                // mergeFromRemote handles persisting + onAfterPull + log
                // status.
                await mergeFromRemote(built, 'pull');
                return;
            }
            // Already up-to-date.
            infoBanner = null;
        });
    }

    // ─── history-viewer helpers (consumed by the controller methods that
    //     the history dockview panel calls into) ────────────────────────────

    async function computeCommitDiff(sha: string) {
        const built = await buildRepo();
        if (!built) return null;
        const commit = recentCommits.find((c) => c.id === sha);
        if (!commit) return null;
        const [tree, parentTree] = await Promise.all([
            getCachedTree(built, sha),
            commit.parent ? getCachedTree(built, commit.parent) : Promise.resolve({} as GitTree),
        ]);
        return diffGitTrees(parentTree, tree);
    }

    async function getCachedTree(built: { adapter: GitHubAdapter }, sha: string): Promise<GitTree> {
        let tree = commitTreeCache.get(sha);
        if (!tree) {
            tree = await built.adapter.getTree(sha);
            commitTreeCache.set(sha, tree);
        }
        return tree;
    }

    /** Restore the working tree to an older commit's tree, then commit the
     *  result on top of the current branch HEAD. History is preserved — we
     *  never rewrite; the restore shows up as a new commit. */
    async function restoreCommit(targetSha: string) {
        const targetCommit = recentCommits.find((c) => c.id === targetSha);
        const short = targetSha.slice(0, 8);
        const msg = targetCommit ? `"${targetCommit.message.split('\n')[0].slice(0, 60)}"` : '';
        if (!confirm(`Restore the working tree to commit ${short} ${msg}? Any uncommitted local changes will be overwritten. The branch history is preserved — this creates a new commit on top of HEAD.`)) return;
        await runBusy(`Restoring to ${short}…`, async (progress) => {
            const built = await buildRepo();
            if (!built) { errorBanner = 'Not connected.'; return; }
            if (opts.flushPendingSaves) await opts.flushPendingSaves();
            // Align the engine with current branch HEAD before we materialize
            // and commit on top — otherwise the new commit's parent might be
            // the old syncedHead, and updateBranch would reject as non-FF.
            await built.repo.refreshSyncedHead();

            const targetTree = await getCachedTree(built, targetSha);

            // Apply target tree to the working tree. We do this manually
            // (not via repo.checkout) because checkout would also move the
            // engine's syncedHead to targetSha — which would then make the
            // subsequent commit emit `targetSha` as its parent and fail FF.
            const liveList = await opts.workspace.list();
            const liveSet = new Set(liveList.filter((p) => !isHiddenFromCommits(p)));
            const targetPaths = Object.entries(targetTree);
            for (let i = 0; i < targetPaths.length; i++) {
                const [path, entry] = targetPaths[i];
                progress({ phase: 'blob-download', path, current: i + 1, total: targetPaths.length });
                const bytes = await built.adapter.getBlob(entry.blobSha);
                progress({ phase: 'apply', path, current: i + 1, total: targetPaths.length });
                await writeFile(path, bytes);
            }
            for (const path of liveSet) {
                if (!(path in targetTree)) {
                    progress({ phase: 'delete', path });
                    try { await deleteFile(path); }
                    catch { /* fade.json delete-guard etc — leave it */ }
                }
            }

            // Commit the restored tree as a new commit on top of HEAD.
            const wt = new OpfsWorkingTree(opts.workspace);
            try {
                await built.repo.commit(wt, {
                    author: user?.login ?? 'unknown',
                    message: `Restore to ${short}${targetCommit ? ` (${targetCommit.message.split('\n')[0].slice(0, 40)})` : ''}`,
                    onProgress: progress,
                });
                persistSyncedFrom(built.repo);
                if (opts.onAfterPull) await opts.onAfterPull(Object.keys(targetTree));
                await refreshStatus();
                await refreshHistory();
                // Drop diff cache — the commit list shifted, old per-commit
                // diffs against old parents are still valid but the new tip
                // wasn't in the cache anyway.
                infoBanner = null;
            } catch (e) {
                errorBanner = `Restore failed: ${errMsg(e)}`;
            }
        });
    }

    async function refreshHistory() {
        const built = await buildRepo();
        if (!built) { recentCommits = []; emitHistory(); return; }
        try {
            const head = await built.adapter.branchHead();
            if (!head) { recentCommits = []; emitHistory(); return; }
            const log2 = await built.repo.log({ from: head, limit: 30 });
            recentCommits = log2.map((c) => ({
                id: c.sha,
                parent: c.parents[0] ?? null,
                message: c.message,
                author: c.author,
                time: c.time,
            }));
        } catch (e) {
            // Non-fatal but worth surfacing — a 404 here means listCommits
            // failed (e.g. the commits endpoint hiccuped on a brand-new
            // repo). Push to the Logs panel so the user can see why their
            // history is empty.
            log.warn(`History failed to load: ${errMsg(e)}`);
            console.warn('[sharing] refreshHistory failed', e);
            recentCommits = [];
        }
        emitHistory();
    }

    async function refreshStatus() {
        try {
            // Wait for any pending legacy-save upgrades so referenceTree()
            // returns the right answer the first time. Cheap when there's
            // nothing to upgrade (Promise.resolve()).
            await savesUpgrade;
            // No remote linked AND no local saves → there's no useful
            // baseline to diff against. Falling through would diff against
            // an empty baseTree and mark every working-tree file "added",
            // painting spurious A badges on a project the user hasn't
            // opted into git or local saves for. Reset all the status
            // surfaces and emit empty so renderFileList draws no badges.
            if (!isConnected(index) && pendingSaves.length === 0) {
                staged = [];
                publishStaged = [];
                conflictFiles = [];
                textConflicts = new Set();
                emitStatus();
                emitConflicts();
                render();
                return;
            }
            const wt = new OpfsWorkingTree(opts.workspace);
            staged = await computeStatus(wt, referenceTree(), hashCache);
            // Second pass: same working tree, but vs the *published* baseTree.
            // The HashCache is warm from the first call, so this only adds an
            // extra `wt.list()` + map lookups — no rereads, no rehashing.
            // When there are no pending saves, this matches `staged` exactly.
            publishStaged = pendingSaves.length === 0
                ? staged
                : await computeStatus(wt, index.baseTree, hashCache);
            // Read unfiltered list to pick up *.fade-conflict.* copies — those
            // are filtered out of the engine snapshot deliberately, so they
            // don't appear in `staged`.
            const rawList = await opts.workspace.list();
            const conflictFilesBefore = conflictFiles.length;
            conflictFiles = rawList.filter(isHiddenFromCommits);
            // Detect text-conflict markers across reloads + verify existing
            // entries. Strategy:
            //   1. Verify every path currently in textConflicts — if its
            //      markers are gone (user resolved manually), drop it.
            //   2. Scan every staged path (regardless of status) for markers
            //      and add to the set. We don't restrict by status because
            //      a hash-collision / classification edge case shouldn't
            //      hide a real conflict.
            const stillConflicted = new Set<string>();
            const oldSnap = [...textConflicts].sort().join('|');

            const checked = new Set<string>();
            for (const path of textConflicts) {
                checked.add(path);
                try {
                    const bytes = await opts.workspace.readBytes(path);
                    const text = new TextDecoder('utf-8', { fatal: false }).decode(bytes);
                    if (hasConflictMarkers(text)) stillConflicted.add(path);
                } catch { /* file gone — drop from set */ }
            }
            for (const entry of staged) {
                if (checked.has(entry.path)) continue;
                checked.add(entry.path);
                try {
                    const bytes = await opts.workspace.readBytes(entry.path);
                    const text = new TextDecoder('utf-8', { fatal: false }).decode(bytes);
                    if (hasConflictMarkers(text)) stillConflicted.add(entry.path);
                } catch { /* unreadable — ignore */ }
            }
            textConflicts = stillConflicted;
            const newSnap = [...textConflicts].sort().join('|');
            if (oldSnap !== newSnap) {
                log.info(`textConflicts now: [${[...textConflicts].join(', ') || '(none)'}]`);
            }
            // If the user just finished resolving the last conflict (text
            // or binary) — clear the stale merge banner. mergeFromRemote
            // sets infoBanner with "Pulled — 1 file need resolution",
            // and used to linger forever because neither the conflict
            // editor's onSave nor the Use mine/theirs flow clears it.
            // Detect the transition (had conflicts, now empty) and
            // reset the info channel only — errors stay put.
            const hadConflicts = oldSnap !== '' || conflictFiles.length > 0 || conflictFilesBefore > 0;
            const cleanNow = textConflicts.size === 0 && conflictFiles.length === 0;
            if (hadConflicts && cleanNow && infoBanner) {
                infoBanner = null;
            }
            emitStatus();
            emitConflicts();
        } catch (e) {
            console.warn('[sharing] refreshStatus failed', e);
            log.warn(`refreshStatus failed: ${errMsg(e)}`);
            staged = [];
            publishStaged = [];
            conflictFiles = [];
            textConflicts = new Set();
            emitConflicts();
        }
        render();
    }

    /**
     * Run a long-running action with a visible progress banner + log
     * channel. The action callback receives a `ProgressFn` it can pass into
     * `repo.commit/checkout/tryFastForward` so engine-level phases bubble
     * up into the UI and the Logs panel. For arbitrary work outside the
     * engine (e.g. "creating repo…"), the action can call the progress
     * function directly with a custom label by emitting a synthetic event;
     * usually it's simpler to just call `setBusy(label)`.
     */
    async function runBusy(initialLabel: string, fn: (progress: ProgressFn) => Promise<void>) {
        busy = { label: initialLabel };
        // Wipe the error channel at the start of every long-running
        // action: by entering the operation, the user is implicitly
        // retrying or moving on. Info banners stay put — they carry
        // multi-step state that survives across operations (e.g. "click
        // Publish to push your merged state" persists from a Pull's
        // auto-merge until the next Publish settles it).
        errorBanner = null;
        render();
        log.info(`▶ ${initialLabel}`);
        const progressFn: ProgressFn = (event) => {
            const { label, progress } = formatProgress(event);
            busy = { label, progress };
            log.info(label, progress);
            render();
        };
        try {
            await fn(progressFn);
            log.info(`✓ ${initialLabel}`);
        } catch (e) {
            errorBanner = errMsg(e);
            log.error(`✗ ${initialLabel}: ${errorBanner}`);
        } finally {
            busy = null;
            render();
        }
    }

    /** Map an engine ProgressEvent into a human label + optional bar info. */
    function formatProgress(event: ProgressEvent): { label: string; progress?: { current: number; total: number } } {
        switch (event.phase) {
            case 'snapshot':       return { label: 'Snapshotting working tree…' };
            case 'diff': {
                const total = event.added + event.modified + event.deleted;
                return { label: `Computed diff: +${event.added} ~${event.modified} -${event.deleted} (${total} file${total === 1 ? '' : 's'})` };
            }
            case 'blob-upload':    return { label: `Uploading ${event.path}`, progress: { current: event.current, total: event.total } };
            case 'blob-download':  return { label: `Downloading ${event.path}`, progress: { current: event.current, total: event.total } };
            case 'apply':          return { label: `Writing ${event.path}`,    progress: { current: event.current, total: event.total } };
            case 'delete':         return { label: `Removing ${event.path}` };
            case 'fetch-tree':     return { label: `Fetching tree at ${event.commitSha.slice(0, 8)}…` };
            case 'tree':           return { label: 'Building tree object…' };
            case 'commit-object':  return { label: 'Creating commit object…' };
            case 'update-branch':  return { label: 'Updating branch ref…' };
        }
    }

    // ─── bootstrap: try to reuse a stored token ─────────────────────────────
    (async function bootstrap() {
        // One-shot migration of any pre-OPFS save chains so the user
        // doesn't lose history when we switch backends. Best-effort; a
        // failure here just delays the move until next boot.
        try {
            const moved = await migrateLegacyLocalStorageSaves();
            if (moved > 0) log.info(`Migrated ${moved} legacy save chain(s) from localStorage to OPFS.`);
        } catch (e) {
            log.warn(`Save migration skipped: ${errMsg(e)}`);
        }
        // Boot path: load the stored TokenSet (migrating any legacy
        // localStorage PAT into it), refresh if needed, validate the
        // resulting access token by hitting /user. A 401 means the
        // token (refresh-token included) is no longer good; wipe.
        const accessToken = await ensureFreshAccessToken();
        if (accessToken) {
            try {
                user = await validateToken(accessToken);
            } catch {
                tokenStore.clear();
                user = null;
            }
        }
        refreshSaves();
        await refreshStatus();
        if (user) await refreshHistory();
        if (user && isConnected(index)) startPolling();
        render();
    })();

    // ─── controller exposed to the host ─────────────────────────────────────
    return {
        setActiveProject(name: string) {
            activeProject = name;
            index = loadSyncIndex(name);
            staged = [];
            publishStaged = [];
            recentCommits = [];
            remoteHeadSha = null;
            pendingPullPaths = new Set();
            emitPendingPull();
            stopPolling();
            refreshSaves();
            emitStatus();
            render();
            void (async () => {
                await refreshStatus();
                if (user && isConnected(index)) {
                    await refreshHistory();
                    startPolling();
                }
                render();
            })();
        },
        refreshStatus,
        async refreshStatusForFile(path: string) {
            hashCache.invalidate(path);
            await refreshStatus();
        },
        invalidateHashFor(path: string) {
            hashCache.invalidate(path);
        },
        setHasDirtyTabs(b) {
            if (hasDirtyTabs === b) return;
            hasDirtyTabs = b;
            // Cheap re-render — the Save button's enabled-state reads
            // `hasDirtyTabs`, and refreshStatus is too expensive (does a
            // full working-tree hash) just to flip one button.
            render();
        },
        onStatusChange(listener) {
            statusListeners.add(listener);
            listener(statusMapFromStaged());
            return () => { statusListeners.delete(listener); };
        },
        getStatusMap: statusMapFromStaged,
        onPendingPullChange(listener) {
            pendingPullListeners.add(listener);
            listener(new Set(pendingPullPaths));
            return () => { pendingPullListeners.delete(listener); };
        },
        getPendingPullPaths() {
            return new Set(pendingPullPaths);
        },
        onConflictChange(listener) {
            conflictListeners.add(listener);
            listener({ text: new Set(textConflicts), binary: new Set(conflictFiles) });
            return () => { conflictListeners.delete(listener); };
        },
        getConflictPaths() {
            return { text: new Set(textConflicts), binary: new Set(conflictFiles) };
        },
        openConflictEditor(path) {
            if (!opts.onOpenConflict) return false;
            void opts.onOpenConflict(path);
            return true;
        },
        openDiffViewer(args) { return openDiffViewerImpl(args); },
        getRecentCommits() {
            return [...recentCommits];
        },
        onHistoryChange(listener) {
            historyListeners.add(listener);
            listener([...recentCommits]);
            return () => { historyListeners.delete(listener); };
        },
        async getCommitDiff(sha: string) {
            const cached = commitDiffCache.get(sha);
            if (cached) return cached;
            const fresh = await computeCommitDiff(sha);
            if (fresh) commitDiffCache.set(sha, fresh);
            return fresh ?? null;
        },
        restoreCommit(sha: string) {
            return restoreCommit(sha);
        },
        getRepoInfo() {
            return index.remoteRepo ? { ...index.remoteRepo } : null;
        },
        async getBaseText(path: string): Promise<string | null> {
            return await fetchPublishedText(path);
        },
        async getPublishedText(path: string): Promise<string | null> {
            return await fetchPublishedText(path);
        },
        onSavesChange(listener) {
            savesListeners.add(listener);
            listener([...pendingSaves]);
            return () => { savesListeners.delete(listener); };
        },
        getPendingSaves() {
            return [...pendingSaves];
        },
        async dropLocalSave(id) {
            await dropSave(activeProject, id);
            refreshSaves();
            void refreshStatus();
        },
        async revertToLocalSave(id) {
            const target = pendingSaves.find((s) => s.id === id);
            if (!target) return;
            if (!confirm(`Revert the working tree to "${target.message}"? Current uncommitted changes will be overwritten.`)) return;
            await runBusy(`Reverting to save…`, async () => {
                if (opts.flushPendingSaves) await opts.flushPendingSaves();
                await revertToSave(opts.workspace, target);
                for (const path of Object.keys(target.files)) hashCache.invalidate(path);
                if (opts.onAfterPull) await opts.onAfterPull(Object.keys(target.files));
                await refreshStatus();
            });
        },
        async getSaveDiff(id) {
            const idx = pendingSaves.findIndex((s) => s.id === id);
            if (idx < 0) return null;
            const save = pendingSaves[idx];
            if (!save.treeHashes) return null;
            // Diff against the prior save (next index, since newest-first)
            // or the published baseTree if this is the oldest save.
            const priorTree: Record<string, string> =
                idx < pendingSaves.length - 1
                    ? (pendingSaves[idx + 1].treeHashes ?? index.baseTree)
                    : index.baseTree;
            // Build GitTree-shaped objects for diffGitTrees.
            const toTree = (h: Record<string, string>) => {
                const out: Record<string, { blobSha: string }> = {};
                for (const [p, b] of Object.entries(h)) out[p] = { blobSha: b };
                return out;
            };
            const { diffGitTrees } = await import('./git-types');
            return diffGitTrees(toTree(priorTree), toTree(save.treeHashes));
        },
        async getSavedText(path: string): Promise<string | null> {
            // Walk newest-first; the first save that has this path is the
            // user's latest local checkpoint. Falls back to the published
            // text if no save contains the path.
            for (const save of pendingSaves) {
                const b64 = save.files[path];
                if (b64 !== undefined) {
                    try {
                        return new TextDecoder('utf-8', { fatal: false })
                            .decode(base64ToBytes(b64));
                    } catch {
                        return null;
                    }
                }
            }
            return await fetchPublishedText(path);
        },
    };

    /** Shared by getBaseText / getPublishedText. Hits the per-blob-sha
     *  cache to avoid re-fetching identical content.
     *
     *  Returns:
     *    - `null` when there's no remote at all (publish doesn't apply, so
     *      the gutter falls back to a single-state view).
     *    - `''` when there IS a remote but `path` isn't in it yet (a file
     *      added since last publish — every line is a change-since-publish).
     *    - the decoded text when `path` is on the remote. */
    async function fetchPublishedText(path: string): Promise<string | null> {
        if (!index.remoteRepo) return null;
        const blobSha = index.baseTree[path];
        if (!blobSha) return '';
        let bytes = baseContentCache.get(blobSha);
        if (!bytes) {
            const built = await buildRepo();
            if (!built) return null;
            try {
                bytes = await built.adapter.getBlob(blobSha);
                baseContentCache.set(blobSha, bytes);
            } catch {
                return null;
            }
        }
        try {
            return new TextDecoder('utf-8', { fatal: false }).decode(bytes);
        } catch {
            return null;
        }
    }
}

/** Inline base64 → bytes helper; mirrors the one in local-saves.ts so
 *  the panel doesn't need to import private helpers from there. */
function base64ToBytes(b64: string): Uint8Array {
    const clean = b64.replace(/\s+/g, '');
    if (typeof Buffer !== 'undefined') {
        const buf = Buffer.from(clean, 'base64');
        return new Uint8Array(buf.buffer, buf.byteOffset, buf.byteLength);
    }
    const bin = atob(clean);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
}

// ─── helpers ────────────────────────────────────────────────────────────────

function errMsg(e: unknown): string {
    if (e instanceof Error) return e.message;
    return String(e);
}

function el<K extends keyof HTMLElementTagNameMap>(tag: K, className: string, textContent?: string): HTMLElementTagNameMap[K] {
    const n = document.createElement(tag);
    n.className = className;
    if (textContent !== undefined) n.textContent = textContent;
    return n;
}

function text(s: string): Text { return document.createTextNode(s); }

function p(s: string, modifier?: 'dim'): HTMLElement {
    const n = el('p', `${CSS_PREFIX}-p` + (modifier === 'dim' ? ` ${CSS_PREFIX}-dim` : ''));
    n.textContent = s;
    return n;
}

function heading(s: string, level: 1 | 2 = 1): HTMLElement {
    const tag = level === 2 ? 'h4' : 'h3';
    const n = el(tag, `${CSS_PREFIX}-h${level}`);
    n.textContent = s;
    return n;
}

function row(...children: Array<Node | string>): HTMLElement {
    const r = el('div', `${CSS_PREFIX}-row`);
    for (const c of children) {
        if (typeof c === 'string') r.appendChild(text(c));
        else r.appendChild(c);
    }
    return r;
}

/** Like `row` but the layout is "input flexes; button hugs right." Used
 *  for the Save and Publish text-box/button pairs. */
function rowInline(...children: Array<Node | string>): HTMLElement {
    const r = el('div', `${CSS_PREFIX}-row-inline`);
    for (const c of children) {
        if (typeof c === 'string') r.appendChild(text(c));
        else r.appendChild(c);
    }
    return r;
}

function banner(s: string, kind: 'busy' | 'err' | 'info'): HTMLElement {
    const b = el('div', `${CSS_PREFIX}-banner ${CSS_PREFIX}-banner-${kind}`);
    b.textContent = s;
    return b;
}

function renderBusyBanner(state: { label: string; progress?: { current: number; total: number } }): HTMLElement {
    const b = el('div', `${CSS_PREFIX}-banner ${CSS_PREFIX}-banner-busy`);
    const label = el('div', `${CSS_PREFIX}-banner-label`);
    label.textContent = state.label;
    b.append(label);
    if (state.progress && state.progress.total > 0) {
        const pct = Math.min(100, Math.max(0, (state.progress.current / state.progress.total) * 100));
        const barWrap = el('div', `${CSS_PREFIX}-bar-wrap`);
        const bar = el('div', `${CSS_PREFIX}-bar`);
        bar.style.width = `${pct.toFixed(1)}%`;
        barWrap.append(bar);
        const pctLabel = el('span', `${CSS_PREFIX}-bar-pct`);
        pctLabel.textContent = `${state.progress.current}/${state.progress.total}`;
        b.append(barWrap, pctLabel);
    }
    return b;
}

type ButtonVariant = 'primary' | 'ghost' | 'primary-small' | 'ghost-small' | 'revert-mini';
function button(label: string, variant: ButtonVariant, onClick: () => void): HTMLButtonElement {
    const b = document.createElement('button');
    b.className = `${CSS_PREFIX}-btn ${CSS_PREFIX}-btn-${variant}`;
    b.textContent = label;
    b.type = 'button';
    b.onclick = onClick;
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
    padding: 8px 10px;
    overflow-y: auto;
    /* Match the workspace sidebar so the panel reads as another sidebar
       rather than as an unrelated chunk of the editor canvas. */
    background: var(--bg-2);
    color: var(--fg);
    font: 13px/1.4 ui-sans-serif, system-ui, sans-serif;
}
.${CSS_PREFIX}-header {
    display: flex; align-items: center; justify-content: space-between;
    gap: 8px; padding-bottom: 8px;
    border-bottom: 1px solid var(--vscode-panel-border, #333);
    margin-bottom: 8px;
}
.${CSS_PREFIX}-statusrow {
    display: flex; gap: 6px; flex-wrap: wrap;
    margin-bottom: 10px;
}
.${CSS_PREFIX}-chip {
    appearance: none; cursor: pointer;
    padding: 3px 10px; border-radius: 999px;
    font: inherit; font-size: 11px; font-weight: 600;
    border: 1px solid transparent;
    transition: filter 0.1s;
}
.${CSS_PREFIX}-chip:hover:not(:disabled) { filter: brightness(1.2); }
.${CSS_PREFIX}-chip:disabled { opacity: 0.6; cursor: progress; }
.${CSS_PREFIX}-chip-unsaved {
    background: rgba(255,200,90,0.15);
    color: #fc6;
    border-color: rgba(255,200,90,0.4);
}
.${CSS_PREFIX}-chip-unpublished {
    background: rgba(180,140,240,0.15);
    color: #cfa8ff;
    border-color: rgba(180,140,240,0.4);
}
.${CSS_PREFIX}-chip-remote {
    background: rgba(80,140,200,0.15);
    color: #88c8ff;
    border-color: rgba(80,140,200,0.4);
}
.${CSS_PREFIX}-chip-conflict {
    background: rgba(255,90,90,0.18);
    color: #ffb0b0;
    border-color: rgba(255,90,90,0.4);
}
.${CSS_PREFIX}-chip-check {
    background: transparent;
    color: inherit;
    border-color: var(--vscode-panel-border, #555);
    opacity: 0.75;
}
.${CSS_PREFIX}-chip-check:hover:not(:disabled) {
    opacity: 1;
    background: rgba(255,255,255,0.05);
}
.${CSS_PREFIX}-who { font-family: ui-monospace, monospace; font-size: 12px; }
.${CSS_PREFIX}-body { display: flex; flex-direction: column; gap: 8px; }
.${CSS_PREFIX}-section { display: flex; flex-direction: column; gap: 8px; }
.${CSS_PREFIX}-h1 { font-size: 13px; font-weight: 600; margin: 4px 0 2px; opacity: 0.95; }
.${CSS_PREFIX}-h2 { font-size: 11px; font-weight: 600; margin: 8px 0 2px; opacity: 0.75; text-transform: uppercase; letter-spacing: 0.04em; }
.${CSS_PREFIX}-p { margin: 0; opacity: 0.92; }
.${CSS_PREFIX}-dim { opacity: 0.55; font-size: 12px; }
.${CSS_PREFIX}-row { display: flex; gap: 6px; flex-wrap: wrap; align-items: center; }
.${CSS_PREFIX}-link { color: var(--vscode-textLink-foreground, #4da6ff); text-decoration: none; font-family: ui-monospace, monospace; }
.${CSS_PREFIX}-link:hover { text-decoration: underline; }
.${CSS_PREFIX}-banner {
    padding: 6px 10px; border-radius: 4px; font-size: 12px;
}
.${CSS_PREFIX}-banner-busy {
    background: rgba(80,140,200,0.15); color: #aac8ff;
    display: flex; flex-direction: column; gap: 4px;
}
.${CSS_PREFIX}-banner-label { font-size: 12px; }
.${CSS_PREFIX}-bar-wrap {
    width: 100%; height: 4px; background: rgba(255,255,255,0.08);
    border-radius: 2px; overflow: hidden;
}
.${CSS_PREFIX}-bar {
    height: 100%; background: #4da6ff;
    transition: width 0.15s ease-out;
}
.${CSS_PREFIX}-bar-pct { font-size: 11px; opacity: 0.7; text-align: right; }
.${CSS_PREFIX}-banner-err  { background: rgba(200,80,80,0.18); color: #f88; }
.${CSS_PREFIX}-banner-info { background: rgba(77,166,255,0.12); color: #9ecbff; border: 1px solid rgba(77,166,255,0.3); }
.${CSS_PREFIX}-repo-header {
    display: flex; align-items: baseline; gap: 12px;
    justify-content: space-between;
    margin-bottom: 4px;
}
.${CSS_PREFIX}-repo-line {
    flex: 1 1 auto;
    display: flex; align-items: baseline; gap: 4px;
    overflow: hidden; text-overflow: ellipsis;
}
.${CSS_PREFIX}-section-header {
    display: flex; align-items: baseline; gap: 8px;
    justify-content: space-between;
}
.${CSS_PREFIX}-stagedlist {
    list-style: none; padding: 0; margin: 0;
    display: flex; flex-direction: column; gap: 1px;
    background: rgba(255,255,255,0.03); border-radius: 4px;
    padding: 4px 0;
}
.${CSS_PREFIX}-staged {
    display: flex; align-items: center; gap: 8px;
    padding: 2px 8px; font-family: ui-monospace, monospace; font-size: 12px;
}
.${CSS_PREFIX}-staged .${CSS_PREFIX}-path { flex: 1 1 auto; }
.${CSS_PREFIX}-btn-revert-mini {
    background: transparent;
    color: inherit;
    border: 1px solid var(--vscode-panel-border, #555);
    padding: 0 6px; font-size: 10px; line-height: 16px;
    opacity: 0.55;
}
.${CSS_PREFIX}-staged:hover .${CSS_PREFIX}-btn-revert-mini {
    opacity: 1;
}
.${CSS_PREFIX}-btn-revert-mini:hover {
    background: rgba(255,120,120,0.15);
    color: #f88;
    border-color: rgba(255,120,120,0.4);
}
.${CSS_PREFIX}-glyph {
    width: 14px; text-align: center; font-weight: 700;
}
.${CSS_PREFIX}-staged-added    .${CSS_PREFIX}-glyph { color: #6e6; }
.${CSS_PREFIX}-staged-modified .${CSS_PREFIX}-glyph { color: #fc6; }
.${CSS_PREFIX}-staged-deleted  .${CSS_PREFIX}-glyph { color: #f88; }
.${CSS_PREFIX}-path { word-break: break-all; }
.${CSS_PREFIX}-pubprev {
    margin-top: 6px;
    padding: 6px 8px;
    background: rgba(184,138,255,0.06);
    border-left: 2px solid #b88aff;
    border-radius: 0 4px 4px 0;
    display: flex; flex-direction: column; gap: 4px;
}
.${CSS_PREFIX}-pubprev-h {
    font-size: 11px; font-weight: 600;
    text-transform: uppercase; letter-spacing: 0.04em;
    opacity: 0.85;
}
.${CSS_PREFIX}-pubprev-counts {
    font-family: ui-monospace, monospace;
    font-size: 11px; font-weight: 400;
    opacity: 0.7;
    text-transform: none; letter-spacing: 0;
}
.${CSS_PREFIX}-pubprev-list {
    list-style: none; padding: 0; margin: 0;
    display: flex; flex-direction: column; gap: 1px;
}
.${CSS_PREFIX}-pubprev-row {
    display: flex; align-items: center; gap: 8px;
    padding: 1px 0; font-family: ui-monospace, monospace; font-size: 11px;
}
.${CSS_PREFIX}-msg {
    width: 100%; box-sizing: border-box;
    background: rgba(255,255,255,0.05);
    color: inherit;
    border: 1px solid var(--vscode-panel-border, #444);
    border-radius: 4px;
    padding: 6px 8px;
    font: inherit; font-size: 12px;
    resize: vertical;
}
.${CSS_PREFIX}-msg:focus { outline: 2px solid var(--vscode-focusBorder, #0e639c); outline-offset: -1px; }
.${CSS_PREFIX}-msg-inline {
    flex: 1 1 auto; min-width: 0; box-sizing: border-box;
    background: rgba(255,255,255,0.05);
    color: inherit;
    border: 1px solid var(--vscode-panel-border, #444);
    border-radius: 4px;
    padding: 4px 8px;
    font: inherit; font-size: 12px;
    height: 28px;
}
.${CSS_PREFIX}-msg-inline:focus { outline: 2px solid var(--vscode-focusBorder, #0e639c); outline-offset: -1px; }
.${CSS_PREFIX}-msg-inline:disabled { opacity: 0.5; cursor: not-allowed; }
.${CSS_PREFIX}-row-inline {
    display: flex; align-items: stretch; gap: 6px;
}
.${CSS_PREFIX}-row-inline > button { flex: 0 0 auto; }
.${CSS_PREFIX}-pullbox {
    margin: 4px 0;
    padding: 8px 10px;
    background: rgba(77,166,255,0.08);
    border-left: 2px solid #4da6ff;
    border-radius: 0 4px 4px 0;
    display: flex; flex-direction: column; gap: 6px;
}
.${CSS_PREFIX}-pullbox-headrow {
    display: flex; align-items: center; gap: 12px;
    justify-content: space-between;
}
.${CSS_PREFIX}-pullbox-h {
    font-size: 12px; color: #4da6ff; font-weight: 600;
}
.${CSS_PREFIX}-pullbox-list {
    list-style: none; padding: 0; margin: 0;
    display: flex; flex-direction: column; gap: 1px;
}
.${CSS_PREFIX}-pullbox-row {
    display: flex; align-items: center; gap: 8px;
    padding: 1px 0; font-family: ui-monospace, monospace; font-size: 11px;
}
.${CSS_PREFIX}-pullbox-row .${CSS_PREFIX}-path { flex: 1 1 auto; min-width: 0; }
.${CSS_PREFIX}-history {
    list-style: none; padding: 0; margin: 0;
    display: flex; flex-direction: column; gap: 2px;
}
.${CSS_PREFIX}-history li {
    padding: 2px 4px; font-size: 12px;
    border-left: 2px solid var(--vscode-panel-border, #444);
    padding-left: 8px;
}
.${CSS_PREFIX}-history-header {
    display: flex; align-items: baseline; gap: 4px;
    padding: 2px 0;
    user-select: none;
}
.${CSS_PREFIX}-history-header:hover {
    background: rgba(255,255,255,0.04);
    border-radius: 3px;
}
.${CSS_PREFIX}-caret {
    width: 12px; font-size: 9px; color: var(--vscode-icon-foreground, #888);
    flex-shrink: 0;
}
.${CSS_PREFIX}-history-detail {
    margin: 4px 0 8px 14px;
    padding: 4px 8px;
    background: rgba(255,255,255,0.03);
    border-radius: 4px;
    display: flex; flex-direction: column; gap: 4px;
}
.${CSS_PREFIX}-commitid { font-family: ui-monospace, monospace; color: var(--vscode-textLink-foreground, #4da6ff); }
.${CSS_PREFIX}-commitid-link {
    text-decoration: none;
    padding: 0 2px;
    border-radius: 2px;
}
.${CSS_PREFIX}-commitid-link:hover {
    text-decoration: underline;
    background: rgba(77,166,255,0.1);
}
.${CSS_PREFIX}-commitid-link::after {
    content: ' ↗';
    font-size: 9px;
    opacity: 0.6;
}
.${CSS_PREFIX}-commitmsg { }
.${CSS_PREFIX}-btn {
    appearance: none; border: 0; cursor: pointer;
    padding: 6px 12px; border-radius: 4px;
    font: inherit; font-weight: 500;
    transition: filter 0.1s;
}
.${CSS_PREFIX}-btn:hover { filter: brightness(1.15); }
.${CSS_PREFIX}-btn:disabled { opacity: 0.4; cursor: not-allowed; filter: none; }
.${CSS_PREFIX}-btn-primary {
    background: var(--vscode-button-background, #0e639c);
    color: var(--vscode-button-foreground, #fff);
}
.${CSS_PREFIX}-btn-primary-small {
    background: var(--vscode-button-background, #0e639c);
    color: var(--vscode-button-foreground, #fff);
    padding: 3px 8px; font-size: 11px;
}
.${CSS_PREFIX}-btn-ghost {
    background: transparent; color: inherit;
    border: 1px solid var(--vscode-panel-border, #555);
}
.${CSS_PREFIX}-btn-ghost-small {
    background: transparent; color: inherit;
    border: 1px solid var(--vscode-panel-border, #555);
    padding: 3px 8px; font-size: 11px;
}

/* Local-save list row. Mirrors the staged-changes row geometry but with
   a richer layout (message + metadata + action row). */
.${CSS_PREFIX}-save {
    display: flex; flex-direction: column; gap: 4px;
    padding: 6px 8px;
    background: rgba(180,140,240,0.06);
    border-left: 2px solid rgba(180,140,240,0.5);
    margin-bottom: 2px;
    font-size: 12px;
}
.${CSS_PREFIX}-save-row {
    display: flex; align-items: baseline; gap: 8px;
    justify-content: space-between;
}
.${CSS_PREFIX}-save-msg { font-weight: 500; flex: 1 1 auto; word-break: break-word; }
.${CSS_PREFIX}-save-meta { font-family: ui-monospace, monospace; font-size: 11px; flex-shrink: 0; }

/* Conflict banner — prominently above all the connected-repo UI so the
   user can't miss a conflict state. */
.${CSS_PREFIX}-conflict-banner {
    display: flex; align-items: center; gap: 10px;
    padding: 8px 12px; border-radius: 6px;
    background: rgba(255,90,90,0.18);
    border: 1px solid rgba(255,90,90,0.4);
    color: #ffb0b0;
    font-weight: 500;
    margin-bottom: 6px;
}
.${CSS_PREFIX}-conflict-banner-msg { flex: 1 1 auto; font-size: 12px; }

/* A/M/D badges rendered in the workspace file list (renderFileList) — global
   selectors, not prefixed, because they live in a different DOM subtree.
   Mirrors the existing .source-badge geometry so the two badge styles align. */
.sharing-status {
    display: inline-block;
    min-width: 14px; padding: 0 4px; margin-left: 4px;
    border-radius: 3px;
    font-family: ui-monospace, monospace;
    font-size: 10px; font-weight: 700; line-height: 14px;
    text-align: center;
    vertical-align: middle;
}
/* Each badge picks its text color from a CSS variable so light themes can
   dial up the contrast without losing the semantic hue. Defaults below are
   the original pastel-on-dark; per-theme overrides live in index.html under
   the matching html[data-theme] block. */
.sharing-added    { background: rgba(110,230,110,0.18); color: var(--badge-added-fg, #6e6); }
.sharing-modified { background: rgba(255,200,90,0.18);  color: var(--badge-modified-fg, #fc6); }
.sharing-deleted  { background: rgba(255,120,120,0.18); color: var(--badge-deleted-fg, #f88); }
.sharing-pending-pull {
    background: rgba(80,140,200,0.20); color: var(--badge-pull-fg, #88c8ff);
    font-weight: 700;
}
.sharing-conflict {
    background: rgba(255,90,90,0.25); color: var(--badge-conflict-fg, #ffb0b0);
    font-weight: 700;
    border: 1px solid rgba(255,90,90,0.55);
}
.sharing-conflict-sibling {
    background: rgba(255,170,90,0.15); color: var(--badge-sibling-fg, #ffc890);
    font-weight: 500;
    font-style: italic;
    text-transform: uppercase;
    letter-spacing: 0.04em;
}
li.fade-conflict-sibling {
    opacity: 0.85;
}

/* Monaco gutter decorations — narrow vertical bars in the editor margin.
   linesDecorationsClassName attaches these to the gutter column. */
.sharing-gutter {
    width: 3px !important;
    margin-left: 3px;
}
/* Tri-state gutter (see line-diff.ts → lineDiffTriState):
   - unsaved  = edits since the latest local save (live in the editor)
   - saved    = edits already in a local save, not yet published
   The colours match the panel's chips so it all reads as one system. */
.sharing-gutter-unsaved { background-color: #ffb74d; }
.sharing-gutter-saved   { background-color: #b88aff; }
/* Deletion anchors stack a coloured border on the marker line. Unsaved
   deletions win when they overlap saved deletions (border-top wins via
   declaration order in the class list). */
.sharing-gutter-deletion-above { border-top:    4px solid transparent; }
.sharing-gutter-deletion-below { border-bottom: 4px solid transparent; }
.sharing-gutter-deletion-unsaved.sharing-gutter-deletion-above { border-top-color: #f44336; }
.sharing-gutter-deletion-unsaved.sharing-gutter-deletion-below { border-bottom-color: #f44336; }
.sharing-gutter-deletion-saved.sharing-gutter-deletion-above   { border-top-color: #b88aff; }
.sharing-gutter-deletion-saved.sharing-gutter-deletion-below   { border-bottom-color: #b88aff; }

/* Conflict list styling — distinct from regular staged-change rows. */
.${CSS_PREFIX}-conflict {
    display: flex; flex-direction: column; gap: 4px;
    padding: 6px 8px;
    background: rgba(255,120,120,0.06);
    border-left: 2px solid #f88;
    margin-bottom: 2px;
    font-family: ui-monospace, monospace;
    font-size: 12px;
}
.${CSS_PREFIX}-conflict-row {
    display: flex; align-items: center; gap: 8px;
}
`;
    document.head.appendChild(style);
}

// Re-exported for the file-list-badge code to consume.
export { pathStatusHint, statusGlyph } from './file-status';
export type { FileStatus } from './file-status';
