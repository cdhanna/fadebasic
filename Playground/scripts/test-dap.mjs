// Headless integration tests for the debug session (DAP) features.
//
// Drives the FadeRunner exposed at `window.__fadeRunnerHelpers.debug` so we
// can validate the end-to-end debug loop without faking Monaco gutter clicks
// or breakpoint glyph rendering.
//
// Usage: node scripts/test-dap.mjs

import { chromium } from 'playwright';

const url = 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
const page = await context.newPage();

const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e));
page.on('console', (msg) => {
    if (msg.type() === 'error') console.error('[browser-error]', msg.text());
});

await page.goto(url, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
// One settling reload to avoid HMR's double-bootstrap pollution.
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
await new Promise((r) => setTimeout(r, 1500));

async function dbg(method, params = {}) {
    return await page.evaluate(({ method, params }) => {
        const r = window.__fadeRunnerHelpers?.debug;
        if (!r) throw new Error('debug helpers not exposed');
        return r[method](params);
    }, { method, params });
}

const tests = [];
function test(name, fn) { tests.push({ name, fn }); }

test('debug: starts and reports statement lines', async () => {
    const source = [
        'x = 1',
        'y = 2',
        'z = x + y',
        'print z',
    ].join('\n');
    const result = await dbg('start', { source });
    if (!result?.ok) throw new Error('start failed: ' + JSON.stringify(result));
    if (!Array.isArray(result.statementLines) || result.statementLines.length === 0)
        throw new Error('no statement lines reported');
    return { lines: result.statementLines };
});

test('debug: breakpoint hits and stack frame visible', async () => {
    const source = [
        'x = 1',
        'y = 2',
        'z = x + y',
        'print z',
    ].join('\n');
    await dbg('terminate');
    // Clear any stale event so the wait below only sees a fresh one.
    await page.evaluate(() => { window.__debugLastEvent = null; });
    const start = await dbg('start', { source });
    if (!start?.ok) throw new Error('start failed: ' + JSON.stringify(start));
    // Session starts paused — set breakpoint on line 3 (`z = x + y`) and resume.
    await dbg('setBreakpoints', { breakpoints: [{ line: 2, column: 0 }] });
    await dbg('continue');
    // Wait for the breakpoint to fire.
    const hit = await page.waitForFunction(() => {
        return window.__debugLastEvent?.type === 'REV_REQUEST_BREAKPOINT';
    }, { timeout: 8000 }).catch(() => null);
    if (!hit) throw new Error('breakpoint did not fire');
    const frames = await dbg('stackFrames');
    if (!Array.isArray(frames) || frames.length === 0) throw new Error('no frames at breakpoint');
    return { topFrame: frames[0] };
});

test('debug: scopes contain locals', async () => {
    const frames = await dbg('stackFrames');
    if (!Array.isArray(frames) || frames.length === 0) throw new Error('no frames available');
    // Frame id is its index in the list (DebugScopeRequest.frameIndex).
    const scopes = await dbg('scopes', { frameId: 0 });
    if (!scopes?.scopes?.length) throw new Error('no scopes returned');
    const flatVars = scopes.scopes.flatMap((s) => s.variables ?? []);
    const x = flatVars.find((v) => v.name?.toLowerCase() === 'x');
    if (!x) throw new Error('variable x not in scope: ' + flatVars.map((v) => v.name).join(','));
    if (x.value !== '1') throw new Error('expected x = 1, got ' + x.value);
    return { x: x.value };
});

test('debug: eval expression', async () => {
    const result = await dbg('eval', { frameId: 0, expression: 'x + y' });
    if (!result || result.failed) throw new Error('eval failed: ' + JSON.stringify(result));
    if (result.value !== '3') throw new Error('expected 3, got ' + result.value);
    return result;
});

test('debug: step over advances to next line', async () => {
    const beforeFrames = await dbg('stackFrames');
    const beforeLine = beforeFrames[0]?.lineNumber;
    // Reset the captured event so we can spot the new one.
    await page.evaluate(() => { window.__debugLastEvent = null; });
    await dbg('step', { kind: 'over' });
    // The session signals a step landing by ACK'ing the original step request
    // with a StepNextResponseMessage (type=PROTO_ACK, status=1). That's how
    // the native DAP adapter knows to fire a Stopped event for VSCode; we
    // do the same recognition on the page.
    await page.waitForFunction(() => {
        const ev = window.__debugLastEvent;
        if (!ev || ev.type !== 'PROTO_ACK' || !ev.json) return false;
        try {
            const parsed = JSON.parse(ev.json);
            return parsed?.status === 1;
        } catch { return false; }
    }, { timeout: 8000 });
    const afterFrames = await dbg('stackFrames');
    if (!afterFrames.length) throw new Error('no frames after step');
    if (afterFrames[0].lineNumber === beforeLine)
        throw new Error('expected line change, still on ' + beforeLine);
    return { from: beforeLine, to: afterFrames[0].lineNumber };
});

test('debug: terminate', async () => {
    await dbg('terminate');
    return true;
});

test('pause during wait ms actually pauses the VM (no next-instruction)', async () => {
    // Short wait + print. Pause mid-wait; settle past when the print
    // WOULD fire if pause didn't take effect. Behavior-only assertion —
    // if the VM ran past the pause, "second" appears in the Output panel.
    const source = 'wait ms(800)\nprint "second"\nwait ms(800)\nprint "third"\n';
    await page.evaluate(() => document.getElementById('output-clear')?.click());
    await dbg('start', { source });
    await dbg('continue');
    await new Promise((r) => setTimeout(r, 200));
    await dbg('pause');
    // Settle past the 800ms wait + a margin. If pause failed to enqueue
    // REQUEST_PAUSE, "second" would print by now.
    await new Promise((r) => setTimeout(r, 2000));
    const printed = await page.evaluate(() =>
        Array.from(document.querySelectorAll('#output .output-line'))
            .map((l) => (l.textContent || '').trim()).filter(Boolean),
    );
    if (printed.some((l) => /second|third/.test(l))) {
        throw new Error('VM ran past the pause: ' + JSON.stringify(printed));
    }
    await dbg('terminate');
    return { printed };
});

test('terminate during wait ms unwinds cleanly (no runtime-error message)', async () => {
    // Long wait + a print. Terminate mid-wait. Two assertions:
    //   1. The print should NOT appear (terminate took effect).
    //   2. No "Runtime exception" text should land in Output — terminate
    //      flips requestedExit on the base session for a clean exit,
    //      not an exception path.
    const source = 'wait ms(3000)\nprint "this should never print"\n';
    await page.evaluate(() => document.getElementById('output-clear')?.click());
    await dbg('start', { source });
    await dbg('continue');
    await new Promise((r) => setTimeout(r, 300));
    await dbg('terminate');
    await new Promise((r) => setTimeout(r, 3500));
    const printed = await page.evaluate(() =>
        Array.from(document.querySelectorAll('#output .output-line'))
            .map((l) => (l.textContent || '').trim()).filter(Boolean),
    );
    if (printed.some((l) => /should never print/.test(l))) {
        throw new Error('VM ran past terminate: ' + JSON.stringify(printed));
    }
    if (printed.some((l) => /Runtime exception|OperationCanceledException|interrupted by terminate/i.test(l))) {
        throw new Error('terminate surfaced an error message: ' + JSON.stringify(printed));
    }
    return { printed };
});

test('mid-run breakpoint update wakes wait + hits within ~2.5s', async () => {
    // Loop with a long wait per iteration. Without the kind=3 wake,
    // adding a breakpoint mid-iteration only takes effect on the NEXT
    // iteration (3s+ later). With the wake, WaitImpl throws a
    // VmInterruptException so DebugTick unwinds; the worker's JS event
    // loop then drains the set-breakpoints message before the next tick.
    const source = [
        'for n = 1 to 5',
        '  print "tick"',
        '  wait ms(3000)',
        '  print "after-wait"',
        'next',
    ].join('\n');
    await page.evaluate(() => document.getElementById('output-clear')?.click());

    // Collect every debug event reliably (the old probe polled
    // __debugLastEvent at 100ms; a fast PROTO_ACK could overwrite the
    // breakpoint event before the next poll). Instead, push every
    // event into an array we read after.
    await page.evaluate(() => {
        window.__diagEvents = [];
        const orig = window.__fadeRunnerHelpers?.debug;
        const runner = orig ? window.runner ?? null : null;
        // Hook by wrapping __debugLastEvent's setter via a property descriptor.
        let _v;
        Object.defineProperty(window, '__debugLastEvent', {
            configurable: true,
            get() { return _v; },
            set(v) { _v = v; window.__diagEvents.push(v); },
        });
    });

    await dbg('start', { source });
    await dbg('continue');
    await new Promise((r) => setTimeout(r, 400)); // enter wait ms
    const t0 = Date.now();
    // Add a breakpoint on "after-wait" (line 4, 1-based).
    await dbg('setBreakpoints', { breakpoints: [{ lineNumber: 4, charNumber: 0 }] });
    // Wait until we observe REV_REQUEST_BREAKPOINT in the collected log.
    let success = false;
    try {
        await page.waitForFunction(() => {
            return (window.__diagEvents || []).some((e) => e && e.type === 'REV_REQUEST_BREAKPOINT');
        }, { timeout: 5000 });
        success = true;
    } catch (_) { /* fall through to dump diagnostics */ }
    const elapsed = Date.now() - t0;
    if (!success) {
        const dump = await page.evaluate(() => ({
            events: (window.__diagEvents || []).map((e) => ({ type: e?.type, json: (e?.json || '').slice(0, 80) })),
            output: Array.from(document.querySelectorAll('#output .output-line'))
                .map((l) => (l.textContent || '').trim()).filter(Boolean),
        }));
        await dbg('terminate');
        throw new Error('no breakpoint event; dump=' + JSON.stringify(dump, null, 2));
    }
    if (elapsed > 2500) {
        throw new Error(`mid-run breakpoint took ${elapsed}ms — wake didn't fire`);
    }
    await dbg('terminate');
    return { elapsed };
});

test('isolation: lsp worker keeps beating while the VM is sync-blocked', async () => {
    // Click Run on a program that issues `wait ms(2000)`. While the VM
    // worker is blocked inside Thread.Sleep, the lsp worker should
    // continue posting heartbeats AND respond to LSP requests within a
    // tight deadline. If they used to share one worker, this would
    // deadlock or time out.
    await page.evaluate(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/main.fbasic'));
        m.applyEdits([{ range: m.getFullModelRange(), text: 'print "sleeping"\nwait ms(2000)\nprint "done"\n' }]);
    });
    await new Promise((r) => setTimeout(r, 600));
    // Fire Run via the header button.
    await page.evaluate(() => {
        const btns = Array.from(document.querySelectorAll('vscode-button, button'));
        btns.find((b) => /Run \(/.test(b.textContent || ''))?.click();
    });
    // Give wait ms a moment to start.
    await new Promise((r) => setTimeout(r, 400));
    // Now hit the LSP — completion is fine. Should return within 1.5s
    // even though the VM is sync-blocked for ~1.6s more.
    const t0 = Date.now();
    const completion = await page.evaluate(async () => {
        return await window.__fadeLspProbe('completion', { line: 0, character: 0 });
    });
    const elapsed = Date.now() - t0;
    if (elapsed > 1500) throw new Error(`LSP took ${elapsed}ms while VM was blocked — should be fast`);
    if (!Array.isArray(completion)) throw new Error('completion did not return an array: ' + JSON.stringify(completion));
    // Wait for the run to finish so subsequent tests start clean.
    await page.waitForFunction(() =>
        Array.from(document.querySelectorAll('#output .output-line')).some((l) => /done/.test(l.textContent || '')),
        { timeout: 5000 });
    return { elapsed, completionCount: completion.length };
});

test('debug: print lines stream once (not duplicated at session end)', async () => {
    // Start a fresh session in a program that prints three lines and ends.
    // Each `print` line should appear in the Output panel exactly once;
    // before this fix, DebugTick re-emitted the print buffer alongside the
    // live JSImport stream, so users saw each line twice.
    await page.evaluate(() => document.getElementById('output-clear')?.click());
    const source = 'print "alpha"\nprint "beta"\nprint "gamma"\n';
    await dbg('start', { source });
    await dbg('continue');
    await page.waitForFunction(() => {
        const ev = window.__debugLastEvent;
        return ev && (ev.type === 'complete' || ev.type === 'REV_REQUEST_EXITED');
    }, { timeout: 10000 });
    // Settle for any straggler `print` messages.
    await new Promise((r) => setTimeout(r, 500));
    const lines = await page.evaluate(() =>
        Array.from(document.querySelectorAll('#output .output-line'))
            .map((l) => (l.textContent || '').trim())
            .filter(Boolean),
    );
    const counts = {};
    for (const l of lines) counts[l] = (counts[l] || 0) + 1;
    const offenders = Object.entries(counts).filter(([_k, v]) => v > 1);
    if (offenders.length) {
        throw new Error('debug print lines duplicated: ' + JSON.stringify(offenders));
    }
    if (lines.length < 3) throw new Error('expected >=3 print lines, got: ' + JSON.stringify(lines));
    await dbg('terminate');
    return { counts };
});

let passed = 0, failed = 0;
for (const t of tests) {
    process.stdout.write(`• ${t.name} ... `);
    try {
        const result = await t.fn();
        console.log('OK', result ? JSON.stringify(result) : '');
        passed++;
    } catch (e) {
        console.log('FAIL');
        console.log('   ', e.message);
        failed++;
    }
}

if (pageErrors.length) {
    console.log('\nPage errors during run:');
    for (const e of pageErrors) console.log('  ' + e.message);
}

await browser.close();
console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
