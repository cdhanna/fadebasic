// Probe for the "observer Debug UI tab renders blank" bug.
//
// Reproduces what the user reported on 2026-06-03: the host's program
// emits a debug-ui-frame envelope with WINDOW_START "shaders" +
// FLOAT_SLIDER, the relay delivers it to the observer's session, the
// observer's #debug-ui-host contains the rendered Tweakpane Pane, BUT
// the dockview `dv-render-overlay` ancestor is stuck on
// `visibility: hidden` (anti-flicker initial state that never cleared).
//
// We don't need an actual live session to repro the dockview side of
// the bug. We boot one playground page, mount the panel, and directly
// invoke debugUiHandle.applyFrameEnvelope (exposed for testing) with
// the same envelope the observer would receive. Then we check that the
// computed `visibility` on #debug-ui-host is `visible` — not hidden.
//
// If the fix in main.ts is regressed, this probe fails with a clear
// "visibility=hidden" assertion error, no manual screenshot required.
//
// Usage: dev server must be running on :5311 first.
//   cd Playground && npm run dev   # in one terminal
//   node scripts/probe-debug-ui-visibility.mjs   # in another

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const browser = await chromium.launch({
    headless: true,
    args: ['--use-gl=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'],
});
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });

const captured = [];
page.on('pageerror', (e) => captured.push(`[PE] ${e.message.slice(0, 400)}`));
page.on('console', (m) => {
    const t = m.text();
    if (/Possible EventEmitter|deprecated|webgl|^GET /i.test(t)) return;
    captured.push(`[${m.type()}] ${t.slice(0, 600)}`);
});

await page.goto('http://localhost:5311/', { waitUntil: 'domcontentloaded' });

// Seed a 'web' project so the playground boots cleanly into a project
// — the Debug UI panel exists regardless of project type, so we don't
// need monogame's iframe to set up.
await page.evaluate(async () => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('dbgui-vis-probe', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'dbgui-vis-probe', type: 'web',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write('print "ok"\n'); await sw.close();
    localStorage.setItem('fade.activeProject', 'dbgui-vis-probe');
});
await page.reload({ waitUntil: 'domcontentloaded' });

// Wait for dockview + the panel to mount. __fadeDockview is exposed by
// main.ts right after `setupDockview()`; debug-ui-host is in the static
// panel-cells pool and reparented into a render-overlay by dockview.
await page.waitForFunction(
    () => !!(window).__fadeDockview
        && !!document.getElementById('debug-ui-host')
        && !!(window).__fadeDebugUiHandle,
    { timeout: 60_000 },
);
console.log('→ playground booted, dockview ready');

// Activate the Debug UI tab so we're actually exercising the visible
// path. Some restored layouts leave it as a non-active sibling.
await page.evaluate(() => {
    const dock = (window).__fadeDockview;
    const panel = dock?.getPanel?.('debug-ui');
    if (panel?.api?.setActive) panel.api.setActive();
});

// Give dockview one rAF tick to settle its overlay visibility.
await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => r())));

// Now feed the EXACT envelope shape the user reported via the live
// session — WINDOW_START "shaders" + FLOAT_SLIDER "Glitch Amount" +
// 2x ARG_FLOAT + WINDOW_END. We synthesize a `debug-ui-frame` postMessage
// the way the monogame iframe would, and rely on monoGameHost's parent-
// window listener to dispatch it. Falls back to a direct
// applyFrameEnvelope on the exposed handle if the postMessage bridge
// isn't set up in this project type.
const result = await page.evaluate(async () => {
    const out = {};
    // The relay is what feeds the observer in production; here we just
    // need to drive applyFrameEnvelope. Both monogame and web projects
    // mount the same debug-ui-panel — we reach in via the host module.
    // The simpler path: post a 'debug-ui-frame' to window with the JSON.
    // monoGameHost listens on `message` and dispatches.
    const env = {
        gen: 1,
        queue: [
            { id: 88660769, t: 0, l: 'shaders', s: null, i: 0, f: 0 },
            { id: 2143514761, t: 15, l: 'Glitch Amount', s: null, i: 0, f: 25 },
            { id: -1369638928, t: 22, l: null, s: null, i: 0, f: 0 },
            { id: -1369638928, t: 22, l: null, s: null, i: 0, f: 100 },
            { id: 88660769, t: 1, l: null, s: null, i: 0, f: 0 },
        ],
        autoInspector: false,
    };
    // Drive the panel directly via the test handle. This bypasses the
    // monogame postMessage bridge (which doesn't fire for 'web' project
    // type) and exercises the exact apply path observers use.
    const handle = (window).__fadeDebugUiHandle;
    handle.applyFrameEnvelope({ gen: 0, queue: [], autoInspector: false });
    handle.applyFrameEnvelope(env);
    await new Promise((r) => setTimeout(r, 50));

    const host = document.getElementById('debug-ui-host');
    if (!host) { out.error = 'no #debug-ui-host'; return out; }

    // Computed visibility — the assertion we actually care about.
    out.hostVisibility = getComputedStyle(host).visibility;

    // Walk ancestors to find the dv-render-overlay and report ITS
    // computed visibility too — that's the layer dockview stamps with
    // visibility:hidden.
    let p = host.parentElement;
    while (p && !p.classList.contains('dv-render-overlay')) p = p.parentElement;
    out.overlayInlineVisibility = p?.style.visibility ?? '<no-overlay>';
    out.overlayComputedVisibility = p ? getComputedStyle(p).visibility : '<no-overlay>';

    // What did the panel actually render?
    out.sliderCount = host.querySelectorAll('.tp-sldv').length;
    out.bindingCount = host.querySelectorAll('.tp-lblv').length;
    out.titleEl = host.querySelector('.tp-rotv_t')?.textContent ?? '';
    return out;
});

console.log('\n── RESULT ──');
console.log(JSON.stringify(result, null, 2));

console.log('\n── CAPTURED ──');
for (const c of captured.slice(-20)) console.log(c);

const passed = result.hostVisibility === 'visible'
    && result.sliderCount >= 1
    && result.titleEl === 'shaders';
console.log('\n── VERDICT ──');
console.log(`#debug-ui-host visibility: ${result.hostVisibility} ${result.hostVisibility === 'visible' ? '✓' : '✗ stuck hidden'}`);
console.log(`overlay inline / computed: ${result.overlayInlineVisibility} / ${result.overlayComputedVisibility}`);
console.log(`slider rendered:           ${result.sliderCount} ${result.sliderCount >= 1 ? '✓' : '✗ none'}`);
console.log(`window title:              "${result.titleEl}" ${result.titleEl === 'shaders' ? '✓' : '✗'}`);
console.log(passed ? '\nPASS' : '\nFAIL');

await browser.close();
process.exit(passed ? 0 : 1);
