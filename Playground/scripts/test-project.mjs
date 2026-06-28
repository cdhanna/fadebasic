// Phase 2 integration probes: fade.json validation, project source concat,
// locked manifest semantics, header label, and Problems integration.
//
// Usage: node scripts/test-project.mjs

import { chromium } from 'playwright';

const url = process.argv[2] || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1500, height: 950 } });
const page = await context.newPage();

const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e));

// Reset OPFS between runs so flake from previous tests can't leak.
await page.goto(url, { waitUntil: 'domcontentloaded' });
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    try { await root.removeEntry('workspace', { recursive: true }); } catch { /* ignore */ }
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
// Settling reload to avoid HMR double-bootstrap pollution.
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
await new Promise((r) => setTimeout(r, 1500));

async function readFadeJson() {
    return await page.evaluate(async () => {
        const m = window.monaco.editor.getModels().find((m) => m.uri.toString().endsWith('/fade.json'));
        return m ? m.getValue() : null;
    });
}
async function writeFadeJson(json) {
    await page.evaluate(({ json }) => {
        const m = window.monaco.editor.getModels().find((m) => m.uri.toString().endsWith('/fade.json'));
        if (m) m.applyEdits([{ range: m.getFullModelRange(), text: json }]);
    }, { json });
    await new Promise((r) => setTimeout(r, 1200));
}

const tests = [];
function test(name, fn) { tests.push({ name, fn }); }

test('boot: fade.json synthesized in default project folder', async () => {
    const text = await readFadeJson();
    if (!text) throw new Error('no fade.json model present');
    const obj = JSON.parse(text);
    if (obj.type !== 'web') throw new Error('type should be "web", got ' + obj.type);
    if (!Array.isArray(obj.sources) || obj.sources.length === 0) {
        throw new Error('sources should be a non-empty array');
    }
    return { name: obj.name, sources: obj.sources };
});

test('header shows the active project name', async () => {
    const label = (await page.locator('#project-name').textContent()) || '';
    if (!label.trim()) throw new Error('project-name label is empty');
    return { label: label.trim() };
});

test('default project lists main.fbasic + fade.json in file list', async () => {
    const names = await page.locator('#file-list li').evaluateAll((els) =>
        els.map((e) => (e.dataset.name || e.textContent || '').trim().split('\n')[0]),
    );
    if (!names.some((n) => /fade\.json/.test(n))) throw new Error('fade.json missing from file list: ' + names.join(','));
    if (!names.some((n) => /main\.fbasic/.test(n))) throw new Error('main.fbasic missing: ' + names.join(','));
    return { names };
});

test('fade.json shows a lock badge in the file list', async () => {
    const hasLock = await page.locator('#file-list li.manifest .file-lock').count();
    if (hasLock === 0) throw new Error('lock indicator missing on manifest row');
    return { hasLock };
});

test('schema error in fade.json surfaces in Problems', async () => {
    await writeFadeJson('{ "name": "demo", "type": "native", "sources": ["main.fbasic"] }');
    // Activate Problems so the items are visible.
    await page.evaluate(() => window.__fadeDockview?.getPanel?.('problems')?.api?.setActive?.());
    await new Promise((r) => setTimeout(r, 400));
    const probTexts = await page.locator('#problems-list .problem-item').evaluateAll((els) =>
        els.map((e) => e.textContent || ''),
    );
    const hasTypeIssue = probTexts.some((t) => /type/.test(t) && /web/.test(t));
    if (!hasTypeIssue) throw new Error('Expected a "type" enum error in Problems: ' + JSON.stringify(probTexts));
    return { count: probTexts.length };
});

test('valid fade.json clears the schema problems', async () => {
    await writeFadeJson('{ "name": "demo", "type": "web", "sources": ["main.fbasic"] }');
    await new Promise((r) => setTimeout(r, 400));
    const probTexts = await page.locator('#problems-list .problem-item').evaluateAll((els) =>
        els.map((e) => e.textContent || ''),
    );
    const stillBroken = probTexts.some((t) => /fade\.json/.test(t));
    if (stillBroken) throw new Error('Schema errors should be gone: ' + JSON.stringify(probTexts));
    return { ok: true };
});

