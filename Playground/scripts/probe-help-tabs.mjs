// Quick probe of the three Help tabs: Commands (existing), Language
// (FadeBook/Language.md), Playground (the page's own doc). Confirms each
// tab populates a TOC and renders a body.

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 200)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await page.evaluate(() => window.__fadeDockview?.getPanel?.('help')?.api?.setActive?.());
await new Promise(r => setTimeout(r, 600));

async function inspectTab(tab) {
    await page.click(`.help-tab[data-tab="${tab}"]`);
    await new Promise(r => setTimeout(r, 600));
    return await page.evaluate(() => {
        const active = document.querySelector('.help-tab.active')?.dataset?.tab;
        const tocItems = Array.from(document.querySelectorAll('#help-toc .help-toc-item'))
            .slice(0, 5)
            .map(el => el.textContent?.trim());
        const bodyText = document.getElementById('help-body')?.textContent?.replace(/\s+/g, ' ').slice(0, 140) ?? '';
        const toolbarHidden = document.querySelector('.help-toolbar')?.hidden ?? false;
        return { active, tocItems, bodyText, toolbarHidden };
    });
}

for (const tab of ['commands', 'language', 'playground', 'commands']) {
    const info = await inspectTab(tab);
    console.log(`tab=${tab}:`);
    console.log('  active:', info.active);
    console.log('  toolbarHidden:', info.toolbarHidden);
    console.log('  toc[0..4]:', info.tocItems);
    console.log('  body[:140]:', info.bodyText);
}

await browser.close();
