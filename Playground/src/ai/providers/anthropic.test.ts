import { describe, it, expect, afterEach, vi } from 'vitest';
import { AnthropicProvider, ensureAnthropicApiKey, clearAnthropicApiKey } from './anthropic';
import type { Msg } from './types';

describe('AnthropicProvider — construction', () => {
    it('reports a stable id and label', () => {
        const p = new AnthropicProvider({ modelId: 'claude-haiku-4-5', label: 'Haiku' });
        expect(p.id).toBe('anthropic:claude-haiku-4-5');
        expect(p.label).toBe('Haiku');
    });

    it('reports supportsTools=false (uses in-prompt protocol)', () => {
        const p = new AnthropicProvider({ modelId: 'claude-opus-4-7' });
        expect(p.capabilities.supportsTools).toBe(false);
        expect(p.capabilities.backend).toBe('anthropic-api');
        expect(p.capabilities.isCached).toBe(true);  // no local weights
    });

    it('counts tokens by char-heuristic (no tokenizer in browser)', () => {
        const p = new AnthropicProvider({ modelId: 'claude-haiku-4-5' });
        expect(p.countTokens('hello world')).toBe(Math.ceil(11 / 4));
    });
});

describe('AnthropicProvider — ensureReady', () => {
    afterEach(() => {
        vi.unstubAllGlobals();
        clearAnthropicApiKey();
    });

    it('throws when no API key is stored', async () => {
        vi.stubGlobal('localStorage', {
            getItem: () => null,
            setItem: () => {},
            removeItem: () => {},
        });
        const p = new AnthropicProvider({ modelId: 'claude-haiku-4-5' });
        await expect(p.ensureReady()).rejects.toThrow(/API key/i);
    });

    it('resolves when a key is stored', async () => {
        const store: Record<string, string> = {
            'fade.ai.anthropic.apiKey': 'sk-ant-fake-test-key',
        };
        vi.stubGlobal('localStorage', {
            getItem: (k: string) => store[k] ?? null,
            setItem: (k: string, v: string) => { store[k] = v; },
            removeItem: (k: string) => { delete store[k]; },
        });
        const p = new AnthropicProvider({ modelId: 'claude-haiku-4-5' });
        await expect(p.ensureReady()).resolves.toBeUndefined();
    });
});

