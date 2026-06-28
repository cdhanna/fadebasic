// Probe: observer's slider change must reach the host.
//
// Two pages in one browser context. Host starts a live session; observer
// joins. Host pushes a debug-ui-frame envelope (the user's "shaders"
// window with a FLOAT_SLIDER). Observer's panel renders. Then we
// programmatically trigger the slider's `change` event from the
// observer side, mimicking a real user drag. We assert that the host's
// `monoGameHost.sendDebugUiChange` is called with the same args.
//
// Failure means the observer→host input channel is broken — usually:
//   - getRemoteRunnerPeerId returns null on the observer (no peer is
//     marked active runner)
//   - the host's `debugUi:sendFbasicChange` RPC handler isn't
//     registered, OR
//   - the panel callback dispatch doesn't actually fire on Tweakpane
//     `change` events the way we expect.
//
// Usage: dev server on :5311.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const ROOM_ID = 'probe-' + Math.random().toString(36).slice(2, 10);

const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ viewport: { width: 1400, height: 900 } });

async function openPage(label) {
    const page = await ctx.newPage();
    page.on('pageerror', (e) => console.log(`[${label} PE]`, e.message.slice(0, 200)));
    page.on('console', (m) => {
        const t = m.text();
        if (/Possible EventEmitter|deprecated|webgl|^GET /i.test(t)) return;
        if (/debug-ui-collab/.test(t)) {
            console.log(`[${label}]`, t.slice(0, 240));
        }
    });
    return page;
}

const hostPage = await openPage('HOST');
const obsPage = await openPage('OBS ');

// ── Host bootstrap ─────────────────────────────────────────────────
await hostPage.goto('http://localhost:5311/', { waitUntil: 'domcontentloaded' });
await hostPage.evaluate(async () => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('input-probe-host', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'input-probe-host', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    });
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write('do\n  sync\nloop\n'); await sw.close();
    localStorage.setItem('fade.activeProject', 'input-probe-host');
    for (const k of Object.keys(localStorage)) {
        if (k.startsWith('fade.dockview') || k.includes('dockview')) localStorage.removeItem(k);
    }
});
await hostPage.reload({ waitUntil: 'domcontentloaded' });
await hostPage.waitForFunction(
    () => !!(window).__fadeBootstrapDone && !!(window).__fadeCollabBootstrap,
    { timeout: 90_000 },
);
const hostRoomId = await hostPage.evaluate(async () => {
    const session = await (window).__fadeCollabBootstrap.startHost({
        transportId: 'mock', displayName: 'ProbeHost',
    });
    return session.__roomId;
});
console.log('→ host room id:', hostRoomId);

// Patch the host's monoGameHost so we can observe sendDebugUiChange
// calls without booting the iframe.
await hostPage.evaluate(() => {
    (window).__capturedDebugUiChanges = [];
    // monoGameHost is the module-level singleton — patch the prototype
    // method on the instance.
    const original = (window).__fadeCollab?.session
        ? null : null;
    // Reach into the module via the bootstrap shim's known refs. The
    // simplest robust hook: intercept at the host's RPC dispatch by
    // re-registering the channel. session.onRequest replaces existing
    // handlers for the same channel.
    const session = (window).__fadeCollab?.session;
    if (session) {
        session.onRequest('debugUi:sendFbasicChange', (_peerId, payload) => {
            (window).__capturedDebugUiChanges.push(payload);
            return { ok: true };
        });
    }
});

