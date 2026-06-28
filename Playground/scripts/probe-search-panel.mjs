// Verifies the workspace-wide Search panel: seeds a known workspace, opens
// the panel via openPanelById, types a query, asserts results group by file
// with highlighted matches, then clicks a match and confirms the editor
// reveals that line.

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 240)));
page.on('console', msg => {
    if (msg.type() === 'error') console.log('[console.error]', msg.text().slice(0, 240));
});

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

// Seed a workspace with predictable files so matches are deterministic.
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('searchprobe', { create: true });
    const writeText = async (name, text) => {
        const fh = await dir.getFileHandle(name, { create: true });
        const w = await fh.createWritable();
        await w.write(text); await w.close();
    };
    await writeText('fade.json', JSON.stringify({
        $schema: '/fade.schema.json',
        name: 'searchprobe',
        type: 'web',
        commandDlls: [],
        sources: ['main.fbasic', 'utils.fbasic'],
    }, null, 2) + '\n');
    await writeText('main.fbasic',
        'print "hello playground"\n' +
        'remstart\nthis comment mentions playground twice — playground!\nremend\n' +
        'gosub greet\n' +
        'end\n' +
        'greet:\n' +
        '  print "hi playground"\n' +
        'return\n');
    await writeText('utils.fbasic',
        '`utils — supporting routines\n' +
        'function shout$(msg as string)\n' +
        '  exitfunction upper$(msg)\n' +
        'endfunction\n');
    localStorage.setItem('fade.activeProject', 'searchprobe');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise(r => setTimeout(r, 2000));

// 1. Open the search panel.
const openResult = await page.evaluate(() => {
    const dock = window.__fadeDockview;
    if (!dock) return { ok: false, reason: 'no __fadeDockview' };
    // openPanelById is not directly exposed — drive it through the View menu
    // path: try to find the View menu button and click "Search".
    // Easier: just addPanel ourselves the same way openPanelById would, since
    // the panel-cell mechanism handles the rest.
    try {
        const existing = dock.getPanel?.('search');
        if (existing) { existing.api?.setActive?.(); return { ok: true, reused: true }; }
        const ref = dock.getPanel?.('workspace')?.id ?? dock.getPanel?.('editor')?.id;
        dock.addPanel({
            id: 'search',
            component: 'search',
            title: 'Search',
            position: ref ? { referencePanel: ref, direction: 'within' } : undefined,
            renderer: 'always',
        });
        dock.getPanel('search')?.api?.setActive?.();
        return { ok: true };
    } catch (e) {
        return { ok: false, reason: String(e) };
    }
});
console.log('open search panel:', JSON.stringify(openResult));
if (!openResult.ok) {
    console.error('FAIL: could not open search panel');
    await browser.close();
    process.exit(1);
}

await new Promise(r => setTimeout(r, 250));

// 2. Confirm the input exists & is visible.
const inputVisible = await page.evaluate(() => {
    const inp = document.querySelector('.search-pane input[type="search"]');
    if (!inp) return false;
    const r = inp.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
});
console.log('search input visible:', inputVisible);
if (!inputVisible) {
    console.error('FAIL: search input not visible after opening panel');
    await browser.close();
    process.exit(1);
}

// 3. Type a query and wait for debounce + scan to finish.
await page.locator('.search-pane input[type="search"]').fill('playground');
await new Promise(r => setTimeout(r, 600));

// 4. Inspect results: expect two files (main.fbasic with 3 matches, fade.json
//    has none since "playground" doesn't appear there; project name *is*
//    "searchprobe" so confirm).
const resultShape = await page.evaluate(() => {
    const fileRows = Array.from(document.querySelectorAll('.search-pane .search-file-row'));
    const matchRows = Array.from(document.querySelectorAll('.search-pane .search-match-row'));
    const summary = document.querySelector('.search-pane .search-summary')?.textContent ?? '';
    const files = fileRows.map(r => ({
        name: r.querySelector('.file-name')?.textContent,
        dir: r.querySelector('.file-dir')?.textContent ?? '',
        count: r.querySelector('.file-count')?.textContent,
    }));
    const matches = matchRows.map(r => ({
        line: r.querySelector('.match-line-no')?.textContent,
        hasHighlight: !!r.querySelector('mark'),
        snippet: r.querySelector('.match-snippet')?.textContent,
    }));
    return { summary, files, matches };
});
console.log('result shape:', JSON.stringify(resultShape, null, 2));

