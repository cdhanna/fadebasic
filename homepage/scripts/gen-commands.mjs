// Build-time generator for the Game Commands reference.
//
// Parses the engine's generated command reference (FadeCommandDocs.md) into a
// grouped, structured data module the Help page renders as a collapsible TOC +
// doc panel — no runtime needed to browse. MonoGame commands are bucketed by
// keyword rules (Debug / Render / Sprite / …) ported from the Playground's
// help.ts `groupsForEntry`; other libraries keep their assembly label.
//
// Usage: node scripts/gen-commands.mjs

import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const homepage = resolve(here, '..');
// The engine's command docs live in the sibling Fade.Playground checkout (same
// place the runtime is built from). If it's missing (CI without the sibling),
// emit an empty set rather than fail the build.
const SRC = resolve(homepage, '..', '..', 'Fade.Playground', 'Playground', 'rag_files', 'monogame', 'FadeCommandDocs.md');
const OUT = resolve(homepage, 'src', 'generated', 'game-commands.js');

// ── MonoGame keyword grouping (ported from Playground help.ts) ──────────────
const hasAny = (...subs) => (n) => subs.some((s) => n.includes(s));
const MONOGAME_GROUP_RULES = [
    { name: 'Debug', exclusive: true, match: (n) => n.startsWith('debug ') || n === 'debug' || n.startsWith('begin debug') || n.startsWith('end debug') || n.startsWith('disable debug') || n.startsWith('enable debug') },
    { name: 'Input', exclusive: true, match: (n) => /\bkey\b/.test(n) || /\bmouse\b/.test(n) || /\bclick\b/.test(n) || /key$/.test(n) || /code$/.test(n) || n.startsWith('new ') },
    { name: 'Math', exclusive: true, match: (n) => ['sin', 'cos', 'tan', 'atan', 'atan2', 'deg', 'rad', 'sqrt'].includes(n) },
    { name: 'Asset (macro)', exclusive: true, match: hasAny('push asset', 'rename asset') },
    { name: 'Sprite', match: hasAny('sprite') },
    { name: 'Texture', match: (n) => n.includes('texture') || n === 'font' },
    { name: 'Render', match: hasAny('render target', 'render width', 'render height', 'render size', 'effect', 'background color', 'screen effect', 'screen shake', 'stage sampler', 'screenshot') },
    { name: 'Screen', match: hasAny('screen size', 'screen width', 'screen height', 'fullscreen', 'display ', 'window title', 'is os') },
    { name: 'Audio', match: hasAny('sfx') },
    { name: 'Transform', match: hasAny('transform') },
    { name: 'Collision', match: hasAny('collider', 'collision') },
    { name: 'Text', match: (n) => /\btext\b/.test(n) || n.includes('drop shadow') },
    { name: 'Tween', match: hasAny('tween') },
    { name: 'Core', match: (n) => ['sync', 'set sync rate', 'frame number', 'game ms', 'print'].includes(n) },
];
function groupsForEntry(name, isMonoGame) {
    if (!isMonoGame) return ['Standard'];
    const lowered = name.toLowerCase();
    const hits = [];
    for (const rule of MONOGAME_GROUP_RULES) {
        if (!rule.match(lowered)) continue;
        if (rule.exclusive) return [rule.name];
        hits.push(rule.name);
    }
    return hits.length > 0 ? hits : ['Other'];
}

