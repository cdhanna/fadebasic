// Repo engine integration tests, driven against the in-memory MockAdapter
// which models git's blob/tree/commit/ref shape exactly the way the real
// GitHubAdapter does.
//
// Coverage:
//   - commit round-trip: snapshot → blobs → tree → commit → ref-advance
//   - dedup: unchanged blobs aren't re-uploaded
//   - checkout/clone reproduces the working tree from a commit's tree
//   - log walks the commit chain
//   - tryFastForward applies remote commits when the working tree is clean
//   - tryFastForward refuses to clobber a dirty working tree
//   - commit raises HeadConflictError when remote moved (pull-before-commit gate)
//   - setSyncedHead rehydrates the engine across instance rebuilds

import { beforeEach, describe, expect, it } from 'vitest';
import { HeadConflictError } from './adapter';
import { MockAdapter } from './mock-adapter';
import { Repo } from './repo';
import { MemoryWorkingTree, type WorkingTree } from './working-tree';
import { gitBlobSha } from './hash';

const enc = new TextEncoder();
const text = (s: string) => enc.encode(s);

async function seed(wt: WorkingTree, files: Record<string, string | Uint8Array>) {
    for (const [path, content] of Object.entries(files)) {
        await wt.write(path, typeof content === 'string' ? text(content) : content);
    }
}

async function readText(wt: WorkingTree, path: string): Promise<string> {
    return new TextDecoder().decode(await wt.read(path));
}

const AUTHOR = { author: 'tester', message: 'change' };

describe('Repo: commit + checkout round-trip', () => {
    let adapter: MockAdapter;
    let repo: Repo;
    let wt: MemoryWorkingTree;

    beforeEach(() => {
        adapter = new MockAdapter();
        repo = new Repo(adapter);
        wt = new MemoryWorkingTree();
    });

    it('first commit creates a root commit with no parents', async () => {
        await seed(wt, { 'main.fbasic': 'print "hi"', 'fade.json': '{}' });
        const c = await repo.commit(wt, { ...AUTHOR, message: 'init' });
        expect(c.parents).toEqual([]);
        expect(c.message).toBe('init');
        // Each file becomes its own blob.
        expect(adapter.blobCount()).toBe(2);
        // Branch ref advanced to this commit.
        expect(adapter.currentBranchHead()).toBe(c.sha);
    });

    it('round-trips bytes through a fresh checkout', async () => {
        await seed(wt, {
            'src.fbasic': 'print 1',
            'assets/hero.png': new Uint8Array([0x89, 0x50, 0x4e, 0x47]),
        });
        const c = await repo.commit(wt, { ...AUTHOR, message: 'init' });

        // Fresh repo / fresh working tree → checkout should reproduce both files.
        const repo2 = new Repo(adapter);
        const wt2 = new MemoryWorkingTree();
        await repo2.checkout(wt2, c.sha);

        expect(await readText(wt2, 'src.fbasic')).toBe('print 1');
        const png = await wt2.read('assets/hero.png');
        expect([...png]).toEqual([0x89, 0x50, 0x4e, 0x47]);
    });

    it('second commit dedups unchanged blobs (createBlob is idempotent)', async () => {
        await seed(wt, { 'a.txt': 'AAA', 'b.txt': 'BBB' });
        await repo.commit(wt, { ...AUTHOR, message: 'init' });
        const blobsAfterFirst = adapter.blobCount();

        // Only b.txt changes. a.txt's blob must NOT count as a new blob.
        await wt.write('b.txt', text('BBB-edited'));
        await repo.commit(wt, { ...AUTHOR, message: 'edit b' });

        // One additional blob (the new b.txt content). a.txt's blob already
        // present and idempotently re-stored by the adapter.
        expect(adapter.blobCount()).toBe(blobsAfterFirst + 1);
    });

    it('handles file deletion: tree at HEAD omits removed paths', async () => {
        await seed(wt, { 'keep.txt': 'k', 'gone.txt': 'g' });
        await repo.commit(wt, { ...AUTHOR, message: 'init' });
        await wt.delete('gone.txt');
        const c2 = await repo.commit(wt, { ...AUTHOR, message: 'rm gone' });

        const tree = await adapter.getTree(c2.sha);
        expect(Object.keys(tree).sort()).toEqual(['keep.txt']);
    });
});

describe('Repo: log walks the commit chain', () => {
    it('returns commits newest-first from a given starting point', async () => {
        const adapter = new MockAdapter();
        const repo = new Repo(adapter);
        const wt = new MemoryWorkingTree();
        await seed(wt, { 'a.txt': '1' });
        const c1 = await repo.commit(wt, { ...AUTHOR, message: 'one' });
        await wt.write('a.txt', text('2'));
        const c2 = await repo.commit(wt, { ...AUTHOR, message: 'two' });
        await wt.write('a.txt', text('3'));
        const c3 = await repo.commit(wt, { ...AUTHOR, message: 'three' });

        const log = await repo.log({ from: c3.sha, limit: 5 });
        expect(log.map((c) => c.message)).toEqual(['three', 'two', 'one']);
        expect(log[0].sha).toBe(c3.sha);
        expect(log[2].parents).toEqual([]);
        void c1; void c2;
    });
});