test('multi-file source: getProjectSource concats in fade.json order', async () => {
    // Seed main.fbasic via Monaco model edit; create util.fbasic through
    // the new dropdown → inline-create flow so it lands in OPFS + tabs.
    await page.evaluate(() => {
        const main = window.monaco.editor.getModels().find((m) => m.uri.toString().endsWith('/main.fbasic'));
        main.applyEdits([{ range: main.getFullModelRange(), text: 'print "A"\n' }]);
    });
    await createFileViaDropdown('util.fbasic');
    await page.evaluate(() => {
        const m = window.monaco.editor.getModels().find((m) => m.uri.toString().endsWith('/util.fbasic'));
        m.applyEdits([{ range: m.getFullModelRange(), text: 'print "B"\n' }]);
    });
    await new Promise((r) => setTimeout(r, 800)); // let save timers flush

    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["main.fbasic","util.fbasic"] }');
    const forward = await page.evaluate(() => window.__fadeRunnerHelpers.project.getSource());
    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["util.fbasic","main.fbasic"] }');
    const reverse = await page.evaluate(() => window.__fadeRunnerHelpers.project.getSource());

    const idxA_fwd = forward.indexOf('"A"');
    const idxB_fwd = forward.indexOf('"B"');
    const idxA_rev = reverse.indexOf('"A"');
    const idxB_rev = reverse.indexOf('"B"');
    if (idxA_fwd === -1 || idxB_fwd === -1 || idxA_rev === -1 || idxB_rev === -1) {
        throw new Error('Both prints should appear in both concats. forward=' + JSON.stringify(forward) + ' reverse=' + JSON.stringify(reverse));
    }
    if (!(idxA_fwd < idxB_fwd)) throw new Error('Forward order should be A then B: ' + JSON.stringify(forward));
    if (!(idxB_rev < idxA_rev)) throw new Error('Reverse order should be B then A: ' + JSON.stringify(reverse));
    return { forward: forward.replace(/\s+/g, ' ').slice(0, 60), reverse: reverse.replace(/\s+/g, ' ').slice(0, 60) };
});

test('schema errors push Monaco markers on fade.json (squiggles)', async () => {
    await writeFadeJson('{ "name":"demo", "type":"native", "sources":[] }');
    await new Promise((r) => setTimeout(r, 600));
    const markers = await page.evaluate(() => {
        const uri = window.monaco.Uri.file('/workspace/fade.json');
        const model = window.monaco.editor.getModel(uri);
        if (!model) return null;
        return window.monaco.editor.getModelMarkers({ resource: uri }).map((m) => ({
            owner: m.owner, severity: m.severity, message: m.message,
            line: m.startLineNumber, col: m.startColumn,
        }));
    });
    if (!Array.isArray(markers) || markers.length === 0) {
        throw new Error('expected Monaco markers on fade.json, got: ' + JSON.stringify(markers));
    }
    const fromConfig = markers.filter((m) => m.owner === 'fade-config');
    if (fromConfig.length === 0) throw new Error('expected fade-config markers');
    const hasTypeMarker = fromConfig.some((m) => /type/.test(m.message));
    if (!hasTypeMarker) throw new Error('expected a "type" enum marker: ' + JSON.stringify(fromConfig));
    return { count: fromConfig.length };
});

test('valid fade.json clears error-severity markers', async () => {
    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["main.fbasic"] }');
    await new Promise((r) => setTimeout(r, 600));
    // Filter to error-severity only — orphan-source warnings may still
    // legitimately exist if other .fbasic files were left behind by an
    // earlier test in this run.
    const errors = await page.evaluate(() => {
        const uri = window.monaco.Uri.file('/workspace/fade.json');
        return window.monaco.editor.getModelMarkers({ resource: uri })
            .filter((m) => m.owner === 'fade-config' && m.severity === window.monaco.MarkerSeverity.Error);
    });
    if (errors.length !== 0) throw new Error('error markers should be empty: ' + JSON.stringify(errors));
    return { ok: true };
});

