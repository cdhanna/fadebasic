import { chromium } from 'playwright';
import { resolve } from 'node:path';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgshot', { create: true });
    const write = async (n, t) => { const fh = await dir.getFileHandle(n, { create: true }); const w = await fh.createWritable(); await w.write(t); await w.close(); };
    await write('fade.json', JSON.stringify({ name: 'mgshot', type: 'monogame', commandDlls: [], sources: ['main.fbasic'] }) + '\n');
    await write('main.fbasic', 'do\nsync\nloop\n');
    localStorage.setItem('fade.activeProject', 'mgshot');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise(r => setTimeout(r, 2000));

// Expand the Sprite group on Commands.
await page.evaluate(() => {
    const g = Array.from(document.querySelectorAll('#help-toc .help-toc-group.help-toc-collapsible'))
        .find(el => el.textContent?.includes('Sprite'));
    g?.click?.();
});
await new Promise(r => setTimeout(r, 300));
await page.locator('#help-pane').screenshot({ path: resolve('commands-toc.png') });
console.log('wrote commands-toc.png');

// Now Language with Operations expanded.
await page.click('.help-tab[data-tab="language"]');
await new Promise(r => setTimeout(r, 700));
await page.locator('#help-toc .help-toc-collapsible', { hasText: 'Operations' }).first().click();
await new Promise(r => setTimeout(r, 400));
await page.locator('#help-pane').screenshot({ path: resolve('language-toc.png') });
console.log('wrote language-toc.png');

await browser.close();
