import { describe, it, expect } from 'vitest';
import { Agent, type AgentEvent } from './agent';
import { MockProvider, mockTurn } from './providers/mock';
import { createDefaultRegistry } from './tools/default-registry';
import { Retriever } from './rag/retrieval';
import { Embedder } from './rag/embedder';
import type { Chunk, DocIndex } from './rag/types';
import { ContextEvictor } from './context';
import type { ToolWorkspace } from './tools';

class InMemoryWorkspace implements ToolWorkspace {
    private files: Map<string, string>;
    private project: string;

    constructor(files: Record<string, string> = {}, project = 'test') {
        this.files = new Map(Object.entries(files));
        this.project = project;
    }

    async list(): Promise<string[]> { return [...this.files.keys()].sort(); }
    async read(name: string): Promise<string> {
        if (!this.files.has(name)) throw new Error(`not found: ${name}`);
        return this.files.get(name)!;
    }
    async write(name: string, content: string): Promise<void> { this.files.set(name, content); }
    currentProject(): string { return this.project; }
    snapshot(): Record<string, string> { return Object.fromEntries(this.files); }
}

function collectEvents(agent: Agent): { events: AgentEvent[]; until: () => Promise<void> } {
    const events: AgentEvent[] = [];
    let resolveDone: (() => void) | null = null;
    const done = new Promise<void>((r) => { resolveDone = r; });
    agent.on((e) => {
        events.push(e);
        if (e.kind === 'turn_complete') resolveDone?.();
    });
    return { events, until: () => done };
}

describe('Agent (text-only response)', () => {
    it('streams a plain reply with no tool calls', async () => {
        const provider = new MockProvider([
            mockTurn.text('Hello back!'),
        ]);
        const workspace = new InMemoryWorkspace();
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
            retriever: null,  // disable RAG for this test
        });

        const collected = collectEvents(agent);
        await agent.send('Hello');
        await collected.until();

        const text = collected.events
            .filter((e): e is Extract<AgentEvent, { kind: 'text_delta' }> => e.kind === 'text_delta')
            .map(e => e.delta)
            .join('');
        expect(text).toBe('Hello back!');

        const toolStarts = collected.events.filter(e => e.kind === 'tool_call_start');
        expect(toolStarts).toHaveLength(0);

        expect(collected.events.at(-1)).toMatchObject({ kind: 'turn_complete', finishReason: 'stop' });
    });
});

describe('Agent (tool calling via in-prompt protocol)', () => {
    it('calls list_files and replies with the result', async () => {
        // Turn 1: model emits a tool_call inline via text
        // Turn 2: model emits the final answer
        const provider = new MockProvider([
            mockTurn.streaming([
                'Let me check.\n',
                '<tool_call>\n{"name":"list_files","args":{}}\n</tool_call>',
            ]),
            mockTurn.text('Found 2 files: main.fade, lib.fade.'),
        ]);

        const workspace = new InMemoryWorkspace({
            'main.fade': 'print "hi"',
            'lib.fade': 'function add(a, b) end function',
        });
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
        });

        const collected = collectEvents(agent);
        await agent.send('What files are here?');
        await collected.until();

        const toolStarts = collected.events.filter((e): e is Extract<AgentEvent, { kind: 'tool_call_start' }> => e.kind === 'tool_call_start');
        expect(toolStarts).toHaveLength(1);
        expect(toolStarts[0].name).toBe('list_files');

        const toolResults = collected.events.filter((e): e is Extract<AgentEvent, { kind: 'tool_call_result' }> => e.kind === 'tool_call_result');
        expect(toolResults).toHaveLength(1);
        expect(toolResults[0].ok).toBe(true);
        expect((toolResults[0].result as { files: string[] }).files).toEqual(['lib.fade', 'main.fade']);

        // Final text accumulates across both turns
        const text = collected.events
            .filter((e): e is Extract<AgentEvent, { kind: 'text_delta' }> => e.kind === 'text_delta')
            .map(e => e.delta)
            .join('');
        expect(text).toContain('Let me check.');
        expect(text).toContain('Found 2 files');
    });

    it('feeds the tool result back into the second turn prompt', async () => {
        const provider = new MockProvider([
            mockTurn.streaming(['<tool_call>{"name":"read_file","args":{"path":"a.fade"}}</tool_call>']),
            mockTurn.text('It says hello.'),
        ]);
        const workspace = new InMemoryWorkspace({ 'a.fade': 'print "hello"' });
        const agent = new Agent({ provider, tools: createDefaultRegistry(), toolContext: { workspace }, retriever: null });

        const collected = collectEvents(agent);
        await agent.send('what is in a.fade?');
        await collected.until();

        // Provider should have received two stream calls; the second's
        // messages must include a tool_result for the read_file call.
        expect(provider.sentMessages).toHaveLength(2);
        const secondTurnMsgs = provider.sentMessages[1].messages;
        const hasToolResult = secondTurnMsgs.some(m =>
            m.role === 'user' && m.content.includes('<tool_result name="read_file">'),
        );
        expect(hasToolResult).toBe(true);
    });

    it('surfaces validation errors when args are wrong', async () => {
        const provider = new MockProvider([
            mockTurn.streaming(['<tool_call>{"name":"read_file","args":{}}</tool_call>']),
            mockTurn.text('ok, giving up.'),
        ]);
        const workspace = new InMemoryWorkspace({ 'a.fade': '' });
        const agent = new Agent({ provider, tools: createDefaultRegistry(), toolContext: { workspace }, retriever: null });

        const collected = collectEvents(agent);
        await agent.send('read a.fade');
        await collected.until();

        const result = collected.events
            .find((e): e is Extract<AgentEvent, { kind: 'tool_call_result' }> => e.kind === 'tool_call_result');
        expect(result?.ok).toBe(false);
        expect((result?.result as { error: string }).error).toBe('Invalid arguments');
    });

    it('retries after malformed tool_call JSON and eventually runs the tool', async () => {
        const provider = new MockProvider([
            mockTurn.streaming(['<tool_call>{not json}</tool_call>']),
            mockTurn.streaming(['<tool_call>{"name":"list_files","args":{}}</tool_call>']),
            mockTurn.text('Two files here.'),
        ]);
        const workspace = new InMemoryWorkspace({ 'a.fade': '', 'b.fade': '' });
        const agent = new Agent({ provider, tools: createDefaultRegistry(), toolContext: { workspace }, retriever: null });

        const collected = collectEvents(agent);
        await agent.send('what files?');
        await collected.until();

        // Bad JSON → nudge → good tool_call → answer = 3 provider calls.
        expect(provider.sentMessages).toHaveLength(3);
        const retryNudge = provider.sentMessages[1].messages.find(m =>
            m.role === 'user' && m.content.includes('Your tool_call was invalid'),
        );
        expect(retryNudge).toBeDefined();

        const toolStarts = collected.events.filter(e => e.kind === 'tool_call_start');
        expect(toolStarts).toHaveLength(1);
        expect(toolStarts[0]).toMatchObject({ name: 'list_files' });
    });
});

