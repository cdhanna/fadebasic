// TransformersJSProvider — runs an instruction-tuned LLM in the browser
// via @huggingface/transformers. WebGPU when available, WASM fallback
// otherwise. Tool calls go through the in-prompt protocol (see
// tool-protocol.ts), so this provider reports supportsTools: false and the
// agent layer parses tool calls out of streamed text.
//
// The actual LLM runs on the main thread for v1. Moving to a Web Worker is
// a later optimization — for the 1.5B default model on a modern machine,
// main-thread inference is acceptable and the worker bridge adds real
// complexity (message-passing, transferring big tensors).

import { pipeline, TextStreamer } from '@huggingface/transformers';
import type { TextGenerationPipeline } from '@huggingface/transformers';
import { getLogger } from '../../log-bus';
import type {
    ChatProvider,
    Msg,
    ProviderCapabilities,
    ProviderProgress,
    StreamEvent,
    StreamOptions,
} from './types';
import { ModelLoadProgressTracker } from './load-progress';

const log = getLogger('ai/provider');

export interface TransformersJSProviderOptions {
    /** HF model ID, e.g. 'onnx-community/Qwen2.5-Coder-1.5B-Instruct'. */
    modelId: string;
    /** Display label for the Models tab. Defaults to the model ID tail. */
    label?: string;
    /** ONNX dtype. 'q4f16' is the standard for WebGPU. */
    dtype?: 'auto' | 'fp32' | 'fp16' | 'q8' | 'int8' | 'uint8' | 'q4' | 'bnb4' | 'q4f16';
    /** Device. 'webgpu' is the default and fastest where supported. */
    device?: 'webgpu' | 'wasm' | 'auto' | 'cpu';
    /** Stop sequences. The protocol adds </tool_call> so generation halts
     *  once a complete tool call has been emitted. */
    stopStrings?: string[];
    /** Max tokens per generation call. */
    maxNewTokens?: number;
    /** Reported native context window. Should match the model's config. */
    maxContext?: number;
}

const DEFAULTS = {
    dtype: 'q4f16' as const,
    device: 'webgpu' as const,
    stopStrings: ['</tool_call>'],
    maxNewTokens: 2048,
    maxContext: 32_768,
};

export class TransformersJSProvider implements ChatProvider {
    readonly id: string;
    readonly label: string;
    readonly capabilities: ProviderCapabilities;

    private readonly opts: Required<TransformersJSProviderOptions>;
    private generator: TextGenerationPipeline | null = null;
    private loadPromise: Promise<TextGenerationPipeline> | null = null;
    private progressListeners = new Set<(p: ProviderProgress) => void>();
    private readonly loadProgress = new ModelLoadProgressTracker();
    /** Whether this load pass hit the network (false = served from IndexedDB). */
    private sawNetworkDownload = false;

    constructor(opts: TransformersJSProviderOptions) {
        this.opts = {
            modelId: opts.modelId,
            label: opts.label ?? opts.modelId.split('/').slice(-1)[0],
            dtype: opts.dtype ?? DEFAULTS.dtype,
            device: opts.device ?? DEFAULTS.device,
            stopStrings: opts.stopStrings ?? DEFAULTS.stopStrings,
            maxNewTokens: opts.maxNewTokens ?? DEFAULTS.maxNewTokens,
            maxContext: opts.maxContext ?? DEFAULTS.maxContext,
        };
        this.id = `transformers-js:${this.opts.modelId}`;
        this.label = this.opts.label;
        this.capabilities = {
            supportsTools: false,
            maxContext: this.opts.maxContext,
            isCached: false,
            backend: this.opts.device,
        };
    }

    countTokens(text: string): number {
        // Use the real tokenizer if we have it, else a rough estimate.
        if (this.generator?.tokenizer) {
            try {
                const ids = this.generator.tokenizer.encode(text);
                return ids.length;
            } catch {
                /* fall through */
            }
        }
        return Math.ceil(text.length / 4);
    }

    onProgress(cb: (p: ProviderProgress) => void): () => void {
        this.progressListeners.add(cb);
        return () => { this.progressListeners.delete(cb); };
    }

    private emitProgress(p: ProviderProgress): void {
        for (const l of this.progressListeners) {
            try { l(p); } catch (e) { console.error('[ai/provider] progress listener threw', e); }
        }
    }

    async ensureReady(): Promise<void> {
        await this.loadPipeline();
    }

