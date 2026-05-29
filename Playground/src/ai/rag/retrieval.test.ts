import { describe, it, expect } from 'vitest';
import { filterChunksForProjectType } from './retrieval';
import type { Chunk } from './types';

function mkChunk(id: string, projectTypes?: string[]): Chunk {
    return {
        id,
        source: `${id}.md`,
        heading: '',
        text: id,
        chars: id.length,
        vector: [],
        ...(projectTypes ? { projectTypes } : {}),
    };
}

describe('filterChunksForProjectType', () => {
    it('passes everything through when no chunk has a projectTypes gate', () => {
        const chunks = [mkChunk('a'), mkChunk('b'), mkChunk('c')];
        expect(filterChunksForProjectType(chunks, 'web')).toBe(chunks);          // identity — fast path
        expect(filterChunksForProjectType(chunks, undefined)).toBe(chunks);
    });

    it('includes always-on chunks plus matching gated chunks', () => {
        const chunks = [
            mkChunk('FadeBook'),
            mkChunk('MonoGame', ['monogame']),
            mkChunk('Web', ['web']),
        ];
        const ids = filterChunksForProjectType(chunks, 'monogame').map(c => c.id);
        expect(ids).toEqual(['FadeBook', 'MonoGame']);
    });

    it('excludes gated chunks when no projectType is supplied', () => {
        // A chat with no active project shouldn't surface MonoGame-only docs.
        const chunks = [mkChunk('FadeBook'), mkChunk('MonoGame', ['monogame'])];
        const ids = filterChunksForProjectType(chunks, undefined).map(c => c.id);
        expect(ids).toEqual(['FadeBook']);
    });

    it('supports multiple allowed project types per chunk', () => {
        const chunks = [mkChunk('shared', ['monogame', 'web'])];
        expect(filterChunksForProjectType(chunks, 'web').map(c => c.id)).toEqual(['shared']);
        expect(filterChunksForProjectType(chunks, 'monogame').map(c => c.id)).toEqual(['shared']);
        expect(filterChunksForProjectType(chunks, 'other').map(c => c.id)).toEqual([]);
    });

    it('treats an empty projectTypes array as always-on', () => {
        const chunks = [mkChunk('weird', [])];
        expect(filterChunksForProjectType(chunks, undefined).map(c => c.id)).toEqual(['weird']);
        expect(filterChunksForProjectType(chunks, 'web').map(c => c.id)).toEqual(['weird']);
    });
});