test('cross-check: missing source file produces an error', async () => {
    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["main.fbasic","does_not_exist.fbasic"] }');
    await new Promise((r) => setTimeout(r, 700));
    const errors = await page.evaluate(() => {
        const uri = window.monaco.Uri.file('/workspace/fade.json');
        return window.monaco.editor.getModelMarkers({ resource: uri })
            .filter((m) => m.owner === 'fade-config' && m.severity === window.monaco.MarkerSeverity.Error)
            .map((m) => m.message);
    });
    if (!errors.some((m) => /does_not_exist/.test(m))) {
        throw new Error('expected an error mentioning the missing file: ' + JSON.stringify(errors));
    }
    return { count: errors.length };
});

test('cross-check: orphan .fbasic produces NO diagnostic', async () => {
    // Only main is listed; util exists in OPFS. We intentionally do NOT
    // warn — unlisted source files are a normal iteration pattern. The
    // dash badge in the file list carries that signal already.
    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["main.fbasic"] }');
    await new Promise((r) => setTimeout(r, 700));
    const markers = await page.evaluate(() => {
        const uri = window.monaco.Uri.file('/workspace/fade.json');
        return window.monaco.editor.getModelMarkers({ resource: uri })
            .filter((m) => m.owner === 'fade-config')
            .map((m) => ({ sev: m.severity, msg: m.message }));
    });
    if (markers.some((m) => /not listed in sources/.test(m.msg))) {
        throw new Error('orphan warning should NOT exist: ' + JSON.stringify(markers));
    }
    return { markerCount: markers.length };
});

test('print output does not duplicate after run completes', async () => {
    await page.evaluate(() => {
        const main = window.monaco.editor.getModels().find((m) => m.uri.toString().endsWith('/main.fbasic'));
        main.applyEdits([{ range: main.getFullModelRange(), text: 'print "alpha"\nprint "beta"\n' }]);
    });
    await new Promise((r) => setTimeout(r, 800));
    await page.evaluate(() => document.getElementById('output-clear')?.click());
    await page.evaluate(() => window.__fadeDockview?.getPanel?.('output')?.api?.setActive?.());
    await new Promise((r) => setTimeout(r, 200));
    await page.evaluate(() => {
        const btns = Array.from(document.querySelectorAll('vscode-button, button'));
        const run = btns.find((b) => /Run \(/.test(b.textContent || ''));
        if (run) run.click();
    });
    await page.waitForFunction(
        () => Array.from(document.querySelectorAll('#output .output-line'))
            .some((l) => /beta/.test(l.textContent)),
        { timeout: 10000 },
    );
    // Wait a beat to allow any (incorrect) duplicate emit to land.
    await new Promise((r) => setTimeout(r, 800));
    const lines = await page.evaluate(() =>
        Array.from(document.querySelectorAll('#output .output-line'))
            .map((l) => (l.textContent || '').trim())
            .filter(Boolean),
    );
    const alphaCount = lines.filter((l) => /alpha/.test(l)).length;
    const betaCount = lines.filter((l) => /beta/.test(l)).length;
    if (alphaCount !== 1 || betaCount !== 1) {
        throw new Error('Expected one "alpha" and one "beta" line, got: ' + JSON.stringify(lines));
    }
    return { lines };
});

test('file list shows numeric source badge for listed .fbasic files', async () => {
    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["main.fbasic","util.fbasic"] }');
    await new Promise((r) => setTimeout(r, 600));
    const badges = await page.locator('#file-list li[data-name$=".fbasic"] .source-badge')
        .evaluateAll((els) => els.map((e) => ({
            file: e.parentElement?.dataset.name,
            text: (e.textContent || '').trim(),
            listed: e.classList.contains('listed'),
            orphan: e.classList.contains('orphan'),
        })));
    const main = badges.find((b) => b.file === 'main.fbasic');
    const util = badges.find((b) => b.file === 'util.fbasic');
    if (!main || main.text !== '1' || !main.listed) throw new Error('main badge wrong: ' + JSON.stringify(main));
    if (!util || util.text !== '2' || !util.listed) throw new Error('util badge wrong: ' + JSON.stringify(util));
    return { badges };
});

