// End-to-end smoke for the test-runner + debugger on monogame projects.
//
// Creates a monogame project with three tests (one pass, one fail, one
// abstract), then:
//   1. Asserts listTests via monoGameHost returns all three with the
//      right metadata.
//   2. Runs all tests + verifies the run envelope counts + per-test
//      results match expectations.
//   3. Starts a debug session against the passing test, asserts
//      `{ok: true, statementLines: [...]}`, then terminates.
//
// Doesn't try to drive the UI buttons — talks directly to monoGameHost
// + the dbg dispatcher exposed via __fadeRunnerHelpers — so we test the
// canvas-side bridge without racing the dockview test panel's renders.

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const BOOT_BUDGET_MS = 60_000;

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

const consoleErrors = [];
const consoleAll = [];
page.on('console', (m) => {
    const t = m.text();
    consoleAll.push(`[${m.type()}] ${t}`);
    if (m.type() === 'error') consoleErrors.push(t);
});
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e.message));

console.log(`→ navigate ${URL}`);
await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

// Drop a monogame project with three tests into OPFS and reload.
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgtests', { create: true });
    const w = async (name, text) => {
        const fh = await dir.getFileHandle(name, { create: true });
        const sw = await fh.createWritable();
        await sw.write(text); await sw.close();
    };
    await w('fade.json', JSON.stringify({
        $schema: '/fade.schema.json',
        name: 'mgtests',
        type: 'monogame',
        commandDlls: [],
        sources: ['main.fbasic'],
    }, null, 2) + '\n');
    await w('main.fbasic',
        // Top-level program — does nothing (test mode swaps the VM
        // entry into one of the tests below).
        'do\n  sync\nloop\n' +
        '\n' +
        'test passes\n' +
        '    assert 1 = 1\n' +
        'endtest\n' +
        '\n' +
        'test fails\n' +
        '    assert 1 = 2\n' +
        'endtest\n' +
        '\n' +
        'abstract test base\n' +
        '    assert 0 = 0\n' +
        'endtest\n');
    localStorage.setItem('fade.activeProject', 'mgtests');
});

await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await page.waitForFunction(() => {
    const el = document.getElementById('status');
    return el && /Ready/i.test(el.textContent || '');
}, { timeout: 30_000 });

// Click Run once so the monogame runtime boots (lazy WASM load + Game1
// construction). Test debug needs _game to exist on the canvas side.
console.log('→ booting canvas runtime (click Run)…');
await page.click('#run');
try {
    await page.waitForSelector('#theCanvas', { timeout: BOOT_BUDGET_MS });
} catch {
    console.log('canvas never appeared');
    process.exit(1);
}
// Wait until the Blazor DotNetObjectReference is installed — `window.theInstance`
// gets assigned inside monoGameHost.onInitRenderJS, which fires after Blazor
// renders the Game panel. The canvas appearing doesn't guarantee theInstance
// is set yet (the rAF loop and the DotNet ref handoff race), so block on it
// before the smoke tests start poking through the bridge.
try {
    await page.waitForFunction(
        () => typeof window.theInstance?.invokeMethodAsync === 'function',
        { timeout: BOOT_BUDGET_MS },
    );
} catch {
    console.log('window.theInstance never appeared');
    process.exit(1);
}
await page.waitForTimeout(500);

const tests = [];
function test(name, fn) { tests.push({ name, fn }); }

test('listTests returns three entries with correct flags + names', async () => {
    const source = await page.evaluate(async () => {
        const root = await navigator.storage.getDirectory();
        const ws = await root.getDirectoryHandle('workspace');
        const dir = await ws.getDirectoryHandle('mgtests');
        const fh = await dir.getFileHandle('main.fbasic');
        return (await fh.getFile()).text();
    });
    const list = await page.evaluate(
        (src) => window.__fadeRunnerHelpers.listTests({ source: src }),
        source,
    );
    // We invoke via runner helpers which always hit the worker. For the
    // monogame-aware path we'd go through monoGameHost — exercise that
    // directly through window.theInstance.
    if (!Array.isArray(list)) throw new Error('worker listTests did not return array');

    const mg = await page.evaluate(async (src) => {
        await window.theInstance.invokeMethodAsync('LoadProgram', 'do\n  sync\nloop\n'); // make sure game is alive
        const json = await window.theInstance.invokeMethodAsync('ListTests', src);
        return JSON.parse(json);
    }, source);
    if (!Array.isArray(mg)) throw new Error('monoGame ListTests did not return array');
    const byName = new Map(mg.map((t) => [t.name, t]));
    if (!byName.has('passes')) throw new Error('missing test "passes"');
    if (!byName.has('fails')) throw new Error('missing test "fails"');
    if (!byName.has('base')) throw new Error('missing test "base"');
    if (!byName.get('base').isAbstract) throw new Error('test "base" should be abstract');
    if (byName.get('passes').isAbstract) throw new Error('test "passes" should NOT be abstract');
    return { count: mg.length, names: mg.map((t) => t.name) };
});

test('runTests on canvas: passing test reports passed=1', async () => {
    const r = await page.evaluate(async () => {
        const root = await navigator.storage.getDirectory();
        const ws = await root.getDirectoryHandle('workspace');
        const dir = await ws.getDirectoryHandle('mgtests');
        const fh = await dir.getFileHandle('main.fbasic');
        const src = await (await fh.getFile()).text();
        const json = await window.theInstance.invokeMethodAsync('RunTests', src, 'passes');
        return JSON.parse(json);
    });
    if (r.passed !== 1 || r.failed !== 0) {
        throw new Error(`expected passed=1 failed=0, got ${JSON.stringify(r)}`);
    }
    return { passed: r.passed, failed: r.failed };
});

