// Reproduces the user's exact source and snapshots the canvas at high
// resolution. Diagnoses two issues at once:
//   1. set background color should produce grey, not black.
//   2. The 200x200 sprite should appear as a SQUARE in screen space
//      (200x200 within a 1920x1080 mainBuffer letterboxed into the
//      canvas; uniform scale, so square stays square).

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { writeFileSync } from 'node:fs';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5314/';
const SOURCE = `set screen size 400, 400
set render size 1920, 1080

set background color rgb(128, 128, 128)
sprite 1, 100, 100, 0
color sprite 1, rgb(255, 0, 0)
size sprite 1, 200, 200
x = 180

do
    sprite 1, x, 100, 0

    if x < 200 then x = x + 1

    sync
loop
`;

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });

page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 200)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

await page.evaluate(async (src) => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgmove', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'mgmove', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(src); await sw.close();
    localStorage.setItem('fade.activeProject', 'mgmove');
}, SOURCE);

await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

await (await page.$('#run')).click();
await page.waitForSelector('#theCanvas', { timeout: 60_000 });
await page.waitForTimeout(4_000);

// Dump canvas + measure its CSS box vs drawing buffer.
const dims = await page.evaluate(() => {
    const c = document.getElementById('theCanvas');
    const r = c.getBoundingClientRect();
    return {
        cssBox: { w: Math.round(r.width), h: Math.round(r.height) },
        drawingBuffer: { w: c.width, h: c.height },
    };
});
console.log('canvas dims:', JSON.stringify(dims));

const canvasHandle = await page.$('#theCanvas');
const pngBytes = await canvasHandle.screenshot({ type: 'png' });
writeFileSync('/tmp/mg-user-source.png', pngBytes);

// Pixel-spread + red-pixel count + bounding box of red pixels.
const probe = await page.evaluate(async (b64) => {
    const bin = atob(b64);
    const u8 = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i);
    const blob = new Blob([u8], { type: 'image/png' });
    const bmp = await createImageBitmap(blob);
    const probe = document.createElement('canvas');
    probe.width = bmp.width; probe.height = bmp.height;
    const ctx = probe.getContext('2d');
    ctx.drawImage(bmp, 0, 0);
    const px = ctx.getImageData(0, 0, bmp.width, bmp.height).data;
    let minX = bmp.width, maxX = 0, minY = bmp.height, maxY = 0;
    let redCount = 0, greyCount = 0, blackCount = 0;
    for (let y = 0; y < bmp.height; y++) {
        for (let x = 0; x < bmp.width; x++) {
            const i = (y * bmp.width + x) * 4;
            const r = px[i], g = px[i+1], b = px[i+2];
            const isRed   = r > 200 && g < 70 && b < 70;
            const isGrey  = r > 100 && r < 160 && Math.abs(r - g) < 10 && Math.abs(r - b) < 10;
            const isBlack = r < 20 && g < 20 && b < 20;
            if (isRed) {
                redCount++;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            if (isGrey)  greyCount++;
            if (isBlack) blackCount++;
        }
    }
    return {
        imageSize: { w: bmp.width, h: bmp.height },
        redCount, greyCount, blackCount,
        redBounds: redCount > 0
            ? { x: minX, y: minY, w: maxX - minX + 1, h: maxY - minY + 1, aspect: (maxX - minX + 1) / (maxY - minY + 1) }
            : null,
    };
}, Buffer.from(pngBytes).toString('base64'));

console.log('probe:', JSON.stringify(probe, null, 2));
console.log('saved /tmp/mg-user-source.png');

await browser.close();
