// End-to-end smoke test for the microui port in the browser, driven by
// the existing fbasic DebugUICommand queue (begin debug window / debug
// button / etc. from Fade.MonoGame.Lib/DebugUICommands.cs).
//
// Boots a monogame fbasic project that calls `begin debug window` +
// `debug button` + `print` (the user-provided example). Verifies:
//
//   1. With the user program running, the iframe canvas renders a
//      panel (PNG byte length well above blank baseline).
//   2. Clicking the rendered "click" button changes the next-frame
//      screenshot — both via microui hover/focus visuals AND via the
//      fbasic side: `if a then print "clicked"` pushes a line into
//      the iframe's stdout which we capture as well.
//
//   The "only renders while running" guarantee falls out of the
//   architecture: with no fbasic program pushing debug commands, the
//   queue stays empty and RenderMicroui draws nothing.
//
// We can't use canvas.getImageData on the WebGL canvas because KNI
// creates the context with preserveDrawingBuffer=false (the GPU swap
// happens before any synchronous JS readback). Playwright's
// elementHandle.screenshot() grabs the composited result from the
// browser's rendering pipeline, which DOES see the most recent frame.

import { chromium } from 'playwright';
import { createHash } from 'node:crypto';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??=
    resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.PROBE_URL ?? 'http://localhost:5311/';
const HEADLESS = process.env.PROBE_HEADED !== '1';

const fatalPageErrors = [];

const b = await chromium.launch({ headless: HEADLESS });
const p = await b.newPage({ viewport: { width: 1400, height: 900 } });

p.on('pageerror', (e) => {
    // Filter out unrelated Playground page errors. We only fail this
    // probe on errors that could originate from microui or the C#
    // runtime. The "classList" null-deref comes from existing
    // Playground bootstrap code and is harmless here.
    const msg = e.message || '';
    const isMicrouiRelated =
        /microui|MicroUi|MuContext|MicroUiRenderer|MicroUiAtlas/i.test(msg);
    console.log(isMicrouiRelated ? '[PE-MU]' : '[PE-ignored]', msg.slice(0, 400));
    if (isMicrouiRelated) fatalPageErrors.push(msg);
});
p.on('console', (m) => {
    if (m.type() === 'error') console.log('[console.error]', m.text().slice(0, 400));
});

// Capture stdout/stderr forwarded by the monogame iframe so we can
// verify the user's `print "clicked"` actually fires after a button
// press. The iframe relays via postMessage({type:'stdout', line}) on
// the parent window — we hook it before navigation finishes.
const capturedStdout = [];
await p.exposeFunction('__probeRecordStdout', (line) => { capturedStdout.push(line); });
await p.addInitScript(() => {
    window.addEventListener('message', (e) => {
        if (e?.data?.type === 'stdout' && typeof e.data.line === 'string') {
            window.__probeRecordStdout(e.data.line);
        }
    });
});

await p.goto(URL, { waitUntil: 'domcontentloaded' });
await p.waitForSelector('#run', { timeout: 60_000 });
console.log('✓ Playground UI mounted');

// Seed a monogame project whose main.fbasic exercises the debug UI
// command surface (begin debug window + debug button + conditional
// print) — this is exactly the user's reported repro. Single window
// so the button is reachable (a second debug window would stagger
// and overlap, putting the click outside the button rect).
const USER_PROGRAM =
    'do\n' +
    '    begin debug window "test"\n' +
    '        a = debug button("click")\n' +
    '        if a\n' +
    '            print "clicked"\n' +
    '        endif\n' +
    '    end debug window\n' +
    '\n' +
    '    sync\n' +
    'loop\n';

