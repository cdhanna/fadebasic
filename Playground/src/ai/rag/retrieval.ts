// High-level retrieval API used by tools, the agent's auto-retrieval, and
// tests. Bundles the embedder + index loader + search into one object so
// callers don't have to wire those together themselves.

import { Embedder } from './embedder';
import { loadDocIndex } from './index-loader';
import { topK } from './search';
import type { Chunk, DocIndex, SearchHit } from './types';

let sharedEmbedder: Embedder | null = null;
let embedderReady: Promise<void> | null = null;

export interface RetrieverOptions {
    /** Override the shared embedder — useful for tests. */
    embedder?: Embedder;
    /** Override the index — useful for tests. Skips the network fetch. */
    index?: DocIndex | null;
}

export class Retriever {
    private readonly embedder: Embedder;
    private indexOverride: DocIndex | null | undefined;

    constructor(opts: RetrieverOptions = {}) {
        if (opts.embedder) {
            this.embedder = opts.embedder;
        } else {
            if (!sharedEmbedder) sharedEmbedder = new Embedder();
            this.embedder = sharedEmbedder;
        }
        if (opts.index !== undefined) this.indexOverride = opts.index;
    }

    /** Idempotent. Pre-loads the embedder so the first query isn't slow. */
    async warm(): Promise<void> {
        if (!embedderReady) {
            embedderReady = this.embedder.ensureReady();
        }
        await embedderReady;
    }

    /** Resolve the docs index (using the override if set, otherwise loading
     *  from the network). Returns null when no index is available. */
    async getIndex(): Promise<DocIndex | null> {
        if (this.indexOverride !== undefined) return this.indexOverride;
        return loadDocIndex();
    }

    /** Search the docs index for `query`. Returns at most `k` hits, sorted
     *  by descending cosine similarity. Returns [] when no index is loaded.
     *
     *  Pass `projectType` to gate chunks that were indexed with a
     *  `projectTypes` filter (see docs-sources.mjs). A chunk with that
     *  field set is only retrievable when the active project's `type`
     *  appears in it. Always-on chunks (no `projectTypes`) pass through
     *  regardless. */
    async search(query: string, k: number = 5, opts?: { projectType?: string }): Promise<SearchHit[]> {
        const index = await this.getIndex();
        if (!index || index.chunks.length === 0) return [];
        await this.warm();
        const vec = await this.embedder.embedQuery(query);
        const chunks = filterChunksForProjectType(index.chunks, opts?.projectType);
        if (chunks.length === 0) return [];
        return topK(vec, chunks, k);
    }
}

/** Exclude chunks whose `projectTypes` gate is non-empty and doesn't include
 *  the active project type. Exported for unit tests. */
export function filterChunksForProjectType(chunks: Chunk[], projectType?: string): Chunk[] {
    let needsFilter = false;
    for (const c of chunks) {
        if (c.projectTypes && c.projectTypes.length > 0) { needsFilter = true; break; }
    }
    if (!needsFilter) return chunks;
    return chunks.filter(c => {
        if (!c.projectTypes || c.projectTypes.length === 0) return true;
        // A gated chunk with no active project type is excluded — we don't
        // know what we're in, so don't surface type-specific content.
        return !!projectType && c.projectTypes.includes(projectType);
    });
}

/** Module-level singleton — what the agent and the search_docs tool use.
 *  Tests construct their own Retriever with overrides. */
let sharedRetriever: Retriever | null = null;
export function getRetriever(): Retriever {
    if (!sharedRetriever) sharedRetriever = new Retriever();
    return sharedRetriever;
}

/** Format a list of hits into a system-prompt-friendly block. Each chunk
 *  is preceded by its citation so the model can refer back. */
export function formatHits(hits: SearchHit[]): string {
    if (hits.length === 0) return '';
    const sections = hits.map((h, i) => {
        const cite = h.chunk.heading ? `${h.chunk.source} → ${h.chunk.heading}` : h.chunk.source;
        return `[${i + 1}] ${cite} (score ${h.score.toFixed(2)}):\n${h.chunk.text}`;
    });
    return `Relevant docs:\n\n${sections.join('\n\n---\n\n')}`;
}
