// Headless check of the Playground page. Captures console messages, errors,
// and key DOM signals so we can iterate without bouncing through the human.
//
// Usage: node scripts/check-page.mjs [--run] [url] [timeoutMs]

import { chromium } from 'playwright';

const positional = process.argv.slice(2).filter((a) => !a.startsWith('--'));
const url = positional[0] || 'http://localhost:5311/';
const timeoutMs = Number(positional[1] || 45000);

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext();
const page = await context.newPage();

const messages = [];
const pageErrors = [];

page.on('console', (msg) => {
    messages.push({ type: msg.type(), text: msg.text() });
});
page.on('pageerror', (err) => {
    pageErrors.push({ message: err.message, stack: err.stack });
});
page.on('console', (msg) => {
    // Already captured above; surface deep stacks via printing args too
});
page.on('requestfailed', (req) => {
    messages.push({
        type: 'requestfailed',
        text: `${req.method()} ${req.url()}  ${req.failure()?.errorText ?? ''}`,
    });
});

try {
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: timeoutMs });
} catch (e) {
    console.error('navigation failed:', e.message);
    await browser.close();
    process.exit(2);
}

// Wait for bootstrap to finish (success or failure).
const settled = await page
    .waitForFunction(
        () => {
            const w = window;
            const status = document.getElementById('status')?.textContent ?? '';
            if (status.startsWith('Bootstrap failed:')) {
                return { kind: 'bootstrap-failed', text: status };
            }
            if (w.__fadeBootstrapDone) {
                return { kind: 'done', status };
            }
            return false;
        },
        { timeout: timeoutMs },
    )
    .then((h) => h.jsonValue())
    .catch(() => null);

const workbenchPresent = await page.evaluate(
    () => document.querySelector('.monaco-workbench') != null,
);
const editorPresent = await page.evaluate(
    () => document.querySelector('.monaco-editor') != null,
);
const activityBarPresent = await page.evaluate(
    () => document.querySelector('.activitybar') != null,
);
const statusBarPresent = await page.evaluate(
    () => document.querySelector('.statusbar') != null,
);

const partsPresent = await page.evaluate(() => {
    const parts = {
        editorPart: !!document.querySelector('.part.editor'),
        sidebarPart: !!document.querySelector('.part.sidebar'),
        panelPart: !!document.querySelector('.part.panel'),
        statusbarPart: !!document.querySelector('.part.statusbar'),
        titlebarPart: !!document.querySelector('.part.titlebar'),
        welcomePage: !!document.querySelector('.editor-instance .welcome-page, .gettingStartedContainer'),
    };
    return parts;
});

// Wait for any post-bootstrap async work to complete (setTimeouts, etc.)
// BEFORE we snapshot console messages.
if (process.argv.includes('--shot') || process.argv.includes('--run')) {
    await new Promise((r) => setTimeout(r, 5000));
}

const editorContent = await page.evaluate(() => {
    // Try to read the active editor's view-lines text content
    const lines = document.querySelectorAll('.monaco-editor .view-lines .view-line');
    return Array.from(lines).map((l) => l.textContent).slice(0, 4).join('\n');
});

const editorBox = await page.evaluate(() => {
    const editor = document.querySelector('.monaco-editor');
    if (!editor) return null;
    const r = editor.getBoundingClientRect();
    const cs = getComputedStyle(editor);
    return {
        width: r.width, height: r.height,
        visibility: cs.visibility, display: cs.display, opacity: cs.opacity,
        viewLinesCount: editor.querySelectorAll('.view-line').length,
    };
});

// Inspect model state via monaco from the page
const modelState = await page.evaluate(() => {
    const w = window;
    const m = w.monaco;
    if (!m) return null;
    const models = m.editor.getModels();
    return models.map((mod) => ({
        uri: mod.uri.toString(),
        languageId: mod.getLanguageId(),
        lineCount: mod.getLineCount(),
    }));
});
console.log('models:', JSON.stringify(modelState));

// Probe the workbench parts dimensions too. Each "part" is `<div class="part X">`
const partsSizes = await page.evaluate(() => {
    const partNames = ['editor', 'sidebar', 'panel', 'auxiliarybar', 'activitybar', 'statusbar', 'titlebar', 'banner'];
    return Object.fromEntries(partNames.map((name) => {
        const el = document.querySelector('.part.' + name);
        if (!el) return [name, null];
        const r = el.getBoundingClientRect();
        return [name, { w: Math.round(r.width), h: Math.round(r.height) }];
    }));
});

// And the body / workbench root
const rootSize = await page.evaluate(() => {
    const wb = document.querySelector('.monaco-workbench');
    if (!wb) return null;
    const r = wb.getBoundingClientRect();
    return { w: Math.round(r.width), h: Math.round(r.height) };
});

