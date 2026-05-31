// Smoke test that verifies the game-stream overlay isn't shadowing the
// real game surface when no live session is active. After Phase 2A
// landed the overlay used `display: flex` in its inline style — that
// shadowed the [hidden] attribute's UA display:none, so the overlay was
// permanently visible and covered both the web iframe and the monogame
// Blazor root with a black box. This probe boots the page, asserts the
// overlay element has `hidden` set, has zero rendered size, and that the
// canvas underneath (web iframe or monogame root) is visible.
//
// Requires `npm run dev` running on localhost:5311.

import { chromium } from 'playwright';

const URL = 'http://localhost:5311/';

(async () => {
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    const failures = [];
    page.on('pageerror', (e) => failures.push('pageerror: ' + e.message));

    // We deliberately don't wait for full bootstrap (the dev env has a
    // pre-existing LSP-worker init failure that aborts bootstrap before
    // monaco/dockview attach). The bug we're verifying is in the static
    // index.html — the overlay is in the DOM as soon as the page parses.
    await page.goto(URL, { waitUntil: 'domcontentloaded' });
    // Give CSS a beat to apply.
    await page.waitForTimeout(500);

    const overlayInfo = await page.evaluate(() => {
        const overlay = document.getElementById('game-stream-overlay');
        if (!overlay) return { exists: false };
        const rect = overlay.getBoundingClientRect();
        const cs = window.getComputedStyle(overlay);
        return {
            exists: true,
            hidden: overlay.hidden,
            display: cs.display,
            width: rect.width,
            height: rect.height,
            zIndex: cs.zIndex,
        };
    });
    console.log('overlay:', overlayInfo);
    if (!overlayInfo.exists) failures.push('overlay element missing entirely');
    else {
        if (!overlayInfo.hidden) failures.push('overlay.hidden is not true at idle');
        if (overlayInfo.display !== 'none') failures.push(`overlay computed display is "${overlayInfo.display}", expected "none"`);
        if (overlayInfo.width !== 0 || overlayInfo.height !== 0) {
            failures.push(`overlay is taking ${overlayInfo.width}x${overlayInfo.height} space; should be 0x0 when hidden`);
        }
    }

    // The monogame Blazor root or the web iframe should be visible
    // underneath. They're siblings inside the game panel-cell.
    const surfacesInfo = await page.evaluate(() => {
        const mg = document.getElementById('mg-blazor-root');
        const webHost = document.getElementById('web-preview-host');
        const mgCs = mg ? window.getComputedStyle(mg) : null;
        const webCs = webHost ? window.getComputedStyle(webHost) : null;
        return {
            mgPresent: !!mg,
            mgDisplay: mgCs?.display ?? null,
            webPresent: !!webHost,
            webDisplay: webCs?.display ?? null,
        };
    });
    console.log('surfaces:', surfacesInfo);
    // At least one of the two surfaces should be rendered (display !== 'none').
    const anyVisible =
        (surfacesInfo.mgPresent && surfacesInfo.mgDisplay !== 'none') ||
        (surfacesInfo.webPresent && surfacesInfo.webDisplay !== 'none');
    if (!anyVisible) failures.push('neither game surface is visible (mg + web both display:none)');

    // Toggle the overlay visible and confirm display flips to flex.
    // Mirrors what main.ts's showGameStreamOverlay() does when a remote
    // peer starts running — flips hidden off, then sets the banner text.
    // If the cascade is wrong, display would stay `none` and we'd never
    // see the streamed frames.
    const toggledInfo = await page.evaluate(() => {
        const overlay = document.getElementById('game-stream-overlay');
        if (!overlay) return null;
        overlay.hidden = false;
        const cs = window.getComputedStyle(overlay);
        return { display: cs.display, position: cs.position };
    });
    console.log('toggled-visible:', toggledInfo);
    if (!toggledInfo) failures.push('overlay missing on toggle test');
    else if (toggledInfo.display !== 'flex') {
        failures.push(`after setting hidden=false, display is "${toggledInfo.display}", expected "flex"`);
    }

    await browser.close();

    if (failures.length) {
        console.error('\nFAILURES:');
        for (const f of failures) console.error('  -', f);
        process.exit(1);
    }
    console.log('\nALL CHECKS PASSED — game overlay is correctly hidden at idle');
})();
