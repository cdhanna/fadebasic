// Configurable list of doc sources to feed into build-docs-index.mjs.
// Each entry is { root, label, glob, projectTypes? }:
//   - root:         absolute or repo-relative directory
//   - label:        prefix used in chunk source paths and surfaced in citations
//   - glob:         forward-slash glob, evaluated relative to `root`
//   - projectTypes: optional gate; when set, every chunk from this source is
//                   only surfaced in retrieval if the active project's `type`
//                   matches one of these values. Omit for always-on docs.
//
// Edit this file to add new doc sets. The indexer reads it directly — no
// separate config file format.

import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, '..', '..');
const playgroundDir = resolve(__dirname, '..');

export const DOC_SOURCES = [
    {
        root: resolve(repoRoot, 'FadeBasic', 'book', 'FadeBook'),
        label: 'FadeBook',
        glob: '**/*.md',
    },
    {
        root: resolve(playgroundDir, 'rag_files', 'monogame'),
        label: 'MonoGame',
        glob: '**/*.md',
        projectTypes: ['monogame'],
    },
];
