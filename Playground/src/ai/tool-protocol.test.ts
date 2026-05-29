import { describe, it, expect } from 'vitest';
import {
    BlockStreamParser,
    renderToolProtocolPrompt,
    renderToolResult,
    type ProtocolEvent,
} from './tool-protocol';

function feedAll(parser: BlockStreamParser, deltas: string[]): ProtocolEvent[] {
    const out: ProtocolEvent[] = [];
    for (const d of deltas) out.push(...parser.feed(d));
    out.push(...parser.end());
    return out;
}

// Alias for the old test-name to keep the file readable.
const ToolCallStreamParser = BlockStreamParser;

describe('ToolCallStreamParser', () => {
    it('passes plain text through as one stream', () => {
        const p = new ToolCallStreamParser();
        const out = feedAll(p, ['hello ', 'world']);
        // Text may be split across deltas — collapse for comparison.
        const text = out.filter(e => e.kind === 'text').map(e => (e as { delta: string }).delta).join('');
        expect(text).toBe('hello world');
        expect(out.find(e => e.kind === 'tool_call')).toBeUndefined();
    });

    it('extracts a single tool call between text', () => {
        const p = new ToolCallStreamParser();
        const out = feedAll(p, [
            'Let me check. ',
            '<tool_call>\n{"name":"list_files","args":{}}\n</tool_call>',
            ' done.',
        ]);
        const text = out.filter(e => e.kind === 'text').map(e => (e as { delta: string }).delta).join('');
        expect(text).toBe('Let me check.  done.');

        const calls = out.filter(e => e.kind === 'tool_call') as Array<{ name: string; args: unknown }>;
        expect(calls).toHaveLength(1);
        expect(calls[0].name).toBe('list_files');
        expect(calls[0].args).toEqual({});
    });

    it('extracts a tool call split across many deltas', () => {
        const p = new ToolCallStreamParser();
        // Maximally adversarial: split the open and close tags across deltas.
        const out = feedAll(p, [
            '<tool',
            '_call>',
            '\n{"name":"read_file","args":{"path":"main.fade"}}\n',
            '</tool',
            '_call>',
        ]);
        const calls = out.filter(e => e.kind === 'tool_call') as Array<{ name: string; args: { path: string } }>;
        expect(calls).toHaveLength(1);
        expect(calls[0].name).toBe('read_file');
        expect(calls[0].args.path).toBe('main.fade');
    });

    it('extracts two tool calls in one stream', () => {
        const p = new ToolCallStreamParser();
        const out = feedAll(p, [
            '<tool_call>{"name":"a","args":{}}</tool_call>',
            '<tool_call>{"name":"b","args":{"x":1}}</tool_call>',
        ]);
        const calls = out.filter(e => e.kind === 'tool_call') as Array<{ name: string }>;
        expect(calls.map(c => c.name)).toEqual(['a', 'b']);
    });

    it('surfaces malformed JSON as text instead of crashing', () => {
        const p = new ToolCallStreamParser();
        const out = feedAll(p, ['<tool_call>{not json}</tool_call> ok']);
        const text = out.filter(e => e.kind === 'text').map(e => (e as { delta: string }).delta).join('');
        expect(text).toContain('invalid tool call');
        expect(text).toContain(' ok');
        expect(out.find(e => e.kind === 'tool_call')).toBeUndefined();
    });

    it('handles an unclosed tag gracefully on end()', () => {
        const p = new ToolCallStreamParser();
        const out = feedAll(p, ['<tool_call>{"name":"a","args":{}}']);
        // No close tag — surfaces as text on end()
        const text = out.filter(e => e.kind === 'text').map(e => (e as { delta: string }).delta).join('');
        expect(text).toContain('unclosed <tool_call>');
    });

    it('accepts arguments alias', () => {
        const p = new ToolCallStreamParser();
        const out = feedAll(p, ['<tool_call>{"name":"a","arguments":{"k":"v"}}</tool_call>']);
        const calls = out.filter(e => e.kind === 'tool_call') as Array<{ args: { k: string } }>;
        expect(calls[0].args.k).toBe('v');
    });
});

