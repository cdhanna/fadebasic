// Two-tab Playwright probe for the observer's Debug UI render bug.
//
// Replicates the user's scenario byte-for-byte:
//   1. Open Page A as the host. Create a monogame project, start a live
//      session using the MOCK transport (BroadcastChannel — works between
//      tabs in the same browser context, no external trackers needed).
//   2. Open Page B as the observer. Join the same room over mock.
//   3. From Page A, broadcast the EXACT debug-ui-frame envelope the
//      user saw in their `debug-ui-collab` logs (WINDOW_START "shaders"
//      + FLOAT_SLIDER "Glitch Amount" + 2x ARG_FLOAT + WINDOW_END).
//   4. On Page B, inspect what actually rendered into the Debug UI tab.
//
// If the observer's Debug UI tab is blank in production, this probe
// will reproduce it. The single-page version of the probe missed it
// because the bug only manifests once a live session has been joined
// (the layout shifts when Live Session panel becomes active).
//
// Usage:
//   cd Playground && npm run dev   # one terminal
//   node scripts/probe-debug-ui-relay-2tab.mjs   # another

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const ROOM_ID = 'probe-' + Math.random().toString(36).slice(2, 10);

// One persistent browser context so both pages share BroadcastChannel
// (mock transport's wire). Headless. No GL needed — we never run the
// monogame iframe in this probe.
const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ viewport: { width: 1400, height: 900 } });

async function openPage(label) {
    const page = await ctx.newPage();
    page.on('pageerror', (e) => console.log(`[${label} PE]`, e.message.slice(0, 200)));
    page.on('console', (m) => {
        const t = m.text();
        if (/Possible EventEmitter|deprecated|webgl|^GET /i.test(t)) return;
        // Surface app-relevant logs.
        if (/debug-ui-collab|fade-collab/.test(t)) {
            console.log(`[${label}]`, t.slice(0, 240));
        }
    });
    return page;
}

const hostPage = await openPage('HOST');
const obsPage = await openPage('OBS ');

// ── Page A (host) bootstrap ────────────────────────────────────────
await hostPage.goto('http://localhost:5311/', { waitUntil: 'domcontentloaded' });

await hostPage.evaluate(async (roomId) => {
    // Seed a minimal MONOGAME project — same project type the user is
    // testing. (The probe doesn't actually boot the iframe, but the
    // dockview layout + UI flow follow the monogame branch.)
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('dbgui-2tab-host', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'dbgui-2tab-host', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write('do\n  sync\nloop\n'); await sw.close();
    localStorage.setItem('fade.activeProject', 'dbgui-2tab-host');
    // Reset dockview saved layout to default — eliminate the
    // "user has a weird saved layout" variable.
    for (const k of Object.keys(localStorage)) {
        if (k.startsWith('fade.dockview') || k.includes('dockview')) localStorage.removeItem(k);
    }
}, ROOM_ID);
await hostPage.reload({ waitUntil: 'domcontentloaded' });
await hostPage.waitForFunction(
    () => !!(window).__fadeBootstrapDone
        && !!(window).__fadeDockview
        && !!(window).__fadeDebugUiHandle
        && !!(window).__fadeCollabBootstrap,
    { timeout: 90_000 },
);
console.log('→ host: playground booted');

// Start a live session via the bootstrap API — bypass the panel UI
// for determinism. We call into the same surface the panel uses.
const hostStarted = await hostPage.evaluate(async (roomId) => {
    const boot = (window).__fadeCollabBootstrap;
    if (!boot) return { ok: false, error: 'no __fadeCollabBootstrap' };
    try {
        await boot.startHost?.({ roomId, transportId: 'mock', displayName: 'ProbeHost' });
        return { ok: true };
    } catch (e) {
        return { ok: false, error: String(e?.message ?? e) };
    }
}, ROOM_ID);
console.log('→ host session start:', hostStarted);

// ── Page B (observer) bootstrap ────────────────────────────────────
await obsPage.goto('http://localhost:5311/', { waitUntil: 'domcontentloaded' });

await obsPage.evaluate(async () => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    await ws.getDirectoryHandle('blank-obs', { create: true });
    localStorage.setItem('fade.activeProject', 'blank-obs');
    for (const k of Object.keys(localStorage)) {
        if (k.startsWith('fade.dockview') || k.includes('dockview')) localStorage.removeItem(k);
    }
});
await obsPage.reload({ waitUntil: 'domcontentloaded' });
await obsPage.waitForFunction(
    () => !!(window).__fadeBootstrapDone
        && !!(window).__fadeDockview
        && !!(window).__fadeDebugUiHandle
        && !!(window).__fadeCollabBootstrap,
    { timeout: 90_000 },
);
console.log('→ obs:  playground booted');

const obsJoined = await obsPage.evaluate(async (roomId) => {
    const boot = (window).__fadeCollabBootstrap;
    if (!boot) return { ok: false, error: 'no __fadeCollabBootstrap' };
    try {
        await boot.startJoin?.({ roomId, transportId: 'mock', displayName: 'ProbeObs' });
        return { ok: true };
    } catch (e) {
        return { ok: false, error: String(e?.message ?? e) };
    }
}, ROOM_ID);
console.log('→ obs  session join:', obsJoined);

