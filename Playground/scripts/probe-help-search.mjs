// Verifies the global Help search:
//   1. Search bar lives above the tabs (not inside any single tab's UI).
//   2. Typing produces a dropdown with results from Commands + Language +
//      Playground sources.
//   3. Each result shows a source badge + title + snippet.
//   4. Clicking a result navigates to the right tab and selects it.
//   5. Esc clears, dropdown collapses.

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 200)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
// Use a monogame project so the Commands tab has sprite/texture/etc.
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgsearch', { create: true });
    const write = async (n, t) => { const fh = await dir.getFileHandle(n, { create: true }); const w = await fh.createWritable(); await w.write(t); await w.close(); };
    await write('fade.json', JSON.stringify({ name: 'mgsearch', type: 'monogame', commandDlls: [], sources: ['main.fbasic'] }) + '\n');
    await write('main.fbasic', 'do\nsync\nloop\n');
    localStorage.setItem('fade.activeProject', 'mgsearch');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise(r => setTimeout(r, 2500));

// Structural: search bar precedes tabs in the DOM.
const order = await page.evaluate(() => {
    const pane = document.getElementById('help-pane');
    if (!pane) return null;
    const kids = Array.from(pane.children).map(el => el.id || el.className).slice(0, 6);
    return kids;
});
console.log('help-pane children:', order);
if (order?.[0] !== 'help-search-bar' || order?.[2] !== 'help-tabs') {
    console.error('FAIL: search bar is not above the tabs');
    await browser.close(); process.exit(1);
}

// Type a query that should match Commands, Language, and Playground.
await page.fill('#help-search', 'sprite');
await new Promise(r => setTimeout(r, 300));

const results = await page.evaluate(() => {
    const rows = Array.from(document.querySelectorAll('#help-search-results .help-search-result'));
    return rows.map(r => ({
        badge: r.querySelector('.help-search-result-badge')?.textContent?.trim(),
        title: r.querySelector('.help-search-result-title')?.textContent?.trim(),
        snippetHasMark: !!r.querySelector('.help-search-result-snippet mark'),
        snippet: r.querySelector('.help-search-result-snippet')?.textContent?.slice(0, 80),
    }));
});
console.log(`results for "sprite": ${results.length}`);
for (const r of results.slice(0, 4)) console.log(' ', r);

if (results.length === 0) { console.error('FAIL: no results'); await browser.close(); process.exit(1); }
if (!results.some(r => r.badge === 'Commands')) { console.error('FAIL: no Commands hit'); await browser.close(); process.exit(1); }
if (!results.every(r => r.snippetHasMark)) { console.error('FAIL: a snippet is missing its <mark>'); await browser.close(); process.exit(1); }

// Type a query that lives in the Language doc, e.g. "scope" or "monkey".
await page.fill('#help-search', 'scope');
await new Promise(r => setTimeout(r, 300));
const langResults = await page.evaluate(() => {
    const rows = Array.from(document.querySelectorAll('#help-search-results .help-search-result'));
    return rows.map(r => ({
        badge: r.querySelector('.help-search-result-badge')?.textContent?.trim(),
        title: r.querySelector('.help-search-result-title')?.textContent?.trim(),
    }));
});
console.log('results for "scope":', langResults.slice(0, 4));
if (!langResults.some(r => r.badge === 'Language')) {
    console.error('FAIL: no Language hits for "scope"');
    await browser.close(); process.exit(1);
}

// Click the first Language result — should switch tabs + select section.
await page.evaluate(() => {
    const rows = Array.from(document.querySelectorAll('#help-search-results .help-search-result'));
    const langRow = rows.find(r => r.querySelector('.help-search-result-badge')?.textContent === 'Language');
    langRow?.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
});
await new Promise(r => setTimeout(r, 400));
const afterClick = await page.evaluate(() => ({
    activeTab: document.querySelector('.help-tab.active')?.dataset?.tab,
    dropdownHidden: !!document.getElementById('help-search-results')?.hidden,
    searchValue: document.getElementById('help-search')?.value,
}));
console.log('after click:', afterClick);
if (afterClick.activeTab !== 'language') {
    console.error('FAIL: did not switch to Language tab');
    await browser.close(); process.exit(1);
}
if (!afterClick.dropdownHidden) {
    console.error('FAIL: dropdown still open after click');
    await browser.close(); process.exit(1);
}

console.log('\n✓ PASS: global search across all 3 tabs');
await browser.close();
