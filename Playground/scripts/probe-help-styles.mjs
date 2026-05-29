// Dump computed CSS for a Commands group header vs a Language section
// header side-by-side so we can spot exactly what's still different.

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 200)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise(r => setTimeout(r, 700));

// Capture a commands group header (any one — they all share the class).
const commandsSample = await page.evaluate(() => {
    const el = document.querySelector('#help-toc .help-toc-group.help-toc-collapsible');
    if (!el) return null;
    const cs = getComputedStyle(el);
    const labelEl = el.querySelector('.help-toc-group-label');
    const lcs = labelEl ? getComputedStyle(labelEl) : null;
    const chevEl = el.querySelector('.help-toc-chevron');
    const ccs = chevEl ? getComputedStyle(chevEl) : null;
    return {
        which: 'commands-group',
        outerText: el.textContent?.trim(),
        outer: {
            display: cs.display, padding: cs.padding,
            color: cs.color, fontFamily: cs.fontFamily,
            fontSize: cs.fontSize, fontWeight: cs.fontWeight,
            textTransform: cs.textTransform, letterSpacing: cs.letterSpacing,
            backgroundColor: cs.backgroundColor,
            gap: cs.gap, alignItems: cs.alignItems,
            height: cs.height,
        },
        label: lcs && {
            fontFamily: lcs.fontFamily, fontSize: lcs.fontSize,
            color: lcs.color, textTransform: lcs.textTransform,
        },
        chevron: ccs && {
            width: ccs.width, height: ccs.height,
            fontSize: ccs.fontSize, color: ccs.color,
        },
    };
});

await page.click('.help-tab[data-tab="language"]');
await new Promise(r => setTimeout(r, 800));

const languageSample = await page.evaluate(() => {
    const el = document.querySelector('#help-toc .help-toc-item.help-toc-collapsible');
    if (!el) return null;
    const cs = getComputedStyle(el);
    const labelEl = el.querySelector('.help-toc-section-label');
    const lcs = labelEl ? getComputedStyle(labelEl) : null;
    const chevEl = el.querySelector('.help-toc-chevron');
    const ccs = chevEl ? getComputedStyle(chevEl) : null;
    return {
        which: 'language-section',
        outerText: el.textContent?.trim(),
        outer: {
            display: cs.display, padding: cs.padding,
            color: cs.color, fontFamily: cs.fontFamily,
            fontSize: cs.fontSize, fontWeight: cs.fontWeight,
            textTransform: cs.textTransform, letterSpacing: cs.letterSpacing,
            backgroundColor: cs.backgroundColor,
            gap: cs.gap, alignItems: cs.alignItems,
            height: cs.height,
        },
        label: lcs && {
            fontFamily: lcs.fontFamily, fontSize: lcs.fontSize,
            color: lcs.color, textTransform: lcs.textTransform,
        },
        chevron: ccs && {
            width: ccs.width, height: ccs.height,
            fontSize: ccs.fontSize, color: ccs.color,
        },
    };
});

const sideBySide = { commandsSample, languageSample };
console.log(JSON.stringify(sideBySide, null, 2));

// Walk every observed key and flag mismatches.
function diff(a, b, prefix = '') {
    const keys = new Set([...Object.keys(a ?? {}), ...Object.keys(b ?? {})]);
    const out = [];
    for (const k of keys) {
        const av = a?.[k], bv = b?.[k];
        if (typeof av === 'object' && av !== null) { out.push(...diff(av, bv, prefix + k + '.')); continue; }
        if (av !== bv) out.push(`${prefix}${k}: commands=${JSON.stringify(av)}  vs  language=${JSON.stringify(bv)}`);
    }
    return out;
}
const differences = diff(commandsSample, languageSample);
console.log('\nDifferences:');
for (const d of differences) console.log('  •', d);

await browser.close();