describe('Agent (plan phase)', () => {
    it('emits a plan_emitted event when the model leads with a plan', async () => {
        const provider = new MockProvider([
            mockTurn.streaming([
                '<plan>{"goal":"list files","steps":[{"tool":"list_files","description":"see what is here"}]}</plan>\n',
                '<tool_call>{"name":"list_files","args":{}}</tool_call>',
            ]),
            mockTurn.text('Found nothing.'),
        ]);
        const workspace = new InMemoryWorkspace();
        const agent = new Agent({ provider, tools: createDefaultRegistry(), toolContext: { workspace }, retriever: null });

        const collected = collectEvents(agent);
        await agent.send('what files?');
        await collected.until();

        const plans = collected.events.filter((e): e is Extract<AgentEvent, { kind: 'plan_emitted' }> => e.kind === 'plan_emitted');
        expect(plans).toHaveLength(1);
        expect(plans[0].plan.goal).toBe('list files');
        expect(plans[0].plan.steps).toEqual([{ tool: 'list_files', description: 'see what is here' }]);

        // Plan should not leak into the text stream
        const text = collected.events
            .filter((e): e is Extract<AgentEvent, { kind: 'text_delta' }> => e.kind === 'text_delta')
            .map(e => e.delta).join('');
        expect(text).not.toContain('<plan>');
        expect(text).not.toContain('goal');
    });
});

describe('Agent (parallel read-only tools)', () => {
    it('runs read_file calls in parallel when emitted together', async () => {
        let active = 0;
        let peakActive = 0;
        const slowWorkspace: import('./tools').ToolWorkspace = {
            async list() { return ['a.fade', 'b.fade']; },
            async read(_name) {
                active++;
                peakActive = Math.max(peakActive, active);
                await new Promise(r => setTimeout(r, 20));
                active--;
                return 'content';
            },
            async write() {},
            currentProject() { return 'p'; },
        };
        const provider = new MockProvider([
            mockTurn.streaming([
                '<tool_call>{"name":"read_file","args":{"path":"a.fade"}}</tool_call>',
                '<tool_call>{"name":"read_file","args":{"path":"b.fade"}}</tool_call>',
            ]),
            mockTurn.text('done'),
        ]);
        const agent = new Agent({ provider, tools: createDefaultRegistry(), toolContext: { workspace: slowWorkspace }, retriever: null });

        const collected = collectEvents(agent);
        await agent.send('read both');
        await collected.until();

        // Both read_files should have been in flight at the same time.
        expect(peakActive).toBe(2);
    });
});

describe('Agent (budget warning)', () => {
    it('emits budget_warning when context usage crosses the threshold', async () => {
        const provider = new MockProvider([mockTurn.text('ok')], {
            capabilities: { maxContext: 100, supportsTools: false, isCached: true },
        });
        const workspace = new InMemoryWorkspace();
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
            budgetWarnAt: 0.5,
            retriever: null,
            evictor: null,  // measure the prompt as-built; don't auto-trim
        });

        const collected = collectEvents(agent);
        // The system prompt alone is well over 100 tokens, so this trips
        await agent.send('hi');
        await collected.until();

        const warnings = collected.events.filter(e => e.kind === 'budget_warning');
        expect(warnings.length).toBeGreaterThanOrEqual(1);
    });
});

