// Reproduces the user's "x doesn't appear after step over" bug.
//   1. Load simple source with a few assignments.
//   2. Set bp on `x = 180`. Run. Bp hits.
//   3. Query scopes — should be empty (correct).
//   4. Step over once. Pause.
//   5. Query scopes — SHOULD now contain x. User reports it doesn't.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5320/';

// The user's EXACT snippet — no do/sync/loop wrapper. Program ends after
// speed=8. 0-based line 6 = `x = 180`.
const SOURCE = `set render size 1920, 1080
set background color rgb(75, 44, 44)
sprite 1, 100, 200, 0
color sprite 1, rgb(255, 0, 0)
size sprite 1, 200, 200
order sprite 1, 1
x = 180
y = 100
speed = 8
`;

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', e => console.log('[PE]', e.message.slice(0, 300)));
page.on('console', m => {
    const t = m.text();
    if (/\[DBG\]|HIT BREAKPOINT|stepping|frame/i.test(t)) console.log('[CON]', t.slice(0, 300));
});

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

await page.evaluate(async (src) => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgstep', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'mgstep', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(src); await sw.close();
    localStorage.setItem('fade.activeProject', 'mgstep');
}, SOURCE);
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

await page.click('#run');
await page.waitForSelector('#theCanvas', { timeout: 60_000 });
await page.waitForTimeout(2_000);

// Combine LoadProgram + DebugStart in a single JS frame so no rAF tick
// fires between them — otherwise the freshly-reloaded VM races through
// all the assignments before REQUEST_PAUSE arrives.
console.log('→ LoadProgram + DebugStart (atomic)…');
const startResult = await page.evaluate(async (src) => {
    await window.theInstance.invokeMethodAsync('LoadProgram', src);
    return await window.theInstance.invokeMethodAsync('DebugStart');
}, SOURCE);
const parsed = JSON.parse(startResult);
console.log(`  DebugStart ok=${parsed.ok} statementLines=[${parsed.statementLines?.join(',')}]`);
await page.waitForTimeout(800);

// Check initial pause state.
const initialFrames = JSON.parse(await page.evaluate(() =>
    window.theInstance.invokeMethodAsync('DebugStackFrames')));
const initFrames = Array.isArray(initialFrames) ? initialFrames : initialFrames?.frames;
console.log(`  After pause: ${initFrames?.length ?? 0} frames, top line=${initFrames?.[0]?.lineNumber}`);

console.log('→ set breakpoint on line 6 (x = 180)…');
await page.evaluate(async () => {
    await window.theInstance.invokeMethodAsync('DebugSetBreakpoints',
        JSON.stringify([{ line: 6, column: 0 }]));
});
await page.waitForTimeout(300);

console.log('→ Continue from initial pause; expect bp hit on x = 180…');
await page.evaluate(() => window.theInstance.invokeMethodAsync('DebugContinue'));

// Poll TickDotNet-drained events for REV_REQUEST_BREAKPOINT — the
// Playground debug-status update lags behind, so we drain directly.
async function waitForPause(label, timeoutMs) {
    const start = Date.now();
    while (Date.now() - start < timeoutMs) {
        // Query frames; if VM is paused, frames are present.
        const framesJson = await page.evaluate(() =>
            window.theInstance.invokeMethodAsync('DebugStackFrames'));
        try {
            const f = JSON.parse(framesJson);
            const arr = Array.isArray(f) ? f : f?.frames;
            if (arr?.length) {
                // Also check that the VM is actually paused by re-checking shortly.
                await page.waitForTimeout(150);
                const f2 = JSON.parse(await page.evaluate(() =>
                    window.theInstance.invokeMethodAsync('DebugStackFrames')));
                const arr2 = Array.isArray(f2) ? f2 : f2?.frames;
                if (arr2?.length && arr2[0].lineNumber === arr[0].lineNumber) {
                    return arr2;
                }
            }
        } catch { /* parse error */ }
        await page.waitForTimeout(100);
    }
    console.warn(`  ! ${label} pause-wait timed out`);
    return null;
}

await waitForPause('bp hit', 5_000);

async function snap(label) {
    const frames = JSON.parse(await page.evaluate(() => window.theInstance.invokeMethodAsync('DebugStackFrames')));
    const top = Array.isArray(frames) ? frames[0] : frames?.frames?.[0];
    const scopesJson = await page.evaluate(() => window.theInstance.invokeMethodAsync('DebugScopes', 0));
    const scopes = JSON.parse(scopesJson);
    const names = (scopes?.scopes ?? []).flatMap(s =>
        (s?.variables ?? []).map(v => `${s.scopeName}.${v.name}=${v.value ?? '?'}`));
    console.log(`\n[${label}]`);
    console.log(`  top frame: line=${top?.lineNumber} col=${top?.colNumber}`);
    console.log(`  vars: ${names.join(', ') || '<none>'}`);
    return { line: top?.lineNumber, names };
}

const at_bp = await snap('after BP hit (paused at x = 180)');

console.log('\n→ Step over (1st time)…');
await page.evaluate(() => window.theInstance.invokeMethodAsync('DebugStep', 'over'));
// Wait for "paused on step" status.
const t1 = Date.now();
while (Date.now() - t1 < 5_000) {
    const s = await page.evaluate(() => document.getElementById('debug-status')?.textContent ?? '');
    if (/paused on step/i.test(s)) break;
    await page.waitForTimeout(100);
}
const after_step1 = await snap('after 1st step over');

console.log('\n→ Step over (2nd time)…');
await page.evaluate(() => window.theInstance.invokeMethodAsync('DebugStep', 'over'));
const t2 = Date.now();
while (Date.now() - t2 < 5_000) {
    const s = await page.evaluate(() => document.getElementById('debug-status')?.textContent ?? '');
    if (/paused on step/i.test(s)) break;
    await page.waitForTimeout(100);
}
const after_step2 = await snap('after 2nd step over');

await browser.close();

console.log('\n— Summary —');
console.log(`BP hit:     line=${at_bp.line}, vars=${at_bp.names.length}`);
console.log(`Step 1:     line=${after_step1.line}, vars=${after_step1.names.length}`);
console.log(`Step 2:     line=${after_step2.line}, vars=${after_step2.names.length}`);

// Top-level `x = 180` is surfaced under either Locals or Globals
// depending on how the compiler flagged the register; we don't care
// which scope wraps it, only whether the binding is visible.
const hasX = (names) => names.some(n => /^[A-Za-z]+\.x=/.test(n));
if (hasX(after_step1.names)) {
    console.log('✓ x appeared after 1st step (expected).');
} else if (hasX(after_step2.names)) {
    console.error('✗ BUG: x only appeared after the 2nd step over.');
    process.exit(1);
} else {
    console.error('✗ BUG: x never appeared in either step.');
    process.exit(1);
}
