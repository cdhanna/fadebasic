// Verifies the worker LSP swaps its CommandCollection based on fade.json type.
// Black-box test: same fbasic source ("sprite 1, 100, 100, 1"), two projects:
//   - mgproj  (type=monogame) → LSP knows `sprite`, no markers
//   - webproj (type=web)      → LSP doesn't know `sprite`, has markers
// We inspect Monaco's getModelMarkers between project switches.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5312/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

page.on('console', msg => {
    if (msg.type() === 'error') console.log('[console.error]', msg.text().slice(0, 300));
});
page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 300)));

console.log(`→ navigate ${URL}`);
await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

// Seed two projects in OPFS with the same source.
await page.evaluate(async () => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    async function mk(name, type) {
        const dir = await ws.getDirectoryHandle(name, { create: true });
        const cfg = JSON.stringify({
            $schema: '/fade.schema.json', name, type, commandDlls: [],
            sources: ['main.fbasic'],
        }, null, 2) + '\n';
        const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
        await cw.write(cfg); await cw.close();
        const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
        await sw.write('sprite 1, 100, 100, 1\n'); await sw.close();
    }
    await mk('mgproj', 'monogame');
    await mk('webproj', 'web');
    localStorage.setItem('fade.activeProject', 'mgproj');
});

await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

async function getMainFbasicMarkers() {
    // Give the LSP a beat — set-project-type + re-set-document are async.
    await page.waitForTimeout(2000);
    return await page.evaluate(() => {
        const models = window.monaco?.editor?.getModels?.() ?? [];
        const target = models.find((m) => /main\.fbasic$/.test(m.uri.toString()));
        if (!target) return { _err: 'main.fbasic model not loaded' };
        const markers = window.monaco.editor.getModelMarkers({ resource: target.uri });
        return {
            uri: target.uri.toString(),
            text: target.getValue(),
            markers: markers.map(m => ({
                message: m.message,
                severity: m.severity,
                startLineNumber: m.startLineNumber,
                startColumn: m.startColumn,
                source: m.source,
                code: m.code,
            })),
        };
    });
}

async function switchProjectTo(name) {
    // Use the Cmd+P projects overlay isn't easy from playwright without
    // knowing exact hotkeys; flip localStorage + reload is sufficient.
    await page.evaluate((n) => localStorage.setItem('fade.activeProject', n), name);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });
}

console.log('\n=== mgproj (monogame) — LSP should know `sprite` ===');
const mg = await getMainFbasicMarkers();
console.log('markers:', JSON.stringify(mg.markers, null, 2));

console.log('\n=== webproj (web) — LSP should NOT know `sprite` ===');
await switchProjectTo('webproj');
const web = await getMainFbasicMarkers();
console.log('markers:', JSON.stringify(web.markers, null, 2));

await browser.close();

// Behavior we expect: monogame mode parses `sprite 1, 100, 100, 1` cleanly
// (the LSP knows it's a command) → no error-severity markers. Web mode
// doesn't recognize `sprite`, so the parser bails — typically with an
// "ambiguous statement" or similar — yielding at least one error marker
// on that line.
const ERR_SEVERITY = 8; // monaco.MarkerSeverity.Error
const mgErrors = (mg.markers ?? []).filter(m => m.severity === ERR_SEVERITY);
const webErrors = (web.markers ?? []).filter(m => m.severity === ERR_SEVERITY);

console.log('\nmonogame errors:', mgErrors.length);
console.log('web errors:     ', webErrors.length);

if (mgErrors.length > 0) {
    console.error('\n✗ FAIL: monogame project has parse errors — LSP didn\'t accept `sprite` (FadeMonoGameCommands not registered).');
    process.exit(1);
}
if (webErrors.length === 0) {
    console.error('\n✗ FAIL: web project has no parse errors for `sprite` — LSP shouldn\'t recognize it under WebCommands.');
    process.exit(1);
}
console.log('\n✓ PASS: LSP command collection switches with fade.json type:');
console.log('   • monogame mode parses `sprite` cleanly');
console.log('   • web mode rejects `sprite` (' + webErrors[0].message.slice(0, 60) + '…)');
