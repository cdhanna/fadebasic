// Diff layout-relevant computed styles for every TOC row variant:
//   Commands parent (group header)  vs  Language parent (section row)
//   Commands child  (command item)  vs  Language child  (sub heading)
// Surfaces all padding / indentation / box-model differences.

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 200)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

// Use monogame so the Commands tab has a meaningful group structure.
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgpad', { create: true });
    const write = async (n, t) => { const fh = await dir.getFileHandle(n, { create: true }); const w = await fh.createWritable(); await w.write(t); await w.close(); };
    await write('fade.json', JSON.stringify({ name: 'mgpad', type: 'monogame', commandDlls: [], sources: ['main.fbasic'] }) + '\n');
    await write('main.fbasic', 'do\nsync\nloop\n');
    localStorage.setItem('fade.activeProject', 'mgpad');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise(r => setTimeout(r, 2500));

// Expand the first Commands group so we can sample a child.
await page.evaluate(() => {
    const g = document.querySelector('#help-toc .help-toc-group.help-toc-collapsible');
    g?.click?.();
});
await new Promise(r => setTimeout(r, 300));

const KEYS = ['display', 'paddingLeft', 'paddingRight', 'paddingTop', 'paddingBottom',
              'marginLeft', 'marginRight', 'fontFamily', 'fontSize', 'fontWeight',
              'lineHeight', 'gap', 'height', 'textTransform'];

const sample = async (sel, label) => {
    const out = await page.evaluate(({ sel, KEYS }) => {
        const el = document.querySelector(sel);
        if (!el) return null;
        const cs = getComputedStyle(el);
        const rect = el.getBoundingClientRect();
        const obj = {};
        for (const k of KEYS) obj[k] = cs[k];
        obj.left = rect.left;
        obj.width = rect.width;
        // What x does the actual text content start at?
        const textNode = Array.from(el.childNodes).find(n => n.nodeType === Node.TEXT_NODE && n.nodeValue?.trim()) ?? null;
        const labelSpan = el.querySelector('.help-toc-group-label, .help-toc-section-label');
        const textRect = labelSpan?.getBoundingClientRect() ?? null;
        if (textRect) obj.textLeft = textRect.left;
        else if (textNode) {
            // Measure first run of text by wrapping a range.
            const r = document.createRange();
            r.selectNodeContents(textNode);
            obj.textLeft = r.getBoundingClientRect().left;
        }
        return obj;
    }, { sel, KEYS });
    return { label, ...out };
};

const cmdParent = await sample('#help-toc .help-toc-group.help-toc-collapsible.expanded', 'Commands parent');
const cmdChild  = await sample('#help-toc .help-toc-item:not(.help-toc-collapsible):not(.help-toc-sub)', 'Commands child');

await page.click('.help-tab[data-tab="language"]');
await new Promise(r => setTimeout(r, 800));
// Expand a section that has subs (Operations).
await page.locator('#help-toc .help-toc-collapsible', { hasText: 'Operations' }).first().click();
await new Promise(r => setTimeout(r, 300));

const langParent = await sample('#help-toc .help-toc-item.help-toc-collapsible.expanded', 'Language parent');
const langChild  = await sample('#help-toc .help-toc-sub', 'Language child');

const all = { cmdParent, cmdChild, langParent, langChild };
for (const k of Object.keys(all)) {
    console.log(`\n── ${all[k].label}`);
    for (const key of Object.keys(all[k])) {
        if (key === 'label') continue;
        console.log(`   ${key.padEnd(18)} ${all[k][key]}`);
    }
}

console.log('\n── differences (parent: commands vs language)');
for (const k of [...KEYS, 'left', 'textLeft']) {
    if (cmdParent?.[k] !== langParent?.[k]) {
        console.log(`   ${k.padEnd(18)} cmd=${cmdParent?.[k]}   lang=${langParent?.[k]}`);
    }
}
console.log('\n── differences (child: commands vs language)');
for (const k of [...KEYS, 'left', 'textLeft']) {
    if (cmdChild?.[k] !== langChild?.[k]) {
        console.log(`   ${k.padEnd(18)} cmd=${cmdChild?.[k]}   lang=${langChild?.[k]}`);
    }
}

await browser.close();
