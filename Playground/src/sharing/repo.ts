// Repo — the playground-side engine that drives commit / checkout /
// fast-forward against any GitAdapter.
//
// Compared to the prior "fake-git on a key-value store" version, this one is
// noticeably smaller: there's no manifest format to serialize, no
// custom-commit-id to compute, no objects-directory to manage. The engine
// just orchestrates git's own primitives — createBlob → createTree →
// createCommit → updateBranch on writes; getCommit → getTree → getBlob on
// reads — and tracks the last-synced state so it can diff the working tree
// against it.

import { type GitAdapter } from './adapter';
import { diffGitTrees, flattenTreeToBlobShas, type GitCommitMeta, type GitTree, type TreeDiff } from './git-types';
import { gitBlobSha } from './hash';
import type { WorkingTree } from './working-tree';

export interface RepoOptions {
    /** Override binary-classification (text vs binary by extension). Used at
     *  merge time — text gets 3-way, binary gets conflict copy. v1 doesn't
     *  expose a per-file merge path so this is currently unused, but kept on
     *  the interface for forward compatibility. */
    isBinary?: (path: string) => boolean;
}

/** Granular progress callback fired by the engine at each phase of a long
 *  operation. Used by the collaboration panel to drive the busy banner +
 *  log stream. `current`/`total` are present only when meaningful (e.g.
 *  "uploading blob 3 of 12"). */
export type ProgressFn = (event: ProgressEvent) => void;

export type ProgressEvent =
    | { phase: 'snapshot' }
    | { phase: 'diff'; added: number; modified: number; deleted: number }
    | { phase: 'blob-upload'; path: string; current: number; total: number }
    | { phase: 'tree' }
    | { phase: 'commit-object' }
    | { phase: 'update-branch' }
    | { phase: 'fetch-tree'; commitSha: string }
    | { phase: 'blob-download'; path: string; current: number; total: number }
    | { phase: 'apply'; path: string; current: number; total: number }
    | { phase: 'delete'; path: string };

export interface CommitOptions {
    message: string;
    author: string;
    onProgress?: ProgressFn;
}

export interface CheckoutOptions {
    onProgress?: ProgressFn;
}

export interface FastForwardOptions {
    onProgress?: ProgressFn;
}

export interface FastForwardResult {
    /** True if the working tree advanced. */
    applied: boolean;
    /** Commit we moved from (the previously-synced HEAD), if any. */
    from?: string;
    /** Commit we moved to (the new HEAD), if anything to apply. */
    to?: string;
    /** True if the working tree had uncommitted changes and we refused to FF.
     *  Caller's job to surface this and route to merge (the panel does it). */
    dirty?: boolean;
}

/** What the Repo tracks about its last-known sync point. The `treeSha` lets
 *  us pass `base_tree` to createTree, which only sends the *changed* entries
 *  instead of the full tree shape. */
export interface SyncedHead {
    commitSha: string;
    treeSha: string;
}

export class Repo {
    private syncedHead: SyncedHead | null = null;
    /** path → entry at the synced commit. Defensive copy of getTree's result. */
    private syncedTree: GitTree = {};

    constructor(private adapter: GitAdapter, _opts: RepoOptions = {}) {
        // _opts reserved for forward compatibility (isBinary, etc.) — kept on
        // the constructor signature so callers can pass it now and the engine
        // picks it up later without an API churn.
        void _opts;
    }

    // ─── synced state ────────────────────────────────────────────────────────

    getSyncedHead(): SyncedHead | null { return this.syncedHead ? { ...this.syncedHead } : null; }
    getSyncedTree(): GitTree {
        return Object.fromEntries(Object.entries(this.syncedTree).map(([p, e]) => [p, { ...e }]));
    }

    /** Inject the engine's view of the last sync point. Required after
     *  rebuilding a Repo from cached state — see the panel's `buildRepo`. */
    setSyncedHead(head: SyncedHead | null, tree: GitTree): void {
        this.syncedHead = head ? { ...head } : null;
        this.syncedTree = { ...tree };
    }

