// AnthropicProvider — Claude via the Messages API directly from the browser.
//
// Uses raw fetch + SSE parsing rather than the @anthropic-ai/sdk because this
// runs in a Vite-bundled browser app and the SDK adds ~150KB of dependencies
// we don't need for one endpoint. Browser CORS is enabled via the
// `anthropic-dangerous-direct-browser-access` header (the name is loud on
// purpose — the API key sits in localStorage, which is fine for a local
// dev tool but not a thing to do in a public product).
//
// Tool calling: reports supportsTools=false so the agent uses the same
// in-prompt <tool_call> protocol the local models use. The point of having
// Claude in here is to test whether the protocol works for a top-tier
// instruction-following model — switching Claude to native tool_use would
// change the comparison and only prove Claude can use Anthropic's API.

import { getLogger } from '../../log-bus';
import type {
    ChatProvider,
    Msg,
    ProviderCapabilities,
    ProviderProgress,
    StreamEvent,
    StreamOptions,
    FinishReason,
} from './types';

const log = getLogger('ai/provider');

const ANTHROPIC_API_URL = 'https://api.anthropic.com/v1/messages';
const API_KEY_STORAGE = 'fade.ai.anthropic.apiKey';

export interface AnthropicProviderOptions {
    /** Anthropic model ID, e.g. 'claude-haiku-4-5'. */
    modelId: string;
    /** Display label. Defaults to a humanized model ID. */
    label?: string;
    /** Max output tokens. Default 4096. */
    maxTokens?: number;
    /** Native context window (used for the budget tracker). */
    maxContext?: number;
}

const DEFAULTS = {
    maxTokens: 4096,
    maxContext: 200_000,  // Haiku 4.5; Opus/Sonnet have 1M but 200K is a safe report
};

export class AnthropicProvider implements ChatProvider {
    readonly id: string;
    readonly label: string;
    readonly capabilities: ProviderCapabilities;

    private readonly opts: Required<AnthropicProviderOptions>;

    constructor(opts: AnthropicProviderOptions) {
        this.opts = {
            modelId: opts.modelId,
            label: opts.label ?? opts.modelId,
            maxTokens: opts.maxTokens ?? DEFAULTS.maxTokens,
            maxContext: opts.maxContext ?? DEFAULTS.maxContext,
        };
        this.id = `anthropic:${this.opts.modelId}`;
        this.label = this.opts.label;
        this.capabilities = {
            supportsTools: false,
            maxContext: this.opts.maxContext,
            isCached: true,    // network model — no local weights to download
            backend: 'anthropic-api',
        };
    }

    countTokens(text: string): number {
        // No tokenizer in the browser. ~4 chars/token is the standard
        // heuristic for Claude — close enough for budget tracking.
        return Math.ceil(text.length / 4);
    }

    onProgress(_cb: (p: ProviderProgress) => void): () => void {
        // Network model — no load progress to report.
        return () => {};
    }

    async ensureReady(): Promise<void> {
        // Network model. The only "load" check is that a key exists.
        const key = this.resolveApiKey();
        if (!key) {
            throw new Error(
                'Anthropic API key required. Click "Load Model" to enter one, '
                + 'or set localStorage.setItem("fade.ai.anthropic.apiKey", "sk-ant-…").',
            );
        }
    }

    reset(): void {
        // No persistent state. The retry path resets API key in case the
        // user wants to swap it; we leave that to a dedicated UI control.
    }

