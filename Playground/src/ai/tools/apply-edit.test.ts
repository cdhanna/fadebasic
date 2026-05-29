import { describe, it, expect } from 'vitest';
import { applyEdit } from './apply-edit';
import type { ToolContext, ToolWorkspace } from './index';

class InMemoryWorkspace implements ToolWorkspace {
    private files: Map<string, string>;
    constructor(initial: Record<string, string> = {}) {
        this.files = new Map(Object.entries(initial));
    }
    async list() { return [...this.files.keys()].sort(); }
    async read(name: string) {
        if (!this.files.has(name)) throw new Error(`not found: ${name}`);
        return this.files.get(name)!;
    }
    async write(name: string, content: string) { this.files.set(name, content); }
    currentProject() { return 'test'; }
    snapshot() { return Object.fromEntries(this.files); }
}

function ctx(workspace: ToolWorkspace, confirmEdit?: ToolContext['confirmEdit']): ToolContext {
    return { workspace, confirmEdit };
}

describe('apply_edit — basic splice', () => {
    it('replaces a single line with new content', async () => {
        const ws = new InMemoryWorkspace({
            'a.fade': 'function greet()\n  print "hello"\nend function',
        });
        const r = await applyEdit.execute(
            { path: 'a.fade', startLine: 2, endLine: 2, newText: '  print "world"' },
            ctx(ws, async () => true),
        );
        expect(r.ok).toBe(true);
        expect(ws.snapshot()['a.fade']).toBe('function greet()\n  print "world"\nend function');
    });

    it('inserts multiple lines in place of one', async () => {
        const ws = new InMemoryWorkspace({
            'a.fade': 'L1\nL2\nL3',
        });
        const r = await applyEdit.execute(
            { path: 'a.fade', startLine: 2, endLine: 2, newText: 'NEW_A\nNEW_B' },
            ctx(ws, async () => true),
        );
        expect(r.ok).toBe(true);
        expect(ws.snapshot()['a.fade']).toBe('L1\nNEW_A\nNEW_B\nL3');
    });
});

describe('apply_edit — line-ending preservation', () => {
    it('preserves CRLF when the file originally used CRLF', async () => {
        const original = 'L1\r\nL2\r\nL3\r\n';
        const ws = new InMemoryWorkspace({ 'a.fade': original });
        const r = await applyEdit.execute(
            { path: 'a.fade', startLine: 2, endLine: 2, newText: 'NEW' },  // LF in newText
            ctx(ws, async () => true),
        );
        expect(r.ok).toBe(true);
        const after = ws.snapshot()['a.fade'];
        // File should be CRLF throughout, not mixed line endings
        expect(after).not.toContain('\r\r');
        // No lone LF without preceding CR
        const lfCount = (after.match(/\n/g) ?? []).length;
        const crlfCount = (after.match(/\r\n/g) ?? []).length;
        expect(lfCount).toBe(crlfCount);
        // The new content is present
        expect(after).toContain('NEW');
        // L1 and L3 are still present
        expect(after).toContain('L1\r\n');
        expect(after).toContain('L3\r\n');
    });

    it('does NOT introduce CRLF in a file that was pure LF', async () => {
        const ws = new InMemoryWorkspace({ 'a.fade': 'L1\nL2\nL3\n' });
        const r = await applyEdit.execute(
            { path: 'a.fade', startLine: 2, endLine: 2, newText: 'NEW' },
            ctx(ws, async () => true),
        );
        expect(r.ok).toBe(true);
        const after = ws.snapshot()['a.fade'];
        expect(after).not.toContain('\r');
        expect(after).toBe('L1\nNEW\nL3\n');
    });

    it('passes a clean (normalized) diff to confirmEdit even when source is CRLF', async () => {
        const ws = new InMemoryWorkspace({ 'a.fade': 'L1\r\nL2\r\nL3\r\n' });
        let oldSeen = '';
        let newSeen = '';
        const r = await applyEdit.execute(
            { path: 'a.fade', startLine: 2, endLine: 2, newText: 'NEW' },
            ctx(ws, async (_p, oldC, newC) => {
                oldSeen = oldC;
                newSeen = newC;
                return true;
            }),
        );
        expect(r.ok).toBe(true);
        // confirmEdit should see LF-normalized content so the diff doesn't
        // hallucinate phantom changes from \r drift.
        expect(oldSeen).not.toContain('\r');
        expect(newSeen).not.toContain('\r');
        expect(oldSeen).toBe('L1\nL2\nL3\n');
        expect(newSeen).toBe('L1\nNEW\nL3\n');
    });
});

describe('apply_edit — rejection', () => {
    it('returns ok:false and does NOT write when user rejects', async () => {
        const ws = new InMemoryWorkspace({ 'a.fade': 'OLD' });
        const r = await applyEdit.execute(
            { path: 'a.fade', startLine: 1, endLine: 1, newText: 'NEW' },
            ctx(ws, async () => false),
        );
        expect(r.ok).toBe(false);
        expect(ws.snapshot()['a.fade']).toBe('OLD');
    });
});
