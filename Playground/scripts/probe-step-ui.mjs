// Reproduces the user's "x doesn't appear after step over" bug through
// the actual Playground UI flow: programmatically install a bp via
// Monaco-side state + click the Debug + Step buttons. Then read the
// variables panel DOM after each step.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5320/';

const SOURCE = `set render size 1920, 1080
set background color rgb(75, 44, 44)
sprite 1, 100, 200, 0
color sprite 1, rgb(255, 0, 0)
size sprite 1, 200, 200
order sprite 1, 1
x = 180
y = 100
speed = 8
do
  sync
loop
`;

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', e => console.log('[PE]', e.message.slice(0, 300)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

await page.evaluate(async (src) => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgstepui', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'mgstepui', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(src); await sw.close();
    localStorage.setItem('fade.activeProject', 'mgstepui');
}, SOURCE);
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

// Install a bp on Monaco line 7 (the `x = 180` line). Wait for the
// editor model to be ready first.
console.log('→ Setting bp on Monaco line 7 (x = 180)…');
await page.waitForFunction(() => {
    try { return (window).__playgroundEditor != null; } catch { return false; }
}, { timeout: 10_000 }).catch(() => { /* fall back */ });

// The Playground doesn't expose the editor; reach in via the breakpoint
// store by simulating the gutter-click code path.
await page.evaluate(() => {
    // Find Monaco model URI through the global monaco instance.
    const editor = (window).monaco?.editor?.getEditors?.()?.[0];
    if (!editor) return;
    const model = editor.getModel();
    if (!model) return;
    // We can't reach Playground's breakpointsByUri set from outside, but
    // syncBreakpointsToWorker is called whenever the gutter-click handler
    // toggles a bp. So simulate that handler by dispatching a mousedown on
    // the editor's gutter glyph margin at line 7.
    const lineHeight = editor.getOption((window).monaco.editor.EditorOption.lineHeight);
    const layoutInfo = editor.getLayoutInfo();
    const y = layoutInfo.glyphMarginTop + (7 - 1) * lineHeight + lineHeight / 2;
    const x = layoutInfo.glyphMarginLeft + 4;
    const target = document.querySelector('.monaco-editor');
    if (!target) return;
    const rect = target.getBoundingClientRect();
    const evt = new MouseEvent('mousedown', {
        clientX: rect.left + x,
        clientY: rect.top + y,
        bubbles: true, cancelable: true, view: window,
        button: 0,
    });
    target.dispatchEvent(evt);
});
await page.waitForTimeout(500);

console.log('→ Click Debug button (Playground startDebug flow)…');
await page.click('#debug', { force: true });

// Wait until the debug session is paused on the bp.
const waitForPausedBp = async () => {
    const t0 = Date.now();
    while (Date.now() - t0 < 15_000) {
        const s = await page.evaluate(() =>
            document.getElementById('debug-status')?.textContent ?? '');
        if (/paused on breakpoint/i.test(s)) return true;
        await page.waitForTimeout(150);
    }
    return false;
};

if (!await waitForPausedBp()) {
    console.error('✗ never reached paused-on-breakpoint state.');
    await browser.close();
    process.exit(1);
}

async function dumpVars(label) {
    // Read the variables panel DOM to see exactly what the user would see.
    const vars = await page.evaluate(() => {
        const tree = document.getElementById('debug-vars-tree');
        if (!tree) return [];
        const out = [];
        let curScope = '';
        tree.querySelectorAll('.debug-scope-header, .debug-var').forEach(el => {
            if (el.classList.contains('debug-scope-header')) {
                curScope = el.textContent.replace(/^[▸▾]/, '').trim();
            } else {
                const n = el.querySelector('.debug-var-name')?.textContent ?? '';
                const v = el.querySelector('.debug-var-value')?.textContent ?? '';
                out.push(`${curScope}.${n}=${v}`);
            }
        });
        return out;
    });
    const line = await page.evaluate(() => {
        // Find the editor decoration with class 'fade-current' — the
        // highlighted current line.
        const els = document.querySelectorAll('.fade-current');
        for (const el of els) {
            const ln = el.closest('.view-line')?.getAttribute?.('style');
            if (ln) return ln;
        }
        // Fallback: read debug-frames-list's top entry.
        const top = document.querySelector('#debug-frames-list .debug-frame .frame-loc');
        return top?.textContent ?? '?';
    });
    console.log(`\n[${label}]`);
    console.log(`  top frame loc: ${line}`);
    console.log(`  vars: ${vars.join(', ') || '<none>'}`);
    return { vars, line };
}

const at_bp = await dumpVars('after BP hit (paused at x = 180)');

console.log('\n→ Click Step Over button (1st time)…');
await page.click('#debug-step-over', { force: true });
// Wait for "paused on step" status.
const waitForPausedStep = async () => {
    const t0 = Date.now();
    while (Date.now() - t0 < 8_000) {
        const s = await page.evaluate(() =>
            document.getElementById('debug-status')?.textContent ?? '');
        if (/paused on step/i.test(s)) return true;
        await page.waitForTimeout(100);
    }
    return false;
};
await waitForPausedStep();
// Add a small delay to let refreshDebugView (which awaited inside the
// ack handler) complete its DOM mutations.
await page.waitForTimeout(500);
const after_step1 = await dumpVars('after 1st step over (UI DOM read)');

console.log('\n→ Click Step Over button (2nd time)…');
await page.click('#debug-step-over', { force: true });
await waitForPausedStep();
await page.waitForTimeout(500);
const after_step2 = await dumpVars('after 2nd step over (UI DOM read)');

await browser.close();

console.log('\n— Summary —');
const hasXAfterStep1 = after_step1.vars.some(v => /\.x=/.test(v));
const hasXAfterStep2 = after_step2.vars.some(v => /\.x=/.test(v));
console.log(`x after step 1: ${hasXAfterStep1}`);
console.log(`x after step 2: ${hasXAfterStep2}`);
if (hasXAfterStep1) {
    console.log('✓ x appears after first step (no UI bug).');
} else if (hasXAfterStep2) {
    console.error('✗ BUG REPRODUCED: x only appears after the 2nd step.');
    process.exit(1);
} else {
    console.error('✗ x never appears in either step.');
    process.exit(1);
}
