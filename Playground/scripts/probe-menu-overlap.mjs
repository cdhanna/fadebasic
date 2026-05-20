// Open the editor context menu near the RIGHT edge of the editor so the
// menu has to overflow into the adjacent Help/Game tab group. With the
// transform-strip fix on .dv-render-overlay, the menu should now extend
// across panel boundaries instead of being clipped at the panel edge.

import { chromium } from 'playwright';
import { writeFileSync } from 'node:fs';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise((r) => setTimeout(r, 1500));

// Click + right-click near the right edge of a real text token.
// Last token in line 0 should be furthest right.
const lastSpan = page.locator('.monaco-editor .view-line').nth(0).locator('span').last();
await lastSpan.click();
await new Promise((r) => setTimeout(r, 200));
await lastSpan.click({ button: 'right' });
await new Promise((r) => setTimeout(r, 800));

// Locate the menu container (inside shadow DOM) and check its rendered
// bounding rect. If our fix worked, the rect's right edge should extend
// past the editor's right edge OR the rect should be fully visible.
const summary = await page.evaluate(() => {
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
    if (!menu) return { menuFound: false };
    const r = menu.getBoundingClientRect();
    const editor = document.querySelector('.monaco-editor .view-lines');
    const er = editor.getBoundingClientRect();
    return {
        menuFound: true,
        menuRect: { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) },
        editorRightEdge: Math.round(er.right),
        menuExtendsPastEditor: r.right > er.right + 5,
    };
});
console.log(JSON.stringify(summary, null, 2));

const png = await page.screenshot();
writeFileSync('/tmp/fade-menu-overlap.png', png);
await browser.close();
