// Git-shaped types the engine and adapter speak in. Replaces the old
// manifest format (which existed to give us "commits" on top of an
// arbitrary key-value store). With real git as the backing store, we don't
// need our own commit serialization — git's own objects ARE the format.
//
// Path conventions: forward-slash-delimited project-relative strings.

export interface GitTreeEntry {
    /** SHA-1 of `blob ${size}\0${bytes}` — what git itself uses as the blob's id. */
    blobSha: string;
    /** File size in bytes. Informational; the engine compares by blobSha. */
    size?: number;
    /** Git file mode. We only write `100644` (regular file); reads ignore. */
    mode?: '100644' | '100755' | '120000';
}

/** Flat path → entry map at one commit's tree. The git Trees API recursive
 *  mode returns this shape directly (after path-flattening). */
export type GitTree = Record<string, GitTreeEntry>;

export interface GitCommitMeta {
    sha: string;
    /** Parent commit SHAs. Root commit has []. v1 commits have one parent;
     *  a merge commit (not yet emitted by our flow) would have two. */
    parents: string[];
    treeSha: string;
    message: string;
    author: string;
    /** ISO-8601 UTC string. */
    time: string;
}

export interface TreeDiff {
    added: string[];
    modified: string[];
    deleted: string[];
}

/** Path-level diff: paths in `next` not in `base` → added; same path,
 *  different blobSha → modified; in `base` not in `next` → deleted. */
export function diffGitTrees(base: GitTree, next: GitTree): TreeDiff {
    const added: string[] = [];
    const modified: string[] = [];
    const deleted: string[] = [];
    for (const p of Object.keys(next)) {
        if (!(p in base)) added.push(p);
        else if (base[p].blobSha !== next[p].blobSha) modified.push(p);
    }
    for (const p of Object.keys(base)) {
        if (!(p in next)) deleted.push(p);
    }
    added.sort(); modified.sort(); deleted.sort();
    return { added, modified, deleted };
}

/** Extract path → blobSha map (drops size/mode info). Useful for the sync
 *  index's `baseTree` which only cares about identity. */
export function flattenTreeToBlobShas(tree: GitTree): Record<string, string> {
    const out: Record<string, string> = {};
    for (const [path, entry] of Object.entries(tree)) {
        out[path] = entry.blobSha;
    }
    return out;
}
