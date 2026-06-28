import { chromium } from 'playwright';
process.env.PLAYWRIGHT_BROWSERS_PATH ??= '/Users/chrishanna/Documents/Github/dby/Playground/node_modules/playwright/.local-browsers';
const b = await chromium.launch({ headless: true });
const p = await b.newPage({ viewport: { width: 800, height: 600 } });
p.on('console', msg => {
  const t = msg.type();
  if (t === 'error' || t === 'warning' || msg.text().includes('error') || msg.text().includes('Error')) {
    console.log(`[${t}]`, msg.text().slice(0, 400));
  }
});
p.on('pageerror', e => console.log('[PAGEERROR]', e.message.slice(0, 400)));
await p.goto('http://localhost:5298/', { waitUntil: 'domcontentloaded' });
await p.waitForTimeout(20000);
const html = await p.content();
const info = await p.evaluate(() => ({
  hasCanvas: !!document.getElementById('theCanvas'),
  status: document.getElementById('status')?.textContent,
  appHtmlLength: document.getElementById('app')?.innerHTML.length,
  appHtmlStart: document.getElementById('app')?.innerHTML.slice(0, 200),
}));
console.log('INFO:', JSON.stringify(info, null, 2));
await b.close();
