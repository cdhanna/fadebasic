// Publishes FadeBasic.Export.Web in Release and copies the resulting wwwroot/*
// into Playground/public/runtime/web/ so Vite can serve the runner from the same
// origin as the Playground page (workers require same-origin).
//
// Layout under public/runtime/ (mg-export-3.md phase 3):
//   public/runtime/web/         ← this script's output (Export.Web template)
//   public/runtime/monogame/    ← build-monogame-runtime.mjs's output
//   public/runtime/fade-libs/   ← shared command DLLs (this script writes these)

import { execSync } from 'node:child_process';
import { rm, mkdir, cp, copyFile, writeFile, readdir, stat } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, relative, posix } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const playgroundDir = resolve(__dirname, '..');
const runtimeProject = resolve(playgroundDir, '..', 'FadeBasic', 'FadeBasic.Export.Web', 'FadeBasic.Export.Web.csproj');
const publishOut = resolve(playgroundDir, '..', 'FadeBasic', 'FadeBasic.Export.Web', 'bin', 'Release', 'net8.0', 'publish', 'wwwroot');
const runtimeRoot = resolve(playgroundDir, 'public', 'runtime');
const targetDir = resolve(runtimeRoot, 'web');

console.log('[build:runtime] dotnet publish', runtimeProject);
execSync(`dotnet publish "${runtimeProject}" -c Release`, {
    stdio: 'inherit',
});

if (!existsSync(publishOut)) {
    console.error(`[build:runtime] expected publish output at ${publishOut} but it does not exist.`);
    process.exit(1);
}

// One-time cleanup of pre-restructure layout: the old flat layout dropped
// every Export.Web file directly into public/runtime/. Now everything goes
// under public/runtime/web/. Wipe any leftover files at the top level so
// stale `_framework/` / `index.html` / etc. don't shadow the new web/ tree.
// Preserves the sibling subdirs (web/, monogame/, fade-libs/) which are
// managed by this script and build-monogame-runtime.mjs.
const keepAtRoot = new Set(['web', 'monogame', 'fade-libs']);
if (existsSync(runtimeRoot)) {
    for (const ent of await readdir(runtimeRoot, { withFileTypes: true })) {
        if (keepAtRoot.has(ent.name)) continue;
        const full = resolve(runtimeRoot, ent.name);
        console.log('[build:runtime] cleaning stale', full);
        await rm(full, { recursive: true, force: true });
    }
}

console.log('[build:runtime] clearing', targetDir);
await rm(targetDir, { recursive: true, force: true });
await mkdir(targetDir, { recursive: true });

console.log('[build:runtime] copying', publishOut, '→', targetDir);
await cp(publishOut, targetDir, { recursive: true });

console.log('[build:runtime] done.');

// ── Command libs ──────────────────────────────────────────────────────────────
// Build each preloaded command library and stage its DLL under
// public/runtime/fade-libs/ so the Playground can fetch and dynamically load
// it at runtime without FadeBasic.Export.Web needing a compile-time reference.
// fade-libs lives at the runtime root (not under web/) because both
// templates may need to load DLLs from it.
// Don't wipe the whole fade-libs dir — build-monogame-runtime.mjs stages
// its own DLLs there (Fade.MonoGame.{Contracts,Game,Lib}.dll). Wiping the
// directory means running `npm run dev` (predev → build-runtime) after a
// prior `build:monogame-runtime` deletes the monogame DLLs, which breaks
// the LSP's command highlighting for monogame projects. Just ensure the
// dir exists and overwrite our own DLLs below.
const fadeLibsDir = resolve(runtimeRoot, 'fade-libs');
await mkdir(fadeLibsDir, { recursive: true });

const commandLibs = [
    {
        project: resolve(playgroundDir, '..', 'FadeBasic', 'FadeBasic.Lib.Web', 'FadeBasic.Lib.Web.csproj'),
        dll:     resolve(playgroundDir, '..', 'FadeBasic', 'FadeBasic.Lib.Web', 'bin', 'Release', 'net8.0', 'FadeBasic.Lib.Web.dll'),
        name:    'FadeBasic.Lib.Web.dll',
    },
];

for (const lib of commandLibs) {
    console.log(`[build:runtime] dotnet build ${lib.name}`);
    execSync(`dotnet build "${lib.project}" -c Release`, { stdio: 'inherit' });
    if (!existsSync(lib.dll)) {
        console.error(`[build:runtime] expected DLL at ${lib.dll} but it does not exist.`);
        process.exit(1);
    }
    await copyFile(lib.dll, resolve(fadeLibsDir, lib.name));
    console.log(`[build:runtime] staged ${lib.name} → public/runtime/fade-libs/`);
}

// ── Runtime manifest ──────────────────────────────────────────────────────────
// Enumerate every file under public/runtime/web/ so the Playground's export
// download knows what to bundle. We can't list files via fetch on a static
// host, so emit a JSON index at build time. Paths are POSIX-style relative
// to the web/ subtree (e.g. "_framework/dotnet.js", "index.html"). The
// manifest is consumed by main.ts's web-export bundler; monogame export
// has its own (future) manifest under public/runtime/monogame/.
async function walk(dir) {
    const out = [];
    for (const ent of await readdir(dir, { withFileTypes: true })) {
        const full = resolve(dir, ent.name);
        if (ent.isDirectory()) {
            out.push(...await walk(full));
        } else if (ent.isFile()) {
            out.push(full);
        }
    }
    return out;
}
const allFiles = await walk(targetDir);
// Skip:
//   - The manifest itself (avoid self-reference).
//   - index.html: the export bundles its own copy from /runtime/, so it's
//     fine to include — keeping it in the manifest is simpler than the
//     per-export carve-out.
const manifestPath = resolve(targetDir, 'runtime-manifest.json');
const relPaths = allFiles
    .map((f) => relative(targetDir, f).split('\\').join('/'))
    .filter((p) => p !== 'runtime-manifest.json')
    .sort();
await writeFile(manifestPath, JSON.stringify({ files: relPaths }, null, 2));
console.log(`[build:runtime] wrote runtime-manifest.json (${relPaths.length} entries)`);
