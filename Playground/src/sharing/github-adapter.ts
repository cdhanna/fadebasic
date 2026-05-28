// GitHub GitAdapter — speaks the Git Data API directly. Replaces the prior
// "blobs-as-base64-files-under-objects/" Contents-API approach.
//
// What this means in practice:
//   - User project files live at their natural paths in the repo
//     (`src.fbasic`, `assets/hero.png`, …) — github.com renders the repo
//     like any normal project.
//   - Each commit is a real git commit, visible in the commits feed, with
//     real parent/author/message metadata.
//   - The "tree at commit" is just a git tree object; we walk it with the
//     recursive Trees API in one round-trip.
//   - CAS comes from the fast-forward rule on `PATCH refs/heads/{branch}`:
//     if the branch moved between our read and write, the FF check fails
//     and we surface HeadConflictError. No expected-old-sha parameter
//     needed — the new commit's parent IS the expectation.
//
// Endpoints used:
//   GET    /branches/{branch}                  branchHead
//   GET    /git/commits/{sha}                  getCommit
//   GET    /git/trees/{treeSha}?recursive=1    getTree (after a getCommit hop)
//   GET    /git/blobs/{sha}                    getBlob (returns base64)
//   POST   /git/blobs                          createBlob
//   POST   /git/trees                          createTree
//   POST   /git/commits                        createCommit
//   PATCH  /git/refs/heads/{branch}            updateBranch (FF-checked)
//   GET    /commits?sha=...&per_page=...       listCommits

import { HeadConflictError, type GitAdapter } from './adapter';
import type { GitCommitMeta, GitTree } from './git-types';
import { getLogger } from '../log-bus';

const adapterLog = getLogger('github-adapter');

interface TreeApiResponse {
    tree: Array<{ path: string; sha: string; mode: string; type: string; size?: number }>;
    truncated?: boolean;
}

const API = 'https://api.github.com';

export interface GitHubAdapterOptions {
    owner: string;
    repo: string;
    token: string;
    branch?: string;                          // default 'main'
    fetchImpl?: typeof fetch;                 // injectable for tests
}

export interface CreateRepoOptions {
    name: string;
    description?: string;
    private?: boolean;
    token: string;
    fetchImpl?: typeof fetch;
}

export class GitHubApiError extends Error {
    constructor(public status: number, public body: unknown, message: string) {
        super(`GitHub ${status}: ${message}`);
        this.name = 'GitHubApiError';
    }
    static async from(r: Response): Promise<GitHubApiError> {
        const text = await r.text().catch(() => '');
        let body: unknown = text;
        try { body = JSON.parse(text); } catch { /* keep as text */ }
        const msg = (body && typeof body === 'object' && 'message' in body)
            ? String((body as { message: unknown }).message)
            : r.statusText || 'unknown error';
        return new GitHubApiError(r.status, body, msg);
    }
}

export class GitHubAdapter implements GitAdapter {
    private readonly owner: string;
    private readonly repo: string;
    private readonly branch: string;
    private token: string;
    private readonly fetchImpl: typeof fetch;

    // Conditional-request bookkeeping for the polled branchHead endpoint.
    // GitHub honors `If-None-Match` on most read endpoints and replies 304
    // (empty body, no quota cost on the primary rate limit's most-restrictive
    // pool) when nothing has changed. Caching here turns a 5–30s poll into
    // essentially-free network when the branch is idle.
    private lastBranchEtag: string | null = null;
    private lastBranchSha: string | null = null;

    constructor(opts: GitHubAdapterOptions) {
        this.owner = opts.owner;
        this.repo = opts.repo;
        this.branch = opts.branch ?? 'main';
        this.token = opts.token;
        this.fetchImpl = opts.fetchImpl ?? fetch.bind(globalThis);
    }

    // ─── setup (not part of the interface) ──────────────────────────────────

