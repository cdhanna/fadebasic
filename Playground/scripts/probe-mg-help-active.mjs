// Reproduce the case where Help is the active tab in the right column
// at the moment the user clicks Run — does dockview detach the Game
// panel's #mg-blazor-root mount point, causing Blazor's router to render
// inside a stale/detached element?

import { chromium } from 'playwright';
import { writeFileSync } from 'node:fs';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });

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

// Before clicking Run: where is mg-blazor-root and which tab is active?
const before = await page.evaluate(() => {
    const root = document.getElementById('mg-blazor-root');
    return {
        rootInDom: !!root,
        rootRect: root?.getBoundingClientRect()?.height,
        rootParentVisible: root && getComputedStyle(root.parentElement).display !== 'none',
        activePanelInRightCol: window.__fadeDockview?.activeGroup?.activePanel?.id ?? null,
    };
});
console.log('before Run:', JSON.stringify(before));

// Force Help to be the active tab in the right column.
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise((r) => setTimeout(r, 400));
const afterHelp = await page.evaluate(() => {
    const root = document.getElementById('mg-blazor-root');
    return {
        rootInDom: !!root,
        rootVisible: root && getComputedStyle(root).display !== 'none',
        rootParentVisible: root && getComputedStyle(root.parentElement).display !== 'none',
    };
});
console.log('Help active:', JSON.stringify(afterHelp));

// Click Run.
await page.click('#run');
console.log('clicked Run, waiting for boot…');
await new Promise((r) => setTimeout(r, 12_000));

const after = await page.evaluate(() => {
    const root = document.getElementById('mg-blazor-root');
    return {
        rootInDom: !!root,
        rootText: root?.textContent?.slice(0, 200) ?? '',
        notFound: document.body.textContent?.includes("Sorry, there's nothing"),
        canvasExists: !!document.getElementById('theCanvas'),
    };
});
console.log('after Run:', JSON.stringify(after));

const png = await page.screenshot();
writeFileSync('/tmp/fade-mg-help-active.png', png);

console.log('---console (last 25)---');
for (const m of consoleAll.slice(-25)) console.log(' ', m.slice(0, 350));

await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    try { await ws.removeEntry('mgrun', { recursive: true }); } catch {}
    localStorage.setItem('fade.activeProject', 'default');
});
await browser.close();
