// Audio asset end-to-end check.
//
// Mirrors test-monogame-assets.mjs but for SoundEffect XNBs. Synthesizes a
// minimal PCM SoundEffect XNB, writes it + a fbasic source that does the
// canonical "load sfx clip → sfx → play sfx" sequence, then asserts that no
// errors landed during the run.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5311/';
const BOOT_BUDGET_MS = 60_000;

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

const consoleErrors = [];
const consoleAll = [];
page.on('console', (msg) => {
    const t = msg.text();
    consoleAll.push(`[${msg.type()}] ${t}`);
    if (msg.type() === 'error') consoleErrors.push(t);
});
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e.message));

console.log(`→ navigate ${URL}`);
await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

// In-page SoundEffect XNB builder. PCM, mono, 8000 Hz, ~0.1s of a 440 Hz
// sine wave (800 samples * 2 bytes = 1600 bytes). Small enough to make the
// XNB lightweight; long enough that the runtime has something to actually
// hand off to the audio backend.
const buildSoundXnbSnippet = `
function build7BitInt(value) {
    const out = [];
    while (value >= 0x80) { out.push((value & 0x7F) | 0x80); value >>>= 7; }
    out.push(value);
    return out;
}
function build7BitPrefixedString(s) {
    const enc = new TextEncoder().encode(s);
    return [...build7BitInt(enc.length), ...enc];
}
function buildInt32LE(v) {
    return [v & 0xFF, (v >>> 8) & 0xFF, (v >>> 16) & 0xFF, (v >>> 24) & 0xFF];
}
function buildSoundXnb() {
    // KNI's ContentTypeReaderManager translates ", MonoGame.Framework" →
    // "Xna.Framework.Audio" (where SoundEffectReader lives). XNA-era
    // ", Microsoft.Xna.Framework" does NOT translate to the audio assembly
    // for built-in audio readers — see ResolveReaderType in
    // kni/Xna.Framework.Content/Content/ContentTypeReaderManager.cs.
    const reader = 'Microsoft.Xna.Framework.Content.SoundEffectReader, MonoGame.Framework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553';
    // Match the shape of a real MGCB-built XNB: stereo PCM, 24 kHz, 16-bit.
    // Matches the parameters of typical user uploads (e.g. bubble-pop FX).
    // 18-byte WAVEFORMATEX with cbSize=0.
    const SR = 24000;
    const CHANNELS = 2;
    const BITS = 16;
    const BLOCK_ALIGN = CHANNELS * (BITS / 8);
    const AVG_BPS = SR * BLOCK_ALIGN;
    const fmt = [
        1, 0,                                                       // formatTag=PCM
        CHANNELS & 0xFF, (CHANNELS >>> 8) & 0xFF,                   // channels
        SR & 0xFF, (SR >>> 8) & 0xFF, (SR >>> 16) & 0xFF, 0,        // samplesPerSec
        AVG_BPS & 0xFF, (AVG_BPS >>> 8) & 0xFF, (AVG_BPS >>> 16) & 0xFF, 0,
        BLOCK_ALIGN & 0xFF, (BLOCK_ALIGN >>> 8) & 0xFF,             // blockAlign
        BITS & 0xFF, (BITS >>> 8) & 0xFF,                           // bitsPerSample
        0, 0,                                                       // cbSize=0
    ];
    // ~2.7 seconds at 24 kHz stereo — matches the user-reported file shape
    // (256.5 KB PCM, 65664 frames per channel).
    const sampleCount = 65664;
    const pcm = [];
    for (let i = 0; i < sampleCount; i++) {
        const v = Math.round(0.3 * 32767 * Math.sin(2 * Math.PI * 440 * (i / SR)));
        for (let c = 0; c < CHANNELS; c++) {
            pcm.push(v & 0xFF, (v >>> 8) & 0xFF);
        }
    }
    const payload = [
        ...build7BitInt(1),
        ...build7BitPrefixedString(reader),
        ...buildInt32LE(0),
        ...build7BitInt(0),
        ...build7BitInt(1),
        ...buildInt32LE(fmt.length),
        ...fmt,
        ...buildInt32LE(pcm.length),
        ...pcm,
        ...buildInt32LE(0),   // loopStart
        ...buildInt32LE(0),   // loopLength
        ...buildInt32LE(100), // durationMs
    ];
    const fileSize = 10 + payload.length;
    return new Uint8Array([
        0x58, 0x4E, 0x42,  // 'XNB'
        0x64,              // 'd' DesktopGL
        5,                 // version
        0,                 // flags (uncompressed, Reach)
        ...buildInt32LE(fileSize),
        ...payload,
    ]);
}
`;

