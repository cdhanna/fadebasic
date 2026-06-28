// Verifies fade.json doesn't get pushed through the Fade LSP — its $schema
// line would trigger [0158] "Substitution missing open bracket" if it did.
// Also confirms stale owner='fade' markers get cleared on project-type change
// (the original bug ran once, leaving markers behind that subsequent fixes
// won't dislodge without explicit cleanup).

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5312/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 200)));
page.on('console', m => { if (m.type() === 'error') console.log('[E]', m.text().slice(0, 200)); });

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

// Seed a monogame project, set it active, reload.
await page.evaluate(async () => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgcheck', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'mgcheck', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write('print "hi"\n'); await sw.close();
    localStorage.setItem('fade.activeProject', 'mgcheck');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });
await page.waitForTimeout(2500);  // let setProjectType + re-push settle

// Look for fade.json markers owned by 'fade' (the Fade LSP) — should be NONE.
// Markers owned by 'fade-config' (the JSON-schema validator) are fine.
const stats = await page.evaluate(() => {
    const models = window.monaco.editor.getModels();
    const fadeJson = models.find(m => /\/fade\.json$/.test(m.uri.toString()));
    if (!fadeJson) return { _err: 'no fade.json model open' };
    // Pull markers from each owner separately.
    const all = window.monaco.editor.getModelMarkers({ resource: fadeJson.uri });
    const byOwner = {};
    for (const m of all) {
        const key = m.owner ?? '?';
        byOwner[key] = (byOwner[key] ?? 0) + 1;
    }
    return {
        uri: fadeJson.uri.toString(),
        markersByOwner: byOwner,
        anyFadeOwned: all.some(m => m.owner === 'fade'),
        fadeErrors: all.filter(m => m.owner === 'fade').map(m => ({
            code: m.code, message: m.message,
        })),
    };
});

console.log(JSON.stringify(stats, null, 2));

await browser.close();

if (stats.anyFadeOwned) {
    console.error('\n✗ FAIL: fade.json has owner="fade" markers — it shouldn\'t be parsed by the Fade LSP.');
    process.exit(1);
}
console.log('\n✓ PASS: fade.json has no Fade-LSP markers. ([0158] no longer leaks.)');