describe('Agent (eviction integration)', () => {
    it('emits an eviction event when history pushes past the threshold', async () => {
        // Pre-load a lot of history, then send a fresh user message.
        // The next iteration's runEviction() should fire and elide the
        // oldest tool_result body.
        const provider = new MockProvider([mockTurn.text('ok')], {
            capabilities: { maxContext: 250, supportsTools: false, isCached: true },
        });
        const workspace = new InMemoryWorkspace();

        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
            retriever: null,
            evictor: new ContextEvictor({
                provider,
                evictAt: 0.5,
                evictTo: 0.3,
                keepRecentMessages: 2,
                enableSummarization: false,
            }),
        });
        // Seed history with a big tool result older than the keep window
        const big = 'X'.repeat(800);
        agent.setHistory([
            { role: 'user', content: 'old question' },
            { role: 'assistant', content: 'looking…' },
            { role: 'user', content: `<tool_result name="read_file">${big}</tool_result>` },
            { role: 'assistant', content: 'older answer' },
        ]);

        const collected = collectEvents(agent);
        await agent.send('current question');
        await collected.until();

        const eviction = collected.events.find((e): e is Extract<AgentEvent, { kind: 'eviction' }> => e.kind === 'eviction');
        expect(eviction).toBeDefined();
        expect(eviction!.result.elided + eviction!.result.summarized + eviction!.result.dropped).toBeGreaterThan(0);
        expect(eviction!.tokensAfter).toBeLessThan(eviction!.tokensBefore);
    });
});

describe('Agent (plan-only continuation)', () => {
    it('nudges the model when it emits a plan but no text or tool call', async () => {
        // Turn 1: plan only. Turn 2: actual answer in response to nudge.
        const provider = new MockProvider([
            mockTurn.streaming([
                '<plan>{"goal":"explain","steps":[{"description":"answer"}]}</plan>',
            ]),
            mockTurn.text('Here is the explanation.'),
        ]);
        const workspace = new InMemoryWorkspace({ 'main.fbasic': 'print "hi"' });
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
            retriever: null,
            evictor: null,
        });

        const collected = collectEvents(agent);
        await agent.send('explain it');
        await collected.until();

        // Two stream calls — the second triggered by the nudge
        expect(provider.sentMessages).toHaveLength(2);
        // The second turn's history must include the nudge as a user message
        const secondTurnMsgs = provider.sentMessages[1].messages;
        const nudge = secondTurnMsgs.find(m => m.role === 'user' && m.content.includes('Continue.'));
        expect(nudge).toBeDefined();

        // The final answer is in the chat text stream
        const text = collected.events
            .filter((e): e is Extract<AgentEvent, { kind: 'text_delta' }> => e.kind === 'text_delta')
            .map(e => e.delta).join('');
        expect(text).toBe('Here is the explanation.');
    });

    it('caps the continuation at one attempt per turn', async () => {
        // Two plan-only turns in a row — the agent should give up after the first nudge.
        const provider = new MockProvider([
            mockTurn.streaming(['<plan>{"goal":"a","steps":[]}</plan>']),
            mockTurn.streaming(['<plan>{"goal":"b","steps":[]}</plan>']),
        ]);
        const workspace = new InMemoryWorkspace();
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
            retriever: null,
            evictor: null,
        });

        const collected = collectEvents(agent);
        await agent.send('hi');
        await collected.until();

        // Exactly two attempts — the third would throw "MockProvider exhausted"
        expect(provider.sentMessages).toHaveLength(2);
        expect(collected.events.at(-1)).toMatchObject({ kind: 'turn_complete' });
    });
});

describe('Agent (empty turn detection)', () => {
    it('emits an error event when the model produces nothing', async () => {
        const provider = new MockProvider([mockTurn.text('')]);
        const workspace = new InMemoryWorkspace();
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
            retriever: null,
            evictor: null,
        });

        const collected = collectEvents(agent);
        await agent.send('hi');
        await collected.until();

        const errors = collected.events.filter((e): e is Extract<AgentEvent, { kind: 'error' }> => e.kind === 'error');
        expect(errors).toHaveLength(1);
        expect(errors[0].message).toContain('no output');
    });
});

