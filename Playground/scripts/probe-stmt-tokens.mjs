// Loads the user's exact source and dumps the statementLines that the
// debug session knows about. The breakpoint resolver snaps clicks to
// the nearest statement token, so seeing which lines DON'T have tokens
// tells us exactly where snap-to-nearest will go wrong.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5320/';

// The user's source, line-numbered for reference.
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
    const dir = await ws.getDirectoryHandle('mgprobe2', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'mgprobe2', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(src); await sw.close();
    localStorage.setItem('fade.activeProject', 'mgprobe2');
}, SOURCE);
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

console.log('→ Run + Debug start to populate statementLines…');
await page.click('#run');
await page.waitForSelector('#theCanvas', { timeout: 60_000 });
await page.waitForTimeout(3_000);

await page.click('#debug', { force: true });
await page.waitForTimeout(2_500);

// DebugStart returns { ok, statementLines } as JSON.
const startResult = await page.evaluate(async () => {
    const json = await window.theInstance.invokeMethodAsync('DebugStart');
    return JSON.parse(json);
});

await browser.close();

if (!startResult.ok) {
    console.error('DebugStart failed:', startResult.error);
    process.exit(1);
}

const lines = (startResult.statementLines || []).map(n => n + 1).sort((a, b) => a - b);
const lineSet = new Set(lines);

// Show every source line with a marker indicating whether it has a token.
const srcLines = SOURCE.split('\n');
const lineCount = srcLines.length;
console.log('\nLine-by-line: ✓ = has statement token, · = no token\n');
for (let i = 0; i < lineCount; i++) {
    const oneBased = i + 1;
    const has = lineSet.has(oneBased);
    const tag = has ? '✓' : '·';
    const lineNum = String(oneBased).padStart(3);
    console.log(`${tag} ${lineNum}  ${srcLines[i]}`);
}

// Specifically highlight the 4 keyboard-input if-bodies the user
// referenced — show whether each body line has its own token.
const flagLines = [];
for (let i = 0; i < srcLines.length; i++) {
    if (/^\s*(if downkey|if upkey|if leftkey|if rightKey)\(/i.test(srcLines[i])) {
        flagLines.push({ kind: 'cond', line: i + 1, text: srcLines[i].trim() });
        flagLines.push({ kind: 'body', line: i + 2, text: srcLines[i + 1]?.trim() ?? '' });
    }
}
console.log('\nKeyboard-input if blocks:');
for (const f of flagLines) {
    const has = lineSet.has(f.line);
    console.log(`  line ${f.line}  ${has ? '✓' : '·'} (${f.kind})  ${f.text}`);
}
