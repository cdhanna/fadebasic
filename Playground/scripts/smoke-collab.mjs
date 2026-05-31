// Smoke test for the Live Session feature: boot two pages in the same
// Playwright browser context (BroadcastChannel works between them), have
// one host + one join via the Mock transport, then verify text typed on
// one page propagates to the other via the Yjs CRDT.
//
// Requires the dev server already running on port 5311 (`npm run dev`).

import { chromium } from 'playwright';

const URL = 'http://localhost:5311/';

(async () => {
    const browser = await chromium.launch({ headless: true });
    const context = await browser.newContext();
    const failures = [];

    async function makePage(label) {
        const p = await context.newPage();
        p.on('console', (m) => {
            const t = m.type();
            if (t === 'error' || t === 'warning') {
                console.log(`[${label}] console.${t}:`, m.text());
            }
        });
        p.on('pageerror', (e) => {
            console.log(`[${label}] PAGE ERROR:`, e.message);
            failures.push(`${label}: ${e.message}`);
        });
        await p.goto(URL, { waitUntil: 'load' });
        await p.waitForFunction(
            () => window.monaco != null && window.__fadeDockview != null,
            { timeout: 30000 },
        );
        return p;
    }

    const host = await makePage('host');
    const guest = await makePage('guest');

    // Both pages: open the first file from the workspace tree.
    for (const [label, p] of [['host', host], ['guest', guest]]) {
        try {
            await p.waitForSelector('#file-list li.file-row', { timeout: 15000 });
            await p.click('#file-list li.file-row >> nth=0');
            await p.waitForTimeout(500);
        } catch (e) {
            console.log(`[${label}] failed to open first file:`, e.message);
        }
    }

    async function openLiveSession(p, label) {
        const viewBtn = p.locator('button:has-text("View")').first();
        await viewBtn.click();
        await p.waitForTimeout(200);
        await p.locator('button.view-menu-item:has-text("Live Session")').first().click();
        await p.waitForTimeout(500);
        const headerVisible = await p.locator('.fade-live-header:has-text("Live Session")').isVisible();
        if (!headerVisible) failures.push(`${label}: Live Session panel header not visible`);
    }

    await openLiveSession(host, 'host');
    await openLiveSession(guest, 'guest');

    // Host: start a session.
    await host.locator('.fade-live-btn:has-text("Host a session")').click();
    await host.waitForSelector('.fade-live-modal');
    await host.fill('.fade-live-modal-field input[type="text"]', 'Alice');
    const transportSelect = host.locator('.fade-live-modal-field select');
    if (await transportSelect.count() > 0) {
        await transportSelect.selectOption({ label: /Mock/ });
    }
    await host.locator('.fade-live-modal button[type="submit"]').click();
    await host.waitForSelector('.fade-live-code-box', { timeout: 8000 });
    const roomId = (await host.locator('.fade-live-code-box').first().textContent())?.trim();
    console.log('[host] roomId =', roomId);
    if (!roomId) failures.push('host: failed to obtain roomId');

    // Guest: join.
    await guest.locator('.fade-live-btn:has-text("Join a session")').click();
    await guest.waitForSelector('.fade-live-modal');
    const inputs = guest.locator('.fade-live-modal-field input');
    await inputs.nth(0).fill('Bob');
    await inputs.nth(1).fill(roomId ?? '');
    const gSel = guest.locator('.fade-live-modal-field select');
    if (await gSel.count() > 0) {
        await gSel.selectOption({ label: /Mock/ });
    }
    await guest.locator('.fade-live-modal button[type="submit"]').click();
    await guest.waitForSelector('.fade-live-banner-guest', { timeout: 8000 });

    await host.waitForTimeout(800);

    const hostPeers = await host.locator('.fade-live-peer-name').allTextContents();
    const guestPeers = await guest.locator('.fade-live-peer-name').allTextContents();
    console.log('[host] peer list:', hostPeers);
    console.log('[guest] peer list:', guestPeers);
    if (!hostPeers.some((n) => /Bob/.test(n))) failures.push('host did not see Bob in peer list');
    if (!guestPeers.some((n) => /Alice/.test(n))) failures.push('guest did not see Alice in peer list');

    // Type on the host, verify it appears on the guest.
    await host.evaluate(() => {
        const ed = window.monaco.editor.getEditors()[0];
        const model = ed.getModel();
        if (!model) throw new Error('no model');
        model.setValue('hello from alice\n' + model.getValue());
    });
    await host.waitForTimeout(800);

    const guestText = await guest.evaluate(() => {
        const ed = window.monaco.editor.getEditors()[0];
        return ed?.getModel()?.getValue() ?? null;
    });
    console.log('[guest] editor first 60 chars:', (guestText ?? '').slice(0, 60));
    if (!guestText || !guestText.startsWith('hello from alice')) {
        failures.push(`guest did not receive host edit. text was: ${(guestText ?? '').slice(0, 80)}`);
    }

    // Reverse direction.
    await guest.evaluate(() => {
        const ed = window.monaco.editor.getEditors()[0];
        const model = ed.getModel();
        model.setValue('typed by bob\n' + model.getValue());
    });
    await guest.waitForTimeout(800);
    const hostText = await host.evaluate(() => {
        const ed = window.monaco.editor.getEditors()[0];
        return ed?.getModel()?.getValue() ?? null;
    });
    if (!hostText || !hostText.startsWith('typed by bob')) {
        failures.push(`host did not receive guest edit. text was: ${(hostText ?? '').slice(0, 80)}`);
    }

    await browser.close();

    if (failures.length) {
        console.error('\nFAILURES:');
        for (const f of failures) console.error('  -', f);
        process.exit(1);
    }
    console.log('\nALL CHECKS PASSED');
})();