const tabLabels = await page.evaluate(() => {
    return Array.from(document.querySelectorAll('.tabs-container .tab .tab-label'))
        .map((t) => t.textContent)
        .slice(0, 8);
});

console.log('---');
console.log('settled:', settled ? JSON.stringify(settled) : '(timed out)');
console.log('workbench mounted:', workbenchPresent);
console.log('editor present:', editorPresent);
console.log('activity bar present:', activityBarPresent);
console.log('status bar present:', statusBarPresent);
console.log('workbench parts:', JSON.stringify(partsPresent));
console.log('editor box:', JSON.stringify(editorBox));
console.log('workbench root:', JSON.stringify(rootSize));
console.log('parts sizes:', JSON.stringify(partsSizes));

// What's inside the editor part?
const editorPartHtml = await page.evaluate(() => {
    const el = document.querySelector('.part.editor');
    if (!el) return null;
    const r = el.getBoundingClientRect();
    const parent = el.parentElement;
    const pr = parent?.getBoundingClientRect();
    return {
        outerHTML: el.outerHTML.slice(0, 500),
        rect: { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) },
        parentRect: pr ? { x: Math.round(pr.x), y: Math.round(pr.y), w: Math.round(pr.width), h: Math.round(pr.height) } : null,
        parentClass: parent?.className,
    };
});
console.log('editor part:', JSON.stringify(editorPartHtml, null, 2));

// Find ALL .monaco-editor instances and where they live
const monacoEditors = await page.evaluate(() => {
    return Array.from(document.querySelectorAll('.monaco-editor')).map((el) => {
        const r = el.getBoundingClientRect();
        const ancestors = [];
        let p = el.parentElement;
        while (p && ancestors.length < 6) {
            const cls = p.className?.toString().slice(0, 50) || '';
            const id = p.id ? '#' + p.id : '';
            ancestors.push(p.tagName + id + (cls ? '.' + cls : ''));
            p = p.parentElement;
        }
        return {
            w: Math.round(r.width),
            h: Math.round(r.height),
            viewLines: el.querySelectorAll('.view-line').length,
            ancestors,
        };
    });
});
console.log('all monaco-editors:', JSON.stringify(monacoEditors, null, 2));

// Walk up the .part.editor chain capturing inline styles
const editorChain = await page.evaluate(() => {
    const out = [];
    let el = document.querySelector('.part.editor');
    while (el && out.length < 10) {
        const r = el.getBoundingClientRect();
        out.push({
            tag: el.tagName,
            class: el.className?.toString().slice(0, 60),
            inlineStyle: el.style?.cssText?.slice(0, 200),
            w: Math.round(r.width),
            h: Math.round(r.height),
        });
        el = el.parentElement;
    }
    return out;
});
console.log('editor part chain:', JSON.stringify(editorChain, null, 2));
console.log('open tabs:', tabLabels);
console.log('editor first lines:');
console.log(editorContent.split('\n').map((l) => '  ' + l).join('\n'));
console.log('---');

const errors = messages.filter((m) => m.type === 'error' || m.type === 'requestfailed');
if (errors.length || pageErrors.length) {
    console.log('Errors (' + (errors.length + pageErrors.length) + '):');
    for (const e of errors) console.log('  [' + e.type + '] ' + e.text);
    for (const e of pageErrors) {
        console.log('  [pageerror] ' + e.message);
        if (e.stack) {
            for (const line of e.stack.split('\n').slice(0, 6)) console.log('    ' + line);
        }
    }
}

const interesting = messages.filter((m) =>
    m.text.includes('[fade]') ||
    m.text.includes('[fade-lsp]') ||
    m.text.includes('[runtime worker]') ||
    m.text.includes('[lsp worker]')
);
if (interesting.length) {
    console.log('Log lines (' + interesting.length + '):');
    for (const l of interesting) console.log('  [' + l.type + '] ' + l.text);
}
console.log('All log messages (last 10):');
for (const m of messages.filter((m) => m.type === 'log' || m.type === 'info' || m.type === 'warning' || m.type === 'error').slice(-10)) {
    console.log('  [' + m.type + '] ' + m.text.slice(0, 200));
}

const warns = messages.filter((m) => m.type === 'warning');
if (warns.length) {
    console.log('Warnings (' + warns.length + '):');
    for (const w of warns.slice(0, 8)) console.log('  ' + w.text);
    if (warns.length > 8) console.log('  ... and ' + (warns.length - 8) + ' more');
}

