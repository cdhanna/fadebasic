// Compile the bundled raw image + font (fade-assets/) into MonoGame `.xnb`
// bytes the web runtime's BrowserContentManager can serve, reusing the
// Playground's in-browser asset compiler VERBATIM.
//
// Why a headless browser: that compiler is browser-only — it decodes images
// with createImageBitmap/OffscreenCanvas and rasterizes fonts via
// document.fonts + canvas2d. So we bundle it, run it in headless Chromium
// (Playwright), feed the raw bytes, and write the resulting XNBs back into
// fade-assets/compiled/ (committed). Audio needs no compile — it's registered
// raw and decoded by Web Audio at runtime.
//
// This is a DEV/authoring step, not part of `prep`: it needs Playwright +
// Chromium (present in the sibling Fade.Playground checkout) and only needs to
// re-run when the raw image/font sources change. `stage:assets` (in prep) just
// copies the already-compiled XNBs + raw audio into public/fade/assets/.
//
// Usage: node scripts/build-assets.mjs

import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, mkdirSync, existsSync, rmSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const homepage = resolve(here, '..');
const assetsRoot = resolve(homepage, 'fade-assets');
const compiledOut = resolve(assetsRoot, 'compiled');
const playground = resolve(homepage, '..', '..', 'Fade.Playground', 'Playground');
const pgAssets = resolve(playground, 'src', 'assets');

if (!existsSync(pgAssets)) {
    console.error(`[build-assets] Playground asset compiler not found at ${pgAssets}. Skipping (XNBs must already be committed).`);
    process.exit(0);
}

// 1. Bundle a tiny harness that reuses the Playground compiler + a Map-backed
//    workspace stub. Written into src/assets so its ./relative imports resolve.
const harnessPath = resolve(pgAssets, '_xnb_harness.ts');
const harnessSrc = `
import { compileImageAssetsWithPlan, compileFontAssetsWithPlan } from './compile-assets';
const EMPTY_PLAN: any = { entries: [], defaultCompression: 'auto' };
class MemWorkspace {
  files = new Map<string, Uint8Array>();
  async list() { return [...this.files.keys()]; }
  async read(p: string) { return new TextDecoder().decode(this.files.get(p)!); }
  async write(p: string, c: string) { this.files.set(p, new TextEncoder().encode(c)); }
  async readBytes(p: string) { const b = this.files.get(p); if (!b) throw new Error('no ' + p); return b; }
  async writeBytes(p: string, b: Uint8Array) { this.files.set(p, b); }
  async delete(p: string) { this.files.delete(p); }
  async mkdir() {}
}
(window as any).__compileXnb = async (kind: string, filename: string, b64: string) => {
  const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
  const ws = new MemWorkspace();
  await ws.writeBytes(filename, bytes);
  const res = kind === 'image'
    ? await compileImageAssetsWithPlan(ws as any, [filename], EMPTY_PLAN)
    : await compileFontAssetsWithPlan(ws as any, [filename], EMPTY_PLAN);
  if (!res.assets.length) throw new Error('no asset produced: ' + JSON.stringify(res.diagnostics));
  const xnb: Uint8Array = res.assets[0].bytes;
  let s = ''; for (let i = 0; i < xnb.length; i++) s += String.fromCharCode(xnb[i]);
  return { b64: btoa(s), name: res.assets[0].assetName };
};
`;
writeFileSync(harnessPath, harnessSrc);

let bundle;
try {
    // esbuild ships with the Playground; bundle to an IIFE we inject into the page.
    const esbuild = resolve(playground, 'node_modules', '.bin', 'esbuild');
    bundle = execFileSync(esbuild, [
        harnessPath, '--bundle', '--format=iife', '--platform=browser', '--log-level=warning',
    ], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
} finally {
    rmSync(harnessPath, { force: true });
}

// 2. Drive the bundle in headless Chromium. Serve a blank page from 127.0.0.1
//    so the page is a SECURE CONTEXT — the compiler hashes via crypto.subtle,
//    which is undefined on about:blank.
const http = await import('node:http');
const server = http.createServer((_req, res) => {
    res.writeHead(200, { 'content-type': 'text/html' });
    res.end('<!doctype html><html><body></body></html>');
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const origin = `http://127.0.0.1:${server.address().port}/`;

const pw = await import(resolve(playground, 'node_modules', 'playwright', 'index.js'));
const chromium = pw.chromium ?? pw.default?.chromium;
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(playground, 'node_modules', 'playwright', '.local-browsers');
const browser = await chromium.launch({ headless: true });
const page = await (await browser.newContext()).newPage();
await page.goto(origin, { waitUntil: 'domcontentloaded' });
await page.addScriptTag({ content: bundle });

mkdirSync(compiledOut, { recursive: true });
const jobs = [
    { kind: 'image', src: resolve(assetsRoot, 'images', 'ghost.png'), out: 'ghost.xnb' },
    { kind: 'font',  src: resolve(assetsRoot, 'fonts', 'press-start-2p.ttf'), out: 'font.xnb', filename: 'font.ttf' },
];
for (const job of jobs) {
    const raw = readFileSync(job.src);
    const filename = job.filename ?? job.src.split('/').pop();
    const { b64, name } = await page.evaluate(
        ([kind, fn, data]) => window.__compileXnb(kind, fn, data),
        [job.kind, filename, raw.toString('base64')],
    );
    const xnb = Buffer.from(b64, 'base64');
    writeFileSync(resolve(compiledOut, job.out), xnb);
    console.log(`[build-assets] ${job.kind} "${name}" → compiled/${job.out} (${xnb.length} B)`);
}
await browser.close();
server.close();
console.log('[build-assets] done. Commit fade-assets/compiled/*.xnb.');
