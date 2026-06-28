import { describe, it, expect } from 'vitest';
import { createFile } from './create-file';
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

const ctx = (ws: ToolWorkspace): ToolContext => ({ workspace: ws, confirmEdit: async () => true });

describe('create_file', () => {
    it('registers a new .fbasic file in fade.json sources', async () => {
        const ws = new InMemoryWorkspace({ 'fade.json': JSON.stringify({ name: 'g', type: 'web', sources: ['main.fbasic'] }) });
        const r = await createFile.execute({ path: 'enemy.fbasic', content: 'rem enemy' }, ctx(ws));
        expect(r.ok).toBe(true);
        expect((r.result as { addedToSources: boolean }).addedToSources).toBe(true);
        const cfg = JSON.parse(ws.snapshot()['fade.json']);
        expect(cfg.sources).toEqual(['main.fbasic', 'enemy.fbasic']);
    });

    it('does not touch sources for a non-Fade file', async () => {
        const ws = new InMemoryWorkspace({ 'fade.json': JSON.stringify({ sources: ['main.fbasic'] }) });
        const r = await createFile.execute({ path: 'notes.txt', content: 'hi' }, ctx(ws));
        expect((r.result as { addedToSources: boolean }).addedToSources).toBe(false);
        expect(JSON.parse(ws.snapshot()['fade.json']).sources).toEqual(['main.fbasic']);
    });

    it('accepts the newText alias for content', async () => {
        const ws = new InMemoryWorkspace();
        const r = await createFile.execute({ path: 'a.txt', newText: 'body' } as never, ctx(ws));
        expect(r.ok).toBe(true);
        expect(ws.snapshot()['a.txt']).toBe('body');
    });

    it('errors clearly when args are missing', async () => {
        const ws = new InMemoryWorkspace();
        const r = await createFile.execute({} as never, ctx(ws));
        expect(r.ok).toBe(false);
        expect((r.result as { error: string }).error).toMatch(/missing required/i);
    });
});