// If --lsp is given, introduce a syntax error and check diagnostics flow.
// To avoid HMR-induced double-bootstrap pollution in tests, do an explicit
// page reload once we know one bootstrap completed. After reload only ONE
// bootstrap will be in play.
if (settled?.kind === 'done') {
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => (window).__fadeBootstrapDone, { timeout: 30000 }).catch(() => null);
    await new Promise((r) => setTimeout(r, 1500));
}

if (process.argv.includes('--hover') && settled?.kind === 'done') {
    console.log('--- triggering hover ---');
    // Wait for the file to be loaded + LSP to have processed it
    await new Promise((r) => setTimeout(r, 2000));
    // Move mouse over a known position (line 1 char 5 — middle of "print")
    await page.evaluate(() => {
        const editor = (window).monaco.editor.getEditors()[0];
        if (editor) {
            // Trigger the editor hover at a known position
            editor.trigger('test', 'editor.action.showHover', {
                position: { lineNumber: 1, column: 5 },
            });
        }
    });
    await new Promise((r) => setTimeout(r, 1500));
    const hoverWidget = await page.evaluate(() => {
        return document.querySelector('.monaco-hover')?.textContent ?? null;
    });
    console.log('  hover widget text:', hoverWidget);
}

if (process.argv.includes('--lsp') && settled?.kind === 'done') {
    console.log('--- introducing a syntax error ---');
    // Use the EDITOR captured at runtime — same path our polling uses now.
    const setResult = await page.evaluate(() => {
        const m = window.monaco;
        const editors = m.editor.getEditors();
        if (!editors.length) return { error: 'no editor' };
        // Try to find the editor with a fade model
        const ed = editors.find((e) => e.getModel()?.getLanguageId() === 'fade') ?? editors[0];
        const model = ed.getModel();
        if (!model) return { error: 'no model on editor' };
        model.applyEdits([{ range: model.getFullModelRange(), text: 'this is not valid fade %@$' }]);
        return { uri: model.uri.toString(), value: model.getValue().slice(0, 50), editorCount: editors.length };
    });
    console.log('  setValue result:', JSON.stringify(setResult));
    // Wait for polling LSP push + diagnostics return
    await new Promise((r) => setTimeout(r, 4000));

    const problems = await page.evaluate(() => {
        const items = document.querySelectorAll('#problems-list .problem-item');
        return Array.from(items).map((el) => el.textContent);
    });
    const markerCount = await page.evaluate(() => {
        const m = window.monaco;
        return m.editor.getModelMarkers({}).length;
    });
    console.log('  marker count:', markerCount);
    console.log('  problems list items:', problems.length);
    for (const p of problems.slice(0, 5)) console.log('   ', p);

    // Test hover at the error location
    const hover = await page.evaluate(async () => {
        const m = window.monaco;
        const editors = m.editor.getEditors();
        if (!editors[0]) return null;
        // Just trigger a hover; we can't read the widget easily
        const model = editors[0].getModel();
        if (!model) return null;
        return { uri: model.uri.toString(), value: model.getValue().slice(0, 50) };
    });
    console.log('  model after edit:', JSON.stringify(hover));
}

// If --run is given AND bootstrap finished AND no fatal errors, click Run
// and check the output.
const shouldRun = process.argv.includes('--run');
if (shouldRun && settled?.kind === 'done') {
    console.log('--- clicking Run ---');
    await page.click('#run');
    try {
        await page.waitForFunction(
            () => {
                const t = document.getElementById('output')?.textContent ?? '';
                return t.length > 0 && t !== '(not yet run)' && !t.startsWith('Running');
            },
            { timeout: 15000 },
        );
    } catch {
        console.error('  output did not populate within 15s');
    }
    const out = await page.evaluate(
        () => document.getElementById('output')?.textContent ?? '',
    );
    console.log('  output (first 400 chars):');
    console.log(out.slice(0, 400).split('\n').map((l) => '    ' + l).join('\n'));
}

// How many stylesheets are adopted?
const adoptedCount = await page.evaluate(() => document.adoptedStyleSheets?.length ?? 0);
console.log('adopted stylesheets:', adoptedCount);

// Save a screenshot for visual debugging.
if (process.argv.includes('--shot')) {
    // Wait an extra moment so any late settling shows up
    await new Promise((r) => setTimeout(r, 3000));
    const lateEditor = await page.evaluate(() => {
        const el = document.querySelector('.monaco-editor');
        if (!el) return null;
        const r = el.getBoundingClientRect();
        return {
            w: Math.round(r.width),
            h: Math.round(r.height),
            viewLines: el.querySelectorAll('.view-line').length,
        };
    });
    console.log('editor 3s later:', JSON.stringify(lateEditor));
    await page.screenshot({ path: 'page-shot.png', fullPage: false });
    console.log('--- screenshot saved to page-shot.png ---');
}

await browser.close();

process.exit(errors.length || pageErrors.length ? 1 : 0);
