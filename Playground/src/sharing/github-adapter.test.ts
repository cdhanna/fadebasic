// GitHubAdapter unit tests against a mocked fetch. Run offline, never touch
// github.com. Each test programs the mock with the responses the Git Data
// API would return and asserts the adapter both made the right call and
// reacted to the response correctly.
//
// Wire-protocol coverage:
//   - branchHead, getCommit, getTree (two-step), getBlob (base64 round-trip)
//   - createBlob, createTree (with base_tree), createCommit
//   - updateBranch fast-forward, 422 / 409 → HeadConflictError, 404 → POST refs
//   - listCommits
//   - empty-branch handling (branchHead 404 → null)

import { beforeEach, describe, expect, it } from 'vitest';
import { HeadConflictError } from './adapter';
import { GitHubAdapter, GitHubApiError } from './github-adapter';

const OWNER = 'alice';
const REPO = 'project';
const TOKEN = 'ghp_test_token';

interface RecordedRequest {
    url: string;
    method: string;
    headers: Record<string, string>;
    body: unknown;
}

type Responder = (req: RecordedRequest) => Response | Promise<Response>;

class FetchMock {
    requests: RecordedRequest[] = [];
    private handlers: Array<{ match: (req: RecordedRequest) => boolean; respond: Responder }> = [];

    on(matcher: string | RegExp | ((req: RecordedRequest) => boolean), respond: Responder): this {
        const match = typeof matcher === 'function'
            ? matcher
            : typeof matcher === 'string'
                ? (req: RecordedRequest) => req.url === matcher
                : (req: RecordedRequest) => matcher.test(req.url);
        this.handlers.push({ match, respond });
        return this;
    }

    asFetch(): typeof fetch {
        return (async (input: RequestInfo | URL, init: RequestInit = {}) => {
            const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
            const headers: Record<string, string> = {};
            const rawHeaders = init.headers ?? {};
            if (rawHeaders instanceof Headers) {
                rawHeaders.forEach((v, k) => { headers[k.toLowerCase()] = v; });
            } else if (Array.isArray(rawHeaders)) {
                for (const [k, v] of rawHeaders) headers[k.toLowerCase()] = v;
            } else {
                for (const [k, v] of Object.entries(rawHeaders)) headers[k.toLowerCase()] = String(v);
            }
            const req: RecordedRequest = {
                url,
                method: (init.method ?? 'GET').toUpperCase(),
                headers,
                body: init.body,
            };
            this.requests.push(req);
            for (const h of this.handlers) {
                if (h.match(req)) return h.respond(req);
            }
            return new Response(JSON.stringify({ message: `unmocked: ${req.method} ${req.url}` }), {
                status: 599,
                headers: { 'Content-Type': 'application/json' },
            });
        }) as typeof fetch;
    }
}

function jsonResp(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
    return new Response(JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json', ...headers },
    });
}

function adapterWith(fetchMock: FetchMock): GitHubAdapter {
    return new GitHubAdapter({
        owner: OWNER,
        repo: REPO,
        token: TOKEN,
        fetchImpl: fetchMock.asFetch(),
    });
}

function b64Bytes(bytes: Uint8Array): string {
    if (typeof Buffer !== 'undefined') return Buffer.from(bytes).toString('base64');
    let bin = '';
    for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
    return btoa(bin);
}
function bytesFromB64(s: string): Uint8Array {
    if (typeof Buffer !== 'undefined') return new Uint8Array(Buffer.from(s, 'base64'));
    const bin = atob(s);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
}

// ─── read path ──────────────────────────────────────────────────────────────

