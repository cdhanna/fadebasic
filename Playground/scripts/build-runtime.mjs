// Publishes FadeBasic.Export.Web in Release and copies the resulting wwwroot/*
// into Playground/public/runtime/ so Vite can serve the runner from the same
// origin as the Playground page (workers require same-origin).

import { execSync } from 'node:child_process';
import { rm, mkdir, cp, copyFile, writeFile, readdir, stat } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, relative, posix } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const playgroundDir = resolve(__dirname, '..');
const runtimeProject = resolve(playgroundDir, '..', 'FadeBasic', 'FadeBasic.Export.Web', 'FadeBasic.Export.Web.csproj');
const publishOut = resolve(playgroundDir, '..', 'FadeBasic', 'FadeBasic.Export.Web', 'bin', 'Release', 'net8.0', 'publish', 'wwwroot');
const targetDir = resolve(playgroundDir, 'public', 'runtime');

console.log('[build:runtime] dotnet publish', runtimeProject);
execSync(`dotnet publish "${runtimeProject}" -c Release`, {
    stdio: 'inherit',
});

if (!existsSync(publishOut)) {
    console.error(`[build:runtime] expected publish output at ${publishOut} but it does not exist.`);
    process.exit(1);
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
const fadeLibsDir = resolve(targetDir, 'fade-libs');
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
// Enumerate every file under public/runtime/ so the Playground's export
// download knows what to bundle. We can't list files via fetch on a static
// host, so emit a JSON index at build time. Paths are POSIX-style relative
// to the runtime root (e.g. "_framework/dotnet.js", "fade-libs/MyLib.dll").
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
