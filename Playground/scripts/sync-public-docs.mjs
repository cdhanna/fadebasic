// Mirrors static doc files into Playground/public/docs/ so Vite can serve
// them at /docs/<name>.md without us needing a long-lived copy in the repo.
// The Help tab's "Language" and "Playground" surfaces fetch from those
// paths on activation.
//
// Sources stay in their canonical homes; we just copy on every (re)build.
// Cheap — tens of KB at most.

import { copyFile, mkdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const playgroundDir = resolve(__dirname, '..');
const repoRoot = resolve(playgroundDir, '..');

const targets = [
    {
        src: resolve(repoRoot, 'FadeBasic', 'book', 'FadeBook', 'Language.md'),
        dst: resolve(playgroundDir, 'public', 'docs', 'Language.md'),
    },
    {
        src: resolve(playgroundDir, 'docs', 'Playground.md'),
        dst: resolve(playgroundDir, 'public', 'docs', 'Playground.md'),
    },
];

for (const t of targets) {
    if (!existsSync(t.src)) {
        console.warn(`[sync-public-docs] missing source: ${t.src}`);
        continue;
    }
    await mkdir(dirname(t.dst), { recursive: true });
    await copyFile(t.src, t.dst);
    console.log(`[sync-public-docs] ${t.src} → ${t.dst}`);
}