    async *stream(opts: StreamOptions): AsyncIterable<StreamEvent> {
        const apiKey = this.resolveApiKey();
        if (!apiKey) {
            throw new Error('Anthropic API key not configured');
        }

        const { system, messages } = splitSystemFromMessages(opts.messages);

        const body: Record<string, unknown> = {
            model: this.opts.modelId,
            max_tokens: opts.maxTokens ?? this.opts.maxTokens,
            messages,
            stream: true,
            // Halt generation at </tool_call> so the model can't roleplay
            // both sides of the conversation. Without this, Claude will
            // emit <tool_call> followed by a fabricated <tool_result> +
            // answer, all in one turn (observed in dev — the agent recovers
            // on turn 2 by running the real tool, but turn 1's output is
            // misleading hallucinated content). Mirrors the local
            // TransformersJSProvider's `stop_strings` configuration.
            stop_sequences: ['</tool_call>'],
        };
        if (system) body.system = system;
        if (opts.temperature !== undefined) body.temperature = opts.temperature;

        log.info(`anthropic stream start model=${this.opts.modelId} messages=${messages.length}`);

        const response = await fetch(ANTHROPIC_API_URL, {
            method: 'POST',
            headers: {
                'x-api-key': apiKey,
                'anthropic-version': '2023-06-01',
                'content-type': 'application/json',
                'anthropic-dangerous-direct-browser-access': 'true',
            },
            body: JSON.stringify(body),
            signal: opts.signal,
        });

        if (!response.ok) {
            const text = await response.text().catch(() => '');
            log.error(`anthropic api ${response.status}: ${text.slice(0, 300)}`);
            throw new Error(`Anthropic API ${response.status}: ${truncate(text, 200)}`);
        }
        if (!response.body) {
            throw new Error('Anthropic API returned empty response body');
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        let finishReason: FinishReason = 'stop';
        // Track which stop_sequence (if any) ended the stream + a tail of
        // emitted text so we can verify whether the close tag came through.
        // Anthropic's API behavior on whether it includes the stop sequence
        // in the output isn't strictly documented and has shifted between
        // releases — being defensive here costs ~30 bytes of memory.
        let stoppedAt: string | null = null;
        let emittedTail = '';

        try {
            while (true) {
                let chunk: ReadableStreamReadResult<Uint8Array>;
                try {
                    chunk = await reader.read();
                } catch (e) {
                    if (opts.signal?.aborted) {
                        yield { kind: 'done', finishReason: 'aborted' };
                        return;
                    }
                    throw e;
                }
                if (chunk.done) break;
                buffer += decoder.decode(chunk.value, { stream: true });

                // SSE event boundaries are blank lines.
                let boundary: number;
                while ((boundary = buffer.indexOf('\n\n')) >= 0) {
                    const eventBlock = buffer.slice(0, boundary);
                    buffer = buffer.slice(boundary + 2);

                    const dataLine = eventBlock
                        .split('\n')
                        .find(line => line.startsWith('data:'));
                    if (!dataLine) continue;

                    const data = dataLine.slice('data:'.length).trim();
                    if (!data || data === '[DONE]') continue;

                    let event: AnthropicSseEvent;
                    try {
                        event = JSON.parse(data) as AnthropicSseEvent;
                    } catch {
                        continue;
                    }

                    if (event.type === 'content_block_delta'
                        && event.delta?.type === 'text_delta'
                        && typeof event.delta.text === 'string') {
                        yield { kind: 'text', delta: event.delta.text };
                        emittedTail = (emittedTail + event.delta.text).slice(-32);
                    } else if (event.type === 'message_delta' && event.delta?.stop_reason) {
                        finishReason = mapStopReason(event.delta.stop_reason);
                        if (typeof event.delta.stop_sequence === 'string') {
                            stoppedAt = event.delta.stop_sequence;
                        }
                    } else if (event.type === 'error') {
                        throw new Error(
                            `Anthropic stream error: ${event.error?.type ?? 'unknown'}: ${event.error?.message ?? ''}`,
                        );
                    }
                }
            }

            // Anthropic strips the stop_sequence from streamed deltas in
            // some configurations. If we halted at </tool_call> but the
            // text doesn't include it, the parser will see an unclosed
            // tag — emit the close so the parser closes cleanly.
            if (stoppedAt && !emittedTail.endsWith(stoppedAt)) {
                yield { kind: 'text', delta: stoppedAt };
            }

            yield { kind: 'done', finishReason };
        } finally {
            try { reader.releaseLock(); } catch { /* already released */ }
        }
    }

    // ─── API key management ────────────────────────────────────────────────

    private resolveApiKey(): string | null {
        if (typeof localStorage === 'undefined') return null;
        return localStorage.getItem(API_KEY_STORAGE);
    }
}

// ─── Key prompt helper used by the chat panel ──────────────────────────────

/** Returns the stored Anthropic API key, prompting the user if absent.
 *  Save via `localStorage.setItem(...)` so subsequent sessions skip the prompt.
 *  Returns null if the user cancels. */
export function ensureAnthropicApiKey(): string | null {
    if (typeof localStorage === 'undefined') return null;
    const existing = localStorage.getItem(API_KEY_STORAGE);
    if (existing) return existing;
    if (typeof prompt !== 'function') return null;
    const entered = prompt(
        'Enter your Anthropic API key (sk-ant-...).\n\n'
        + 'Stored in localStorage on this device only — clear by running:\n'
        + 'localStorage.removeItem("fade.ai.anthropic.apiKey")',
    );
    if (!entered) return null;
    const trimmed = entered.trim();
    if (!trimmed) return null;
    localStorage.setItem(API_KEY_STORAGE, trimmed);
    return trimmed;
}

export function clearAnthropicApiKey(): void {
    if (typeof localStorage !== 'undefined') {
        localStorage.removeItem(API_KEY_STORAGE);
    }
}

// ─── Conversion + parsing helpers ──────────────────────────────────────────

interface AnthropicSseEvent {
    type: string;
    delta?: {
        type?: string;
        text?: string;
        stop_reason?: string;
        stop_sequence?: string;
    };
    error?: { type?: string; message?: string };
}

/** Anthropic's Messages API requires `system` as a top-level field, not as
 *  a role in `messages`. Pulls system messages out, joins them, and returns
 *  the remaining alternating user/assistant turns. */
function splitSystemFromMessages(history: Msg[]): {
    system: string | null;
    messages: Array<{ role: 'user' | 'assistant'; content: string }>;
} {
    const systemBlocks: string[] = [];
    const rest: Array<{ role: 'user' | 'assistant'; content: string }> = [];
    for (const m of history) {
        if (m.role === 'system') {
            systemBlocks.push(m.content);
        } else if (m.role === 'user' || m.role === 'assistant') {
            // Skip empty messages — Anthropic rejects them.
            if (!m.content || m.content.length === 0) continue;
            rest.push({ role: m.role, content: m.content });
        }
        // `tool` role messages aren't valid in this protocol path; the
        // agent uses in-prompt <tool_result> in user messages instead.
    }
    return {
        system: systemBlocks.length > 0 ? systemBlocks.join('\n\n') : null,
        messages: rest,
    };
}

function mapStopReason(s: string): FinishReason {
    switch (s) {
        case 'end_turn': return 'stop';
        case 'stop_sequence': return 'stop';
        case 'max_tokens': return 'length';
        case 'tool_use': return 'tool_calls';
        case 'refusal': return 'error';
        case 'pause_turn': return 'stop';
        default: return 'stop';
    }
}

function truncate(s: string, n: number): string {
    return s.length <= n ? s : s.slice(0, n) + '…';
}