describe('Agent (workspace context auto-injection)', () => {
    it('injects project name + file list into the system prompt', async () => {
        const provider = new MockProvider([mockTurn.text('ok')]);
        const workspace = new InMemoryWorkspace({
            'main.fade': '...', 'lib.fade': '...', 'fade.json': '{}',
        }, 'demo-project');

        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
            retriever: null,
            evictor: null,
        });

        const collected = collectEvents(agent);
        await agent.send('hello');
        await collected.until();

        const sysMsg = provider.sentMessages[0].messages.find(m => m.role === 'system');
        expect(sysMsg).toBeDefined();
        expect(sysMsg!.content).toContain('Workspace state:');
        expect(sysMsg!.content).toContain('Project: demo-project');
        expect(sysMsg!.content).toContain('Files (3):');
        expect(sysMsg!.content).toContain('main.fade');
        expect(sysMsg!.content).toContain('lib.fade');
    });

    it('marks an empty workspace explicitly', async () => {
        const provider = new MockProvider([mockTurn.text('ok')]);
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace: new InMemoryWorkspace() },
            retriever: null,
            evictor: null,
        });
        const collected = collectEvents(agent);
        await agent.send('hi');
        await collected.until();
        const sysMsg = provider.sentMessages[0].messages.find(m => m.role === 'system');
        expect(sysMsg!.content).toContain('Files: (empty)');
    });

    it('injects MonoGame runtime rules only for monogame projects', async () => {
        const mk = async (type: 'web' | 'monogame') => {
            const provider = new MockProvider([mockTurn.text('ok')]);
            const agent = new Agent({
                provider,
                tools: createDefaultRegistry(),
                toolContext: { workspace: new InMemoryWorkspace() },
                getProjectType: () => type,
                retriever: null,
                evictor: null,
            });
            const collected = collectEvents(agent);
            await agent.send('make a sprite move with the arrow keys');
            await collected.until();
            return provider.sentMessages[0].messages.find(m => m.role === 'system')!.content;
        };
        expect(await mk('monogame')).toContain('MONOGAME RUNTIME RULES');
        expect(await mk('web')).not.toContain('MONOGAME RUNTIME RULES');
    });

    it('truncates large file lists in workspace context', async () => {
        const provider = new MockProvider([mockTurn.text('ok')]);
        const files: Record<string, string> = {};
        for (let i = 0; i < 50; i++) files[`f${i}.fade`] = 'x';
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace: new InMemoryWorkspace(files) },
            retriever: null,
            evictor: null,
        });
        const collected = collectEvents(agent);
        await agent.send('hi');
        await collected.until();
        const sysMsg = provider.sentMessages[0].messages.find(m => m.role === 'system');
        expect(sysMsg!.content).toContain('Files (50, first 25):');
        expect(sysMsg!.content).toContain('…');
    });

    it('includes editor focus when an editor adapter is wired', async () => {
        const provider = new MockProvider([mockTurn.text('ok')]);
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: {
                workspace: new InMemoryWorkspace({ 'main.fade': '...' }),
                editor: {
                    activeFile: () => 'main.fade',
                    cursorLine: () => 14,           // 0-indexed
                    selectionText: () => '',
                },
            },
            retriever: null,
            evictor: null,
        });
        const collected = collectEvents(agent);
        await agent.send('hi');
        await collected.until();
        const sysMsg = provider.sentMessages[0].messages.find(m => m.role === 'system');
        // Cursor line displayed 1-indexed
        expect(sysMsg!.content).toContain('Open file: main.fade (cursor at line 15)');
    });

    it('includes a diagnostics summary when an adapter is wired', async () => {
        const provider = new MockProvider([mockTurn.text('ok')]);
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: {
                workspace: new InMemoryWorkspace({ 'main.fade': '...' }),
                diagnostics: {
                    async getAll() {
                        return [
                            { path: 'main.fade', severity: 'error',   line: 1, column: 1, endLine: 1, endColumn: 2, message: 'oops' },
                            { path: 'main.fade', severity: 'warning', line: 2, column: 1, endLine: 2, endColumn: 2, message: 'hm' },
                            { path: 'main.fade', severity: 'warning', line: 3, column: 1, endLine: 3, endColumn: 2, message: 'hm' },
                        ];
                    },
                    async forFile() { return []; },
                },
            },
            retriever: null,
            evictor: null,
        });
        const collected = collectEvents(agent);
        await agent.send('hi');
        await collected.until();
        const sysMsg = provider.sentMessages[0].messages.find(m => m.role === 'system');
        expect(sysMsg!.content).toContain('Diagnostics: 1 error(s), 2 warning(s)');
    });

    it('reports diagnostics as "clean" when no markers are present', async () => {
        const provider = new MockProvider([mockTurn.text('ok')]);
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: {
                workspace: new InMemoryWorkspace({ 'main.fade': '...' }),
                diagnostics: {
                    async getAll() { return []; },
                    async forFile() { return []; },
                },
            },
            retriever: null,
            evictor: null,
        });
        const collected = collectEvents(agent);
        await agent.send('hi');
        await collected.until();
        const sysMsg = provider.sentMessages[0].messages.find(m => m.role === 'system');
        expect(sysMsg!.content).toContain('Diagnostics: clean');
    });
});

