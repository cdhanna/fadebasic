// Headless integration tests for the Playground LSP features.
//
// Drives Monaco directly inside the page rather than simulating keystrokes so
// the tests are deterministic. Each test seeds a known source string, calls
// monaco's command for the feature being tested, then inspects the resulting
// widget DOM.
//
// Usage: node scripts/test-lsp.mjs [--only=name1,name2] [--url URL]

import { chromium } from 'playwright';

const args = process.argv.slice(2);
const urlArg = (args.find((a) => a.startsWith('--url=')) ?? '').slice('--url='.length);
const url = urlArg || 'http://localhost:5311/';
const onlyArg = (args.find((a) => a.startsWith('--only=')) ?? '').slice('--only='.length);
const only = onlyArg ? new Set(onlyArg.split(',').map((s) => s.trim())) : null;

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
// Let the runtime worker finish booting + the model push the initial doc.
await new Promise((r) => setTimeout(r, 2000));

// ─── Helpers in page context ─────────────────────────────────────────────
async function seedSource(source) {
    await page.evaluate(({ source }) => {
        const ed = window.monaco.editor.getEditors().find((e) => e.getModel()?.getLanguageId() === 'fade');
        const model = ed.getModel();
        model.applyEdits([{ range: model.getFullModelRange(), text: source }]);
    }, { source });
    // Polling pushes the new doc to the LSP every 250ms; allow time for the
    // lex/parse + diagnostic round-trip.
    await new Promise((r) => setTimeout(r, 800));
}

async function setCursor(line, column) {
    await page.evaluate(({ line, column }) => {
        const ed = window.monaco.editor.getEditors().find((e) => e.getModel()?.getLanguageId() === 'fade');
        ed.setPosition({ lineNumber: line, column });
        ed.focus();
    }, { line, column });
}

// Dismiss any open suggest / hover / signature widgets between tests so the
// next test isn't reading state left behind by the previous one.
async function dismissWidgets() {
    await page.keyboard.press('Escape');
    await page.keyboard.press('Escape');
    await new Promise((r) => setTimeout(r, 150));
    // Belt-and-braces: hide the hover explicitly via Monaco's API.
    await page.evaluate(() => {
        const ed = window.monaco.editor.getEditors().find((e) => e.getModel()?.getLanguageId() === 'fade');
        ed?.getContribution?.('editor.contrib.hover')?.hideContentHover?.();
    });
}

async function triggerCommand(command) {
    await page.evaluate(({ command }) => {
        const ed = window.monaco.editor.getEditors().find((e) => e.getModel()?.getLanguageId() === 'fade');
        ed.trigger('test', command, null);
    }, { command });
}

// Pull a snapshot of the completion suggest widget after triggering it.
async function getCompletions() {
    await triggerCommand('editor.action.triggerSuggest');
    // Suggest widget builds asynchronously; poll for visible items.
    for (let i = 0; i < 30; i++) {
        const items = await page.evaluate(() => {
            const rows = document.querySelectorAll('.monaco-editor .suggest-widget .monaco-list-row');
            return Array.from(rows).map((r) => {
                const label = r.querySelector('.label-name .monaco-icon-label-container .monaco-icon-name-container .label-name')?.textContent
                    ?? r.querySelector('.label-name')?.textContent
                    ?? r.textContent;
                return label?.trim();
            }).filter(Boolean);
        });
        if (items.length > 0) return items;
        await new Promise((r) => setTimeout(r, 100));
    }
    return [];
}

async function getHoverText() {
    await triggerCommand('editor.action.showHover');
    for (let i = 0; i < 30; i++) {
        const text = await page.evaluate(() => {
            const hover = document.querySelector('.monaco-editor .monaco-hover');
            return hover?.textContent?.trim() ?? null;
        });
        if (text) return text;
        await new Promise((r) => setTimeout(r, 100));
    }
    return null;
}

