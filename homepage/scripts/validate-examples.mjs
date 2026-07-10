// Validate every generated command example by compiling it through the real
// FadeBasic LSP (with the MonoGame command set registered), and report which
// examples fail + why. Runs the LSP worker in headless Chromium against the
// dev server (which serves /fade/web/worker.js + /fade/fade-libs/*.dll).
//
// Usage: node scripts/validate-examples.mjs        (dev server must be on :5173)

import { execFileSync } from 'node:child_process';
import { writeFileSync, rmSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const homepage = resolve(here, '..');
const playground = resolve(homepage, '..', '..', 'Fade.Playground', 'Playground');
const runtimeSrc = resolve(homepage, '..', '..', 'Fade.Playground', 'packages', 'runtime', 'src');
const ORIGIN = process.env.FADE_DEV_ORIGIN ?? 'http://localhost:5173/';

// 1. Bundle a harness: a FadeRunner + the MonoGame command registration
//    (mirrors getMonoLspReady), exposing window.__check(code) → diagnostics.
const harnessPath = resolve(runtimeSrc, '_check_harness.ts');
writeFileSync(harnessPath, `
import { FadeRunner } from './index';
let n = 0;
(async () => {
  const runner = new FadeRunner({ assetBase: '/fade/', onPrint() {}, onAlert() {} });
  await runner.ready;
  await runner.setProjectType('web');
  const base = '/fade/fade-libs/';
  const reg = async (name: string, cls: string) => {
    const r = await fetch(base + name + '.dll'); if (r.ok) await runner.registerCommandAssembly(await r.arrayBuffer(), cls);
  };
  const load = async (name: string) => {
    const r = await fetch(base + name + '.dll'); if (r.ok) await runner.loadAssembly(await r.arrayBuffer());
  };
  // Match the MonoGame runtime's command set (Standard + FadeMonoGame). We do
  // NOT register WebCommands here — the runtime has no Web lib, and registering
  // it duplicates Standard commands (print, game ms, …), yielding false
  // 'command ambiguous' diagnostics that never occur at runtime.
  await load('Fade.MonoGame.Contracts'); await load('Fade.MonoGame.Game');
  await reg('Fade.MonoGame.Lib', 'Fade.MonoGame.Lib.FadeMonoGameCommands');
  (window as any).__check = async (code: string) => {
    const uri = 'mem://ex' + (n++) + '.fbasic';
    const diags = await runner.checkDocumentDiagnostics(uri, code);
    return (diags || []).filter((d: any) => (d.severity ?? 1) === 1).map((d: any) => ({
      message: d.message, line: (d.range?.start?.line ?? 0) + 1,
    }));
  };
  (window as any).__ready = true;
})();
`);

let bundle;
try {
  const esbuild = resolve(playground, 'node_modules', '.bin', 'esbuild');
  bundle = execFileSync(esbuild, [harnessPath, '--bundle', '--format=iife', '--platform=browser', '--log-level=error'], { encoding: 'utf8', maxBuffer: 128 * 1024 * 1024 });
} finally {
  rmSync(harnessPath, { force: true });
}

// 2. Load the examples.
const genPath = resolve(homepage, 'src', 'generated', 'game-commands.js');
if (!existsSync(genPath)) { console.error('run gen:commands first'); process.exit(1); }
const { GAME_COMMAND_GROUPS } = await import(genPath);
const seen = new Set();
const examples = [];
for (const g of GAME_COMMAND_GROUPS) for (const c of g.commands) {
  for (let i = 0; i < (c.examples?.length ?? 0); i++) {
    const key = c.name + '#' + i;
    if (seen.has(key)) continue; seen.add(key);
    examples.push({ command: c.name, index: i, code: c.examples[i].code });
  }
}
console.log(`[validate] ${examples.length} examples to check`);

// 3. Drive the LSP in headless Chromium.
const pw = await import(resolve(playground, 'node_modules', 'playwright', 'index.js'));
const chromium = pw.chromium ?? pw.default?.chromium;
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(playground, 'node_modules', 'playwright', '.local-browsers');
const browser = await chromium.launch({ headless: true });
const page = await (await browser.newContext()).newPage();
await page.goto(ORIGIN, { waitUntil: 'domcontentloaded' });
await page.addScriptTag({ content: bundle });
await page.waitForFunction(() => window.__ready === true, { timeout: 60_000 });

const failures = [];
for (const ex of examples) {
  try {
    const diags = await page.evaluate((code) => window.__check(code), ex.code);
    if (diags.length) failures.push({ ...ex, diags });
  } catch (e) {
    failures.push({ ...ex, diags: [{ message: 'check threw: ' + (e?.message ?? e), line: 0 }] });
  }
}
await browser.close();

// 4. Report.
console.log(`\n[validate] ${failures.length}/${examples.length} examples have compile errors\n`);
const byMsg = new Map();
for (const f of failures) for (const d of f.diags) {
  const norm = d.message.replace(/'[^']*'/g, "'X'").replace(/\d+/g, 'N').slice(0, 80);
  byMsg.set(norm, (byMsg.get(norm) ?? 0) + 1);
}
console.log('=== error patterns (normalized) ===');
for (const [m, c] of [...byMsg.entries()].sort((a, b) => b[1] - a[1])) console.log(`${String(c).padStart(4)}  ${m}`);
console.log('\n=== per-command failures (first 60) ===');
for (const f of failures.slice(0, 60)) console.log(`${f.command}#${f.index}: ${f.diags.map((d) => `L${d.line} ${d.message}`).join(' | ')}`);
writeFileSync(resolve(homepage, 'example-validation.json'), JSON.stringify(failures, null, 1));
console.log(`\n[validate] full report → example-validation.json`);
