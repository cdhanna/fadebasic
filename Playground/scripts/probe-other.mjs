import { chromium } from 'playwright';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage();
await page.goto('http://localhost:5311/', { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgother', { create: true });
    const write = async (n, t) => { const fh = await dir.getFileHandle(n, { create: true }); const w = await fh.createWritable(); await w.write(t); await w.close(); };
    await write('fade.json', JSON.stringify({ name: 'mgother', type: 'monogame', commandDlls: [], sources: ['main.fbasic'] }) + '\n');
    await write('main.fbasic', 'do\nsync\nloop\n');
    localStorage.setItem('fade.activeProject', 'mgother');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise(r => setTimeout(r, 2500));
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise(r => setTimeout(r, 500));

const found = await page.evaluate(() => {
    const items = Array.from(document.querySelectorAll('#help-toc .help-toc-item'));
    const out = [];
    for (const el of items) {
        let prev = el.previousElementSibling;
        while (prev && !prev.classList.contains('help-toc-group')) prev = prev.previousElementSibling;
        const group = prev?.textContent?.replace(/\s*\(\d+\)\s*$/, '').trim();
        if (group === 'Other') out.push(el.dataset.name);
    }
    return out;
});
console.log('Other contents:', found);
await browser.close();
