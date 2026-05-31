// OPFS-backed cache for compiled assets. Keys combine the source bytes'
// SHA-256, the chosen format, and the encoder version — so changing any
// of them invalidates cleanly.
//
// Layout under the active project's OPFS root:
//
//   .fade-cache/
//     index.json                 ← entry metadata (assetName, hash, fmt, ver)
//     blobs/<hash>.<fmt>.<ver>.xnb
//
// Stale blobs (no matching index entry, or hash no longer present in the
// project) get swept by `garbageCollect` when the next compile pass runs.

// (No type imports — the cache is intentionally format-agnostic now.)

export const CACHE_DIR = '.fade-cache';
export const CACHE_INDEX_PATH = `${CACHE_DIR}/index.json`;
export const CACHE_BLOBS_DIR = `${CACHE_DIR}/blobs`;

export interface AssetCacheEntry {
    assetName: string;
    sourcePath: string;
    sourceHash: string;
    /** Format string — `TextureCompression` ('color', 'dxt5', …) for
     *  image assets, `AudioCompression` ('pcm', …) for audio assets. The
     *  cache itself is type-agnostic; the format string is just one more
     *  key dimension. Stored as plain string so a new asset kind can be
     *  added without changing the schema. */
    format: string;
    encoderVersion: number;
    /** Dimensions for image assets. Zero for audio (carry duration via
     *  the metadata field instead). Kept on the struct so the
     *  CompiledAsset shape stays stable. */
    width: number;
    height: number;
    blobPath: string;
    /** Free-form per-asset-kind metadata. For audio: `{sampleRate,
     *  channels, duration}`. Optional so legacy entries (pre-audio
     *  rollout) still parse. */
    metadata?: Record<string, number | string>;
    /** ISO timestamp; the GC pass uses it for LRU pruning if we ever
     *  bound the cache. Not load-bearing today. */
    compiledAt: string;
}

interface AssetCacheIndex {
    schemaVersion: 1;
    entries: AssetCacheEntry[];
}

const EMPTY_INDEX: AssetCacheIndex = { schemaVersion: 1, entries: [] };

// Minimal workspace surface we need. Lets tests inject an in-memory impl
// without dragging in OpfsWorkspace.
export interface CacheWorkspaceLike {
    list(): Promise<string[]>;
    read(path: string): Promise<string>;
    write(path: string, content: string): Promise<void>;
    readBytes(path: string): Promise<Uint8Array>;
    writeBytes(path: string, bytes: Uint8Array): Promise<void>;
    delete(path: string): Promise<void>;
    mkdir(path: string): Promise<void>;
}

export class AssetCache {
    constructor(private readonly workspace: CacheWorkspaceLike) {}

    private async readIndex(): Promise<AssetCacheIndex> {
        try {
            const text = await this.workspace.read(CACHE_INDEX_PATH);
            const parsed = JSON.parse(text) as AssetCacheIndex;
            if (parsed?.schemaVersion === 1 && Array.isArray(parsed.entries)) return parsed;
        } catch { /* missing or malformed → start fresh */ }
        return { ...EMPTY_INDEX, entries: [] };
    }

    private async writeIndex(index: AssetCacheIndex): Promise<void> {
        await this.workspace.mkdir(CACHE_DIR);
        await this.workspace.write(CACHE_INDEX_PATH, JSON.stringify(index, null, 2) + '\n');
    }

    /** Look up a cache entry by (assetName, sourceHash, format,
     *  encoderVersion). Returns the entry plus its bytes, or null when
     *  any key field doesn't match — or the blob is missing on disk. */
    async lookup(
        assetName: string,
        sourceHash: string,
        format: string,
        encoderVersion: number,
    ): Promise<{ entry: AssetCacheEntry; bytes: Uint8Array } | null> {
        const index = await this.readIndex();
        const entry = index.entries.find(
            (e) =>
                e.assetName === assetName &&
                e.sourceHash === sourceHash &&
                e.format === format &&
                e.encoderVersion === encoderVersion,
        );
        if (!entry) return null;
        try {
            const bytes = await this.workspace.readBytes(entry.blobPath);
            return { entry, bytes };
        } catch { return null; /* blob disappeared; treat as miss */ }
    }

    async store(
        assetName: string,
        sourcePath: string,
        sourceHash: string,
        format: string,
        encoderVersion: number,
        bytes: Uint8Array,
        width: number,
        height: number,
        metadata?: Record<string, number | string>,
    ): Promise<AssetCacheEntry> {
        await this.workspace.mkdir(CACHE_BLOBS_DIR);
        const blobPath = `${CACHE_BLOBS_DIR}/${sourceHash}.${format}.v${encoderVersion}.xnb`;
        await this.workspace.writeBytes(blobPath, bytes);

        const index = await this.readIndex();
        // Replace any prior entry for the same assetName — there's only
        // ever one live compilation per asset at a time, so this keeps
        // the index small and the GC simple.
        const filtered = index.entries.filter((e) => e.assetName !== assetName);
        const entry: AssetCacheEntry = {
            assetName,
            sourcePath,
            sourceHash,
            format,
            encoderVersion,
            width,
            height,
            blobPath,
            metadata,
            compiledAt: new Date().toISOString(),
        };
        filtered.push(entry);
        await this.writeIndex({ schemaVersion: 1, entries: filtered });
        return entry;
    }

    /** Remove cache entries whose source no longer exists in the
     *  workspace and delete any orphaned blobs under blobs/. Called by
     *  the compile pass after it knows the live source set. */
    async garbageCollect(liveSourcePaths: Set<string>): Promise<void> {
        const index = await this.readIndex();
        const keep = index.entries.filter((e) => liveSourcePaths.has(e.sourcePath));
        const removed = index.entries.filter((e) => !liveSourcePaths.has(e.sourcePath));
        for (const e of removed) {
            try { await this.workspace.delete(e.blobPath); } catch { /* already gone */ }
        }
        // Drop blobs that aren't referenced anywhere — guards against
        // stale entries from crashed writes.
        try {
            const all = await this.workspace.list();
            const referenced = new Set(keep.map((e) => e.blobPath));
            for (const path of all) {
                if (!path.startsWith(CACHE_BLOBS_DIR + '/')) continue;
                if (!referenced.has(path)) {
                    try { await this.workspace.delete(path); } catch { /* ignore */ }
                }
            }
        } catch { /* listing failed — index-side GC is still correct */ }
        if (keep.length !== index.entries.length) {
            await this.writeIndex({ schemaVersion: 1, entries: keep });
        }
    }
}

/** SHA-256 of a byte buffer, hex-encoded. Uses the browser's WebCrypto. */
export async function sha256Hex(bytes: Uint8Array): Promise<string> {
    const digest = await crypto.subtle.digest('SHA-256', bytes as BufferSource);
    const view = new Uint8Array(digest);
    let hex = '';
    for (let i = 0; i < view.length; i++) hex += view[i].toString(16).padStart(2, '0');
    return hex;
}
