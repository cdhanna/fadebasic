// Minimal Monarch tokenizers + theme contributions for the supporting file
// types the playground hosts alongside .fbasic. Kept small on purpose —
// users edit fade source, the other types are mostly read-only project
// metadata (fade.json), shader stubs (.fx), or notes (.md/.yaml). When a
// language outgrows this, swap in the full TextMate grammar via the
// @codingame default-extension package + textmate-service-override.

import * as monaco from 'monaco-editor';

type ThemeRule = { token: string; foreground?: string; fontStyle?: string };

// Per-language theme contributions, merged into the base fade-dark rules.
// Tokens that overlap with fade's existing rules (comment, keyword, string,
// number, etc.) inherit fade's colors; only language-unique tokens listed.
const themeContributions: ThemeRule[] = [
    // markdown
    { token: 'heading.md',    foreground: '569CD6', fontStyle: 'bold' },
    { token: 'strong.md',     foreground: 'D4D4D4', fontStyle: 'bold' },
    { token: 'emphasis.md',   foreground: 'D4D4D4', fontStyle: 'italic' },
    { token: 'code.md',       foreground: 'CE9178' },
    { token: 'link.md',       foreground: '569CD6', fontStyle: 'underline' },
    { token: 'list.md',       foreground: 'C586C0' },
    { token: 'quote.md',      foreground: '6A9955' },
    { token: 'hr.md',         foreground: '6A9955' },
    // json
    { token: 'key.json',      foreground: '9CDCFE' },
    // yaml
    { token: 'key.yaml',      foreground: '9CDCFE' },
    { token: 'anchor.yaml',   foreground: 'DCDCAA' },
    // hlsl shares fade's keyword/type/number/string tokens — no extras.
];

// Light-theme counterparts. Same token set; colors chosen for contrast on a
// white background (VSCode Light+ palette). Anything light-gray in the dark
// rules becomes black here so it stays visible.
const themeContributionsLight: ThemeRule[] = [
    { token: 'heading.md',    foreground: '0451A5', fontStyle: 'bold' },
    { token: 'strong.md',     foreground: '000000', fontStyle: 'bold' },
    { token: 'emphasis.md',   foreground: '000000', fontStyle: 'italic' },
    { token: 'code.md',       foreground: 'A31515' },
    { token: 'link.md',       foreground: '0451A5', fontStyle: 'underline' },
    { token: 'list.md',       foreground: 'AF00DB' },
    { token: 'quote.md',      foreground: '008000' },
    { token: 'hr.md',         foreground: '008000' },
    { token: 'key.json',      foreground: '0451A5' },
    { token: 'key.yaml',      foreground: '0451A5' },
    { token: 'anchor.yaml',   foreground: '795E26' },
];

