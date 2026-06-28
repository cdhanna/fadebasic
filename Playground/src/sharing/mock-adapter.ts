// In-memory `GitAdapter` for unit tests. Models git's three object kinds
// (blob / tree / commit) plus the branch ref, with all the same invariants
// the real backend enforces:
//
//   - createBlob / createTree / createCommit are content-addressed (same
//     inputs → same SHA, idempotent re-creation).
//   - updateBranch enforces the fast-forward rule using the commit's own
//     parent pointer — the new commit must descend from the current head,
//     or HeadConflictError is thrown.
//
// SHAs here are *not* git's actual SHA-1 derivation — that would force
// canonical-byte serialization just to compute test ids. Instead we use the
// sha256 of a deterministic JSON encoding. Stable across runs, comparable,
// good enough since tests never cross between the mock and a real git
// process.

import { HeadConflictError, type GitAdapter } from './adapter';
import type { GitCommitMeta, GitTree } from './git-types';
import { gitBlobSha, sha256Hex } from './hash';

interface MockCommit {
    sha: string;
    parents: string[];
    treeSha: string;
    message: string;
    author: string;
    time: string;
}

export class MockAdapter implements GitAdapter {
    private blobs = new Map<string, Uint8Array>();
    private trees = new Map<string, GitTree>();
    private commits = new Map<string, MockCommit>();
    private branchHeadSha: string | null = null;
    private clock = 0;

    // ─── read path ──────────────────────────────────────────────────────────

    async branchHead(): Promise<string | null> {
        return this.branchHeadSha;
    }

    async getCommit(sha: string): Promise<GitCommitMeta> {
        const c = this.commits.get(sha);
        if (!c) throw new Error(`commit not found: ${sha}`);
        return { ...c };
    }

    async getTree(commitSha: string): Promise<GitTree> {
        const c = this.commits.get(commitSha);
        if (!c) throw new Error(`commit not found: ${commitSha}`);
        const t = this.trees.get(c.treeSha);
        if (!t) throw new Error(`tree not found: ${c.treeSha}`);
        // Return a defensive copy so callers can mutate without poisoning storage.
        return Object.fromEntries(Object.entries(t).map(([p, e]) => [p, { ...e }]));
    }

    async getBlob(blobSha: string): Promise<Uint8Array> {
        const b = this.blobs.get(blobSha);
        if (!b) throw new Error(`blob not found: ${blobSha}`);
        return new Uint8Array(b);
    }

    // ─── write path ─────────────────────────────────────────────────────────

    async createBlob(bytes: Uint8Array): Promise<{ sha: string }> {
        const sha = await gitBlobSha(bytes);
        if (!this.blobs.has(sha)) this.blobs.set(sha, new Uint8Array(bytes));
        return { sha };
    }

    async createTree(opts: {
        baseTreeSha?: string;
        entries: Array<{ path: string; blobSha: string | null }>;
    }): Promise<{ sha: string }> {
        const base = opts.baseTreeSha ? this.trees.get(opts.baseTreeSha) : undefined;
        const next: GitTree = base ? { ...base } : {};
        for (const e of opts.entries) {
            if (e.blobSha === null) {
                delete next[e.path];
            } else {
                next[e.path] = { blobSha: e.blobSha, mode: '100644' };
            }
        }
        const sha = await canonicalSha(['tree', JSON.stringify(sortKeys(next))]);
        if (!this.trees.has(sha)) this.trees.set(sha, next);
        return { sha };
    }

    async createCommit(opts: {
        message: string;
        treeSha: string;
        parents: string[];
        author?: { name: string; email: string };
    }): Promise<{ sha: string }> {
        this.clock++;
        const author = opts.author?.name ?? 'tester';
        const time = new Date(1700000000000 + this.clock * 1000).toISOString();
        const sha = await canonicalSha([
            'commit', opts.treeSha, opts.parents.join(','), opts.message, author, time,
        ]);
        const commit: MockCommit = {
            sha,
            parents: [...opts.parents],
            treeSha: opts.treeSha,
            message: opts.message,
            author,
            time,
        };
        if (!this.commits.has(sha)) this.commits.set(sha, commit);
        return { sha };
    }

    async updateBranch(commitSha: string): Promise<void> {
        const c = this.commits.get(commitSha);
        if (!c) throw new Error(`updateBranch: commit not in store: ${commitSha}`);
        if (this.branchHeadSha === null) {
            // First commit on the branch — must be a root commit (no parents).
            if (c.parents.length > 0) {
                throw new HeadConflictError(null, this.branchHeadSha);
            }
            this.branchHeadSha = commitSha;
            return;
        }
        // FF check: new commit's parent must be the current head (single-line
        // history). True git fast-forward would walk the parent chain to find
        // an ancestor; v1 mock keeps it simple.
        if (!c.parents.includes(this.branchHeadSha)) {
            throw new HeadConflictError(this.branchHeadSha, this.branchHeadSha);
        }
        this.branchHeadSha = commitSha;
    }

    // ─── log ────────────────────────────────────────────────────────────────

    async listCommits(opts: { start?: string; limit?: number } = {}): Promise<GitCommitMeta[]> {
        const limit = opts.limit ?? 50;
        let cursor: string | undefined = opts.start ?? this.branchHeadSha ?? undefined;
        const out: GitCommitMeta[] = [];
        while (cursor && out.length < limit) {
            const c = this.commits.get(cursor);
            if (!c) break;
            out.push({ ...c });
            cursor = c.parents[0];
        }
        return out;
    }

    // ─── test helpers (not part of the interface) ──────────────────────────

    blobCount(): number { return this.blobs.size; }
    treeCount(): number { return this.trees.size; }
    commitCount(): number { return this.commits.size; }
    currentBranchHead(): string | null { return this.branchHeadSha; }
}

// Same canonicalization recipe as the manifest used to do: sort keys, hash
// the bytes. We don't need git's actual SHA-1 derivation for tests — only
// that ids are stable and content-derived.
async function canonicalSha(parts: string[]): Promise<string> {
    const text = parts.join('');
    return await sha256Hex(new TextEncoder().encode(text));
}

function sortKeys<T extends Record<string, unknown>>(obj: T): T {
    const sorted: Record<string, unknown> = {};
    for (const k of Object.keys(obj).sort()) sorted[k] = obj[k];
    return sorted as T;
}
