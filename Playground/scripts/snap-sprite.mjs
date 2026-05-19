// Visual smoke test for the user's source:
//   set screen size 400, 400
//   set render size 200, 200
//   sprite 1, 100, 100, 0
//   size sprite 1, 100, 100
//   do
//     sync
//   loop
// We open a monogame project with that source, hit Run, wait, then
// screenshot the canvas. A non-uniform image proves the basic sprite
// pipeline reaches the canvas; uniform/black means the render path
// is still broken.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { writeFileSync } from 'node:fs';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5314/';
const SOURCE = `set screen size 400, 400
set render size 200, 200

sprite 1, 100, 100, 0
size sprite 1, 100, 100

do
    sync
loop
`;

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 200)));
page.on('console', m => {
    const t = m.type();
    if (t === 'error') console.log('[E]', m.text().slice(0, 200));
    else if (t === 'warning') {
        const txt = m.text();
        if (!txt.includes('CSS') && !txt.includes('Linking')) console.log('[W]', txt.slice(0, 200));
    }
});

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

await page.evaluate(async (src) => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgsprite', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'mgsprite', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(src); await sw.close();
    localStorage.setItem('fade.activeProject', 'mgsprite');
}, SOURCE);

await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

// Click Run. First click boots the runtime (~8 MB), subsequent clicks
// hot-reload via Game1.LoadProgram.
console.log('→ click Run (lazy WASM boot)…');
await (await page.$('#run')).click();
await page.waitForSelector('#theCanvas', { timeout: 60_000 });
await page.waitForTimeout(4_000);

// Read the canvas pixel spread + dump a screenshot.
const canvasHandle = await page.$('#theCanvas');
const pngBytes = await canvasHandle.screenshot({ type: 'png' });
writeFileSync('/tmp/mg-sprite.png', pngBytes);

const spread = await page.evaluate(async (b64) => {
    const bin = atob(b64);
    const u8 = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i);
    const blob = new Blob([u8], { type: 'image/png' });
    const bmp = await createImageBitmap(blob);
    const probe = document.createElement('canvas');
    probe.width = 64; probe.height = 64;
    const ctx = probe.getContext('2d');
    ctx.drawImage(bmp, 0, 0, 64, 64);
    const px = ctx.getImageData(0, 0, 64, 64).data;
    let minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
    let whiteCount = 0;
    for (let i = 0; i < px.length; i += 4) {
        const r = px[i], g = px[i+1], b = px[i+2];
        if (r < minR) minR = r; if (r > maxR) maxR = r;
        if (g < minG) minG = g; if (g > maxG) maxG = g;
        if (b < minB) minB = b; if (b > maxB) maxB = b;
        if (r > 200 && g > 200 && b > 200) whiteCount++;
    }
    return { minR, maxR, minG, maxG, minB, maxB, whiteCount, totalPixels: (px.length / 4) };
}, Buffer.from(pngBytes).toString('base64'));

console.log('canvas spread:', JSON.stringify(spread));
console.log('screenshot at: /tmp/mg-sprite.png');

await browser.close();

const hasSpread = (spread.maxR - spread.minR) + (spread.maxG - spread.minG) + (spread.maxB - spread.minB) > 30;
const hasWhite = spread.whiteCount > 0;

if (!hasSpread || !hasWhite) {
    console.error('\n✗ FAIL: no white sprite visible. Render path still missing pixels.');
    console.error('  spread:', hasSpread, '   any-white-pixels:', hasWhite);
    process.exit(1);
}
console.log('\n✓ PASS: sprite (white square) is rendering on the canvas.');
console.log('   white pixels:', spread.whiteCount, '/', spread.totalPixels);
