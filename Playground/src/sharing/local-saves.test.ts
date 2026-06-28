// Unit tests for the local-saves snapshot store. Storage is injected as
// a MemorySaveStorage so the tests don't need a browser localStorage.
// Workspace is a tiny in-memory fake that satisfies SaveWorkspaceLike.

import { beforeEach, describe, expect, it } from 'vitest';
import {
    MemorySaveStorage,
    clearSaves,
    createSave,
    dropSave,
    loadSaves,
    revertToSave,
    upgradeSave,
    type LocalSave,
    type SaveWorkspaceLike,
} from './local-saves';
import { gitBlobSha } from './hash';

class MockWorkspace implements SaveWorkspaceLike {
    files = new Map<string, Uint8Array>();
    async list(): Promise<string[]> { return [...this.files.keys()].sort(); }
    async readBytes(name: string): Promise<Uint8Array> {
        const b = this.files.get(name);
        if (!b) throw new Error(`not found: ${name}`);
        return new Uint8Array(b);
    }
    async writeBytes(name: string, bytes: Uint8Array): Promise<void> {
        this.files.set(name, new Uint8Array(bytes));
    }
    async delete(name: string): Promise<void> {
        this.files.delete(name);
    }
}

const enc = new TextEncoder();
const text = (s: string) => enc.encode(s);
const decode = (b: Uint8Array) => new TextDecoder().decode(b);

describe('createSave', () => {
    let storage: MemorySaveStorage;
    let ws: MockWorkspace;
    beforeEach(() => { storage = new MemorySaveStorage(); ws = new MockWorkspace(); });

    it('snapshots every workspace file into the save', async () => {
        await ws.writeBytes('a.txt', text('hello'));
        await ws.writeBytes('b.txt', text('world'));
        const save = await createSave('p', ws, 'first', storage);
        expect(Object.keys(save.files).sort()).toEqual(['a.txt', 'b.txt']);
        // base64 decode each and verify content roundtripped.
        const aBytes = Buffer.from(save.files['a.txt'], 'base64');
        expect(decode(aBytes)).toBe('hello');
    });

    it('populates treeHashes that match gitBlobSha of the bytes', async () => {
        await ws.writeBytes('a.txt', text('hello'));
        const save = await createSave('p', ws, 'first', storage);
        expect(save.treeHashes).toBeDefined();
        expect(save.treeHashes!['a.txt']).toBe(await gitBlobSha(text('hello')));
    });

    it('filters out *.fade-conflict.* scratch files', async () => {
        await ws.writeBytes('a.txt', text('hello'));
        await ws.writeBytes('a.txt.fade-conflict.abc123', text('remote version'));
        const save = await createSave('p', ws, 'first', storage);
        expect(Object.keys(save.files)).toEqual(['a.txt']);
        expect(save.treeHashes!['a.txt.fade-conflict.abc123']).toBeUndefined();
    });

    it('stores newest-first and persists across reloads', async () => {
        await ws.writeBytes('a.txt', text('v1'));
        await createSave('p', ws, 'first', storage);
        await ws.writeBytes('a.txt', text('v2'));
        await createSave('p', ws, 'second', storage);
        const reloaded = await loadSaves('p', storage);
        expect(reloaded.map((s) => s.message)).toEqual(['second', 'first']);
    });

    it('keys are scoped per project', async () => {
        await ws.writeBytes('a.txt', text('shared'));
        await createSave('alpha', ws, 'in alpha', storage);
        await createSave('beta', ws, 'in beta', storage);
        expect((await loadSaves('alpha', storage)).map((s) => s.message)).toEqual(['in alpha']);
        expect((await loadSaves('beta', storage)).map((s) => s.message)).toEqual(['in beta']);
    });

    it('LRU-trims to MAX_SAVES_PER_PROJECT', async () => {
        await ws.writeBytes('a.txt', text('x'));
        // Create 12 saves; only 10 should remain.
        for (let i = 0; i < 12; i++) {
            await createSave('p', ws, `save ${i}`, storage);
        }
        const all = await loadSaves('p', storage);
        expect(all.length).toBe(10);
        // Newest first → last save kept, oldest dropped.
        expect(all[0].message).toBe('save 11');
        expect(all[all.length - 1].message).toBe('save 2');
    });
});