async function getSignatureHelp() {
    await triggerCommand('editor.action.triggerParameterHints');
    for (let i = 0; i < 30; i++) {
        const sig = await page.evaluate(() => {
            const w = document.querySelector('.monaco-editor .parameter-hints-widget');
            if (!w || w.classList.contains('hidden') || !w.classList.contains('visible')) return null;
            const sigLine = w.querySelector('.signature')?.textContent?.trim();
            const activeParam = w.querySelector('.signature .parameter.active')?.textContent?.trim();
            const docs = w.querySelector('.docs')?.textContent?.trim();
            return { signature: sigLine, activeParam, docs };
        });
        if (sig?.signature) return sig;
        await new Promise((r) => setTimeout(r, 100));
    }
    return null;
}

// Direct LSP probes via the runtime worker — bypass the editor UI entirely so
// we can validate the Core handlers even before they're wired through Monaco
// providers. Uses globally-exposed helpers from main.ts (set later).
async function lspProbe(method, params) {
    return await page.evaluate(({ method, params }) => {
        const probe = window.__fadeLspProbe;
        if (!probe) throw new Error('__fadeLspProbe not exposed');
        return probe(method, params);
    }, { method, params });
}

async function workerCall(method, params = {}) {
    return await page.evaluate(({ method, params }) => {
        const helpers = window.__fadeRunnerHelpers;
        if (!helpers) throw new Error('__fadeRunnerHelpers not exposed');
        return helpers[method](params);
    }, { method, params });
}

// ─── Tests ───────────────────────────────────────────────────────────────
const tests = [];
function test(name, fn) { tests.push({ name, fn }); }

test('lsp-direct: completion at start of fresh statement', async () => {
    // Completion in LSPUtil fires when the cursor sits at a statement-start
    // position. Newline + space ensures leftToken is EndStatement so the
    // GetStatementCompletions branch fires.
    await seedSource('print "hi"\n ');
    const items = await lspProbe('completion', { line: 1, character: 1 });
    if (!items.length) throw new Error('no completions');
    const hasPrint = items.some((i) => i.label?.toLowerCase().startsWith('print'));
    if (!hasPrint) throw new Error('no print completion: ' + items.slice(0, 5).map(i => i.label).join(','));
    return { count: items.length, sample: items.slice(0, 3).map((i) => i.label) };
});

test('ui: completion widget surfaces at fresh statement', async () => {
    await seedSource('print "hi"\n ');
    await dismissWidgets();
    await setCursor(2, 2); // 1-based line 2, column 2 = after the space
    const items = await getCompletions();
    if (!items.length) throw new Error('no completions returned');
    const hasPrint = items.some((i) => i.toLowerCase().startsWith('print'));
    if (!hasPrint) throw new Error('expected a "print*" completion, got: ' + items.slice(0, 10).join(', '));
    return { items: items.slice(0, 5) };
});

test('ui: hover token info for "print"', async () => {
    await seedSource('print "hi"\n');
    await dismissWidgets();
    await setCursor(1, 3);
    const text = await getHoverText();
    if (!text) throw new Error('no hover widget surfaced');
    return { text: text.slice(0, 80) };
});

test('ui: hover error message on bad token', async () => {
    await seedSource('this is not valid fade %@$\n');
    await dismissWidgets();
    await setCursor(1, 24); // inside %@$
    const text = await getHoverText();
    if (!text) throw new Error('no hover widget for error');
    if (!/error/i.test(text)) throw new Error('hover did not mention "error": ' + text.slice(0, 100));
    return { text: text.slice(0, 120) };
});

test('ui: signature-help built-in command', async () => {
    await seedSource('print upper$("hi")\n');
    await dismissWidgets();
    // column after `upper$(` — name has 7 chars from col 7-12, paren at 13, cursor at 14
    await setCursor(1, 14);
    const sig = await getSignatureHelp();
    if (!sig) throw new Error('no signature widget');
    return sig;
});

