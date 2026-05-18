// Headless tests for the Tests-panel improvements:
//   - search filter narrows the visible list
//   - failureFrames flow through the bridge and onto the run result
//   - inline test-log writes failure rows
//   - editor "Run Test at Cursor" / "Debug Test at Cursor" actions resolve
//     the surrounding test from cursor position
//
// Usage: node scripts/test-tests-panel.mjs

import { chromium } from 'playwright';

const url = process.argv[2] || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
const page = await context.newPage();

const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e));

await page.goto(url, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
// Settling reload to avoid HMR double-bootstrap noise.
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
await new Promise((r) => setTimeout(r, 1500));

// The Tests panel is hidden inside dockview when another tab is active.
// Activate it explicitly so Playwright sees the inputs as visible.
async function activateTestsPanel() {
    await page.evaluate(() => {
        const api = window.__fadeDockview;
        const p = api?.getPanel?.('tests');
        if (p) p.api.setActive();
    });
    await page.waitForSelector('#tests-search', { state: 'visible', timeout: 5000 });
}
await activateTestsPanel();

const TEST_SOURCE = [
    'test addsone',
    '    assert 1 + 1 = 2',
    'endtest',
    'test failsonpurpose',
    '    assert 1 = 0',
    'endtest',
    'test anotherpass',
    '    assert 2 + 2 = 4',
    'endtest',
].join('\n');

async function seedSource(source) {
    await page.evaluate(({ source }) => {
        const ed = window.monaco.editor.getEditors().find((e) => e.getModel()?.getLanguageId() === 'fade');
        const m = ed.getModel();
        m.applyEdits([{ range: m.getFullModelRange(), text: source }]);
    }, { source });
    // Wait for the 400ms refreshDebounce + a margin.
    await new Promise((r) => setTimeout(r, 1200));
}

const tests = [];
function test(name, fn) { tests.push({ name, fn }); }

test('bridge: failureFrames present on failing test result', async () => {
    const run = await page.evaluate(({ source }) => {
        return window.__fadeRunnerHelpers.runTests({ source });
    }, { source: TEST_SOURCE });
    if (run.failed !== 1) throw new Error('expected 1 failure, got ' + run.failed);
    const fail = run.results.find((r) => !r.passed);
    if (!fail) throw new Error('no failing result returned');
    if (!Array.isArray(fail.failureFrames)) throw new Error('failureFrames missing on failing result');
    if (fail.failureFrames.length === 0) throw new Error('failureFrames is empty');
    const f = fail.failureFrames[0];
    if (typeof f.lineNumber !== 'number') throw new Error('frame.lineNumber missing');
    return { frames: fail.failureFrames.length, line0: f.lineNumber };
});

test('ui: search filter narrows the visible test list', async () => {
    await seedSource(TEST_SOURCE);
    // Discover happens on doc-push; wait for the list to populate.
    await page.waitForFunction(() => document.querySelectorAll('#tests-list .test-item').length >= 3, { timeout: 8000 });
    const beforeCount = await page.locator('#tests-list .test-item').count();
    await page.fill('#tests-search', 'fail');
    await new Promise((r) => setTimeout(r, 100));
    const afterCount = await page.locator('#tests-list .test-item').count();
    const afterName = (await page.locator('#tests-list .test-name').first().textContent()) || '';
    await page.fill('#tests-search', '');
    if (beforeCount < 3) throw new Error('expected >=3 tests before filter, got ' + beforeCount);
    if (afterCount !== 1) throw new Error('expected 1 test after filter, got ' + afterCount);
    if (!/fail/i.test(afterName)) throw new Error('filtered name should mention "fail": ' + afterName);
    return { beforeCount, afterCount };
});

test('ui: running a test writes a row into the inline test log', async () => {
    await seedSource(TEST_SOURCE);
    await page.waitForFunction(() => document.querySelectorAll('#tests-list .test-item').length >= 3, { timeout: 8000 });
    // Click the Run button on the failing test row.
    const items = page.locator('#tests-list .test-item');
    const count = await items.count();
    let clickedFail = false;
    for (let i = 0; i < count; i++) {
        const row = items.nth(i);
        const name = (await row.locator('.test-name').textContent()) || '';
        if (/failsonpurpose/i.test(name)) {
            await row.locator('vscode-button', { hasText: 'Run' }).click();
            clickedFail = true;
            break;
        }
    }
    if (!clickedFail) throw new Error('failing row not found');
    await page.waitForFunction(
        () => document.querySelectorAll('#tests-log .tests-log-line.fail').length > 0,
        { timeout: 8000 },
    );
    const lines = await page.evaluate(() =>
        Array.from(document.querySelectorAll('#tests-log .tests-log-line')).map((l) => l.textContent),
    );
    const hasFailLine = lines.some((l) => /failsonpurpose/i.test(l));
    if (!hasFailLine) throw new Error('inline log missing fail row: ' + JSON.stringify(lines));
    return { lines: lines.length };
});

test('ui: failure-frame link in inline log jumps editor', async () => {
    // The previous test populated the log; rerun all to be safe.
    await page.evaluate(() => document.getElementById('tests-log-clear').click());
    await page.locator('#tests-run-all').click();
    await page.waitForFunction(
        () => document.querySelectorAll('#tests-log .test-failure-frame').length > 0,
        { timeout: 12000 },
    );
    // Move cursor far away first so we can verify the click moves it.
    await page.evaluate(() => {
        const ed = window.monaco.editor.getEditors().find((e) => e.getModel()?.getLanguageId() === 'fade');
        ed.setPosition({ lineNumber: 1, column: 1 });
    });
    const frameEl = page.locator('#tests-log .test-failure-frame').first();
    await frameEl.click();
    const newLine = await page.evaluate(() => {
        const ed = window.monaco.editor.getEditors().find((e) => e.getModel()?.getLanguageId() === 'fade');
        return ed.getPosition().lineNumber;
    });
    // Failing assert is on line 5 (1-based) of TEST_SOURCE.
    if (newLine === 1) throw new Error('editor cursor did not move (still at line 1)');
    return { newLine };
});

test('editor action: "Run Test at Cursor" runs the surrounding test', async () => {
    await seedSource(TEST_SOURCE);
    await page.waitForFunction(() => document.querySelectorAll('#tests-list .test-item').length >= 3, { timeout: 8000 });
    // Cursor inside `anotherpass` (line 8).
    await page.evaluate(() => {
        const ed = window.monaco.editor.getEditors().find((e) => e.getModel()?.getLanguageId() === 'fade');
        ed.setPosition({ lineNumber: 8, column: 1 });
        ed.focus();
    });
    // Clear the log to make the result observable.
    await page.evaluate(() => document.getElementById('tests-log-clear').click());
    // Trigger via Monaco action API (no need to actually right-click).
    await page.evaluate(() => {
        const ed = window.monaco.editor.getEditors().find((e) => e.getModel()?.getLanguageId() === 'fade');
        return ed.getAction('fade.runTestAtCursor').run();
    });
    await page.waitForFunction(
        () => Array.from(document.querySelectorAll('#tests-log .tests-log-line.pass'))
            .some((l) => /anotherpass/i.test(l.textContent)),
        { timeout: 12000 },
    );
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
    for (const e of pageErrors) console.log('  ' + e.message);
}

await browser.close();
console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