test('orphan .fbasic shows dash badge', async () => {
    // Drop util from sources; util.fbasic still exists in OPFS → orphan.
    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["main.fbasic"] }');
    await new Promise((r) => setTimeout(r, 600));
    const badge = await page.locator('#file-list li[data-name="util.fbasic"] .source-badge').first();
    const cls = await badge.getAttribute('class');
    const text = (await badge.textContent() || '').trim();
    if (!/orphan/.test(cls || '')) throw new Error('expected orphan class: ' + cls);
    if (text !== '–' && text !== '-') throw new Error('expected dash, got: ' + text);
    return { cls };
});

// Helper: open the file-row right-click menu and click the matching item.
async function clickFileContextItem(fileName, itemPattern) {
    const row = page.locator(`#file-list li[data-name="${fileName}"]`);
    await row.dispatchEvent('contextmenu', {});
    await page.waitForSelector('.source-badge-menu[data-menu="file-context"]', { timeout: 3000 });
    await page.evaluate((rx) => {
        const re = new RegExp(rx);
        const item = Array.from(document.querySelectorAll('.source-badge-menu .source-badge-item'))
            .find((el) => re.test(el.textContent || ''));
        item?.click();
    }, itemPattern);
}

test('right-click → "Add to sources (end)" appends to fade.json', async () => {
    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["main.fbasic"] }');
    await new Promise((r) => setTimeout(r, 600));
    await clickFileContextItem('util.fbasic', 'Add to sources \\(end\\)');
    await page.waitForFunction(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/fade.json'));
        return /"util\.fbasic"/.test(m?.getValue() || '');
    }, { timeout: 5000 });
    await new Promise((r) => setTimeout(r, 500));
    const text = await page.locator('#file-list li[data-name="util.fbasic"] .source-badge').textContent();
    if ((text || '').trim() !== '2') throw new Error('util badge should be "2", got: ' + text);
    return { ok: true };
});

test('right-click → "Remove from sources" rewrites fade.json', async () => {
    await clickFileContextItem('util.fbasic', 'Remove from sources');
    await page.waitForFunction(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/fade.json'));
        try {
            const obj = JSON.parse(m?.getValue() || '');
            return Array.isArray(obj.sources) && !obj.sources.includes('util.fbasic');
        } catch { return false; }
    }, { timeout: 5000 });
    await new Promise((r) => setTimeout(r, 500));
    const cls = await page.locator('#file-list li[data-name="util.fbasic"] .source-badge').getAttribute('class');
    if (!/orphan/.test(cls || '')) throw new Error('expected orphan after removal, got: ' + cls);
    return { cls };
});

test('right-click → "Go to fade.json" reveals the source line', async () => {
    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["main.fbasic","util.fbasic"] }');
    await new Promise((r) => setTimeout(r, 600));
    await clickFileContextItem('util.fbasic', 'Go to fade\\.json');
    await new Promise((r) => setTimeout(r, 500));
    const position = await page.evaluate(() => {
        const ed = window.monaco.editor.getEditors().find((e) => /fade\.json/.test(e.getModel()?.uri.toString() || ''));
        if (!ed) return null;
        const p = ed.getPosition();
        const line = ed.getModel()?.getLineContent(p.lineNumber) || '';
        return { line, lineNumber: p.lineNumber, column: p.column };
    });
    if (!position) throw new Error('editor not on fade.json');
    if (!/util\.fbasic/.test(position.line)) {
        throw new Error('expected cursor on util.fbasic source entry: ' + JSON.stringify(position));
    }
    return position;
});

