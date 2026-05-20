// Diagnostic: measure the bottom dockview group's height + Help panel
// content height on a fresh layout, so we can tell whether the "Help is
// too tall" complaint is about the group size or the help-pane forcing
// its parent taller than configured.
//
// Usage: node scripts/probe-help-height.mjs

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1500, height: 950 } });
await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise((r) => setTimeout(r, 1000));

// Force-restore default layout to bypass any localStorage state.
await page.evaluate(() => localStorage.removeItem('fade.dockview.layout.v3'));
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise((r) => setTimeout(r, 1500));

// Click the Help tab to force it active.
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise((r) => setTimeout(r, 500));

const measurements = await page.evaluate(() => {
    function rect(el) {
        if (!el) return null;
        const r = el.getBoundingClientRect();
        return { width: Math.round(r.width), height: Math.round(r.height), top: Math.round(r.top) };
    }
    const helpPane = document.getElementById('help-pane');
    const helpSplit = document.getElementById('help-split');
    const helpToc = document.getElementById('help-toc');
    const helpBody = document.getElementById('help-body');
    const panelCell = helpPane?.closest('.panel-cell');
    const dockviewContent = panelCell?.parentElement;
    const dockGroup = dockviewContent?.closest('.dv-groupview, .dv-grid-view, .groupview');
    return {
        viewport: { width: window.innerWidth, height: window.innerHeight },
        panelCell: rect(panelCell),
        helpPane: rect(helpPane),
        helpSplit: rect(helpSplit),
        helpToc: rect(helpToc),
        helpBody: rect(helpBody),
        dockGroupAncestors: (() => {
            const out = [];
            let el = helpPane?.parentElement;
            while (el && el !== document.body) {
                out.push({
                    tag: el.tagName,
                    cls: el.className,
                    h: Math.round(el.getBoundingClientRect().height),
                });
                el = el.parentElement;
            }
            return out;
        })(),
    };
});
console.log(JSON.stringify(measurements, null, 2));

await browser.close();
