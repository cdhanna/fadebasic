// Stage the Fade web + MonoGame runtimes into homepage/public/fade.
//
// LOCAL DEV (source): build from the sibling `dby` (+ Fade.MonoGame) source via
// Fade.Playground's build-runtime.mjs + build-monogame-runtime.mjs, so the
// homepage picks up unpublished engine fixes. The monogame runtime is built
// from source when the Fade.MonoGame checkout is present, else fetched from the
// pinned nupkg (build-monogame-runtime auto-detects).
//
// CI / no sibling checkout (package): stage the pinned published nupkgs via
// @fadebasic/runtime-assets' stage.mjs, which stages BOTH the web and monogame
// runtimes (no .NET SDK). The version is a single pin in runtime-versions.json
// (the Fade.MonoGame release); the web/core version is derived from it.

import { execSync } from 'node:child_process';
import { existsSync, mkdirSync, rmSync, cpSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const homepage = resolve(here, '..');
const out = resolve(homepage, 'public', 'fade');
const playground = resolve(homepage, '..', '..', 'Fade.Playground', 'Playground');
const dbyFade = resolve(homepage, '..', 'FadeBasic'); // sibling engine source

const canSourceBuild = existsSync(resolve(playground, 'scripts', 'build-runtime.mjs'))
    && existsSync(dbyFade);

// FADE_RUNTIME_MODE overrides the auto-detection below:
//   package — always stage the pinned published nupkgs (CI / GitHub Pages: no
//             .NET SDK required, deterministic, driven by runtime-versions.json)
//   source  — always build from the sibling dby engine checkout
//   auto    — (default) source when that checkout is present, else package
const modeEnv = (process.env.FADE_RUNTIME_MODE || 'auto').toLowerCase();
const useSource = modeEnv === 'source' ? true
    : modeEnv === 'package' ? false
    : canSourceBuild;

if (useSource && !canSourceBuild) {
    console.error('[stage:fade] FADE_RUNTIME_MODE=source, but the sibling Fade.Playground + dby engine source is not present.');
    process.exit(1);
}

if (useSource) {
    console.log('[stage:fade] building web + monogame runtimes from local source (includes unpublished fixes)');
    // Web from dby source (the whole point of source mode — unpublished fixes).
    execSync('node scripts/build-runtime.mjs', {
        cwd: playground,
        stdio: 'inherit',
        env: { ...process.env, FADE_RUNTIME_MODE: 'source' },
    });
    // MonoGame runtime: let build-monogame-runtime auto-detect — source when the
    // Fade.MonoGame checkout is present, else the pinned nupkg. Don't force the
    // web 'source' mode onto it (its source build needs .NET + the MonoGame repo).
    const { FADE_RUNTIME_MODE: _drop, ...envNoMode } = process.env;
    execSync('node scripts/build-monogame-runtime.mjs', {
        cwd: playground,
        stdio: 'inherit',
        env: envNoMode,
    });
    const src = resolve(playground, 'public', 'runtime');
    mkdirSync(out, { recursive: true });
    // Game Commands / tutorial tabs point their output iframe at monogame/.
    for (const sub of ['web', 'fade-libs', 'monogame']) {
        if (!existsSync(resolve(src, sub))) continue;
        rmSync(resolve(out, sub), { recursive: true, force: true });
        cpSync(resolve(src, sub), resolve(out, sub), { recursive: true });
    }
    console.log('[stage:fade] staged source runtime → public/fade');
} else {
    console.log('[stage:fade] staging pinned published runtime (web + monogame, no local source)');
    execSync(`node ../../Fade.Playground/packages/runtime-assets/scripts/stage.mjs --out ${JSON.stringify(out)}`, {
        cwd: homepage,
        stdio: 'inherit',
    });
}