test('right-click → Rename moves the file + rewrites sources', async () => {
    // util.fbasic exists + is listed; rename to helper.fbasic.
    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["main.fbasic","util.fbasic"] }');
    await new Promise((r) => setTimeout(r, 600));
    page.once('dialog', (d) => d.accept('helper.fbasic'));
    await clickFileContextItem('util.fbasic', 'Rename');
    await page.waitForFunction(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/helper.fbasic'));
        return !!m;
    }, { timeout: 5000 });
    // Wait for the file list re-render (it happens after the model swap).
    await page.waitForFunction(
        () => Array.from(document.querySelectorAll('#file-list li')).some((l) => l.dataset.name === 'helper.fbasic'),
        { timeout: 5000 },
    );
    await new Promise((r) => setTimeout(r, 400));
    // Old name gone, new name present.
    const names = await page.locator('#file-list li').evaluateAll((els) =>
        els.map((e) => e.dataset.name).filter(Boolean),
    );
    if (names.includes('util.fbasic')) throw new Error('util.fbasic should be gone after rename');
    if (!names.includes('helper.fbasic')) throw new Error('helper.fbasic should be present');
    // fade.json sources updated.
    const sources = await page.evaluate(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/fade.json'));
        try { return JSON.parse(m?.getValue() || '').sources; } catch { return []; }
    });
    if (!sources.includes('helper.fbasic') || sources.includes('util.fbasic')) {
        throw new Error('sources should reflect rename: ' + JSON.stringify(sources));
    }
    return { names, sources };
});

test('right-click → Delete removes file + entry from sources', async () => {
    // helper.fbasic from previous test; delete it.
    page.once('dialog', (d) => d.accept());   // confirm
    await clickFileContextItem('helper.fbasic', 'Delete');
    await page.waitForFunction(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/helper.fbasic'));
        return !m;
    }, { timeout: 5000 });
    const names = await page.locator('#file-list li').evaluateAll((els) =>
        els.map((e) => e.dataset.name).filter(Boolean),
    );
    if (names.includes('helper.fbasic')) throw new Error('helper.fbasic should be gone');
    const sources = await page.evaluate(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/fade.json'));
        try { return JSON.parse(m?.getValue() || '').sources; } catch { return []; }
    });
    if (sources.includes('helper.fbasic')) {
        throw new Error('sources should not list deleted file: ' + JSON.stringify(sources));
    }
    return { names, sources };
});

test('right-click on fade.json shows no menu (locked)', async () => {
    const row = page.locator('#file-list li[data-name="fade.json"]');
    await row.dispatchEvent('contextmenu', {});
    await new Promise((r) => setTimeout(r, 300));
    const count = await page.locator('.source-badge-menu[data-menu="file-context"]').count();
    if (count !== 0) throw new Error('fade.json should not open a file-context menu');
    return { ok: true };
});

// Drive the new dropdown + inline-create flow. Picks the .fbasic entry,
// types the requested name, and presses Enter.
async function createFileViaDropdown(name) {
    await page.locator('#new-file').click();
    await page.waitForSelector('.source-badge-menu[data-menu="file-context"]', { timeout: 3000 });
    await page.evaluate(() => {
        const items = Array.from(document.querySelectorAll('.source-badge-menu .source-badge-item'));
        const ext = name => /(\.fbasic|\.fb)$/i;
        // Always pick the first item that matches the requested extension.
        // Fall back to first item if no match.
        const item = items[0];
        item?.click();
    });
    await page.waitForSelector('#file-list li.file-edit-row input', { timeout: 3000 });
    await page.fill('#file-list li.file-edit-row input', name);
    await page.keyboard.press('Enter');
    await page.waitForFunction(
        (n) => Array.from(document.querySelectorAll('#file-list li'))
            .some((l) => l.dataset.name === n),
        name,
        { timeout: 5000 },
    );
}

test('new .fbasic file auto-appends to fade.json sources (via dropdown)', async () => {
    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["main.fbasic"] }');
    await new Promise((r) => setTimeout(r, 600));
    await createFileViaDropdown('autoadded.fbasic');
    await page.waitForFunction(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/fade.json'));
        return /"autoadded\.fbasic"/.test(m?.getValue() || '');
    }, { timeout: 5000 });
    const sources = await page.evaluate(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/fade.json'));
        return JSON.parse(m.getValue()).sources;
    });
    if (sources[sources.length - 1] !== 'autoadded.fbasic') {
        throw new Error('new fbasic should land at end of sources: ' + JSON.stringify(sources));
    }
    return { sources };
});

