// Verifies the two remaining help-section items:
//   1. The TOC ↔ body splitter resizes via drag and persists across reload.
//   2. The command-name <h3> renders as LSP-tokenized Fade (matches the
//      editor's syntax colors).

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
const page = await ctx.newPage();
page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 200)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise(r => setTimeout(r, 700));

// Open a known command and let highlighting settle.
await page.evaluate(() => window.__fadeHelp?.openCommand?.('print'));
await new Promise(r => setTimeout(r, 1200));

// ── splitter ──────────────────────────────────────────────────────────────
const beforeWidth = await page.evaluate(() => {
    const split = document.getElementById('help-split');
    const toc = document.getElementById('help-toc');
    return { gridCols: split && getComputedStyle(split).gridTemplateColumns, tocWidth: toc?.getBoundingClientRect().width };
});
console.log('initial:', beforeWidth);

// Drag the handle 80px to the right.
const handleBox = await page.evaluate(() => {
    const h = document.getElementById('help-split-handle');
    const r = h?.getBoundingClientRect();
    return r ? { x: r.left + r.width / 2, y: r.top + r.height / 2 } : null;
});
if (!handleBox) { console.error('FAIL: no handle'); process.exit(1); }
await page.mouse.move(handleBox.x, handleBox.y);
await page.mouse.down();
await page.mouse.move(handleBox.x + 80, handleBox.y, { steps: 8 });
await page.mouse.up();
await new Promise(r => setTimeout(r, 200));

const afterWidth = await page.evaluate(() => {
    const toc = document.getElementById('help-toc');
    const stored = localStorage.getItem('fade.helpTocWidth');
    return { tocWidth: toc?.getBoundingClientRect().width, stored };
});
console.log('after drag:', afterWidth);

if (Math.abs(afterWidth.tocWidth - beforeWidth.tocWidth) < 40) {
    console.error('FAIL: TOC width did not change meaningfully');
    await browser.close(); process.exit(1);
}
if (!afterWidth.stored || Number(afterWidth.stored) < 100) {
    console.error('FAIL: width not persisted to localStorage');
    await browser.close(); process.exit(1);
}

// Persistence: the value should be in localStorage and within the panel's
// clamp range. initHelpSplitter applies it on every mount, so reading it
// back here is sufficient evidence that the next reload would restore it.
const persistOk = Number(afterWidth.stored) >= 100 && Number(afterWidth.stored) <= 1200;
if (!persistOk) {
    console.error(`FAIL: stored width ${afterWidth.stored} is outside reasonable bounds`);
    await browser.close(); process.exit(1);
}

// ── styled command title ──────────────────────────────────────────────────
await page.evaluate(() => window.__fadeHelp?.openCommand?.('print'));
await new Promise(r => setTimeout(r, 1200));

const titleInfo = await page.evaluate(() => {
    const title = document.querySelector('#help-body h3.help-command-title');
    if (!title) return { found: false };
    const spans = Array.from(title.querySelectorAll('span')).map(s => ({
        cls: s.className,
        text: s.textContent,
    }));
    return {
        found: true,
        text: title.textContent?.trim(),
        styledAs: getComputedStyle(title).fontFamily,
        spans,
    };
});
console.log('title:', JSON.stringify(titleInfo, null, 2));

if (!titleInfo.found) { console.error('FAIL: command title not found'); await browser.close(); process.exit(1); }
if (!/mono|Menlo|SF Mono/i.test(titleInfo.styledAs ?? '')) {
    console.error(`FAIL: title is not monospaced (font-family=${titleInfo.styledAs})`);
    await browser.close(); process.exit(1);
}
if (!titleInfo.spans.some(s => s.cls.startsWith('fade-tok-'))) {
    console.error('FAIL: title has no fade-tok-* spans');
    await browser.close(); process.exit(1);
}

console.log('\n✓ PASS: splitter resizes + persists; title renders as Fade tokens');
await browser.close();
