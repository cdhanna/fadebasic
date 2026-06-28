// Per-project sync state. Persisted to localStorage, keyed by project name.
//
// **Third-pivot edition**: simpler than before. With real git as the
// backing store, "our commit id" and "git commit sha" collapse into one
// value, and we no longer track a separate HEAD-file blob sha for CAS
// (CAS now comes from git's fast-forward rule on `updateBranch`).
//
// The key prefix is v3 so old (manifest-format) indexes are silently
// orphaned — the user just signs in and reconnects.

const KEY_PREFIX = 'fade-sharing:project-v3:';

/** Persisted state for one workspace project. */
export interface ProjectSyncIndex {
    /** GitHub repo bound to this workspace, or null if not connected yet. */
    remoteRepo: { owner: string; name: string; branch: string } | null;
    /** Git commit SHA we last materialized into the working tree. */
    syncedCommitSha: string | null;
    /** Git tree SHA at that commit. Lets createTree use base_tree on next
     *  commit so we only send the *changed* entries. */
    syncedTreeSha: string | null;
    /** path → git blob sha at the synced commit. Used by file-status and by
     *  the conflict-detection logic — both compare local git-blob-sha
     *  against these. */
    baseTree: Record<string, string>;
}

const EMPTY: ProjectSyncIndex = {
    remoteRepo: null,
    syncedCommitSha: null,
    syncedTreeSha: null,
    baseTree: {},
};

export function loadSyncIndex(projectName: string): ProjectSyncIndex {
    try {
        const raw = localStorage.getItem(KEY_PREFIX + projectName);
        if (!raw) return { ...EMPTY };
        const parsed = JSON.parse(raw) as Partial<ProjectSyncIndex>;
        return {
            remoteRepo: parsed.remoteRepo ?? null,
            syncedCommitSha: parsed.syncedCommitSha ?? null,
            syncedTreeSha: parsed.syncedTreeSha ?? null,
            baseTree: parsed.baseTree ?? {},
        };
    } catch {
        return { ...EMPTY };
    }
}

export function saveSyncIndex(projectName: string, idx: ProjectSyncIndex): void {
    try {
        localStorage.setItem(KEY_PREFIX + projectName, JSON.stringify(idx));
    } catch {
        // Quota or private mode — drop silently. The cache can always be rebuilt.
    }
}

export function clearSyncIndex(projectName: string): void {
    try { localStorage.removeItem(KEY_PREFIX + projectName); } catch { /* ignore */ }
}

/** Convenience: true iff the project has a remote repo bound. */
export function isConnected(idx: ProjectSyncIndex): boolean {
    return idx.remoteRepo !== null;
}
