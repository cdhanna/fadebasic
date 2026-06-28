// Phase 3 probes: project viewer overlay + project switching.
// Verifies create / switch flows + that fade.json + main.fbasic
// land in fresh projects with the expected content.
//
// Usage: node scripts/test-projects-overlay.mjs

import { chromium } from 'playwright';

const url = process.argv[2] || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ viewport: { width: 1500, height: 950 } });
const page = await ctx.newPage();

const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e));

// Wipe OPFS so probes start clean.
await page.goto(url, { waitUntil: 'domcontentloaded' });
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    try { await root.removeEntry('workspace', { recursive: true }); } catch { /* ignore */ }
    localStorage.removeItem('fade.activeProject');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
await new Promise((r) => setTimeout(r, 1500));

const tests = [];
function test(name, fn) { tests.push({ name, fn }); }

test('header button opens overlay', async () => {
    await page.locator('#open-projects').click();
    await page.waitForSelector('#project-overlay:not([hidden])', { timeout: 3000 });
    const rows = await page.locator('#project-list .project-row').count();
    if (rows < 1) throw new Error('overlay should list at least one project');
    return { rows };
});

test('default project is marked active', async () => {
    const active = await page.locator('#project-list .project-row.active').textContent();
    if (!/default/.test(active || '')) throw new Error('default project should be active: ' + active);
    return { active: (active || '').trim().slice(0, 30) };
});

test('Esc closes the overlay', async () => {
    await page.keyboard.press('Escape');
    await page.waitForFunction(() => document.getElementById('project-overlay')?.hidden === true, { timeout: 3000 });
    return { ok: true };
});

test('⌘P/Ctrl+P opens the overlay', async () => {
    await page.keyboard.press('ControlOrMeta+p');
    await page.waitForSelector('#project-overlay:not([hidden])', { timeout: 3000 });
    return { ok: true };
});

test('creating a new project seeds fade.json + main.fbasic and switches', async () => {
    await page.locator('#project-new-input').fill('demoproj');
    await page.locator('#project-new-input').press('Enter');
    // switchToProject reloads — wait for bootstrap to finish on the new
    // project, then verify state.
    await page.waitForFunction(() => window.__fadeBootstrapDone === true && /demoproj/.test(document.getElementById('project-name')?.textContent || ''), { timeout: 30000 });
    await new Promise((r) => setTimeout(r, 1000));
    const label = (await page.locator('#project-name').textContent() || '').trim();
    if (!/demoproj/.test(label)) throw new Error('header should show demoproj, got: ' + label);
    // File list must contain fade.json + main.fbasic.
    const names = await page.locator('#file-list li').evaluateAll((els) =>
        els.map((e) => (e.dataset.name || e.textContent || '').trim().split('\n')[0]),
    );
    if (!names.some((n) => /fade\.json/.test(n))) throw new Error('fade.json missing in new project: ' + names.join(','));
    if (!names.some((n) => /main\.fbasic/.test(n))) throw new Error('main.fbasic missing in new project: ' + names.join(','));
    return { label, names };
});

test('overlay lists both projects after the new one was created', async () => {
    await page.locator('#open-projects').click();
    await page.waitForSelector('#project-overlay:not([hidden])', { timeout: 3000 });
    // Wait for the lazy meta resolution.
    await new Promise((r) => setTimeout(r, 600));
    const rows = await page.locator('#project-list .project-row').evaluateAll((els) =>
        els.map((e) => (e.querySelector('.project-row-name')?.textContent || '').trim()),
    );
    if (!rows.some((n) => /default/.test(n))) throw new Error('default missing in overlay: ' + rows.join(','));
    if (!rows.some((n) => /demoproj/.test(n))) throw new Error('demoproj missing in overlay: ' + rows.join(','));
    return { rows };
});

test('switching back to default project works', async () => {
    // Click the default row.
    await page.evaluate(() => {
        const rows = Array.from(document.querySelectorAll('#project-list .project-row'));
        const row = rows.find((r) => /default/.test(r.querySelector('.project-row-name')?.textContent || ''));
        row?.click();
    });
    await page.waitForFunction(() => window.__fadeBootstrapDone === true && /default/.test(document.getElementById('project-name')?.textContent || ''), { timeout: 30000 });
    const label = (await page.locator('#project-name').textContent() || '').trim();
    if (!/default/.test(label)) throw new Error('header should show default, got: ' + label);
    return { label };
});

test('duplicate project name is rejected', async () => {
    await page.locator('#open-projects').click();
    await page.waitForSelector('#project-overlay:not([hidden])', { timeout: 3000 });
    await page.locator('#project-new-input').fill('demoproj');
    await page.locator('#project-new-input').press('Enter');
    await new Promise((r) => setTimeout(r, 300));
    const errVisible = await page.locator('#project-new-error:not([hidden])').count();
    if (!errVisible) throw new Error('expected duplicate-name error to be visible');
    const errText = (await page.locator('#project-new-error').textContent() || '').trim();
    await page.keyboard.press('Escape');
    if (!/already exists/i.test(errText)) throw new Error('error wording should mention "already exists": ' + errText);
    return { errText };
});

test('forceHardReset() is exposed on window', async () => {
    const isFn = await page.evaluate(() => typeof window.forceHardReset === 'function');
    if (!isFn) throw new Error('window.forceHardReset should be a function');
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
