import { chromium } from 'playwright';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage();
await page.goto('http://localhost:5311/', { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mginspect2', { create: true });
    const write = async (n, t) => { const fh = await dir.getFileHandle(n, { create: true }); const w = await fh.createWritable(); await w.write(t); await w.close(); };
    await write('fade.json', JSON.stringify({ name: 'mginspect2', type: 'monogame', commandDlls: [], sources: ['main.fbasic'] }) + '\n');
    await write('main.fbasic', 'do\nsync\nloop\n');
    localStorage.setItem('fade.activeProject', 'mginspect2');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise(r => setTimeout(r, 2500));

const positions = await page.evaluate(() => {
    const grp = document.querySelector('#help-toc .help-toc-group.help-toc-collapsible');
    if (!grp) return null;
    const cs = getComputedStyle(grp);
    return {
        grp: { left: grp.getBoundingClientRect().left, gap: cs.gap, paddingLeft: cs.paddingLeft },
        chev: grp.querySelector('.help-toc-chevron')?.getBoundingClientRect(),
        label: grp.querySelector('.help-toc-group-label')?.getBoundingClientRect(),
        labelComputed: (() => { const e = grp.querySelector('.help-toc-group-label'); const cs2 = e && getComputedStyle(e); return cs2 && { marginLeft: cs2.marginLeft, paddingLeft: cs2.paddingLeft, flex: cs2.flex }; })(),
        count: grp.querySelector('.help-toc-group-count')?.getBoundingClientRect(),
    };
});
console.log(JSON.stringify(positions, null, 2));
await browser.close();