test('new-file dropdown: invalid name silently discards the row', async () => {
    await page.locator('#new-file').click();
    await page.waitForSelector('.source-badge-menu[data-menu="file-context"]', { timeout: 3000 });
    await page.evaluate(() => {
        const items = Array.from(document.querySelectorAll('.source-badge-menu .source-badge-item'));
        items[3]?.click(); // .txt
    });
    await page.waitForSelector('#file-list li.file-edit-row input', { timeout: 3000 });
    // Invalid name (contains a space) — silent discard expected.
    await page.fill('#file-list li.file-edit-row input', 'bad name.txt');
    await page.keyboard.press('Enter');
    await new Promise((r) => setTimeout(r, 400));
    const rowGone = await page.locator('#file-list li.file-edit-row').count();
    if (rowGone !== 0) throw new Error('edit row should be removed after invalid Enter');
    const persisted = await page.locator('#file-list li[data-name="bad name.txt"]').count();
    if (persisted !== 0) throw new Error('invalid name should not be saved');
    return { ok: true };
});

test('new-file dropdown: Escape cancels without writing anything', async () => {
    const beforeCount = await page.locator('#file-list li').count();
    await page.locator('#new-file').click();
    await page.waitForSelector('.source-badge-menu[data-menu="file-context"]', { timeout: 3000 });
    await page.evaluate(() => {
        const items = Array.from(document.querySelectorAll('.source-badge-menu .source-badge-item'));
        items[0]?.click(); // .fbasic
    });
    await page.waitForSelector('#file-list li.file-edit-row input', { timeout: 3000 });
    await page.keyboard.press('Escape');
    await new Promise((r) => setTimeout(r, 300));
    const afterCount = await page.locator('#file-list li').count();
    if (afterCount !== beforeCount) {
        throw new Error('Escape should not add a row, before=' + beforeCount + ' after=' + afterCount);
    }
    return { beforeCount, afterCount };
});

test('right-click workspace empty area opens new-file dropdown', async () => {
    // Trigger contextmenu on the workspace pane host (outside any file row).
    await page.evaluate(() => {
        const host = document.querySelector('.sidebar-host');
        const rect = host.getBoundingClientRect();
        const ev = new MouseEvent('contextmenu', {
            bubbles: true, cancelable: true,
            clientX: rect.left + rect.width / 2,
            clientY: rect.bottom - 10,
        });
        host.dispatchEvent(ev);
    });
    const menuOpen = await page.locator('.source-badge-menu[data-menu="file-context"]').count();
    if (menuOpen !== 1) throw new Error('workspace empty-area right-click should open the dropdown');
    // Close + leave clean state.
    await page.keyboard.press('Escape');
    await new Promise((r) => setTimeout(r, 200));
    return { ok: true };
});

test('reload respects fade.json source order for default-opened file', async () => {
    // Create a second .fbasic via the dropdown, then promote it to be
    // the only listed source. After reload, the editor should land on
    // it (not on the alphabetically-first file).
    const alphaExists = await page.locator('#file-list li[data-name="alpha.fbasic"]').count();
    if (!alphaExists) {
        await createFileViaDropdown('alpha.fbasic');
    }
    await page.evaluate(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/alpha.fbasic'));
        m.applyEdits([{ range: m.getFullModelRange(), text: 'print "alpha is special"\n' }]);
    });
    await new Promise((r) => setTimeout(r, 800));
    // Edit fade.json via the open flow so the save listener fires.
    await page.locator('#file-list li[data-name="fade.json"]').click();
    await new Promise((r) => setTimeout(r, 200));
    await page.evaluate(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/fade.json'));
        m.applyEdits([{
            range: m.getFullModelRange(),
            text: '{ "name":"demo", "type":"web", "sources":["alpha.fbasic"] }',
        }]);
    });
    await new Promise((r) => setTimeout(r, 1200));
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
    await new Promise((r) => setTimeout(r, 1500));
    const active = await page.evaluate(() => {
        const eds = window.monaco.editor.getEditors();
        return eds[0]?.getModel()?.uri.toString();
    });
    if (!/alpha\.fbasic/.test(active || '')) throw new Error('expected alpha.fbasic as default, got: ' + active);
    // And it should be tokenized as fade.
    const hasFadeTokens = await page.evaluate(() =>
        Array.from(document.querySelectorAll('.monaco-editor .view-lines .view-line span'))
            .some((s) => Array.from(s.classList).some((c) => c.startsWith('fade-token-'))),
    );
    if (!hasFadeTokens) throw new Error('alpha.fbasic should have fade syntax highlighting');
    return { active };
});

