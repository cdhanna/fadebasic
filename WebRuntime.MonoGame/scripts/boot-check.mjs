// Headless boot check for WebRuntime.MonoGame standalone.
//
// Runs the dev server's index.html in a real Chromium tab, captures console
// messages and uncaught errors, and asserts the canvas has actually been
// drawn to (non-uniform pixels). The Phase 1 sample game cycles its clear
// color and slides a white square horizontally — if the canvas stays uniform
// for 3 seconds, the bootstrap didn't reach the render loop.
//
// Usage:
//   1. In another terminal: `cd WebRuntime.MonoGame && dotnet run -c Release --urls http://localhost:5298`
//   2. `node scripts/boot-check.mjs` (uses Playwright from ../Playground/node_modules)

import { chromium } from 'playwright';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
// Reuse Playground's playwright install — it's already pinned there and the
// monogame runtime is dev-only tooling, no need for a separate node_modules.
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', '..', 'Playground', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5298/';
const TIMEOUT_MS = 30000;

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });

const logs = [];
const errors = [];
page.on('console', (msg) => logs.push(`[${msg.type()}] ${msg.text()}`));
page.on('pageerror', (e) => errors.push(`[pageerror] ${e.message}\n${e.stack ?? ''}`));
page.on('requestfailed', (req) => {
    errors.push(`[requestfailed] ${req.url()} — ${req.failure()?.errorText}`);
});

console.log(`→ navigating to ${URL}`);
await page.goto(URL, { waitUntil: 'domcontentloaded', timeout: TIMEOUT_MS });

// Wait for theCanvas to exist (Index.razor mounts after Blazor boots).
try {
    await page.waitForSelector('#theCanvas', { timeout: TIMEOUT_MS });
} catch (e) {
    console.log('\n=== canvas never appeared, dumping logs/errors ===');
    for (const l of logs) console.log(l);
    for (const er of errors) console.log(er);
    await browser.close();
    process.exit(1);
}

// Give the rAF loop a few seconds to draw something.
console.log('→ waiting 3s for render loop to produce frames…');
await page.waitForTimeout(3000);

// Sample the canvas via element screenshot rather than reading the
// drawing-buffer. KNI's WebGL context uses preserveDrawingBuffer:false (the
// default), so canvas.toDataURL / drawImage(canvas) post-frame returns a
// cleared framebuffer. page.screenshot captures the composited pixels that
// the user actually sees — works regardless of the preserveDrawingBuffer flag.
const canvasInfo = await page.evaluate(() => {
    const canvas = document.getElementById('theCanvas');
    if (!canvas) return { ok: false, reason: 'no canvas element' };
    return { ok: true, w: canvas.width, h: canvas.height };
});

if (!canvasInfo.ok) {
    await browser.close();
    console.error(`\n✗ FAIL: ${canvasInfo.reason}`);
    process.exit(1);
}

const canvasHandle = await page.$('#theCanvas');
const pngBytes = await canvasHandle.screenshot({ type: 'png' });

// Decode the PNG with a tiny in-process parser is overkill — use the page
// itself: blob it back through an Image+canvas to inspect pixel spread.
const sample = await page.evaluate(async (b64) => {
    const bin = atob(b64);
    const u8 = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i);
    const blob = new Blob([u8], { type: 'image/png' });
    const bmp = await createImageBitmap(blob);
    const probe = document.createElement('canvas');
    probe.width = 32; probe.height = 32;
    const ctx = probe.getContext('2d');
    ctx.drawImage(bmp, 0, 0, 32, 32);
    const px = ctx.getImageData(0, 0, 32, 32).data;
    let minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
    for (let i = 0; i < px.length; i += 4) {
        if (px[i]   < minR) minR = px[i];   if (px[i]   > maxR) maxR = px[i];
        if (px[i+1] < minG) minG = px[i+1]; if (px[i+1] > maxG) maxG = px[i+1];
        if (px[i+2] < minB) minB = px[i+2]; if (px[i+2] > maxB) maxB = px[i+2];
    }
    const spread = (maxR - minR) + (maxG - minG) + (maxB - minB);
    return { ok: spread > 0, spread, sample: { minR, maxR, minG, maxG, minB, maxB } };
}, Buffer.from(pngBytes).toString('base64'));

sample.canvasSize = { w: canvasInfo.w, h: canvasInfo.h };

await browser.close();

console.log('\n=== console log ===');
for (const l of logs.slice(-40)) console.log(l);

if (errors.length) {
    console.log('\n=== errors ===');
    for (const e of errors) console.log(e);
}

console.log('\n=== render probe ===');
console.log(JSON.stringify(sample, null, 2));

if (!sample.ok) {
    console.error('\n✗ FAIL: canvas appears uniform — render loop never drew or readback returned transparent pixels.');
    process.exit(1);
}
console.log('\n✓ PASS: canvas has non-uniform pixels — KNI render pipeline is alive.');
