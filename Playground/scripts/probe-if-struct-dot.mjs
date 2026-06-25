// Probe for the "if ballPos. doesn't show x/y" bug.
// Seeds a web project with a struct, opens the playground, and reads
// directly what runner.getCompletions returns at `if ballPos.|` —
// then checks what Monaco actually surfaces to the user via its
// suggestion widget.
//
// Three diagnostic outputs:
//   1. LSP_RESPONSE  — raw items returned by FB.LspCompletion.
//   2. MONACO_SUGS   — what Monaco's completion controller decided
//                      to show after running its filter.
//   3. WORD_AT_CURSOR — Monaco's getWordUntilPosition view of the
//                      cursor — the value that drives item filtering.
//
// Usage: dev server must be running on :5311 (HTTPS) first.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const browser = await chromium.launch({
    headless: true,
    args: ['--ignore-certificate-errors', '--use-gl=swiftshader', '--enable-unsafe-swiftshader'],
});
const page = await browser.newPage({
    viewport: { width: 1400, height: 900 },
    ignoreHTTPSErrors: true,
});

const captured = [];
page.on('pageerror', e => {
    captured.push(`[PE] ${e.message.slice(0, 400)}`);
});
page.on('console', m => {
    const t = m.text();
    if (/Possible EventEmitter|deprecated|webgl|^GET /i.test(t)) return;
    captured.push(`[${m.type()}] ${t.slice(0, 800)}`);
});

await page.goto('https://localhost:5311/', { waitUntil: 'domcontentloaded' });

const SRC = `type Vec2
  x as integer
  y as integer
endtype
local ballPos as Vec2
if ballPos.
`;

await page.evaluate(async (src) => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('ifdotprobe', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'ifdotprobe', type: 'web',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write(src); await sw.close();
    localStorage.setItem('fade.activeProject', 'ifdotprobe');
}, SRC);
await page.reload({ waitUntil: 'domcontentloaded' });

page.setDefaultTimeout(120_000);
// Wait for monaco + an open editor with our source.
await page.waitForFunction(() => {
    const m = (window).monaco;
    if (!m) return false;
    const eds = m.editor.getEditors();
    if (!eds || eds.length === 0) return false;
    return eds.some(e => (e.getModel()?.getValue() ?? '').includes('ballPos.'));
}, { timeout: 120_000 });
console.log('→ editor ready');
// Let LSP/setDocument flush.
await page.waitForTimeout(1000);

const result = await page.evaluate(async () => {
    const m = window.monaco;
    const editor = m.editor.getEditors().find(e => (e.getModel()?.getValue() ?? '').includes('ballPos.'));
    const model = editor.getModel();
    const uri = model.uri.toString();

    const text = model.getValue();
    const lines = text.split('\n');
    const lineIdx = lines.findIndex(l => l.includes('if ballPos.'));
    const dotCol = lines[lineIdx].indexOf('.') + 1; // 0-based char after dot
    const pos = { lineNumber: lineIdx + 1, column: dotCol + 1 }; // 1-based

    const word = model.getWordUntilPosition(pos);

    // LSP direct path. setDocument first to make sure the worker matches
    // what's in the editor model. lspUri mirrors how main.ts maps model
    // uris to LSP uris (no transform for non-project files).
    const lspUri = uri.startsWith('fade://') ? uri : ('fade://' + uri.replace(/^[a-z]+:\/\//, ''));
    const helpers = window.__fadeRunnerHelpers;
    helpers.setDocument?.({ uri: lspUri, source: text });
    await new Promise(r => setTimeout(r, 200));

    let lspItems = null;
    try {
        lspItems = await helpers.getCompletions({ uri: lspUri, line: lineIdx, character: dotCol });
    } catch (e) {
        lspItems = { error: String(e) };
    }

    // Now also drive Monaco's UI path to see what the user sees.
    editor.setPosition(pos);
    editor.focus();
    try {
        await editor.getAction('editor.action.triggerSuggest').run();
    } catch (e) { /* fall through */ }
    await new Promise(r => setTimeout(r, 1200));

    const suggestController = editor.getContribution('editor.contrib.suggestController');
    const monacoSuggestions = [];
    const controllerState = {};
    if (suggestController) {
        controllerState.hasModel = !!suggestController.model;
        const completionModel = suggestController.model?._completionModel;
        controllerState.hasCompletionModel = !!completionModel;
        if (completionModel) {
            controllerState.completionModelKeys = Object.keys(completionModel);
            const cmItems = completionModel.items ?? completionModel._items ?? [];
            controllerState.completionModelItemCount = cmItems.length;
            for (const it of cmItems) {
                monacoSuggestions.push({
                    label: it.completion?.label ?? it.textLabel ?? '?',
                    kind: it.completion?.kind,
                });
            }
        }
        controllerState.state = suggestController.model?.state;
    }

    // Also call our completion provider directly through Monaco's API,
    // bypassing the suggest controller. This tells us whether the
    // provider itself returned anything Monaco's filter could even see.
    let providerResult = null;
    try {
        const providers = m.languages.getCompletionItemProvider?.('fade');
        // Monaco doesn't expose getCompletionItemProvider publicly; try
        // an alternative — drive provideCompletionItems via the
        // language features registry the way a test would.
        // Fallback: directly inspect the registered provider on the
        // global completion item provider registry if present.
        providerResult = providers ? 'found ' + typeof providers : 'no_get_api';
    } catch (e) { providerResult = 'err: ' + e.message; }


    return {
        lspUri,
        sourceLine: lines[lineIdx],
        cursorCol1Based: pos.column,
        word: { word: word.word, startColumn: word.startColumn, endColumn: word.endColumn },
        lspItemCount: Array.isArray(lspItems) ? lspItems.length : -1,
        lspItems: Array.isArray(lspItems) ? lspItems.slice(0, 20).map(i => ({ label: i.label, kind: i.kind })) : lspItems,
        monacoSuggestionCount: monacoSuggestions.length,
        monacoSuggestions: monacoSuggestions.slice(0, 20),
        controllerState,
        providerResult,
    };
});

console.log('\n── PROBE RESULT ──');
console.log(JSON.stringify(result, null, 2));

console.log('\n── VERDICT ──');
const monacoHasX = (result.monacoSuggestions ?? []).some(it => it.label === 'x');
console.log('Monaco shows `x`:    ', monacoHasX);
console.log('Word at cursor:      ', JSON.stringify(result.word));

let exitCode = 0;
if (!monacoHasX) {
    console.log('FAIL: Monaco did NOT surface `x` after `if ballPos.`.');
    exitCode = 1;
} else {
    console.log('PASS: x reaches the Monaco dropdown.');
}

if (exitCode !== 0) {
    console.log('\n── CAPTURED ──');
    for (const line of captured.slice(-30)) console.log(line);
}

await browser.close();
process.exit(exitCode);
