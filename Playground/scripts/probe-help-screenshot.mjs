// Take a screenshot of the fresh-default-layout with Help active so we
// can see what the user means by "way too tall". Captures at several
// viewport sizes — maybe the issue is monitor-size-dependent.
//
// Usage: node scripts/probe-help-screenshot.mjs

import { chromium } from 'playwright';
import { writeFileSync, mkdirSync } from 'node:fs';

mkdirSync('/tmp/fade-help-probe', { recursive: true });
const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });

const viewports = [
    { name: '1280x800', w: 1280, h: 800 },
    { name: '1920x1080', w: 1920, h: 1080 },
    { name: '1440x900', w: 1440, h: 900 },
    { name: 'mac-13in', w: 1280, h: 720 },
    { name: '4k-tall', w: 1500, h: 2000 },
    { name: '5k-tall', w: 2560, h: 2880 },
];

for (const vp of viewports) {
    const page = await browser.newPage({ viewport: { width: vp.w, height: vp.h } });
    await page.goto(URL, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
    // Force-fresh layout.
    await page.evaluate(() => localStorage.removeItem('fade.dockview.layout.v3'));
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
    await new Promise((r) => setTimeout(r, 1500));

    await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
    await new Promise((r) => setTimeout(r, 500));

    const png = await page.screenshot({ fullPage: false });
    writeFileSync(`/tmp/fade-help-probe/${vp.name}.png`, png);
    const m = await page.evaluate(() => {
        const c = document.querySelector('.panel-cell[data-panel="help"], #help-pane')
            ?.closest('.panel-cell');
        const r = c?.getBoundingClientRect();
        return r ? { panelHeight: Math.round(r.height), viewportH: window.innerHeight } : null;
    });
    console.log(`${vp.name}: ${JSON.stringify(m)}`);
    await page.close();
}

await browser.close();
console.log('screenshots written to /tmp/fade-help-probe/');
