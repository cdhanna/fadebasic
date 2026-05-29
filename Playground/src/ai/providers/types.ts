// Core types for the AI subsystem. Every provider produces StreamEvents in
// the same shape so the agent loop is provider-agnostic. New providers
// (Ollama, hosted APIs) just need to emit the same events; tests and the
// agent don't care which model is actually running.

export type Role = 'system' | 'user' | 'assistant' | 'tool';

export interface Msg {
    role: Role;
    content: string;
    /** Set on `tool` role messages — correlates back to the call. */
    toolCallId?: string;
    /** Set on `tool` role messages — human-readable tool name (for display). */
    name?: string;
}

/** Public-facing tool description sent to the model (via prompt or API). */
export interface Tool {
    name: string;
    description: string;
    /** JSON Schema for the arguments (derived from the Zod schema). */
    schema: Record<string, unknown>;
}

/** Unified stream event. Every provider produces this regardless of how the
 *  underlying model communicates (native tools API, in-prompt protocol, etc.). */
export type StreamEvent =
    | { kind: 'text'; delta: string }
    | { kind: 'tool_call'; id: string; name: string; args: unknown }
    | { kind: 'done'; finishReason: FinishReason };

export type FinishReason = 'stop' | 'tool_calls' | 'length' | 'error' | 'aborted';

export interface ProviderCapabilities {
    /** True if the runtime has a native tools API. Otherwise the agent injects
     *  the in-prompt <tool_call> protocol itself. */
    supportsTools: boolean;
    /** Native context window for the loaded model. */
    maxContext: number;
    /** Whether weights live in IndexedDB / OPFS already. */
    isCached: boolean;
    /** Inference backend in use: "webgpu", "wasm", "cpu", "remote", etc.
     *  Surfaced in /model so the user knows whether they're on GPU or CPU. */
    backend?: string;
}

/** Reported by providers during model load. `pct` is 0..1. */
export interface ProviderProgress {
    text: string;
    pct: number;
}

export interface StreamOptions {
    messages: Msg[];
    tools?: Tool[];
    signal?: AbortSignal;
    /** Optional sampling controls; providers may ignore. */
    temperature?: number;
    maxTokens?: number;
}

export interface ChatProvider {
    /** Stable identifier for telemetry / persistence. */
    readonly id: string;
    /** Human-readable label shown in the Models tab. */
    readonly label: string;

    /** Cheap, conservative token count. */
    countTokens(text: string): number;

    /** Stream a completion. */
    stream(opts: StreamOptions): AsyncIterable<StreamEvent>;

    /** Idempotent. Loads weights with progress events on the bus. */
    ensureReady(): Promise<void>;

    /** Drop any in-memory session state. The next ensureReady() reloads
     *  from scratch. Used to recover from runtime errors (e.g. ORT-Web's
     *  intermittent "Invalid buffer" failures) by forcing a fresh session.
     *  May be async because backends like transformers.js need to await
     *  GPU-buffer disposal before the next session can allocate cleanly. */
    reset(): void | Promise<void>;

    /** Capabilities of the currently-loaded model. May change after
     *  ensureReady() resolves. */
    readonly capabilities: ProviderCapabilities;

    /** Subscribe to load-progress updates. Returns unsubscribe. */
    onProgress(cb: (p: ProviderProgress) => void): () => void;
}
