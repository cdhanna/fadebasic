import { describe, it, expect } from 'vitest';
import {
    BlockStreamParser,
    repairAndParseJson,
    parseToolCallBody,
    renderToolProtocolPrompt,
    renderToolResult,
    renderToolCallRetryPrompt,
    type ProtocolEvent,
} from './tool-protocol';

function feedAll(parser: BlockStreamParser, deltas: string[]): ProtocolEvent[] {
    const out: ProtocolEvent[] = [];
    for (const d of deltas) out.push(...parser.feed(d));
    out.push(...parser.end());
    return out;
}

const ToolCallStreamParser = BlockStreamParser;

describe('ToolCallStreamParser', () => {
    it('passes plain text through as one stream', () => {
        const p = new ToolCallStreamParser();
        const out = feedAll(p, ['hello ', 'world']);
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

    it('surfaces malformed JSON as tool_parse_error', () => {
        const p = new ToolCallStreamParser();
        const out = feedAll(p, ['<tool_call>{not json}</tool_call> ok']);
        const errors = out.filter(e => e.kind === 'tool_parse_error') as Array<{ error: string }>;
        expect(errors).toHaveLength(1);
        expect(errors[0].error).toContain('not valid JSON');
        const text = out.filter(e => e.kind === 'text').map(e => (e as { delta: string }).delta).join('');
        expect(text).toBe(' ok');
    });

    it('salvages a valid tool_call that is missing its closing tag', () => {
        // Small models often stop right after the JSON (end-of-turn token)
        // without emitting </tool_call>. The body is valid, so recover it.
        const p = new ToolCallStreamParser();
        const out = feedAll(p, ['<tool_call>{"name":"read_file","args":{"path":"main.fbasic"}}']);
        const calls = out.filter(e => e.kind === 'tool_call') as Array<{ name: string; args: { path: string } }>;
        expect(calls).toHaveLength(1);
        expect(calls[0].name).toBe('read_file');
        expect(calls[0].args.path).toBe('main.fbasic');
        const text = out.filter(e => e.kind === 'text').map(e => (e as { delta: string }).delta).join('');
        expect(text).not.toContain('unclosed');
    });

    it('reports a parse error for an unclosed AND malformed tool_call body', () => {
        const p = new ToolCallStreamParser();
        const out = feedAll(p, ['<tool_call>{"name":"a", not json']);
        const errors = out.filter(e => e.kind === 'tool_parse_error');
        expect(errors).toHaveLength(1);
    });

    it('accepts arguments alias', () => {
        const p = new ToolCallStreamParser();
        const out = feedAll(p, ['<tool_call>{"name":"a","arguments":{"k":"v"}}</tool_call>']);
        const calls = out.filter(e => e.kind === 'tool_call') as Array<{ args: { k: string } }>;
        expect(calls[0].args.k).toBe('v');
    });

    it('parses attributed apply_edit with multiline body', () => {
        const p = new ToolCallStreamParser();
        const out = feedAll(p, [
            '<tool_call name="apply_edit" path="main.fbasic" start="2" end="3">\n',
            'print "hello"\n',
            'print "world"\n',
            '</tool_call>',
        ]);
        const calls = out.filter(e => e.kind === 'tool_call') as Array<{
            name: string;
            args: { path: string; startLine: number; endLine: number; newText: string };
        }>;
        expect(calls).toHaveLength(1);
        expect(calls[0].name).toBe('apply_edit');
        expect(calls[0].args.path).toBe('main.fbasic');
        expect(calls[0].args.startLine).toBe(2);
        expect(calls[0].args.endLine).toBe(3);
        expect(calls[0].args.newText).toBe('\nprint "hello"\nprint "world"\n');
    });

    it('parses attributed create_file', () => {
        const p = new ToolCallStreamParser();
        const out = feedAll(p, [
            '<tool_call name="create_file" path="new.fbasic">\n',
            'print "new"\n',
            '</tool_call>',
        ]);
        const calls = out.filter(e => e.kind === 'tool_call') as Array<{
            args: { path: string; content: string };
        }>;
        expect(calls[0].args.path).toBe('new.fbasic');
        expect(calls[0].args.content).toBe('\nprint "new"\n');
    });
});

describe('repairAndParseJson', () => {
    it('strips markdown fences', () => {
        const r = repairAndParseJson('```json\n{"name":"a","args":{}}\n```');
        expect(r.ok).toBe(true);
        expect((r as { value: { name: string } }).value.name).toBe('a');
    });

    it('tolerates trailing commas', () => {
        const r = repairAndParseJson('{"name":"a","args":{},}');
        expect(r.ok).toBe(true);
    });

    it('extracts JSON from surrounding prose', () => {
        const r = repairAndParseJson('here: {"name":"read_file","args":{"path":"x.fade"}} thanks');
        expect(r.ok).toBe(true);
        expect((r as { value: { name: string } }).value.name).toBe('read_file');
    });
});

describe('parseToolCallBody', () => {
    it('parses attributed read_file', () => {
        const r = parseToolCallBody('', 1, { name: 'read_file', path: 'a.fade' });
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.value.name).toBe('read_file');
            expect(r.value.args).toEqual({ path: 'a.fade' });
        }
    });
});

describe('BlockStreamParser — plan blocks', () => {
    it('extracts a plan block', () => {
        const p = new BlockStreamParser();
        const out = feedAll(p, [
            '<plan>{"goal":"list files","steps":[{"tool":"list_files","description":"see what is here"}]}</plan>',
            'OK now I will look.',
        ]);
        const plans = out.filter(e => e.kind === 'plan') as Array<{ plan: { goal: string } }>;
        expect(plans).toHaveLength(1);
        expect(plans[0].plan.goal).toBe('list files');
    });

    it('treats prose plans as goal-only plans', () => {
        const p = new BlockStreamParser();
        const out = feedAll(p, [
            '<plan>I will read main.fbasic, then add the print statement.</plan> ok',
        ]);
        const text = out.filter(e => e.kind === 'text').map(e => (e as { delta: string }).delta).join('');
        expect(text).not.toContain('invalid plan');
        const plan = out.find(e => e.kind === 'plan') as { plan: { goal: string } } | undefined;
        expect(plan?.plan.goal).toContain('read main.fbasic');
    });
});

describe('renderToolProtocolPrompt', () => {
    it('lists tool names and documents attribute form for writes', () => {
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
        expect(prompt).toContain('name="apply_edit"');
        expect(prompt).toContain('read_file(path: string) — Read a file');
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

describe('renderToolCallRetryPrompt', () => {
    it('includes the parse error and format hints', () => {
        const p = renderToolCallRetryPrompt('not valid JSON');
        expect(p).toContain('not valid JSON');
        expect(p).toContain('apply_edit');
    });
});
