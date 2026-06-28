// Minimal Monarch tokenizers + theme contributions for the supporting file
// types the playground hosts alongside .fbasic. Kept small on purpose —
// users edit fade source, the other types are mostly read-only project
// metadata (fade.json), shader stubs (.fx), or notes (.md/.yaml). When a
// language outgrows this, swap in the full TextMate grammar via the
// @codingame default-extension package + textmate-service-override.

import * as monaco from 'monaco-editor';
import {
    getModelSymbols,
    attachFxSymbolTracker,
    rangeFromOffsets,
    type FxSymbolKind,
} from './shader/fx-symbols';

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
    // fadefx (.fx files): intrinsic functions + entry semantics get
    // distinguishable colors. Without these, Monaco's default theme paints
    // `predefined`/`attribute` tokens the same as identifiers, so `tex2D(…)`
    // and `: TEXCOORD0` look like ordinary names.
    //
    // We use a custom language ID `fadefx` instead of the standard `hlsl`
    // because @codingame/monaco-vscode-api registers VS Code's built-in
    // HLSL TextMate grammar for the `hlsl` ID; our Monarch registration
    // gets shadowed. A custom ID has no such conflict.
    { token: 'predefined.fadefx', foreground: 'DCDCAA' },                        // function-yellow
    { token: 'attribute.fadefx',  foreground: '4EC9B0', fontStyle: 'italic' },   // teal italic for semantics
    { token: 'type.fadefx',       foreground: '4EC9B0' },
    { token: 'macro.fadefx',      foreground: 'BD63C5' },                        // preprocessor purple
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
    // fadefx light-theme colors (VSCode Light+ palette)
    { token: 'predefined.fadefx', foreground: '795E26' },
    { token: 'attribute.fadefx',  foreground: '267F99', fontStyle: 'italic' },
    { token: 'type.fadefx',       foreground: '267F99' },
    { token: 'macro.fadefx',      foreground: 'AF00DB' },
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
    tokenPostfix: '.fadefx',
    keywords: [
        'asm', 'asm_fragment', 'break', 'case', 'cbuffer', 'centroid', 'class',
        'column_major', 'compile', 'const', 'continue', 'default', 'discard',
        'do', 'else', 'export', 'extern', 'for', 'fxgroup', 'globallycoherent',
        'goto', 'groupshared', 'if', 'in', 'inline', 'inout', 'interface',
        'line', 'lineadj', 'linear', 'namespace', 'nointerpolation', 'noperspective',
        'NULL', 'out', 'packoffset', 'pass', 'pixelfragment', 'point',
        'precise', 'register', 'return', 'row_major',
        'shared', 'snorm', 'stateblock', 'stateblock_state', 'static', 'string',
        'struct', 'switch', 'tbuffer', 'technique', 'technique10', 'technique11',
        'texture', 'triangle', 'triangleadj', 'typedef', 'uniform', 'unorm',
        'unsigned', 'vertexfragment', 'void', 'volatile', 'while',
        // Effect / sampler-state framing — these introduce nested literal
        // blocks the user writes inside `sampler2D X = sampler_state {…};`,
        // `BlendState X = blend_state {…};` etc.
        'sampler_state', 'blend_state', 'depth_state', 'raster_state',
        'BlendState', 'DepthStencilState', 'RasterizerState',
        'true', 'false',
    ],
    typeKeywords: [
        'bool', 'int', 'uint', 'half', 'float', 'double', 'min10float',
        'min16float', 'min12int', 'min16int', 'min16uint', 'matrix', 'vector',
        'bool1', 'bool2', 'bool3', 'bool4',
        'int1', 'int2', 'int3', 'int4',
        'uint1', 'uint2', 'uint3', 'uint4',
        'float1', 'float2', 'float3', 'float4',
        'float1x1', 'float2x2', 'float3x3', 'float4x4',
        // DX10+ texture/sampler object types
        'Texture1D', 'Texture2D', 'Texture3D', 'TextureCube',
        'Texture2DArray', 'TextureCubeArray', 'SamplerState', 'SamplerComparisonState',
        'StructuredBuffer', 'RWStructuredBuffer', 'Buffer', 'RWBuffer',
        'ByteAddressBuffer', 'RWByteAddressBuffer',
        // DX9-era sampler types (still emitted by MonoGame's stock SpriteEffect
        // and friends). Used as `sampler2D X = sampler_state {…};`.
        'sampler', 'sampler1D', 'sampler2D', 'sampler3D', 'samplerCUBE',
        'sampler_state',
    ],
    // Intrinsic functions — get colored as `predefined` so they read as
    // built-ins distinct from user-defined functions. Covers the standard
    // HLSL/MonoGame surface plus the DX9 tex* family.
    builtinFunctions: [
        // Texture sampling
        'tex2D', 'tex2Dlod', 'tex2Dproj', 'tex2Dgrad', 'tex2Dbias',
        'tex3D', 'texCUBE', 'tex1D',
        'Sample', 'SampleLevel', 'SampleGrad', 'SampleBias', 'Load', 'Gather',
        // Linear algebra
        'mul', 'dot', 'cross', 'normalize', 'length', 'distance',
        'reflect', 'refract', 'transpose', 'determinant',
        // Math
        'abs', 'sign', 'floor', 'ceil', 'round', 'trunc', 'fmod', 'fwidth',
        'sqrt', 'rsqrt', 'pow', 'exp', 'exp2', 'log', 'log2', 'log10',
        'sin', 'cos', 'tan', 'asin', 'acos', 'atan', 'atan2',
        'sinh', 'cosh', 'tanh',
        'radians', 'degrees',
        'min', 'max', 'clamp', 'lerp', 'mix', 'saturate', 'smoothstep', 'step',
        'frac', 'modf', 'ddx', 'ddy', 'ddx_coarse', 'ddy_coarse', 'ddx_fine', 'ddy_fine',
        'all', 'any', 'isnan', 'isinf', 'isfinite',
        // Vector / matrix construction (these are types but also called like fns)
    ],
    // Output / input semantics. Get colored as `attribute` so they stand out
    // from regular identifiers.
    semantics: [
        'SV_POSITION', 'SV_TARGET', 'SV_TARGET0', 'SV_TARGET1', 'SV_TARGET2', 'SV_TARGET3',
        'SV_DEPTH', 'SV_VERTEXID', 'SV_INSTANCEID', 'SV_PRIMITIVEID', 'SV_ISFRONTFACE',
        'POSITION', 'POSITION0', 'POSITION1',
        'COLOR', 'COLOR0', 'COLOR1', 'COLOR2', 'COLOR3',
        'NORMAL', 'NORMAL0', 'TANGENT', 'BINORMAL',
        'TEXCOORD', 'TEXCOORD0', 'TEXCOORD1', 'TEXCOORD2', 'TEXCOORD3',
        'TEXCOORD4', 'TEXCOORD5', 'TEXCOORD6', 'TEXCOORD7',
        'BLENDWEIGHT', 'BLENDINDICES', 'PSIZE', 'FOG', 'DEPTH',
    ],
    operators: [
        '=', '+', '-', '*', '/', '%', '!', '~', '&', '|', '^', '<<', '>>',
        '==', '!=', '<', '>', '<=', '>=', '&&', '||', '++', '--', '+=', '-=',
        '*=', '/=', '%=', '&=', '|=', '^=', '<<=', '>>=', '?', ':',
    ],
    symbols: /[=><!~?:&|+\-*/^%]+/,
    tokenizer: {
        root: [
            // Function-call detection: a known intrinsic immediately followed
            // by `(` colors as `predefined`. Distinguishes `tex2D(…)` from
            // a user variable named `tex2D`.
            [/[A-Za-z_]\w*(?=\s*\()/, {
                cases: {
                    '@builtinFunctions': 'predefined',
                    '@keywords':         'keyword',
                    '@typeKeywords':     'type',
                    '@default':          'identifier',
                },
            }],
            // Identifiers + keywords + types + semantics
            [/[A-Za-z_]\w*/, {
                cases: {
                    '@typeKeywords': 'type',
                    '@keywords':     'keyword',
                    '@semantics':    'attribute',
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
    // `fadefx` (not `hlsl`) to avoid collision with @codingame/monaco-vscode-api's
    // built-in HLSL TextMate grammar — using `hlsl` causes their tokenizer
    // to shadow our Monarch grammar, losing the intrinsic + semantic coloring.
    { id: 'fadefx',   extensions: ['.fx', '.hlsl'],     aliases: ['Fade FX', 'fx'],   monarch: hlslLang },
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
    configureHlslLanguage();
    registerHlslCompletions();
    registerHlslHovers();
    registerHlslDocumentSymbols();
    registerHlslDefinitions();
    // Hook the per-model symbol tracker onto every existing AND future
    // .fx model. The tracker debounces a reparse on each edit so the
    // hover/definition/outline providers always have a recent table to
    // look up against. Without this, lookups would parse the model
    // synchronously every call — wasteful when many providers fire on
    // the same cursor move.
    for (const m of monaco.editor.getModels()) attachFxSymbolTracker(m);
    monaco.editor.onDidCreateModel((m) => attachFxSymbolTracker(m));
}

// ── Document Symbols (outline) ─────────────────────────────────────────────
//
// Feeds the VS Code "Outline" panel + Ctrl+Shift+O quick-pick. We surface
// every top-level declaration (uniforms, cbuffers with their fields,
// structs with their fields, functions, samplers, techniques) so the user
// can jump around large shaders quickly.
function registerHlslDocumentSymbols() {
    monaco.languages.registerDocumentSymbolProvider('fadefx', {
        provideDocumentSymbols(model) {
            const { parsed } = getModelSymbols(model);
            const out: monaco.languages.DocumentSymbol[] = [];

            for (const cb of parsed.cbuffers) {
                // Skip fields without name ranges (synthetic / non-author-
                // produced records); they have no jump-to-definition target.
                const fields: monaco.languages.DocumentSymbol[] = cb.fields
                    .filter(f => f.nameStart != null && f.nameEnd != null)
                    .map((f) => ({
                        name: f.name,
                        detail: f.typeName + (f.arraySize > 0 ? `[${f.arraySize}]` : ''),
                        kind: monaco.languages.SymbolKind.Field,
                        range: rangeFromOffsets(model, f.nameStart!, f.nameEnd!),
                        selectionRange: rangeFromOffsets(model, f.nameStart!, f.nameEnd!),
                        tags: [],
                    }));
                if (cb.synthetic) {
                    // Top-level uniforms surface flat at the document root.
                    out.push(...fields);
                } else {
                    out.push({
                        name: cb.name,
                        detail: `cbuffer (${cb.sizeInBytes} bytes)`,
                        kind: monaco.languages.SymbolKind.Namespace,
                        range: rangeFromOffsets(model, cb.sourceStart, cb.sourceEnd),
                        selectionRange: rangeFromOffsets(model, cb.sourceStart, cb.sourceEnd),
                        children: fields,
                        tags: [],
                    });
                }
            }

            for (const s of parsed.structs) {
                out.push({
                    name: s.name,
                    detail: `struct (${s.fields.length} fields)`,
                    kind: monaco.languages.SymbolKind.Struct,
                    range: rangeFromOffsets(model, s.sourceStart, s.sourceEnd),
                    selectionRange: rangeFromOffsets(model, s.nameStart, s.nameEnd),
                    children: s.fields.map((f) => ({
                        name: f.name,
                        detail: f.semantic ? `${f.typeName} : ${f.semantic}` : f.typeName,
                        kind: monaco.languages.SymbolKind.Field,
                        range: rangeFromOffsets(model, f.nameStart, f.nameEnd),
                        selectionRange: rangeFromOffsets(model, f.nameStart, f.nameEnd),
                        tags: [],
                    })),
                    tags: [],
                });
            }

            for (const fn of parsed.functions) {
                const sig = fn.params.map(p => `${p.typeName} ${p.name}`).join(', ');
                out.push({
                    name: fn.name,
                    detail: `${fn.returnType}(${sig})`,
                    kind: monaco.languages.SymbolKind.Function,
                    range: rangeFromOffsets(model, fn.sourceStart, fn.sourceEnd),
                    selectionRange: rangeFromOffsets(model, fn.nameStart, fn.nameEnd),
                    tags: [],
                });
            }

            for (const samp of parsed.samplerStateLiterals) {
                out.push({
                    name: samp.samplerName,
                    detail: samp.samplerType,
                    kind: monaco.languages.SymbolKind.Object,
                    range: rangeFromOffsets(model, samp.sourceStart, samp.sourceEnd),
                    selectionRange: rangeFromOffsets(
                        model, samp.sourceStart, samp.sourceStart + samp.samplerName.length,
                    ),
                    tags: [],
                });
            }

            for (const t of parsed.techniques) {
                out.push({
                    name: t.name,
                    detail: `technique (${t.passes.length} pass${t.passes.length === 1 ? '' : 'es'})`,
                    kind: monaco.languages.SymbolKind.Class,
                    range: rangeFromOffsets(model, t.sourceStart, t.sourceEnd),
                    selectionRange: rangeFromOffsets(model, t.sourceStart, t.sourceEnd),
                    tags: [],
                });
            }

            return out;
        },
    });
}

// ── Go-to-Definition / Ctrl+click ──────────────────────────────────────────
//
// The symbol table records each identifier's declaration site. Resolve the
// word under the cursor against the per-model table; return the location of
// the declaration's name token. Works for uniforms, struct types, struct
// fields (cross-struct ambiguity broken by first-match), functions, and
// function parameters.
function registerHlslDefinitions() {
    monaco.languages.registerDefinitionProvider('fadefx', {
        provideDefinition(model, position) {
            const word = model.getWordAtPosition(position);
            if (!word) return null;
            const sym = getModelSymbols(model).byName.get(word.word);
            if (!sym) return null;
            return {
                uri: model.uri,
                range: rangeFromOffsets(model, sym.nameStart, sym.nameEnd),
            };
        },
    });
}

// ── HLSL / .fx language configuration ──────────────────────────────────────
//
// Bracket matching, auto-indent, comment toggling — the things that make an
// editor feel like an editor rather than a textarea. Without these, Ctrl+/
// does nothing on `.fx` files, `{` auto-closing doesn't fire, and pressing
// Enter after `{` doesn't indent the new line. These are all configured per
// language, separate from the Monarch grammar that drives syntax colors.
function configureHlslLanguage() {
    monaco.languages.setLanguageConfiguration('fadefx', {
        comments: {
            lineComment: '//',
            blockComment: ['/*', '*/'],
        },
        brackets: [
            ['{', '}'],
            ['[', ']'],
            ['(', ')'],
        ],
        autoClosingPairs: [
            { open: '{', close: '}' },
            { open: '[', close: ']' },
            { open: '(', close: ')' },
            { open: '"', close: '"', notIn: ['string'] },
            { open: '<', close: '>', notIn: ['string'] },
        ],
        surroundingPairs: [
            { open: '{', close: '}' },
            { open: '[', close: ']' },
            { open: '(', close: ')' },
            { open: '"', close: '"' },
            { open: '<', close: '>' },
        ],
        indentationRules: {
            // Indent after `{`-terminated lines; unindent on `}` lines.
            increaseIndentPattern: /^.*\{[^}"']*$/,
            decreaseIndentPattern: /^\s*\}/,
        },
        wordPattern: /[A-Za-z_]\w*/,
    });
}

// ── HLSL completion provider — snippets for common .fx patterns ────────────
//
// Snippets trigger on the prefix you type then Tab to expand. Cursors land
// on the next `${N:placeholder}` so you can fill in pieces without arrow-
// keying. The catalog below covers the boilerplate that appears in every
// MonoGame-style shader: cbuffer, sampler_state literal, struct VS_OUTPUT,
// MainPS skeleton, technique/pass scaffold, sampling intrinsics.
function registerHlslCompletions() {
    monaco.languages.registerCompletionItemProvider('fadefx', {
        provideCompletionItems(model, position) {
            const word = model.getWordUntilPosition(position);
            const range: monaco.IRange = {
                startLineNumber: position.lineNumber,
                endLineNumber: position.lineNumber,
                startColumn: word.startColumn,
                endColumn: word.endColumn,
            };
            const Kind = monaco.languages.CompletionItemKind;
            const InsertAsSnippet = monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet;

            const snippet = (label: string, insertText: string, doc: string) => ({
                label,
                kind: Kind.Snippet,
                insertText,
                insertTextRules: InsertAsSnippet,
                documentation: doc,
                range,
            });

            // User-declared symbols (uniforms, structs, functions, samplers,
            // techniques, function params) get folded into completion
            // alongside the static snippet list. Each maps to a Monaco
            // SymbolKind via monacoSymbolKindFor so the IDE shows the right
            // icon. We dedupe by name+kind to keep the list tidy when the
            // user opens multiple shaders with similar APIs.
            const userSyms = getModelSymbols(model).symbols;
            const seen = new Set<string>();
            const completionKindFor = (k: FxSymbolKind): monaco.languages.CompletionItemKind => {
                switch (k) {
                    case 'uniform':        return Kind.Variable;
                    case 'cbuffer':        return Kind.Module;
                    case 'struct':         return Kind.Struct;
                    case 'struct-field':   return Kind.Field;
                    case 'function':       return Kind.Function;
                    case 'function-param': return Kind.Variable;
                    case 'sampler':        return Kind.Variable;
                    case 'technique':      return Kind.Class;
                }
            };
            const userCompletions: monaco.languages.CompletionItem[] = [];
            for (const s of userSyms) {
                const key = `${s.kind}:${s.name}`;
                if (seen.has(key)) continue;
                seen.add(key);
                userCompletions.push({
                    label: s.name,
                    kind: completionKindFor(s.kind),
                    detail: s.typeLabel,
                    insertText: s.kind === 'function' && s.detail
                        ? `${s.name}\${1:${s.detail}}`
                        : s.name,
                    insertTextRules: s.kind === 'function' ? InsertAsSnippet : undefined,
                    range,
                });
            }

            return {
                suggestions: [
                    ...userCompletions,
                    snippet(
                        'technique',
                        [
                            'technique ${1:TechniqueName}',
                            '{',
                            '\tpass P0',
                            '\t{',
                            '\t\tPixelShader = compile ps_4_0 ${2:MainPS}();',
                            '\t}',
                            '};',
                        ].join('\n'),
                        'A technique with one pass binding a pixel shader.',
                    ),
                    snippet(
                        'technique-vsps',
                        [
                            'technique ${1:TechniqueName}',
                            '{',
                            '\tpass P0',
                            '\t{',
                            '\t\tVertexShader = compile vs_4_0 ${2:MainVS}();',
                            '\t\tPixelShader  = compile ps_4_0 ${3:MainPS}();',
                            '\t}',
                            '};',
                        ].join('\n'),
                        'A technique with both vertex and pixel shaders.',
                    ),
                    snippet(
                        'cbuffer',
                        [
                            'cbuffer ${1:ps_uniforms_vec4}',
                            '{',
                            '\tfloat4 ${2:Tint};',
                            '};',
                        ].join('\n'),
                        'A constant buffer the runtime can write via `set effect param *`.',
                    ),
                    snippet(
                        'sampler-state',
                        [
                            'Texture2D ${1:SpriteTexture};',
                            'sampler2D ${2:SpriteTextureSampler} = sampler_state',
                            '{',
                            '\tTexture = <${1:SpriteTexture}>;',
                            '};',
                        ].join('\n'),
                        'DX9-style texture + sampler_state literal pair. Sample with tex2D(SamplerName, uv).',
                    ),
                    snippet(
                        'vsoutput',
                        [
                            'struct ${1:VertexShaderOutput}',
                            '{',
                            '\tfloat4 Position : SV_POSITION;',
                            '\tfloat4 Color    : COLOR0;',
                            '\tfloat2 TextureCoordinates : TEXCOORD0;',
                            '};',
                        ].join('\n'),
                        'Standard SpriteBatch VS output struct (matches MonoGame stock layout).',
                    ),
                    snippet(
                        'mainps',
                        [
                            'float4 ${1:MainPS}(${2:VertexShaderOutput} input) : SV_TARGET',
                            '{',
                            '\treturn ${3:tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color};',
                            '}',
                        ].join('\n'),
                        'Pixel shader skeleton taking a struct input + SV_TARGET output.',
                    ),
                    snippet(
                        'mainvs',
                        [
                            '${1:VertexShaderOutput} ${2:MainVS}(${3:VertexShaderInput} input)',
                            '{',
                            '\t${1:VertexShaderOutput} output;',
                            '\toutput.Position = mul(input.Position, MatrixTransform);',
                            '\t$0',
                            '\treturn output;',
                            '}',
                        ].join('\n'),
                        'Vertex shader skeleton. Note: mul() is not yet translated to GLSL — use with caution.',
                    ),
                    snippet(
                        'sprite-fx',
                        [
                            '#if OPENGL',
                            '\t#define SV_POSITION POSITION',
                            '\t#define VS_SHADERMODEL vs_3_0',
                            '\t#define PS_SHADERMODEL ps_3_0',
                            '#else',
                            '\t#define VS_SHADERMODEL vs_4_0_level_9_1',
                            '\t#define PS_SHADERMODEL ps_4_0_level_9_1',
                            '#endif',
                            '',
                            'Texture2D SpriteTexture;',
                            'sampler2D SpriteTextureSampler = sampler_state',
                            '{',
                            '\tTexture = <SpriteTexture>;',
                            '};',
                            '',
                            'cbuffer Globals',
                            '{',
                            '\tfloat4x4 MatrixTransform;',
                            '};',
                            '',
                            'struct VertexShaderInput',
                            '{',
                            '\tfloat4 Position : POSITION0;',
                            '\tfloat4 Color    : COLOR0;',
                            '\tfloat2 TextureCoordinates : TEXCOORD0;',
                            '};',
                            '',
                            'struct VertexShaderOutput',
                            '{',
                            '\tfloat4 Position : SV_POSITION;',
                            '\tfloat4 Color    : COLOR0;',
                            '\tfloat2 TextureCoordinates : TEXCOORD0;',
                            '};',
                            '',
                            'VertexShaderOutput ${1:MainVS}(VertexShaderInput input)',
                            '{',
                            '\tVertexShaderOutput output;',
                            '\toutput.Position = mul(input.Position, MatrixTransform);',
                            '\toutput.Color = input.Color;',
                            '\toutput.TextureCoordinates = input.TextureCoordinates;',
                            '\treturn output;',
                            '}',
                            '',
                            'float4 ${2:MainPS}(VertexShaderOutput input) : COLOR',
                            '{',
                            '\treturn tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;',
                            '}',
                            '',
                            'technique ${3:SpriteDrawing}',
                            '{',
                            '\tpass P0',
                            '\t{',
                            '\t\tVertexShader = compile VS_SHADERMODEL ${1:MainVS}();',
                            '\t\tPixelShader  = compile PS_SHADERMODEL ${2:MainPS}();',
                            '\t}',
                            '};',
                        ].join('\n'),
                        'The complete stock MonoGame SpriteEffect.fx scaffold — drop-in compatible.',
                    ),
                    snippet(
                        'tint-fx',
                        [
                            'cbuffer ps_uniforms_vec4',
                            '{',
                            '\tfloat4 ${1:Tint};',
                            '};',
                            '',
                            'Texture2D ps_s0;',
                            'SamplerState ps_s0_sampler;',
                            '',
                            'float4 ${2:MainPS}(float2 uv : TEXCOORD0) : SV_TARGET',
                            '{',
                            '\tfloat4 sampled = ps_s0.Sample(ps_s0_sampler, uv);',
                            '\treturn sampled * ${1:Tint};',
                            '}',
                            '',
                            'technique ${3:TintEffect}',
                            '{',
                            '\tpass P0',
                            '\t{',
                            '\t\tPixelShader = compile ps_4_0 ${2:MainPS}();',
                            '\t}',
                            '};',
                        ].join('\n'),
                        'Tint screen-effect scaffold (cbuffer-driven, DX10 sampler style).',
                    ),
                ],
            };
        },
    });
}

// ── HLSL hover provider — quick docs on common intrinsics ──────────────────
//
// Showing a tooltip on hover for the standard intrinsics lets users learn
// what's available without leaving the editor. Each entry includes the
// signature, the GLSL equivalent (so users coming from web know what
// translates to what), and a one-line description.
function registerHlslHovers() {
    interface HoverEntry { sig: string; doc: string; glsl?: string }
    const HOVER_TABLE: Record<string, HoverEntry> = {
        // Texture sampling
        'tex2D': {
            sig: 'float4 tex2D(sampler2D smp, float2 uv)',
            glsl: 'texture2D(smp, uv)',
            doc: 'DX9-era texture lookup. Returns the sampled RGBA at `uv`.',
        },
        'tex2Dlod': {
            sig: 'float4 tex2Dlod(sampler2D smp, float4 uv_lod)',
            glsl: 'texture2DLod(smp, uv, lod)',
            doc: 'Texture lookup at an explicit mip level (in .w of the arg).',
        },
        'Sample': {
            sig: 'TextureN.Sample(SamplerState smp, float2 uv)',
            glsl: 'texture2D(tex, uv)',
            doc: 'DX10+ texture sampling method. Translator drops the sampler arg.',
        },
        'SampleLevel': {
            sig: 'TextureN.SampleLevel(SamplerState smp, float2 uv, float lod)',
            glsl: 'texture2DLod(tex, uv, lod)',
            doc: 'DX10+ sample at an explicit mip level.',
        },
        // Math
        'saturate': {
            sig: 'T saturate(T x)',
            glsl: 'clamp(x, 0.0, 1.0)',
            doc: 'Clamps each component into [0, 1].',
        },
        'lerp': {
            sig: 'T lerp(T a, T b, T t)',
            glsl: 'mix(a, b, t)',
            doc: 'Linear interpolation: `a + (b - a) * t`.',
        },
        'frac': {
            sig: 'T frac(T x)',
            glsl: 'fract(x)',
            doc: 'Returns the fractional part — i.e. `x - floor(x)`.',
        },
        'rsqrt': {
            sig: 'T rsqrt(T x)',
            glsl: 'inversesqrt(x)',
            doc: 'Reciprocal square root: `1.0 / sqrt(x)`.',
        },
        'atan2': {
            sig: 'T atan2(T y, T x)',
            glsl: 'atan(y, x)',
            doc: 'Angle of the vector (x, y) in radians, range [-π, π].',
        },
        'mul': {
            sig: 'mul(M, V)  or  mul(V, M)',
            doc: 'Matrix-vector / vector-matrix product. ⚠️ **Not yet translated** — HLSL is row-major, GLSL is column-major; use direct `M * V` syntax in HLSL and the translator handles it.',
        },
        'dot': {
            sig: 'float dot(vec a, vec b)',
            glsl: 'dot(a, b)',
            doc: 'Inner product of two vectors.',
        },
        'cross': {
            sig: 'float3 cross(float3 a, float3 b)',
            glsl: 'cross(a, b)',
            doc: 'Right-handed cross product of two 3-vectors.',
        },
        'normalize': {
            sig: 'T normalize(T v)',
            glsl: 'normalize(v)',
            doc: 'Unit-length version of `v` (= `v / length(v)`).',
        },
        'length': {
            sig: 'float length(T v)',
            glsl: 'length(v)',
            doc: 'Euclidean length / magnitude of `v`.',
        },
        'smoothstep': {
            sig: 'T smoothstep(T edge0, T edge1, T x)',
            glsl: 'smoothstep(edge0, edge1, x)',
            doc: 'Smooth Hermite interpolation: 0 below edge0, 1 above edge1, smooth between.',
        },
        // Types
        'float4': { sig: 'float4 = vec4', doc: '4-component float vector.' },
        'float3': { sig: 'float3 = vec3', doc: '3-component float vector.' },
        'float2': { sig: 'float2 = vec2', doc: '2-component float vector.' },
        'float4x4': { sig: 'float4x4 = mat4', doc: '4×4 column-major matrix.' },
        'matrix':  { sig: 'matrix = float4x4 = mat4', doc: '4×4 column-major matrix (HLSL shorthand).' },
        // Semantics — common ones
        'SV_POSITION': { sig: ': SV_POSITION', doc: 'Clip-space position output of the VS / position input to the PS (mapped to gl_Position / gl_FragCoord).' },
        'SV_TARGET':   { sig: ': SV_TARGET',   doc: 'Color output of the PS (mapped to gl_FragColor).' },
        'POSITION':    { sig: ': POSITION',    doc: 'DX9 alias for SV_POSITION.' },
        'COLOR':       { sig: ': COLOR (PS return) or : COLORn (varying)', doc: 'DX9 alias for SV_TARGET when on a PS return; otherwise an interpolated varying.' },
        'TEXCOORD0':   { sig: ': TEXCOORDn',   doc: 'Texture-coordinate varying — VS writes, PS reads.' },
    };

    monaco.languages.registerHoverProvider('fadefx', {
        provideHover(model, position) {
            const word = model.getWordAtPosition(position);
            if (!word) return null;
            const range = new monaco.Range(
                position.lineNumber, word.startColumn,
                position.lineNumber, word.endColumn,
            );

            // User-declared symbol (uniform, struct, function, …) wins
            // over the static intrinsic table — if someone names their
            // own field `lerp` we'd rather show them what it is than the
            // intrinsic.
            const userSym = getModelSymbols(model).byName.get(word.word);
            if (userSym) {
                const lines = [`**\`${userSym.typeLabel}\`**`];
                switch (userSym.kind) {
                    case 'uniform':
                        lines.push('', `Set from Fade with \`set effect param <id>, "${userSym.name}", …\`.`);
                        break;
                    case 'struct':
                        lines.push('', 'User-defined struct.');
                        break;
                    case 'struct-field':
                        lines.push('', userSym.container
                            ? `Field of \`struct ${userSym.container}\`.`
                            : 'Struct field.');
                        break;
                    case 'function':
                        lines.push('', 'User-defined function.');
                        break;
                    case 'function-param':
                        lines.push('', userSym.container
                            ? `Parameter of \`${userSym.container}\`.`
                            : 'Function parameter.');
                        break;
                    case 'sampler':
                        lines.push('', 'Sampler declared via `sampler_state`.');
                        break;
                    case 'technique':
                        lines.push('', 'Technique block.');
                        break;
                    case 'cbuffer':
                        lines.push('', 'Constant-buffer block.');
                        break;
                }
                return { range, contents: [{ value: lines.join('\n') }] };
            }

            const entry = HOVER_TABLE[word.word];
            if (!entry) return null;
            const lines = [`**\`${entry.sig}\`**`];
            if (entry.glsl) lines.push(`GLSL: \`${entry.glsl}\``);
            lines.push('');
            lines.push(entry.doc);
            return {
                range,
                contents: [{ value: lines.join('\n') }],
            };
        },
    });
}

// Theme rules to merge into the playground's fade-dark theme. Callers
// merge before defineTheme so all rules apply in one pass.
export function extraThemeRules(variant: 'dark' | 'light' = 'dark'): ThemeRule[] {
    return variant === 'light' ? themeContributionsLight : themeContributions;
}
