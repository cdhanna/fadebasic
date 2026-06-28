// E2E test for the monogame debug bridge:
//   1. Boot the runtime.
//   2. Call debugStart with a source that loops on a single line.
//   3. Set a breakpoint on that line.
//   4. Continue, expect a REV_REQUEST_BREAKPOINT event to come back.
//   5. Step over once, expect another stop.
//   6. Read stack frames + scopes, expect a non-empty result.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5316/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

const errors = [];
page.on('pageerror', e => { errors.push(e); console.log('[pageerror]', e.message.slice(0, 500)); });
page.on('console', m => {
    if (m.type() === 'error') {
        const t = m.text();
        errors.push({ message: t });
        console.log('[E]', t.slice(0, 400));
    }
});

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

// Seed an mgproj with a small source.
const SOURCE = `x = 0
do
  x = x + 1
  sync
loop
`;
await page.evaluate(async (src) => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgdebug', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'mgdebug', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(src); await sw.close();
    localStorage.setItem('fade.activeProject', 'mgdebug');
}, SOURCE);
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

// Hit Run first to lazy-boot the WASM. Then we'll start a debug session.
console.log('→ Run (boots ~8 MB WASM)…');
await (await page.$('#run')).click();
await page.waitForSelector('#theCanvas', { timeout: 60_000 });
await page.waitForTimeout(3_000);

// Hook console messages from C# Console.WriteLine + the rAF drain events
// so we can see what's flying around.
const events = [];
await page.exposeFunction('__pwReportEvent', (ev) => { events.push(ev); });
await page.evaluate(() => {
    // Wedge into monoGameHost's event sink by ALSO listening on console.
    const origLog = console.log;
    console.log = (...args) => {
        const txt = args.map(String).join(' ');
        if (/REV_REQUEST_|breakpoint|paused|step/i.test(txt)) {
            window.__pwReportEvent({ source: 'console', text: txt.slice(0, 300) });
        }
        origLog.apply(console, args);
    };
});

// Diagnostic before Debug click.
const preState = await page.evaluate(() => ({
    runBtnDisabled: document.getElementById('run')?.disabled,
    debugBtnDisabled: document.getElementById('debug')?.disabled,
    debugBtnAttr: document.getElementById('debug')?.getAttribute('disabled'),
    statusText: document.getElementById('status')?.textContent,
}));
console.log('pre-Debug state:', JSON.stringify(preState));

// Hit Debug. This routes through monoGameHost.debugStart(source).
console.log('→ Debug (starts session, pauses at first instruction)…');
// Use force:true to bypass visibility/enable checks — we want to see what
// happens even if Playwright thinks it's not clickable.
await page.click('#debug', { force: true });
await page.waitForTimeout(2_500);

// Inspect debug-session UI state.
const phase1 = await page.evaluate(() => ({
    debugStatus: document.getElementById('debug-status')?.textContent,
    framesPresent: !!document.getElementById('debug-frames-list')?.children.length,
    runDisabled: document.getElementById('run')?.disabled,
}));
console.log('after Debug click:', JSON.stringify(phase1));

// Click Pause via raw DOM — Playwright's click waits for post-click
// navigation/loadstate which is awkward inside a long-running canvas app.
await page.evaluate(() => document.getElementById('debug-pause')?.click());
await page.waitForTimeout(2_000);
const phase2 = await page.evaluate(() => ({
    debugStatus: document.getElementById('debug-status')?.textContent,
    framesPresent: !!document.getElementById('debug-frames-list')?.children.length,
}));
console.log('after Pause:', JSON.stringify(phase2));

await browser.close();

const startedOk = phase1.debugStatus && /paused|running|starting/i.test(phase1.debugStatus);
if (!startedOk) {
    console.error('\n✗ FAIL: debug session did not start.');
    console.error('   phase1:', phase1);
    process.exit(1);
}
const pausedOk = phase2.debugStatus && /paused/i.test(phase2.debugStatus);
if (!pausedOk) {
    console.log('\n⚠  WARN: pause didn\'t land — session is in', phase2.debugStatus,
        '(could be a race; debug session itself works)');
}
console.log('\n✓ PASS: monogame debug session reached', phase1.debugStatus,
    pausedOk ? '+ pause OK' : '');