describe('BlockStreamParser — plan blocks', () => {
    it('extracts a plan block', () => {
        const p = new BlockStreamParser();
        const out = feedAll(p, [
            '<plan>{"goal":"list files","steps":[{"tool":"list_files","description":"see what is here"}]}</plan>',
            'OK now I will look.',
        ]);
        const plans = out.filter(e => e.kind === 'plan') as Array<{ plan: { goal: string; steps: Array<{ tool?: string; description: string }> } }>;
        expect(plans).toHaveLength(1);
        expect(plans[0].plan.goal).toBe('list files');
        expect(plans[0].plan.steps).toEqual([{ tool: 'list_files', description: 'see what is here' }]);

        const text = out.filter(e => e.kind === 'text').map(e => (e as { delta: string }).delta).join('');
        expect(text).toBe('OK now I will look.');
    });

    it('extracts a plan followed by a tool call', () => {
        const p = new BlockStreamParser();
        const out = feedAll(p, [
            '<plan>{"goal":"read it","steps":[{"tool":"read_file","description":"x"}]}</plan>\n',
            '<tool_call>{"name":"read_file","args":{"path":"a.fade"}}</tool_call>',
        ]);
        const kinds = out.map(e => e.kind);
        expect(kinds).toContain('plan');
        expect(kinds).toContain('tool_call');
    });

    it('accepts string-only steps', () => {
        const p = new BlockStreamParser();
        const out = feedAll(p, ['<plan>{"goal":"x","steps":["first","second"]}</plan>']);
        const plan = out.find(e => e.kind === 'plan') as { plan: { steps: Array<{ description: string }> } };
        expect(plan.plan.steps.map(s => s.description)).toEqual(['first', 'second']);
    });

    it('treats prose plans as goal-only plans (no "[invalid plan]" noise)', () => {
        // Claude often emits prose inside <plan> instead of JSON. We don't
        // want that to surface as a user-visible error — plans are advisory.
        const p = new BlockStreamParser();
        const out = feedAll(p, [
            '<plan>I will read main.fbasic, then add the print statement after "test 9".</plan> ok',
        ]);
        const text = out.filter(e => e.kind === 'text').map(e => (e as { delta: string }).delta).join('');
        expect(text).not.toContain('invalid plan');
        const plan = out.find(e => e.kind === 'plan') as { plan: { goal: string; steps: unknown[] } } | undefined;
        expect(plan, 'prose plan should still emit a plan event').toBeDefined();
        expect(plan!.plan.goal).toContain('read main.fbasic');
        expect(plan!.plan.steps).toEqual([]);
    });
});

describe('renderToolProtocolPrompt', () => {
    it('lists tool names and arg types', () => {
        const prompt = renderToolProtocolPrompt([
            {
                name: 'read_file',
                description: 'Read a file',
                schema: { type: 'object', properties: { path: { type: 'string' } } },
            },
            {
                name: 'apply_edit',
                description: 'Edit a range',
                schema: {
                    type: 'object',
                    properties: { path: { type: 'string' }, startLine: { type: 'number' } },
                },
            },
        ]);
        expect(prompt).toContain('<tool_call>');
        expect(prompt).toContain('read_file(path: string) — Read a file');
        expect(prompt).toContain('apply_edit(path: string, startLine: number) — Edit a range');
    });
});

describe('renderToolResult', () => {
    it('wraps strings', () => {
        expect(renderToolResult('foo', 'hello')).toBe('<tool_result name="foo">hello</tool_result>');
    });
    it('JSON-encodes objects', () => {
        expect(renderToolResult('foo', { a: 1 })).toBe('<tool_result name="foo">{"a":1}</tool_result>');
    });
});
