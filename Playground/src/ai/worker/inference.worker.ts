// Dedicated worker for transformers.js inference. Keeps ONNX/WebGPU load +
// token generation off the main thread so the editor stays responsive.

import { TransformersJSProvider, type TransformersJSProviderOptions } from '../providers/transformers-js';
import type { Msg, StreamEvent, StreamOptions } from '../providers/types';

interface WorkerRequest {
    type: string;
    requestId?: number;
    streamId?: number;
    opts?: TransformersJSProviderOptions;
    messages?: Msg[];
    streamOpts?: Pick<StreamOptions, 'maxTokens' | 'temperature'>;
    text?: string;
}

let provider: TransformersJSProvider | null = null;
const abortControllers = new Map<number, AbortController>();

function post(msg: unknown): void {
    self.postMessage(msg);
}

self.onmessage = async (ev: MessageEvent<WorkerRequest>) => {
    const msg = ev.data;
    try {
        switch (msg.type) {
            case 'load': {
                if (provider) await provider.reset();
                if (!msg.opts) throw new Error('load requires opts');
                provider = new TransformersJSProvider(msg.opts);
                provider.onProgress(({ text, pct }) => {
                    post({ type: 'progress', text, pct });
                });
                await provider.ensureReady();
                post({ type: 'load-done', requestId: msg.requestId });
                break;
            }
            case 'reset': {
                if (provider) await provider.reset();
                provider = null;
                post({ type: 'reset-done', requestId: msg.requestId });
                break;
            }
            case 'count-tokens': {
                if (!provider) throw new Error('provider not loaded');
                const count = provider.countTokens(msg.text ?? '');
                post({ type: 'count-tokens-result', requestId: msg.requestId, count });
                break;
            }
            case 'stream': {
                if (!provider) throw new Error('provider not loaded');
                const streamId = msg.streamId!;
                const ac = new AbortController();
                abortControllers.set(streamId, ac);
                const opts: StreamOptions = {
                    messages: msg.messages ?? [],
                    maxTokens: msg.streamOpts?.maxTokens,
                    temperature: msg.streamOpts?.temperature,
                    signal: ac.signal,
                };
                try {
                    for await (const event of provider.stream(opts)) {
                        post({ type: 'stream-event', streamId, event });
                        if (event.kind === 'done') break;
                    }
                } finally {
                    abortControllers.delete(streamId);
                    post({ type: 'stream-end', streamId });
                }
                break;
            }
            case 'abort': {
                const ac = abortControllers.get(msg.streamId!);
                ac?.abort();
                break;
            }
            default:
                throw new Error(`unknown worker message: ${msg.type}`);
        }
    } catch (e) {
        const message = (e as Error).message ?? String(e);
        if (msg.type === 'stream') {
            post({
                type: 'stream-event',
                streamId: msg.streamId,
                event: { kind: 'done', finishReason: 'error' } satisfies StreamEvent,
            });
            post({ type: 'stream-error', streamId: msg.streamId, message });
            post({ type: 'stream-end', streamId: msg.streamId });
        } else {
            post({ type: 'error', requestId: msg.requestId, message });
        }
    }
};
