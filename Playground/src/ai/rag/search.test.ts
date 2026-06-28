import { describe, it, expect } from 'vitest';
import { cosine, topK, searchIndex } from './search';
import type { Chunk, DocIndex } from './types';

function vec(...values: number[]): Float32Array {
    return new Float32Array(values);
}

function normalize(v: Float32Array): Float32Array {
    let sum = 0;
    for (const x of v) sum += x * x;
    const norm = Math.sqrt(sum) || 1;
    return v.map(x => x / norm) as Float32Array;
}

function mkChunk(id: string, v: number[]): Chunk {
    const normalized = Array.from(normalize(new Float32Array(v)));
    return {
        id,
        source: 'x.md',
        heading: '',
        text: id,
        chars: id.length,
        vector: normalized,
    };
}

describe('cosine', () => {
    it('returns dot product for normalized vectors', () => {
        expect(cosine(vec(1, 0), vec(1, 0))).toBeCloseTo(1);
        expect(cosine(vec(1, 0), vec(0, 1))).toBeCloseTo(0);
        expect(cosine(vec(1, 0), vec(-1, 0))).toBeCloseTo(-1);
    });

    it('returns 0 when lengths differ', () => {
        expect(cosine(vec(1, 2), vec(1, 2, 3))).toBe(0);
    });
});

describe('topK', () => {
    it('returns the K most similar chunks sorted by score', () => {
        const chunks = [
            mkChunk('A', [1, 0, 0]),
            mkChunk('B', [0, 1, 0]),
            mkChunk('C', [0.9, 0.1, 0]),  // most similar to query
            mkChunk('D', [-1, 0, 0]),
        ];
        const query = normalize(vec(1, 0, 0));
        const hits = topK(query, chunks, 2);
        expect(hits).toHaveLength(2);
        expect(hits[0].chunk.id).toBe('A');
        expect(hits[1].chunk.id).toBe('C');
        expect(hits[0].score).toBeGreaterThan(hits[1].score);
    });

    it('honors the K bound', () => {
        const chunks = [
            mkChunk('A', [1, 0]),
            mkChunk('B', [0, 1]),
            mkChunk('C', [0.5, 0.5]),
        ];
        const query = normalize(vec(1, 0));
        expect(topK(query, chunks, 1)).toHaveLength(1);
        expect(topK(query, chunks, 10)).toHaveLength(3);
    });

    it('returns [] for empty input', () => {
        expect(topK(vec(1, 0), [], 5)).toEqual([]);
        expect(topK(vec(1, 0), [mkChunk('A', [1, 0])], 0)).toEqual([]);
    });

    it('handles number[] vector storage (post-JSON-parse) just like Float32Array', () => {
        const chunk = mkChunk('A', [1, 0, 0]);
        // Simulate JSON.parse result — chunk.vector is already number[] in
        // our DocIndex shape, but the runtime path should not require a
        // pre-conversion to Float32Array.
        expect(Array.isArray(chunk.vector)).toBe(true);
        const hits = topK(normalize(vec(1, 0, 0)), [chunk], 1);
        expect(hits[0].score).toBeCloseTo(1);
    });

    it('applies the optional weighting function', () => {
        const chunks = [
            { ...mkChunk('A', [1, 0]), source: 'low.md' },
            { ...mkChunk('B', [0.9, 0.1]), source: 'high.md' },
        ];
        const query = normalize(vec(1, 0));
        const hits = topK(query, chunks, 2, c => c.source === 'high.md' ? 2 : 1);
        // B's raw score is lower but the 2x weight should flip the order
        expect(hits[0].chunk.source).toBe('high.md');
    });
});

describe('searchIndex', () => {
    it('queries an entire DocIndex with default K=5', () => {
        const index: DocIndex = {
            version: 1,
            model: 'test',
            dim: 2,
            builtAt: 'now',
            sourceCount: 1,
            chunks: [mkChunk('A', [1, 0]), mkChunk('B', [0, 1])],
        };
        const hits = searchIndex(normalize(vec(1, 0)), index);
        expect(hits[0].chunk.id).toBe('A');
    });
});
