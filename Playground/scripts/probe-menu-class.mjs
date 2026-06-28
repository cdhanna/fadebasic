// Open the editor's context menu and dump EVERY positioned element on
// the page so we can ID which class the menu actually uses. Uses
// page.locator click which is more reliable than page.mouse.

import { chromium } from 'playwright';
import { writeFileSync } from 'node:fs';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise((r) => setTimeout(r, 1500));

// Click on real text in the editor to focus + place cursor, then right-click
// on the same word so a menu actually opens.
const word = await page.locator('.monaco-editor .view-line').nth(0).locator('span').first();
await word.click();
await new Promise((r) => setTimeout(r, 200));
await word.click({ button: 'right' });
await new Promise((r) => setTimeout(r, 1000));

// Dump every visible positioned element + parent of "Go to Definition" text.
const dump = await page.evaluate(() => {
    const out = { positionedClasses: [], menuFinds: [], shadowRoots: 0 };
    // Recursively walk shadow roots too, in case Monaco renders into one.
    function collect(root, arr) {
        const list = root.querySelectorAll('*');
        for (const el of list) {
            arr.push(el);
            if (el.shadowRoot) {
                out.shadowRoots++;
                collect(el.shadowRoot, arr);
            }
        }
    }
    const all = [];
    collect(document, all);
    // Locate the shadow host for the menu container.
    let host = null;
    for (const el of all) {
        if (el.shadowRoot) {
            for (const ch of el.shadowRoot.querySelectorAll('*')) {
                if (ch.classList?.contains('monaco-menu-container')) {
                    host = el;
                    break;
                }
            }
        }
        if (host) break;
    }
    if (host) {
        const r = host.getBoundingClientRect();
        out.menuHost = {
            tag: host.tagName,
            id: host.id || null,
            cls: (host.className && typeof host.className === 'string') ? host.className : '',
            parent: host.parentElement?.tagName + '#' + (host.parentElement?.id || '') + '.' + (host.parentElement?.className || '').slice(0, 60),
            rect: { w: Math.round(r.width), h: Math.round(r.height) },
        };
    }
    for (const el of all) {
        const cs = getComputedStyle(el);
        if (cs.position !== 'fixed' && cs.position !== 'absolute') continue;
        if (cs.display === 'none' || cs.visibility === 'hidden') continue;
        const cls = (el.className && typeof el.className === 'string') ? el.className : '';
        if (!cls) continue;
        if (/context|menu|popup|action/i.test(cls)) {
            const r = el.getBoundingClientRect();
            out.positionedClasses.push({
                tag: el.tagName,
                cls: cls.slice(0, 200),
                pos: cs.position,
                z: cs.zIndex,
                parent: el.parentElement?.tagName + '.' + (el.parentElement?.className || '').slice(0, 40),
                w: Math.round(r.width),
                h: Math.round(r.height),
            });
        }
    }
    // Search for any element whose text starts with "Go to"
    for (const el of all) {
        const t = (el.textContent || '').trim();
        if (t.startsWith('Go to ') && el.children.length <= 3) {
            out.menuFinds.push({
                tag: el.tagName,
                cls: (el.className && typeof el.className === 'string') ? el.className.slice(0, 100) : '',
                text: t.slice(0, 50),
            });
            if (out.menuFinds.length >= 4) break;
        }
    }
    return out;
});
console.log(JSON.stringify(dump, null, 2));

const png = await page.screenshot();
writeFileSync('/tmp/fade-menu-class.png', png);

await browser.close();
