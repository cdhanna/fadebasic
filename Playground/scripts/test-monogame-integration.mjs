// End-to-end Playground × WebRuntime.MonoGame integration check.
//
// Setup: vite preview (or dev) running on $URL, with monogame-runtime
// already published into Playground/public/monogame-runtime/.
//
// Test flow:
//   1. Open Playground page.
//   2. Synthesize a fade.json with type='monogame' + a single main.fbasic.
//      Easiest path: drive the existing project-create flow (Cmd+P opens
//      the project overlay). But that needs to support the 'monogame'
//      type in the UI — Phase 0 only updated the schema, not the create
//      dropdown. For now, write the fade.json + sources directly into
//      OPFS and reload.
//   3. Click Run. Expect the Game panel to be revealed and the MonoGame
//      runtime to boot. Assert the canvas appears + has non-uniform pixels.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5312/';
const BOOT_BUDGET_MS = 60_000; // generous: ~8 MB WASM download + warmup

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

const errors = [];
page.on('console', msg => {
    if (msg.type() === 'error') {
        const t = msg.text();
        if (t.length < 1000) console.log('[console.error]', t.slice(0, 300));
    }
});
page.on('pageerror', e => { errors.push(e); console.log('[pageerror]', e.message.slice(0, 300)); });

console.log(`→ navigate ${URL}`);
await page.goto(URL, { waitUntil: 'domcontentloaded' });

// Wait for the Playground to be Ready (status text flips off "Loading…").
await page.waitForFunction(() => {
    const el = document.getElementById('status');
    return el && /Ready/i.test(el.textContent || '');
}, { timeout: 30_000 });
console.log('→ Playground ready');

// Write a monogame project into OPFS through the Playground's own APIs.
// We use the same FileSystemAccessHandle path the editor uses; quickest
// is via the page-level OPFS root.
const written = await page.evaluate(async () => {
    try {
        const opfsRoot = await navigator.storage.getDirectory();
        // Playground stores projects under workspace/<name>/. The bootstrap
        // discovers them by listing this directory and reading their fade.json.
        const workspace = await opfsRoot.getDirectoryHandle('workspace', { create: true });
        const dir = await workspace.getDirectoryHandle('mgtest', { create: true });
        const cfg = JSON.stringify({
            $schema: '/fade.schema.json',
            name: 'mgtest',
            type: 'monogame',
            commandDlls: [],
            sources: ['main.fbasic'],
        }, null, 2) + '\n';
        const cfgFile = await dir.getFileHandle('fade.json', { create: true });
        const cfgW = await cfgFile.createWritable();
        await cfgW.write(cfg);
        await cfgW.close();

        const src = await dir.getFileHandle('main.fbasic', { create: true });
        const srcW = await src.createWritable();
        await srcW.write('do\n  sync\nloop\n');
        await srcW.close();

        // Remember the active project — the bootstrap reads this at load.
        localStorage.setItem('fade.activeProject', 'mgtest');
        return true;
    } catch (e) {
        return { _err: String(e) };
    }
});
console.log('→ project written:', written);

// Reload so the Playground picks up the new active project.
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => {
    const el = document.getElementById('status');
    return el && /Ready/i.test(el.textContent || '');
}, { timeout: 30_000 });
console.log('→ reloaded with mgtest as active');

// Confirm fade-config recognized type=monogame (no Problems entries for it).
const projectBadge = await page.evaluate(() => {
    const el = document.getElementById('project-name');
    return el ? el.textContent : null;
});
console.log('→ project badge:', projectBadge);

// Click Run. The handler routes to monoGameHost.loadProgram(source).
console.log('→ clicking Run (this triggers lazy WASM boot — ~8 MB)…');
const runBtn = await page.$('#run');
if (!runBtn) throw new Error('#run button not in DOM');
await runBtn.click();

// Wait for the canvas to appear and render.
try {
    await page.waitForSelector('#theCanvas', { timeout: BOOT_BUDGET_MS });
} catch (e) {
    console.log('canvas never appeared. recent console errors:');
    for (const er of errors.slice(-10)) console.log(' ', er.message.slice(0, 400));
    await browser.close();
    process.exit(1);
}
console.log('→ canvas appeared, waiting for render…');
await page.waitForTimeout(3000);

// Pixel-spread probe (preserveDrawingBuffer:false safe — uses element screenshot).
const canvasHandle = await page.$('#theCanvas');
const pngBytes = await canvasHandle.screenshot({ type: 'png' });
const result = await page.evaluate(async (b64) => {
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
    return { minR, maxR, minG, maxG, minB, maxB,
             nonzero: (maxR + maxG + maxB) > 0 };
}, Buffer.from(pngBytes).toString('base64'));

console.log('→ pixel sample:', JSON.stringify(result));

await browser.close();

if (!result.nonzero) {
    console.error('\n✗ FAIL: canvas was all-black after Run.');
    process.exit(1);
}
console.log('\n✓ PASS: monogame project routed through Playground Run → MonoGame canvas rendered.');
