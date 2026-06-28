// Asset registration end-to-end check.
//
// Synthesizes a Texture2D XNB in-page, writes it into the active project's
// OPFS folder along with a monogame fade.json + a main.fbasic that loads
// the texture and draws a sprite. Clicks Run, waits for the canvas to
// appear and render a few frames, then asserts that no "Game tick error"
// landed in the console (which is the symptom of TextureSystem.GetSourceRect
// dereferencing a null watchedTexture — the bug this whole change fixes).
//
// Requires a vite server with the monogame template already published into
// public/runtime/monogame/. Match test-monogame-integration.mjs's setup.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5311/';
const BOOT_BUDGET_MS = 60_000;

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

const consoleErrors = [];
const consoleAll = [];
page.on('console', (msg) => {
    const t = msg.text();
    consoleAll.push(`[${msg.type()}] ${t}`);
    if (msg.type() === 'error') consoleErrors.push(t);
});
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e.message));

console.log(`→ navigate ${URL}`);
await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

// In-page XNB builder. Same construction as test-binary-preview's
// Texture2D probe — 8×8 Color surface, 256 bytes RGBA. Small enough that
// the entire XNB is well under a kilobyte.
const buildTextureXnbSnippet = `
function build7BitInt(value) {
    const out = [];
    while (value >= 0x80) { out.push((value & 0x7F) | 0x80); value >>>= 7; }
    out.push(value);
    return out;
}
function build7BitPrefixedString(s) {
    const enc = new TextEncoder().encode(s);
    return [...build7BitInt(enc.length), ...enc];
}
function buildInt32LE(v) {
    return [v & 0xFF, (v >>> 8) & 0xFF, (v >>> 16) & 0xFF, (v >>> 24) & 0xFF];
}
function buildTextureXnb() {
    // Modern MGCB form: assembly tag is "MonoGame.Framework" (not the
    // legacy XNA "Microsoft.Xna.Framework"). KNI's ContentTypeReaderManager
    // translates this to its own Xna.Framework.Graphics; the legacy XNA
    // form only resolves for readers that happen to live in
    // Xna.Framework.Content (not Texture2D, which lives in .Graphics).
    const reader = 'Microsoft.Xna.Framework.Content.Texture2DReader, MonoGame.Framework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553';
    const W = 8, H = 8;
    const pixels = [];
    // Magenta-and-white checkerboard so a render-pass mistake is obvious.
    for (let y = 0; y < H; y++) {
        for (let x = 0; x < W; x++) {
            const on = (x + y) & 1;
            pixels.push(on ? 255 : 0, 0, on ? 255 : 0, 255);
        }
    }
    const payload = [
        ...build7BitInt(1),
        ...build7BitPrefixedString(reader),
        ...buildInt32LE(0),
        ...build7BitInt(0),
        ...build7BitInt(1),
        ...buildInt32LE(0),
        ...buildInt32LE(W),
        ...buildInt32LE(H),
        ...buildInt32LE(1),
        ...buildInt32LE(pixels.length),
        ...pixels,
    ];
    const fileSize = 10 + payload.length;
    return new Uint8Array([
        0x58, 0x4E, 0x42,  // 'XNB'
        0x64,              // 'd' DesktopGL
        5,                 // version
        0,                 // flags (uncompressed, Reach profile)
        ...buildInt32LE(fileSize),
        ...payload,
    ]);
}
`;

const setup = await page.evaluate(async (xnbSnippet) => {
    eval(xnbSnippet);
    const tex = buildTextureXnb();
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgassets', { create: true });

    const writeText = async (name, text) => {
        const fh = await dir.getFileHandle(name, { create: true });
        const w = await fh.createWritable();
        await w.write(text);
        await w.close();
    };
    const writeBytes = async (name, bytes) => {
        const fh = await dir.getFileHandle(name, { create: true });
        const w = await fh.createWritable();
        await w.write(new Blob([bytes]));
        await w.close();
    };

    await writeText('fade.json', JSON.stringify({
        $schema: '/fade.schema.json',
        name: 'mgassets',
        type: 'monogame',
        commandDlls: [],
        sources: ['main.fbasic'],
    }, null, 2) + '\n');
    // Minimal fbasic: load the texture, attach to sprite 1 at 100,100, loop
    // forever via sync. If LoadTextureFromContent's browser branch is still
    // a no-op, GetSourceRect will NRE on the first render frame.
    await writeText('main.fbasic',
        'texture 1, "Probe"\n' +
        'sprite 1, 100, 100, 1\n' +
        'do\n  sync\nloop\n');
    await writeBytes('Probe.xnb', tex);
    localStorage.setItem('fade.activeProject', 'mgassets');
    return { xnbLen: tex.length };
}, buildTextureXnbSnippet);

console.log('→ setup:', setup);

await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await page.waitForFunction(() => {
    const el = document.getElementById('status');
    return el && /Ready/i.test(el.textContent || '');
}, { timeout: 30_000 });

console.log('→ clicking Run');
await page.click('#run');

try {
    await page.waitForSelector('#theCanvas', { timeout: BOOT_BUDGET_MS });
} catch (e) {
    console.log('FAIL: canvas never appeared. console errors:');
    for (const er of consoleErrors.slice(-12)) console.log(' ', er.slice(0, 400));
    await browser.close();
    process.exit(1);
}
console.log('→ canvas appeared, allowing 3s of render frames…');
await page.waitForTimeout(3000);

// Pull the registered-asset count off the runtime to confirm the page
// actually pushed the XNB before LoadProgram.
const tickErrors = consoleErrors.filter((e) =>
    /Game tick error|NullReferenceException|asset/i.test(e),
);
console.log('matching console errors:', tickErrors.length);
for (const e of tickErrors) console.log('  ', e.slice(0, 200));

// Pass criteria: no tick errors, no page errors, canvas is alive.
let failed = false;
if (tickErrors.length > 0) {
    console.log('FAIL: expected no tick errors / NRE / asset errors during the run');
    failed = true;
}
if (pageErrors.length > 0) {
    console.log('FAIL: page errors during the run:', pageErrors);
    failed = true;
}
if (!failed) console.log('PASS: monogame run with registered XNB asset did not NRE');

if (failed) {
    console.log('--- recent console messages (last 30) ---');
    for (const m of consoleAll.slice(-30)) console.log(' ', m.slice(0, 400));
}

// Clean up — leaves the active project as 'mgassets' for follow-up
// inspection if a debugger wants to step through, but removes the
// probe files so the workspace is clean.
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    try { await ws.removeEntry('mgassets', { recursive: true }); } catch {}
    localStorage.setItem('fade.activeProject', 'default');
});

await browser.close();
process.exit(failed ? 1 : 0);
