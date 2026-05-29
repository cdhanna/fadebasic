// Verifies the Language tab behaves like Commands: clicking a TOC entry
// swaps the body to that section only (no long scroll, no over-scroll
// glitch). Also confirms body.scrollTop resets to 0 on every selection.

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 200)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise(r => setTimeout(r, 600));

await page.click('.help-tab[data-tab="language"]');
await new Promise(r => setTimeout(r, 800));

// Inspect the TOC: each entry should be a discrete section title.
const tocTitles = await page.evaluate(() =>
    Array.from(document.querySelectorAll('#help-toc .help-toc-item'))
        .map(el => el.textContent?.trim()));
console.log('language TOC:', tocTitles);

if (!tocTitles.includes('Memory')) {
    console.error('FAIL: expected "Memory" in TOC');
    await browser.close();
    process.exit(1);
}

async function snapshot() {
    return await page.evaluate(() => {
        const body = document.getElementById('help-body');
        const active = document.querySelector('#help-toc .help-toc-item.active');
        const firstH = body?.querySelector('h1, h2, h3');
        return {
            activeTitle: active?.textContent?.trim() ?? null,
            firstHeading: firstH?.textContent?.trim() ?? null,
            scrollTop: body?.scrollTop ?? -1,
            scrollHeight: body?.scrollHeight ?? -1,
            clientHeight: body?.clientHeight ?? -1,
        };
    });
}

// Click late-doc sections (Memory, Testing) to exercise the regression
// path — they were the ones that triggered the over-scroll before.
for (const target of ['Memory', 'Testing', 'Comments']) {
    await page.evaluate((t) => {
        const link = Array.from(document.querySelectorAll('#help-toc .help-toc-item'))
            .find(el => el.textContent?.trim() === t);
        link?.click();
    }, target);
    await new Promise(r => setTimeout(r, 300));
    const snap = await snapshot();
    console.log(`clicked "${target}":`, snap);
    if (snap.activeTitle !== target) {
        console.error(`FAIL: active TOC item is "${snap.activeTitle}", expected "${target}"`);
        await browser.close();
        process.exit(1);
    }
    if (snap.firstHeading !== target) {
        console.error(`FAIL: body's first heading is "${snap.firstHeading}", expected "${target}"`);
        await browser.close();
        process.exit(1);
    }
    if (snap.scrollTop !== 0) {
        console.error(`FAIL: body.scrollTop is ${snap.scrollTop}, expected 0`);
        await browser.close();
        process.exit(1);
    }
    if (snap.scrollHeight < snap.clientHeight) {
        console.error(`FAIL: body content (${snap.scrollHeight}) is smaller than viewport (${snap.clientHeight}) — likely over-scroll glitch`);
        await browser.close();
        process.exit(1);
    }
}

console.log('\n✓ PASS: Language tab swaps sections cleanly, no over-scroll');
await browser.close();