describe('Repo: tryFastForward', () => {
    it('applies a remote commit onto a clean working tree', async () => {
        const adapter = new MockAdapter();
        const wt = new MemoryWorkingTree();

        // "Remote" — author commits.
        const author = new Repo(adapter);
        await seed(wt, { 'a.txt': 'one' });
        const c1 = await author.commit(wt, { ...AUTHOR, message: 'init' });
        await wt.write('a.txt', text('two'));
        const c2 = await author.commit(wt, { ...AUTHOR, message: 'edit' });

        // "Local" — fresh Repo + WT seeded to the same starting state.
        const local = new Repo(adapter);
        const localWt = new MemoryWorkingTree();
        await local.checkout(localWt, c1.sha);
        // local now matches commit 1; remote is at commit 2 → FF expected.
        const result = await local.tryFastForward(localWt);
        expect(result.applied).toBe(true);
        expect(result.to).toBe(c2.sha);
        expect(await readText(localWt, 'a.txt')).toBe('two');
    });

    it('refuses to FF when the working tree is dirty', async () => {
        const adapter = new MockAdapter();
        const wt = new MemoryWorkingTree();
        const author = new Repo(adapter);
        await seed(wt, { 'a.txt': 'one' });
        const c1 = await author.commit(wt, { ...AUTHOR, message: 'init' });
        await wt.write('a.txt', text('two'));
        await author.commit(wt, { ...AUTHOR, message: 'remote edit' });

        const local = new Repo(adapter);
        const localWt = new MemoryWorkingTree();
        await local.checkout(localWt, c1.sha);
        // Dirty the working tree with a local change.
        await localWt.write('a.txt', text('local-edit'));

        const result = await local.tryFastForward(localWt);
        expect(result.applied).toBe(false);
        expect(result.dirty).toBe(true);
    });
});

describe('Repo: commit raises HeadConflictError on race', () => {
    it('two Repos sharing one MockAdapter — second commit collides', async () => {
        const adapter = new MockAdapter();
        const sharedWt = new MemoryWorkingTree();
        await seed(sharedWt, { 'a.txt': 'one' });

        const a = new Repo(adapter);
        await a.commit(sharedWt, { ...AUTHOR, message: 'init' });

        // Independent Repo instance — same backing store, but local syncedHead
        // tracks something different. We force this by *not* calling
        // refreshSyncedHead — `b` starts as if there were no prior commits.
        const b = new Repo(adapter);
        const bWt = new MemoryWorkingTree();
        await seed(bWt, { 'a.txt': 'different' });
        // b thinks it's emitting a root commit; updateBranch will reject
        // because the branch already has `a`'s commit.
        await expect(b.commit(bWt, { ...AUTHOR, message: 'b clobber' })).rejects.toBeInstanceOf(HeadConflictError);
    });
});

describe('Repo: setSyncedHead rehydration', () => {
    it('lets a fresh Repo commit cleanly when given a synced state', async () => {
        const adapter = new MockAdapter();
        const wt = new MemoryWorkingTree();
        await seed(wt, { 'a.txt': 'one' });
        const author = new Repo(adapter);
        const c1 = await author.commit(wt, { ...AUTHOR, message: 'init' });

        // Rebuild Repo from scratch — simulates the panel's `buildRepo`.
        const fresh = new Repo(adapter);
        const tree = await adapter.getTree(c1.sha);
        fresh.setSyncedHead({ commitSha: c1.sha, treeSha: (await adapter.getCommit(c1.sha)).treeSha }, tree);

        // Now edit and commit through `fresh`. Without setSyncedHead this
        // would error as a HeadConflict (its parent would be empty).
        await wt.write('a.txt', text('two'));
        const c2 = await fresh.commit(wt, { ...AUTHOR, message: 'edit' });
        expect(c2.parents).toEqual([c1.sha]);
    });
});

describe('Repo: stagedChanges', () => {
    it('reports added / modified / deleted paths against syncedTree', async () => {
        const adapter = new MockAdapter();
        const wt = new MemoryWorkingTree();
        await seed(wt, { 'kept.txt': 'k', 'edit.txt': 'old', 'gone.txt': 'g' });
        const repo = new Repo(adapter);
        await repo.commit(wt, { ...AUTHOR, message: 'init' });

        await wt.write('edit.txt', text('new'));
        await wt.delete('gone.txt');
        await wt.write('fresh.txt', text('f'));

        const diff = await repo.stagedChanges(wt);
        expect(diff.added).toEqual(['fresh.txt']);
        expect(diff.modified).toEqual(['edit.txt']);
        expect(diff.deleted).toEqual(['gone.txt']);
    });
});

describe('gitBlobSha integration', () => {
    it('matches the blob sha createBlob returns', async () => {
        const adapter = new MockAdapter();
        const bytes = text('hello playground');
        const local = await gitBlobSha(bytes);
        const { sha } = await adapter.createBlob(bytes);
        expect(sha).toBe(local);
    });
});
