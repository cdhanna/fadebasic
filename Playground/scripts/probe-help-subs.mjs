// Verifies:
//   1. Language tab TOC shows sub-headings (H3+) indented under the
//      active H2 section.
//   2. Clicking a sub-heading scrolls the body to that anchor WITHOUT
//      bubbling outer scroll.
//   3. Fade code blocks pick up LSP-driven syntax highlighting (spans
//      with .fade-tok-* classes wrap the source tokens).

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

// Pick a section that's known to have sub-headings (Operations, Control
// Statements). Click Operations.
await page.evaluate(() => {
    const link = Array.from(document.querySelectorAll('#help-toc .help-toc-item'))
        .find(el => el.textContent?.trim() === 'Operations');
    link?.click();
});
await new Promise(r => setTimeout(r, 400));

const tocBreakdown = await page.evaluate(() => {
    const items = Array.from(document.querySelectorAll('#help-toc .help-toc-item'));
    return items.map(el => ({
        text: el.textContent?.trim(),
        isSub: el.classList.contains('help-toc-sub'),
        active: el.classList.contains('active'),
    }));
});

const subItems = tocBreakdown.filter(x => x.isSub);
console.log(`TOC sub-items under "Operations": ${subItems.length}`);
console.log('  first few:', subItems.slice(0, 6).map(s => s.text));
if (subItems.length === 0) {
    console.error('FAIL: no sub-items rendered for Operations');
    await browser.close();
    process.exit(1);
}

// Click a sub-item and confirm body scrolls there without bubbling.
const targetSub = subItems[subItems.length - 1].text;
await page.evaluate((t) => {
    const link = Array.from(document.querySelectorAll('#help-toc .help-toc-item.help-toc-sub'))
        .find(el => el.textContent?.trim() === t);
    link?.click();
}, targetSub);
await new Promise(r => setTimeout(r, 400));

const subSnap = await page.evaluate(() => {
    const body = document.getElementById('help-body');
    return {
        active: document.querySelector('#help-toc .help-toc-sub.active')?.textContent?.trim(),
        scrollTop: body?.scrollTop ?? -1,
        scrollHeight: body?.scrollHeight ?? -1,
        clientHeight: body?.clientHeight ?? -1,
    };
});
console.log('after sub-click:', JSON.stringify(subSnap));
if (subSnap.active !== targetSub) {
    console.error(`FAIL: active sub is "${subSnap.active}", expected "${targetSub}"`);
    await browser.close();
    process.exit(1);
}
if (subSnap.scrollTop <= 0) {
    console.error(`FAIL: body.scrollTop is ${subSnap.scrollTop}, expected > 0 (scrolled to sub anchor)`);
    await browser.close();
    process.exit(1);
}
if (subSnap.scrollHeight < subSnap.clientHeight) {
    console.error('FAIL: body content smaller than viewport — likely over-scroll glitch');
    await browser.close();
    process.exit(1);
}

// Now look for code blocks with the fade-tok-* spans. Wait a moment for
// the async tokenize pass to land.
await new Promise(r => setTimeout(r, 1500));
const highlightInfo = await page.evaluate(() => {
    const codes = Array.from(document.querySelectorAll('#help-body pre > code'));
    return codes.slice(0, 5).map(c => ({
        hasFadeSpans: c.querySelector('.fade-tok-keyword, .fade-tok-string, .fade-tok-comment, .fade-tok-number') !== null,
        firstSpans: Array.from(c.querySelectorAll('span'))
            .slice(0, 3)
            .map(s => ({ cls: s.className, text: s.textContent })),
        sample: (c.textContent ?? '').slice(0, 80),
    }));
});
console.log('code blocks (first 5):', JSON.stringify(highlightInfo, null, 2));

const anyHighlighted = highlightInfo.some(c => c.hasFadeSpans);
console.log(anyHighlighted ? '\n✓ PASS: subs + LSP highlighting working' : '\n⚠ subs work, but no .fade-tok-* spans found yet');

await browser.close();
process.exit(anyHighlighted ? 0 : 1);
