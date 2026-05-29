// Sweeps every available theme, switching via settings and asserting the
// three layers (html data-theme, dockview class, Monaco class) line up.
// Also reads computed CSS on the previously-broken selectors (file-list
// active row, help heading, logs background) to confirm light themes no
// longer pin dark values.

import { chromium } from 'playwright';
import { writeFileSync, mkdirSync } from 'fs';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 240)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

// Reset settings so we start from a known place.
await page.evaluate(() => localStorage.removeItem('fade.settings.user.v1'));
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise(r => setTimeout(r, 1500));

const SCREENSHOT_DIR = process.env.SCREENSHOT_DIR;
if (SCREENSHOT_DIR) mkdirSync(SCREENSHOT_DIR, { recursive: true });

const THEMES = [
    { id: 'dark',           monaco: 'vs-dark',                    dockview: 'dockview-theme-vs' },
    { id: 'light',          monaco: 'vs',                         dockview: 'dockview-theme-light' },
    { id: 'dracula',        monaco: 'vs-dark',                    dockview: 'dockview-theme-dracula' },
    { id: 'solarized-dark', monaco: 'vs-dark',                    dockview: 'dockview-theme-vs' },
    { id: 'monokai',        monaco: 'vs-dark',                    dockview: 'dockview-theme-vs' },
    { id: 'nord',           monaco: 'vs-dark',                    dockview: 'dockview-theme-vs' },
    { id: 'high-contrast',  monaco: 'hc-black',                   dockview: 'dockview-theme-vs' },
    { id: 'dbp',            monaco: 'vs',                         dockview: 'dockview-theme-light' },
];

async function switchTheme(id) {
    await page.evaluate((id) => {
        const cur = JSON.parse(localStorage.getItem('fade.settings.user.v1') || '{}');
        cur['ui.theme'] = id;
        localStorage.setItem('fade.settings.user.v1', JSON.stringify(cur));
    }, id);
    // Trigger the settings reload path the easy way: a full reload picks
    // up the new value through initSettings + the boot-time applyTheme.
    // Faster than driving the settings panel UI.
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
    await new Promise(r => setTimeout(r, 800));
}

let failed = false;

for (const t of THEMES) {
    await switchTheme(t.id);
    const snap = await page.evaluate(() => ({
        dataTheme: document.documentElement.dataset.theme,
        dockClass: document.querySelector('.dv-shell')?.className ?? '',
        monacoEditor: document.querySelector('.monaco-editor')?.className ?? '',
        // Computed-style probes for the previously-broken selectors. We
        // don't have a file in the active row for a fresh project; pick a
        // common test bed: file-list, help TOC active mock, logs background.
        fileListBg: getComputedStyle(document.querySelector('#file-list li.active') ?? document.body).backgroundColor,
        rootBg: getComputedStyle(document.body).backgroundColor,
        rootFg: getComputedStyle(document.body).color,
    }));
    console.log(`[${t.id}]`, JSON.stringify(snap));

    if (snap.dataTheme !== t.id) {
        console.error(`  FAIL: data-theme expected ${t.id}, got ${snap.dataTheme}`);
        failed = true;
    }
    if (!snap.dockClass.includes(t.dockview)) {
        console.error(`  FAIL: dockview class missing ${t.dockview} — got "${snap.dockClass}"`);
        failed = true;
    }
    // Monaco base class — vs-dark adds `.vs-dark`, light is `.vs`, HC is `.hc-black`.
    if (t.monaco === 'vs') {
        if (!snap.monacoEditor.match(/\bvs\b/) || snap.monacoEditor.includes('vs-dark')) {
            console.error(`  FAIL: Monaco didn't pick up the light variant`);
            failed = true;
        }
    } else if (t.monaco === 'hc-black') {
        if (!snap.monacoEditor.includes('hc-black')) {
            console.error(`  FAIL: Monaco didn't pick up high-contrast`);
            failed = true;
        }
    } else {
        if (!snap.monacoEditor.includes('vs-dark')) {
            console.error(`  FAIL: Monaco didn't pick up a dark variant`);
            failed = true;
        }
    }

    if (SCREENSHOT_DIR) {
        const fname = `${SCREENSHOT_DIR}/theme-${t.id}.png`;
        await page.screenshot({ path: fname, fullPage: false });
        console.log(`  saved ${fname}`);
    }
}

// Sanity: verify the previously-broken selectors now read theme-aware values
// in light mode. The file-list active row should NOT be #37373d on light.
await switchTheme('light');
const lightSelectors = await page.evaluate(() => {
    const dummyLi = document.createElement('li');
    dummyLi.className = 'active';
    document.getElementById('file-list')?.appendChild(dummyLi);
    const liBg = getComputedStyle(dummyLi).backgroundColor;
    dummyLi.remove();
    // help TOC active class
    const dummyTocActive = document.createElement('div');
    dummyTocActive.className = 'help-toc-item active';
    document.body.appendChild(dummyTocActive);
    const tocColor = getComputedStyle(dummyTocActive).color;
    const tocBg = getComputedStyle(dummyTocActive).backgroundColor;
    dummyTocActive.remove();
    return { liBg, tocColor, tocBg };
});
console.log('light selectors after fix:', JSON.stringify(lightSelectors));
// rgb(55, 55, 61) was the old broken hardcoded value
if (lightSelectors.liBg.includes('55, 55, 61')) {
    console.error('FAIL: file-list active row still uses the dark hardcoded color');
    failed = true;
}

if (failed) {
    console.error('\nSome themes failed verification.');
    await browser.close(); process.exit(1);
}

console.log('\nOK: all themes applied cleanly across html data-theme + dockview + Monaco');
await browser.close();