    /** Create a brand-new repo via `POST /user/repos` (auto_init=true so the
     *  branch exists out of the gate), returning a ready adapter. */
    static async createRepo(opts: CreateRepoOptions): Promise<GitHubAdapter> {
        const fetchImpl = opts.fetchImpl ?? fetch.bind(globalThis);
        const body = await apiRequest(fetchImpl, opts.token, `${API}/user/repos`, {
            method: 'POST',
            body: JSON.stringify({
                name: opts.name,
                description: opts.description ?? 'Fade playground project',
                private: opts.private ?? false,
                auto_init: true,
            }),
        }) as { owner: { login: string }; name: string; default_branch: string };
        return new GitHubAdapter({
            owner: body.owner.login,
            repo: body.name,
            branch: body.default_branch,
            token: opts.token,
            fetchImpl,
        });
    }

    /** Open an existing repo. (No setup work; ready immediately.) */
    static open(opts: GitHubAdapterOptions): GitHubAdapter {
        return new GitHubAdapter(opts);
    }

    /** Owner/repo/branch the adapter is bound to. Useful for UI display. */
    info(): { owner: string; repo: string; branch: string } {
        return { owner: this.owner, repo: this.repo, branch: this.branch };
    }

    // ─── read path ──────────────────────────────────────────────────────────

    async branchHead(): Promise<string | null> {
        // Use a raw fetch here (not `this.api`) so we can handle 304 without
        // it being treated as an error. The conditional header is omitted
        // on the first call and on cache invalidation.
        const url = `${API}/repos/${this.owner}/${this.repo}/branches/${encodePathSeg(this.branch)}`;
        const headers: Record<string, string> = {
            Authorization: `Bearer ${this.token}`,
            Accept: 'application/vnd.github+json',
            'X-GitHub-Api-Version': '2022-11-28',
        };
        if (this.lastBranchEtag) headers['If-None-Match'] = this.lastBranchEtag;
        const r = await this.fetchImpl(url, { headers });
        if (r.status === 304) {
            // Nothing changed since the last call — return the cached sha
            // without touching the body. The ETag stays the same too.
            return this.lastBranchSha;
        }
        if (r.status === 404) {
            this.lastBranchEtag = null;
            this.lastBranchSha = null;
            return null;
        }
        if (!r.ok) throw await GitHubApiError.from(r);
        const etag = r.headers.get('ETag');
        if (etag) this.lastBranchEtag = etag;
        const body = await r.json() as { commit: { sha: string } };
        this.lastBranchSha = body.commit.sha;
        return this.lastBranchSha;
    }

    async getCommit(sha: string): Promise<GitCommitMeta> {
        const c = await this.api(`/repos/${this.owner}/${this.repo}/git/commits/${sha}`) as {
            sha: string;
            tree: { sha: string };
            parents: Array<{ sha: string }>;
            message: string;
            author: { name: string; email: string; date: string };
        };
        return {
            sha: c.sha,
            parents: c.parents.map((p) => p.sha),
            treeSha: c.tree.sha,
            message: c.message,
            author: c.author.name,
            time: c.author.date,
        };
    }

    async getTree(commitSha: string): Promise<GitTree> {
        // Two-step: commit object → tree sha → recursive tree fetch. The
        // Trees endpoint accepts a tree SHA or a ref name but NOT a commit
        // SHA — passing a commit SHA returns an empty tree silently, which
        // was a real bug in the prior implementation.
        const commit = await this.getCommit(commitSha);
        let t: TreeApiResponse;
        try {
            t = await this.api(
                `/repos/${this.owner}/${this.repo}/git/trees/${commit.treeSha}?recursive=1`,
            ) as TreeApiResponse;
        } catch (e) {
            // Observed in the wild on fresh repos: getCommit returns a
            // tree.sha that the Trees endpoint 404s on for a brief window
            // (data-API eventual consistency vs the commits endpoint).
            // Retry once with the branch name as the ref — the Trees API
            // resolves refs internally and tends to be more up-to-date than
            // raw tree-sha lookups during this window.
            //
            // This only helps when the branch head IS this commit; if the
            // user is fetching an older commit's tree, the retry would
            // return the wrong data, so we only retry when the commit
            // matches the current branch head.
            if (e instanceof GitHubApiError && e.status === 404) {
                const branchHeadSha = await this.branchHead().catch(() => null);
                if (branchHeadSha === commitSha) {
                    adapterLog.warn(
                        `Trees API 404 on tree ${commit.treeSha.slice(0, 8)} (commit ${commitSha.slice(0, 8)}); retrying via branch ref "${this.branch}"`,
                    );
                    t = await this.api(
                        `/repos/${this.owner}/${this.repo}/git/trees/${encodePathSeg(this.branch)}?recursive=1`,
                    ) as TreeApiResponse;
                } else {
                    throw e;
                }
            } else {
                throw e;
            }
        }
        if (t.truncated) {
            // Surface loudly — silently truncating would orphan reachable
            // entries in any code path that relies on completeness.
            throw new Error('git tree truncated; repo exceeded the Trees API single-call cap (100k entries / 7 MB)');
        }
        const out: GitTree = {};
        for (const e of t.tree) {
            if (e.type !== 'blob') continue;     // skip subtree directory entries
            out[e.path] = {
                blobSha: e.sha,
                mode: e.mode as '100644' | '100755' | '120000',
                size: e.size,
            };
        }
        return out;
    }