describe('Agent (auto-retrieval)', () => {
    // Build a tiny in-memory index with a fake embedder so we exercise the
    // wiring without downloading bge-small.

    function makeMockRetriever(chunks: Chunk[]): Retriever {
        const index: DocIndex = {
            version: 1, model: 'mock', dim: 3, builtAt: 'now',
            sourceCount: 1, chunks,
        };
        // Hand-build a fake Embedder that returns a fixed query vector.
        // We override `embedQuery` directly via a duck-typed object.
        const fakeEmbedder = {
            async ensureReady() {},
            async embedQuery() {
                return new Float32Array([1, 0, 0]);  // matches chunk A
            },
            async embedPassage() {
                return new Float32Array([1, 0, 0]);
            },
        };
        return new Retriever({
            embedder: fakeEmbedder as unknown as Embedder,
            index,
        });
    }

    function normalize(values: number[]): number[] {
        let s = 0;
        for (const v of values) s += v * v;
        const n = Math.sqrt(s) || 1;
        return values.map(v => v / n);
    }

    it('runs retrieval on conceptual turns and emits docs_retrieved', async () => {
        const provider = new MockProvider([mockTurn.text('answer')]);
        const workspace = new InMemoryWorkspace();
        const retriever = makeMockRetriever([
            { id: 'A', source: 'doc.md', heading: 'A', text: 'sprite rotation primer', chars: 22, vector: normalize([1, 0, 0]) },
            { id: 'B', source: 'doc.md', heading: 'B', text: 'file I/O', chars: 8, vector: normalize([0, 1, 0]) },
        ]);

        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
            retriever,
            autoRetrievalK: 1,
        });

        const collected = collectEvents(agent);
        await agent.send('how do sprites rotate?');
        await collected.until();

        const retrieved = collected.events.filter((e): e is Extract<AgentEvent, { kind: 'docs_retrieved' }> => e.kind === 'docs_retrieved');
        expect(retrieved).toHaveLength(1);
        expect(retrieved[0].hits).toHaveLength(1);
        expect(retrieved[0].hits[0].chunk.id).toBe('A');
    });

    it('classifies capabilities and drives a search_docs research phase for code requests', async () => {
        const provider = new MockProvider([
            mockTurn.text('read the arrow keys\ndraw a sprite'),  // 1: capability classification
            mockTurn.text('Here is the code.'),                    // 2: the actual answer
        ]);
        const retriever = makeMockRetriever([
            { id: 'A', source: 'doc.md', heading: 'A', text: 'sprite', chars: 6, vector: normalize([1, 0, 0]) },
        ]);
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace: new InMemoryWorkspace() },
            retriever,
            autoRetrievalK: 1,
        });
        const collected = collectEvents(agent);
        await agent.send('write me a sprite demo with arrow keys');
        await collected.until();

        // The AI-classified plan drove a visible research step per capability.
        const plan = collected.events.find(
            (e): e is Extract<AgentEvent, { kind: 'plan_emitted' }> => e.kind === 'plan_emitted');
        expect(plan?.plan.steps.map(s => s.description)).toEqual(['read the arrow keys', 'draw a sprite']);
        expect(plan?.plan.steps.every(s => s.tool === 'search_docs')).toBe(true);
        const searches = collected.events.filter(
            e => e.kind === 'tool_call_start' && e.name === 'search_docs');
        expect(searches).toHaveLength(2);
    });

    it('resolves "fix the code you showed" to the prior reply, not a file', async () => {
        const provider = new MockProvider([mockTurn.text('Updated code coming up.')]);
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace: new InMemoryWorkspace({ 'main.fbasic': 'print "unrelated"' }) },
            retriever: null,
        });
        // Seed a prior assistant reply that SHOWED code (never written to a file).
        agent.setHistory([
            { role: 'user', content: 'show me a loop' },
            { role: 'assistant', content: 'Here:\n```fade\nfor i = 1 to 3\n  print i\nnext\n```' },
        ]);
        const collected = collectEvents(agent);
        await agent.send('there is a bug in that code, can you fix it?');
        await collected.until();

        // It pinned the prior snippet into context and did NOT read main.fbasic.
        const reads = collected.events.filter(e => e.kind === 'tool_call_start' && e.name === 'read_file');
        expect(reads).toHaveLength(0);
        const ctx = agent.getHistory().find(m =>
            m.role === 'user' && m.content.includes('NOT saved in main.fbasic'));
        expect(ctx?.content).toContain('for i = 1 to 3');
    });

    it('routes a debug request: reads the named file and fetches diagnostics', async () => {
        const provider = new MockProvider([
            mockTurn.text('INTENT: debug\nFILES: main.fbasic\nCAPABILITIES: none'),  // router
            mockTurn.text('Fixed it.'),                                               // answer
        ]);
        const retriever = makeMockRetriever([
            { id: 'A', source: 'doc.md', heading: 'A', text: 'x', chars: 1, vector: normalize([1, 0, 0]) },
        ]);
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace: new InMemoryWorkspace({ 'main.fbasic': 'print x' }) },
            retriever,
            autoRetrievalK: 1,
        });
        const collected = collectEvents(agent);
        await agent.send('fix the crash in my code');
        await collected.until();

        const plan = collected.events.find(
            (e): e is Extract<AgentEvent, { kind: 'plan_emitted' }> => e.kind === 'plan_emitted');
        expect(plan?.plan.steps.some(s => s.tool === 'read_file' && s.description === 'main.fbasic')).toBe(true);
        const reads = collected.events.filter(e => e.kind === 'tool_call_start' && e.name === 'read_file');
        expect(reads).toHaveLength(1);
        const diags = collected.events.filter(e => e.kind === 'tool_call_start' && e.name === 'get_diagnostics');
        expect(diags).toHaveLength(1);
    });

    it('injects retrieved chunks into the system prompt', async () => {
        const provider = new MockProvider([mockTurn.text('answer')]);
        const workspace = new InMemoryWorkspace();
        const retriever = makeMockRetriever([
            { id: 'A', source: 'doc.md', heading: 'Sprites', text: 'distinctive_doc_string_xyz', chars: 26, vector: normalize([1, 0, 0]) },
        ]);

        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
            retriever,
            autoRetrievalK: 1,
        });

        const collected = collectEvents(agent);
        await agent.send('how do I declare a function in Fade?');
        await collected.until();

        const sysMsg = provider.sentMessages[0].messages.find(m => m.role === 'system');
        expect(sysMsg).toBeDefined();
        expect(sysMsg!.content).toContain('Relevant docs:');
        expect(sysMsg!.content).toContain('distinctive_doc_string_xyz');
    });

    it('gracefully proceeds when retrieval throws', async () => {
        const provider = new MockProvider([mockTurn.text('still works')]);
        const workspace = new InMemoryWorkspace();
        const brokenRetriever = {
            async search() { throw new Error('boom'); },
            async warm() {},
            async getIndex() { return null; },
        } as unknown as Retriever;

        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
            retriever: brokenRetriever,
        });

        const collected = collectEvents(agent);
        await agent.send('hi');
        await collected.until();

        expect(collected.events.at(-1)).toMatchObject({ kind: 'turn_complete', finishReason: 'stop' });
        const docsEvents = collected.events.filter(e => e.kind === 'docs_retrieved');
        expect(docsEvents).toHaveLength(0);
    });

    it('auto-runs list_files for project inventory questions', async () => {
        const provider = new MockProvider([mockTurn.text('main.fbasic only')]);
        const workspace = new InMemoryWorkspace({ 'main.fbasic': 'x', 'fade.json': '{}' });
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
            retriever: null,
        });
        const collected = collectEvents(agent);
        await agent.send('what is in this project');
        await collected.until();

        const toolStarts = collected.events.filter(
            (e): e is Extract<AgentEvent, { kind: 'tool_call_start' }> => e.kind === 'tool_call_start',
        );
        expect(toolStarts.some(e => e.name === 'list_files')).toBe(true);

        const sysMsg = provider.sentMessages[0].messages.find(m => m.role === 'system');
        expect(sysMsg!.content).toContain('list_files was run automatically');

        const toolResult = provider.sentMessages[0].messages.find(
            m => m.role === 'user' && m.content.includes('<tool_result name="list_files">'),
        );
        expect(toolResult).toBeDefined();
        expect(toolResult!.content).toContain('main.fbasic');
    });

    it('skips auto-retrieval for workspace inspection questions', async () => {
        const provider = new MockProvider([mockTurn.text('here are the files')]);
        const workspace = new InMemoryWorkspace();
        const retriever = makeMockRetriever([
            { id: 'A', source: 'doc.md', heading: 'A', text: 'should not appear', chars: 18, vector: normalize([1, 0, 0]) },
        ]);

        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
            retriever,
            autoRetrievalK: 1,
        });

        const collected = collectEvents(agent);
        await agent.send('what files are in this project?');
        await collected.until();

        const docsEvents = collected.events.filter(e => e.kind === 'docs_retrieved');
        expect(docsEvents).toHaveLength(0);
        const sysMsg = provider.sentMessages[0].messages.find(m => m.role === 'system');
        expect(sysMsg!.content).not.toContain('Relevant docs:');
    });
});

