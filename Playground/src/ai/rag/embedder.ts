// Embedder wrapper around @huggingface/transformers' feature-extraction
// pipeline. BGE models require asymmetric prefixes — "query: " for the
// query at retrieval time, "passage: " for the indexed chunks — and
// skipping that quietly degrades retrieval. The wrapper enforces them so
// callers can't forget.
//
// The same code runs at build time (Node, model cached in ~/.cache/huggingface)
// and at runtime (browser, model cached in IndexedDB via transformers.js).
// We just pass through the device choice and let transformers.js pick the
// right backend.

import { pipeline, type FeatureExtractionPipeline } from '@huggingface/transformers';
import { EMBEDDING_DIM, EMBEDDING_MODEL } from './types.ts';

export type EmbedderDevice = 'webgpu' | 'wasm' | 'auto' | 'cpu';
export type EmbedderDtype = 'auto' | 'fp32' | 'fp16' | 'q8' | 'int8' | 'uint8' | 'q4' | 'bnb4' | 'q4f16';

export interface EmbedderOptions {
    /** Override the default BGE model. Embedding dim must match
     *  EMBEDDING_DIM (384) or downstream consumers will break. */
    modelId?: string;
    /** Backend selection. Browser default 'webgpu', Node default 'cpu'. */
    device?: EmbedderDevice;
    /** Quantization. q4f16 is the WebGPU sweet spot, fp32 for CPU. */
    dtype?: EmbedderDtype;
    /** Optional progress callback during model load. */
    onProgress?: (info: { status?: string; file?: string; progress?: number }) => void;
}

export class Embedder {
    private readonly opts: Required<Omit<EmbedderOptions, 'onProgress'>> & Pick<EmbedderOptions, 'onProgress'>;
    private extractor: FeatureExtractionPipeline | null = null;
    private loadPromise: Promise<FeatureExtractionPipeline> | null = null;

    constructor(opts: EmbedderOptions = {}) {
        // Browser default is WASM, not WebGPU. Why: when the LLM provider
        // also uses WebGPU (which it does by default), ONNX Runtime Web has
        // contention issues sharing GPU state across sessions — manifests
        // as "Failed to download data from buffer: Invalid buffer" right
        // after the embedder runs. Embedder is tiny (30MB, 384-dim output)
        // and WASM is plenty fast for it (~80ms/query), so we cede the GPU
        // to the LLM. Callers can override with device: 'webgpu' if they
        // know their stack handles it.
        this.opts = {
            modelId: opts.modelId ?? EMBEDDING_MODEL,
            device: opts.device ?? (isNode() ? 'cpu' : 'wasm'),
            dtype: opts.dtype ?? (isNode() ? 'fp32' : 'q8'),
            onProgress: opts.onProgress,
        };
    }

    async ensureReady(): Promise<void> {
        await this.loadExtractor();
    }

    private async loadExtractor(): Promise<FeatureExtractionPipeline> {
        if (this.extractor) return this.extractor;
        if (this.loadPromise) return this.loadPromise;

        this.loadPromise = pipeline('feature-extraction', this.opts.modelId, {
            device: this.opts.device,
            dtype: this.opts.dtype,
            progress_callback: this.opts.onProgress as (info: unknown) => void,
        }).then((p) => {
            this.extractor = p as FeatureExtractionPipeline;
            return this.extractor;
        });
        return this.loadPromise;
    }

    /** Embed one or more strings as queries — prepends "query: " to each.
     *  Returns L2-normalized vectors so cosine similarity is just dot product. */
    async embedQuery(text: string): Promise<Float32Array>;
    async embedQuery(texts: string[]): Promise<Float32Array[]>;
    async embedQuery(input: string | string[]): Promise<Float32Array | Float32Array[]> {
        return this.embed(input, 'query: ');
    }

    /** Embed one or more strings as passages — prepends "passage: " to each. */
    async embedPassage(text: string): Promise<Float32Array>;
    async embedPassage(texts: string[]): Promise<Float32Array[]>;
    async embedPassage(input: string | string[]): Promise<Float32Array | Float32Array[]> {
        return this.embed(input, 'passage: ');
    }

    private async embed(
        input: string | string[],
        prefix: string,
    ): Promise<Float32Array | Float32Array[]> {
        const extractor = await this.loadExtractor();
        const isBatch = Array.isArray(input);
        const arr = isBatch ? input : [input];
        const prefixed = arr.map(t => prefix + t);

        const output = await extractor(prefixed, {
            pooling: 'mean',
            normalize: true,
        });

        // output.data is a flat Float32Array of [batch * dim].
        const data = output.data as Float32Array;
        const dim = output.dims?.[output.dims.length - 1] ?? EMBEDDING_DIM;
        if (dim !== EMBEDDING_DIM) {
            throw new Error(`Embedder produced ${dim}-dim vectors; expected ${EMBEDDING_DIM}. Model mismatch?`);
        }

        const out: Float32Array[] = [];
        for (let i = 0; i < arr.length; i++) {
            out.push(data.slice(i * dim, (i + 1) * dim));
        }
        return isBatch ? out : out[0];
    }
}

function isNode(): boolean {
    return typeof globalThis !== 'undefined'
        && typeof (globalThis as { process?: { versions?: { node?: string } } }).process?.versions?.node === 'string';
}