describe('loadSaves', () => {
    it('returns [] when nothing stored', async () => {
        const storage = new MemorySaveStorage();
        expect(await loadSaves('p', storage)).toEqual([]);
    });

    it('returns [] when JSON is corrupt (graceful fallback)', async () => {
        const storage = new MemorySaveStorage();
        await storage.setItem('fade-sharing:saves-v1:p', '{not-valid-json');
        expect(await loadSaves('p', storage)).toEqual([]);
    });
});

describe('revertToSave', () => {
    let storage: MemorySaveStorage;
    let ws: MockWorkspace;
    beforeEach(() => { storage = new MemorySaveStorage(); ws = new MockWorkspace(); });

    it('overwrites changed files back to the saved content', async () => {
        await ws.writeBytes('a.txt', text('saved'));
        const save = await createSave('p', ws, 'baseline', storage);
        await ws.writeBytes('a.txt', text('edited!'));
        await revertToSave(ws, save);
        expect(decode(await ws.readBytes('a.txt'))).toBe('saved');
    });

    it('deletes files that exist in the workspace but not the save', async () => {
        await ws.writeBytes('a.txt', text('one'));
        const save = await createSave('p', ws, 'baseline', storage);
        // Now add a file that wasn't in the save.
        await ws.writeBytes('extra.txt', text('added since save'));
        await revertToSave(ws, save);
        expect(await ws.list()).toEqual(['a.txt']);
    });

    it('leaves .fade-conflict.* scratch files untouched (not in the save, not deleted)', async () => {
        await ws.writeBytes('a.txt', text('one'));
        const save = await createSave('p', ws, 'baseline', storage);
        // Scratch conflict copy appears (hidden from saves).
        await ws.writeBytes('a.txt.fade-conflict.deadbeef', text('remote'));
        await revertToSave(ws, save);
        // Both should be present: a.txt from the save, conflict-copy left alone.
        expect((await ws.list()).sort()).toEqual(['a.txt', 'a.txt.fade-conflict.deadbeef']);
    });
});

describe('dropSave', () => {
    it('removes only the targeted save by id', async () => {
        const storage = new MemorySaveStorage();
        const ws = new MockWorkspace();
        await ws.writeBytes('a.txt', text('x'));
        const s1 = await createSave('p', ws, 'first', storage);
        const s2 = await createSave('p', ws, 'second', storage);
        await dropSave('p', s1.id, storage);
        const left = await loadSaves('p', storage);
        expect(left.map((s) => s.id)).toEqual([s2.id]);
    });

    it('no-op when id is unknown', async () => {
        const storage = new MemorySaveStorage();
        const ws = new MockWorkspace();
        await ws.writeBytes('a.txt', text('x'));
        await createSave('p', ws, 'first', storage);
        await dropSave('p', 'never-existed', storage);
        expect((await loadSaves('p', storage)).length).toBe(1);
    });
});

describe('clearSaves', () => {
    it('wipes the entire chain for one project', async () => {
        const storage = new MemorySaveStorage();
        const ws = new MockWorkspace();
        await ws.writeBytes('a.txt', text('x'));
        await createSave('p', ws, 'a', storage);
        await createSave('p', ws, 'b', storage);
        await clearSaves('p', storage);
        expect(await loadSaves('p', storage)).toEqual([]);
    });

    it('leaves other projects intact', async () => {
        const storage = new MemorySaveStorage();
        const ws = new MockWorkspace();
        await ws.writeBytes('a.txt', text('x'));
        await createSave('alpha', ws, 'in alpha', storage);
        await createSave('beta', ws, 'in beta', storage);
        await clearSaves('alpha', storage);
        expect(await loadSaves('alpha', storage)).toEqual([]);
        expect((await loadSaves('beta', storage)).map((s) => s.message)).toEqual(['in beta']);
    });
});