describe('Agent (apply_edit)', () => {
    it('replaces a line range and writes the file', async () => {
        const provider = new MockProvider([
            mockTurn.streaming([
                '<tool_call>{"name":"apply_edit","args":{"path":"a.fade","startLine":2,"endLine":2,"newText":"  print \\"world\\""}}</tool_call>',
            ]),
            mockTurn.text('Done.'),
        ]);
        const workspace = new InMemoryWorkspace({
            'a.fade': 'function greet()\n  print "hello"\nend function',
        });
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace, confirmEdit: async () => true },
            retriever: null,
        });

        const collected = collectEvents(agent);
        await agent.send('change hello to world');
        await collected.until();

        const after = workspace.snapshot()['a.fade'];
        expect(after).toBe('function greet()\n  print "world"\nend function');
    });

    it('injects post-edit diagnostics after a successful write so the model can self-heal', async () => {
        // The agent edits `a.fade`. The LSP reports an error on the
        // newly-inserted line. The agent should:
        //   1. Push a <post_edit_diagnostics> block into history.
        //   2. Emit a `post_edit_diagnostics` event so the UI can chip.
        //   3. Loop again so the model sees the diagnostics and can react.
        const provider = new MockProvider([
            mockTurn.streaming([
                '<tool_call>{"name":"apply_edit","args":{"path":"a.fade","startLine":1,"endLine":1,"newText":"prnt \\"hi\\""}}</tool_call>',
            ]),
            // Second turn: model "fixes" the typo it sees in the diagnostics
            mockTurn.streaming([
                '<tool_call>{"name":"apply_edit","args":{"path":"a.fade","startLine":1,"endLine":1,"newText":"print \\"hi\\""}}</tool_call>',
            ]),
            mockTurn.text('Fixed the typo.'),
        ]);
        const workspace = new InMemoryWorkspace({ 'a.fade': 'OLD' });
        // Stub diagnostics: first call returns an error, second is clean.
        let getCalls = 0;
        const diagnostics = {
            async getAll() { return []; },
            async forFile() {
                getCalls++;
                if (getCalls === 1) {
                    return [{
                        path: 'a.fade', severity: 'error' as const,
                        line: 1, column: 1, endLine: 1, endColumn: 4,
                        message: "Unknown command 'prnt'", code: 'FB101',
                    }];
                }
                return [];
            },
        };
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace, confirmEdit: async () => true, diagnostics },
            retriever: null,
        });

        const collected = collectEvents(agent);
        await agent.send('add a print');
        await collected.until();

        const diagEvents = collected.events.filter(e => e.kind === 'post_edit_diagnostics');
        // Two writes happen → two post-edit probes.
        expect(diagEvents.length).toBeGreaterThanOrEqual(2);
        expect(diagEvents[0]).toMatchObject({ path: 'a.fade', errors: 1, clean: false });
        expect(diagEvents.at(-1)).toMatchObject({ path: 'a.fade', errors: 0, clean: true });

        // Final file content should be the corrected line — proves the
        // model used the injected diagnostics to self-heal.
        expect(workspace.snapshot()['a.fade']).toBe('print "hi"');
    });

    it('skips post-edit diagnostics when no DiagnosticsProvider is wired', async () => {
        // No `diagnostics` in toolContext → just skip; no error, no event.
        const provider = new MockProvider([
            mockTurn.streaming([
                '<tool_call>{"name":"apply_edit","args":{"path":"a.fade","startLine":1,"endLine":1,"newText":"X"}}</tool_call>',
            ]),
            mockTurn.text('done'),
        ]);
        const workspace = new InMemoryWorkspace({ 'a.fade': 'OLD' });
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace, confirmEdit: async () => true },
            retriever: null,
        });

        const collected = collectEvents(agent);
        await agent.send('change it');
        await collected.until();

        expect(collected.events.find(e => e.kind === 'post_edit_diagnostics')).toBeUndefined();
        expect(workspace.snapshot()['a.fade']).toBe('X');
    });

    it('does not inject post-edit diagnostics when the user rejects the edit', async () => {
        const provider = new MockProvider([
            mockTurn.streaming([
                '<tool_call>{"name":"apply_edit","args":{"path":"a.fade","startLine":1,"endLine":1,"newText":"X"}}</tool_call>',
            ]),
            mockTurn.text('ok, rejected'),
        ]);
        const workspace = new InMemoryWorkspace({ 'a.fade': 'OLD' });
        let probed = false;
        const diagnostics = {
            async getAll() { return []; },
            async forFile() { probed = true; return []; },
        };
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace, confirmEdit: async () => false, diagnostics },
            retriever: null,
        });

        const collected = collectEvents(agent);
        await agent.send('change it');
        await collected.until();

        // A rejection makes apply_edit return ok=false → no path collected
        // → no probe.
        expect(probed).toBe(false);
        expect(collected.events.find(e => e.kind === 'post_edit_diagnostics')).toBeUndefined();
    });

    it('honors confirmEdit=false by leaving the file untouched', async () => {
        const provider = new MockProvider([
            mockTurn.streaming([
                '<tool_call>{"name":"apply_edit","args":{"path":"a.fade","startLine":1,"endLine":1,"newText":"NEW"}}</tool_call>',
            ]),
            mockTurn.text('ok, no edit.'),
        ]);
        const workspace = new InMemoryWorkspace({ 'a.fade': 'OLD' });
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace, confirmEdit: async () => false },
            retriever: null,
        });

        const collected = collectEvents(agent);
        await agent.send('change it');
        await collected.until();

        expect(workspace.snapshot()['a.fade']).toBe('OLD');
        const result = collected.events
            .find((e): e is Extract<AgentEvent, { kind: 'tool_call_result' }> => e.kind === 'tool_call_result');
        expect(result?.ok).toBe(false);
    });
});