test('lsp-direct: signature help', async () => {
    await seedSource('print upper$("hi")\n');
    const r = await lspProbe('signature-help', { line: 0, character: 13 });
    if (!r) throw new Error('no signature returned from worker');
    if (!r.signatures || r.signatures.length === 0) throw new Error('no signatures: ' + JSON.stringify(r));
    return { label: r.signatures[0].label };
});

test('lsp-direct: references on use site', async () => {
    await seedSource('x = 1\ny = x + 2\nprint x\n');
    // Click on the `x` on line 1 (the use, not the declaration).
    const r = await lspProbe('references', { line: 1, character: 4 });
    if (!r || !Array.isArray(r)) throw new Error('expected array, got ' + JSON.stringify(r));
    if (r.length < 2) throw new Error('expected >= 2 references, got ' + r.length + ': ' + JSON.stringify(r));
    return { count: r.length };
});

test('lsp-direct: references on def site', async () => {
    await seedSource('x = 1\ny = x + 2\nprint x\n');
    const r = await lspProbe('references', { line: 0, character: 0 });
    if (!r || !Array.isArray(r)) throw new Error('expected array, got ' + JSON.stringify(r));
    if (r.length < 2) throw new Error('expected >= 2 references from def site, got ' + r.length + ': ' + JSON.stringify(r));
    return { count: r.length };
});

test('lsp-direct: goto-def for variable', async () => {
    await seedSource('x = 1\nprint x\n');
    const r = await lspProbe('goto-def', { line: 1, character: 6 });
    if (!r) throw new Error('no definition returned');
    if (r.range?.start?.line !== 0) throw new Error('expected definition on line 0, got ' + JSON.stringify(r));
    return r;
});

