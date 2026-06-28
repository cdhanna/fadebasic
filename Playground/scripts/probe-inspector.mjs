// End-to-end smoke test for the new IDebugProvider inspector pipeline.
// Verifies the round-trip we care about:
//
//   1. C# providers are reachable. DebugListTypes returns at least
//      "sprite" and "metadata" (Game1.Initialize registers both).
//   2. Metadata snapshot works. DebugGetEntity("metadata", 0) returns
//      a non-null object containing "fps" and "spriteCount".
//   3. Sprite edits round-trip. After creating one sprite via fbasic,
//      DebugListEntities("sprite") returns its id, DebugGetEntity
//      returns its current state, and DebugSetField("sprite", id,
//      "rotation", "1.5") changes the next snapshot's rotation field.
//
// The probe drives the JS bridge directly (via monoGameHost) so we
// can keep the assertions tight + diagnostic. The Tweakpane panel
// itself is layered on top of these same calls; its rendering is
// visually inspected via screenshots in probe-microui.mjs.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??=
    resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.PROBE_URL ?? 'http://localhost:5311/';
const HEADLESS = process.env.PROBE_HEADED !== '1';

const errors = [];

const b = await chromium.launch({ headless: HEADLESS });
const p = await b.newPage({ viewport: { width: 1400, height: 900 } });

p.on('pageerror', (e) => {
    // Same filter as probe-microui: ignore unrelated Playground
    // bootstrap errors, fail only on inspector-related exceptions.
    const msg = e.message || '';
    const isOurs = /DebugRegistry|IDebugProvider|debug-inspector|SpriteDebugProvider|MetadataDebugProvider/i.test(msg);
    console.log(isOurs ? '[PE-INSP]' : '[PE-ignored]', msg.slice(0, 400));
    if (isOurs) errors.push(msg);
});
p.on('console', (m) => {
    if (m.type() === 'error') console.log('[console.error]', m.text().slice(0, 400));
});

await p.goto(URL, { waitUntil: 'domcontentloaded' });
await p.waitForSelector('#run', { timeout: 60_000 });
console.log('✓ Playground UI mounted');

// Seed a program that creates one sprite — enough state to verify
// the sprite provider's listIds + snapshot + apply paths.
// `sprite 1, 100, 100, 0` creates sprite with id 1 at (100, 100) with
// no texture. textureId=0 is fine — we just need the sprite registered
// in SpriteSystem.sprites so the provider can list and snapshot it.
const USER_PROGRAM =
    'sprite 1, 100, 100, 0\n' +
    'do\n' +
    '    sync\n' +
    'loop\n';

await p.evaluate(async (source) => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('insprobe', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'insprobe', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(source); await sw.close();
    localStorage.setItem('fade.activeProject', 'insprobe');
    if (typeof caches !== 'undefined') {
        const names = await caches.keys();
        await Promise.all(names.map((n) => caches.delete(n)));
    }
}, USER_PROGRAM);
await p.reload({ waitUntil: 'domcontentloaded' });
await p.waitForSelector('#run', { timeout: 60_000 });
await p.waitForTimeout(3000);
console.log('✓ Project loaded (cache purged)');

await (await p.$('#run')).click();
console.log('→ Run clicked; waiting for monogame iframe…');

let iframeHandle = null;
for (let i = 0; i < 45; i++) {
    iframeHandle = await p.$('#mg-preview-frame');
    if (iframeHandle) break;
    await p.waitForTimeout(2000);
}
if (!iframeHandle) {
    console.error('FAIL: no #mg-preview-frame after 90s');
    await b.close(); process.exit(2);
}
console.log('→ iframe present; polling DebugListTypes until providers appear…');
// Poll for up to 60s for the providers to register. We call the
// iframe's theInstance.invokeMethodAsync directly (no postMessage
// hop) because importing monogame-host inside p.evaluate doesn't
// resolve to the same module instance Playground main.ts uses.
let ready = false;
for (let i = 0; i < 30; i++) {
    await p.waitForTimeout(2000);
    const peek = await p.evaluate(async () => {
        const f = document.getElementById('mg-preview-frame');
        const cw = f && f.contentWindow;
        if (!cw || !cw.theInstance) return null;
        try {
            const json = await cw.theInstance.invokeMethodAsync('DebugListTypes');
            return JSON.parse(json);
        } catch { return null; }
    });
    if (Array.isArray(peek) && peek.length > 0) {
        console.log(`  providers ready after ${(i + 1) * 2}s: ${JSON.stringify(peek)}`);
        ready = true;
        break;
    }
}
if (!ready) {
    console.error('FAIL: no providers registered after 60s — Game1.Initialize never ran?');
    await b.close();
    process.exit(2);
}
// Give the program a beat to actually create the sprite.
await p.waitForTimeout(2000);