describe('Agent (end-of-turn analysis)', () => {
    const BAD_SNIPPET = 'Try:\n```fade\nx = 0\nif a then\n  x = 1\nelseif b then\n  x = 2\nend if\n```';
    const GOOD_SNIPPET = 'Fixed:\n```fade\nx = 0\nif a then\n  x = 1\nelse\n  if b then\n    x = 2\n  endif\nendif\n```';
    // LSP mock: only the `elseif` form is an error; the corrected form compiles.
    const elseifLsp = async (src: string) =>
        /elseif/i.test(src)
            ? [{ path: 's', severity: 'error' as const, line: 4, column: 1, endLine: 4, endColumn: 7, message: "'elseif' is not valid", code: '0100' }]
            : [];

    it('repairs a shown snippet in an isolated sub-agent and splices it back', async () => {
        const provider = new MockProvider([
            mockTurn.text(BAD_SNIPPET),  // 1: the main answer (invalid)
            mockTurn.text(GOOD_SNIPPET), // 2: the repair sub-agent's corrected answer
        ]);
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace: new InMemoryWorkspace(), lintFadeSnippet: elseifLsp },
            retriever: null,
        });
        const collected = collectEvents(agent);
        await agent.send('how do I branch?');
        await collected.until();

        // The repair ran in an ISOLATED context: its prompt is a fresh
        // system+user pair (not the main conversation), and it emitted a revised
        // answer that the UI swaps in.
        const repairCall = provider.sentMessages[1];
        expect(repairCall.messages).toHaveLength(2);                 // system + user only
        expect(repairCall.messages[1].content).toContain('--- draft answer ---');
        const revised = collected.events.find(
            (e): e is Extract<AgentEvent, { kind: 'answer_revised' }> => e.kind === 'answer_revised');
        expect(revised?.text).toBe(GOOD_SNIPPET);
        // The fixed answer replaced the broken draft in history — no heal churn.
        const lastAssistant = agent.getHistory().filter(m => m.role === 'assistant').at(-1);
        expect(lastAssistant?.content).toBe(GOOD_SNIPPET);
        expect(agent.getHistory().some(m => /--- draft answer ---/.test(m.content))).toBe(false);
        // Final answer is clean → no passive lint shown to the user.
        expect(collected.events.find(e => e.kind === 'code_lint')).toBeUndefined();
    });

    it('surfaces remaining errors after the heal budget is spent', async () => {
        // Model never fixes it — after the heal passes are spent, the leftover
        // errors are surfaced passively so the user isn't misled.
        const provider = new MockProvider([
            mockTurn.text(BAD_SNIPPET), mockTurn.text(BAD_SNIPPET), mockTurn.text(BAD_SNIPPET),
        ]);
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace: new InMemoryWorkspace(), lintFadeSnippet: elseifLsp },
            retriever: null,
        });
        const collected = collectEvents(agent);
        await agent.send('how do I branch?');
        await collected.until();

        const lint = collected.events.find(
            (e): e is Extract<AgentEvent, { kind: 'code_lint' }> => e.kind === 'code_lint');
        expect(lint?.issues[0].message).toContain('elseif');
    });

    it('does NOT flag unknown-symbol on a one-line illustrative snippet', async () => {
        const provider = new MockProvider([mockTurn.text('Use `sprite 1, x, y, 1` like so:\n```fade\nsprite 1, x, y, 1\n```')]);
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: {
                workspace: new InMemoryWorkspace(),
                lintFadeSnippet: async () => [
                    { path: 's', severity: 'error' as const, line: 1, column: 1, endLine: 1, endColumn: 2, message: 'unknown symbol, x', code: '0200' },
                ],
            },
            retriever: null,
        });
        const collected = collectEvents(agent);
        await agent.send('show me sprite');
        await collected.until();
        expect(collected.events.find(e => e.kind === 'code_lint')).toBeUndefined();
    });

    it('reports a missing asset and suggests the catalog', async () => {
        const provider = new MockProvider([
            mockTurn.text('Here:\n```fade\ntexture 1, "Images/Ball"\nsprite 1, 0, 0, 1\n```'),
        ]);
        const workspace = new InMemoryWorkspace({ 'main.fbasic': 'rem', 'fade.json': '{}' });
        const catalogCalls: Array<{ q: string }> = [];
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: {
                workspace,
                catalog: {
                    async search(q) { catalogCalls.push({ q }); return []; },
                    async import() { return { name: 'x', paths: [] }; },
                },
            },
            retriever: null,
        });

        const collected = collectEvents(agent);
        await agent.send('give me a ball sprite');
        await collected.until();

        const report = collected.events.find(
            (e): e is Extract<AgentEvent, { kind: 'asset_report' }> => e.kind === 'asset_report');
        expect(report?.missing.map(m => m.name)).toEqual(['Images/Ball']);

        const suggestion = collected.events.find(
            (e): e is Extract<AgentEvent, { kind: 'suggestion' }> => e.kind === 'suggestion');
        expect(suggestion?.suggestions[0].title).toContain('Images/Ball');
    });

    it('suggests a docs lookup when the answer speculates about a real command', async () => {
        const provider = new MockProvider([
            mockTurn.text('The `sync` keyword is likely a command that flushes pending state.'),
        ]);
        const workspace = new InMemoryWorkspace();
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace },
            retriever: null,
            getCommandNames: async () => ['sync', 'print', 'texture'],
        });

        const collected = collectEvents(agent);
        await agent.send('what does sync do?');
        await collected.until();

        const suggestion = collected.events.find(
            (e): e is Extract<AgentEvent, { kind: 'suggestion' }> => e.kind === 'suggestion');
        expect(suggestion?.suggestions.some(s => s.title.includes('sync'))).toBe(true);
    });

    it('does NOT add a docs-lookup chip when the answer is confident (but still offers forward steps)', async () => {
        const provider = new MockProvider([
            mockTurn.text('The print command writes text to the screen.'),
        ]);
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace: new InMemoryWorkspace() },
            retriever: null,
            getCommandNames: async () => ['sync', 'print'],
        });

        const collected = collectEvents(agent);
        await agent.send('what does print do?');
        await collected.until();

        const suggestion = collected.events.find(
            (e): e is Extract<AgentEvent, { kind: 'suggestion' }> => e.kind === 'suggestion');
        // Always pushes the conversation forward...
        expect(suggestion).toBeDefined();
        // ...but no "Look up the `X` command" chip when nothing was hedged.
        expect(suggestion!.suggestions.some(s => s.title.startsWith('Look up'))).toBe(false);
    });

    it('always emits at least one forward suggestion', async () => {
        const provider = new MockProvider([mockTurn.text('Here is some general advice.')]);
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: { workspace: new InMemoryWorkspace() },
            retriever: null,
        });
        const collected = collectEvents(agent);
        await agent.send('any tips?');
        await collected.until();
        const suggestion = collected.events.find(
            (e): e is Extract<AgentEvent, { kind: 'suggestion' }> => e.kind === 'suggestion');
        expect(suggestion!.suggestions.length).toBeGreaterThanOrEqual(1);
    });
});

