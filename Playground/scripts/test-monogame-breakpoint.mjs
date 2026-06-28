// Validates breakpoint hit + recovery for monogame projects.
//   1. Boot the runtime, run a simple source (loops on `x = x + 1`).
//   2. Hit Debug — session pauses at first instruction.
//   3. Set a breakpoint on the `x = x + 1` line via window.theInstance.
//   4. Click Continue.
//   5. Wait for the breakpoint to hit — status should flip to "paused"
//      and the page must stay responsive (we test by being able to run
//      a follow-up page.evaluate within the wait).

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5320/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', e => console.log('[PE]', e.message.slice(0, 300)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

const SOURCE = `x = 0
do
  x = x + 1
  sync
loop
`;
await page.evaluate(async (src) => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgbp', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'mgbp', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(src); await sw.close();
    localStorage.setItem('fade.activeProject', 'mgbp');
}, SOURCE);
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

// Run (boots WASM).
console.log('→ Run (boots WASM)…');
await page.click('#run');
await page.waitForSelector('#theCanvas', { timeout: 60_000 });
await page.waitForTimeout(3_000);

// Debug start.
console.log('→ Debug start…');
await page.click('#debug', { force: true });
await page.waitForTimeout(2_500);

// Set a breakpoint on `x = x + 1` — line index 2 (0-based) per
// BreakpointRequest contract: camelCase keys, 0-based line numbers
// matching the lexer's token space.
console.log('→ set breakpoint on line 2 (0-based: x = x + 1)…');
await page.evaluate(async () => {
    const linesJson = JSON.stringify([{ line: 2, column: 0 }]);
    await window.theInstance.invokeMethodAsync('DebugSetBreakpoints', linesJson);
});
await page.waitForTimeout(500);

// Continue. Should run until the breakpoint hits.
console.log('→ Continue…');
await page.evaluate(() => window.theInstance.invokeMethodAsync('DebugContinue'));

// Poll: the breakpoint should hit within ~5 seconds. We poll because
// the test wants to verify the page is responsive (not hung).
const start = Date.now();
let phase = null;
while (Date.now() - start < 8_000) {
    phase = await page.evaluate(() => ({
        debugStatus: document.getElementById('debug-status')?.textContent ?? '',
        framesPresent: !!document.getElementById('debug-frames-list')?.children.length,
    }));
    if (/paused/i.test(phase.debugStatus)) break;
    await page.waitForTimeout(500);
}
console.log('phase after Continue:', JSON.stringify(phase));

await browser.close();

if (!phase || !/paused/i.test(phase.debugStatus)) {
    console.error('\n✗ FAIL: breakpoint did not trigger a paused state within 8s.');
    process.exit(1);
}
console.log('\n✓ PASS: breakpoint triggered + page stayed responsive (status=', phase.debugStatus, ')');
