import { chromium } from 'playwright';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage();
await page.goto('http://localhost:5311/', { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mginspect', { create: true });
    const write = async (n, t) => { const fh = await dir.getFileHandle(n, { create: true }); const w = await fh.createWritable(); await w.write(t); await w.close(); };
    await write('fade.json', JSON.stringify({ name: 'mginspect', type: 'monogame', commandDlls: [], sources: ['main.fbasic'] }) + '\n');
    await write('main.fbasic', 'do\nsync\nloop\n');
    localStorage.setItem('fade.activeProject', 'mginspect');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise(r => setTimeout(r, 2500));

// Capture the OUTERHTML of one Commands group + one Language section.
const dump = await page.evaluate(() => {
    const cmdGrp = document.querySelector('#help-toc .help-toc-group.help-toc-collapsible');
    return cmdGrp?.outerHTML?.replace(/></g, '>\n<') ?? null;
});
console.log('--- Commands group HTML:');
console.log(dump);

// Now switch to language tab.
await page.click('.help-tab[data-tab="language"]');
await new Promise(r => setTimeout(r, 800));
const langDump = await page.evaluate(() => {
    const sec = document.querySelector('#help-toc .help-toc-item.help-toc-collapsible');
    return sec?.outerHTML?.replace(/></g, '>\n<') ?? null;
});
console.log('--- Language section HTML:');
console.log(langDump);

// Compare chevron computed widths and positions.
const widths = await page.evaluate(() => {
    const lang = document.querySelector('#help-toc .help-toc-item.help-toc-collapsible');
    const langChev = lang?.querySelector('.help-toc-chevron');
    return {
        langChevRect: langChev?.getBoundingClientRect(),
        langChevComputedWidth: langChev ? getComputedStyle(langChev).width : null,
    };
});
console.log('--- Language chevron details:', JSON.stringify(widths, null, 2));
await browser.close();
