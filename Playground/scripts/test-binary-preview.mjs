// Smoke test for the binary-file preview pane. Synthesizes a minimal valid
// XNB (2×2 Texture2D, Color surface format, single mip) and a minimal valid
// SoundEffect XNB (PCM, 8 samples), writes them into OPFS, then clicks the
// rows in the file list and asserts the preview pane renders the right
// payload (canvas with correct dims for the texture; an <audio> element +
// PCM duration for the sound).
//
// Usage: node scripts/test-binary-preview.mjs [http://localhost:5311/]

import { chromium } from 'playwright';

const url = process.argv[2] || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ viewport: { width: 1500, height: 950 } });
const page = await ctx.newPage();

const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e.message));
const consoleErrors = [];
page.on('console', (msg) => {
    if (msg.type() === 'error') consoleErrors.push(msg.text());
});

await page.goto(url, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
await new Promise((r) => setTimeout(r, 800));

// Page-side XNB builder. Pure JS — runs inside `page.evaluate`. The output
// matches what MonoGame's XnbWriter emits for an uncompressed Texture2D /
// SoundEffect, which is exactly what the reader expects.
const buildTextureXnb = () => `
function build7BitInt(value) {
    const out = [];
    while (value >= 0x80) {
        out.push((value & 0x7F) | 0x80);
        value >>>= 7;
    }
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
function buildTextureXnb() {
    const reader = 'Microsoft.Xna.Framework.Content.Texture2DReader, Microsoft.Xna.Framework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553';
    const W = 2, H = 2;
    const pixels = [
        255, 0, 0, 255,    // red
        0, 255, 0, 255,    // green
        0, 0, 255, 255,    // blue
        255, 255, 255, 255 // white
    ];
    const payload = [
        ...build7BitInt(1),
        ...build7BitPrefixedString(reader),
        ...buildInt32LE(0),
        ...build7BitInt(0),
        ...build7BitInt(1),
        ...buildInt32LE(0), // SurfaceFormat.Color
        ...buildInt32LE(W),
        ...buildInt32LE(H),
        ...buildInt32LE(1), // mip count
        ...buildInt32LE(pixels.length),
        ...pixels,
    ];
    const fileSize = 10 + payload.length;
    return new Uint8Array([
        0x58, 0x4E, 0x42, // 'XNB'
        0x64,             // 'd' DesktopGL
        5,                // format version
        0,                // flags (uncompressed)
        ...buildInt32LE(fileSize),
        ...payload,
    ]);
}
`;

const buildSoundXnb = () => `
function buildSoundXnb() {
    const reader = 'Microsoft.Xna.Framework.Content.SoundEffectReader, Microsoft.Xna.Framework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553';
    // 8000 Hz, mono, 16-bit PCM, 8 samples (16 bytes) of silence.
    const fmt = new Uint8Array([
        1, 0,                  // formatTag PCM
        1, 0,                  // channels
        0x40, 0x1F, 0, 0,      // 8000 Hz
        0x80, 0x3E, 0, 0,      // 16000 bytes/sec
        2, 0,                  // blockAlign
        16, 0,                 // bitsPerSample
    ]);
    const data = new Uint8Array(16); // 8 silent samples
    const payload = [
        ...build7BitInt(1),
        ...build7BitPrefixedString(reader),
        ...buildInt32LE(0),
        ...build7BitInt(0),
        ...build7BitInt(1),
        ...buildInt32LE(fmt.length),
        ...fmt,
        ...buildInt32LE(data.length),
        ...data,
        ...buildInt32LE(0),   // loopStart
        ...buildInt32LE(0),   // loopLength
        ...buildInt32LE(1),   // durationMs
    ];
    const fileSize = 10 + payload.length;
    return new Uint8Array([
        0x58, 0x4E, 0x42,
        0x64,
        5,
        0,
        ...buildInt32LE(fileSize),
        ...payload,
    ]);
}
`;

// Write both XNBs into OPFS under the active project's folder so the file
// list picks them up on next render. The page exposes its workspace by
// listing files via `__fadeRunnerHelpers.project.…`; the simplest path
// here is to use the FileSystem API directly.
const written = await page.evaluate(async (helpers) => {
    eval(helpers.buildTextureXnb);
    eval(helpers.buildSoundXnb);
    const tex = buildTextureXnb();
    const snd = buildSoundXnb();
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    // Find the active project. Convention from main.ts: localStorage[fade.activeProject].
    const projectName = localStorage.getItem('fade.activeProject') || 'default';
    const proj = await ws.getDirectoryHandle(projectName, { create: true });
    const write = async (name, bytes) => {
        const fh = await proj.getFileHandle(name, { create: true });
        const w = await fh.createWritable();
        await w.write(new Blob([bytes]));
        await w.close();
    };
    await write('Probe.xnb', tex);
    await write('ProbeSound.xnb', snd);
    return { texLen: tex.length, sndLen: snd.length, projectName };
}, { buildTextureXnb: buildTextureXnb(), buildSoundXnb: buildSoundXnb() });

console.log('written:', written);

// Reload so renderFileList() picks up the new files.
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
await new Promise((r) => setTimeout(r, 800));

const tests = [];
function test(name, fn) { tests.push({ name, fn }); }

test('XNB files appear in the workspace file list', async () => {
    const present = await page.evaluate(() => {
        return Array.from(document.querySelectorAll('#file-list li'))
            .map((li) => li.dataset.name || (li.querySelector('span')?.textContent || ''));
    });
    if (!present.includes('Probe.xnb')) throw new Error('Probe.xnb missing from file list: ' + present.join(','));
    if (!present.includes('ProbeSound.xnb')) throw new Error('ProbeSound.xnb missing: ' + present.join(','));
    return { listing: present };
});

test('Clicking the Texture2D XNB opens the binary preview with a canvas', async () => {
    await page.locator('#file-list li[data-name="Probe.xnb"]').click();
    await page.waitForSelector('.binary-preview-host[data-filename="Probe.xnb"]', { timeout: 5000 });
    await new Promise((r) => setTimeout(r, 400));
    const dims = await page.evaluate(() => {
        const root = document.querySelector('.binary-preview-host[data-filename="Probe.xnb"]');
        const canvas = root?.querySelector('canvas.binary-preview-canvas');
        return canvas ? { w: canvas.width, h: canvas.height } : null;
    });
    if (!dims) throw new Error('expected a canvas inside the binary-preview pane');
    if (dims.w !== 2 || dims.h !== 2) throw new Error(`canvas dims should be 2×2, got ${JSON.stringify(dims)}`);
    // Verify a metadata row says "Texture2D" — the kind classification.
    const kindLabel = await page.evaluate(() => {
        const rows = Array.from(document.querySelectorAll('.binary-preview-host[data-filename="Probe.xnb"] .binary-preview-row'));
        for (const r of rows) {
            const k = r.querySelector('.binary-preview-key')?.textContent?.trim();
            if (k === 'Kind') return r.querySelector('.binary-preview-value')?.textContent?.trim();
        }
        return null;
    });
    if (kindLabel !== 'Texture2D') throw new Error('expected Kind=Texture2D, got ' + kindLabel);
    return dims;
});

test('HiDef profile bit (0x01) does not get confused with compression flags', async () => {
    // Build a Texture2D XNB whose flags byte is 0x01 (HiDef + uncompressed),
    // matching what real MonoGame content like catfish.xnb sets. Earlier we
    // had bit 7 mapped to HiDef and bit 0 to LZX, which made this case look
    // LZX-compressed and short-circuited the rest of the parse.
    const written = await page.evaluate(async (helpers) => {
        eval(helpers.buildTextureXnb);
        const tex = buildTextureXnb();
        tex[5] = 0x01; // flip flags byte to HiDef
        const root = await navigator.storage.getDirectory();
        const ws = await root.getDirectoryHandle('workspace', { create: true });
        const projectName = localStorage.getItem('fade.activeProject') || 'default';
        const proj = await ws.getDirectoryHandle(projectName, { create: true });
        const fh = await proj.getFileHandle('ProbeHiDef.xnb', { create: true });
        const w = await fh.createWritable();
        await w.write(new Blob([tex]));
        await w.close();
        return tex.length;
    }, { buildTextureXnb: buildTextureXnb() });

    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
    await new Promise((r) => setTimeout(r, 500));

    await page.locator('#file-list li[data-name="ProbeHiDef.xnb"]').click();
    await page.waitForSelector('.binary-preview-host[data-filename="ProbeHiDef.xnb"]', { timeout: 5000 });
    await new Promise((r) => setTimeout(r, 300));

    const rows = await page.evaluate(() => {
        const root = document.querySelector('.binary-preview-host[data-filename="ProbeHiDef.xnb"]');
        return Array.from(root.querySelectorAll('.binary-preview-row')).map((r) => ({
            k: r.querySelector('.binary-preview-key')?.textContent?.trim(),
            v: r.querySelector('.binary-preview-value')?.textContent?.trim(),
        }));
    });
    const findVal = (k) => rows.find((r) => r.k === k)?.v;
    if (findVal('Profile') !== 'HiDef') throw new Error('expected Profile=HiDef, got ' + findVal('Profile'));
    if (findVal('Compression') !== 'None') throw new Error('expected Compression=None, got ' + findVal('Compression'));
    if (findVal('Kind') !== 'Texture2D') throw new Error('expected Kind=Texture2D, got ' + findVal('Kind'));
    return { profile: findVal('Profile'), compression: findVal('Compression'), bytesWritten: written };
});

test('Switching between two XNBs reuses one Asset Preview panel', async () => {
    // Click Probe.xnb (texture), then ProbeSound.xnb (audio), then back to
    // Probe.xnb. There should be exactly one panel with id "asset-preview"
    // the whole time, and its content should swap to match the latest
    // selection.
    await page.locator('#file-list li[data-name="Probe.xnb"]').click();
    await page.waitForFunction(
        () => {
            const root = document.querySelector('.binary-preview-host');
            return root && root.getAttribute('data-filename') === 'Probe.xnb';
        },
        { timeout: 5000 },
    );

    await page.locator('#file-list li[data-name="ProbeSound.xnb"]').click();
    await page.waitForFunction(
        () => {
            const root = document.querySelector('.binary-preview-host');
            return root && root.getAttribute('data-filename') === 'ProbeSound.xnb';
        },
        { timeout: 5000 },
    );

    await page.locator('#file-list li[data-name="Probe.xnb"]').click();
    await page.waitForFunction(
        () => {
            const root = document.querySelector('.binary-preview-host');
            return root && root.getAttribute('data-filename') === 'Probe.xnb';
        },
        { timeout: 5000 },
    );

    // There must be exactly one .binary-preview-host in the DOM — proves
    // we're not stacking up per-file panels.
    const hostCount = await page.locator('.binary-preview-host').count();
    if (hostCount !== 1) {
        throw new Error(`expected exactly one .binary-preview-host, got ${hostCount}`);
    }
    // And dockview should hold exactly one panel with the shared id.
    const panelCount = await page.evaluate(() => {
        const api = window.__fadeDockview;
        if (!api) return -1;
        return api.panels.filter((p) => p.id === 'asset-preview').length;
    });
    if (panelCount !== 1) {
        throw new Error(`expected one dockview panel with id 'asset-preview', got ${panelCount}`);
    }
    return { hostCount, panelCount };
});

test('Clicking the SoundEffect XNB opens the binary preview with audio + PCM metadata', async () => {
    await page.locator('#file-list li[data-name="ProbeSound.xnb"]').click();
    await page.waitForSelector('.binary-preview-host[data-filename="ProbeSound.xnb"]', { timeout: 5000 });
    await new Promise((r) => setTimeout(r, 400));
    const summary = await page.evaluate(() => {
        const root = document.querySelector('.binary-preview-host[data-filename="ProbeSound.xnb"]');
        if (!root) return null;
        const audio = root.querySelector('audio.binary-preview-audio');
        const rows = Array.from(root.querySelectorAll('.binary-preview-row')).map((r) => ({
            k: r.querySelector('.binary-preview-key')?.textContent?.trim(),
            v: r.querySelector('.binary-preview-value')?.textContent?.trim(),
        }));
        return { hasAudio: !!audio, rows };
    });
    if (!summary) throw new Error('preview host missing for ProbeSound.xnb');
    if (!summary.hasAudio) throw new Error('expected an <audio> element');
    const kind = summary.rows.find((r) => r.k === 'Kind')?.v;
    if (kind !== 'SoundEffect') throw new Error('expected Kind=SoundEffect, got ' + kind);
    const format = summary.rows.find((r) => r.k === 'Format')?.v;
    if (format !== 'PCM') throw new Error('expected Format=PCM, got ' + format);
    return summary;
});

// Run + report.
let passed = 0, failed = 0;
for (const { name, fn } of tests) {
    try {
        const detail = await fn();
        console.log('PASS', name, detail ? JSON.stringify(detail) : '');
        passed++;
    } catch (e) {
        console.log('FAIL', name, '\n  ', e.message);
        failed++;
    }
}

// Clean up the probe files so subsequent runs see a clean workspace.
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const projectName = localStorage.getItem('fade.activeProject') || 'default';
    const proj = await ws.getDirectoryHandle(projectName, { create: true });
    for (const name of ['Probe.xnb', 'ProbeSound.xnb', 'ProbeHiDef.xnb']) {
        try { await proj.removeEntry(name); } catch {}
    }
});

if (pageErrors.length) console.log('PAGE ERRORS:', pageErrors);
if (consoleErrors.length) console.log('CONSOLE ERRORS:', consoleErrors);

await browser.close();
process.exit(failed > 0 || pageErrors.length > 0 ? 1 : 0);
