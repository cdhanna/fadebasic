// Client for the FadeLand Catalog (separate repo, hosted by jsDelivr).
// See https://github.com/cdhanna/FadeLandAssets for the source of truth.
//
// On open:
//   1. Fetch /manifest.json (tiny — ~2 KB). It carries the build "version" and
//      paths to every other JSON file.
//   2. Compare version to what's cached in IndexedDB. If unchanged, serve all
//      subsequent queries from cache. If changed, refetch indices + shards.
//
// What's cached: manifest, the four index files, all shards, every pack manifest
// the user has opened. Asset blobs themselves are NOT cached here — the browser's
// HTTP cache + jsDelivr's CDN handle that, and content-addressed URLs make
// invalidation a non-issue.
//
// Search / filtering happens against the in-memory index data; this module just
// loads and serves it. See catalog-panel.ts for the UI side.

export interface CatalogManifest {
    schema: number;
    version: string;
    builtAt: string;
    entryCount: number;
    packCount: number;
    remoteCount: number;
    mirroredCount: number;
    localCount: number;
    shardCount: number;
    paths: {
        tags: string;
        trigrams: string;
        facets: string;
        idMap: string;
        shards: Record<string, string>;
        blobsBase: string;
        thumbsBase: string;
        packsBase: string;
    };
    checksums: Record<string, unknown>;
}

export interface CatalogEntry {
    id: number;
    slug: string;
    name: string;
    kind: 'asset' | 'pack';
    description: string | null;
    mime: string;
    bytes: number;
    hosting: 'remote' | 'mirrored' | 'local';
    url: string;                         // dist-relative or absolute
    originalUrl: string | null;          // upstream URL when hosting='mirrored'
    sha256: string;
    thumb: string | null;
    tags: string[];
    license: string;
    attribution: string | null;
    homepage: string | null;
    added: string | null;

    // Asset-only
    width?: number | null;
    height?: number | null;
    durationSec?: number | null;
    sampleRate?: number | null;
    channels?: number | null;

    // Pack-only
    packManifest?: string;
    fileCount?: number;
    totalExtractedBytes?: number;
    imageCount?: number;
    audioCount?: number;
    fontCount?: number;
}

export interface CatalogPackFile {
    path: string;
    sha256: string;
    bytes: number;
    mime: string;
    width?: number;
    height?: number;
    hasAlpha?: boolean;
    durationSec?: number | null;
    sampleRate?: number | null;
    channels?: number | null;
}

export interface CatalogPackManifest {
    schema: number;
    zipSha256: string;
    zipUrl: string;
    zipBytes: number;
    summary: { fileCount: number; totalExtractedBytes: number; images: number; audio: number };
    files: CatalogPackFile[];
}

export interface CatalogTagIndex { schema: number; tags: Record<string, number[]>; }
export interface CatalogFacets { schema: number; dimensions: Record<string, number>; mime: Record<string, number>; license: Record<string, number>; hosting: Record<string, number>; kind: Record<string, number>; }

export interface CatalogClientOptions {
    /** jsDelivr base for immutable artifacts (indices, shards, blobs, thumbs).
     *  These files are version-suffixed so jsDelivr's long edge cache is
     *  actually CORRECT for them — same URL always means same content.
     *  Example: https://cdn.jsdelivr.net/gh/cdhanna/FadeLandAssets@main/dist/ */
    baseUrl: string;
    /** URL for manifest.json — the one file in the catalog whose CONTENT
     *  changes while its URL stays the same. jsDelivr caches branch URLs
     *  for ~12 hours and ignores query-string cache busters, which makes
     *  catalog updates invisible for far too long. So we fetch the
     *  manifest from GitHub raw instead (5-minute cache, CORS-safe). All
     *  the version-suffixed files referenced INSIDE the manifest still
     *  come from jsDelivr — they're new URLs after every build, so the
     *  CDN serves them fresh on first request. */
    manifestUrl: string;
}

const DEFAULT_BASE = 'https://cdn.jsdelivr.net/gh/cdhanna/FadeLandAssets@main/dist/';
const DEFAULT_MANIFEST_URL = 'https://raw.githubusercontent.com/cdhanna/FadeLandAssets/main/dist/manifest.json';
const IDB_NAME = 'fade-catalog';
const IDB_VERSION = 1;

