// Right-click in the editor and dump the popup container's full DOM
// ancestor chain (positions, z-indices, transforms) so we can pinpoint
// what CSS rule needs to apply to keep it above other dockview tabs.

import { chromium } from 'playwright';
import { writeFileSync } from 'node:fs';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise((r) => setTimeout(r, 1500));

// Click near the BOTTOM of the editor so the context menu opens upward
// — but its drop-shadow / bottom edge ought to overlap the Output panel's
// tab strip below. That's the case where z-index matters most.
const editorPos = await page.evaluate(() => {
    const lines = document.querySelector('.monaco-editor .view-lines');
    if (!lines) return null;
    const r = lines.getBoundingClientRect();
    return { x: r.left + 80, y: r.bottom - 30 };
});
await page.mouse.click(editorPos.x, editorPos.y);
await new Promise((r) => setTimeout(r, 150));
await page.mouse.click(editorPos.x, editorPos.y, { button: 'right' });
// Wait until ANY new element with class containing "context-view" appears
// (or 3s timeout, in which case the probe will report what's actually there).
await page.waitForFunction(() => {
    return Array.from(document.querySelectorAll('*')).some((el) => {
        const c = el.className;
        if (typeof c !== 'string') return false;
        return c.includes('context-view') || c.includes('monaco-menu');
    });
}, { timeout: 3000 }).catch(() => {});

// Dump the full HTML to disk so we can grep.
const html = await page.content();
import('node:fs').then(({ writeFileSync }) => writeFileSync('/tmp/fade-monaco-popup.html', html));

const info = await page.evaluate(() => {
    // Dump all direct children of document.body so we can see where Monaco
    // puts the context menu.
    const bodyChildren = Array.from(document.body.children).map((c) => {
        const cs = getComputedStyle(c);
        return {
            tag: c.tagName,
            cls: (c.className && typeof c.className === 'string') ? c.className.slice(0, 200) : '',
            id: c.id || null,
            position: cs.position,
            zIndex: cs.zIndex,
            display: cs.display,
            visibility: cs.visibility,
            rect: (() => { const r = c.getBoundingClientRect(); return r.width === 0 && r.height === 0 ? null : { w: Math.round(r.width), h: Math.round(r.height) }; })(),
            textPreview: (c.textContent || '').replace(/\s+/g, ' ').slice(0, 100),
        };
    });
    // Target the menu by its known vscode class names directly.
    const matches = [];
    const selectors = ['.context-view', '.context-view.fixed', '.monaco-menu', '.monaco-menu-container'];
    for (const sel of selectors) {
        const els = document.querySelectorAll(sel);
        for (const el of els) {
            const cs = getComputedStyle(el);
            const r = el.getBoundingClientRect();
            matches.push({
                selector: sel,
                tag: el.tagName,
                cls: (el.className && typeof el.className === 'string') ? el.className.slice(0, 200) : '',
                pos: cs.position,
                z: cs.zIndex,
                rect: r.width === 0 && r.height === 0 ? null : { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) },
                parentChain: (() => {
                    const chain = [];
                    let p = el.parentElement;
                    let d = 0;
                    while (p && d < 6) {
                        const pcs = getComputedStyle(p);
                        chain.push({
                            tag: p.tagName,
                            id: p.id || null,
                            cls: (p.className && typeof p.className === 'string') ? p.className.slice(0, 60) : '',
                            z: pcs.zIndex,
                            pos: pcs.position,
                        });
                        p = p.parentElement;
                        d++;
                    }
                    return chain;
                })(),
            });
        }
    }
    // Brute force: enumerate every element whose computed position is fixed/absolute
    // and rect is non-empty, regardless of class name. The menu has to be one of these.
    const positioned = [];
    const walker = document.createTreeWalker(document.documentElement, NodeFilter.SHOW_ELEMENT);
    let node;
    while ((node = walker.nextNode())) {
        const cs = getComputedStyle(node);
        if (cs.position !== 'fixed' && cs.position !== 'absolute') continue;
        if (cs.display === 'none' || cs.visibility === 'hidden') continue;
        const r = node.getBoundingClientRect();
        if (r.width < 100 || r.height < 50) continue;
        const cls = (node.className && typeof node.className === 'string') ? node.className : '';
        positioned.push({
            tag: node.tagName,
            cls: cls.slice(0, 200),
            id: node.id || null,
            pos: cs.position,
            z: cs.zIndex,
            rect: { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) },
        });
    }
    return { bodyChildren, matches, positioned: positioned.slice(0, 20) };
});
console.log(JSON.stringify(info, null, 2));

const png = await page.screenshot();
writeFileSync('/tmp/fade-monaco-popup.png', png);
await browser.close();
