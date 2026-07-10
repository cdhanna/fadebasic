// Stage the bundled default game assets into public/fade/assets/ and emit a
// manifest the MonoGame runtime reads on boot (see monogame-preview.ts).
//
// Images + fonts are pre-compiled to .xnb by `build-assets.mjs` (committed in
// fade-assets/compiled/); audio is copied raw and decoded by Web Audio at
// runtime. Runs as part of `npm run prep` — no browser needed here.

import { readdirSync, existsSync, rmSync, cpSync, mkdirSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const homepage = resolve(here, '..');
const assetsRoot = resolve(homepage, 'fade-assets');
const compiled = resolve(assetsRoot, 'compiled');
const audioDir = resolve(assetsRoot, 'audio');
const out = resolve(homepage, 'public', 'fade', 'assets');

rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

const manifest = [];

// Compiled image/font XNBs — the asset "name" (used in Fade code as e.g.
// `texture 1, "ghost"`) is the filename minus .xnb.
if (existsSync(compiled)) {
    for (const file of readdirSync(compiled).filter((f) => f.endsWith('.xnb'))) {
        cpSync(resolve(compiled, file), resolve(out, file));
        manifest.push({ name: file.replace(/\.xnb$/i, ''), kind: 'xnb', file });
    }
} else {
    console.warn('[stage:assets] no compiled/ dir — run `node scripts/build-assets.mjs` to produce XNBs.');
}

// Raw audio — registered via register-audio, decoded by Web Audio.
if (existsSync(audioDir)) {
    for (const file of readdirSync(audioDir).filter((f) => /\.(wav|mp3|ogg|flac)$/i.test(f))) {
        cpSync(resolve(audioDir, file), resolve(out, file));
        manifest.push({ name: file.replace(/\.[^.]+$/, ''), kind: 'audio', file });
    }
}

writeFileSync(resolve(out, 'assets-manifest.json'), JSON.stringify(manifest, null, 0));
console.log(`[stage:assets] staged ${manifest.length} assets → public/fade/assets (${manifest.map((m) => m.name).join(', ')})`);