    async getBlob(blobSha: string): Promise<Uint8Array> {
        const r = await this.api(`/repos/${this.owner}/${this.repo}/git/blobs/${blobSha}`) as {
            content: string;
            encoding: string;
        };
        if (r.encoding !== 'base64') {
            throw new Error(`unexpected blob encoding ${r.encoding}`);
        }
        return base64ToBytes(r.content);
    }

    // ─── write path ─────────────────────────────────────────────────────────

    async createBlob(bytes: Uint8Array): Promise<{ sha: string }> {
        const r = await this.api(`/repos/${this.owner}/${this.repo}/git/blobs`, {
            method: 'POST',
            body: JSON.stringify({
                content: bytesToBase64(bytes),
                encoding: 'base64',
            }),
        }) as { sha: string };
        return { sha: r.sha };
    }

    async createTree(opts: {
        baseTreeSha?: string;
        entries: Array<{ path: string; blobSha: string | null }>;
    }): Promise<{ sha: string }> {
        const body: Record<string, unknown> = {
            tree: opts.entries.map((e) => ({
                path: e.path,
                mode: '100644',
                type: 'blob',
                // null SHA means "remove this path from the tree" — git
                // recognizes this in the Trees API.
                sha: e.blobSha,
            })),
        };
        if (opts.baseTreeSha) body.base_tree = opts.baseTreeSha;
        const r = await this.api(`/repos/${this.owner}/${this.repo}/git/trees`, {
            method: 'POST',
            body: JSON.stringify(body),
        }) as { sha: string };
        return { sha: r.sha };
    }

    async createCommit(opts: {
        message: string;
        treeSha: string;
        parents: string[];
        author?: { name: string; email: string };
    }): Promise<{ sha: string }> {
        const body: Record<string, unknown> = {
            message: opts.message,
            tree: opts.treeSha,
            parents: opts.parents,
        };
        if (opts.author) body.author = opts.author;
        const r = await this.api(`/repos/${this.owner}/${this.repo}/git/commits`, {
            method: 'POST',
            body: JSON.stringify(body),
        }) as { sha: string };
        return { sha: r.sha };
    }

    async updateBranch(commitSha: string): Promise<void> {
        try {
            await this.api(`/repos/${this.owner}/${this.repo}/git/refs/heads/${encodePathSeg(this.branch)}`, {
                method: 'PATCH',
                body: JSON.stringify({ sha: commitSha, force: false }),
            });
        } catch (e) {
            // GitHub returns 422 with message "Update is not a fast forward"
            // (or similar) when our new commit's parent isn't an ancestor of
            // the branch's current head. Translate to HeadConflictError so
            // the Repo engine knows to do pull-before-commit.
            if (e instanceof GitHubApiError && (e.status === 422 || e.status === 409)) {
                // Re-fetch the current branch head so the error carries the
                // actual remote state — useful for the panel's UI.
                let actual: string | null = null;
                try { actual = await this.branchHead(); } catch { /* ignore */ }
                throw new HeadConflictError(null, actual);
            }
            // The ref might not exist yet (initial commit on a brand-new repo
            // without auto_init). In that case create the ref instead of
            // patching.
            if (e instanceof GitHubApiError && e.status === 404) {
                await this.createBranchRef(commitSha);
                return;
            }
            throw e;
        }
    }