// Light inline-markdown → HTML for prose fields (desc / remarks / param descs).
// The doc text comes from XML-doc comments: `code`, **bold**, and [label](url)
// links (whose URLs point at Playground routes, so we keep just the label).
const escapeHtml = (s) => s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
function mdInline(s) {
    return escapeHtml(s || '')
        .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
        .replace(/`([^`]+)`/g, '<code>$1</code>')
        .replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>');
}

// ── Markdown parsing ────────────────────────────────────────────────────────
// One `- ` bullet under **Parameters**:  `Type` _(optional)_ **name** - desc
function parseParam(line) {
    const m = line.match(/^-\s*`([^`]+)`\s*(?:_\(([^)]+)\)_\s*)?\*\*([^*]+)\*\*\s*(?:-\s*(.*))?$/);
    if (!m) return null;
    return { type: m[1], modifier: m[2] || '', name: m[3].trim(), desc: (m[4] || '').trim() };
}
// **Returns** `Type` - desc
function parseReturns(line) {
    const m = line.match(/^\*\*Returns\*\*\s*`([^`]+)`\s*(?:-\s*(.*))?$/);
    return m ? { type: m[1], desc: (m[2] || '').trim() } : null;
}

function parse(md) {
    const lines = md.split('\n');
    const commands = [];
    let isMonoGame = false;
    let cur = null;
    let section = 'desc';   // desc | params | returns | remarks | examples
    // Examples state: accumulate a caption from prose lines, then the fenced
    // code that follows it, into cur.examples = [{ caption, code }].
    let inFence = false;
    let fenceLines = [];
    let caption = '';
    const flushExample = () => {
        if (fenceLines.length) cur.examples.push({ caption: caption.trim(), code: fenceLines.join('\n').replace(/\s+$/, '') });
        fenceLines = []; caption = '';
    };
    const flush = () => { if (cur) { if (inFence) { inFence = false; flushExample(); } commands.push(cur); cur = null; } };

    for (const raw of lines) {
        const line = raw.trimEnd();
        if (!inFence && line.startsWith('## ')) { flush(); isMonoGame = /MonoGame/i.test(line); continue; }
        if (!inFence && line.startsWith('### ')) {
            flush();
            cur = { name: line.slice(4).trim(), isMonoGame, desc: '', params: [], returns: null, remarks: '', examples: [] };
            section = 'desc';
            continue;
        }
        if (!cur) continue;

        // Fenced code capture (only meaningful inside the Examples section).
        if (section === 'examples' && line.trim().startsWith('```')) {
            if (inFence) { inFence = false; flushExample(); }
            else { inFence = true; }
            continue;
        }
        if (inFence) { fenceLines.push(raw); continue; }

        if (line === '---') { flush(); continue; }
        if (line === '**Parameters**') { section = 'params'; continue; }
        if (line.startsWith('**Returns**')) { cur.returns = parseReturns(line); section = 'returns'; continue; }
        if (line === '**Remarks**') { flushExample(); section = 'remarks'; continue; }
        if (line === '**Examples**') { section = 'examples'; caption = ''; continue; }

        if (section === 'params' && line.startsWith('-')) { const p = parseParam(line); if (p) cur.params.push(p); continue; }
        if (section === 'desc' && line.trim()) cur.desc += (cur.desc ? ' ' : '') + line.trim();
        else if (section === 'remarks') cur.remarks += (cur.remarks ? '\n' : '') + line;
        // In the examples section, prose lines between fences are the caption
        // for the NEXT fence (reset after each fence flush).
        else if (section === 'examples' && line.trim()) caption += (caption ? ' ' : '') + line.trim();
    }
    flush();
    return commands;
}

// ── Build grouped output ────────────────────────────────────────────────────
function build(commands) {
    const byGroup = new Map();
    for (const c of commands) {
        const entry = {
            name: c.name,
            desc: mdInline(c.desc),
            params: c.params.map((p) => ({ ...p, desc: mdInline(p.desc) })),
            returns: c.returns ? { ...c.returns, desc: mdInline(c.returns.desc) } : null,
            remarks: mdInline(c.remarks.trim()),
            examples: (c.examples || []).filter((ex) => ex.code.trim()).map((ex) => ({ caption: mdInline(ex.caption), code: ex.code })),
        };
        for (const g of groupsForEntry(c.name, c.isMonoGame)) {
            if (!byGroup.has(g)) byGroup.set(g, []);
            byGroup.get(g).push(entry);
        }
    }
    // Biggest groups first (matches the Playground); commands alphabetical.
    const groups = [...byGroup.entries()]
        .map(([name, cmds]) => ({ name, commands: cmds.sort((a, b) => a.name.localeCompare(b.name)) }))
        .sort((a, b) => b.commands.length - a.commands.length || a.name.localeCompare(b.name));
    return groups;
}

let groups = [];
if (existsSync(SRC)) {
    groups = build(parse(readFileSync(SRC, 'utf8')));
} else {
    console.warn(`[gen-commands] source not found (${SRC}); emitting empty set`);
}

mkdirSync(dirname(OUT), { recursive: true });
const banner = `// AUTO-GENERATED by scripts/gen-commands.mjs from FadeCommandDocs.md. Do not edit.\n`;
writeFileSync(OUT, banner + `export const GAME_COMMAND_GROUPS = ${JSON.stringify(groups, null, 0)};\n`);
const total = groups.reduce((n, g) => n + g.commands.length, 0);
console.log(`[gen-commands] ${groups.length} groups, ${total} command entries → ${OUT}`);