    /** Drop the cached pipeline so the next call to ensureReady() / stream()
     *  builds a fresh one. Used by the retry path when ORT-Web's WebGPU
     *  backend gets stuck (intermittent "Invalid buffer" / failed-mapping
     *  errors). Cheap-ish — the model weights stay in IndexedDB, only the
     *  in-memory ORT session is rebuilt (~1-2s on cache hit).
     *
     *  Critically: we await pipeline.dispose() before nulling the reference.
     *  ORT-Web's GPU buffers are NOT freed by garbage collection — they
     *  must be explicitly disposed. Skipping this strands ~1.3 GB of GPU
     *  memory and the reload OOMs trying to allocate fresh weights on top
     *  of an undead session. */
    async reset(): Promise<void> {
        log.warn(`resetting pipeline for ${this.opts.modelId}`);
        const stale = this.generator;
        this.generator = null;
        this.loadPromise = null;
        this.loadProgress.reset();
        this.sawNetworkDownload = false;
        this.capabilities.isCached = false;
        if (stale) {
            try {
                await stale.dispose();
            } catch (e) {
                log.warn(`pipeline.dispose() threw (continuing): ${(e as Error).message ?? e}`);
            }
            // dispose() resolving doesn't strictly guarantee the WebGPU
            // device has released the buffers by the time we ask it to
            // allocate fresh ones. A short yield gives the browser a chance
            // to actually free. Without this, reload-after-error can OOM
            // on top of the un-freed previous session.
            await new Promise<void>(r => setTimeout(r, 100));
        }
    }

    private async loadPipeline(): Promise<TextGenerationPipeline> {
        if (this.generator) return this.generator;
        if (this.loadPromise) return this.loadPromise;

        log.info(`loading ${this.opts.modelId} device=${this.opts.device} dtype=${this.opts.dtype}`);
        this.loadProgress.reset();
        this.sawNetworkDownload = false;

        let lastFileEventAt = Date.now();
        const warmupDetail = this.opts.device === 'webgpu'
            ? 'initializing WebGPU session (this can take a minute)'
            : 'compiling WASM runtime (this can take a minute)';

        const warmupPoll = setInterval(() => {
            if (this.generator) return;
            const idleMs = Date.now() - lastFileEventAt;
            if (idleMs < 1200) return;
            const smoothed = idleMs < 4000
                ? this.loadProgress.enterWarmup(warmupDetail)
                : this.loadProgress.tickWarmup();
            this.emitProgress({ text: smoothed.text, pct: smoothed.pct });
        }, 1500);

        const stopWarmupPoll = () => clearInterval(warmupPoll);

        this.loadPromise = pipeline('text-generation', this.opts.modelId, {
            dtype: this.opts.dtype,
            device: this.opts.device,
            progress_callback: (info: unknown) => {
                lastFileEventAt = Date.now();
                const raw = info as { status?: string };
                if (raw.status === 'download') this.sawNetworkDownload = true;
                const smoothed = this.loadProgress.update(
                    info as Parameters<ModelLoadProgressTracker['update']>[0],
                );
                this.emitProgress({ text: smoothed.text, pct: smoothed.pct });
            },
        }).then((gen) => {
            stopWarmupPoll();
            this.generator = gen as TextGenerationPipeline;
            this.capabilities.isCached = true;
            const readyLabel = this.sawNetworkDownload
                ? 'model ready'
                : 'ready (loaded from cache)';
            const done = this.loadProgress.complete(readyLabel);
            this.emitProgress({ text: done.text, pct: done.pct });
            log.info(`loaded ${this.opts.modelId} cached=${!this.sawNetworkDownload}`);
            return this.generator;
        }).catch((e) => {
            stopWarmupPoll();
            this.loadPromise = null;
            log.error(`load failed: ${(e as Error).message ?? e}`);
            throw e;
        });

        return this.loadPromise;
    }

    async *stream(opts: StreamOptions): AsyncIterable<StreamEvent> {
        // Auto-retry envelope. ORT-Web's WebGPU backend has intermittent
        // "Invalid buffer" / failed-mapping errors that recover after a
        // fresh ORT session. We catch those, drop the cached pipeline,
        // reload, try once more.
        //
        // Important: we only retry if ZERO text tokens were emitted by the
        // failed attempt. Otherwise the user would see the partial output
        // from attempt N concatenated with the full output from N+1 — a
        // garbled mess. The known WebGPU error fires before any token
        // production, so this guard is rarely an obstacle but always safe.
        const maxAttempts = 2;
        for (let attempt = 1; attempt <= maxAttempts; attempt++) {
            let recoverable: Error | null = null;
            let emittedTokens = false;
            const inner = this.runOnce(opts, e => { recoverable = e; });
            for await (const ev of inner) {
                if (ev.kind === 'text') emittedTokens = true;
                if (ev.kind === 'done' && (recoverable || ev.finishReason === 'error')) {
                    // Suppress the done event of a failed attempt — we'll
                    // either retry (and emit our own done at the end) or
                    // surface the error.
                    break;
                }
                yield ev;
            }
            if (!recoverable) return;
            const canRetry = attempt < maxAttempts
                && !emittedTokens
                && isRecoverableGenerateError(recoverable as Error);
            if (canRetry) {
                log.warn(`recoverable generate failure (attempt ${attempt}/${maxAttempts}): ${(recoverable as Error).message}`);
                await this.reset();
                continue;
            }
            throw friendlyError(recoverable as Error);
        }
    }

