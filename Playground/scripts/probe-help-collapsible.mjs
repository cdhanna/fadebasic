// Verifies the collapsible-TOC changes:
//   1. All Commands groups start collapsed; clicking a header expands it.
//   2. Language tab section TOC entries start collapsed; clicking expands +
//      switches to that page.
//   3. Sub-headings live under every section header (always present in
//      DOM after expand), no longer "only under the active section".
//   4. External selectCommand (e.g. hover deep-link) auto-expands the
//      command's group(s).

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 200)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise(r => setTimeout(r, 700));

// ── commands: groups start collapsed ──────────────────────────────────────
const initial = await page.evaluate(() => ({
    groups: Array.from(document.querySelectorAll('#help-toc .help-toc-group')).length,
    items: Array.from(document.querySelectorAll('#help-toc .help-toc-item:not(.help-toc-collapsible)')).length,
    expanded: Array.from(document.querySelectorAll('#help-toc .help-toc-group.expanded')).length,
}));
console.log('commands initial:', initial);
if (initial.items !== 0) { console.error('FAIL: items visible before any expansion'); await browser.close(); process.exit(1); }
if (initial.expanded !== 0) { console.error('FAIL: a group is pre-expanded'); await browser.close(); process.exit(1); }

// Click a specific group header.
await page.evaluate(() => {
    const g = Array.from(document.querySelectorAll('#help-toc .help-toc-group'))
        .find(el => el.textContent?.includes('Standard'));
    g?.click();
});
await new Promise(r => setTimeout(r, 200));
const afterExpand = await page.evaluate(() => ({
    expandedNames: Array.from(document.querySelectorAll('#help-toc .help-toc-group.expanded'))
        .map(el => el.querySelector('.help-toc-group-label')?.textContent),
    visibleItems: Array.from(document.querySelectorAll('#help-toc .help-toc-item:not(.help-toc-collapsible)')).length,
}));
console.log('after click "Standard":', afterExpand);
if (afterExpand.visibleItems === 0) { console.error('FAIL: items still hidden after expand'); await browser.close(); process.exit(1); }

// Click again to collapse.
await page.evaluate(() => {
    const g = Array.from(document.querySelectorAll('#help-toc .help-toc-group'))
        .find(el => el.textContent?.includes('Standard'));
    g?.click();
});
await new Promise(r => setTimeout(r, 200));
const afterCollapse = await page.evaluate(() => ({
    visibleItems: Array.from(document.querySelectorAll('#help-toc .help-toc-item:not(.help-toc-collapsible)')).length,
}));
console.log('after collapse:', afterCollapse);
if (afterCollapse.visibleItems !== 0) { console.error('FAIL: items still visible after collapse'); await browser.close(); process.exit(1); }

// External selectCommand auto-expands.
await page.evaluate(() => window.__fadeHelp?.openCommand?.('print'));
await new Promise(r => setTimeout(r, 400));
const afterDeepLink = await page.evaluate(() => ({
    expanded: Array.from(document.querySelectorAll('#help-toc .help-toc-group.expanded'))
        .map(el => el.querySelector('.help-toc-group-label')?.textContent),
    activeItemText: document.querySelector('#help-toc .help-toc-item.active:not(.help-toc-collapsible)')?.textContent,
}));
console.log('after deep-link "print":', afterDeepLink);
if (!afterDeepLink.expanded.length) { console.error('FAIL: deep-link did not auto-expand'); await browser.close(); process.exit(1); }
if (afterDeepLink.activeItemText !== 'print') { console.error('FAIL: deep-link did not surface "print"'); await browser.close(); process.exit(1); }

// ── language: sections start collapsed, subs always exist when expanded ───
await page.click('.help-tab[data-tab="language"]');
await new Promise(r => setTimeout(r, 800));
const langInitial = await page.evaluate(() => ({
    sections: Array.from(document.querySelectorAll('#help-toc .help-toc-collapsible')).length,
    expanded: Array.from(document.querySelectorAll('#help-toc .help-toc-collapsible.expanded')).length,
    subs: Array.from(document.querySelectorAll('#help-toc .help-toc-sub')).length,
}));
console.log('language initial:', langInitial);
if (langInitial.expanded !== 0 || langInitial.subs !== 0) {
    console.error('FAIL: language tab not fully collapsed');
    await browser.close(); process.exit(1);
}

// Click "Operations" — it has subs. Should expand AND switch the body.
await page.evaluate(() => {
    const link = Array.from(document.querySelectorAll('#help-toc .help-toc-collapsible'))
        .find(el => el.textContent?.includes('Operations'));
    link?.click();
});
await new Promise(r => setTimeout(r, 400));
const langAfter = await page.evaluate(() => {
    const expanded = Array.from(document.querySelectorAll('#help-toc .help-toc-collapsible.expanded'))
        .map(el => el.querySelector('.help-toc-section-label')?.textContent);
    const subs = Array.from(document.querySelectorAll('#help-toc .help-toc-sub'))
        .map(el => el.textContent);
    const bodyFirst = document.querySelector('#help-body h1, #help-body h2')?.textContent;
    return { expanded, subs, bodyFirst };
});
console.log('language after expand "Operations":', langAfter);
if (!langAfter.expanded.some(t => t?.includes('Operations'))) {
    console.error('FAIL: Operations did not expand');
    await browser.close(); process.exit(1);
}
if (langAfter.subs.length === 0) {
    console.error('FAIL: no subs visible under expanded section');
    await browser.close(); process.exit(1);
}
if (!langAfter.bodyFirst?.includes('Operations')) {
    console.error(`FAIL: body did not switch to Operations (saw "${langAfter.bodyFirst}")`);
    await browser.close(); process.exit(1);
}

// Subs exist independent of body selection: switch body to a different
// section by clicking another section's row — Operations should STILL be
// expanded (sub-items still visible) until we explicitly collapse it.
await page.evaluate(() => {
    const link = Array.from(document.querySelectorAll('#help-toc .help-toc-collapsible'))
        .find(el => el.textContent?.includes('Variables') && !el.textContent?.includes('Operations'));
    link?.click();
});
await new Promise(r => setTimeout(r, 400));
const langStill = await page.evaluate(() => ({
    operationsStillExpanded: Array.from(document.querySelectorAll('#help-toc .help-toc-collapsible.expanded'))
        .some(el => el.textContent?.includes('Operations')),
    subsForOperationsVisible: Array.from(document.querySelectorAll('#help-toc .help-toc-sub')).length > 0,
}));
console.log('after navigating to Variables:', langStill);
if (!langStill.operationsStillExpanded) {
    console.error('FAIL: Operations collapsed after navigating to another section');
    await browser.close(); process.exit(1);
}

console.log('\n✓ PASS: collapsible TOC across Commands + Language, subs always there');
await browser.close();