const inspectorResults = await p.evaluate(async () => {
    const f = document.getElementById('mg-preview-frame');
    const cw = f && f.contentWindow;
    if (!cw || !cw.theInstance) return { error: 'iframe / theInstance missing' };
    const inst = cw.theInstance;

    const parseOrNull = (s) => { try { return JSON.parse(s); } catch { return null; } };

    const typesJson = await inst.invokeMethodAsync('DebugListTypes');
    const metaJson  = await inst.invokeMethodAsync('DebugGetEntity', 'metadata', 0);
    const idsJson   = await inst.invokeMethodAsync('DebugListEntities', 'sprite');

    const types = parseOrNull(typesJson) ?? [];
    const meta = parseOrNull(metaJson);
    const spriteIds = parseOrNull(idsJson) ?? [];

    let beforeSnap = null, afterSnap = null, setOk = null;
    if (spriteIds.length > 0) {
        const id = spriteIds[0];
        beforeSnap = parseOrNull(await inst.invokeMethodAsync('DebugGetEntity', 'sprite', id));
        setOk = await inst.invokeMethodAsync('DebugSetField', 'sprite', id, 'rotation', JSON.stringify(1.5));
        afterSnap = parseOrNull(await inst.invokeMethodAsync('DebugGetEntity', 'sprite', id));
    }
    return { types, meta, spriteIds, beforeSnap, afterSnap, setOk };
});

if (inspectorResults.error) {
    console.error('FAIL:', inspectorResults.error);
    await b.close(); process.exit(2);
}

console.log('types:        ', JSON.stringify(inspectorResults.types));
console.log('metadata.fps: ', inspectorResults.meta?.fps);
console.log('metadata.sprt:', inspectorResults.meta?.spriteCount);
console.log('spriteIds:    ', JSON.stringify(inspectorResults.spriteIds));
console.log('before.rot:   ', inspectorResults.beforeSnap?.rotation);
console.log('setOk:        ', inspectorResults.setOk);
console.log('after.rot:    ', inspectorResults.afterSnap?.rotation);

// All 10 providers should be registered after Game1.Initialize.
const expectedTypes = [
    'metadata', 'sprite', 'transform', 'tween', 'collider',
    'text', 'sfx', 'texture', 'renderOutput', 'effect',
];
const checks = {
    providersRegistered: Array.isArray(inspectorResults.types)
        && expectedTypes.every((t) => inspectorResults.types.includes(t)),
    metadataSnapshot: inspectorResults.meta
        && typeof inspectorResults.meta.fps === 'number'
        && typeof inspectorResults.meta.spriteCount === 'number',
    spriteListed: Array.isArray(inspectorResults.spriteIds) && inspectorResults.spriteIds.length > 0,
    spriteSnapshotShape: inspectorResults.beforeSnap
        && Array.isArray(inspectorResults.beforeSnap.position)
        && typeof inspectorResults.beforeSnap.rotation === 'number',
    spriteSetReturnedTrue: inspectorResults.setOk === true,
    spriteRotationApplied: inspectorResults.afterSnap
        && Math.abs(Number(inspectorResults.afterSnap.rotation) - 1.5) < 0.001,
};

console.log('');
for (const [k, v] of Object.entries(checks)) {
    console.log(v ? `✓ ${k}` : `✗ ${k}`);
}

await b.close();
const pass = errors.length === 0 && Object.values(checks).every(Boolean);
process.exit(pass ? 0 : 1);
