// Build script: walks the configured doc sources (see docs-sources.mjs),
// chunks each markdown file, embeds every chunk with bge-small-en-v1.5,
// and writes public/docs-index.json for the runtime to serve.
//
// Usage:
//   npm run build:docs-index
//
// The model downloads to ~/.cache/huggingface/ on first run (~30 MB) and
// is reused thereafter. The output JSON is committed-or-not at your
// discretion — it's small (typically <1 MB) and deterministic given the
// same docs + same model, but rebuilding from source is cheap.

import { readdir, readFile, writeFile, mkdir, stat } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { resolve, dirname, relative, posix } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { DOC_SOURCES } from './docs-sources.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const playgroundDir = resolve(__dirname, '..');
const outPath = resolve(playgroundDir, 'public', 'docs-index.json');

// We import the TS modules directly via Vite-compatible source paths.
// Node + TypeScript-from-source requires tsx, ts-node, or pre-built JS.
// Simplest: use `node --experimental-strip-types`. Available since Node 22.6.
//
// If the user is on older Node, they can do `npx tsx scripts/build-docs-index.mjs`.
// We don't pre-build here to keep the toolchain minimal.

const srcRoot = resolve(playgroundDir, 'src', 'ai', 'rag');
const { Embedder } = await import(pathToFileURL(resolve(srcRoot, 'embedder.ts')).href);
const { chunkMarkdown } = await import(pathToFileURL(resolve(srcRoot, 'chunker.ts')).href);
const { EMBEDDING_DIM, EMBEDDING_MODEL, INDEX_VERSION } = await import(pathToFileURL(resolve(srcRoot, 'types.ts')).href);

// ─── Walk + chunk ───────────────────────────────────────────────────────────

console.log('[build:docs-index] walking doc sources');
const allChunks = [];
let sourceCount = 0;

for (const src of DOC_SOURCES) {
    if (!existsSync(src.root)) {
        console.warn(`[build:docs-index] skipping ${src.label}: ${src.root} does not exist`);
        continue;
    }
    const files = await walk(src.root, src.glob);
    const scopeNote = src.projectTypes?.length
        ? ` [scoped to projectTypes=${JSON.stringify(src.projectTypes)}]`
        : '';
    console.log(`[build:docs-index] ${src.label}: ${files.length} file(s)${scopeNote}`);
    sourceCount += files.length;

    for (const file of files) {
        const text = await readFile(file, 'utf-8');
        const relPath = posix.join(src.label, relative(src.root, file).split(/[\\/]/).join('/'));
        const chunks = chunkMarkdown({ source: relPath, text });
        console.log(`  - ${relPath}: ${chunks.length} chunk(s)`);
        for (const c of chunks) {
            // Tag the chunk with the source's projectTypes (when set) so the
            // runtime Retriever can gate retrieval by active project type.
            // Stored as a plain field; absent / empty arrays are treated as
            // "always include".
            if (src.projectTypes?.length) c.projectTypes = [...src.projectTypes];
            allChunks.push(c);
        }
    }
}

console.log(`[build:docs-index] total chunks: ${allChunks.length}`);

if (allChunks.length === 0) {
    console.warn('[build:docs-index] no chunks produced — writing empty index');
    await writeIndex([]);
    process.exit(0);
}

// ─── Embed ──────────────────────────────────────────────────────────────────

console.log(`[build:docs-index] loading embedder (${EMBEDDING_MODEL})…`);
const embedder = new Embedder({
    device: 'cpu',
    dtype: 'fp32',
    onProgress: (info) => {
        if (info?.status === 'progress' && typeof info.progress === 'number') {
            // Throttle: only log at 25/50/75/100% milestones per file.
            const pct = Math.round(info.progress);
            if (pct % 25 === 0) console.log(`  [${info.file ?? '?'}] ${pct}%`);
        } else if (info?.status === 'ready') {
            console.log(`  loaded ${info.file ?? ''}`);
        }
    },
});
await embedder.ensureReady();

console.log('[build:docs-index] embedding chunks…');
const BATCH = 16;
const t0 = Date.now();
const enriched = [];

for (let i = 0; i < allChunks.length; i += BATCH) {
    const batch = allChunks.slice(i, i + BATCH);
    const vectors = await embedder.embedPassage(batch.map(c => c.text));
    for (let j = 0; j < batch.length; j++) {
        const v = vectors[j];
        if (v.length !== EMBEDDING_DIM) {
            throw new Error(`Embedding dim mismatch: got ${v.length}, expected ${EMBEDDING_DIM}`);
        }
        const row = {
            id: batch[j].id,
            source: batch[j].source,
            heading: batch[j].heading,
            text: batch[j].text,
            chars: batch[j].text.length,
            // Plain number[] so JSON.parse hydrates directly; runtime
            // converts to Float32Array on demand.
            vector: Array.from(v),
        };
        if (batch[j].projectTypes?.length) row.projectTypes = batch[j].projectTypes;
        enriched.push(row);
    }
    process.stdout.write(`\r  embedded ${Math.min(i + BATCH, allChunks.length)}/${allChunks.length}`);
}
console.log(`\n[build:docs-index] embedded in ${((Date.now() - t0) / 1000).toFixed(1)}s`);

await writeIndex(enriched);

// ─── Helpers ────────────────────────────────────────────────────────────────

async function writeIndex(chunks) {
    const index = {
        version: INDEX_VERSION,
        model: EMBEDDING_MODEL,
        dim: EMBEDDING_DIM,
        builtAt: new Date().toISOString(),
        sourceCount,
        chunks,
    };
    await mkdir(dirname(outPath), { recursive: true });
    await writeFile(outPath, JSON.stringify(index));
    const bytes = (await stat(outPath)).size;
    console.log(`[build:docs-index] wrote ${outPath} (${(bytes / 1024).toFixed(1)} KB, ${chunks.length} chunks)`);
}

async function walk(root, glob) {
    // We support only `**/*.<ext>`-style globs for simplicity. If we need
    // more, swap in fast-glob — but a few-line manual walk avoids adding
    // a build dep we don't otherwise need.
    const m = glob.match(/\.([a-z0-9]+)$/i);
    const wantExt = m ? `.${m[1]}` : '.md';
    const out = [];
    async function rec(dir) {
        const entries = await readdir(dir, { withFileTypes: true });
        for (const e of entries) {
            const full = resolve(dir, e.name);
            if (e.isDirectory()) {
                await rec(full);
            } else if (e.isFile() && e.name.toLowerCase().endsWith(wantExt)) {
                out.push(full);
            }
        }
    }
    await rec(root);
    out.sort();
    return out;
}