describe('AnthropicProvider — stream', () => {
    afterEach(() => {
        vi.unstubAllGlobals();
    });

    function mockLocalStorage(key: string | null): void {
        const store: Record<string, string> = key ? { 'fade.ai.anthropic.apiKey': key } : {};
        vi.stubGlobal('localStorage', {
            getItem: (k: string) => store[k] ?? null,
            setItem: (k: string, v: string) => { store[k] = v; },
            removeItem: (k: string) => { delete store[k]; },
        });
    }

    /** Build a Response with an SSE body from a list of events. */
    function mockSseResponse(events: string[]): Response {
        const body = new ReadableStream<Uint8Array>({
            start(controller) {
                const encoder = new TextEncoder();
                for (const ev of events) {
                    controller.enqueue(encoder.encode(ev));
                }
                controller.close();
            },
        });
        return new Response(body, {
            status: 200,
            headers: { 'content-type': 'text/event-stream' },
        });
    }

    it('splits system messages out and only sends user/assistant in messages', async () => {
        mockLocalStorage('sk-ant-test');
        let capturedBody: Record<string, unknown> | null = null;
        vi.stubGlobal('fetch', vi.fn(async (_url: string, init: RequestInit) => {
            capturedBody = JSON.parse(init.body as string);
            return mockSseResponse([
                'event: message_delta\ndata: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}\n\n',
            ]);
        }));

        const p = new AnthropicProvider({ modelId: 'claude-haiku-4-5' });
        const msgs: Msg[] = [
            { role: 'system', content: 'You are helpful.' },
            { role: 'user', content: 'hi' },
            { role: 'assistant', content: 'hello' },
            { role: 'user', content: 'how are you?' },
        ];

        for await (const _ of p.stream({ messages: msgs })) { /* drain */ }

        expect(capturedBody).not.toBeNull();
        expect(capturedBody!.system).toBe('You are helpful.');
        const sentMessages = capturedBody!.messages as Array<{ role: string; content: string }>;
        expect(sentMessages).toHaveLength(3);
        expect(sentMessages.every(m => m.role !== 'system')).toBe(true);
        expect(sentMessages[0]).toMatchObject({ role: 'user', content: 'hi' });
    });

    it('emits text deltas from content_block_delta events', async () => {
        mockLocalStorage('sk-ant-test');
        vi.stubGlobal('fetch', vi.fn(async () => mockSseResponse([
            'event: content_block_delta\ndata: {"type":"content_block_delta","delta":{"type":"text_delta","text":"Hello"}}\n\n',
            'event: content_block_delta\ndata: {"type":"content_block_delta","delta":{"type":"text_delta","text":" world"}}\n\n',
            'event: message_delta\ndata: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}\n\n',
        ])));

        const p = new AnthropicProvider({ modelId: 'claude-haiku-4-5' });
        const deltas: string[] = [];
        let finishReason: string | null = null;
        for await (const ev of p.stream({ messages: [{ role: 'user', content: 'hi' }] })) {
            if (ev.kind === 'text') deltas.push(ev.delta);
            if (ev.kind === 'done') finishReason = ev.finishReason;
        }
        expect(deltas.join('')).toBe('Hello world');
        expect(finishReason).toBe('stop');
    });

    it('maps stop_reason values correctly', async () => {
        mockLocalStorage('sk-ant-test');
        vi.stubGlobal('fetch', vi.fn(async () => mockSseResponse([
            'event: message_delta\ndata: {"type":"message_delta","delta":{"stop_reason":"max_tokens"}}\n\n',
        ])));
        const p = new AnthropicProvider({ modelId: 'claude-haiku-4-5' });
        let reason: string | null = null;
        for await (const ev of p.stream({ messages: [{ role: 'user', content: 'hi' }] })) {
            if (ev.kind === 'done') reason = ev.finishReason;
        }
        expect(reason).toBe('length');
    });

    it('handles SSE events split across chunk boundaries', async () => {
        mockLocalStorage('sk-ant-test');
        vi.stubGlobal('fetch', vi.fn(async () => mockSseResponse([
            'event: content_block_delta\ndata: {"type":"content_block_delta",',  // chunk 1: split mid-JSON
            '"delta":{"type":"text_delta","text":"split"}}\n\n',                  // chunk 2: rest
            'event: message_delta\ndata: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}\n\n',
        ])));
        const p = new AnthropicProvider({ modelId: 'claude-haiku-4-5' });
        const deltas: string[] = [];
        for await (const ev of p.stream({ messages: [{ role: 'user', content: 'hi' }] })) {
            if (ev.kind === 'text') deltas.push(ev.delta);
        }
        expect(deltas.join('')).toBe('split');
    });

    it('throws on non-2xx HTTP response with the body in the message', async () => {
        mockLocalStorage('sk-ant-test');
        vi.stubGlobal('fetch', vi.fn(async () => new Response(
            JSON.stringify({ error: { type: 'authentication_error', message: 'invalid key' } }),
            { status: 401, headers: { 'content-type': 'application/json' } },
        )));
        const p = new AnthropicProvider({ modelId: 'claude-haiku-4-5' });
        await expect(async () => {
            for await (const _ of p.stream({ messages: [{ role: 'user', content: 'hi' }] })) { /* */ }
        }).rejects.toThrow(/401/);
    });

    it('sends stop_sequences: [</tool_call>] so the model halts at the close tag', async () => {
        mockLocalStorage('sk-ant-test');
        let capturedBody: Record<string, unknown> | null = null;
        vi.stubGlobal('fetch', vi.fn(async (_url: string, init: RequestInit) => {
            capturedBody = JSON.parse(init.body as string);
            return mockSseResponse([
                'event: message_delta\ndata: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}\n\n',
            ]);
        }));
        const p = new AnthropicProvider({ modelId: 'claude-haiku-4-5' });
        await drainStream(p.stream({ messages: [{ role: 'user', content: 'hi' }] }));
        expect(capturedBody!.stop_sequences).toEqual(['</tool_call>']);
    });

    it('synthesizes </tool_call> on stop_sequence if Anthropic strips it from the output', async () => {
        mockLocalStorage('sk-ant-test');
        // Mid-tool-call halt — text deltas don't include the close tag,
        // and the message_delta says stop_reason=stop_sequence with stop_sequence=</tool_call>.
        vi.stubGlobal('fetch', vi.fn(async () => mockSseResponse([
            'event: content_block_delta\ndata: {"type":"content_block_delta","delta":{"type":"text_delta","text":"<tool_call>{\\"name\\":\\"read_file\\",\\"args\\":{}}"}}\n\n',
            'event: message_delta\ndata: {"type":"message_delta","delta":{"stop_reason":"stop_sequence","stop_sequence":"</tool_call>"}}\n\n',
        ])));
        const p = new AnthropicProvider({ modelId: 'claude-haiku-4-5' });
        const text = (await drainStream(p.stream({ messages: [{ role: 'user', content: 'hi' }] })))
            .filter(e => e.kind === 'text')
            .map(e => (e as { delta: string }).delta)
            .join('');
        expect(text).toContain('</tool_call>');
    });

    it('does NOT duplicate </tool_call> if Anthropic already included it', async () => {
        mockLocalStorage('sk-ant-test');
        vi.stubGlobal('fetch', vi.fn(async () => mockSseResponse([
            'event: content_block_delta\ndata: {"type":"content_block_delta","delta":{"type":"text_delta","text":"<tool_call>{\\"name\\":\\"x\\",\\"args\\":{}}</tool_call>"}}\n\n',
            'event: message_delta\ndata: {"type":"message_delta","delta":{"stop_reason":"stop_sequence","stop_sequence":"</tool_call>"}}\n\n',
        ])));
        const p = new AnthropicProvider({ modelId: 'claude-haiku-4-5' });
        const text = (await drainStream(p.stream({ messages: [{ role: 'user', content: 'hi' }] })))
            .filter(e => e.kind === 'text')
            .map(e => (e as { delta: string }).delta)
            .join('');
        // Exactly one occurrence of the close tag
        const matches = text.match(/<\/tool_call>/g) ?? [];
        expect(matches).toHaveLength(1);
    });

    it('drops empty-content messages (Anthropic rejects them)', async () => {
        mockLocalStorage('sk-ant-test');
        let capturedBody: Record<string, unknown> | null = null;
        vi.stubGlobal('fetch', vi.fn(async (_url: string, init: RequestInit) => {
            capturedBody = JSON.parse(init.body as string);
            return mockSseResponse([
                'event: message_delta\ndata: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}\n\n',
            ]);
        }));
        const p = new AnthropicProvider({ modelId: 'claude-haiku-4-5' });
        await drainStream(p.stream({
            messages: [
                { role: 'user', content: 'real' },
                { role: 'assistant', content: '' },          // empty — must be dropped
                { role: 'user', content: 'second real' },
            ],
        }));
        const sent = capturedBody!.messages as Array<{ content: string }>;
        expect(sent).toHaveLength(2);
        expect(sent.every(m => m.content.length > 0)).toBe(true);
    });
});

