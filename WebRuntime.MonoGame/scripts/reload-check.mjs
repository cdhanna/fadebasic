// Verifies hot-reload by calling LoadProgram from JS with a new fbasic
// source after the initial boot. The test:
//   1. Wait for the canvas to render the initial boot-stub program.
//   2. Compile + load a new fbasic program via DotNet.invokeMethod('LoadProgram', src).
//   3. Capture status text — it should flip to "reloaded" if the call landed
//      and the existing _game was reused, "running" if Game1 was just
//      constructed for the first time.
//   4. Bonus: load an intentionally-broken source and confirm we get
//      "compile error" without nuking the game.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', '..', 'Playground', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5298/';

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });

const errors = [];
page.on('console', msg => {
    const t = msg.type();
    if (t === 'error') console.log('[console.error]', msg.text().slice(0, 300));
});
page.on('pageerror', e => { errors.push(e); console.log('[pageerror]', e.message.slice(0, 300)); });

console.log(`→ navigate ${URL}`);
await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForSelector('#theCanvas', { timeout: 30000 });
await page.waitForTimeout(2500);

// Find the Blazor circuit's component ID so we can call our JSInvokables.
// XnaFiddle's pattern uses window.theInstance — our index.html exposes it
// when initRenderJS fires. Use it directly here.
async function callLoadProgram(src) {
    return await page.evaluate(async (source) => {
        if (!window.theInstance) return { ok: false, err: 'window.theInstance missing — initRenderJS may not have fired yet' };
        try {
            const ok = await window.theInstance.invokeMethodAsync('LoadProgram', source);
            return { ok, status: document.getElementById('status')?.textContent };
        } catch (e) {
            return { ok: false, err: String(e) };
        }
    }, src);
}

console.log('\n=== Test 1: good source, expect "reloaded" ===');
const goodSrc = `print "hello from hot reload"
do
  sync
loop
`;
const r1 = await callLoadProgram(goodSrc);
console.log(JSON.stringify(r1));

console.log('\n=== Test 2: bad source (syntax error), expect "compile error" ===');
const badSrc = `THIS IS NOT VALID FADE BASIC ====`;
const r2 = await callLoadProgram(badSrc);
console.log(JSON.stringify(r2));

console.log('\n=== Test 3: another good source, recover ===');
const r3 = await callLoadProgram(`print "second reload"
do
  sync
loop
`);
console.log(JSON.stringify(r3));

const pass =
    r1.ok === true && r1.status === 'reloaded' &&
    r2.ok === false && r2.status === 'compile error' &&
    r3.ok === true && r3.status === 'reloaded';

await browser.close();

if (errors.length) {
    console.log('\nPAGE ERRORS:');
    for (const e of errors) console.log(e.message);
}

if (!pass) {
    console.error('\n✗ FAIL: one or more hot-reload assertions failed.');
    process.exit(1);
}
console.log('\n✓ PASS: hot reload + compile-error path both working.');
