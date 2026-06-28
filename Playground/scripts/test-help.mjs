// Help tab probes: data loads, TOC + search filter, and the hover
// deep-link routes through the Help controller.
//
// Usage: node scripts/test-help.mjs

import { chromium } from 'playwright';

const url = process.argv[2] || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ viewport: { width: 1500, height: 950 } });
const page = await ctx.newPage();

const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e.message));

await page.goto(url, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
await new Promise((r) => setTimeout(r, 1500));

// Activate Help so its DOM is visible to Playwright queries.
async function activateHelp() {
    await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
    await page.waitForSelector('#help-search', { state: 'visible', timeout: 5000 });
}

const tests = [];
function test(name, fn) { tests.push({ name, fn }); }

test('Help panel populates a TOC with multiple commands', async () => {
    await activateHelp();
    // The dataset is loaded asynchronously after bootstrap; wait until
    // the TOC has at least a handful of entries.
    await page.waitForFunction(
        () => (document.querySelectorAll('#help-toc .help-toc-item').length || 0) >= 5,
        { timeout: 10000 },
    );
    const count = await page.locator('#help-toc .help-toc-item').count();
    const groups = await page.locator('#help-toc .help-toc-group').count();
    if (count < 5) throw new Error('expected >=5 TOC entries, got ' + count);
    if (groups < 1) throw new Error('expected >=1 TOC group header');
    return { count, groups };
});

test('Clicking a TOC entry renders its markdown in the body', async () => {
    // Pick the `rgb` command if available — it's a well-documented one.
    const rgbExists = await page.locator('#help-toc .help-toc-item[data-name="rgb"]').count();
    const targetName = rgbExists > 0 ? 'rgb' : await page.evaluate(() =>
        document.querySelector('#help-toc .help-toc-item')?.getAttribute('data-name'));
    if (!targetName) throw new Error('no TOC entries to click');
    await page.locator(`#help-toc .help-toc-item[data-name="${targetName}"]`).click();
    await new Promise((r) => setTimeout(r, 200));
    const bodyText = await page.locator('#help-body').textContent();
    if (!bodyText || !bodyText.includes(targetName)) {
        throw new Error(`body should contain command name "${targetName}": ` + (bodyText || '').slice(0, 80));
    }
    const activeName = await page.locator('#help-toc .help-toc-item.active').getAttribute('data-name');
    if (activeName !== targetName) throw new Error('TOC active highlight should match selection');
    return { targetName };
});

test('Search filter narrows the TOC', async () => {
    const beforeCount = await page.locator('#help-toc .help-toc-item').count();
    await page.fill('#help-search', 'print');
    await new Promise((r) => setTimeout(r, 200));
    const afterCount = await page.locator('#help-toc .help-toc-item').count();
    if (afterCount === 0) throw new Error('expected at least one match for "print"');
    if (afterCount >= beforeCount) throw new Error('filter should reduce the TOC count');
    // Every visible name should include "print" OR the body contains it.
    const names = await page.locator('#help-toc .help-toc-item')
        .evaluateAll((els) => els.map((e) => e.dataset.name));
    // It's OK if some matches came from the body — we don't insist all
    // names literally contain the query. But at least one should.
    if (!names.some((n) => /print/i.test(n))) {
        throw new Error('expected at least one TOC entry literally named with "print": ' + JSON.stringify(names));
    }
    await page.fill('#help-search', '');
    return { beforeCount, afterCount };
});

test('Search "no-such-command" shows empty-state message', async () => {
    await page.fill('#help-search', 'zzz-no-such-command-zzz');
    await new Promise((r) => setTimeout(r, 200));
    const emptyVisible = await page.locator('#help-toc .help-toc-empty').count();
    if (emptyVisible === 0) throw new Error('empty-state message should be visible');
    await page.fill('#help-search', '');
    return { ok: true };
});

test('window.__fadeHelp.openCommand activates Help + selects the command', async () => {
    // Switch to a different panel first so we can verify the open call
    // actually flips the Help tab to active.
    await page.evaluate(() => window.__fadeDockview?.getPanel?.('output')?.api?.setActive?.());
    await new Promise((r) => setTimeout(r, 200));
    const opened = await page.evaluate(() => window.__fadeHelp.openCommand('print'));
    if (opened !== true) throw new Error('openCommand("print") should return true (known command)');
    await new Promise((r) => setTimeout(r, 200));
    const active = await page.locator('#help-toc .help-toc-item.active').getAttribute('data-name');
    if (active !== 'print') throw new Error('TOC active should be "print", got ' + active);
    return { ok: true };
});

test('window.__fadeHelp.openCommand returns false for unknown commands', async () => {
    const opened = await page.evaluate(() => window.__fadeHelp.openCommand('this-is-not-real-zzzz'));
    if (opened !== false) throw new Error('expected false for unknown command, got ' + opened);
    return { ok: true };
});

let passed = 0, failed = 0;
for (const t of tests) {
    process.stdout.write(`• ${t.name} ... `);
    try {
        const r = await t.fn();
        console.log('OK', r ? JSON.stringify(r) : '');
        passed++;
    } catch (e) {
        console.log('FAIL');
        console.log('   ', e.message);
        failed++;
    }
}

if (pageErrors.length) {
    console.log('\nPage errors during run:');
    for (const e of pageErrors) console.log('  ' + e);
}

await browser.close();
console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