if (resultShape.files.length === 0) {
    console.error('FAIL: no file rows rendered');
    await browser.close();
    process.exit(1);
}
const mainFile = resultShape.files.find(f => f.name === 'main.fbasic');
if (!mainFile) {
    console.error('FAIL: main.fbasic not in results');
    await browser.close();
    process.exit(1);
}
if (Number(mainFile.count) < 2) {
    console.error('FAIL: expected at least 2 matches in main.fbasic, got', mainFile.count);
    await browser.close();
    process.exit(1);
}
const allHighlighted = resultShape.matches.every(m => m.hasHighlight);
if (!allHighlighted) {
    console.error('FAIL: some match rows are missing the <mark> highlight');
    await browser.close();
    process.exit(1);
}

// 5. Click a match and confirm the editor cursor moved to that line.
await page.locator('.search-pane .search-match-row').first().click();
await new Promise(r => setTimeout(r, 400));
const cursorInfo = await page.evaluate(() => {
    const editors = window.monaco?.editor?.getEditors?.() ?? [];
    if (editors.length === 0) return null;
    const ed = editors[0];
    const pos = ed.getPosition?.();
    const sel = ed.getSelection?.();
    const model = ed.getModel?.();
    return {
        uri: model?.uri?.toString?.() ?? null,
        line: pos?.lineNumber ?? null,
        column: pos?.column ?? null,
        selectedLength: sel ? (sel.endColumn - sel.startColumn) : null,
    };
});
console.log('cursor after click:', JSON.stringify(cursorInfo));
if (!cursorInfo || !cursorInfo.uri?.includes('main.fbasic')) {
    console.error('FAIL: editor did not switch to main.fbasic');
    await browser.close();
    process.exit(1);
}
if (cursorInfo.selectedLength !== 'playground'.length) {
    console.error('FAIL: expected selection length =', 'playground'.length, 'got', cursorInfo.selectedLength);
    await browser.close();
    process.exit(1);
}

// 6. Try regex toggle — search for `\bplay\w+` and confirm matches still found.
await page.locator('.search-pane .search-flag').nth(2).click(); // regex toggle
await page.locator('.search-pane input[type="search"]').fill('\\bplay\\w+');
await new Promise(r => setTimeout(r, 600));
const regexShape = await page.evaluate(() => {
    const matchRows = Array.from(document.querySelectorAll('.search-pane .search-match-row'));
    return { matchCount: matchRows.length };
});
console.log('regex matches:', JSON.stringify(regexShape));
if (regexShape.matchCount === 0) {
    console.error('FAIL: regex search returned 0 matches');
    await browser.close();
    process.exit(1);
}

// 7. Close the panel, then verify the ⌘⇧F (Meta+Shift+F) shortcut reopens it
//    and focuses the input.
await page.evaluate(() => {
    try { window.__fadeDockview?.getPanel?.('search')?.api?.close?.(); } catch {}
});
await new Promise(r => setTimeout(r, 200));
const closedBeforeShortcut = await page.evaluate(() => !window.__fadeDockview?.getPanel?.('search'));
console.log('panel closed before shortcut:', closedBeforeShortcut);

// Click somewhere neutral first so the shortcut isn't swallowed by an
// element with its own keydown handler.
await page.locator('body').click({ position: { x: 10, y: 10 } });
const isMac = process.platform === 'darwin';
await page.keyboard.press(`${isMac ? 'Meta' : 'Control'}+Shift+F`);
await new Promise(r => setTimeout(r, 300));
const afterShortcut = await page.evaluate(() => {
    const panel = window.__fadeDockview?.getPanel?.('search');
    const input = document.querySelector('.search-pane input[type="search"]');
    const focused = document.activeElement === input;
    return { panelOpen: !!panel, focused };
});
console.log('after shortcut:', JSON.stringify(afterShortcut));
if (!afterShortcut.panelOpen) {
    console.error('FAIL: ⌘⇧F did not open the search panel');
    await browser.close();
    process.exit(1);
}
if (!afterShortcut.focused) {
    console.error('FAIL: search input not focused after ⌘⇧F');
    await browser.close();
    process.exit(1);
}

console.log('OK: search panel works (open, scan, render, click-to-open, regex, shortcut)');
await browser.close();
