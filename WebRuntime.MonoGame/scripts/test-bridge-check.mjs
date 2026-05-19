// Verifies the testing bridge: ListTests + RunTests JSInvokables on
// Pages/Index.razor.cs. The fbasic source declares a couple of tests
// (passing + failing) and we check the JSON the bridge returns.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', '..', 'Playground', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5298/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });

const consoleErrors = [];
page.on('console', msg => {
    if (msg.type() === 'error') consoleErrors.push(msg.text().slice(0, 300));
});
page.on('pageerror', e => consoleErrors.push('[pageerror] ' + e.message.slice(0, 300)));

console.log(`→ navigate ${URL}`);
await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForSelector('#theCanvas', { timeout: 30000 });
await page.waitForTimeout(2500);

const testSrc = `test foo
    assert 1 + 1 = 2
endtest

test bar_will_fail
    assert 1 = 2
endtest

do
  sync
loop
`;

async function rpc(method, ...args) {
    return await page.evaluate(async ({ method, args }) => {
        if (!window.theInstance) return { _err: 'no instance' };
        try {
            return await window.theInstance.invokeMethodAsync(method, ...args);
        } catch (e) {
            return { _err: String(e) };
        }
    }, { method, args });
}

console.log('\n=== ListTests ===');
const listed = JSON.parse(await rpc('ListTests', testSrc));
console.log(JSON.stringify(listed, null, 2).slice(0, 600));

console.log('\n=== RunTests (all) ===');
const all = JSON.parse(await rpc('RunTests', testSrc, ''));
console.log(JSON.stringify(all, null, 2));

console.log('\n=== RunTests (named: foo) ===');
const one = JSON.parse(await rpc('RunTests', testSrc, 'foo'));
console.log(JSON.stringify(one, null, 2));

await browser.close();

if (consoleErrors.length) {
    console.log('\nCONSOLE ERRORS:');
    for (const e of consoleErrors) console.log(' ', e);
}

const ok =
    Array.isArray(listed) && listed.length === 2 &&
    listed.some(t => t.name === 'foo') &&
    listed.some(t => t.name === 'bar_will_fail') &&
    all.passed === 1 && all.failed === 1 &&
    one.passed === 1 && one.failed === 0;

if (!ok) {
    console.error('\n✗ FAIL: testing bridge assertions failed.');
    process.exit(1);
}
console.log('\n✓ PASS: ListTests + RunTests (all and named) both work.');
