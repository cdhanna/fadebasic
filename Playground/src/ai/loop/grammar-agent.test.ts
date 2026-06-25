import { describe, it, expect } from 'vitest';
import { GrammarAgent } from './grammar-agent';
import { MockProvider, mockTurn } from '../providers/mock';
import type { Retriever } from '../rag/retrieval';
import type { AgentEvent } from '../agent';
import { createDefaultRegistry } from '../tools/default-registry';
import type { ToolContext } from '../tools';

function memWorkspace(files: Record<string, string>) {
    return {
        list: async () => Object.keys(files),
        read: async (n: string) => { if (!(n in files)) throw new Error('not found'); return files[n]; },
        write: async (n: string, c: string) => { files[n] = c; },
    };
}

function fakeRetriever(): Retriever {
    return {
        search: async () => [{
            chunk: { id: 'c1', source: 'doc.md', heading: 'Sprites', text: 'sprite n, x, y', chars: 14 },
            score: 0.9,
        }],
    } as unknown as Retriever;
}

function collect(agent: GrammarAgent): AgentEvent[] {
    const evs: AgentEvent[] = [];
    agent.on(e => evs.push(e));
    return evs;
}

describe('GrammarAgent — write_code branch', () => {
    it('classifies, researches, and emits code with the right grammar per node', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),                 // 1: classify
            mockTurn.text('read the arrow keys\ndraw a sprite'), // 2: capabilities
            mockTurn.text('x = 0\nsprite 1, x, 0'),      // 3: emit-code (pure Fade)
        ]);
        const agent = new GrammarAgent({
            provider,
            retriever: fakeRetriever(),
            getCommandNames: async () => ['sprite', 'print'],
            getProjectType: () => 'monogame',
        });
        const evs = collect(agent);
        await agent.send('make a sprite move with the arrow keys');

        // classify runs greedy (temp 0); correctness comes from the focused
        // context + post-emit verify (no decode-time constraint).
        expect(provider.sentMessages[0].temperature).toBe(0);

        // Visible plan + docs from the research node.
        expect(evs.some(e => e.kind === 'plan_emitted')).toBe(true);
        expect(evs.some(e => e.kind === 'docs_retrieved')).toBe(true);

        // The emitted code is streamed into the bubble inside a fade fence.
        const streamed = evs.filter((e): e is Extract<AgentEvent, { kind: 'text_delta' }> => e.kind === 'text_delta')
            .map(e => e.delta).join('');
        expect(streamed).toContain('```fade');
        expect(streamed).toContain('sprite 1, x, 0');

        // Turn completes cleanly; history records the fenced answer.
        expect(evs.some(e => e.kind === 'turn_complete')).toBe(true);
        const last = agent.getHistory().at(-1);
        expect(last?.role).toBe('assistant');
        expect(last?.content).toContain('sprite 1, x, 0');
    });

    it('edit_code: reads the file, emits the full updated file (unconstrained), applies it', async () => {
        const provider = new MockProvider([
            mockTurn.text('edit_code'),   // classify
            mockTurn.text('print 2'),     // emit-code: complete updated file
        ]);
        const files = { 'main.fbasic': 'print 1' };
        const toolContext = { workspace: memWorkspace(files), confirmEdit: async () => true } as unknown as ToolContext;
        const agent = new GrammarAgent({
            provider,
            tools: createDefaultRegistry(),
            toolContext,
            getCommandNames: async () => ['print'],
        });
        const evs = collect(agent);
        await agent.send('change main.fbasic to print 2');

        // It read the file and applied an edit (deterministic tool calls).
        const toolStarts = evs.filter((e): e is Extract<AgentEvent, { kind: 'tool_call_start' }> => e.kind === 'tool_call_start');
        expect(toolStarts.map(e => e.name)).toEqual(expect.arrayContaining(['read_file', 'apply_edit']));
        // The edit was actually written through the tool.
        expect(files['main.fbasic']).toBe('print 2');
    });

    it('write_code: curates the retrieved docs (keeps the relevant ones) before coding', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),                       // classify
            mockTurn.text('draw a sprite\nanimate a sprite'),  // capabilities
            mockTurn.text('KEEP: 1'),                          // curate — keep only the simple one
            mockTurn.text('sprite 1, 100, 100'),              // emit
        ]);
        // Distinct hit per query → >1 hit → curation runs.
        const retriever = {
            search: async (q: string) => [{
                chunk: { id: q, source: 'doc.md', heading: q, text: `docs about ${q}`, chars: 10 },
                score: 0.9,
            }],
        } as unknown as Retriever;
        const agent = new GrammarAgent({ provider, retriever, getCommandNames: async () => ['sprite'] });
        const evs = collect(agent);
        await agent.send('write me a simple sprite demo');

        // The curate node ran and narrowed the docs.
        expect(evs.some(e => e.kind === 'reasoning' && /Kept \d+ most-relevant/i.test(e.title))).toBe(true);
        const docs = evs.find((e): e is Extract<AgentEvent, { kind: 'docs_retrieved' }> => e.kind === 'docs_retrieved');
        expect(docs?.hits).toHaveLength(1);
        expect(docs?.hits[0].chunk.heading).toBe('draw a sprite');  // kept the simple one, dropped "animate"
    });

    it('write_code: verifies emitted code and re-emits to fix invalid Fade (no grammar)', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),                  // classify
            mockTurn.text('while 1\nx = 1\nwend'),        // emit #1 — `wend` is invalid
            mockTurn.text('while 1\nx = 1\nendwhile'),    // emit #2 — fixed after verify
        ]);
        const agent = new GrammarAgent({
            provider,
            retriever: null,                              // skip research → fewer turns
            getCommandNames: async () => ['print', 'sync'],
        });
        const evs = collect(agent);
        await agent.send('write a loop');

        // Post-emit verify caught `wend` and triggered a fix.
        expect(evs.some(e => e.kind === 'reasoning' && /found \d+ issue/i.test(e.title))).toBe(true);
        expect(evs.some(e => e.kind === 'revising')).toBe(true);
        // Final answer is the corrected code.
        const last = agent.getHistory().at(-1)?.content ?? '';
        expect(last).toContain('endwhile');
        expect(last).not.toContain('wend');
    });

    it('write_code: review catches a value command used without parens and fixes it', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),            mockTurn.text('position sprite 1, mouse x, mouse y'),   // emit #1 — missing parens
            mockTurn.text('position sprite 1, mouse x(), mouse y()'), // emit #2 — fixed
        ]);
        const agent = new GrammarAgent({
            provider,
            retriever: null,
            getCommandNames: async () => ['position sprite', 'mouse x', 'mouse y'],
            getValueReturningCommands: async () => ['mouse x', 'mouse y'],
        });
        const evs = collect(agent);
        await agent.send('make the sprite follow the mouse');

        expect(evs.some(e => e.kind === 'reasoning' && /found \d+ issue/i.test(e.title))).toBe(true);
        const last = agent.getHistory().at(-1)?.content ?? '';
        expect(last).toContain('mouse x()');
        expect(last).toContain('mouse y()');
    });

    it('write_code: resolves the commands it will use and injects their EXACT signatures into the coder', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),                       // classify
            mockTurn.text('mouse x\nmouse y\nnotacommand'),    // resolveCommandDocs — names (one bogus)
            mockTurn.text('mx = mouse x()\nmy = mouse y()'),   // emit
        ]);
        const agent = new GrammarAgent({
            provider,
            retriever: null,  // skip RAG → resolveCommandDocs is the only doc source
            getCommandNames: async () => ['mouse x', 'mouse y'],
            getCommandDocs: async () => [
                { name: 'mouse x', signature: 'integerR', markdown: '### mouse x\n`integer mouse x()` — current mouse X position.' },
                { name: 'mouse y', signature: 'integerR', markdown: '### mouse y\n`integer mouse y()` — current mouse Y position.' },
            ],
        });
        const evs = collect(agent);
        await agent.send('print the mouse position');

        // The resolve node ran and reported the commands it looked up.
        expect(evs.some(e => e.kind === 'reasoning' && /exact signatures for 2 command/i.test(e.title))).toBe(true);
        // The coder's system prompt carries the exact signatures (and not the bogus name).
        const coderSys = provider.sentMessages.at(-1)?.messages[0].content ?? '';
        expect(coderSys).toContain('EXACT command reference');
        expect(coderSys).toContain('mouse x()');
        expect(coderSys).toContain('mouse y()');
        expect(coderSys).not.toContain('notacommand');
    });

    it('write_code (monogame): review catches a file extension on an asset load and fixes it', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),            mockTurn.text('texture 1, "ship.png"\nsprite 1'),   // emit #1 — extension
            mockTurn.text('texture 1, "ship"\nsprite 1'),       // emit #2 — fixed
        ]);
        const agent = new GrammarAgent({
            provider,
            retriever: null,
            getProjectType: () => 'monogame',
            getCommandNames: async () => ['texture', 'sprite'],
        });
        const evs = collect(agent);
        await agent.send('load a ship sprite');

        expect(evs.some(e => e.kind === 'reasoning' && /found \d+ issue/i.test(e.title))).toBe(true);
        const last = agent.getHistory().at(-1)?.content ?? '';
        expect(last).toContain('"ship"');
        expect(last).not.toContain('ship.png');
    });

    function fakeCatalogCtx(imported: { name: string; paths: string[] }): ToolContext {
        return {
            workspace: memWorkspace({}),
            catalog: {
                search: async () => [
                    { id: 7, name: 'Spaceship', kind: 'asset', mime: 'image/png', tags: ['sprite'], description: null, bytes: 2048, license: 'CC0' },
                ],
                import: async () => imported,
            },
        } as unknown as ToolContext;
    }

    it('write_code: asset request → searches catalog, imports on confirm, injects the bare asset name', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),            mockTurn.text('texture 1, "ship"\nsprite 1'),
        ]);
        const confirmCalls: string[] = [];
        const agent = new GrammarAgent({
            provider,
            retriever: null,
            getProjectType: () => 'monogame',
            tools: createDefaultRegistry(),
            toolContext: fakeCatalogCtx({ name: 'Spaceship', paths: ['catalog-imports/ship.png'] }),
            confirmCatalogImport: async (e) => { confirmCalls.push(e.name); return true; },
            getCommandNames: async () => ['texture', 'sprite'],
        });
        const evs = collect(agent);
        await agent.send('make a spaceship sprite');

        // It searched + imported (visible tool calls), and asked to confirm first.
        const toolNames = evs.filter((e): e is Extract<AgentEvent, { kind: 'tool_call_start' }> => e.kind === 'tool_call_start').map(e => e.name);
        expect(toolNames).toContain('search_catalog');
        expect(toolNames).toContain('import_catalog_asset');
        expect(confirmCalls).toEqual(['Spaceship']);
        // The coder was told the real, extension-less asset name.
        const coderSys = provider.sentMessages.at(-1)?.messages[0].content ?? '';
        expect(coderSys).toContain('"ship"');
    });

    it('write_code: asset request, confirm rejected → does NOT import', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),            mockTurn.text('print "no asset"'),
        ]);
        const agent = new GrammarAgent({
            provider,
            retriever: null,
            getProjectType: () => 'monogame',
            tools: createDefaultRegistry(),
            toolContext: fakeCatalogCtx({ name: 'Spaceship', paths: ['catalog-imports/ship.png'] }),
            confirmCatalogImport: async () => false,
            getCommandNames: async () => ['print'],
        });
        const evs = collect(agent);
        await agent.send('make a spaceship sprite');

        const toolNames = evs.filter((e): e is Extract<AgentEvent, { kind: 'tool_call_start' }> => e.kind === 'tool_call_start').map(e => e.name);
        expect(toolNames).toContain('search_catalog');
        expect(toolNames).not.toContain('import_catalog_asset');
    });

    it('write_code (monogame): uses an EXISTING project asset file, skips the catalog', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),            mockTurn.text('texture 1, "hero"\nsprite 1, 0, 0, 1'),
        ]);
        const ctx = {
            workspace: memWorkspace({ 'main.fbasic': '', 'hero.png': 'x' }),
            catalog: { search: async () => { throw new Error('should not search'); }, import: async () => ({ name: '', paths: [] }) },
        } as unknown as ToolContext;
        const agent = new GrammarAgent({
            provider, retriever: null, getProjectType: () => 'monogame',
            tools: createDefaultRegistry(), toolContext: ctx,
            confirmCatalogImport: async () => true,
            getCommandNames: async () => ['texture', 'sprite'],
        });
        const evs = collect(agent);
        await agent.send('show a sprite');

        const toolNames = evs.filter((e): e is Extract<AgentEvent, { kind: 'tool_call_start' }> => e.kind === 'tool_call_start').map(e => e.name);
        expect(toolNames).not.toContain('search_catalog');
        const coderSys = provider.sentMessages.at(-1)?.messages[0].content ?? '';
        expect(coderSys).toContain('"hero"');
    });

    it('write_code (monogame): no asset anywhere → falls back to the built-in pixel texture (id 0) + size sprite', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),            mockTurn.text('sprite 1, 100, 100, 0\nsize sprite 1, 50, 50\nDO\nsync\nLOOP'),
        ]);
        const ctx = {
            workspace: memWorkspace({ 'main.fbasic': '' }),
            catalog: { search: async () => [], import: async () => ({ name: '', paths: [] }) },
        } as unknown as ToolContext;
        const agent = new GrammarAgent({
            provider, retriever: null, getProjectType: () => 'monogame',
            tools: createDefaultRegistry(), toolContext: ctx,
            confirmCatalogImport: async () => true,
            getCommandNames: async () => ['sprite', 'size sprite', 'sync'],
        });
        const evs = collect(agent);
        await agent.send('draw a moving square sprite');

        // The coder was told to use texture id 0 and size the sprite up.
        const coderSys = provider.sentMessages.at(-1)?.messages[0].content ?? '';
        expect(coderSys).toContain('texture id 0');
        expect(coderSys).toContain('size sprite');
        expect(evs.some(e => e.kind === 'reasoning' && /built-in pixel texture/i.test(e.title))).toBe(true);
    });

    it('write_code (monogame): a catalog PACK is never auto-imported (falls back to pixel)', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),            mockTurn.text('sprite 1, 0, 0, 0\nsize sprite 1, 50, 50'),
        ]);
        const ctx = {
            workspace: memWorkspace({ 'main.fbasic': '' }),
            catalog: {
                // A pack-only result — must NOT be imported (id 6 bug).
                search: async () => [{ id: 6, name: 'Mega Pack', kind: 'pack', mime: 'application/zip', tags: [], description: null, bytes: 999, license: 'CC0' }],
                import: async () => { throw new Error('Packs must be imported from the Catalog tab'); },
            },
        } as unknown as ToolContext;
        const agent = new GrammarAgent({
            provider, retriever: null, getProjectType: () => 'monogame',
            tools: createDefaultRegistry(), toolContext: ctx,
            confirmCatalogImport: async () => true,
            getCommandNames: async () => ['sprite', 'size sprite'],
        });
        const evs = collect(agent);
        await agent.send('add a sprite');

        const toolNames = evs.filter((e): e is Extract<AgentEvent, { kind: 'tool_call_start' }> => e.kind === 'tool_call_start').map(e => e.name);
        expect(toolNames).not.toContain('import_catalog_asset');  // pack filtered out
        const coderSys = provider.sentMessages.at(-1)?.messages[0].content ?? '';
        expect(coderSys).toContain('texture id 0');
    });

    it('write_code (monogame): a sprite request rejects a FONT catalog match (wrong category)', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),            mockTurn.text('sprite 1, 0, 0, 0\nsize sprite 1, 50, 50'),
        ]);
        const searchArgs: unknown[] = [];
        const ctx = {
            workspace: memWorkspace({ 'main.fbasic': '' }),
            catalog: {
                search: async (_q: string, opts: unknown) => {
                    searchArgs.push(opts);
                    // Only a font is available — must NOT satisfy a sprite request.
                    return [{ id: 3, name: 'Press Start 2P', kind: 'asset', mime: 'font/ttf', tags: ['font'], description: null, bytes: 100, license: 'OFL' }];
                },
                import: async () => { throw new Error('should not import a font for a sprite'); },
            },
        } as unknown as ToolContext;
        const agent = new GrammarAgent({
            provider, retriever: null, getProjectType: () => 'monogame',
            tools: createDefaultRegistry(), toolContext: ctx,
            confirmCatalogImport: async () => true,
            getCommandNames: async () => ['sprite', 'size sprite'],
        });
        const evs = collect(agent);
        await agent.send('write me a simple demo using sprites and arrow keys');

        // It searched for an IMAGE, and never imported the font → pixel fallback.
        expect(searchArgs.some(o => (o as { category?: string })?.category === 'image')).toBe(true);
        const toolNames = evs.filter((e): e is Extract<AgentEvent, { kind: 'tool_call_start' }> => e.kind === 'tool_call_start').map(e => e.name);
        expect(toolNames).not.toContain('import_catalog_asset');
        const coderSys = provider.sentMessages.at(-1)?.messages[0].content ?? '';
        expect(coderSys).toContain('texture id 0');
    });

    it('write_code (monogame): catalog has only PACKS → surfaces them as a suggestion, code uses pixel', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),            mockTurn.text('sprite 1, 0, 0, 0\nsize sprite 1, 50, 50'),
        ]);
        // Mirrors the real catalog: image content lives in packs, not single assets.
        const ctx = {
            workspace: memWorkspace({ 'main.fbasic': '' }),
            catalog: {
                search: async (_q: string, opts: { kind?: string } = {}) => {
                    const packs = [
                        { id: 20, name: 'Pixel Shmup', kind: 'pack', mime: 'application/zip', tags: ['pixel', 'shmup', 'sprites'], description: null, bytes: 1000, license: 'CC0' },
                        { id: 21, name: 'Tiny Dungeon', kind: 'pack', mime: 'application/zip', tags: ['pixel', 'dungeon', 'tiles'], description: null, bytes: 1000, license: 'CC0' },
                    ];
                    // kind:'asset' search → nothing (all content is packs).
                    return opts.kind === 'asset' ? [] : packs;
                },
                import: async () => { throw new Error('Packs must be imported from the Catalog tab'); },
            },
        } as unknown as ToolContext;
        const agent = new GrammarAgent({
            provider, retriever: null, getProjectType: () => 'monogame',
            tools: createDefaultRegistry(), toolContext: ctx,
            confirmCatalogImport: async () => true,
            getCommandNames: async () => ['sprite', 'size sprite'],
        });
        const evs = collect(agent);
        await agent.send('write me a simple demo using sprites and arrow keys');

        // No import attempted (packs can't be auto-imported), packs surfaced, pixel fallback.
        const toolNames = evs.filter((e): e is Extract<AgentEvent, { kind: 'tool_call_start' }> => e.kind === 'tool_call_start').map(e => e.name);
        expect(toolNames).not.toContain('import_catalog_asset');
        expect(evs.some(e => e.kind === 'reasoning' && /pack\(s\)/i.test(e.title) && /Pixel Shmup|Tiny Dungeon/.test(e.detail ?? ''))).toBe(true);
        const coderSys = provider.sentMessages.at(-1)?.messages[0].content ?? '';
        expect(coderSys).toContain('texture id 0');
    });

    it('write_code: non-asset request never touches the catalog', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),            mockTurn.text('x = 1\nprint x'),
        ]);
        const agent = new GrammarAgent({
            provider,
            retriever: null,
            getProjectType: () => 'monogame',
            tools: createDefaultRegistry(),
            toolContext: fakeCatalogCtx({ name: 'x', paths: [] }),
            confirmCatalogImport: async () => true,
            getCommandNames: async () => ['print'],
        });
        const evs = collect(agent);
        await agent.send('print the number one');

        const toolNames = evs.filter((e): e is Extract<AgentEvent, { kind: 'tool_call_start' }> => e.kind === 'tool_call_start').map(e => e.name);
        expect(toolNames).not.toContain('search_catalog');
    });

    it('write_code: review catches assigning to a command result and re-emits a fix', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),            mockTurn.text('sprite 1, 0, 0, 0\nDO\nsprite x(1) = sprite x(1) + 1\nsync\nLOOP'),   // invalid
            mockTurn.text('x = 0\nsprite 1, 0, 0, 0\nDO\nx = x + 1\nposition sprite 1, x, 0\nsync\nLOOP'), // fixed
        ]);
        const agent = new GrammarAgent({
            provider,
            retriever: null,
            getCommandNames: async () => ['sprite', 'sprite x', 'position sprite', 'sync'],
        });
        const evs = collect(agent);
        await agent.send('move a sprite right');

        expect(evs.some(e => e.kind === 'reasoning' && /found \d+ issue/i.test(e.title))).toBe(true);
        const last = agent.getHistory().at(-1)?.content ?? '';
        expect(last).toContain('position sprite');
        expect(last).not.toContain('sprite x(1) =');
    });

    it('follow-up: a modify request reuses the PREVIOUS code as the base (history-aware iterate)', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),   // classifier says write_code…
            mockTurn.text('x = 0\nsprite 1, 100, 100, 0\nDO\nx = x + 2\nposition sprite 1, x, 100\nsync\nLOOP'),
        ]);
        const agent = new GrammarAgent({
            provider, retriever: null,
            getCommandNames: async () => ['sprite', 'position sprite', 'sync'],
        });
        // Pretend the assistant already produced a sprite demo last turn.
        agent.setHistory([
            { role: 'user', content: 'write a simple sprite demo' },
            { role: 'assistant', content: '```fade\nsprite 1, 100, 100, 0\nDO\nsync\nLOOP\n```' },
        ]);
        const evs = collect(agent);
        await agent.send('make it faster with real velocity and bounce off the walls');

        // …but with prior code present, a modify request is rerouted to edit.
        expect(evs.some(e => e.kind === 'reasoning' && /Approach — edit_code/.test(e.title))).toBe(true);
        // The emit prompt MODIFIES the previous program (carries it as the base).
        const coderSys = provider.sentMessages.at(-1)?.messages[0].content ?? '';
        expect(coderSys).toContain('MODIFYING');
        expect(coderSys).toContain('sprite 1, 100, 100, 0');     // the prior code is the base
        expect(coderSys).toContain('Recent conversation');        // history threaded in
        // Final answer builds on it (movement added).
        expect(agent.getHistory().at(-1)?.content).toContain('position sprite');
    });

    it('follow-up: a brand-new "write me a …" request is NOT treated as an edit', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),            mockTurn.text('print "pong"'),
        ]);
        const agent = new GrammarAgent({ provider, retriever: null, getCommandNames: async () => ['print'] });
        agent.setHistory([
            { role: 'user', content: 'write a sprite demo' },
            { role: 'assistant', content: '```fade\nsprite 1, 0, 0, 0\n```' },
        ]);
        const evs = collect(agent);
        await agent.send('write me a new pong game');

        expect(evs.some(e => e.kind === 'reasoning' && /Approach — write_code/.test(e.title))).toBe(true);
        const coderSys = provider.sentMessages.at(-1)?.messages[0].content ?? '';
        expect(coderSys).not.toContain('MODIFYING');
        expect(coderSys).not.toContain('sprite 1, 0, 0, 0');   // did NOT drag in the old code
    });

    it('write_code: runs a data-model design step and injects the plan (array/UDT) into the coder', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),                                              // classify
            mockTurn.text('Use a UDT `Enemy` with x#, y#; store them in `dim enemies(10) as Enemy` and loop with FOR.'), // design
            mockTurn.text('type Enemy\nx#\ny#\nendtype\ndim enemies(10) as Enemy'),    // emit
        ]);
        // Turn order: classify → planDataModel (the design turn) → emit.
        const agent = new GrammarAgent({
            provider, retriever: null,
            getCommandNames: async () => ['sprite', 'sync'],
        });
        const evs = collect(agent);
        await agent.send('spawn 10 enemies that chase the player');

        // The design node ran and is visible.
        expect(evs.some(e => e.kind === 'reasoning' && /data model/i.test(e.title))).toBe(true);
        // The coder prompt carries the planned approach.
        const coderSys = provider.sentMessages.at(-1)?.messages[0].content ?? '';
        expect(coderSys).toContain('PLANNED APPROACH');
        expect(coderSys).toContain('dim enemies(10) as Enemy');
    });

    it('write_code: a model that emits its OWN ``` fence does not produce doubled/empty code blocks', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),
            // Model ignores "no fences" and wraps its output — the classic cause
            // of "```fade at top, stray empty block at the end".
            mockTurn.text('```fade\nsprite 1, 0, 0, 0\nsync\n```'),
        ]);
        const agent = new GrammarAgent({ provider, retriever: null, getCommandNames: async () => ['sprite', 'sync'] });
        const evs = collect(agent);
        await agent.send('show a sprite');

        const streamed = evs.filter((e): e is Extract<AgentEvent, { kind: 'text_delta' }> => e.kind === 'text_delta')
            .map(e => e.delta).join('');
        // Exactly one opening and one closing fence — the model's own fences were stripped.
        expect((streamed.match(/```/g) ?? []).length).toBe(2);
        expect(streamed).toContain('sprite 1, 0, 0, 0');
        // No empty trailing block (``` immediately followed by ```).
        expect(/```fade\s*```/.test(streamed)).toBe(false);
        // History stores clean, unfenced-then-refenced code.
        expect(agent.getHistory().at(-1)?.content).toBe('```fade\nsprite 1, 0, 0, 0\nsync\n```');
    });

    it('write_code: truncated output (token ceiling) does NOT spin the fix loop', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),
            // Emit hits the token limit mid-program → finishReason 'length'.
            mockTurn.text('sprite 1, 0, 0, 0\nposition sprite 1, abs', 'length'),
        ]);
        const agent = new GrammarAgent({
            provider, retriever: null,
            getCommandNames: async () => ['sprite', 'position sprite', 'sync'],
        });
        const evs = collect(agent);
        await agent.send('move a sprite');

        // It surfaces the truncation and stops — no repeated "fixing (pass N)" churn.
        expect(evs.some(e => e.kind === 'reasoning' && /cut off at the token limit/i.test(e.title))).toBe(true);
        expect(evs.filter(e => e.kind === 'reasoning' && /found \d+ issue/i.test((e as { title: string }).title)).length).toBe(0);
        // Only classify + the single (truncated) emit ran — no re-emit attempts.
        expect(provider.sentMessages).toHaveLength(2);
    });

    it('write_code: a SIMPLE request skips the design step (no extra model call)', async () => {
        const provider = new MockProvider([
            mockTurn.text('write_code'),               // classify
            mockTurn.text('sprite 1, 100, 100, 0'),    // emit (no design turn in between)
        ]);
        const agent = new GrammarAgent({
            provider, retriever: null,
            getCommandNames: async () => ['sprite'],
        });
        const evs = collect(agent);
        await agent.send('show a single sprite on screen');

        // No design reasoning, and only the two calls (classify + emit) happened.
        expect(evs.some(e => e.kind === 'reasoning' && /data model/i.test(e.title))).toBe(false);
        expect(provider.sentMessages).toHaveLength(2);
        expect(agent.getHistory().at(-1)?.content).toContain('sprite 1, 100, 100, 0');
    });

    it('explain: retrieves docs (two-hop) and answers FROM them, no grammar', async () => {
        const provider = new MockProvider([
            mockTurn.text('explain'),                                  // classify
            mockTurn.text('`defer` runs a statement at scope exit.'),  // answer
        ]);
        const retriever = {
            search: async () => [{
                chunk: { id: 'd1', source: 'Language.md', heading: 'Defer', text: 'defer <stmt> runs at scope exit', chars: 31 },
                score: 0.9,
            }],
        } as unknown as Retriever;
        const agent = new GrammarAgent({ provider, retriever });
        const evs = collect(agent);
        await agent.send('explain the defer statement');

        // It looked up docs (visible) and fed them into the answer node.
        expect(evs.some(e => e.kind === 'docs_retrieved')).toBe(true);
        expect(evs.some(e => e.kind === 'plan_emitted')).toBe(true);
        const answerSys = provider.sentMessages[1].messages[0].content;
        expect(answerSys).toContain('defer <stmt> runs at scope exit');  // docs in the prompt
    });

    it('routes a non-code question to a prose node', async () => {
        const provider = new MockProvider([
            mockTurn.text('explain'),                                 // classify
            mockTurn.text('The `defer` statement runs at scope exit.'), // prose answer
        ]);
        const agent = new GrammarAgent({ provider, retriever: null });
        const evs = collect(agent);
        await agent.send('what does the defer statement do?');

        // No research/plan for a prose answer.
        expect(evs.some(e => e.kind === 'plan_emitted')).toBe(false);
        expect(agent.getHistory().at(-1)?.content).toContain('defer');
    });
});