// Mark the host as "running" so getRemoteRunnerPeerId on the observer
// finds us. broadcastLiveActivity normally does this when runActive
// flips true; we shortcut by setting awareness directly.
//
// Also override host's monoGameHost.onDebugUiFrame so the iframe's
// stream of empty envelopes is replaced with our real "shaders"
// envelope. Otherwise empty frames at gen=0 would race the observer's
// apply path and wipe the slider before we can drag it.
await hostPage.evaluate(async () => {
    const session = (window).__fadeCollab?.session;
    session?.setActivity?.('running');
    // Replace monoGameHost.onDebugUiFrame so the iframe's stream of
    // empty envelopes doesn't get broadcast — they would race our
    // shaders envelope below and wipe the observer's slider Pane.
    // Import the module to reach the singleton.
    const mod = await import('/src/monogame-host.ts');
    if (mod?.monoGameHost) {
        mod.monoGameHost.onDebugUiFrame = () => { /* no-op during probe */ };
    }
    const shadersJson = JSON.stringify({
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
    // Repeatedly broadcast the real envelope so observer's lastGen
    // stays at 1 and the slider Pane doesn't get wiped.
    (window).__shadersBroadcastTimer = setInterval(() => {
        try { session?.sendDebugUiFrame?.(shadersJson); } catch { /* ignore */ }
    }, 30);
});

console.log('→ host: session running, broadcasting shaders envelope');

// ── Observer bootstrap ─────────────────────────────────────────────
await obsPage.goto('http://localhost:5311/', { waitUntil: 'domcontentloaded' });
await obsPage.evaluate(async () => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    await ws.getDirectoryHandle('input-probe-obs', { create: true });
    localStorage.setItem('fade.activeProject', 'input-probe-obs');
    for (const k of Object.keys(localStorage)) {
        if (k.startsWith('fade.dockview') || k.includes('dockview')) localStorage.removeItem(k);
    }
});
await obsPage.reload({ waitUntil: 'domcontentloaded' });
await obsPage.waitForFunction(
    () => !!(window).__fadeBootstrapDone && !!(window).__fadeCollabBootstrap,
    { timeout: 90_000 },
);
await obsPage.evaluate(async (roomId) => {
    await (window).__fadeCollabBootstrap.startJoin({
        roomId, transportId: 'mock', displayName: 'ProbeObs',
    });
}, hostRoomId);
await new Promise((r) => setTimeout(r, 800));

// Sanity: observer should see host's activity as "running" so its
// remote-runner detection returns the host's peerId.
const sanity = await obsPage.evaluate(() => {
    const session = (window).__fadeCollab?.session;
    const peers = session?.getState()?.peers ?? [];
    return peers.map((p) => ({ name: p.identity?.displayName, isSelf: p.isSelf, activity: p.activity, peerId: p.peerId ? p.peerId.slice(0, 8) : null }));
});
console.log('→ obs peers:', JSON.stringify(sanity));

// Apply a real shaders envelope on the observer so the panel actually
// has a slider to interact with.
await obsPage.evaluate(() => {
    const handle = (window).__fadeDebugUiHandle;
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
});
await new Promise((r) => setTimeout(r, 200));

// Dump what Tweakpane rendered so we know what to drive.
const sliderDom = await obsPage.evaluate(() => {
    const root = document.getElementById('debug-ui-host');
    if (!root) return { error: 'no host' };
    const sldv = root.querySelector('.tp-sldv');
    return {
        rotvHtml: root.querySelector('.tp-rotv')?.innerHTML?.slice(0, 1500) ?? null,
        sldvHtml: sldv?.outerHTML?.slice(0, 800) ?? null,
        allInputs: Array.from(root.querySelectorAll('input')).map((i) => ({
            type: i.type, classList: i.className, value: i.value,
        })),
    };
});
console.log('\n── OBS SLIDER DOM ──');
console.log(JSON.stringify(sliderDom, null, 2));

// Trigger via the most likely Tweakpane v4 path: text field next to
// the slider track. Tweakpane wires a 'change' event on its internal
// value controller; firing on the visible text input usually flows
// through.
const triggered = await obsPage.evaluate(() => {
    const root = document.getElementById('debug-ui-host');
    if (!root) return { ok: false, error: 'no host' };
    // Most likely candidates in order.
    const candidates = [
        root.querySelector('.tp-sldv input'),
        root.querySelector('.tp-txtv input'),
        root.querySelector('.tp-lblv input'),
        root.querySelector('input'),
    ].filter(Boolean);
    if (candidates.length === 0) return { ok: false, error: 'no input candidates' };
    const target = candidates[0];
    target.focus();
    target.value = '77';
    // Tweakpane usually listens on 'change' AFTER a value is committed;
    // some controllers commit on 'input', some on 'change' or 'blur'.
    target.dispatchEvent(new Event('input', { bubbles: true }));
    target.dispatchEvent(new Event('change', { bubbles: true }));
    target.dispatchEvent(new Event('blur', { bubbles: true }));
    return { ok: true, targetTag: target.tagName, targetType: target.type, targetClass: target.className };
});
console.log('→ obs slider triggered:', triggered);

await new Promise((r) => setTimeout(r, 400));

// ── Verify the host received the change ───────────────────────────
const captured = await hostPage.evaluate(() => (window).__capturedDebugUiChanges ?? []);
console.log('\n── HOST CAPTURED CHANGES ──');
console.log(JSON.stringify(captured, null, 2));

const passed = captured.length > 0
    && captured.some((c) => c.ctrlId === 2143514761 && c.kind === 2);

console.log('\n── VERDICT ──');
console.log(`changes reached host:      ${captured.length}`);
console.log(`expected change present:   ${passed ? '✓' : '✗ host never saw the slider drag'}`);
console.log(passed ? '\nPASS' : '\nFAIL');

await browser.close();
process.exit(passed ? 0 : 1);
