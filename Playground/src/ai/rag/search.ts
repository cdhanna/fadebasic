// Cosine-similarity search over a DocIndex. BGE vectors are L2-normalized
// at embedding time, so cosine reduces to a plain dot product.

import type { Chunk, DocIndex, SearchHit } from './types.ts';

/** Dot product of two equal-length Float32Arrays. Used as cosine similarity
 *  for L2-normalized vectors (which BGE always produces). */
export function cosine(a: Float32Array, b: Float32Array): number {
    if (a.length !== b.length) return 0;
    let sum = 0;
    for (let i = 0; i < a.length; i++) sum += a[i] * b[i];
    return sum;
}

/** Top-K most similar chunks to the query vector. Returns hits sorted
 *  descending by score. */
export function topK(
    query: Float32Array,
    chunks: Chunk[],
    k: number,
    /** Optional per-source weighting — e.g. boost FadeBook docs over
     *  monogame docs. Returns a multiplier per chunk. */
    weight?: (chunk: Chunk) => number,
): SearchHit[] {
    if (k <= 0 || chunks.length === 0) return [];

    // Heap-of-K would be faster for very large indexes, but for our scale
    // (hundreds of chunks) a single sort is simpler and clearly correct.
    const scored: SearchHit[] = new Array(chunks.length);
    for (let i = 0; i < chunks.length; i++) {
        const chunk = chunks[i];
        const vec = chunk.vector as unknown as Float32Array;
        // The vector may have come from JSON.parse as number[] — wrap once
        // here. Real Float32Arrays in DocIndex (e.g. fresh from the
        // indexer) pass through unchanged.
        const queryDot = vec instanceof Float32Array
            ? cosine(query, vec)
            : cosineNumberArray(query, chunk.vector);
        let score = queryDot;
        if (weight) score *= weight(chunk);
        scored[i] = { chunk, score };
    }
    scored.sort((a, b) => b.score - a.score);
    return scored.slice(0, k);
}

function cosineNumberArray(q: Float32Array, c: number[]): number {
    if (q.length !== c.length) return 0;
    let sum = 0;
    for (let i = 0; i < q.length; i++) sum += q[i] * c[i];
    return sum;
}

/** Convenience: top-K search against an entire DocIndex with all defaults. */
export function searchIndex(
    query: Float32Array,
    index: DocIndex,
    k: number = 5,
): SearchHit[] {
    return topK(query, index.chunks, k);
}