const setup = await page.evaluate(async (xnbSnippet) => {
    eval(xnbSnippet);
    const snd = buildSoundXnb();
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgaudio', { create: true });

    const writeText = async (name, text) => {
        const fh = await dir.getFileHandle(name, { create: true });
        const w = await fh.createWritable();
        await w.write(text);
        await w.close();
    };
    const writeBytes = async (name, bytes) => {
        const fh = await dir.getFileHandle(name, { create: true });
        const w = await fh.createWritable();
        await w.write(new Blob([bytes]));
        await w.close();
    };

    await writeText('fade.json', JSON.stringify({
        $schema: '/fade.schema.json',
        name: 'mgaudio',
        type: 'monogame',
        commandDlls: [],
        sources: ['main.fbasic'],
    }, null, 2) + '\n');
    // Standard audio chain: load clip → create instance → play → loop sync.
    await writeText('main.fbasic',
        'load sfx clip 1, "Beep"\n' +
        'sfx 1, 1\n' +
        'play sfx 1\n' +
        'do\n  sync\nloop\n');
    await writeBytes('Beep.xnb', snd);
    localStorage.setItem('fade.activeProject', 'mgaudio');
    return { xnbLen: snd.length };
}, buildSoundXnbSnippet);

console.log('→ setup:', setup);

await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await page.waitForFunction(() => {
    const el = document.getElementById('status');
    return el && /Ready/i.test(el.textContent || '');
}, { timeout: 30_000 });

console.log('→ clicking Run');
await page.click('#run');

try {
    await page.waitForSelector('#theCanvas', { timeout: BOOT_BUDGET_MS });
} catch (e) {
    console.log('FAIL: canvas never appeared. console errors:');
    for (const er of consoleErrors.slice(-12)) console.log(' ', er.slice(0, 400));
    await browser.close();
    process.exit(1);
}
console.log('→ canvas appeared, allowing 3s of render frames…');
await page.waitForTimeout(3000);

// Pass criteria: no tick errors, no page errors, no asset-load errors. We
// don't try to confirm audio actually played — headless WebAudio output is
// hard to observe — but we do confirm the whole load/sfx/play chain didn't
// throw or log an error path.
const failuresLog = consoleErrors.filter((e) =>
    /Game tick error|NullReferenceException|sfx load failed|texture load failed|ContentLoadException/i.test(e),
);
console.log('matching error log entries:', failuresLog.length);
for (const e of failuresLog) console.log('  ', e.slice(0, 240));

let failed = false;
if (failuresLog.length > 0) { console.log('FAIL: expected no audio-path errors'); failed = true; }
if (pageErrors.length > 0)  { console.log('FAIL: page errors during the run:', pageErrors); failed = true; }
if (!failed) console.log('PASS: load sfx + sfx + play sfx chain ran without errors');

if (failed) {
    console.log('--- recent console messages (last 30) ---');
    for (const m of consoleAll.slice(-30)) console.log(' ', m.slice(0, 400));
}

// Cleanup.
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    try { await ws.removeEntry('mgaudio', { recursive: true }); } catch {}
    localStorage.setItem('fade.activeProject', 'default');
});

await browser.close();
process.exit(failed ? 1 : 0);