    /** Refetch the branch HEAD + its tree from the adapter. Equivalent to
     *  "reset to remote state, discarding our local view." Used to recover
     *  from stale-state confusion. */
    async refreshSyncedHead(): Promise<SyncedHead | null> {
        const sha = await this.adapter.branchHead();
        if (!sha) {
            this.syncedHead = null;
            this.syncedTree = {};
            return null;
        }
        const commit = await this.adapter.getCommit(sha);
        const tree = await this.adapter.getTree(sha);
        this.syncedHead = { commitSha: sha, treeSha: commit.treeSha };
        this.syncedTree = tree;
        return { ...this.syncedHead };
    }

    // ─── log / commit lookup ─────────────────────────────────────────────────

    async getCommit(sha: string): Promise<GitCommitMeta> {
        return await this.adapter.getCommit(sha);
    }

    async log(opts: { from?: string; limit?: number } = {}): Promise<GitCommitMeta[]> {
        return await this.adapter.listCommits({
            start: opts.from ?? this.syncedHead?.commitSha,
            limit: opts.limit ?? 30,
        });
    }

    // ─── snapshot / diff ─────────────────────────────────────────────────────

    /** Hash every file in the working tree to a GitTree. Used by stagedChanges
     *  and as the first step of commit. */
    async snapshot(wt: WorkingTree): Promise<GitTree> {
        const tree: GitTree = {};
        const paths = await wt.list();
        for (const p of paths) {
            const bytes = await wt.read(p);
            tree[p] = { blobSha: await gitBlobSha(bytes), size: bytes.length, mode: '100644' };
        }
        return tree;
    }

    async stagedChanges(wt: WorkingTree): Promise<TreeDiff> {
        const live = await this.snapshot(wt);
        return diffGitTrees(this.syncedTree, live);
    }

    // ─── commit (the write path) ─────────────────────────────────────────────

    async commit(wt: WorkingTree, opts: CommitOptions): Promise<GitCommitMeta> {
        const progress = opts.onProgress;
        // 1. Snapshot + diff
        progress?.({ phase: 'snapshot' });
        const live = await this.snapshot(wt);
        const diff = diffGitTrees(this.syncedTree, live);
        progress?.({
            phase: 'diff',
            added: diff.added.length,
            modified: diff.modified.length,
            deleted: diff.deleted.length,
        });
        if (diff.added.length === 0 && diff.modified.length === 0 && diff.deleted.length === 0) {
            throw new Error('nothing to commit');
        }

        // 2. Upload changed blobs (createBlob is content-addressed and
        //    idempotent, so duplicates against existing repo content are
        //    just a wasted byte — no correctness issue).
        const entries: Array<{ path: string; blobSha: string | null }> = [];
        const changedPaths = [...diff.added, ...diff.modified];
        for (let i = 0; i < changedPaths.length; i++) {
            const path = changedPaths[i];
            progress?.({ phase: 'blob-upload', path, current: i + 1, total: changedPaths.length });
            const bytes = await wt.read(path);
            const { sha } = await this.adapter.createBlob(bytes);
            entries.push({ path, blobSha: sha });
            if (sha !== live[path].blobSha) live[path].blobSha = sha;
        }
        for (const path of diff.deleted) {
            entries.push({ path, blobSha: null });
        }

        // 3. Build the new tree (use base_tree so we only resend the diff).
        progress?.({ phase: 'tree' });
        const { sha: treeSha } = await this.adapter.createTree({
            baseTreeSha: this.syncedHead?.treeSha,
            entries,
        });

        // 4. Create the commit object referencing the synced head as parent.
        progress?.({ phase: 'commit-object' });
        const parents = this.syncedHead ? [this.syncedHead.commitSha] : [];
        const { sha: commitSha } = await this.adapter.createCommit({
            message: opts.message,
            treeSha,
            parents,
            author: { name: opts.author, email: `${opts.author}@fade-playground` },
        });

        // 5. Move the branch ref. FF-checked; throws HeadConflictError on
        //    race (the new commit's parent isn't an ancestor of the current
        //    ref → updateBranch translates the 422 into HeadConflictError).
        progress?.({ phase: 'update-branch' });
        await this.adapter.updateBranch(commitSha);

        // 6. Advance our synced state.
        this.syncedHead = { commitSha, treeSha };
        this.syncedTree = { ...live };

        return await this.adapter.getCommit(commitSha);
    }