// IndexedDB store names. Kept generic so a future schema bump can rename keys
// without touching every call site.
const STORE_META = 'meta';
const STORE_INDEX = 'indices';
const STORE_SHARDS = 'shards';
const STORE_PACKS = 'packs';

interface IdbCache {
    get<T = unknown>(store: string, key: string): Promise<T | undefined>;
    put(store: string, key: string, value: unknown): Promise<void>;
    clearAll(): Promise<void>;
}

async function openIdb(): Promise<IdbCache> {
    const db: IDBDatabase = await new Promise((resolve, reject) => {
        const req = indexedDB.open(IDB_NAME, IDB_VERSION);
        req.onupgradeneeded = () => {
            const d = req.result;
            for (const name of [STORE_META, STORE_INDEX, STORE_SHARDS, STORE_PACKS]) {
                if (!d.objectStoreNames.contains(name)) d.createObjectStore(name);
            }
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });

    function tx<T>(stores: string[], mode: IDBTransactionMode, fn: (t: IDBTransaction) => Promise<T> | T): Promise<T> {
        return new Promise<T>((resolve, reject) => {
            const t = db.transaction(stores, mode);
            const result = Promise.resolve(fn(t));
            t.oncomplete = () => result.then(resolve, reject);
            t.onerror = () => reject(t.error);
            t.onabort = () => reject(t.error);
        });
    }

    return {
        get<T = unknown>(store: string, key: string) {
            return tx<T | undefined>([store], 'readonly', (t) =>
                new Promise<T | undefined>((res, rej) => {
                    const r = t.objectStore(store).get(key);
                    r.onsuccess = () => res(r.result as T | undefined);
                    r.onerror = () => rej(r.error);
                }),
            );
        },
        put(store: string, key: string, value: unknown) {
            return tx<void>([store], 'readwrite', (t) =>
                new Promise<void>((res, rej) => {
                    const r = t.objectStore(store).put(value, key);
                    r.onsuccess = () => res();
                    r.onerror = () => rej(r.error);
                }),
            );
        },
        clearAll() {
            return tx<void>([STORE_META, STORE_INDEX, STORE_SHARDS, STORE_PACKS], 'readwrite', (t) =>
                new Promise<void>((res, rej) => {
                    let remaining = 4;
                    const done = () => { if (--remaining === 0) res(); };
                    for (const name of [STORE_META, STORE_INDEX, STORE_SHARDS, STORE_PACKS]) {
                        const r = t.objectStore(name).clear();
                        r.onsuccess = done;
                        r.onerror = () => rej(r.error);
                    }
                }),
            );
        },
    };
}

export class CatalogClient {
    private readonly baseUrl: string;
    private readonly manifestUrl: string;
    private idb: IdbCache | null = null;

    private manifest: CatalogManifest | null = null;
    private entries: CatalogEntry[] = [];
    private byId = new Map<number, CatalogEntry>();
    private tagIndex: CatalogTagIndex | null = null;
    private facets: CatalogFacets | null = null;
    private packCache = new Map<string, CatalogPackManifest>();

    constructor(opts: Partial<CatalogClientOptions> = {}) {
        const base = opts.baseUrl ?? DEFAULT_BASE;
        this.baseUrl = base.endsWith('/') ? base : base + '/';
        this.manifestUrl = opts.manifestUrl ?? DEFAULT_MANIFEST_URL;
    }

    /** Load (or refresh) the catalog. Returns immediately if already loaded
     *  and the upstream version matches. Force=true skips the cache. */
    async load(force = false): Promise<void> {
        if (!this.idb) this.idb = await openIdb();

        // Fetch the manifest from GitHub raw (NOT jsDelivr). The `?ts=` is a
        // belt-and-suspenders cache buster — GitHub raw respects it, and even
        // if it didn't the cache is only 5 minutes.
        const fresh = await this.fetchJson<CatalogManifest>(`${this.manifestUrl}?ts=${Date.now()}`);
        const cachedVersion = await this.idb.get<string>(STORE_META, 'version');

        if (!force && cachedVersion === fresh.version && this.manifest?.version === fresh.version) {
            return; // already in memory, version matches
        }

        const isUpgrade = cachedVersion !== fresh.version;
        if (isUpgrade) {
            // Version changed → all the version-suffixed files in IDB are stale.
            await this.idb.clearAll();
            this.packCache.clear();
        }

        // Fetch indices + shards in parallel.
        const [tags, facets, ...shards] = await Promise.all([
            this.fetchJson<CatalogTagIndex>(fresh.paths.tags),
            this.fetchJson<CatalogFacets>(fresh.paths.facets),
            ...Object.values(fresh.paths.shards).map(p => this.fetchJson<{ schema: number; entries: Record<string, CatalogEntry> }>(p)),
        ]);

        // Cache to IDB.
        await this.idb.put(STORE_META, 'version', fresh.version);
        await this.idb.put(STORE_META, 'manifest', fresh);
        await this.idb.put(STORE_INDEX, 'tags', tags);
        await this.idb.put(STORE_INDEX, 'facets', facets);
        const shardKeys = Object.keys(fresh.paths.shards);
        await Promise.all(shards.map((s, i) => this.idb!.put(STORE_SHARDS, shardKeys[i], s)));

        this.manifest = fresh;
        this.tagIndex = tags;
        this.facets = facets;
        this.entries = [];
        this.byId.clear();
        for (const shard of shards) {
            for (const e of Object.values(shard.entries)) {
                this.entries.push(e);
                this.byId.set(e.id, e);
            }
        }
        this.entries.sort((a, b) => a.name.localeCompare(b.name));
    }

    getManifest(): CatalogManifest {
        if (!this.manifest) throw new Error('CatalogClient.load() must be called before getManifest()');
        return this.manifest;
    }

    getEntries(): readonly CatalogEntry[] { return this.entries; }
    getEntry(id: number): CatalogEntry | undefined { return this.byId.get(id); }
    getFacets(): CatalogFacets | null { return this.facets; }
    getTagIndex(): CatalogTagIndex | null { return this.tagIndex; }

    /** Keyword search over name/slug/description/tags, optionally constrained
     *  by kind and a category of asset (image/audio/font). Mirrors the
     *  substring + tag matching the Catalog panel uses, exposed so the AI
     *  agent (and anything else) can query programmatically. Results are the
     *  already-name-sorted entries, sliced to `limit`. */
    search(query: string, opts: {
        kind?: 'asset' | 'pack';
        category?: 'image' | 'audio' | 'font';
        tags?: string[];
        limit?: number;
    } = {}): CatalogEntry[] {
        const q = query.trim().toLowerCase();
        const limit = opts.limit ?? 12;
        const wantTags = (opts.tags ?? []).map(t => t.toLowerCase());

        const passesFilters = (e: CatalogEntry): boolean => {
            if (opts.kind && e.kind !== opts.kind) return false;
            if (opts.category && !e.mime.startsWith(`${opts.category}/`)) return false;
            if (wantTags.length && !wantTags.every(t => e.tags.some(et => et.toLowerCase() === t))) return false;
            return true;
        };
        const hayOf = (e: CatalogEntry) =>
            `${e.name} ${e.slug} ${e.description ?? ''} ${e.tags.join(' ')}`.toLowerCase();

        // 1. Full-phrase substring match.
        let out = this.entries.filter(e => passesFilters(e) && (!q || hayOf(e).includes(q)));
        // 2. Fall back to ANY-word match — models phrase queries loosely
        //    ("spaceship sprite for my game") and the exact phrase rarely hits.
        if (out.length === 0 && q) {
            const words = q.split(/\s+/).filter(w => w.length > 2);
            if (words.length) {
                out = this.entries.filter(e => passesFilters(e) && words.some(w => hayOf(e).includes(w)));
            }
        }
        return out.slice(0, limit);
    }

    /** A browse sample (no query) — entries matching just the filters, for
     *  "show me what's available" when a search comes up empty. */
    browse(opts: { kind?: 'asset' | 'pack'; category?: 'image' | 'audio' | 'font'; limit?: number } = {}): CatalogEntry[] {
        return this.search('', opts);
    }

    /** Resolve a dist-relative URL (or absolute, for hosting='remote') to its
     *  full fetchable form. */
    resolveUrl(distRelativeOrAbsolute: string): string {
        if (/^https?:\/\//i.test(distRelativeOrAbsolute)) return distRelativeOrAbsolute;
        return this.baseUrl + distRelativeOrAbsolute;
    }

    /** URL for an entry's bytes — always CORS-safe by construction (catalog
     *  build mirrors CORS-unsafe upstreams into dist/blobs/). */
    getAssetUrl(entry: CatalogEntry): string {
        return this.resolveUrl(entry.url);
    }

    /** URL for an entry's thumbnail. Returns null when the entry has no thumb
     *  (audio assets don't get one — callers should render a placeholder). */
    getThumbUrl(entry: CatalogEntry): string | null {
        return entry.thumb ? this.resolveUrl(entry.thumb) : null;
    }

    /** Fetch (or return cached) pack manifest. Lazy — only loaded when the
     *  user clicks into a pack. */
    async getPackManifest(entry: CatalogEntry): Promise<CatalogPackManifest> {
        if (entry.kind !== 'pack' || !entry.packManifest) {
            throw new Error(`entry ${entry.slug} is not a pack`);
        }
        const cached = this.packCache.get(entry.sha256);
        if (cached) return cached;
        const idbHit = await this.idb!.get<CatalogPackManifest>(STORE_PACKS, entry.sha256);
        if (idbHit) {
            this.packCache.set(entry.sha256, idbHit);
            return idbHit;
        }
        const fetched = await this.fetchJson<CatalogPackManifest>(entry.packManifest);
        await this.idb!.put(STORE_PACKS, entry.sha256, fetched);
        this.packCache.set(entry.sha256, fetched);
        return fetched;
    }

    /** Fetch raw bytes for an asset (or zip) and verify the sha256 matches
     *  what the catalog claimed. Throws if the bytes don't match — catches
     *  CDN corruption / upstream content swaps. */
    async fetchBytes(entry: CatalogEntry): Promise<Uint8Array> {
        const url = this.getAssetUrl(entry);
        const res = await fetch(url);
        if (!res.ok) throw new Error(`fetch ${url} → HTTP ${res.status}`);
        const buf = new Uint8Array(await res.arrayBuffer());
        const actual = await sha256Hex(buf);
        if (actual !== entry.sha256) {
            throw new Error(
                `sha256 mismatch for ${entry.slug}: catalog said ${entry.sha256.slice(0, 12)}…, got ${actual.slice(0, 12)}…`,
            );
        }
        return buf;
    }

    private async fetchJson<T>(path: string): Promise<T> {
        const url = this.resolveUrl(path);
        const res = await fetch(url);
        if (!res.ok) throw new Error(`fetch ${url} → HTTP ${res.status}`);
        return await res.json() as T;
    }
}

/** Default in-project filename for a catalog asset: `<slug><ext>`. The
 *  Catalog panel and the AI import tool share this so an asset lands at the
 *  same path either way. */
export function catalogFilename(entry: CatalogEntry): string {
    return `${entry.slug}${guessCatalogExt(entry.mime, entry.url)}`;
}

export function guessCatalogExt(mime: string, url: string): string {
    const m = /\.([a-z0-9]+)(?:\?|#|$)/i.exec(url);
    if (m) return `.${m[1].toLowerCase()}`;
    switch (mime) {
        case 'image/png': return '.png';
        case 'image/jpeg': return '.jpg';
        case 'image/gif': return '.gif';
        case 'image/webp': return '.webp';
        case 'audio/wav': return '.wav';
        case 'audio/mpeg': return '.mp3';
        case 'audio/ogg': return '.ogg';
        case 'application/zip': return '.zip';
        default: return '';
    }
}

async function sha256Hex(bytes: Uint8Array): Promise<string> {
    // SubtleCrypto.digest types want BufferSource backed by ArrayBuffer.
    // Round-tripping through `.buffer` widens to ArrayBufferLike (which
    // includes SharedArrayBuffer in lib.dom.d.ts), so explicitly take a
    // tight ArrayBuffer slice — the bytes copy is fine at sha-of-asset scale.
    const ab = new ArrayBuffer(bytes.byteLength);
    new Uint8Array(ab).set(bytes);
    const buf = await crypto.subtle.digest('SHA-256', ab);
    return [...new Uint8Array(buf)].map(b => b.toString(16).padStart(2, '0')).join('');
}
