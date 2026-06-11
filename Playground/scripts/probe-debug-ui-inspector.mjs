// Two-tab probe for the auto-inspector entity-data path.
//
// Replicates "enable debug inspector" + a sprite provider on the host,
// joins as an observer, and checks that the observer's debug UI panel
// renders the sprite's FIELDS (not just the count). This is the path
// the user reports broken: "I can see there is 1 sprite, but I don't
// SEE the sprite data."
//
// Strategy:
//   1. Host starts a session over mock transport, marks itself active.
//   2. Host's monoGameHost.debugGetSchema / debugGetEntity are stubbed
//      to return sample sprite data — bypasses the real iframe so the
//      probe doesn't depend on WebGL / Blazor boot.
//   3. Observer joins. Host broadcasts an envelope with autoInspector
//      = true and entities = { sprite: [1] } at 30ms cadence.
//   4. Observer's panel applies the envelope, which triggers
//      buildTypeFolder("sprite", [1]) → addEntities → opts.getEntity
//      via RPC → host stub returns the snapshot → buildBindingsFor
//      renders one .tp-lblv per schema field.
//   5. We poll the observer DOM for those .tp-lblv rows.
//
// PASS = observer's #debug-ui-host contains at least one binding row
// from the sprite type. FAIL = the host returns data but the rows
// don't render, OR the observer's RPC never reaches the host.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ viewport: { width: 1400, height: 900 } });

async function openPage(label) {
    const page = await ctx.newPage();
    page.on('pageerror', (e) => console.log(`[${label} PE]`, e.message.slice(0, 200)));
    page.on('console', (m) => {
        const t = m.text();
        if (/Possible EventEmitter|deprecated|webgl|^GET /i.test(t)) return;
        if (/debug-ui-collab/.test(t)) console.log(`[${label}]`, t.slice(0, 240));
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
    const dir = await ws.getDirectoryHandle('inspector-probe-host', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'inspector-probe-host', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    });
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write('do\n  sync\nloop\n'); await sw.close();
    localStorage.setItem('fade.activeProject', 'inspector-probe-host');
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

// Wire host: set activity=running, stub monoGameHost inspector methods
// with sample sprite data, replace the iframe's empty envelope stream
// with one that has autoInspector + entities map.
await hostPage.evaluate(async () => {
    const session = (window).__fadeCollab?.session;
    session?.setActivity?.('running');

    // Stub the monoGameHost inspector surface so the host's
    // debugUi:* RPC handlers (which forward to monoGameHost) return
    // real data without a live iframe. Replace via the imported
    // module so the singleton's methods are intercepted.
    const mod = await import('/src/monogame-host.ts');
    if (mod?.monoGameHost) {
        mod.monoGameHost.debugListTypes = async () => ['sprite'];
        mod.monoGameHost.debugGetSchema = async (typeName) => {
            if (typeName !== 'sprite') return null;
            return [
                { path: 'x', type: 'float', label: 'X', min: 0, max: 1000 },
                { path: 'y', type: 'float', label: 'Y', min: 0, max: 1000 },
                { path: 'visible', type: 'bool', label: 'Visible' },
            ];
        };
        mod.monoGameHost.debugGetEntitySchema = async () => null;
        mod.monoGameHost.debugListEntities = async (typeName) => typeName === 'sprite' ? [1] : [];
        mod.monoGameHost.debugGetLabels = async () => ({ '1': 'sprite #1' });
        mod.monoGameHost.debugGetEntity = async (typeName, id) => {
            if (typeName !== 'sprite' || id !== 1) return null;
            return { x: 128, y: 256, visible: true };
        };
        mod.monoGameHost.debugSetField = async () => true;
        // Replace onDebugUiFrame so the iframe's empty envelope stream
        // doesn't compete with our broadcast.
        mod.monoGameHost.onDebugUiFrame = () => { /* swallow */ };
    }
    const envelopeJson = JSON.stringify({
        gen: 1,
        queue: [],
        autoInspector: true,
        metadata: {},
        entities: { sprite: [1] },
    });
    (window).__inspectorBroadcastTimer = setInterval(() => {
        try { session?.sendDebugUiFrame?.(envelopeJson); } catch { /* ignore */ }
    }, 30);
});
console.log('→ host: session running, broadcasting autoInspector envelope');

// ── Observer bootstrap ─────────────────────────────────────────────
await obsPage.goto('http://localhost:5311/', { waitUntil: 'domcontentloaded' });
await obsPage.evaluate(async () => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    await ws.getDirectoryHandle('inspector-probe-obs', { create: true });
    localStorage.setItem('fade.activeProject', 'inspector-probe-obs');
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

// Activate Debug UI tab on observer so the panel content is observed.
await obsPage.evaluate(() => {
    const dock = (window).__fadeDockview;
    dock?.getPanel?.('debug-ui')?.api?.setActive?.();
});

// Wait for the inspector to potentially mount + the type folder to
// build + the entity bindings to render. Inspector entity fetch goes
// through opts.getEntity → RPC → host stub → sample data. Give it up
// to 3s.
const deadline = Date.now() + 3000;
let domState = null;
while (Date.now() < deadline) {
    domState = await obsPage.evaluate(() => {
        const host = document.getElementById('debug-ui-host');
        if (!host) return null;
        // Expand every Tweakpane folder so the entity folder's contents
        // are queried (the panel's entity polling is gated on `expanded`).
        host.querySelectorAll('.tp-fldv').forEach((f) => {
            if (!f.classList.contains('tp-fldv-expanded')) {
                const btn = f.querySelector('.tp-fldv_b');
                if (btn instanceof HTMLElement) btn.click();
            }
        });
        const bindingRows = host.querySelectorAll('.tp-lblv').length;
        const sliderRows = host.querySelectorAll('.tp-sldv').length;
        const folders = Array.from(host.querySelectorAll('.tp-fldv_t')).map((el) => el.textContent?.trim() ?? '');
        const labels = Array.from(host.querySelectorAll('.tp-lblv_l')).map((el) => el.textContent?.trim() ?? '');
        return { bindingRows, sliderRows, folders, labels };
    });
    if (domState && domState.bindingRows >= 3) break;
    await new Promise((r) => setTimeout(r, 100));
}

console.log('\n── OBSERVER DOM ──');
console.log(JSON.stringify(domState, null, 2));

const passed = domState
    && domState.bindingRows >= 3
    && domState.labels.includes('X')
    && domState.labels.includes('Y');

console.log('\n── VERDICT ──');
console.log(`binding rows rendered:     ${domState?.bindingRows ?? '?'}`);
console.log(`folders present:           ${JSON.stringify(domState?.folders ?? [])}`);
console.log(`labels rendered:           ${JSON.stringify(domState?.labels ?? [])}`);
console.log(`X label present:           ${domState?.labels?.includes('X') ? '✓' : '✗'}`);
console.log(`Y label present:           ${domState?.labels?.includes('Y') ? '✓' : '✗'}`);
console.log(passed ? '\nPASS' : '\nFAIL');

await browser.close();
process.exit(passed ? 0 : 1);