    /** Single stream attempt. Reports a recoverable error via the callback
     *  rather than throwing so we can yield any partial output first. */
    private async *runOnce(
        opts: StreamOptions,
        onError: (e: Error) => void,
    ): AsyncIterable<StreamEvent> {
        const gen = await this.loadPipeline();
        const messages = opts.messages.map(msgToHf);

        // Bridge transformers.js's streamer-callback API into our
        // AsyncIterable of StreamEvents. The streamer fires synchronously
        // on the same task as generate(), so we queue deltas and yield
        // them via a Promise loop.
        const queue: string[] = [];
        let waiter: { resolve: (v: void) => void } | null = null;
        let finished = false;
        let aborted = false;
        let generateError: Error | null = null;

        const streamer = new TextStreamer(gen.tokenizer, {
            skip_prompt: true,
            skip_special_tokens: true,
            callback_function: (text: string) => {
                if (aborted) return;
                queue.push(text);
                if (waiter) { waiter.resolve(); waiter = null; }
            },
        });

        // Cancellation via signal — no clean abort path in transformers.js
        // generate(), but we can stop yielding deltas and mark ourselves
        // done. The model keeps running in the background; cost of v1
        // without a worker.
        const onAbort = () => {
            aborted = true;
            if (waiter) { waiter.resolve(); waiter = null; }
        };
        opts.signal?.addEventListener('abort', onAbort, { once: true });

        const generatePromise = (gen(messages as unknown as Parameters<typeof gen>[0], {
            max_new_tokens: opts.maxTokens ?? this.opts.maxNewTokens,
            do_sample: (opts.temperature ?? 0) > 0,
            temperature: opts.temperature,
            stop_strings: this.opts.stopStrings,
            streamer,
        }) as Promise<unknown>).then(() => {
            finished = true;
            if (waiter) { waiter.resolve(); waiter = null; }
        }).catch((e) => {
            finished = true;
            generateError = e instanceof Error ? e : new Error(String(e));
            log.error(`generate failed: ${(generateError as Error).message}`);
            if (waiter) { waiter.resolve(); waiter = null; }
        });

        try {
            while (true) {
                if (queue.length > 0) {
                    const delta = queue.shift()!;
                    yield { kind: 'text', delta };
                    continue;
                }
                if (finished || aborted) break;
                await new Promise<void>((resolve) => { waiter = { resolve }; });
            }
            while (queue.length > 0) {
                yield { kind: 'text', delta: queue.shift()! };
            }

            if (generateError) {
                onError(generateError);
                yield { kind: 'done', finishReason: 'error' };
                return;
            }

            const finishReason = aborted ? 'aborted' : 'stop';
            yield { kind: 'done', finishReason };
        } finally {
            opts.signal?.removeEventListener('abort', onAbort);
            void generatePromise;
        }
    }
}

/** Errors we know recover after a fresh ORT-Web session. Keeping the
 *  predicate tight so we don't mask real bugs as "transient." */
export function isRecoverableGenerateError(e: Error): boolean {
    const msg = e.message ?? '';
    return /Invalid buffer/i.test(msg)
        || /Failed to download data from buffer/i.test(msg)
        || /Mapping WebGPU buffer/i.test(msg)
        || /OrtRun/i.test(msg);
}

/** Errors that indicate the GPU is out of memory. Distinct from
 *  recoverable runtime errors — a retry won't fix this; the user has to
 *  switch to a smaller model or restart the browser. */
export function isOOMError(e: Error): boolean {
    const msg = e.message ?? '';
    return /failed to allocate a buffer/i.test(msg)
        || /out of memory/i.test(msg)
        || /OutOfMemoryError/i.test(msg)
        || /Can't create a session/i.test(msg);
}

/** Wrap raw transformers.js / ORT-Web errors with text that tells the
 *  user what to do. Preserves the original message in `.cause` for
 *  debugging. */
export function friendlyError(e: Error): Error {
    if (isOOMError(e)) {
        const wrapped = new Error(
            'Out of GPU memory loading the model. Open the AI Models tab '
            + 'and switch to a smaller model (e.g. Qwen 3 0.6B), then reload '
            + 'the page. The current model has crashed and cannot recover '
            + 'until the GPU resets.',
        );
        (wrapped as Error & { cause?: unknown }).cause = e;
        return wrapped;
    }
    return e;
}

/** Convert our Msg shape to the @huggingface/transformers Message shape.
 *  They're compatible — both have role + content — but we re-map to keep
 *  the boundary explicit. */
function msgToHf(m: Msg): { role: string; content: string } {
    return { role: m.role, content: m.content };
}
