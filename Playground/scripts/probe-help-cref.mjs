// Verifies that cross-command links in help docs (rendered from <see cref/>
// XML in the source) become same-page #fade-cmd: anchors and that clicking
// one routes through selectCommand instead of navigating away.

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 200)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

// Switch to a monogame project so we hit FadeMonoGameCommands docs (which
// have rich <see cref/> usage referencing C# method names like LoadTexture).
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgcref', { create: true });
    const writeText = async (name, text) => {
        const fh = await dir.getFileHandle(name, { create: true });
        const w = await fh.createWritable();
        await w.write(text); await w.close();
    };
    await writeText('fade.json', JSON.stringify({
        $schema: '/fade.schema.json', name: 'mgcref', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n');
    await writeText('main.fbasic', 'do\n  sync\nloop\n');
    localStorage.setItem('fade.activeProject', 'mgcref');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise(r => setTimeout(r, 3000));

await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise(r => setTimeout(r, 500));

// "push asset" links to "rename asset", "texture", etc. via <see cref/>.
const opened = await page.evaluate(() => window.__fadeHelp?.openCommand?.('push asset'));
console.log('opened "push asset":', opened);
await new Promise(r => setTimeout(r, 300));

const linkInfo = await page.evaluate(() => {
    const body = document.getElementById('help-body');
    if (!body) return null;
    const links = Array.from(body.querySelectorAll('a')).map(a => ({
        href: a.getAttribute('href'),
        text: a.textContent,
    }));
    return links;
});
console.log('links rendered in body:', JSON.stringify(linkInfo, null, 2));

const fadeLinks = linkInfo?.filter(l => (l.href ?? '').startsWith('#fade-cmd:')) ?? [];
if (fadeLinks.length === 0) {
    console.error('FAIL: no #fade-cmd: links found in body');
    process.exit(1);
}

// Click the first such link and see whether selectCommand fires.
const beforeSelected = await page.evaluate(() => {
    const active = document.querySelector('#help-toc .help-toc-item.active');
    return active?.dataset?.name ?? active?.textContent ?? null;
});
const expectedTarget = decodeURIComponent(fadeLinks[0].href.slice('#fade-cmd:'.length));
console.log(`clicking link [${fadeLinks[0].text}] → expecting selection of "${expectedTarget}" (was "${beforeSelected}")`);

await page.evaluate(() => {
    const body = document.getElementById('help-body');
    const link = body?.querySelector('a[href^="#fade-cmd:"]');
    link?.click();
});
await new Promise(r => setTimeout(r, 400));

const afterSelected = await page.evaluate(() => {
    const active = document.querySelector('#help-toc .help-toc-item.active');
    return active?.dataset?.name ?? active?.textContent ?? null;
});
console.log('selection after click:', afterSelected);

const ok = afterSelected === expectedTarget;
console.log(ok ? '\n✓ PASS: cref link routes to selectCommand' : '\n✗ FAIL: link click did not change selection');

await browser.close();
process.exit(ok ? 0 : 1);