describe('ensureAnthropicApiKey', () => {
    afterEach(() => { vi.unstubAllGlobals(); });

    it('returns the stored key without prompting if one exists', () => {
        const store: Record<string, string> = { 'fade.ai.anthropic.apiKey': 'sk-ant-existing' };
        vi.stubGlobal('localStorage', {
            getItem: (k: string) => store[k] ?? null,
            setItem: (k: string, v: string) => { store[k] = v; },
            removeItem: (k: string) => { delete store[k]; },
        });
        const promptSpy = vi.fn();
        vi.stubGlobal('prompt', promptSpy);
        expect(ensureAnthropicApiKey()).toBe('sk-ant-existing');
        expect(promptSpy).not.toHaveBeenCalled();
    });

    it('prompts and stores when missing', () => {
        const store: Record<string, string> = {};
        vi.stubGlobal('localStorage', {
            getItem: (k: string) => store[k] ?? null,
            setItem: (k: string, v: string) => { store[k] = v; },
            removeItem: (k: string) => { delete store[k]; },
        });
        vi.stubGlobal('prompt', () => '  sk-ant-from-prompt  ');
        const key = ensureAnthropicApiKey();
        expect(key).toBe('sk-ant-from-prompt');
        expect(store['fade.ai.anthropic.apiKey']).toBe('sk-ant-from-prompt');
    });

    it('returns null when the user cancels the prompt', () => {
        const store: Record<string, string> = {};
        vi.stubGlobal('localStorage', {
            getItem: (k: string) => store[k] ?? null,
            setItem: (k: string, v: string) => { store[k] = v; },
            removeItem: (k: string) => { delete store[k]; },
        });
        vi.stubGlobal('prompt', () => null);
        expect(ensureAnthropicApiKey()).toBeNull();
        expect(store['fade.ai.anthropic.apiKey']).toBeUndefined();
    });
});

async function drainStream<T>(stream: AsyncIterable<T>): Promise<T[]> {
    const out: T[] = [];
    for await (const ev of stream) out.push(ev);
    return out;
}
