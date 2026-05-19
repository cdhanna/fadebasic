// Mirrors build-runtime.mjs but publishes WebRuntime.MonoGame (KNI BlazorGL)
// into Playground/public/monogame-runtime/ so Vite can serve it from the same
// origin as the Playground page when a 'monogame' fade.json project is open.
//
// Two runtimes live side-by-side under public/:
//   - public/runtime/           ← WebRuntime (net10 plain WASM, LSP/compile/tests/debug, runs in a worker)
//   - public/monogame-runtime/  ← WebRuntime.MonoGame (net8 Blazor WASM + KNI, runs the game on a canvas)
// The two boot styles are different on purpose; see Playground/mg.md.

import { execSync } from 'node:child_process';
import { rm, mkdir, cp } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const playgroundDir = resolve(__dirname, '..');
const runtimeProject = resolve(playgroundDir, '..', 'WebRuntime.MonoGame', 'WebRuntime.MonoGame.csproj');
const publishOut = resolve(
    playgroundDir, '..', 'WebRuntime.MonoGame',
    'bin', 'Release', 'net8.0', 'publish', 'wwwroot',
);
const targetDir = resolve(playgroundDir, 'public', 'monogame-runtime');

console.log('[build:monogame-runtime] dotnet publish', runtimeProject);
execSync(`dotnet publish "${runtimeProject}" -c Release`, {
    stdio: 'inherit',
});

if (!existsSync(publishOut)) {
    console.error(`[build:monogame-runtime] expected publish output at ${publishOut} but it does not exist.`);
    process.exit(1);
}

console.log('[build:monogame-runtime] clearing', targetDir);
await rm(targetDir, { recursive: true, force: true });
await mkdir(targetDir, { recursive: true });

console.log('[build:monogame-runtime] copying', publishOut, '→', targetDir);
await cp(publishOut, targetDir, { recursive: true });

console.log('[build:monogame-runtime] done.');