test('switching to a not-yet-opened source tab shows highlighting immediately', async () => {
    // Make sure util.fbasic exists (earlier tests may have renamed/deleted it).
    const utilExists = await page.locator('#file-list li[data-name="util.fbasic"]').count();
    if (!utilExists) {
        await createFileViaDropdown('util.fbasic');
    }
    // Create util.fbasic with content that contains tokens we can recognize
    // (keyword `print`, string literal). Then make sure main.fbasic stays
    // active. The first activation of util.fbasic must already carry the
    // semantic-token decorations — no edit required.
    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["main.fbasic","util.fbasic"] }');
    await new Promise((r) => setTimeout(r, 600));
    await page.evaluate(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/util.fbasic'));
        m.applyEdits([{ range: m.getFullModelRange(), text: 'print "highlight me"\n' }]);
    });
    // Switch the active tab back to main.fbasic.
    await page.locator('#file-list li[data-name="main.fbasic"]').click();
    await new Promise((r) => setTimeout(r, 500));
    // Now click util.fbasic — this is the "first time activated" scenario.
    await page.locator('#file-list li[data-name="util.fbasic"]').click();
    // Brief settle so applySemanticTokens has a chance to run.
    await new Promise((r) => setTimeout(r, 600));
    const tokenInfo = await page.evaluate(() => {
        // Look at the visible view-lines: every line should have at least
        // one fade-token-* class somewhere if semantic tokens applied.
        const spans = Array.from(document.querySelectorAll('.monaco-editor .view-lines .view-line span'));
        const classes = new Set();
        for (const s of spans) for (const c of s.classList) classes.add(c);
        const fadeClasses = Array.from(classes).filter((c) => c.startsWith('fade-token-'));
        return { fadeClasses, activeUri: window.monaco.editor.getEditors()[0]?.getModel()?.uri.toString() };
    });
    if (!/util\.fbasic/.test(tokenInfo.activeUri || '')) {
        throw new Error('expected util.fbasic active, got: ' + tokenInfo.activeUri);
    }
    if (tokenInfo.fadeClasses.length === 0) {
        throw new Error('util.fbasic should have semantic-token decorations on first open, got: ' + JSON.stringify(tokenInfo));
    }
    return tokenInfo;
});

test('closing then reopening a fbasic tab does not throw', async () => {
    const utilExists = await page.locator('#file-list li[data-name="util.fbasic"]').count();
    if (!utilExists) {
        await createFileViaDropdown('util.fbasic');
    }
    // Make sure util.fbasic exists and is openable.
    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["main.fbasic","util.fbasic"] }');
    await new Promise((r) => setTimeout(r, 400));
    // Open util.fbasic from the file list, then close its tab.
    await page.locator('#file-list li[data-name="util.fbasic"]').click();
    await new Promise((r) => setTimeout(r, 200));
    await page.evaluate(() => {
        const tab = Array.from(document.querySelectorAll('.tab')).find((t) => /util\.fbasic/.test(t.textContent || ''));
        tab?.querySelector('.close')?.click();
    });
    await new Promise((r) => setTimeout(r, 200));
    // Capture page errors during the next open attempt.
    const errs = [];
    const handler = (e) => errs.push(e.message);
    page.on('pageerror', handler);
    await page.locator('#file-list li[data-name="util.fbasic"]').click();
    await new Promise((r) => setTimeout(r, 400));
    page.off('pageerror', handler);
    const dupErr = errs.find((m) => /already exists/.test(m));
    if (dupErr) throw new Error('reopen still crashes: ' + dupErr);
    // util.fbasic should be active again.
    const activeTab = await page.evaluate(() => {
        const active = document.querySelector('.tab.active');
        return active?.textContent || '';
    });
    if (!/util\.fbasic/.test(activeTab)) throw new Error('reopened tab should be active: ' + activeTab);
    return { activeTab };
});

