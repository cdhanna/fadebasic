// Verifies the Settings panel: ⌘, opens it, the User form changes editor
// font-size live, the Workspace tab writes to <project>/.fade/settings.json,
// and the JSON editor round-trips edits back into the form.

import { chromium } from 'playwright';

const URL = process.env.URL || 'http://localhost:5311/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 240)));

await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });

// Seed a known project; reload so settings/init paths fire from a clean slate.
await page.evaluate(async () => {
    localStorage.removeItem('fade.settings.user.v1');
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('settingsprobe', { create: true });
    // Wipe any leftover .fade/settings.json from a previous run.
    try {
        const fade = await dir.getDirectoryHandle('.fade');
        await fade.removeEntry('settings.json').catch(() => {});
    } catch {}
    const fh = await dir.getFileHandle('fade.json', { create: true });
    const w = await fh.createWritable();
    await w.write(JSON.stringify({
        $schema: '/fade.schema.json', name: 'settingsprobe', type: 'web',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2));
    await w.close();
    const mfh = await dir.getFileHandle('main.fbasic', { create: true });
    const mw = await mfh.createWritable();
    await mw.write('print "hi"\n'); await mw.close();
    localStorage.setItem('fade.activeProject', 'settingsprobe');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 30_000 });
await new Promise(r => setTimeout(r, 2000));

// 1. Press ⌘, to open the settings panel.
const isMac = process.platform === 'darwin';
await page.locator('body').click({ position: { x: 10, y: 10 } });
await page.keyboard.press(`${isMac ? 'Meta' : 'Control'}+Comma`);
await new Promise(r => setTimeout(r, 300));
const opened = await page.evaluate(() => !!window.__fadeDockview?.getPanel?.('settings'));
console.log('settings panel opened via shortcut:', opened);
if (!opened) {
    console.error('FAIL: ⌘, did not open the settings panel');
    await browser.close(); process.exit(1);
}

// 2. The User tab should be active by default. Confirm a field shows up.
const initialFontSize = await page.evaluate(() => {
    const f = document.querySelector('.settings-pane [data-key="editor.fontSize"] input');
    return f ? (f).value : null;
});
console.log('initial editor.fontSize on the form:', initialFontSize);
if (initialFontSize == null) {
    console.error('FAIL: editor.fontSize field not rendered'); await browser.close(); process.exit(1);
}

// 3. Change font size — the editor should re-apply immediately.
await page.locator('.settings-pane [data-key="editor.fontSize"] input').fill('22');
await page.locator('.settings-pane [data-key="editor.fontSize"] input').press('Tab');
await new Promise(r => setTimeout(r, 250));
const liveFont = await page.evaluate(() => {
    const ed = window.monaco?.editor?.getEditors?.()[0];
    return ed?.getOption?.(window.monaco.editor.EditorOption.fontInfo)?.fontSize ?? null;
});
console.log('editor font size after change:', liveFont);
if (liveFont !== 22) {
    console.error('FAIL: expected editor fontSize=22, got', liveFont);
    await browser.close(); process.exit(1);
}
const stored = await page.evaluate(() => JSON.parse(localStorage.getItem('fade.settings.user.v1') || '{}'));
console.log('user settings persisted:', JSON.stringify(stored));
if (stored['editor.fontSize'] !== 22) {
    console.error('FAIL: localStorage user settings did not persist editor.fontSize=22');
    await browser.close(); process.exit(1);
}

// 4. Switch to the Workspace tab and set tabSize=4.
await page.locator('.settings-pane .settings-tab').nth(1).click();
await new Promise(r => setTimeout(r, 150));
await page.locator('.settings-pane [data-key="editor.tabSize"] input').fill('4');
await page.locator('.settings-pane [data-key="editor.tabSize"] input').press('Tab');
await new Promise(r => setTimeout(r, 350));
const wsFile = await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const ws = await root.getDirectoryHandle('workspace');
    const dir = await ws.getDirectoryHandle('settingsprobe');
    const fade = await dir.getDirectoryHandle('.fade');
    const fh = await fade.getFileHandle('settings.json');
    return await (await fh.getFile()).text();
});
console.log('workspace .fade/settings.json:', wsFile);
const wsParsed = JSON.parse(wsFile);
if (wsParsed['editor.tabSize'] !== 4) {
    console.error('FAIL: workspace settings file missing editor.tabSize=4');
    await browser.close(); process.exit(1);
}

// 5. Confirm a model is using tabSize=4 (workspace overrides user).
const modelTab = await page.evaluate(() => {
    const m = window.monaco?.editor?.getModels?.()[0];
    return m ? m.getOptions().tabSize : null;
});
console.log('model tab size after workspace override:', modelTab);
if (modelTab !== 4) {
    console.error('FAIL: model tabSize did not pick up workspace override');
    await browser.close(); process.exit(1);
}

