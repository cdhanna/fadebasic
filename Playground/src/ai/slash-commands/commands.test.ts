import { describe, it, expect } from 'vitest';
import { createDefaultSlashRegistry } from './default-registry';
import { emptySlashState } from './registry';
import type { SlashContext } from './types';
import { ToolRegistry } from '../tools';
import { listFiles } from '../tools/list-files';
import { readFile } from '../tools/read-file';
import { MockProvider } from '../providers/mock';

function mkCtx(overrides: Partial<Omit<SlashContext, 'lookup' | 'list'>> = {}): Omit<SlashContext, 'lookup' | 'list'> {
    const tools = new ToolRegistry();
    tools.register(listFiles);
    tools.register(readFile);
    return {
        agent: null,
        provider: null,
        tools,
        state: emptySlashState(),
        callbacks: {},
        ...overrides,
    };
}

describe('/help', () => {
    it('lists every registered command', async () => {
        const r = createDefaultSlashRegistry();
        const result = await r.run('/help', () => mkCtx());
        expect(result?.title).toBe('Slash commands');
        const body = result?.body as string;
        for (const name of ['help', 'tools', 'model', 'context', 'plan', 'clear', 'logs']) {
            expect(body).toContain(`/${name}`);
        }
    });

    it('responds to the /? alias', async () => {
        const r = createDefaultSlashRegistry();
        const result = await r.run('/?', () => mkCtx());
        expect(result?.title).toBe('Slash commands');
    });
});

describe('/tools', () => {
    it('lists registered tools with arg signatures', async () => {
        const r = createDefaultSlashRegistry();
        const result = await r.run('/tools', () => mkCtx());
        const body = result?.body as string;
        expect(body).toContain('list_files');
        expect(body).toContain('read_file(path: string)');
    });

    it('reports "(no tools registered)" on an empty registry', async () => {
        const r = createDefaultSlashRegistry();
        const result = await r.run('/tools', () => mkCtx({ tools: new ToolRegistry() }));
        expect(result?.body).toContain('no tools');
    });
});

describe('/model', () => {
    it('shows a helpful message when no provider is loaded', async () => {
        const r = createDefaultSlashRegistry();
        const result = await r.run('/model', () => mkCtx());
        expect(result?.body).toContain('No model loaded');
    });

    it('renders provider capabilities when loaded', async () => {
        const provider = new MockProvider([], {
            id: 'mock-test', label: 'Mock Test',
            capabilities: { maxContext: 32_768, supportsTools: false, isCached: true },
        });
        const r = createDefaultSlashRegistry();
        const result = await r.run('/model', () => mkCtx({ provider }));
        const body = result?.body as string;
        expect(body).toContain('mock-test');
        expect(body).toContain('Mock Test');
        expect(body).toContain('32,768');
        expect(body).toContain('in-prompt protocol');
    });
});

describe('/context', () => {
    it('reports an empty conversation when no agent is loaded', async () => {
        const r = createDefaultSlashRegistry();
        const result = await r.run('/context', () => mkCtx());
        const body = result?.body as string;
        expect(body).toContain('0 message');
        expect(body).toContain('Last RAG retrieval: none');
        expect(body).toContain('Last plan: none');
    });

    it('shows last retrieved docs when set', async () => {
        const r = createDefaultSlashRegistry();
        const state = emptySlashState();
        state.lastDocs = [
            { chunk: { id: 'A', source: 'FadeBook/Language.md', heading: 'Variables', text: '', chars: 0, vector: [] }, score: 0.78 },
        ];
        const result = await r.run('/context', () => mkCtx({ state }));
        const body = result?.body as string;
        expect(body).toContain('FadeBook/Language.md → Variables');
        expect(body).toContain('0.78');
    });
});

describe('/plan', () => {
    it('says no plan when nothing emitted', async () => {
        const r = createDefaultSlashRegistry();
        const result = await r.run('/plan', () => mkCtx());
        expect(result?.body).toContain('No plan emitted');
    });

    it('renders the last plan', async () => {
        const r = createDefaultSlashRegistry();
        const state = emptySlashState();
        state.lastPlan = {
            goal: 'fix rotation bug',
            steps: [
                { tool: 'read_file', description: 'inspect main.fade' },
                { description: 'patch the rotate call' },
            ],
        };
        const result = await r.run('/plan', () => mkCtx({ state }));
        const body = result?.body as string;
        expect(body).toContain('fix rotation bug');
        expect(body).toContain('[read_file] inspect main.fade');
        expect(body).toContain('patch the rotate call');
    });
});

describe('/clear', () => {
    it('invokes the clearConversation callback and returns null', async () => {
        const r = createDefaultSlashRegistry();
        let cleared = false;
        const result = await r.run('/clear', () => mkCtx({
            callbacks: { clearConversation: () => { cleared = true; } },
        }));
        expect(cleared).toBe(true);
        expect(result).toBeNull();
    });

    it('returns an error when the callback is missing', async () => {
        const r = createDefaultSlashRegistry();
        const result = await r.run('/clear', () => mkCtx());
        expect(result?.variant).toBe('error');
    });
});

describe('/logs', () => {
    it('calls focusLogs with an ai/ pattern', async () => {
        const r = createDefaultSlashRegistry();
        let captured: RegExp | null = null;
        const result = await r.run('/logs', () => mkCtx({
            callbacks: { focusLogs: (p) => { captured = p; } },
        }));
        expect(captured).not.toBeNull();
        expect(captured!.test('ai/agent')).toBe(true);
        expect(captured!.test('sharing')).toBe(false);
        expect(result?.body).toContain('Logs panel focused');
    });

    it('returns a hint when focusLogs is not wired', async () => {
        const r = createDefaultSlashRegistry();
        const result = await r.run('/logs', () => mkCtx());
        expect(result?.body).toContain('not wired');
    });
});