test('lsp-direct: hover shows rich docs for built-in command', async () => {
    await seedSource('print "hello"\n');
    // Hover anywhere inside the `print` command — line 0, char 2.
    const r = await lspProbe('hover', { line: 0, character: 2 });
    if (!r) throw new Error('no hover returned');
    if (!/### print/.test(r.contents)) throw new Error('missing command header: ' + r.contents.slice(0, 120));
    // Built-in `print` has at least one parameter — the docs path should
    // include a Parameters section.
    if (!/Parameters/.test(r.contents)) throw new Error('missing Parameters section: ' + r.contents.slice(0, 200));
    return { contents: r.contents.slice(0, 80) };
});

test('lsp-direct: hover renders trivia for function as markdown', async () => {
    // Trivia is the contiguous block of comment lines immediately before
    // a function/declaration. Hover over the call-site name to verify.
    await seedSource([
        "` Greets the user politely.",
        "` Returns a friendly greeting.",
        "function greet(name as string)",
        "    exitfunction \"hi \" + name",
        "endfunction \"\"",
        "",
        "msg$ = greet(\"world\")",
    ].join('\n'));
    // Hover on `greet` of the call (last line, 0-indexed line 6, the `g` at col 7)
    const r = await lspProbe('hover', { line: 6, character: 7 });
    if (!r) throw new Error('no hover returned');
    if (!/greet/.test(r.contents)) throw new Error('hover missing function header: ' + r.contents);
    if (!/Greets the user/i.test(r.contents)) throw new Error('hover missing trivia: ' + r.contents);
    return { contents: r.contents.slice(0, 120) };
});

test('lsp-direct: document symbols lists function + label', async () => {
    await seedSource([
        "function foo()",
        "endfunction \"\"",
        ":mylabel",
        "print 1",
    ].join('\n'));
    const r = await lspProbe('document-symbols', {});
    if (!Array.isArray(r) || r.length === 0) throw new Error('no symbols: ' + JSON.stringify(r));
    const names = r.map((s) => s.name);
    if (!names.includes('foo')) throw new Error('missing fn symbol: ' + names.join(','));
    return { names };
});

test('lsp-direct: folding ranges for if-block', async () => {
    // Block-form `if ... endif` requires NO `then` after the condition.
    // The `then` form is one-liner-only per ParseIfStatement.
    await seedSource([
        "if 1 = 1",
        "    print \"a\"",
        "    print \"b\"",
        "endif",
    ].join('\n'));
    const r = await lspProbe('folding-ranges', {});
    if (!Array.isArray(r) || r.length === 0) throw new Error('no folds returned');
    const span = r.find((x) => x.endLine - x.startLine >= 2);
    if (!span) throw new Error('no multi-line fold: ' + JSON.stringify(r));
    return { count: r.length, first: r[0] };
});

test('lsp-direct: format emits TokenFormatter edits', async () => {
    // Messy indent should produce at least one whitespace edit.
    await seedSource("if 1 = 1 then\n     print \"a\"\nendif\n");
    const r = await lspProbe('format', { options: { tabSize: 2, insertSpaces: true, casing: 0 } });
    if (!Array.isArray(r)) throw new Error('format did not return array: ' + JSON.stringify(r));
    return { count: r.length, sample: r[0] };
});

test('lsp-direct: rename produces a workspace edit', async () => {
    await seedSource('foo = 1\nbar = foo + 2\n');
    const r = await lspProbe('rename', { line: 0, character: 0, newName: 'baz' });
    if (!r?.changes) throw new Error('no rename edit returned: ' + JSON.stringify(r));
    const allEdits = Object.values(r.changes).flat();
    if (allEdits.length < 2) throw new Error('expected >= 2 edits, got ' + allEdits.length);
    if (!allEdits.every((e) => e.newText === 'baz')) throw new Error('edit text wrong: ' + JSON.stringify(allEdits));
    return { edits: allEdits.length };
});

test('lsp: lex+parse duplicates collapse to a single diagnostic', async () => {
    // An unclosed string literal historically surfaced in both the lex
    // pass (tokenErrors) AND the parse pass (Program.GetAllErrors), so the
    // LSP returned two identical entries. We now dedupe by signature.
    await seedSource('x$ = "hello\n');
    const markers = await page.evaluate(() => {
        const uri = window.monaco.Uri.file('/workspace/main.fbasic');
        return window.monaco.editor.getModelMarkers({ resource: uri })
            .filter((m) => m.owner === 'fade')
            .map((m) => ({ msg: m.message, line: m.startLineNumber, col: m.startColumn }));
    });
    if (markers.length === 0) {
        throw new Error('expected at least one diagnostic for the unclosed string');
    }
    // Look for the [0002] code specifically; any two markers with the
    // identical (msg, line, col) tuple count as a duplicate failure.
    const seen = new Set();
    for (const m of markers) {
        const key = `${m.msg}|${m.line}|${m.col}`;
        if (seen.has(key)) {
            throw new Error('duplicate diagnostic emitted: ' + JSON.stringify(markers));
        }
        seen.add(key);
    }
    return { markers };
});

test('tests: list+run integration', async () => {
    // Fade test syntax uses bare names: `test foo` / `endtest`.
    const source = [
        "test addsone",
        "    assert 1 + 1 = 2",
        "endtest",
        "test failsonpurpose",
        "    assert 1 = 0",
        "endtest",
    ].join('\n');
    const list = await workerCall('listTests', { source });
    if (!Array.isArray(list) || list.length !== 2) throw new Error('expected 2 tests, got ' + JSON.stringify(list));
    const run = await workerCall('runTests', { source });
    if (run.passed !== 1 || run.failed !== 1) {
        throw new Error('expected 1 pass / 1 fail, got ' + JSON.stringify({ p: run.passed, f: run.failed, err: run.error }));
    }
    return { passed: run.passed, failed: run.failed, names: list.map((t) => t.name) };
});

// ─── Run ─────────────────────────────────────────────────────────────────
let passed = 0;
let failed = 0;
for (const t of tests) {
    if (only && !only.has(t.name)) continue;
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