// Wait for the sessions to discover each other.
await new Promise((r) => setTimeout(r, 1500));

const peersHost = await hostPage.evaluate(() => (window).__fadeCollab?.peers?.length ?? -1);
const peersObs = await obsPage.evaluate(() => (window).__fadeCollab?.peers?.length ?? -1);
console.log(`→ peers host=${peersHost}, obs=${peersObs}`);

// Directly drive the OBSERVER's applyFrameEnvelope while the live
// session is active. Bypasses the mock-transport relay (which has
// timing issues between Playwright pages) so we isolate the question
// "does the panel render correctly when a session is active?". If
// this passes but the user still sees blank, the relay's failing
// somewhere ELSE; if this fails, the bug is in how the panel behaves
// with a session attached.
const broadcasted = await obsPage.evaluate(() => {
    const handle = (window).__fadeDebugUiHandle;
    if (!handle) return { ok: false, error: 'no __fadeDebugUiHandle' };
    handle.applyFrameEnvelope({ gen: 0, queue: [], autoInspector: false });
    handle.applyFrameEnvelope({
        gen: 1,
        queue: [
            { id: 88660769, t: 0, l: 'shaders', s: null, i: 0, f: 0 },
            { id: 2143514761, t: 15, l: 'Glitch Amount', s: null, i: 0, f: 25 },
            { id: -1369638928, t: 22, l: null, s: null, i: 0, f: 0 },
            { id: -1369638928, t: 22, l: null, s: null, i: 0, f: 100 },
            { id: 88660769, t: 1, l: null, s: null, i: 0, f: 0 },
        ],
        autoInspector: false,
    });
    return { ok: true };
});
console.log('→ observer direct-applied frames:', broadcasted);

await new Promise((r) => setTimeout(r, 300));

// ── Activate the Debug UI tab on observer ──────────────────────────
await obsPage.evaluate(() => {
    const dock = (window).__fadeDockview;
    dock?.getPanel?.('debug-ui')?.api?.setActive?.();
});
await new Promise((r) => setTimeout(r, 200));

// ── Probe dockview's view of the world ─────────────────────────────
const dockState = await obsPage.evaluate(() => {
    const dock = (window).__fadeDockview;
    if (!dock) return { error: 'no __fadeDockview' };
    const allPanels = dock.panels.map((p) => ({
        id: p.id,
        component: p.api.component ?? '?',
        isActive: p.api.isActive,
        isVisible: p.api.isVisible,
        groupId: p.group?.api?.id ?? '?',
    }));
    const debugUiPanels = allPanels.filter((p) => p.component === 'debug-ui');
    const debugUiHosts = document.querySelectorAll('#debug-ui-host').length;
    const debugUiCells = document.querySelectorAll('.panel-cell[data-panel="debug-ui"]').length;
    return { debugUiPanels, debugUiHosts, debugUiCells, totalPanels: allPanels.length };
});
console.log('\n── DOCKVIEW STATE ──');
console.log(JSON.stringify(dockState, null, 2));

// ── Inspect observer DOM ───────────────────────────────────────────
const result = await obsPage.evaluate(() => {
    const out = {};
    const host = document.getElementById('debug-ui-host');
    if (!host) { out.error = 'no #debug-ui-host on observer'; return out; }
    const cs = getComputedStyle(host);
    out.hostInlineVisibility = host.style.visibility;
    out.hostComputedVisibility = cs.visibility;
    const rect = host.getBoundingClientRect();
    out.hostSize = `${Math.round(rect.width)}x${Math.round(rect.height)}`;
    let p = host.parentElement;
    while (p && !p.classList.contains('dv-render-overlay')) p = p.parentElement;
    if (p) {
        out.overlayInlineVisibility = p.style.visibility;
        out.overlayComputedVisibility = getComputedStyle(p).visibility;
    } else {
        out.overlayInlineVisibility = '<no dv-render-overlay>';
    }
    out.sliderCount = host.querySelectorAll('.tp-sldv').length;
    out.bindingCount = host.querySelectorAll('.tp-lblv').length;
    out.titleText = host.querySelector('.tp-rotv_t')?.textContent ?? '';
    out.idleHintShown = (host.textContent ?? '').includes('Run your program');
    return out;
});

console.log('\n── OBSERVER DOM ──');
console.log(JSON.stringify(result, null, 2));

const passed = result.hostComputedVisibility === 'visible'
    && result.sliderCount >= 1
    && result.titleText === 'shaders'
    && !result.idleHintShown;

console.log('\n── VERDICT ──');
console.log(`host computed visibility:  ${result.hostComputedVisibility} ${result.hostComputedVisibility === 'visible' ? '✓' : '✗'}`);
console.log(`slider rendered:           ${result.sliderCount} ${result.sliderCount >= 1 ? '✓' : '✗'}`);
console.log(`title "shaders":           "${result.titleText}" ${result.titleText === 'shaders' ? '✓' : '✗'}`);
console.log(`idle hint hidden:          ${!result.idleHintShown} ${!result.idleHintShown ? '✓' : '✗ STILL SHOWING'}`);
console.log(passed ? '\nPASS' : '\nFAIL');

await browser.close();
process.exit(passed ? 0 : 1);
