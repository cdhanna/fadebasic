// Verifies that the Help-tab TOC groups now reflect the owning IMethodSource
// (class name minus "Commands" suffix), not the first-word heuristic.

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 200)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

// Set monogame so we get both StandardCommands + FadeMonoGameCommands loaded.
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mggroups', { create: true });
    const writeText = async (name, text) => {
        const fh = await dir.getFileHandle(name, { create: true });
        const w = await fh.createWritable();
        await w.write(text); await w.close();
    };
    await writeText('fade.json', JSON.stringify({
        $schema: '/fade.schema.json', name: 'mggroups', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n');
    await writeText('main.fbasic', 'do\n  sync\nloop\n');
    localStorage.setItem('fade.activeProject', 'mggroups');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise(r => setTimeout(r, 3000));

await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise(r => setTimeout(r, 500));

const groups = await page.evaluate(() => {
    return Array.from(document.querySelectorAll('#help-toc .help-toc-group'))
        .map(el => el.textContent?.trim());
});
console.log('groups:', groups);

// Spot-check a few commands appear in the expected buckets. "attach sprite
// to transform" should show up under BOTH Sprite and Transform.
const spotCheck = await page.evaluate(() => {
    const items = Array.from(document.querySelectorAll('#help-toc .help-toc-item'));
    const occurrences = new Map();
    for (const el of items) {
        const name = el.dataset?.name;
        if (!name) continue;
        // Walk back to find this item's group header.
        let prev = el.previousElementSibling;
        while (prev && !prev.classList.contains('help-toc-group')) prev = prev.previousElementSibling;
        const group = prev?.textContent?.replace(/\s*\(\d+\)\s*$/, '').trim();
        if (!group) continue;
        const list = occurrences.get(name) ?? [];
        list.push(group);
        occurrences.set(name, list);
    }
    const pick = (name) => occurrences.get(name)?.sort() ?? [];
    return {
        attachSpriteToTransform: pick('attach sprite to transform'),
        setSpriteTexture: pick('set sprite texture'),
        debugSprite: pick('debug sprite'),
        downkey: pick('downkey'),
        sin: pick('sin'),
        print: pick('print'),
    };
});
console.log('spot check:', JSON.stringify(spotCheck, null, 2));

await browser.close();
