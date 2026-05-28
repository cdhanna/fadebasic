// Storage adapter interface — the surface the Repo engine talks to.
//
// **Third pivot edition**: we used to layer our own object store + manifest
// format on top of an arbitrary key-value backend. Now that the backend is
// always GitHub (= real git), we collapsed that layer and speak git's data
// model directly. The shape mirrors GitHub's Git Data API endpoints so the
// adapter is essentially a 1:1 wire wrapper.
//
// Anything that answers these calls correctly works as a backend — the
// MockAdapter in this directory satisfies the interface against in-memory
// state for tests; the GitHubAdapter is the production implementation.

import type { GitCommitMeta, GitTree } from './git-types';

// Thrown when updateBranch's fast-forward check rejects (the branch ref
// moved between our read and write). The Repo engine catches this to drive
// pull-before-commit (see sharing.md §8.5).
export class HeadConflictError extends Error {
    constructor(public expected: string | null, public actual: string | null) {
        super(`branch moved: expected ${expected ?? '<none>'}, got ${actual ?? '<none>'}`);
        this.name = 'HeadConflictError';
    }
}

export interface GitAdapter {
    // ─── read path ──────────────────────────────────────────────────────────

    /** Current branch HEAD's commit SHA, or null if the branch doesn't exist
     *  yet (a freshly-created repo without auto_init). */
    branchHead(): Promise<string | null>;

    /** Metadata for a single commit. Throws if the SHA isn't a commit object. */
    getCommit(sha: string): Promise<GitCommitMeta>;

    /** Resolve a commit SHA to its full recursive tree. The adapter takes a
     *  *commit* SHA (not a tree SHA) and does the two-step lookup internally;
     *  that's the affordance the engine wants. */
    getTree(commitSha: string): Promise<GitTree>;

    /** Raw bytes of a single blob. */
    getBlob(blobSha: string): Promise<Uint8Array>;

    // ─── write path (Git Data API) ──────────────────────────────────────────

    /** Upload a blob. Idempotent: same bytes yield the same SHA, and the
     *  adapter MAY short-circuit if it already knows the SHA exists. */
    createBlob(bytes: Uint8Array): Promise<{ sha: string }>;

    /** Build a new tree by patching a base tree with the supplied entries.
     *  Entries with `blobSha = null` delete that path; otherwise the path is
     *  added or replaced. Omit `baseTreeSha` to build a tree from scratch. */
    createTree(opts: {
        baseTreeSha?: string;
        entries: Array<{ path: string; blobSha: string | null }>;
    }): Promise<{ sha: string }>;

    /** Wrap a tree as a commit object referencing zero or more parents.
     *  An empty `parents` array creates a root commit. */
    createCommit(opts: {
        message: string;
        treeSha: string;
        parents: string[];
        author?: { name: string; email: string };
    }): Promise<{ sha: string }>;

    /**
     * Move the branch ref to a new commit. Implementations MUST enforce the
     * git fast-forward rule: the new commit must descend from the ref's
     * current value (i.e. its parent chain reaches the current head). If
     * not, throw {@link HeadConflictError} so the Repo engine can drive a
     * pull-before-commit retry. This is how we get CAS without a separate
     * expected-old-sha parameter — the commit's parent IS the expectation.
     */
    updateBranch(commitSha: string): Promise<void>;

    // ─── log ────────────────────────────────────────────────────────────────

    /** Walk commits starting at `start` (defaults to branch HEAD), newest
     *  first, up to `limit`. */
    listCommits(opts?: { start?: string; limit?: number }): Promise<GitCommitMeta[]>;
}
