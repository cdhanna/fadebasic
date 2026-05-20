// Check that FadeMonoGameCommands docs (Summary + Remarks + Examples)
// actually surface in the Help tab when the project type is 'monogame'.
// Before the GenerateDocumentationFile fix on Fade.MonoGame.Lib's net8
// build, every monogame command had an empty docString in the metadata
// blob the LSP worker reads, so the Help tab showed names with no body.
//
// Pass criteria: every FadeMonoGame command we sample has a non-trivial
// markdown body (length > 60 chars, includes a Remarks or Examples
// section header).

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

const errors = [];
page.on('pageerror', (e) => errors.push(e.message));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

// Force a monogame project on this run.
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mghelp', { create: true });
    const writeText = async (name, text) => {
        const fh = await dir.getFileHandle(name, { create: true });
        const w = await fh.createWritable();
        await w.write(text);
        await w.close();
    };
    await writeText('fade.json', JSON.stringify({
        $schema: '/fade.schema.json',
        name: 'mghelp',
        type: 'monogame',
        commandDlls: [],
        sources: ['main.fbasic'],
    }, null, 2) + '\n');
    await writeText('main.fbasic', 'do\n  sync\nloop\n');
    localStorage.setItem('fade.activeProject', 'mghelp');
});

await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise((r) => setTimeout(r, 2000));

// Open the Help tab so its DOM populates.
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise((r) => setTimeout(r, 800));

// Sample a handful of FadeMonoGame commands that we know have rich XML
// docs (we inspected the metadata blob earlier).
const targets = ['push asset', 'rename asset', 'load sfx clip', 'sfx', 'play sfx', 'texture', 'sprite'];

const results = [];
for (const name of targets) {
    const ok = await page.evaluate((n) => window.__fadeHelp?.openCommand(n) ?? false, name);
    if (!ok) {
        results.push({ name, ok: false, reason: 'openCommand returned false (not in TOC)' });
        continue;
    }
    await new Promise((r) => setTimeout(r, 200));
    const body = await page.evaluate(() => {
        const b = document.getElementById('help-body');
        return b ? b.textContent || '' : '';
    });
    const hasRemarks = /\bRemarks\b/.test(body);
    const hasExamples = /\bExamples?\b/.test(body);
    const hasParams = /\bParameters?\b/.test(body);
    results.push({
        name,
        ok: true,
        len: body.length,
        hasRemarks,
        hasExamples,
        hasParams,
        firstChars: body.replace(/\s+/g, ' ').slice(0, 100),
    });
}

console.log(JSON.stringify(results, null, 2));

// Cleanup.
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    try { await ws.removeEntry('mghelp', { recursive: true }); } catch {}
    localStorage.setItem('fade.activeProject', 'default');
});

let failed = 0;
for (const r of results) {
    if (!r.ok) { console.log('FAIL', r.name, r.reason); failed++; continue; }
    if (r.len < 60 || (!r.hasRemarks && !r.hasExamples)) {
        console.log('FAIL', r.name, '- body too thin:', r.firstChars);
        failed++;
    }
}
if (errors.length) console.log('PAGE ERRORS:', errors);
console.log(failed === 0 ? 'PASS: all monogame commands have rich docs' : `FAIL: ${failed} commands missing docs`);
await browser.close();
process.exit(failed > 0 || errors.length > 0 ? 1 : 0);