// ─── markdown ───────────────────────────────────────────────────────────
const markdownLang: monaco.languages.IMonarchLanguage = {
    defaultToken: '',
    tokenPostfix: '.md',
    tokenizer: {
        root: [
            // fenced code blocks (```lang … ```)
            [/^\s*```\s*([a-zA-Z0-9_-]*)\s*$/, { token: 'code.md', next: '@codeblock' }],
            // ATX headers
            [/^#{1,6}\s.*$/, 'heading.md'],
            // Setext header underline
            [/^[=-]{3,}\s*$/, 'heading.md'],
            // Block quote
            [/^\s*>.*$/, 'quote.md'],
            // Horizontal rule
            [/^\s*[-*_]{3,}\s*$/, 'hr.md'],
            // Unordered list marker
            [/^\s*[-*+]\s+/, 'list.md'],
            // Ordered list marker
            [/^\s*\d+\.\s+/, 'list.md'],
            // Inline code
            [/`[^`]+`/, 'code.md'],
            // Bold (** … **) and italics (* … *)
            [/\*\*[^*]+\*\*/, 'strong.md'],
            [/\*[^*]+\*/, 'emphasis.md'],
            [/__[^_]+__/, 'strong.md'],
            [/_[^_]+_/, 'emphasis.md'],
            // Image / link
            [/!?\[[^\]]*\]\([^)]+\)/, 'link.md'],
            // Auto-link
            [/<https?:[^>]+>/, 'link.md'],
        ],
        codeblock: [
            [/^\s*```\s*$/, { token: 'code.md', next: '@pop' }],
            [/.*/, 'code.md'],
        ],
    },
};

// ─── json ───────────────────────────────────────────────────────────────
const jsonLang: monaco.languages.IMonarchLanguage = {
    defaultToken: '',
    tokenPostfix: '.json',
    keywords: ['true', 'false', 'null'],
    tokenizer: {
        root: [
            [/"(?:[^"\\]|\\.)*"\s*(?=:)/, 'key.json'],
            [/"(?:[^"\\]|\\.)*"/, 'string'],
            [/-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/, 'number'],
            [/\b(?:true|false|null)\b/, 'keyword'],
            [/[{}\[\],:]/, 'operator'],
            [/\/\/.*$/, 'comment'],
            [/\/\*/, { token: 'comment', next: '@blockcomment' }],
        ],
        blockcomment: [
            [/[^*]+/, 'comment'],
            [/\*\//, { token: 'comment', next: '@pop' }],
            [/./, 'comment'],
        ],
    },
};

// ─── yaml ───────────────────────────────────────────────────────────────
const yamlLang: monaco.languages.IMonarchLanguage = {
    defaultToken: '',
    tokenPostfix: '.yaml',
    keywords: ['true', 'false', 'null', 'yes', 'no', 'on', 'off', '~'],
    tokenizer: {
        root: [
            // Document markers
            [/^---/, 'operator'],
            [/^\.\.\./, 'operator'],
            // Comments
            [/#.*$/, 'comment'],
            // Anchors / aliases
            [/&[A-Za-z0-9_-]+/, 'anchor.yaml'],
            [/\*[A-Za-z0-9_-]+/, 'anchor.yaml'],
            // Keys (word + optional spaces + ':' at end of word)
            [/^\s*-?\s*[A-Za-z_][\w.-]*(?=\s*:)/, 'key.yaml'],
            [/^\s*"(?:[^"\\]|\\.)*"(?=\s*:)/, 'key.yaml'],
            // Block scalar indicators
            [/[|>][+-]?\d*\s*$/, 'operator'],
            // Strings
            [/"(?:[^"\\]|\\.)*"/, 'string'],
            [/'(?:[^'\\]|\\.)*'/, 'string'],
            // Booleans / null / numbers
            [/\b(?:true|false|null|yes|no|on|off)\b/i, 'keyword'],
            [/\b-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b/, 'number'],
            // List bullet
            [/^\s*-\s+/, 'list.md'],
            // Flow indicators
            [/[\[\]{},]/, 'operator'],
        ],
    },
};

// ─── hlsl (.fx) ─────────────────────────────────────────────────────────
const hlslLang: monaco.languages.IMonarchLanguage = {
    defaultToken: '',
    tokenPostfix: '.hlsl',
    keywords: [
        'asm', 'asm_fragment', 'break', 'case', 'cbuffer', 'centroid', 'class',
        'column_major', 'compile', 'const', 'continue', 'default', 'discard',
        'do', 'else', 'export', 'extern', 'for', 'fxgroup', 'globallycoherent',
        'goto', 'groupshared', 'if', 'in', 'inline', 'inout', 'interface',
        'line', 'lineadj', 'linear', 'namespace', 'nointerpolation', 'noperspective',
        'NULL', 'out', 'packoffset', 'pass', 'pixelfragment', 'point',
        'precise', 'register', 'return', 'row_major', 'sample', 'sampler',
        'shared', 'snorm', 'stateblock', 'stateblock_state', 'static', 'string',
        'struct', 'switch', 'tbuffer', 'technique', 'technique10', 'technique11',
        'texture', 'triangle', 'triangleadj', 'typedef', 'uniform', 'unorm',
        'unsigned', 'vertexfragment', 'void', 'volatile', 'while',
    ],
    typeKeywords: [
        'bool', 'int', 'uint', 'half', 'float', 'double', 'min10float',
        'min16float', 'min12int', 'min16int', 'min16uint', 'matrix', 'vector',
        'bool1', 'bool2', 'bool3', 'bool4',
        'int1', 'int2', 'int3', 'int4',
        'uint1', 'uint2', 'uint3', 'uint4',
        'float1', 'float2', 'float3', 'float4',
        'float1x1', 'float2x2', 'float3x3', 'float4x4',
        'Texture1D', 'Texture2D', 'Texture3D', 'TextureCube',
        'Texture2DArray', 'TextureCubeArray', 'SamplerState', 'SamplerComparisonState',
        'StructuredBuffer', 'RWStructuredBuffer', 'Buffer', 'RWBuffer',
        'ByteAddressBuffer', 'RWByteAddressBuffer',
    ],
    operators: [
        '=', '+', '-', '*', '/', '%', '!', '~', '&', '|', '^', '<<', '>>',
        '==', '!=', '<', '>', '<=', '>=', '&&', '||', '++', '--', '+=', '-=',
        '*=', '/=', '%=', '&=', '|=', '^=', '<<=', '>>=', '?', ':',
    ],
    symbols: /[=><!~?:&|+\-*/^%]+/,
    tokenizer: {
        root: [
            // Identifiers + keywords
            [/[A-Za-z_]\w*/, {
                cases: {
                    '@typeKeywords': 'type',
                    '@keywords':     'keyword',
                    '@default':      'identifier',
                },
            }],
            // Whitespace
            { include: '@whitespace' },
            // Numbers
            [/\d*\.\d+([eE][+-]?\d+)?[fFhH]?/, 'number'],
            [/0[xX][0-9a-fA-F]+/, 'number'],
            [/\d+[fFhHuU]?/, 'number'],
            // Strings
            [/"([^"\\]|\\.)*$/, 'string'],
            [/"/, { token: 'string', next: '@string' }],
            // Preprocessor
            [/^\s*#\s*\w+/, 'macro'],
            // Punctuation
            [/[{}()\[\]]/, '@brackets'],
            [/[;,.]/, 'delimiter'],
            [/@symbols/, {
                cases: {
                    '@operators': 'operator',
                    '@default':   '',
                },
            }],
        ],
        whitespace: [
            [/[ \t\r\n]+/, ''],
            [/\/\*/, { token: 'comment', next: '@comment' }],
            [/\/\/.*$/, 'comment'],
        ],
        comment: [
            [/[^/*]+/, 'comment'],
            [/\*\//, { token: 'comment', next: '@pop' }],
            [/[/*]/, 'comment'],
        ],
        string: [
            [/[^\\"]+/, 'string'],
            [/\\./, 'string.escape'],
            [/"/, { token: 'string', next: '@pop' }],
        ],
    },
};

interface LangSpec {
    id: string;
    extensions: string[];
    aliases: string[];
    monarch: monaco.languages.IMonarchLanguage;
}

const SPECS: LangSpec[] = [
    { id: 'markdown', extensions: ['.md', '.markdown'], aliases: ['Markdown', 'md'], monarch: markdownLang },
    { id: 'json',     extensions: ['.json'],            aliases: ['JSON'],            monarch: jsonLang },
    { id: 'yaml',     extensions: ['.yaml', '.yml'],    aliases: ['YAML'],            monarch: yamlLang },
    { id: 'hlsl',     extensions: ['.fx', '.hlsl'],     aliases: ['HLSL', 'fx'],      monarch: hlslLang },
];

// Map file basename → Monaco language id. Returns 'plaintext' for unknown.
export function languageForExtra(name: string): string | null {
    const lower = name.toLowerCase();
    for (const s of SPECS) {
        for (const ext of s.extensions) if (lower.endsWith(ext)) return s.id;
    }
    return null;
}

// Register all supporting languages and contribute theme rules. Safe to
// call once at boot; idempotent guards prevent re-registration if HMR
// somehow re-runs us.
const registered = new Set<string>();
export function registerExtraLanguages() {
    for (const s of SPECS) {
        if (registered.has(s.id)) continue;
        registered.add(s.id);
        monaco.languages.register({ id: s.id, extensions: s.extensions, aliases: s.aliases });
        monaco.languages.setMonarchTokensProvider(s.id, s.monarch);
    }
}

// Theme rules to merge into the playground's fade-dark theme. Callers
// merge before defineTheme so all rules apply in one pass.
export function extraThemeRules(variant: 'dark' | 'light' = 'dark'): ThemeRule[] {
    return variant === 'light' ? themeContributionsLight : themeContributions;
}
