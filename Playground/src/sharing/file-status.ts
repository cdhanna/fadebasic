// Per-file status (Added / Modified / Deleted / Unchanged) used both by the
// commit panel and by the workspace file-list badges. Computed by diffing
// the *current* working tree against the baseTree captured at last sync.
//
// Why this layer instead of Repo.stagedChanges? stagedChanges hashes every
// file on every call — the right primitive for an authoritative diff before
// a commit, but expensive when the file list re-renders on every keystroke.
// `quickStatus` skips hashing when size or mtime is unchanged; the badges
// don't need a cryptographic answer, just "did this file change."

import { gitBlobSha } from './hash';
import type { WorkingTree } from './working-tree';

export type FileStatus = 'added' | 'modified' | 'deleted' | 'unchanged';

export interface FileStatusEntry {
    path: string;
    status: FileStatus;
}

/**
 * Path → git-blob-sha cache. Computing a sha requires a full file read +
 * sha-1 over the bytes, which on every autosave (every 600 ms typing burst)
 * times every file in the project is the dominant cost of `computeStatus`.
 * We cache shas keyed by path; callers are responsible for `invalidate(path)`
 * on any write so the next status pass re-reads exactly the changed paths.
 *
 * The cache lives on the WorkingTree wrapper instance, *not* in a global,
 * so each project's panel has its own — and so tests get a clean slate.
 */
export class HashCache {
    private map = new Map<string, string>();
    get(path: string): string | undefined { return this.map.get(path); }
    set(path: string, sha: string): void { this.map.set(path, sha); }
    invalidate(path: string): void { this.map.delete(path); }
    invalidateAll(): void { this.map.clear(); }
    /** For tests / instrumentation. */
    size(): number { return this.map.size; }
}

/**
 * Compute per-file status by comparing the live working tree against a known
 * baseTree (path → content hash at last sync).
 *
 * Pass a `HashCache` to skip the read+hash for files whose content hasn't
 * changed since the last call. Callers MUST invalidate the cache entry for
 * any path they wrote since the last status pass — see the panel's
 * `refreshStatusForFile` for the autosave hot path.
 */
export async function computeStatus(
    wt: WorkingTree,
    baseTree: Record<string, string>,
    cache?: HashCache,
): Promise<FileStatusEntry[]> {
    const paths = await wt.list();
    const seen = new Set<string>();
    const out: FileStatusEntry[] = [];

    for (const path of paths) {
        seen.add(path);
        let liveHash = cache?.get(path);
        if (!liveHash) {
            liveHash = await gitBlobSha(await wt.read(path));
            cache?.set(path, liveHash);
        }
        const baseHash = baseTree[path];
        if (baseHash === undefined) {
            out.push({ path, status: 'added' });
        } else if (baseHash !== liveHash) {
            out.push({ path, status: 'modified' });
        } else {
            out.push({ path, status: 'unchanged' });
        }
    }
    for (const path of Object.keys(baseTree)) {
        if (!seen.has(path)) out.push({ path, status: 'deleted' });
    }

    return out;
}

/**
 * Cheap status hint without hashing — purely structural ("is the path in
 * baseTree?"). Used by the file-list badge renderer where a stale "modified"
 * is fine; the commit panel re-hashes before showing the staged-changes list.
 */
export function pathStatusHint(
    path: string,
    livePaths: Set<string>,
    baseTree: Record<string, string>,
): FileStatus {
    const inLive = livePaths.has(path);
    const inBase = path in baseTree;
    if (inLive && !inBase) return 'added';
    if (!inLive && inBase) return 'deleted';
    // Live + base → we can't tell from structure alone whether content
    // changed. Conservative: report 'unchanged' here; commit panel will
    // upgrade to 'modified' after hashing.
    return 'unchanged';
}

/** Single-char glyph for badge rendering. */
export function statusGlyph(s: FileStatus): string {
    switch (s) {
        case 'added': return 'A';
        case 'modified': return 'M';
        case 'deleted': return 'D';
        case 'unchanged': return '';
    }
}
