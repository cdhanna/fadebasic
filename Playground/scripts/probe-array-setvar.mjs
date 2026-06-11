// Probe for the "no variable for given id" array setVariable bug.
// Seeds a web project (faster boot than monogame, same C# code path
// for TrySetValue), runs to a breakpoint, expands an array, and tries
// to set an element. Reports either the C# throw or the fix landing.
//
// Usage: dev server must be running on :5311 first.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

// Headless Chrome with software-rendered WebGL — monogame's iframe
// needs a working GL context to boot, and headless's default WebGL
// either no-ops or fails on Linux/CI hardware.
const browser = await chromium.launch({
    headless: true,
    args: ['--use-gl=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'],
});
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });

const captured = [];
page.on('pageerror', e => {
    captured.push(`[PE] ${e.message.slice(0, 400)}`);
    console.log('[PE]', e.message.slice(0, 200));
});
page.on('console', m => {
    const t = m.text();
    // Pre-filter common monaco noise to keep the log readable.
    if (/Possible EventEmitter|deprecated|webgl|^GET /i.test(t)) return;
    if (/FIX-ELEM|FIX-SET/.test(t)) console.log(t);
    captured.push(`[${m.type()}] ${t.slice(0, 800)}`);
});

await page.goto('http://localhost:5311/', { waitUntil: 'domcontentloaded' });

// Seed a minimal web project + reload so the editor / runner come up.
await page.evaluate(async () => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('arrprobe', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'arrprobe', type: 'web',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const src = `dim n(3) as integer
n(0) = 7
n(1) = 8
n(2) = 9
print "ready"
do
loop
`;
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(src); await sw.close();
    localStorage.setItem('fade.activeProject', 'arrprobe');
});
await page.reload({ waitUntil: 'domcontentloaded' });

// Wait for the helpers (which mount once main.ts has booted).
await page.waitForFunction(() => !!window.__fadeRunnerHelpers?.debug, { timeout: 90_000 });
console.log('→ helpers ready');

// Drive the whole flow from page.evaluate so we get clean error
// catching without playwright timing in the middle of each step.
const result = await page.evaluate(async () => {
    const out = { steps: [] };
    const h = window.__fadeRunnerHelpers.debug;

    // Click Debug. startDebug pauses at the entry, syncs breakpoints,
    // then continues. The program is `do / loop` so it runs forever
    // and we can pause it whenever we want.
    const dbgBtn = document.getElementById('debug');
    out.steps.push(`debug btn disabled=${dbgBtn?.hasAttribute('disabled')}`);
    dbgBtn?.click();

    // Wait for the program to be running.
    await new Promise(r => setTimeout(r, 1500));

    // Send a pause request — VM stops mid-loop. The runtime doesn't
    // emit a BREAKPOINT event for REQUEST_PAUSE (only real bps do); the
    // Pause button updates the UI manually after the call. For our
    // probe purposes, just give the VM a beat to settle then call the
    // debug APIs — by then IsPaused is true on the C# side and scopes/
    // setVariable will work.
    await h.pause();
    out.steps.push('pause sent');
    await new Promise(r => setTimeout(r, 300));

    // Fetch scopes → locate n.
    const scopes = await h.scopes({ frameId: 0 });
    const allVars = (scopes?.scopes ?? []).flatMap(s => s.variables);
    const nVar = allVars.find(v => v.name === 'n');
    if (!nVar) return { ...out, error: 'n not found', allVars };
    out.steps.push(`n.id=${nVar.id}`);

    // Expand n → register element ids.
    const expansion = await h.expand({ variableId: nVar.id });
    const elements = expansion?.scopes?.[0]?.variables ?? [];
    out.elements = elements.map(e => ({ id: e.id, name: e.name, value: e.value }));
    const nMid = elements.find(e => e.name === '1');
    if (!nMid) return { ...out, error: 'n[1] not in expansion' };

    // Set n[1] = 42. THIS IS THE LINE THE USER'S BUG HITS.
    try {
        const r = await h.setVariable({ frameId: 0, variableId: nMid.id, rhs: '42' });
        out.setResult = r;
        out.steps.push('setVariable returned cleanly');
    } catch (e) {
        out.setError = String(e?.message ?? e).slice(0, 800);
        out.steps.push('setVariable THREW');
    }

    // Verify by re-fetching.
    const scopes2 = await h.scopes({ frameId: 0 });
    const nAfter = scopes2.scopes.flatMap(s => s.variables).find(v => v.name === 'n');
    const exp2 = nAfter ? await h.expand({ variableId: nAfter.id }) : null;
    out.afterElements = (exp2?.scopes?.[0]?.variables ?? []).map(e => ({ name: e.name, value: e.value }));
    return out;
});

console.log('\n── RESULT ──');
console.log(JSON.stringify(result, null, 2));
console.log('\n── CAPTURED ──');
for (const c of captured) console.log(c);

const noVarErr = /no variable for given id/i.test(result?.setError ?? '');
const nLanded = result?.afterElements?.find(e => e.name === '1')?.value === '42';
const nKept = result?.afterElements?.find(e => e.name === '0')?.value === '7';

console.log('\n── VERDICT ──');
console.log(`"no variable" error?  ${noVarErr ? 'YES (bug)' : 'no'}`);
console.log(`n[1] now 42?          ${nLanded ? 'yes' : 'NO'}`);
console.log(`n[0] kept 7?          ${nKept ? 'yes' : 'NO'}`);
console.log(noVarErr ? 'FAIL — bug reproduces' : (nLanded && nKept) ? 'PASS' : 'INCONCLUSIVE');

await browser.close();
process.exit(noVarErr ? 1 : 0);