await p.evaluate(async (source) => {
    // Project seeding (OPFS + localStorage) + Blazor cache purge in
    // one evaluate so the page doesn't navigate between calls. The
    // cache purge is critical: Blazor caches DLL/WASM bundles in
    // cacheStorage; without wiping them, the iframe loads yesterday's
    // Game1.dll and the C# changes never take effect.
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('muprobe', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'muprobe', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(source); await sw.close();
    localStorage.setItem('fade.activeProject', 'muprobe');
    if (typeof caches !== 'undefined') {
        const names = await caches.keys();
        await Promise.all(names.map((n) => caches.delete(n)));
    }
}, USER_PROGRAM);
await p.reload({ waitUntil: 'domcontentloaded' });
await p.waitForSelector('#run', { timeout: 60_000 });
await p.waitForTimeout(3000);
console.log('✓ Project loaded (browser cache purged)');

await (await p.$('#run')).click();
console.log('→ Run clicked; waiting for monogame iframe to appear…');
// After a cache purge the Blazor runtime has to refetch ~8MB. Give
// it up to 90s before giving up. Once the iframe is in the DOM it's
// at least partway through boot.
let iframeHandle = null;
for (let i = 0; i < 45; i++) {
    iframeHandle = await p.$('#mg-preview-frame');
    if (iframeHandle) break;
    await p.waitForTimeout(2000);
}
if (!iframeHandle) {
    console.error('FAIL: no #mg-preview-frame after 90s — monogame runtime did not boot');
    await b.close();
    process.exit(2);
}
console.log('→ iframe exists; waiting 12s for first microui frame to render…');
await p.waitForTimeout(12_000);

// ── Pre-click screenshot ──────────────────────────────────────────
const screenshotBefore = await iframeHandle.screenshot();
const hashBefore = createHash('sha1').update(screenshotBefore).digest('hex').slice(0, 12);
console.log(`✓ Pre-click screenshot: ${screenshotBefore.length} bytes, sha1=${hashBefore}`);

const beforePath = resolve(__dirname, '..', 'mu-probe-before.png');
const afterPath  = resolve(__dirname, '..', 'mu-probe-after.png');
await iframeHandle.screenshot({ path: beforePath });

// ── Click on the "click me" button ────────────────────────────────
// Hardcoded test panel lives at (20, 20, 280, 200) in canvas-local
// coords. Layout: title (24px), first row label "click the button:",
// second row is the button (full width). With default style padding
// and ~17px text height, the button is around y=70-90 in canvas
// coords. We need to translate canvas-local → page coords via the
// iframe's bounding box.
const ifBox = await iframeHandle.boundingBox();
if (!ifBox) {
    console.error('FAIL: cannot read iframe bounding box');
    await b.close();
    process.exit(2);
}
// The KNI canvas inside the iframe scales to fit; sample what it
// reports so we can map canvas-coords to viewport-coords.
const frame = await iframeHandle.contentFrame();
const canvasInfo = await frame.evaluate(() => {
    const c = document.getElementById('theCanvas');
    if (!c) return null;
    const r = c.getBoundingClientRect();
    return { left: r.left, top: r.top, w: r.width, h: r.height, drawW: c.width, drawH: c.height };
});
if (!canvasInfo) {
    console.error('FAIL: no canvas inside iframe');
    await b.close();
    process.exit(2);
}
console.log('canvas:', JSON.stringify(canvasInfo));

// The user's `begin debug window "test"` opens at the default initial
// rect (16, 16, 320, 280) — title bar 24px tall (16→40), 5px padding,
// then the first widget (the "click" button) sits at canvas y≈45-65
// with full available width starting at x≈21. Click roughly mid-button.
const scaleX = canvasInfo.w / canvasInfo.drawW;
const scaleY = canvasInfo.h / canvasInfo.drawH;
const btnCanvasX = 120;
const btnCanvasY = 55;
const pageX = ifBox.x + canvasInfo.left + btnCanvasX * scaleX;
const pageY = ifBox.y + canvasInfo.top + btnCanvasY * scaleY;
console.log(`→ Clicking at page (${pageX.toFixed(0)}, ${pageY.toFixed(0)})…`);

// microui's button reacts to the *click frame* — it needs a hover
// frame first, then a mousedown on the next frame. We don't need
// to be precise about that here: a single Playwright .click() at the
// coords moves the mouse there (hover frame fires), waits ~10ms,
// presses (focus frame), waits, releases. Across those 30ms several
// game frames tick.
await p.mouse.move(pageX, pageY);
await p.waitForTimeout(200);
await p.mouse.down();
await p.waitForTimeout(50);
await p.mouse.up();
// Give the click 2 ticks to propagate: frame N writes controlIdToBool,
// frame N+1 fbasic reads it and runs `print "clicked"`, frame N+2's
// stdout post arrives in the parent.
await p.waitForTimeout(1500);

const screenshotAfter = await iframeHandle.screenshot();
const hashAfter = createHash('sha1').update(screenshotAfter).digest('hex').slice(0, 12);
console.log(`✓ Post-click screenshot: ${screenshotAfter.length} bytes, sha1=${hashAfter}`);
await iframeHandle.screenshot({ path: afterPath });

// ── Verification ──────────────────────────────────────────────────
// (a) Pre-click screenshot must have meaningfully more bytes than an
//     all-black canvas of the same size would compress to (~600 bytes).
//     The user's minimal "test" + "click" panel compresses to ~2.4KB;
//     1500 is a comfortable floor that distinguishes "panel rendered"
//     from "iframe is black".
const renderedBytesFloor = 1500;
const muRendered = screenshotBefore.length > renderedBytesFloor;

// (b) Post-click pixel content must differ from pre-click. We use
//     byte-length-diff + sha1-mismatch as a cheap "did anything
//     change" signal. The click-counter label rerenders, so even
//     conservative compression will produce a different PNG.
const muClickedThrough = hashAfter !== hashBefore;

// Did the click reach fbasic's `if a then print "clicked"`?
const sawClickedPrint = capturedStdout.some(line => /clicked/i.test(line));

// ── Minimize test ────────────────────────────────────────────────
// Click the minimize triangle in the title bar (top-right corner of
// the first window). After click, the window should collapse to its
// title bar only — the next-frame screenshot should differ substantially
// from the post-click-button screenshot.
//
// First window opens at (16, 16, 320, 280) per DefaultWindow* in
// DebugUISystem.Microui.cs. Title bar height is 24px. Minimize icon
// sits in a 24×24 box at the right edge: x ≈ 16+320-24 = 312, y ≈ 16.
const minBtnCanvasX = 312;
const minBtnCanvasY = 28;
const minPageX = ifBox.x + canvasInfo.left + minBtnCanvasX * scaleX;
const minPageY = ifBox.y + canvasInfo.top + minBtnCanvasY * scaleY;
console.log(`→ Clicking minimize at page (${minPageX.toFixed(0)}, ${minPageY.toFixed(0)})…`);
await p.mouse.move(minPageX, minPageY);
await p.waitForTimeout(150);
await p.mouse.down();
await p.waitForTimeout(50);
await p.mouse.up();
await p.waitForTimeout(800);

const screenshotMinimized = await iframeHandle.screenshot();
const hashMinimized = createHash('sha1').update(screenshotMinimized).digest('hex').slice(0, 12);
console.log(`✓ Post-minimize screenshot: ${screenshotMinimized.length} bytes, sha1=${hashMinimized}`);
const minimizedPath = resolve(__dirname, '..', 'mu-probe-minimized.png');
await iframeHandle.screenshot({ path: minimizedPath });

// Minimizing the window should shrink it visibly → fewer pixels in
// the title-bar-plus-body region → smaller PNG. Re-clicking the same
// spot should restore.
const muMinimized = hashMinimized !== hashAfter && screenshotMinimized.length < screenshotAfter.length;

// Click the minimize triangle again — should restore. After restore
// the screenshot should differ from the minimized state (and be
// closer in byte length to the original).
await p.mouse.move(minPageX, minPageY);
await p.waitForTimeout(150);
await p.mouse.down();
await p.waitForTimeout(50);
await p.mouse.up();
await p.waitForTimeout(800);
const screenshotRestored = await iframeHandle.screenshot();
const hashRestored = createHash('sha1').update(screenshotRestored).digest('hex').slice(0, 12);
const restoredPath = resolve(__dirname, '..', 'mu-probe-restored.png');
await iframeHandle.screenshot({ path: restoredPath });
console.log(`✓ Post-restore screenshot: ${screenshotRestored.length} bytes, sha1=${hashRestored} → ${restoredPath}`);
const muRestored = hashRestored !== hashMinimized && screenshotRestored.length > screenshotMinimized.length;

console.log('');
console.log(muRendered ? '✓ microui panel is rendering' : '✗ canvas is empty / panel not rendering');
console.log(muClickedThrough ? '✓ click registered (post-click frame differs)' : '✗ click had no visible effect');
console.log(sawClickedPrint
    ? '✓ fbasic round-trip: `print "clicked"` reached stdout'
    : '✗ fbasic round-trip: no `clicked` line in stdout (' + capturedStdout.length + ' lines captured)');
console.log(muMinimized
    ? '✓ minimize: window collapsed to title bar (PNG shrunk ' + screenshotAfter.length + ' → ' + screenshotMinimized.length + ')'
    : '✗ minimize: window did not shrink (' + screenshotAfter.length + ' → ' + screenshotMinimized.length + ')');
console.log(muRestored
    ? '✓ restore: re-clicked triangle brought the body back (' + screenshotMinimized.length + ' → ' + screenshotRestored.length + ')'
    : '✗ restore: body did not come back');
if (capturedStdout.length) console.log('  stdout: ' + JSON.stringify(capturedStdout.slice(0, 5)));
console.log('→ screenshots saved: ' + beforePath + ', ' + afterPath + ', ' + minimizedPath);

await b.close();
const pass = fatalPageErrors.length === 0 && muRendered && muClickedThrough && sawClickedPrint && muMinimized && muRestored;
process.exit(pass ? 0 : 1);