test('compile error appears once (Problems) and not duplicated in Output', async () => {
    // Type a parse-error source into main.fbasic, click Run, then count
    // how many times the error message shows up across Problems + Output.
    await writeFadeJson('{ "name":"demo", "type":"web", "sources":["main.fbasic"] }');
    await new Promise((r) => setTimeout(r, 400));
    await page.evaluate(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/main.fbasic'));
        m.applyEdits([{ range: m.getFullModelRange(), text: 'asdf qwert\n' }]);
    });
    await new Promise((r) => setTimeout(r, 1200));
    await page.evaluate(() => document.getElementById('output-clear')?.click());
    await page.evaluate(() => {
        const btns = Array.from(document.querySelectorAll('vscode-button, button'));
        btns.find((b) => /Run \(/.test(b.textContent || ''))?.click();
    });
    await new Promise((r) => setTimeout(r, 2000));
    const counts = await page.evaluate(() => {
        const problems = Array.from(document.querySelectorAll('#problems-list .problem-item'))
            .filter((li) => /ambiguous|expression|0107/i.test(li.textContent || '')).length;
        const output = Array.from(document.querySelectorAll('#output .output-line'))
            .filter((l) => /ambiguous|expression|0107/i.test(l.textContent || '')).length;
        return { problems, output };
    });
    if (counts.problems < 1) throw new Error('expected the parse error in Problems, got: ' + JSON.stringify(counts));
    if (counts.output !== 0) {
        throw new Error('parse error should not also be dumped in Output: ' + JSON.stringify(counts));
    }
    return counts;
});

test('lock: $schema line in fade.json reverts targeted edits', async () => {
    // Make sure fade.json has the canonical layout with a $schema line.
    await writeFadeJson(`{
  "$schema": "/fade.schema.json",
  "name": "demo",
  "type": "web",
  "sources": ["main.fbasic"]
}
`);
    await new Promise((r) => setTimeout(r, 800));
    // Try to surgically replace ONLY the $schema line — guard should revert.
    const result = await page.evaluate(() => {
        const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/fade.json'));
        // Locate the $schema line.
        let schemaLine = -1;
        for (let i = 1; i <= m.getLineCount(); i++) {
            if (/\$schema/.test(m.getLineContent(i))) { schemaLine = i; break; }
        }
        if (schemaLine < 0) return { failed: 'no $schema line in initial state' };
        const lineLen = m.getLineMaxColumn(schemaLine);
        const Range = window.monaco.Range;
        m.applyEdits([{
            range: new Range(schemaLine, 1, schemaLine, lineLen),
            text: '  "$schema": "hijacked",',
        }]);
        const after = m.getLineContent(schemaLine);
        return { after };
    });
    if (result.failed) throw new Error(result.failed);
    if (/hijacked/.test(result.after)) {
        throw new Error('schema line should have been reverted, got: ' + result.after);
    }
    return result;
});

test('lock: typing "fade.json" into the inline-create row silently discards', async () => {
    await page.locator('#new-file').click();
    await page.waitForSelector('.source-badge-menu[data-menu="file-context"]', { timeout: 3000 });
    await page.evaluate(() => {
        const items = Array.from(document.querySelectorAll('.source-badge-menu .source-badge-item'));
        items[2]?.click(); // .json
    });
    await page.waitForSelector('#file-list li.file-edit-row input', { timeout: 3000 });
    await page.fill('#file-list li.file-edit-row input', 'fade.json');
    await page.keyboard.press('Enter');
    await new Promise((r) => setTimeout(r, 400));
    // No second fade.json should be written. Count fade.json rows: still
    // exactly the original one (the manifest), edit row gone.
    const count = await page.locator('#file-list li[data-name="fade.json"]').count();
    if (count !== 1) throw new Error('fade.json count should stay at 1, got ' + count);
    const rowGone = await page.locator('#file-list li.file-edit-row').count();
    if (rowGone !== 0) throw new Error('edit row should be removed');
    return { count };
});

let passed = 0, failed = 0;
for (const t of tests) {
    process.stdout.write(`• ${t.name} ... `);
    try {
        const r = await t.fn();
        console.log('OK', r ? JSON.stringify(r) : '');
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