describe('GitHubAdapter: read path', () => {
    let fm: FetchMock;
    let a: GitHubAdapter;
    beforeEach(() => { fm = new FetchMock(); a = adapterWith(fm); });

    it('branchHead returns the commit sha for main', async () => {
        fm.on(/\/branches\/main$/, () => jsonResp({ commit: { sha: 'abc123' } }));
        expect(await a.branchHead()).toBe('abc123');
    });

    it('branchHead returns null on 404 (empty repo / branch not yet created)', async () => {
        fm.on(/\/branches\/main$/, () => jsonResp({ message: 'Branch not found' }, 404));
        expect(await a.branchHead()).toBeNull();
    });

    it('branchHead sends If-None-Match after the first call and treats 304 as "unchanged"', async () => {
        // First call: 200 with ETag. Second call: must send If-None-Match
        // with the captured ETag; we reply 304 (no body) and the adapter
        // returns the cached sha without re-parsing.
        let calls = 0;
        fm.on(/\/branches\/main$/, (req) => {
            calls++;
            if (calls === 1) {
                expect(req.headers['if-none-match']).toBeUndefined();
                return jsonResp({ commit: { sha: 'abc123' } }, 200, { ETag: '"etag-v1"' });
            }
            expect(req.headers['if-none-match']).toBe('"etag-v1"');
            return new Response(null, { status: 304 });
        });
        expect(await a.branchHead()).toBe('abc123');
        expect(await a.branchHead()).toBe('abc123');  // served from cache
        expect(calls).toBe(2);
    });

    it('branchHead refreshes the cached sha when 200 (ETag changed)', async () => {
        let calls = 0;
        fm.on(/\/branches\/main$/, () => {
            calls++;
            if (calls === 1) return jsonResp({ commit: { sha: 'abc123' } }, 200, { ETag: '"etag-v1"' });
            return jsonResp({ commit: { sha: 'def456' } }, 200, { ETag: '"etag-v2"' });
        });
        expect(await a.branchHead()).toBe('abc123');
        expect(await a.branchHead()).toBe('def456');
    });

    it('getCommit normalizes parents to a sha array and exposes treeSha', async () => {
        fm.on(/\/git\/commits\/c1$/, () => jsonResp({
            sha: 'c1',
            tree: { sha: 't1' },
            parents: [{ sha: 'p1' }],
            message: 'hello',
            author: { name: 'alice', email: 'a@x', date: '2026-05-28T00:00:00Z' },
        }));
        const c = await a.getCommit('c1');
        expect(c.parents).toEqual(['p1']);
        expect(c.treeSha).toBe('t1');
        expect(c.author).toBe('alice');
        expect(c.message).toBe('hello');
    });

    it('getTree does the two-step commit → tree resolution', async () => {
        fm.on(/\/git\/commits\/c1$/, () => jsonResp({
            sha: 'c1', tree: { sha: 't1' }, parents: [], message: '', author: { name: 'a', email: 'a@x', date: '2026-01-01' },
        }));
        fm.on(/\/git\/trees\/t1/, () => jsonResp({
            tree: [
                { path: 'src.fbasic', sha: 'b-src', mode: '100644', type: 'blob', size: 12 },
                { path: 'assets', sha: 't-assets', mode: '040000', type: 'tree' },
                { path: 'assets/hero.png', sha: 'b-hero', mode: '100644', type: 'blob', size: 4 },
            ],
        }));
        const out = await a.getTree('c1');
        // Tree-type entries are skipped — only blob paths survive.
        expect(Object.keys(out).sort()).toEqual(['assets/hero.png', 'src.fbasic']);
        expect(out['src.fbasic'].blobSha).toBe('b-src');
        expect(out['assets/hero.png'].blobSha).toBe('b-hero');
    });

    it('getTree throws when the Trees API truncates (correctness safeguard)', async () => {
        fm.on(/\/git\/commits\/c1$/, () => jsonResp({
            sha: 'c1', tree: { sha: 't1' }, parents: [], message: '', author: { name: 'a', email: 'a@x', date: '2026-01-01' },
        }));
        fm.on(/\/git\/trees\/t1/, () => jsonResp({ tree: [], truncated: true }));
        await expect(a.getTree('c1')).rejects.toThrow(/truncated/);
    });

    it('getBlob decodes base64 to raw bytes', async () => {
        const original = new Uint8Array([10, 20, 30, 40, 50]);
        fm.on(/\/git\/blobs\/b1$/, () => jsonResp({ content: b64Bytes(original), encoding: 'base64' }));
        const out = await a.getBlob('b1');
        expect([...out]).toEqual([...original]);
    });
});

// ─── write path ─────────────────────────────────────────────────────────────

