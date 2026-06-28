// Verifies hover docs land for a Fade.MonoGame command when the active
// project is type='monogame'. Positions the Monaco cursor on `print` (in
// FadeMonoGameCommands it's the [FadeBasicCommand("print")] that has the
// XML <summary> "Prints one or more values to the console output."), then
// reads the hover contents via Monaco's HoverController.

import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const URL = process.env.URL || 'http://localhost:5312/';
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

page.on('console', msg => { if (msg.type() === 'error') console.log('[console.error]', msg.text().slice(0, 300)); });
page.on('pageerror', e => console.log('[pageerror]', e.message.slice(0, 300)));

console.log(`→ navigate ${URL}`);
await page.goto(URL, { waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

await page.evaluate(async () => {
    const opfs = await navigator.storage.getDirectory();
    const ws = await opfs.getDirectoryHandle('workspace', { create: true });
    const dir = await ws.getDirectoryHandle('mgtest', { create: true });
    const cfg = JSON.stringify({
        $schema: '/fade.schema.json', name: 'mgtest', type: 'monogame',
        commandDlls: [], sources: ['main.fbasic'],
    }, null, 2) + '\n';
    const cw = await (await dir.getFileHandle('fade.json', { create: true })).createWritable();
    await cw.write(cfg); await cw.close();
    // Probe `game ms()` — Fade.MonoGame.Lib only, very rich XML doc.
    const sw = await (await dir.getFileHandle('main.fbasic', { create: true })).createWritable();
    await sw.write('t = game ms()\n'); await sw.close();
    localStorage.setItem('fade.activeProject', 'mgtest');
});
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => /Ready/i.test(document.getElementById('status')?.textContent || ''), { timeout: 30_000 });

// Give the worker time to receive set-project-type + re-set the document.
await page.waitForTimeout(2500);

// Drive a Monaco hover at column 7 (inside the word "game") and read it.
const hoverContents = await page.evaluate(async () => {
    const models = window.monaco.editor.getModels();
    const target = models.find(m => /main\.fbasic$/.test(m.uri.toString()));
    if (!target) return { _err: 'no main.fbasic' };

    // Trigger the registered hover provider directly. Monaco's API exposes
    // `monaco.languages.HoverProviderRegistry.all(model)` only in editor-internal
    // namespaces; instead use the public `editor.invokeWithinContext` route —
    // OR just call the hover provider lookup via the language id.
    const provs = (window.monaco.languages.getHoverProvider?.(target.getLanguageId?.() ?? '')) ?? null;
    // Public Monaco doesn't expose getHoverProvider directly; use the side
    // door: any registered provider keeps a reference inside the editor's
    // private registry, which we can reach via:
    const langId = target.getLanguageId();
    const candidates = window.monaco.languages._modeService
        ?? window.monaco.languages.LanguageFeatureRegistry
        ?? null;
    if (!candidates) {
        // Fallback: synthesize the hover by directly invoking the registered
        // provider through `monaco.editor.getEditors()[0].trigger`.
        // Position cursor on (1,7) where "game" begins, then trigger Hover.
        const ed = window.monaco.editor.getEditors().find(e => e.getModel()?.uri.toString() === target.uri.toString());
        if (!ed) return { _err: 'no editor for main.fbasic' };
        ed.setPosition({ lineNumber: 1, column: 7 });
        // Wait a bit and read the hover content via the controller.
        ed.focus();
        ed.trigger('test', 'editor.action.showHover', {});
        await new Promise(r => setTimeout(r, 800));
        // The Monaco hover renders into a .monaco-hover element in the DOM.
        const hover = document.querySelector('.monaco-hover .markdown-hover')
            ?? document.querySelector('.monaco-hover');
        return { text: hover ? hover.textContent : null };
    }
    return { _note: 'fallback path not taken; LSP API surfaced' };
});

console.log('hover contents:', JSON.stringify(hoverContents, null, 2));

await browser.close();

const text = hoverContents.text ?? '';
// The basic signature header is "game ms" + "Returns DoubleFloat" — always
// present. The rich XML body says "Returns the total elapsed game time
// in milliseconds." We need that body to confirm the docs pipeline ran.
const hasSignature = /game\s*ms/i.test(text);
const hasRichBody = /elapsed game time/i.test(text) || /total.+milliseconds/i.test(text);

console.log('  signature present?', hasSignature);
console.log('  rich body present?', hasRichBody);

if (!hasRichBody) {
    console.error('\n✗ FAIL: hover signature is there but the XML <summary> body is missing.');
    console.error('   text:', text.slice(0, 500));
    process.exit(1);
}
console.log('\n✓ PASS: hover renders rich docs for `game ms` from FadeMonoGameCommandsMetaData.');
