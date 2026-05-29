// Runtime loader for docs-index.json. Fetches once, caches in module state,
// and returns null gracefully when the file is missing — so the agent still
// works when the index hasn't been built yet.

import { getLogger } from '../../log-bus';
import { EMBEDDING_DIM, EMBEDDING_MODEL, type DocIndex } from './types';

const log = getLogger('ai/rag');

const DEFAULT_URL = '/docs-index.json';

let cached: DocIndex | null | undefined; // undefined = not attempted, null = attempted-and-missing
let loadPromise: Promise<DocIndex | null> | null = null;

export interface LoadOptions {
    /** URL to fetch the index from. Defaults to /docs-index.json. */
    url?: string;
    /** Force a re-fetch even if cached. Useful for dev. */
    force?: boolean;
}

/** Load the docs index. Returns null if the file is missing or invalid;
 *  log entries explain why. Safe to call repeatedly. */
export async function loadDocIndex(opts: LoadOptions = {}): Promise<DocIndex | null> {
    if (!opts.force && cached !== undefined) return cached;
    if (loadPromise) return loadPromise;

    const url = opts.url ?? DEFAULT_URL;
    loadPromise = (async () => {
        try {
            const res = await fetch(url);
            if (!res.ok) {
                log.warn(`docs index fetch failed: ${res.status} ${res.statusText} — RAG disabled`);
                cached = null;
                return null;
            }
            const ct = res.headers.get('content-type') ?? '';
            if (!ct.includes('json')) {
                log.warn(`docs index unexpected content-type "${ct}" — RAG disabled`);
                cached = null;
                return null;
            }
            const json = await res.json() as DocIndex;
            const validation = validate(json);
            if (!validation.ok) {
                log.warn(`docs index invalid: ${validation.reason} — RAG disabled`);
                cached = null;
                return null;
            }
            log.info(`docs index loaded: ${json.chunks.length} chunks, ${json.sourceCount} sources`);
            cached = json;
            return json;
        } catch (e) {
            log.warn(`docs index load threw: ${(e as Error).message} — RAG disabled`);
            cached = null;
            return null;
        } finally {
            loadPromise = null;
        }
    })();
    return loadPromise;
}

/** Manually inject an index. Used by tests and by the indexer when running
 *  build-time. */
export function setDocIndex(index: DocIndex | null): void {
    cached = index;
    loadPromise = null;
}

/** Synchronously check whether an index is loaded. */
export function getLoadedDocIndex(): DocIndex | null {
    return cached ?? null;
}

function validate(idx: DocIndex): { ok: true } | { ok: false; reason: string } {
    if (!idx || typeof idx !== 'object') return { ok: false, reason: 'not an object' };
    if (idx.model !== EMBEDDING_MODEL) {
        return { ok: false, reason: `model mismatch (have ${idx.model}, need ${EMBEDDING_MODEL})` };
    }
    if (idx.dim !== EMBEDDING_DIM) {
        return { ok: false, reason: `dim mismatch (have ${idx.dim}, need ${EMBEDDING_DIM})` };
    }
    if (!Array.isArray(idx.chunks)) return { ok: false, reason: 'chunks not an array' };
    return { ok: true };
}
