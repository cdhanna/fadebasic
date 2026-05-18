// Publishes WebRuntime in Release and copies the resulting wwwroot/* into
// Playground/public/runtime/ so Vite can serve the runner from the same origin
// as the Playground page (workers require same-origin).

import { execSync } from 'node:child_process';
import { rm, mkdir, cp } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const playgroundDir = resolve(__dirname, '..');
const runtimeProject = resolve(playgroundDir, '..', 'WebRuntime', 'WebRuntime.csproj');
const publishOut = resolve(playgroundDir, '..', 'WebRuntime', 'bin', 'Release', 'net10.0', 'publish', 'wwwroot');
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
