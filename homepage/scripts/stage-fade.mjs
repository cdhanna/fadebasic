// Stage the Fade web runtime into homepage/public/fade.
//
// LOCAL DEV: build from the sibling `dby` source (via Fade.Playground's
// build-runtime.mjs in source mode) so the homepage picks up unpublished engine
// fixes — e.g. the breakpoint trim fix (FadeBridge [DynamicDependency]) that
// isn't in the published FadeBasic.Export.Web 0.1.2.1 nupkg yet.
//
// CI / no sibling checkout: fall back to the pinned published nupkg via
// @fadebasic/runtime-assets. Once 0.1.3 is published and runtime-versions.json
// is bumped, that path also gets the fix and this dev shortcut is moot.

import { execSync } from 'node:child_process';
import { existsSync, rmSync, cpSync, mkdirSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const homepage = resolve(here, '..');
const out = resolve(homepage, 'public', 'fade');
const playground = resolve(homepage, '..', '..', 'Fade.Playground', 'Playground');
const dbyFade = resolve(homepage, '..', 'FadeBasic'); // sibling engine source

const canSourceBuild = existsSync(resolve(playground, 'scripts', 'build-runtime.mjs'))
    && existsSync(dbyFade);

if (canSourceBuild) {
    console.log('[stage:fade] building runtime from local dby source (includes unpublished fixes)');
    execSync('node scripts/build-runtime.mjs', {
        cwd: playground,
        stdio: 'inherit',
        env: { ...process.env, FADE_RUNTIME_MODE: 'source' },
    });
    const src = resolve(playground, 'public', 'runtime');
    mkdirSync(out, { recursive: true });
    for (const sub of ['web', 'fade-libs']) {
        rmSync(resolve(out, sub), { recursive: true, force: true });
        cpSync(resolve(src, sub), resolve(out, sub), { recursive: true });
    }
    console.log('[stage:fade] staged source runtime → public/fade');
} else {
    console.log('[stage:fade] staging pinned published runtime (no local source)');
    execSync(`node ../../Fade.Playground/packages/runtime-assets/scripts/stage.mjs --out ${JSON.stringify(out)}`, {
        cwd: homepage,
        stdio: 'inherit',
    });
}
