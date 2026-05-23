// Mirrors build-runtime.mjs but publishes WebRuntime.MonoGame (KNI BlazorGL)
// into Playground/public/runtime/monogame/ so Vite can serve it from the same
// origin as the Playground page when a 'monogame' fade.json project is open.
//
// Layout under public/runtime/ (mg-export-3.md phase 3):
//   public/runtime/web/         ← Export.Web template (build-runtime.mjs)
//   public/runtime/monogame/    ← this script's output (WebRuntime.MonoGame template)
//   public/runtime/fade-libs/   ← shared command DLLs (build-runtime.mjs)
//
// The two templates' boot styles differ on purpose; see Playground/mg.md.

import { execSync } from 'node:child_process';
import { rm, mkdir, cp, copyFile, readdir, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, relative } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const playgroundDir = resolve(__dirname, '..');
const runtimeProject = resolve(playgroundDir, '..', '..', 'Fade.MonoGame', 'Fade.MonoGame', 'WebRuntime.MonoGame', 'WebRuntime.MonoGame.csproj');
const publishOut = resolve(
    playgroundDir, '..', '..', 'Fade.MonoGame', 'Fade.MonoGame', 'WebRuntime.MonoGame',
    'bin', 'Release', 'net8.0', 'publish', 'wwwroot',
);
const targetDir = resolve(playgroundDir, 'public', 'runtime', 'monogame');
// Old (pre-restructure) location. We wipe it once on next build so
// stale leftover assets don't keep getting served. Safe to remove this
// guard a few weeks after the rename has shipped.
const legacyTargetDir = resolve(playgroundDir, 'public', 'monogame-runtime');

console.log('[build:monogame-runtime] dotnet publish', runtimeProject);
execSync(`dotnet publish "${runtimeProject}" -c Release`, {
    stdio: 'inherit',
});

if (!existsSync(publishOut)) {
    console.error(`[build:monogame-runtime] expected publish output at ${publishOut} but it does not exist.`);
    process.exit(1);
}

// One-time cleanup of pre-restructure layout. Safe to remove this block
// once everyone's rebuilt past the rename.
if (existsSync(legacyTargetDir)) {
    console.log('[build:monogame-runtime] removing legacy', legacyTargetDir);
    await rm(legacyTargetDir, { recursive: true, force: true });
}

console.log('[build:monogame-runtime] clearing', targetDir);
await rm(targetDir, { recursive: true, force: true });
await mkdir(targetDir, { recursive: true });

console.log('[build:monogame-runtime] copying', publishOut, '→', targetDir);
await cp(publishOut, targetDir, { recursive: true });

// ── Command libs for the LSP ────────────────────────────────────────
// The LSP worker (FadeBasic.Export.Web in /runtime/web/) needs to know
// the MonoGame command surface for hover, completion, and parse — even
// though it never *executes* monogame commands (the iframe's Game1 owns
// execution). Stage Lib + its required project-deps as real .dll files
// (not the renamed-to-.wasm Blazor variants — those are real WASM modules
// in .NET 8, not loadable via Assembly.Load) so main.ts's LSP-sync can
// fetch + load them when fade.json declares type='monogame'.
//
// Why Game + Contracts: Fade.MonoGame.Lib's csproj has
// ProjectReference → Fade.MonoGame.Game, which transitively pulls in
// Contracts. Activator.CreateInstance(FadeMonoGameCommands) shouldn't
// touch their types eagerly (class-level only references IMethodSource
// from FadeBasic), but pre-loading is cheap insurance. KNI BlazorGL +
// MonoGame.Framework are NOT staged — they're huge and the LSP never
// needs them: method bodies aren't JITed during metadata enumeration.
const monoLibsSrc = resolve(
    playgroundDir, '..', '..', 'Fade.MonoGame', 'Fade.MonoGame', 'WebRuntime.MonoGame',
    'bin', 'Release', 'net8.0',
);
const fadeLibsDir = resolve(playgroundDir, 'public', 'runtime', 'fade-libs');
await mkdir(fadeLibsDir, { recursive: true });
const monoCommandLibs = [
    'Fade.MonoGame.Contracts.dll',
    'Fade.MonoGame.Game.dll',
    'Fade.MonoGame.Lib.dll',
];
for (const name of monoCommandLibs) {
    const src = resolve(monoLibsSrc, name);
    if (!existsSync(src)) {
        console.error(`[build:monogame-runtime] expected ${src} but it does not exist.`);
        process.exit(1);
    }
    await copyFile(src, resolve(fadeLibsDir, name));
    console.log(`[build:monogame-runtime] staged ${name} → public/runtime/fade-libs/`);
}

// ── Runtime manifest ──────────────────────────────────────────────────────────
// Enumerate every file under public/runtime/monogame/ so the Playground's
// export bundler knows what to include in the static-host zip. Same shape as
// build-runtime.mjs's web manifest — the Playground reads either at zip time
// based on the active project's type. Paths are POSIX-style relative to the
// monogame/ subtree.
async function walk(dir) {
    const out = [];
    for (const ent of await readdir(dir, { withFileTypes: true })) {
        const full = resolve(dir, ent.name);
        if (ent.isDirectory()) out.push(...await walk(full));
        else if (ent.isFile()) out.push(full);
    }
    return out;
}
const allFiles = await walk(targetDir);
const relPaths = allFiles
    .map((f) => relative(targetDir, f).split('\\').join('/'))
    .filter((p) => p !== 'runtime-manifest.json')
    .sort();
const manifestPath = resolve(targetDir, 'runtime-manifest.json');
await writeFile(manifestPath, JSON.stringify({ files: relPaths }, null, 2));
console.log(`[build:monogame-runtime] wrote runtime-manifest.json (${relPaths.length} entries)`);

console.log('[build:monogame-runtime] done.');
