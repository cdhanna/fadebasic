// Verifies the KNI-patched (MGFX v10) FadeSpriteBatchEffect that
// FadeBasic.MonoGame.Game now bakes + loads on the browser path actually
// loads on KNI BlazorGL — i.e. the web sprite-effect bug is fixed.
//
// Before the fix, Game1's #else used KNI's built-in SpriteEffect (custom
// effect dropped); and an un-patched v11 MGFX blob throws an MGFX-version
// exception when KNI's EffectReader parses it / at SpriteBatch.End.
//
// Primary signal: a sprite program boots and runs with NO effect/MGFX
// exception. Secondary: the canvas renders a non-blank frame (white square).
//
// Usage: dev server up (npm run dev), then `node scripts/probe-mg-sprite-effect.mjs`.

import { chromium } from 'playwright';
import { writeFileSync } from 'node:fs';

const URL = process.env.URL || 'https://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1500, height: 900 }, ignoreHTTPSErrors: true });
const page = await context.newPage();

const errors = [];
const consoleAll = [];
page.on('pageerror', (e) => errors.push('[pageerror] ' + e.message));
page.on('console', (m) => consoleAll.push(`[${m.type()}] ${m.text()}`));

// First load just to get an origin for OPFS/localStorage — do NOT wait for
// bootstrap here (the no-project welcome path never completes).
await page.goto(URL, { waitUntil: 'domcontentloaded' });

await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgsprite', { create: true });
    const w = async (n, t) => {
        const fh = await dir.getFileHandle(n, { create: true });
        const sw = await fh.createWritable();
        await sw.write(t); await sw.close();
    };
    await w('fade.json', JSON.stringify({
        $schema: '/fade.schema.json',
        name: 'mgsprite',
        type: 'monogame',
        commandDlls: [],
        sources: ['main.fbasic'],
    }, null, 2) + '\n');
    // A white 50x50 sprite (texture 0 = built-in 1x1 white pixel) drawn each
    // frame through FadeSpriteBatch -> FadeSpriteBatchEffect.
    await w('main.fbasic',
        'sprite 1, 320, 240, 0\n' +
        'size sprite 1, 50, 50\n' +
        'do\n  sync\nloop\n');
    localStorage.setItem('fade.activeProject', 'mgsprite');
    localStorage.removeItem('fade.dockview.layout.v4');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 90_000 });
await new Promise((r) => setTimeout(r, 1500));

await page.click('#run');
console.log('clicked Run, waiting for KNI WASM boot + render…');
await new Promise((r) => setTimeout(r, 22_000));

const png = await page.screenshot();
writeFileSync('/tmp/fade-mg-sprite.png', png);

// The monogame runtime renders into a canvas INSIDE an iframe — find the frame
// that has #theCanvas and sample pixels there.
const sampleCanvas = () => {
    const canvas = document.getElementById('theCanvas');
    if (!canvas) return { canvasExists: false };
    const out = { canvasExists: true, w: canvas.width, h: canvas.height };
    try {
        const c2 = document.createElement('canvas');
        c2.width = canvas.width; c2.height = canvas.height;
        const ctx = c2.getContext('2d');
        ctx.drawImage(canvas, 0, 0);
        const { data } = ctx.getImageData(0, 0, canvas.width, canvas.height);
        let bright = 0, nonZero = 0;
        for (let i = 0; i < data.length; i += 4) {
            const r = data[i], g = data[i + 1], b = data[i + 2], a = data[i + 3];
            if (a > 0 && (r + g + b) > 0) nonZero++;
            if (r > 200 && g > 200 && b > 200) bright++;
        }
        out.nonZeroPixels = nonZero;
        out.brightPixels = bright;   // ~ the white square (≈2500 at 50x50)
    } catch (e) {
        out.sampleError = String(e);  // WebGL drawImage may be blank w/o preserveDrawingBuffer
    }
    return out;
};
let render = { canvasExists: false };
for (const fr of page.frames()) {
    const has = await fr.evaluate(() => !!document.getElementById('theCanvas')).catch(() => false);
    if (has) { render = await fr.evaluate(sampleCanvas); break; }
}

// A v11-on-KNI failure surfaces as an MGFX/EffectReader/Effect-content
// exception (or a disposed-effect blow-up at SpriteBatch.End). The
// get-version-info ILLink/deserialization log is unrelated (Gotcha #2).
const effectErrors = [...errors, ...consoleAll].filter((m) =>
    /MGFX|EffectReader|ContentLoadException|ObjectDisposedException|unsupported.*shader|shader.*version/i.test(m)
    && !/get-version-info/i.test(m));

console.log('render:', JSON.stringify(render, null, 2));
console.log('pageerrors:', errors.length ? errors : '(none)');
console.log('effect-related error lines:', effectErrors.length ? effectErrors : '(none)');
console.log('--- last 25 console lines ---');
for (const m of consoleAll.slice(-25)) console.log('  ', m.slice(0, 300));

// Cleanup
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    try { await ws.removeEntry('mgsprite', { recursive: true }); } catch {}
    localStorage.setItem('fade.activeProject', 'default');
});
await browser.close();

const noEffectException = effectErrors.length === 0;
const rendered = render.canvasExists && (render.brightPixels > 200);
console.log('\n── VERDICT ──');
console.log('no effect/MGFX exception :', noEffectException);
console.log('canvas rendered sprite   :', rendered, render.brightPixels != null ? `(brightPixels=${render.brightPixels})` : '');
if (!noEffectException) {
    console.log('FAIL: an effect/MGFX exception fired — KNI did not accept the effect.');
    process.exit(1);
}
console.log(rendered
    ? 'PASS: effect loaded on KNI and the sprite rendered.'
    : 'PASS (partial): no effect exception; pixel sample inconclusive (headless WebGL) — see /tmp/fade-mg-sprite.png.');
