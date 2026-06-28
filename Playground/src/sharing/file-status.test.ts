// Unit tests for file-status: the HashCache class + computeStatus,
// including cache-hit/cache-miss behavior. Uses MemoryWorkingTree from
// working-tree.ts as the fake working tree so we never touch OPFS.

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { HashCache, computeStatus, pathStatusHint, statusGlyph } from './file-status';
import { gitBlobSha } from './hash';
import { MemoryWorkingTree } from './working-tree';

const enc = new TextEncoder();
const text = (s: string) => enc.encode(s);

describe('HashCache', () => {
    it('get/set roundtrips', () => {
        const c = new HashCache();
        expect(c.get('a')).toBeUndefined();
        c.set('a', 'sha-a');
        expect(c.get('a')).toBe('sha-a');
    });
    it('invalidate drops one entry', () => {
        const c = new HashCache();
        c.set('a', 'sha-a');
        c.set('b', 'sha-b');
        c.invalidate('a');
        expect(c.get('a')).toBeUndefined();
        expect(c.get('b')).toBe('sha-b');
    });
    it('invalidateAll wipes', () => {
        const c = new HashCache();
        c.set('a', 'sha-a');
        c.set('b', 'sha-b');
        c.invalidateAll();
        expect(c.size()).toBe(0);
    });
});

describe('computeStatus (no cache)', () => {
    let wt: MemoryWorkingTree;
    beforeEach(() => { wt = new MemoryWorkingTree(); });

    it('reports added/modified/deleted/unchanged correctly', async () => {
        await wt.write('keep.txt', text('same'));
        await wt.write('edit.txt', text('NEW'));
        await wt.write('add.txt', text('extra'));
        const keepHash = await gitBlobSha(text('same'));
        const editBaseHash = await gitBlobSha(text('OLD'));
        const goneHash = await gitBlobSha(text('removed content'));
        const baseTree = {
            'keep.txt': keepHash,
            'edit.txt': editBaseHash,
            'gone.txt': goneHash,
        };
        const out = await computeStatus(wt, baseTree);
        const byPath = Object.fromEntries(out.map((e) => [e.path, e.status]));
        expect(byPath['keep.txt']).toBe('unchanged');
        expect(byPath['edit.txt']).toBe('modified');
        expect(byPath['add.txt']).toBe('added');
        expect(byPath['gone.txt']).toBe('deleted');
    });

    it('empty working tree + empty base → no entries', async () => {
        expect(await computeStatus(wt, {})).toEqual([]);
    });
});

describe('computeStatus (with HashCache)', () => {
    let wt: MemoryWorkingTree;
    let cache: HashCache;
    beforeEach(() => { wt = new MemoryWorkingTree(); cache = new HashCache(); });

    it('first pass reads + hashes, second pass hits cache (no re-read)', async () => {
        await wt.write('a.txt', text('one'));
        await wt.write('b.txt', text('two'));
        const aHash = await gitBlobSha(text('one'));
        const bHash = await gitBlobSha(text('two'));
        const base = { 'a.txt': aHash, 'b.txt': bHash };

        // Spy on read to count IO.
        const readSpy = vi.spyOn(wt, 'read');

        await computeStatus(wt, base, cache);
        expect(readSpy).toHaveBeenCalledTimes(2);

        readSpy.mockClear();
        await computeStatus(wt, base, cache);
        // Second pass: cached → zero reads of the file bytes.
        expect(readSpy).toHaveBeenCalledTimes(0);
    });

    it('invalidate(path) causes that one path to be re-read on the next pass', async () => {
        await wt.write('a.txt', text('one'));
        await wt.write('b.txt', text('two'));
        await computeStatus(wt, {}, cache);   // warm

        const readSpy = vi.spyOn(wt, 'read');
        cache.invalidate('a.txt');
        await computeStatus(wt, {}, cache);
        expect(readSpy).toHaveBeenCalledTimes(1);
        expect(readSpy).toHaveBeenCalledWith('a.txt');
    });

    it('after edit + invalidate, status reflects new content', async () => {
        await wt.write('a.txt', text('one'));
        const baseHash = await gitBlobSha(text('one'));
        const base = { 'a.txt': baseHash };

        await computeStatus(wt, base, cache);   // warm — status: unchanged

        // Simulate an external write that the caller knows about.
        await wt.write('a.txt', text('TWO'));
        cache.invalidate('a.txt');

        const out = await computeStatus(wt, base, cache);
        expect(out.find((e) => e.path === 'a.txt')?.status).toBe('modified');
    });

    it('stale cache (no invalidate after write) reports stale status — caller bug, surfaced honestly', async () => {
        // This test documents the contract: callers MUST invalidate when
        // they write. If they don't, status reports last-known content.
        await wt.write('a.txt', text('one'));
        const base = { 'a.txt': await gitBlobSha(text('one')) };
        await computeStatus(wt, base, cache);
        // Edit without invalidating.
        await wt.write('a.txt', text('CHANGED'));
        const out = await computeStatus(wt, base, cache);
        // The cache still has the old hash → status comes back unchanged.
        expect(out.find((e) => e.path === 'a.txt')?.status).toBe('unchanged');
    });
});

describe('pathStatusHint', () => {
    it('added: path in live, not in base', () => {
        expect(pathStatusHint('a', new Set(['a']), {})).toBe('added');
    });
    it('deleted: path in base, not in live', () => {
        expect(pathStatusHint('a', new Set(), { 'a': 'sha' })).toBe('deleted');
    });
    it('unchanged (conservative): in both — structural check only', () => {
        // pathStatusHint doesn't hash; it can't tell modified from
        // unchanged. The contract is "no false positives on modified".
        expect(pathStatusHint('a', new Set(['a']), { 'a': 'any-sha' })).toBe('unchanged');
    });
});

describe('statusGlyph', () => {
    it('maps each status to its single-char glyph', () => {
        expect(statusGlyph('added')).toBe('A');
        expect(statusGlyph('modified')).toBe('M');
        expect(statusGlyph('deleted')).toBe('D');
        expect(statusGlyph('unchanged')).toBe('');
    });
});
