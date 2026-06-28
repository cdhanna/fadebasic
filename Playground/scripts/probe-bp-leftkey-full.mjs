// Same as probe-bp-leftkey but uses the user's FULL source (with the second
// for-loop after the if-rightKey block, and the `_L1:` label). The simpler
// probe showed 0 false fires, so the trigger must live in this extra code.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5320/';

// User's exact source (lines 1-107 numbered).
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

    for n = 0 to boxLength - 1
        b = backgroundBoxes(n)

        b.pos.x += b.vel.x
        b.pos.y += b.vel.y

        if (b.pos.x < -100)
            b.pos.x = render width() + 100
        endif

        position sprite b.spriteId, b.pos.x, b.pos.y

        backgroundBoxes(n) = b
    next


    sync

    _L1:
loop

\` TODO: resizing the window should adjust the letter-boxing
\` TODO: auto complete is appearing on comment string
\` TODO: these types should live in another file, but they aren't working.
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
    const dir = await ws.getDirectoryHandle('mgbpfull', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'mgbpfull', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(src); await sw.close();
    localStorage.setItem('fade.activeProject', 'mgbpfull');
}, SOURCE);
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

await page.click('#run');
await page.waitForSelector('#theCanvas', { timeout: 60_000 });
await page.waitForTimeout(3_000);

await page.click('#debug', { force: true });
await page.waitForTimeout(2_500);

// Set bp ONLY on the body line — display line 51, 0-based 50 — which
// is the `x = x - speed` body of the if-leftkey block. We never press
// any key, so the bp should not fire. line=49 (the `if leftkey()` line)
// IS a legitimate every-frame bp; we don't set it here.
console.log('→ set breakpoint on line 50 only (body of if-leftkey)…');
await page.evaluate(async () => {
    const linesJson = JSON.stringify([
        { line: 50, column: 0 },
    ]);
    await window.theInstance.invokeMethodAsync('DebugSetBreakpoints', linesJson);
});
await page.evaluate(() => window.theInstance.invokeMethodAsync('DebugContinue'));

let hits = 0;
const byLine = new Map();
const start = Date.now();
let lastStatus = '';
while (Date.now() - start < 6_000) {
    const status = await page.evaluate(() =>
        document.getElementById('debug-status')?.textContent ?? '');
    if (/paused on breakpoint/i.test(status) && status !== lastStatus) {
        hits++;
        // Query stack to find what line we paused on.
        const framesJson = await page.evaluate(() =>
            window.theInstance.invokeMethodAsync('DebugStackFrames'));
        let line = '?';
        let col = '?';
        try {
            const frames = JSON.parse(framesJson);
            const top = Array.isArray(frames) ? frames[0] : frames?.frames?.[0];
            line = top?.lineNumber ?? top?.line ?? '?';
            col = top?.colNumber ?? top?.col ?? '?';
        } catch { /* ignore */ }
        const key = `${line}:${col}`;
        byLine.set(key, (byLine.get(key) ?? 0) + 1);
        if (hits <= 12) console.log(`  hit #${hits} on line=${line} col=${col} at ${Date.now() - start}ms`);
        await page.evaluate(() => window.theInstance.invokeMethodAsync('DebugContinue'));
    }
    lastStatus = status;
    await page.waitForTimeout(150);
}
console.log('\nHits by line:', JSON.stringify(Object.fromEntries(byLine)));

await browser.close();

console.log(`\nbreakpoint on if-leftkey body fired ${hits} times in 6s (no keypress simulated).`);
if (hits === 0) {
    console.log('✓ EXPECTED: no false fires.');
} else {
    console.error('✗ BUG REPRODUCED: breakpoint fires without leftkey press.');
    process.exit(1);
}
