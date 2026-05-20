// Reproduce "Sorry, there's nothing at this address." in the Game panel
// after the Help-next-to-Game layout change. Sets up a monogame project,
// clicks Run, screenshots the Game panel and dumps its inner HTML.

import { chromium } from 'playwright';
import { writeFileSync } from 'node:fs';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });

const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
const consoleAll = [];
page.on('console', (m) => consoleAll.push(`[${m.type()}] ${m.text()}`));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgrun', { create: true });
    const w = async (n, t) => {
        const fh = await dir.getFileHandle(n, { create: true });
        const sw = await fh.createWritable();
        await sw.write(t); await sw.close();
    };
    await w('fade.json', JSON.stringify({
        $schema: '/fade.schema.json',
        name: 'mgrun',
        type: 'monogame',
        commandDlls: [],
        sources: ['main.fbasic'],
    }, null, 2) + '\n');
    await w('main.fbasic', 'do\n  sync\nloop\n');
    localStorage.setItem('fade.activeProject', 'mgrun');
    localStorage.removeItem('fade.dockview.layout.v4');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise((r) => setTimeout(r, 1500));

// Click Run.
await page.click('#run');
console.log('clicked Run, waiting for boot…');
await new Promise((r) => setTimeout(r, 15_000));

const png = await page.screenshot();
writeFileSync('/tmp/fade-mg-run.png', png);
const summary = await page.evaluate(() => {
    const root = document.getElementById('mg-blazor-root');
    const canvas = document.getElementById('theCanvas');
    return {
        rootHTML: root ? root.outerHTML.slice(0, 600) : null,
        canvasExists: !!canvas,
        canvasDims: canvas ? { w: canvas.width, h: canvas.height } : null,
        bodyText: document.body.innerText.includes("Sorry, there's nothing"),
        notFoundInRoot: root?.textContent?.includes("Sorry, there's nothing"),
    };
});
console.log('summary:', JSON.stringify(summary, null, 2));
console.log('---recent console---');
for (const m of consoleAll.slice(-20)) console.log(' ', m.slice(0, 400));

// Cleanup
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    try { await ws.removeEntry('mgrun', { recursive: true }); } catch {}
    localStorage.setItem('fade.activeProject', 'default');
});
await browser.close();
