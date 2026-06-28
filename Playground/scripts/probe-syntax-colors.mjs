// Sample the actual rendered color of the same token across themes.
// Tells us whether monaco.editor.setTheme really swaps syntax colors.

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 240)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise(r => setTimeout(r, 1800));

const THEMES = ['dark', 'light', 'dbp', 'dracula', 'monokai', 'nord'];
const out = {};

for (const id of THEMES) {
    await page.evaluate((id) => {
        const cur = JSON.parse(localStorage.getItem('fade.settings.user.v1') || '{}');
        cur['ui.theme'] = id;
        localStorage.setItem('fade.settings.user.v1', JSON.stringify(cur));
    }, id);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
    // Wait for LSP push (semantic tokens arrive a beat after open).
    await new Promise(r => setTimeout(r, 2500));

    const sample = await page.evaluate(() => {
        const lines = document.querySelectorAll('.view-line');
        if (!lines.length) return { error: 'no .view-line in DOM' };
        // Grab the first non-empty line's first few spans; report their text + computed color.
        const samples = [];
        for (const line of lines) {
            const spans = line.querySelectorAll('span > span');
            for (const s of spans) {
                const text = (s.textContent || '').trim();
                if (!text) continue;
                samples.push({ text, color: getComputedStyle(s).color, classes: s.className });
                if (samples.length >= 8) break;
            }
            if (samples.length >= 8) break;
        }
        return samples;
    });
    out[id] = sample;
    console.log(`[${id}]`, JSON.stringify(sample));
}

// Cross-check: do the colors for the 'print' token actually differ across themes?
const colorsOfPrint = {};
for (const id of THEMES) {
    const m = (out[id] || []).find(s => s.text === 'print');
    if (m) colorsOfPrint[id] = m.color;
}
console.log('\nprint-token color per theme:', colorsOfPrint);
const unique = new Set(Object.values(colorsOfPrint));
console.log('unique colors:', unique.size, '/', Object.keys(colorsOfPrint).length);
await browser.close();
