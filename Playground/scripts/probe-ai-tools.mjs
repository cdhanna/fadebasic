// Probe: local AI agent calls list_files on a seeded project.
//
// Requires the dev server on :5311. First run downloads ~2.4 GB (Qwen 3 4B).
// Use WASM variant for headless — WebGPU is flaky in headless Chrome.
//
//   npm run dev          # terminal 1
//   node scripts/probe-ai-tools.mjs   # terminal 2
//
// Pass SKIP_LOAD=1 to skip model download (checks helpers only).

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const SKIP_LOAD = process.env.SKIP_LOAD === '1';
const MODEL_TIMEOUT_MS = SKIP_LOAD ? 30_000 : 12 * 60_000; // 12 min for first download

const browser = await chromium.launch({
    headless: true,
    args: ['--use-gl=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'],
});
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });

const captured = [];
page.on('pageerror', e => captured.push(`[PE] ${e.message}`));
page.on('console', m => {
    const t = m.text();
    if (/ai\/(agent|tool|provider)/.test(t)) console.log(t.slice(0, 300));
    captured.push(`[${m.type()}] ${t.slice(0, 500)}`);
});

await page.goto('http://localhost:5311/', { waitUntil: 'domcontentloaded' });

await page.evaluate(async () => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('aiprobe', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'aiprobe', type: 'web',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const src = 'print "hello"\n';
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(src); await sw.close();
    localStorage.setItem('fade.activeProject', 'aiprobe');
    // WASM 4B — reliable in headless; WebGPU often unavailable.
    localStorage.setItem('fade.ai.selectedProvider', 'transformers-js:qwen3-4b-wasm');
});
await page.reload({ waitUntil: 'domcontentloaded' });

await page.waitForFunction(() => !!window.__fadeAiHelpers, { timeout: 90_000 });
console.log('→ __fadeAiHelpers ready');

if (!SKIP_LOAD) {
    console.log('→ loading Qwen 3 4B WASM (first run downloads ~2.4 GB)…');
    await page.evaluate(() => window.__fadeAiHelpers.loadModel());
    await page.waitForFunction(
        () => window.__fadeAiHelpers?.engineStatus() === 'ready',
        { timeout: MODEL_TIMEOUT_MS },
    );
    const label = await page.evaluate(() => window.__fadeAiHelpers.providerLabel());
    console.log(`→ model ready: ${label}`);
}

const result = await page.evaluate(async () => {
    const out = { steps: [] };
    const h = window.__fadeAiHelpers;

    if (h.engineStatus() !== 'ready') {
        return { ...out, skipped: true, status: h.engineStatus() };
    }

    const before = h.toolRowCount();
    out.steps.push(`tool rows before=${before}`);

    await h.sendMessage('List every file in this project. Use list_files.');

    // Poll for a completed tool row (badge flips to done).
    const deadline = Date.now() + 180_000;
    let toolRows = 0;
    let doneBadge = false;
    while (Date.now() < deadline) {
        await new Promise(r => setTimeout(r, 500));
        toolRows = h.toolRowCount();
        doneBadge = !!document.querySelector('.ai-tool-badge-done');
        if (toolRows > before && doneBadge) break;
    }

    out.toolRows = toolRows;
    out.doneBadge = doneBadge;
    out.steps.push(`tool rows after=${toolRows} doneBadge=${doneBadge}`);

    const labels = [...document.querySelectorAll('.ai-tool-label')].map(el => el.textContent);
    out.toolLabels = labels;

    const err = document.querySelector('.ai-msg-error');
    out.errorText = err?.textContent ?? null;

    return out;
});

console.log('── RESULT ──');
console.log(JSON.stringify(result, null, 2));

let ok = false;
if (result.skipped) {
    console.log('── VERDICT ── SKIP (model not loaded — set SKIP_LOAD=0 to run full probe)');
    ok = true;
} else if (result.toolLabels?.includes('list_files') && result.doneBadge) {
    console.log('── VERDICT ── PASS (list_files tool ran successfully)');
    ok = true;
} else {
    console.log('── VERDICT ── FAIL');
    if (result.errorText) console.log('error:', result.errorText);
    console.log('console tail:', captured.slice(-15).join('\n'));
}

await browser.close();
process.exit(ok ? 0 : 1);