    /** First-time branch creation (when PATCH ref returns 404). */
    private async createBranchRef(commitSha: string): Promise<void> {
        await this.api(`/repos/${this.owner}/${this.repo}/git/refs`, {
            method: 'POST',
            body: JSON.stringify({ ref: `refs/heads/${this.branch}`, sha: commitSha }),
        });
    }

    // ─── log ────────────────────────────────────────────────────────────────

    async listCommits(opts: { start?: string; limit?: number } = {}): Promise<GitCommitMeta[]> {
        const params = new URLSearchParams();
        if (opts.start) params.set('sha', opts.start);
        params.set('per_page', String(Math.min(opts.limit ?? 30, 100)));
        try {
            const arr = await this.api(`/repos/${this.owner}/${this.repo}/commits?${params}`) as Array<{
                sha: string;
                parents: Array<{ sha: string }>;
                commit: {
                    message: string;
                    tree: { sha: string };
                    author: { name: string; date: string };
                };
            }>;
            return arr.map((c) => ({
                sha: c.sha,
                parents: c.parents.map((p) => p.sha),
                treeSha: c.commit.tree.sha,
                message: c.commit.message,
                author: c.commit.author.name,
                time: c.commit.author.date,
            }));
        } catch (e) {
            // Empty repo or branch — return an empty log instead of throwing.
            if (e instanceof GitHubApiError && (e.status === 404 || e.status === 409)) return [];
            throw e;
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private async api(path: string, init: RequestInit = {}): Promise<unknown> {
        const url = path.startsWith('http') ? path : `${API}${path}`;
        const headers: Record<string, string> = {
            Authorization: `Bearer ${this.token}`,
            Accept: 'application/vnd.github+json',
            'X-GitHub-Api-Version': '2022-11-28',
        };
        if (init.body && typeof init.body === 'string') headers['Content-Type'] = 'application/json';
        Object.assign(headers, init.headers ?? {});
        const method = init.method ?? 'GET';
        const r = await this.fetchImpl(url, { ...init, headers });
        if (!r.ok) {
            const err = await GitHubApiError.from(r);
            // Surface every API failure on the LogBus so the user can see it
            // in the Logs panel. The thrown error still bubbles to the
            // caller for control flow.
            adapterLog.error(`${method} ${url.replace(API, '')} → ${r.status} ${err.message}`);
            throw err;
        }
        if (r.status === 204) return null;
        return await r.json();
    }
}

async function apiRequest(
    fetchImpl: typeof fetch,
    token: string,
    url: string,
    init: RequestInit,
): Promise<unknown> {
    const headers: Record<string, string> = {
        Authorization: `Bearer ${token}`,
        Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28',
    };
    if (init.body && typeof init.body === 'string') headers['Content-Type'] = 'application/json';
    Object.assign(headers, init.headers ?? {});
    const r = await fetchImpl(url, { ...init, headers });
    if (!r.ok) throw await GitHubApiError.from(r);
    if (r.status === 204) return null;
    return await r.json();
}

// Branch names with slashes (e.g. "feature/x") need URL-encoding for the
// `{branch}` path slot. Other special chars are rare but we encode anyway.
function encodePathSeg(s: string): string {
    return encodeURIComponent(s);
}

function bytesToBase64(bytes: Uint8Array): string {
    if (typeof Buffer !== 'undefined') return Buffer.from(bytes).toString('base64');
    let bin = '';
    const CHUNK = 0x8000;
    for (let i = 0; i < bytes.length; i += CHUNK) {
        bin += String.fromCharCode.apply(null, bytes.subarray(i, i + CHUNK) as unknown as number[]);
    }
    return btoa(bin);
}

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