describe('upgradeSave', () => {
    it('back-fills treeHashes from base64 file contents', async () => {
        const legacy: LocalSave = {
            id: 'legacy-1',
            message: 'pre-treehashes save',
            time: '2026-05-28T00:00:00Z',
            files: {
                'a.txt': Buffer.from('legacy content').toString('base64'),
            },
            // no treeHashes
        };
        const upgraded = await upgradeSave(legacy);
        expect(upgraded.treeHashes).toBeDefined();
        expect(upgraded.treeHashes!['a.txt']).toBe(await gitBlobSha(text('legacy content')));
    });

    it('returns the same record unchanged when treeHashes already present', async () => {
        const save: LocalSave = {
            id: 's',
            message: 'm',
            time: 't',
            files: { 'a': 'YWJj' },
            treeHashes: { 'a': 'cached-sha' },
        };
        const out = await upgradeSave(save);
        expect(out.treeHashes!['a']).toBe('cached-sha');
    });
});

describe('round-trip integration', () => {
    it('save → edit → revertToSave restores exact bytes', async () => {
        const storage = new MemorySaveStorage();
        const ws = new MockWorkspace();
        await ws.writeBytes('src.fbasic', text('print "v1"'));
        await ws.writeBytes('fade.json', text('{"v":1}'));
        const save = await createSave('p', ws, 'baseline', storage);

        // Heavy edits.
        await ws.writeBytes('src.fbasic', text('print "totally different"'));
        await ws.writeBytes('fade.json', text('{"v":2}'));
        await ws.writeBytes('new.txt', text('added since save'));

        await revertToSave(ws, save);

        const list = (await ws.list()).sort();
        expect(list).toEqual(['fade.json', 'src.fbasic']);
        expect(decode(await ws.readBytes('src.fbasic'))).toBe('print "v1"');
        expect(decode(await ws.readBytes('fade.json'))).toBe('{"v":1}');
    });

    it('after createSave, treeHashes matches gitBlobSha of workspace bytes', async () => {
        const storage = new MemorySaveStorage();
        const ws = new MockWorkspace();
        await ws.writeBytes('a.txt', text('alpha'));
        await ws.writeBytes('b.txt', text('beta'));
        const save = await createSave('p', ws, 'snap', storage);
        for (const path of await ws.list()) {
            const bytes = await ws.readBytes(path);
            expect(save.treeHashes![path]).toBe(await gitBlobSha(bytes));
        }
    });

    // Models the exact panel flow that drives "did Save clear the unsaved
    // chip?": modify a tracked file, snapshot via createSave, reload via
    // loadSaves, then run computeStatus against the loaded save's
    // treeHashes. A stale HashCache (the bug fixed in main.ts'
    // flushPendingSaves) would surface here as a still-'modified' status.
    it('modify → createSave → loadSaves → computeStatus reports unchanged (no stale cache)', async () => {
        const { computeStatus, HashCache } = await import('./file-status');
        const { MemoryWorkingTree } = await import('./working-tree');
        const storage = new MemorySaveStorage();
        const wt = new MemoryWorkingTree();
        const cache = new HashCache();

        await wt.write('main.fbasic', text('print "old"'));
        const baseTree = { 'main.fbasic': await gitBlobSha(text('print "old"')) };

        // First pass: warm the cache against the baseTree — file matches base.
        const first = await computeStatus(wt, baseTree, cache);
        expect(first.find((e) => e.path === 'main.fbasic')?.status).toBe('unchanged');

        // User edits the file (external write — caller MUST invalidate).
        await wt.write('main.fbasic', text('print "new"'));
        cache.invalidate('main.fbasic');

        const modified = await computeStatus(wt, baseTree, cache);
        expect(modified.find((e) => e.path === 'main.fbasic')?.status).toBe('modified');

        // User clicks Save: createSave reads via SaveWorkspaceLike — adapt
        // the WorkingTree to that interface for the snapshot call.
        const wsForSave: SaveWorkspaceLike = {
            list: () => wt.list(),
            readBytes: (p) => wt.read(p),
            writeBytes: (p, b) => wt.write(p, b),
            delete: (p) => wt.delete(p),
        };
        await createSave('p', wsForSave, 'first', storage);

        // Reload from storage exactly the way refreshSaves does, then use
        // the new save's treeHashes as the reference for computeStatus.
        const loaded = await loadSaves('p', storage);
        expect(loaded.length).toBe(1);
        expect(loaded[0].treeHashes!['main.fbasic']).toBeDefined();

        const afterSave = await computeStatus(wt, loaded[0].treeHashes!, cache);
        expect(afterSave.find((e) => e.path === 'main.fbasic')?.status).toBe('unchanged');
    });
});
