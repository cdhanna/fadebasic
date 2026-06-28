// Quick visual-check probe for the editor's right-click menu. Opens a
// playground tab, right-clicks the editor at three positions that put
// the menu near a panel boundary, screenshots each one, and dumps the
// menu's computed position + every ancestor's transform / overflow /
// stacking properties so we can see exactly what's clipping it.
//
// Output:
//   /tmp/fade-menu-left.png   menu opens in editor middle (control)
//   /tmp/fade-menu-right.png  menu opens near editor's right edge
//   /tmp/fade-menu-bottom.png menu opens near editor's bottom edge
//   stdout: rect + ancestor-chain JSON for each case
//
// Usage: node scripts/probe-menu-visual.mjs

import { chromium } from 'playwright';
import { writeFileSync } from 'node:fs';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise((r) => setTimeout(r, 1500));

// Quick check: do my CSS overrides actually load?
const sanityCheck = await page.evaluate(() => {
    const ro = document.querySelector('.dv-render-overlay');
    if (!ro) return { error: 'no .dv-render-overlay in DOM' };
    const cs = getComputedStyle(ro);
    return {
        transform: cs.transform,
        contain: cs.contain,
        isolation: cs.isolation,
        backfaceVisibility: cs.backfaceVisibility,
    };
});
console.log('--- CSS sanity check ---');
console.log(JSON.stringify(sanityCheck, null, 2));

async function dumpMenu(label) {
    const info = await page.evaluate(() => {
        function findInShadows(root) {
            for (const el of root.querySelectorAll('*')) {
                if (el.classList?.contains('monaco-menu-container')) return el;
                if (el.shadowRoot) {
                    const hit = findInShadows(el.shadowRoot);
                    if (hit) return hit;
                }
            }
            return null;
        }
        const menu = findInShadows(document);
        if (!menu) return { found: false };
        const cs = getComputedStyle(menu);
        const r = menu.getBoundingClientRect();
        // Walk up through normal DOM ancestors AND across shadow boundaries.
        const chain = [];
        let p = menu;
        while (p && chain.length < 25) {
            const pcs = (p.nodeType === 1) ? getComputedStyle(p) : null;
            chain.push({
                tag: p.tagName ?? '#' + p.nodeName,
                cls: (typeof p.className === 'string' ? p.className : '').slice(0, 80),
                id: p.id || null,
                transform: pcs ? (pcs.transform === 'none' ? '' : pcs.transform.slice(0, 40)) : null,
                overflow: pcs ? (pcs.overflow + '/' + pcs.overflowX + '/' + pcs.overflowY) : null,
                position: pcs?.position ?? null,
                zIndex: pcs?.zIndex ?? null,
                filter: pcs ? (pcs.filter === 'none' ? '' : pcs.filter.slice(0, 20)) : null,
            });
            p = p.parentNode;
            if (p && p instanceof ShadowRoot) {
                chain.push({ tag: '#shadow-root', host: p.host?.tagName + '.' + (p.host?.className || '').slice(0, 40) });
                p = p.host;
            }
        }
        return {
            found: true,
            position: cs.position,
            zIndex: cs.zIndex,
            rect: { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) },
            chain,
        };
    });
    console.log('=== ' + label + ' ===');
    console.log(JSON.stringify(info, null, 2));
}

async function openMenuAt(span, label) {
    // Dismiss any prior menu by clicking the body.
    await page.mouse.click(50, 50);
    await new Promise((r) => setTimeout(r, 200));
    await span.click();
    await new Promise((r) => setTimeout(r, 150));
    await span.click({ button: 'right' });
    await new Promise((r) => setTimeout(r, 700));
    await dumpMenu(label);
    const png = await page.screenshot();
    writeFileSync(`/tmp/fade-menu-${label}.png`, png);
}

// Three positions: leftmost token (control), rightmost token in line 1 (overlaps Help/Game),
// rightmost token in the last visible line (overlaps Output panel below).
await openMenuAt(
    page.locator('.monaco-editor .view-line').nth(0).locator('span').first(),
    'left',
);
await openMenuAt(
    page.locator('.monaco-editor .view-line').nth(0).locator('span').last(),
    'right',
);
await openMenuAt(
    page.locator('.monaco-editor .view-line').nth(6).locator('span').last(),
    'bottom',
);

await browser.close();
