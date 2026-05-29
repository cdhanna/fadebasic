// Deterministic ChatProvider for tests. The constructor takes a list of
// "turns" — each turn is a sequence of StreamEvents the provider emits the
// next time `stream()` is called. After all scripted turns are consumed,
// further `stream()` calls throw, which is the test's signal that the agent
// asked for more iterations than the test expected.
//
// MockProvider is intentionally minimal: no sampling, no real tokenization,
// no async delays. If a test needs to simulate timing, it can `await`
// between turns itself.

import type {
    ChatProvider,
    ProviderCapabilities,
    ProviderProgress,
    StreamEvent,
    StreamOptions,
} from './types';

export interface MockTurn {
    events: StreamEvent[];
}

/** Convenience helpers for constructing turns concisely in tests. */
export const mockTurn = {
    text(text: string, finishReason: 'stop' | 'length' = 'stop'): MockTurn {
        return { events: [{ kind: 'text', delta: text }, { kind: 'done', finishReason }] };
    },

    toolCall(name: string, args: unknown, id: string = `call_${Math.random().toString(36).slice(2, 8)}`): MockTurn {
        return {
            events: [
                { kind: 'tool_call', id, name, args },
                { kind: 'done', finishReason: 'tool_calls' },
            ],
        };
    },

    textThenToolCall(text: string, name: string, args: unknown, id: string = `call_${Math.random().toString(36).slice(2, 8)}`): MockTurn {
        return {
            events: [
                { kind: 'text', delta: text },
                { kind: 'tool_call', id, name, args },
                { kind: 'done', finishReason: 'tool_calls' },
            ],
        };
    },

    streaming(deltas: string[], finishReason: 'stop' | 'length' = 'stop'): MockTurn {
        const events: StreamEvent[] = deltas.map(d => ({ kind: 'text' as const, delta: d }));
        events.push({ kind: 'done', finishReason });
        return { events };
    },
};

export interface MockProviderOptions {
    id?: string;
    label?: string;
    capabilities?: Partial<ProviderCapabilities>;
}

export class MockProvider implements ChatProvider {
    readonly id: string;
    readonly label: string;
    readonly capabilities: ProviderCapabilities;

    private turns: MockTurn[];
    private nextTurnIdx = 0;
    /** Messages received by each call, recorded for test assertions. */
    public sentMessages: StreamOptions[] = [];

    constructor(turns: MockTurn[], opts: MockProviderOptions = {}) {
        this.turns = turns;
        this.id = opts.id ?? 'mock';
        this.label = opts.label ?? 'Mock';
        this.capabilities = {
            supportsTools: false,
            maxContext: 32_768,
            isCached: true,
            ...(opts.capabilities ?? {}),
        };
    }

    countTokens(text: string): number {
        // Cheap approximation. Good enough for tests; real providers use a
        // tokenizer. Roughly 4 chars per token for English code-ish text.
        return Math.ceil(text.length / 4);
    }

    async ensureReady(): Promise<void> {
        // Always ready.
    }

    onProgress(_cb: (p: ProviderProgress) => void): () => void {
        return () => {};
    }

    async *stream(opts: StreamOptions): AsyncIterable<StreamEvent> {
        this.sentMessages.push(opts);

        if (this.nextTurnIdx >= this.turns.length) {
            throw new Error(
                `MockProvider exhausted: agent requested turn ${this.nextTurnIdx + 1} ` +
                `but only ${this.turns.length} turn(s) were scripted.`,
            );
        }
        const turn = this.turns[this.nextTurnIdx++];

        for (const ev of turn.events) {
            if (opts.signal?.aborted) {
                yield { kind: 'done', finishReason: 'aborted' };
                return;
            }
            yield ev;
        }
    }

    /** Drop scripted-turn cursor + sentMessages history. Satisfies the
     *  ChatProvider.reset() contract AND serves as a between-test hook. */
    reset(): void {
        this.nextTurnIdx = 0;
        this.sentMessages = [];
    }

    /** Lets tests append more turns mid-run if needed. */
    pushTurn(turn: MockTurn): void {
        this.turns.push(turn);
    }
}
