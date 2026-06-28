// ChatProvider that delegates transformers.js work to ai/worker/inference.worker.ts.

import type {
    ChatProvider,
    ProviderCapabilities,
    ProviderProgress,
    StreamEvent,
    StreamOptions,
} from './types';
import type { TransformersJSProviderOptions } from './transformers-js';

type WorkerInbound =
    | { type: 'progress'; text: string; pct: number }
    | { type: 'load-done'; requestId: number }
    | { type: 'reset-done'; requestId: number }
    | { type: 'error'; requestId?: number; message: string }
    | { type: 'count-tokens-result'; requestId: number; count: number }
    | { type: 'stream-event'; streamId: number; event: StreamEvent }
    | { type: 'stream-end'; streamId: number }
    | { type: 'stream-error'; streamId: number; message: string };

let sharedWorker: Worker | null = null;
let nextRequestId = 1;
let nextStreamId = 1;
const moduleProgressListeners = new Set<(p: ProviderProgress) => void>();
let progressHooked = false;

function getSharedWorker(): Worker {
    if (!sharedWorker) {
        sharedWorker = new Worker(
            new URL('../worker/inference.worker.ts', import.meta.url),
            { type: 'module', name: 'fade-ai-inference' },
        );
        if (!progressHooked) {
            progressHooked = true;
            sharedWorker.addEventListener('message', (ev: MessageEvent<WorkerInbound>) => {
                if (ev.data.type !== 'progress') return;
                const p = { text: ev.data.text, pct: ev.data.pct };
                for (const l of moduleProgressListeners) {
                    try { l(p); } catch { /* ignore */ }
                }
            });
        }
    }
    return sharedWorker;
}

function rpc(worker: Worker, msg: Record<string, unknown>): Promise<void> {
    const requestId = nextRequestId++;
    return new Promise((resolve, reject) => {
        const onMessage = (ev: MessageEvent<WorkerInbound>) => {
            const data = ev.data;
            // Progress messages don't carry a requestId — they're a
            // global broadcast handled by getSharedWorker's listener.
            // Filter them out before checking requestId.
            if (data.type === 'progress') return;
            if (data.type === 'stream-event' || data.type === 'stream-end' || data.type === 'stream-error') return;
            if (data.requestId !== requestId) return;
            worker.removeEventListener('message', onMessage);
            if (data.type === 'error') reject(new Error(data.message));
            else resolve();
        };
        worker.addEventListener('message', onMessage);
        worker.postMessage({ ...msg, requestId });
    });
}

export class WorkerBridgeProvider implements ChatProvider {
    readonly id: string;
    readonly label: string;
    readonly capabilities: ProviderCapabilities;
    private readonly opts: TransformersJSProviderOptions;
    private readonly worker: Worker;
    private loaded = false;

    constructor(opts: TransformersJSProviderOptions) {
        this.opts = opts;
        this.worker = getSharedWorker();
        this.id = `transformers-js:${opts.modelId}`;
        this.label = opts.label ?? opts.modelId.split('/').slice(-1)[0];
        this.capabilities = {
            supportsTools: false,
            maxContext: opts.maxContext ?? 32_768,
            isCached: false,
            backend: opts.device ?? 'webgpu',
        };
    }

    countTokens(text: string): number {
        // Sync fallback — budget checks use a conservative estimate until
        // loaded; accurate counts aren't worth an async RPC on every check.
        if (!this.loaded) return Math.ceil(text.length / 4);
        // Best-effort: worker RPC is async; heuristic is fine for eviction.
        return Math.ceil(text.length / 4);
    }

    onProgress(cb: (p: ProviderProgress) => void): () => void {
        moduleProgressListeners.add(cb);
        return () => { moduleProgressListeners.delete(cb); };
    }

    async ensureReady(): Promise<void> {
        await rpc(this.worker, { type: 'load', opts: this.opts });
        this.loaded = true;
        this.capabilities.isCached = true;
    }

    async reset(): Promise<void> {
        await rpc(this.worker, { type: 'reset' });
        this.loaded = false;
        this.capabilities.isCached = false;
    }

    async *stream(opts: StreamOptions): AsyncIterable<StreamEvent> {
        const streamId = nextStreamId++;
        const queue: StreamEvent[] = [];
        let finished = false;
        let waiter: (() => void) | null = null;

        const onMessage = (ev: MessageEvent<WorkerInbound>) => {
            const data = ev.data;
            if (data.type === 'stream-event' && data.streamId === streamId) {
                queue.push(data.event);
                waiter?.();
            } else if (data.type === 'stream-end' && data.streamId === streamId) {
                finished = true;
                waiter?.();
            }
        };

        this.worker.addEventListener('message', onMessage);
        opts.signal?.addEventListener('abort', () => {
            this.worker.postMessage({ type: 'abort', streamId });
        }, { once: true });

        this.worker.postMessage({
            type: 'stream',
            streamId,
            messages: opts.messages,
            streamOpts: {
                maxTokens: opts.maxTokens,
                temperature: opts.temperature,
            },
        });

        try {
            while (true) {
                if (queue.length > 0) {
                    const event = queue.shift()!;
                    yield event;
                    if (event.kind === 'done') return;
                    continue;
                }
                if (finished) return;
                await new Promise<void>(resolve => { waiter = resolve; });
            }
        } finally {
            this.worker.removeEventListener('message', onMessage);
        }
    }
}

/** Terminate the shared worker (tests / page unload). */
export function disposeInferenceWorker(): void {
    sharedWorker?.terminate();
    sharedWorker = null;
    progressHooked = false;
    moduleProgressListeners.clear();
}