test('runTests on canvas: failing test reports passed=0 failed=1 + reason', async () => {
    const r = await page.evaluate(async () => {
        const root = await navigator.storage.getDirectory();
        const ws = await root.getDirectoryHandle('workspace');
        const dir = await ws.getDirectoryHandle('mgtests');
        const fh = await dir.getFileHandle('main.fbasic');
        const src = await (await fh.getFile()).text();
        const json = await window.theInstance.invokeMethodAsync('RunTests', src, 'fails');
        return JSON.parse(json);
    });
    if (r.passed !== 0 || r.failed !== 1) {
        throw new Error(`expected passed=0 failed=1, got ${JSON.stringify(r)}`);
    }
    const fail = (r.results || [])[0];
    if (!fail || fail.passed) throw new Error('expected first result to be a failure');
    if (!(fail.failureMessage || fail.failureReason || fail.failureSourceText)) {
        throw new Error('failure result has no message/reason/sourceText');
    }
    return { passed: r.passed, failed: r.failed, message: (fail.failureMessage || fail.failureReason || '').slice(0, 60) };
});

test('DebugStartTest opens a session paused at the test entry', async () => {
    const r = await page.evaluate(async () => {
        const root = await navigator.storage.getDirectory();
        const ws = await root.getDirectoryHandle('workspace');
        const dir = await ws.getDirectoryHandle('mgtests');
        const fh = await dir.getFileHandle('main.fbasic');
        const src = await (await fh.getFile()).text();
        const json = await window.theInstance.invokeMethodAsync('DebugStartTest', src, 'passes');
        return JSON.parse(json);
    });
    if (!r.ok) throw new Error('DebugStartTest returned not-ok: ' + JSON.stringify(r));
    if (!Array.isArray(r.statementLines) || r.statementLines.length === 0) {
        throw new Error('statementLines should be a non-empty array, got ' + JSON.stringify(r.statementLines));
    }
    // Terminate so the canvas doesn't sit in a paused-debug state for
    // the rest of this run.
    await page.evaluate(() => window.theInstance.invokeMethodAsync('DebugTerminate'));
    return { ok: r.ok, statementLineCount: r.statementLines.length };
});

test('DebugStartTest rejects an abstract test cleanly', async () => {
    const r = await page.evaluate(async () => {
        const root = await navigator.storage.getDirectory();
        const ws = await root.getDirectoryHandle('workspace');
        const dir = await ws.getDirectoryHandle('mgtests');
        const fh = await dir.getFileHandle('main.fbasic');
        const src = await (await fh.getFile()).text();
        const json = await window.theInstance.invokeMethodAsync('DebugStartTest', src, 'base');
        return JSON.parse(json);
    });
    if (r.ok) throw new Error('expected ok=false for abstract test');
    if (!/abstract/i.test(r.error || '')) throw new Error('error message should mention "abstract": ' + r.error);
    return { ok: r.ok, error: r.error };
});

test('DebugStartTest rejects an unknown test name with a useful error', async () => {
    const r = await page.evaluate(async () => {
        const root = await navigator.storage.getDirectory();
        const ws = await root.getDirectoryHandle('workspace');
        const dir = await ws.getDirectoryHandle('mgtests');
        const fh = await dir.getFileHandle('main.fbasic');
        const src = await (await fh.getFile()).text();
        const json = await window.theInstance.invokeMethodAsync('DebugStartTest', src, 'does-not-exist');
        return JSON.parse(json);
    });
    if (r.ok) throw new Error('expected ok=false for unknown test name');
    if (!/no test/i.test(r.error || '')) throw new Error('error message should mention "no test": ' + r.error);
    return { ok: r.ok, error: r.error };
});

test('game can be re-run via LoadProgram after RunTests completes', async () => {
    // RunTests leaves _testMode=true without the fix, which blocks LoadProgram:
    // Game1.Update returns early from the test-mode branch before it ever
    // reaches the _reloadRequestedFromUi check.
    const src = await page.evaluate(async () => {
        const root = await navigator.storage.getDirectory();
        const ws = await root.getDirectoryHandle('workspace');
        const dir = await ws.getDirectoryHandle('mgtests');
        const fh = await dir.getFileHandle('main.fbasic');
        return (await fh.getFile()).text();
    });

    // Run a test to enter (and exit) test mode.
    const testJson = await page.evaluate(
        (s) => window.theInstance.invokeMethodAsync('RunTests', s, 'passes'),
        src,
    );
    const testResult = JSON.parse(testJson);
    if (testResult.passed !== 1) throw new Error('setup: expected passing test, got ' + JSON.stringify(testResult));

    // Now call LoadProgram — this is the path the Run button takes.
    const ok = await page.evaluate(() =>
        window.theInstance.invokeMethodAsync('LoadProgram', 'do\n  sync\nloop\n'),
    );
    if (!ok) throw new Error('LoadProgram returned false after tests completed');

    // Wait a few frames and confirm the runtime is still alive (no crash).
    await page.waitForTimeout(600);
    const alive = await page.evaluate(() =>
        typeof window.theInstance?.invokeMethodAsync === 'function',
    );
    if (!alive) throw new Error('theInstance died after LoadProgram following RunTests');

    return { ok };
});

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

if (pageErrors.length) console.log('PAGE ERRORS:', pageErrors);
if (failed > 0) {
    console.log('--- recent console (last 25) ---');
    for (const m of consoleAll.slice(-25)) console.log(' ', m.slice(0, 300));
}

// Cleanup.
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    try { await ws.removeEntry('mgtests', { recursive: true }); } catch {}
    localStorage.setItem('fade.activeProject', 'default');
});

await browser.close();
process.exit(failed > 0 || pageErrors.length > 0 ? 1 : 0);