// 6. JSON view round-trip: click "Edit in settings.json →", change a value,
//    confirm form reflects it after going back.
await page.locator('.settings-pane .settings-tab').nth(0).click(); // User
await new Promise(r => setTimeout(r, 150));
await page.locator('.settings-pane .settings-link').click(); // Edit in settings.json
await new Promise(r => setTimeout(r, 600)); // monaco mount
// Replace the JSON entirely with a new fontSize value.
await page.evaluate(() => {
    const eds = window.monaco?.editor?.getEditors?.() ?? [];
    // The settings JSON editor is the most recently created — find it by
    // model language 'json' inside the settings pane.
    const settingsEd = eds.find((e) => e.getModel?.()?.getLanguageId?.() === 'json');
    if (!settingsEd) throw new Error('no json editor found');
    settingsEd.getModel().setValue('{ "editor.fontSize": 18 }');
});
await new Promise(r => setTimeout(r, 600)); // debounce + save
const afterJson = await page.evaluate(() => {
    const ed = window.monaco?.editor?.getEditors?.()
        .find((e) => e.getModel?.()?.getLanguageId?.() === 'fade');
    return ed?.getOption?.(window.monaco.editor.EditorOption.fontInfo)?.fontSize ?? null;
});
console.log('editor font after JSON edit:', afterJson);
if (afterJson !== 18) {
    console.error('FAIL: JSON-edit didn\'t propagate to the editor');
    await browser.close(); process.exit(1);
}

// 7. Theme toggle: switch to light, confirm the data-theme attr + Monaco
//    + dockview classes all swap. Step 6 left us in the JSON view; click
//    "Back to form" to get the GUI back.
await page.locator('.settings-pane .settings-link').click();
await new Promise(r => setTimeout(r, 200));
// The JSON-edit step above replaced user settings with just fontSize, so the
// theme selector should currently sit on the default 'dark'. Switch it.
await page.locator('.settings-pane [data-key="ui.theme"] select').selectOption('light');
await new Promise(r => setTimeout(r, 400));
const themeSnapshot = await page.evaluate(() => ({
    dataTheme: document.documentElement.dataset.theme,
    // Dockview's theme class lives on a `.dv-shell` child of #dock-root,
    // not on #dock-root itself.
    dockClass: document.querySelector('.dv-shell')?.className ?? '',
    // Monaco doesn't expose the active theme name directly; check the
    // body's vs-light class which monaco adds based on the active theme.
    monacoLight: document.querySelector('.monaco-editor.vs') != null,
}));
console.log('theme snapshot after switching to light:', JSON.stringify(themeSnapshot));
if (themeSnapshot.dataTheme !== 'light') {
    console.error('FAIL: html data-theme attribute not set to light');
    await browser.close(); process.exit(1);
}
if (!themeSnapshot.dockClass.includes('dockview-theme-light')) {
    console.error('FAIL: dockview did not switch to light class');
    await browser.close(); process.exit(1);
}
if (!themeSnapshot.monacoLight) {
    console.error('FAIL: Monaco did not adopt the light variant');
    await browser.close(); process.exit(1);
}

// 8. JSON editor stability: type into it without losing focus mid-edit.
//    Open the JSON view and type a character — the editor's text should
//    still be there after the debounce/save cycle fires.
await page.locator('.settings-pane .settings-link').click(); // Edit in settings.json
await new Promise(r => setTimeout(r, 600));
await page.evaluate(() => {
    const eds = window.monaco?.editor?.getEditors?.() ?? [];
    const settingsEd = eds.find((e) => e.getModel?.()?.getLanguageId?.() === 'json');
    settingsEd?.focus?.();
    settingsEd?.getModel()?.setValue('{\n  "ui.theme": "light",\n  "editor.fontSize": 19\n}');
});
await new Promise(r => setTimeout(r, 900)); // > debounce + a save round
const afterStability = await page.evaluate(() => {
    const eds = window.monaco?.editor?.getEditors?.() ?? [];
    const settingsEd = eds.find((e) => e.getModel?.()?.getLanguageId?.() === 'json');
    return {
        text: settingsEd?.getModel()?.getValue() ?? null,
        editorAlive: !!settingsEd,
    };
});
console.log('json editor after edit:', JSON.stringify(afterStability));
if (!afterStability.editorAlive || !afterStability.text?.includes('"editor.fontSize": 19')) {
    console.error('FAIL: JSON editor disappeared or lost content during edit');
    await browser.close(); process.exit(1);
}

console.log('OK: settings panel works (shortcut, live editor reapply, user+workspace split, JSON round-trip, theme, json-edit stability)');
await browser.close();