describe('Agent (docs for rejected edits)', () => {
    it('looks up the real command docs when an edit is rejected', async () => {
        const provider = new MockProvider([
            // 'make the ship move left' is a code request, so the agent first
            // runs the plan-and-research classification pass (consumes a turn).
            mockTurn.text('move the ship left'),
            mockTurn.streaming([
                '<tool_call>{"name":"apply_edit","args":{"path":"main.fbasic","startLine":1,"endLine":1,"newText":"IF key down \\"left\\" THEN x = 1"}}</tool_call>',
            ]),
            mockTurn.text('let me reconsider.'),
        ]);
        const ws = new InMemoryWorkspace({ 'main.fbasic': 'rem' });
        const searches: string[] = [];
        const fakeRetriever = {
            search: async (q: string) => {
                searches.push(q);
                return [{
                    chunk: { id: 'c1', source: 'Language.md', heading: 'Input', text: 'keystate(code)', chars: 13 },
                    score: 0.9,
                }];
            },
        };
        const agent = new Agent({
            provider,
            tools: createDefaultRegistry(),
            toolContext: {
                workspace: ws,
                reviewEdit: async () => ({ approved: false, feedback: 'L1: [0147] No overload for command (147)' }),
                confirmEdit: async () => true,
            },
            // eslint-disable-next-line @typescript-eslint/no-explicit-any
            retriever: fakeRetriever as any,
            getProjectType: () => 'web',
        });

        const collected = collectEvents(agent);
        await agent.send('make the ship move left');
        await collected.until();

        // It searched docs for the command the model used inside the edit.
        expect(searches).toContain('key down');
        const docs = collected.events.find(
            (e): e is Extract<AgentEvent, { kind: 'docs_retrieved' }> =>
                e.kind === 'docs_retrieved' && e.query.includes('key down'));
        expect(docs).toBeDefined();
    });
});