describe('GitHubAdapter: write path', () => {
    let fm: FetchMock;
    let a: GitHubAdapter;
    beforeEach(() => { fm = new FetchMock(); a = adapterWith(fm); });

    it('createBlob POSTs base64-encoded bytes and returns the sha', async () => {
        fm.on(/\/git\/blobs$/, (req) => {
            expect(req.method).toBe('POST');
            const body = JSON.parse(req.body as string);
            expect(body.encoding).toBe('base64');
            const decoded = bytesFromB64(body.content);
            expect([...decoded]).toEqual([1, 2, 3]);
            return jsonResp({ sha: 'b-new' }, 201);
        });
        const out = await a.createBlob(new Uint8Array([1, 2, 3]));
        expect(out.sha).toBe('b-new');
    });

    it('createTree sends entries (including null-sha deletions) + base_tree', async () => {
        fm.on(/\/git\/trees$/, (req) => {
            expect(req.method).toBe('POST');
            const body = JSON.parse(req.body as string);
            expect(body.base_tree).toBe('t-base');
            expect(body.tree).toEqual([
                { path: 'a.txt', mode: '100644', type: 'blob', sha: 'b-a' },
                { path: 'gone.txt', mode: '100644', type: 'blob', sha: null },
            ]);
            return jsonResp({ sha: 't-new' }, 201);
        });
        const out = await a.createTree({
            baseTreeSha: 't-base',
            entries: [
                { path: 'a.txt', blobSha: 'b-a' },
                { path: 'gone.txt', blobSha: null },
            ],
        });
        expect(out.sha).toBe('t-new');
    });

    it('createCommit POSTs message + tree + parents and returns the sha', async () => {
        fm.on(/\/git\/commits$/, (req) => {
            const body = JSON.parse(req.body as string);
            expect(body.message).toBe('hello');
            expect(body.tree).toBe('t1');
            expect(body.parents).toEqual(['p1']);
            return jsonResp({ sha: 'c-new' }, 201);
        });
        const out = await a.createCommit({ message: 'hello', treeSha: 't1', parents: ['p1'] });
        expect(out.sha).toBe('c-new');
    });

    it('updateBranch PATCHes the ref with force:false', async () => {
        fm.on(/\/git\/refs\/heads\/main$/, (req) => {
            expect(req.method).toBe('PATCH');
            const body = JSON.parse(req.body as string);
            expect(body).toEqual({ sha: 'c-new', force: false });
            return jsonResp({ ref: 'refs/heads/main', object: { sha: 'c-new' } });
        });
        await a.updateBranch('c-new');
    });

    it('updateBranch translates 422 (non-fast-forward) into HeadConflictError', async () => {
        let patchCalls = 0;
        fm.on(/\/git\/refs\/heads\/main$/, (req) => {
            if (req.method === 'PATCH') {
                patchCalls++;
                return jsonResp({ message: 'Update is not a fast forward' }, 422);
            }
            return jsonResp({}, 599);
        });
        // The error path also re-reads branchHead for the actual sha.
        fm.on(/\/branches\/main$/, () => jsonResp({ commit: { sha: 'actual-head' } }));
        const err = await a.updateBranch('mine').catch((e) => e);
        expect(err).toBeInstanceOf(HeadConflictError);
        expect((err as HeadConflictError).actual).toBe('actual-head');
        expect(patchCalls).toBe(1);
    });

    it('updateBranch falls back to POST when the ref does not exist yet (404 on PATCH)', async () => {
        let posted = false;
        fm.on((req) => req.method === 'PATCH' && /\/git\/refs\/heads\/main$/.test(req.url),
            () => jsonResp({ message: 'Reference does not exist' }, 404));
        fm.on((req) => req.method === 'POST' && /\/git\/refs$/.test(req.url), (req) => {
            const body = JSON.parse(req.body as string);
            expect(body.ref).toBe('refs/heads/main');
            expect(body.sha).toBe('c-new');
            posted = true;
            return jsonResp({}, 201);
        });
        await a.updateBranch('c-new');
        expect(posted).toBe(true);
    });
});

// ─── listCommits ────────────────────────────────────────────────────────────

describe('GitHubAdapter: listCommits', () => {
    let fm: FetchMock;
    let a: GitHubAdapter;
    beforeEach(() => { fm = new FetchMock(); a = adapterWith(fm); });

    it('parses the commits-feed shape into GitCommitMeta', async () => {
        fm.on(/\/commits\?/, () => jsonResp([
            {
                sha: 'c2', parents: [{ sha: 'c1' }],
                commit: {
                    message: 'two',
                    tree: { sha: 't2' },
                    author: { name: 'alice', date: '2026-05-28T01:00:00Z' },
                },
            },
            {
                sha: 'c1', parents: [],
                commit: {
                    message: 'one',
                    tree: { sha: 't1' },
                    author: { name: 'alice', date: '2026-05-28T00:00:00Z' },
                },
            },
        ]));
        const out = await a.listCommits({ start: 'c2', limit: 10 });
        expect(out.map((c) => ({ sha: c.sha, message: c.message }))).toEqual([
            { sha: 'c2', message: 'two' },
            { sha: 'c1', message: 'one' },
        ]);
        expect(out[1].parents).toEqual([]);
    });

    it('returns [] on 409 (empty repo, no commits yet)', async () => {
        fm.on(/\/commits\?/, () => jsonResp({ message: 'Git Repository is empty.' }, 409));
        expect(await a.listCommits()).toEqual([]);
    });
});

// ─── error surface ──────────────────────────────────────────────────────────

describe('GitHubApiError', () => {
    it('captures status and body and surfaces 401 distinctly from CAS', async () => {
        const fm = new FetchMock();
        fm.on(/\/branches\/main$/, () => jsonResp({ message: 'Bad credentials' }, 401));
        const a = adapterWith(fm);
        const err = await a.branchHead().catch((e) => e);
        expect(err).toBeInstanceOf(GitHubApiError);
        expect((err as GitHubApiError).status).toBe(401);
        expect(JSON.stringify((err as GitHubApiError).body)).toContain('Bad credentials');
        expect((err as GitHubApiError).message).toContain('Bad credentials');
    });
});
