import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const b = await chromium.launch({ headless: true });
const p = await b.newPage({ viewport: { width: 1280, height: 800 } });

p.on('pageerror', e => console.log('[PE]', e.message.slice(0, 400)));
p.on('console', m => {
    const t = m.type();
    if (t === 'error' || /error|fail|exception/i.test(m.text())) {
        console.log(`[${t}]`, m.text().slice(0, 400));
    }
});

await p.goto('http://localhost:5316/', { waitUntil: 'domcontentloaded' });
await p.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });
console.log('→ Playground Ready');

// Seed monogame project + reload
await p.evaluate(async () => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgprobe', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'mgprobe', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write('print "hi"\ndo\n  sync\nloop\n'); await sw.close();
    localStorage.setItem('fade.activeProject', 'mgprobe');
});
await p.reload({ waitUntil: 'domcontentloaded' });
await p.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

await (await p.$('#run')).click();
console.log('→ Run clicked, waiting 10s and dumping state…');
await p.waitForTimeout(10_000);

const info = await p.evaluate(() => {
    const c = document.getElementById('theCanvas');
    return {
        hasCanvas: !!c,
        canvasSize: c ? { w: c.width, h: c.height } : null,
        outputText: (document.getElementById('output')?.textContent || '').slice(0, 500),
        statusText: document.getElementById('status')?.textContent || '',
        bodyText: document.body.innerText.slice(0, 600),
    };
});
console.log('INFO:', JSON.stringify(info, null, 2));

await b.close();