    // ─── checkout / materialize ──────────────────────────────────────────────

    /** Make the working tree match the tree of `targetCommitSha` (defaults to
     *  branch HEAD). Files in WT but not in target are deleted; files in
     *  target not in WT are written; files differing by blobSha are
     *  overwritten. Caller is responsible for surfacing local changes that
     *  would be lost — this method does *not* refuse to clobber. */
    async checkout(wt: WorkingTree, targetCommitSha?: string, opts: CheckoutOptions = {}): Promise<void> {
        const target = targetCommitSha ?? await this.adapter.branchHead();
        if (!target) throw new Error('checkout: no commit to check out');
        opts.onProgress?.({ phase: 'fetch-tree', commitSha: target });
        const commit = await this.adapter.getCommit(target);
        const tree = await this.adapter.getTree(target);
        await this.materialize(wt, tree, opts.onProgress);
        this.syncedHead = { commitSha: target, treeSha: commit.treeSha };
        this.syncedTree = { ...tree };
    }

    private async materialize(wt: WorkingTree, tree: GitTree, onProgress?: ProgressFn): Promise<void> {
        const localPaths = new Set(await wt.list());
        const paths = Object.keys(tree);
        for (let i = 0; i < paths.length; i++) {
            const path = paths[i];
            const entry = tree[path];
            // Skip the fetch if local content already matches — saves a blob
            // download on every file on every checkout.
            if (localPaths.has(path)) {
                const localBytes = await wt.read(path);
                const localSha = await gitBlobSha(localBytes);
                if (localSha === entry.blobSha) continue;
            }
            onProgress?.({ phase: 'blob-download', path, current: i + 1, total: paths.length });
            const bytes = await this.adapter.getBlob(entry.blobSha);
            onProgress?.({ phase: 'apply', path, current: i + 1, total: paths.length });
            await wt.write(path, bytes);
        }
        // Drop any local file that's not in the target tree.
        for (const path of localPaths) {
            if (!(path in tree)) {
                onProgress?.({ phase: 'delete', path });
                await wt.delete(path);
            }
        }
    }

    // ─── pull / fast-forward ─────────────────────────────────────────────────

    async tryFastForward(wt: WorkingTree, opts: FastForwardOptions = {}): Promise<FastForwardResult> {
        const remoteSha = await this.adapter.branchHead();
        if (!remoteSha) return { applied: false };
        if (this.syncedHead && remoteSha === this.syncedHead.commitSha) return { applied: false };

        // Refuse to clobber local changes. A "clean" working tree is one
        // whose snapshot exactly equals syncedTree.
        if (this.syncedHead) {
            opts.onProgress?.({ phase: 'snapshot' });
            const live = await this.snapshot(wt);
            const diff = diffGitTrees(this.syncedTree, live);
            opts.onProgress?.({
                phase: 'diff',
                added: diff.added.length,
                modified: diff.modified.length,
                deleted: diff.deleted.length,
            });
            if (diff.added.length || diff.modified.length || diff.deleted.length) {
                return { applied: false, dirty: true, from: this.syncedHead.commitSha, to: remoteSha };
            }
        }

        opts.onProgress?.({ phase: 'fetch-tree', commitSha: remoteSha });
        const commit = await this.adapter.getCommit(remoteSha);
        const tree = await this.adapter.getTree(remoteSha);
        await this.materialize(wt, tree, opts.onProgress);

        const from = this.syncedHead?.commitSha;
        this.syncedHead = { commitSha: remoteSha, treeSha: commit.treeSha };
        this.syncedTree = { ...tree };
        return { applied: true, from, to: remoteSha };
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    /** Flatten the synced tree to a path → blobSha map. Used by the panel's
     *  sync-index serialization (it only persists ids, not metadata). */
    syncedTreeToBlobShas(): Record<string, string> {
        return flattenTreeToBlobShas(this.syncedTree);
    }
}
