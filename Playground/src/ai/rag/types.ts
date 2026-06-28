// Types shared between the build-time indexer and the runtime
// search. The on-disk format (DocIndex) is the contract — if you bump it,
// regenerate the index.

export const EMBEDDING_MODEL = 'Xenova/bge-small-en-v1.5';
export const EMBEDDING_DIM = 384;
export const INDEX_VERSION = 1;

/** A single retrievable unit. Chunks are at sub-section granularity:
 *  typically one or two paragraphs under a heading. */
export interface Chunk {
    /** Stable ID for the chunk — used for de-dup and as a citation key. */
    id: string;
    /** Source path relative to the docs root, e.g. "FadeBook/Language.md". */
    source: string;
    /** Heading path joined with " > ", e.g. "Language > Variables".
     *  Used for context-priming the model and for UI citations. */
    heading: string;
    /** The chunk text itself, with markdown preserved. No BGE prefix here —
     *  the indexer adds "passage: " when embedding. */
    text: string;
    /** Length of `text` in characters — fast token-budget estimate. */
    chars: number;
    /** 384-dim L2-normalized BGE embedding. Stored as plain number[] so the
     *  JSON parser can hydrate it directly; convert to Float32Array on use. */
    vector: number[];
    /** Optional gate: when set, this chunk is only included in retrieval if
     *  the active project's `type` is one of these values. Omitted/empty
     *  means "always include". Set by build-docs-index.mjs from the source
     *  config in docs-sources.mjs. */
    projectTypes?: string[];
}

/** Top-level shape of the docs-index.json file. */
export interface DocIndex {
    version: number;
    /** Model identifier — runtime MUST embed queries with the same model. */
    model: string;
    /** Embedding dimension. Sanity-check vs vectors. */
    dim: number;
    /** Build timestamp (ISO). */
    builtAt: string;
    /** Total source docs scanned. */
    sourceCount: number;
    /** Chunks, ordered roughly by source then position. */
    chunks: Chunk[];
}

export interface SearchHit {
    chunk: Chunk;
    /** Cosine similarity, [0, 1]. Higher = more relevant. */
    score: number;
}
