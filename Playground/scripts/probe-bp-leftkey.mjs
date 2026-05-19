// Reproduces the user's false-fire breakpoint scenario. Loads the full
// source, sets a breakpoint on line 51 (`x = x - speed` body of if-leftkey,
// 0-based = 50), runs for ~6 seconds without simulating any keypress, and
// counts how many times the breakpoint hits. A correct debugger should
// hit 0 times since leftkey is never pressed.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5320/';

const SOURCE = `set render size 1920, 1080

set background color rgb(75, 44, 44)
sprite 1, 100, 200, 0
color sprite 1, rgb(255, 0, 0)
size sprite 1, 200, 200
order sprite 1, 1
x = 180
y = 100
speed = 8

boxLength = 50
DIM backgroundBoxes(boxLength) as box
for n = 0 to boxLength - 1
    b = backgroundBoxes(n)
    id = reserve sprite id(b.spriteId)
    b.pos.x = rnd(render width())
    b.pos.y = rnd(render height())
    b.size.x = 10 + rnd(40)
    if b.size.x > 30
        b.size.y -= 5
    endif
    b.vel.x = -1 * b.size.x * .01 * b.size.x
    b.size.y = 10
    sprite id, b.pos.x, b.pos.y, 0
    color sprite id, rgb(128 + rnd(64), 64 + rnd(32), 128)
    size sprite id, b.size.x, b.size.y

    backgroundBoxes(n) = b
next


width = render width() - 100
height = render height() - 100

do
    sprite 1, x, y, 0

    if x > width then x = width
    if y > height then y = height
    if x < 100 then x = 100
    if y < 100 then y = 100

    if downkey()
        y = y + speed
    endif
    if upkey()
        y = y - speed
    endif
    if leftkey()
        x = x - speed
    endif
    if rightKey()
        x = x + speed
    endif

    sync
loop

type box
    spriteId
    pos as vec
    vel as vec
    size as vec
endtype

type vec
    x
    y
endtype
`;

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', e => console.log('[PE]', e.message.slice(0, 300)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

await page.evaluate(async (src) => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgbp51', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'mgbp51', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(src); await sw.close();
    localStorage.setItem('fade.activeProject', 'mgbp51');
}, SOURCE);
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

await page.click('#run');
await page.waitForSelector('#theCanvas', { timeout: 60_000 });
await page.waitForTimeout(3_000);

await page.click('#debug', { force: true });
await page.waitForTimeout(2_500);

// Set bp on line 51 (0-based 50), then continue.
console.log('→ set breakpoint on line 51 (if-leftkey body)…');
await page.evaluate(async () => {
    const linesJson = JSON.stringify([{ line: 50, column: 0 }]);
    await window.theInstance.invokeMethodAsync('DebugSetBreakpoints', linesJson);
});
await page.evaluate(() => window.theInstance.invokeMethodAsync('DebugContinue'));

// Watch the debug status text. If it shows "paused on breakpoint" — a hit.
// Continue past each hit, count them.
let hits = 0;
const start = Date.now();
let lastStatus = '';
while (Date.now() - start < 6_000) {
    const status = await page.evaluate(() =>
        document.getElementById('debug-status')?.textContent ?? '');
    if (/paused on breakpoint/i.test(status) && status !== lastStatus) {
        hits++;
        console.log(`  hit #${hits} at ${Date.now() - start}ms`);
        // Continue to look for the next one.
        await page.evaluate(() => window.theInstance.invokeMethodAsync('DebugContinue'));
    }
    lastStatus = status;
    await page.waitForTimeout(150);
}

await browser.close();

console.log(`\nbreakpoint on if-leftkey body fired ${hits} times in 6s (no keypress simulated).`);
if (hits === 0) {
    console.log('✓ EXPECTED: no false fires.');
} else {
    console.error('✗ BUG REPRODUCED: breakpoint fires without leftkey press.');
    process.exit(1);
}
