// Scans Fade source for asset-loading commands and reports which referenced
// assets actually exist in the project. Used by the agent loop to call out
// when a generated snippet relies on an asset the user hasn't added yet
// (so the assistant can offer to pull it from the Catalog).

import {
    assetNameForSourcePath,
    isImageSourcePath,
    isAudioSourcePath,
    isFontSourcePath,
} from '../assets/types';

/** Pull Fade code out of a markdown answer — fenced blocks tagged fade /
 *  fbasic / basic / no-language. Returns each block's source. Used to lint
 *  code the model SHOWS (vs code it applies, which goes through review). */
export function extractCodeBlocks(markdown: string): string[] {
    const out: string[] = [];
    const fence = /```([^\n`]*)\n([\s\S]*?)```/g;
    let m: RegExpExecArray | null;
    while ((m = fence.exec(markdown)) !== null) {
        const lang = m[1].trim().toLowerCase();
        if (lang && !['fade', 'fbasic', 'basic'].includes(lang)) continue;
        const code = m[2];
        if (code.trim()) out.push(code.replace(/\n$/, ''));
    }
    return out;
}

export type AssetCategory = 'image' | 'audio' | 'font';

export interface AssetRef {
    category: AssetCategory;
    /** Asset name as written in the command, e.g. "Images/Player" (no ext). */
    name: string;
    /** The loader command that referenced it, e.g. "texture". */
    command: string;
}

export interface AssetCheck {
    present: AssetRef[];
    missing: AssetRef[];
}

interface LoaderPattern {
    category: AssetCategory;
    command: string;
    /** First capture group must be the quoted asset name. */
    regex: RegExp;
}

// Fade asset loaders that take a quoted asset name. The name is a
// project-relative path without extension (e.g. `texture 1, "Images/Ball"`).
// Keep these conservative so prose never matches — each requires the
// command keyword, an id/operand, a comma, then a quoted string.
const LOADERS: LoaderPattern[] = [
    { category: 'image', command: 'texture', regex: /\btexture\s+[^,\n]+,\s*"([^"]+)"/gi },
    { category: 'font', command: 'font', regex: /\bfont\s+[^,\n]+,\s*"([^"]+)"/gi },
    { category: 'audio', command: 'load sfx clip', regex: /\bload\s+sfx\s+clip\s+[^,\n]+,\s*"([^"]+)"/gi },
    { category: 'audio', command: 'load music', regex: /\bload\s+music\s+[^,\n]*,?\s*"([^"]+)"/gi },
];

/** Pull every asset reference out of a block of Fade source (or a markdown
 *  answer containing Fade code). Deduplicated by category+name. */
export function extractAssetRefs(code: string): AssetRef[] {
    const seen = new Set<string>();
    const out: AssetRef[] = [];
    for (const loader of LOADERS) {
        loader.regex.lastIndex = 0;
        let m: RegExpExecArray | null;
        while ((m = loader.regex.exec(code)) !== null) {
            const name = m[1].trim();
            if (!name) continue;
            const key = `${loader.category}:${name.toLowerCase()}`;
            if (seen.has(key)) continue;
            seen.add(key);
            out.push({ category: loader.category, name, command: loader.command });
        }
    }
    return out;
}

/** Build the set of asset names (lowercased) the project provides per
 *  category. Compiled `.xnb` files have lost their source category, so they
 *  count toward every category. */
function availableNames(projectFiles: string[]): Record<AssetCategory, Set<string>> {
    const sets: Record<AssetCategory, Set<string>> = {
        image: new Set(),
        audio: new Set(),
        font: new Set(),
    };
    for (const path of projectFiles) {
        const name = assetNameForSourcePath(path).toLowerCase();
        const isXnb = path.toLowerCase().endsWith('.xnb');
        if (isImageSourcePath(path) || isXnb) sets.image.add(name);
        if (isAudioSourcePath(path) || isXnb) sets.audio.add(name);
        if (isFontSourcePath(path) || isXnb) sets.font.add(name);
    }
    return sets;
}

/** Scan code for asset refs and split them into present vs missing based on
 *  the project's files. Matching is case-insensitive to stay forgiving. */
export function checkAssetRefs(code: string, projectFiles: string[]): AssetCheck {
    const refs = extractAssetRefs(code);
    if (refs.length === 0) return { present: [], missing: [] };
    const avail = availableNames(projectFiles);
    const present: AssetRef[] = [];
    const missing: AssetRef[] = [];
    for (const ref of refs) {
        if (avail[ref.category].has(ref.name.toLowerCase())) present.push(ref);
        else missing.push(ref);
    }
    return { present, missing };
}
