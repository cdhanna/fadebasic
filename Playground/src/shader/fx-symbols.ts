// LSP-side symbol table for `.fx` models. Parses the model's text with
// fx-parser (which already extracts cbuffers, structs, functions, samplers,
// etc. with source ranges), flattens it into a lookup table keyed by name,
// and caches the result per model with a debounced reparse on edit.
//
// Consumers (hover / definition / outline / completion providers) call
// `getModelSymbols(model)` to read the most recent table. The first call on
// a model triggers a parse synchronously so first-frame lookups have data;
// subsequent edits queue a reparse a few hundred ms later so we don't churn
// while the user is typing.

import * as monaco from 'monaco-editor';
import { parseFx, type FxParsed, type FxStructDecl } from './fx-parser';

export type FxSymbolKind =
    | 'uniform'         // top-level or cbuffer field
    | 'cbuffer'         // cbuffer block (excluding the synthetic _TopLevelUniforms)
    | 'struct'
    | 'struct-field'
    | 'function'
    | 'function-param'
    | 'sampler'
    | 'technique';

export interface FxSymbol {
    kind: FxSymbolKind;
    name: string;
    // Human-readable type/category label. Examples:
    //   "float4"
    //   "float4x4 (cbuffer Globals)"
    //   "struct"
    //   "sampler2D"
    //   "function VertexShaderOutput → MainVS(VertexShaderInput input)"
    typeLabel: string;
    // Where the identifier is declared. Half-open byte offsets into the
    // original source. The Monaco providers convert these to line/col via
    // model.getPositionAt().
    nameStart: number;
    nameEnd: number;
    // Optional "container" — e.g. the cbuffer or struct the field lives in.
    // Used for outline grouping. Top-level symbols have container=undefined.
    container?: string;
    // For functions: the original `(<params>)` text so hover can show the
    // signature without re-stringifying.
    detail?: string;
}

export interface FxSymbolTable {
    parsed: FxParsed;
    // All symbols, in declaration order. Includes container members
    // (struct fields, function params, cbuffer fields) intermixed.
    symbols: FxSymbol[];
    // Lookup by identifier name → first matching symbol. Most identifiers
    // are unique at this level; on collision (e.g. two parameters named
    // `input` in different functions) the first wins for hover/definition.
    // Container-scoped resolution (struct member completion) walks
    // `symbols` instead.
    byName: Map<string, FxSymbol>;
    // Struct-name → its definition, for member completion (`x.<TAB>` when
    // x has type `StructName`).
    byStructName: Map<string, FxStructDecl>;
}

// Build the symbol list from a parsed result. Pure function — no Monaco
// dependency, easy to unit-test.
export function buildSymbolTable(parsed: FxParsed): FxSymbolTable {
    const symbols: FxSymbol[] = [];

    for (const cb of parsed.cbuffers) {
        const isSynthetic = !!cb.synthetic;
        // Show user-authored cbuffer blocks themselves as symbols (so they
        // appear in the outline). The synthetic `_TopLevelUniforms` has no
        // declaration site — we expose its fields directly.
        if (!isSynthetic) {
            symbols.push({
                kind: 'cbuffer',
                name: cb.name,
                typeLabel: `cbuffer (${cb.sizeInBytes} bytes)`,
                nameStart: cb.sourceStart,
                nameEnd: cb.sourceEnd,
            });
        }
        for (const f of cb.fields) {
            // Synthetic / stub-produced FxCbufferField records may not
            // carry name ranges. We surface the symbol anyway so hover +
            // completion still work, but its declaration site collapses
            // to (0,0) — go-to-definition becomes a no-op.
            if (f.nameStart == null || f.nameEnd == null) continue;
            const containerLabel = isSynthetic
                ? 'top-level uniform'
                : `cbuffer ${cb.name}`;
            symbols.push({
                kind: 'uniform',
                name: f.name,
                typeLabel: `${f.typeName} (${containerLabel}, offset ${f.offsetBytes})`,
                nameStart: f.nameStart,
                nameEnd: f.nameEnd,
                container: isSynthetic ? undefined : cb.name,
            });
        }
    }

    for (const s of parsed.structs) {
        symbols.push({
            kind: 'struct',
            name: s.name,
            typeLabel: `struct (${s.fields.length} fields)`,
            nameStart: s.nameStart,
            nameEnd: s.nameEnd,
        });
        for (const f of s.fields) {
            symbols.push({
                kind: 'struct-field',
                name: f.name,
                typeLabel: f.semantic
                    ? `${f.typeName} : ${f.semantic} (struct ${s.name})`
                    : `${f.typeName} (struct ${s.name})`,
                nameStart: f.nameStart,
                nameEnd: f.nameEnd,
                container: s.name,
            });
        }
    }

    for (const fn of parsed.functions) {
        const paramText = fn.params
            .map(p => `${p.typeName} ${p.name}`)
            .join(', ');
        symbols.push({
            kind: 'function',
            name: fn.name,
            typeLabel: `${fn.returnType} ${fn.name}(${paramText})`,
            nameStart: fn.nameStart,
            nameEnd: fn.nameEnd,
            detail: `(${paramText})`,
        });
        for (const p of fn.params) {
            symbols.push({
                kind: 'function-param',
                name: p.name,
                typeLabel: `${p.typeName} (parameter of ${fn.name})`,
                nameStart: p.nameStart,
                nameEnd: p.nameEnd,
                container: fn.name,
            });
        }
    }

    for (const samp of parsed.samplerStateLiterals) {
        symbols.push({
            kind: 'sampler',
            name: samp.samplerName,
            typeLabel: samp.textureRef
                ? `${samp.samplerType} (texture = ${samp.textureRef})`
                : samp.samplerType,
            // The FX parser captures the sampler-state block's full range,
            // not just the name range — point at the start of the block.
            nameStart: samp.sourceStart,
            nameEnd: samp.sourceStart + samp.samplerName.length,
        });
    }

    for (const t of parsed.techniques) {
        symbols.push({
            kind: 'technique',
            name: t.name,
            typeLabel: `technique (${t.passes.length} pass${t.passes.length === 1 ? '' : 'es'})`,
            nameStart: t.sourceStart,
            nameEnd: t.sourceEnd,
        });
    }

    const byName = new Map<string, FxSymbol>();
    for (const s of symbols) {
        if (!byName.has(s.name)) byName.set(s.name, s);
    }
    const byStructName = new Map<string, FxStructDecl>();
    for (const s of parsed.structs) byStructName.set(s.name, s);

    return { parsed, symbols, byName, byStructName };
}

// ── Per-model cache + debounced reparse ────────────────────────────────────

// model URI → cached table + the version we parsed at. We rebuild on the
// first lookup of a stale version.
interface CacheEntry {
    versionId: number;
    table: FxSymbolTable;
}
const cache = new WeakMap<monaco.editor.ITextModel, CacheEntry>();

// Pending reparse timers, keyed by model. We drop the timer when the
// model is disposed or another edit lands.
const pendingTimers = new WeakMap<monaco.editor.ITextModel, ReturnType<typeof setTimeout>>();

// Called by the editor wiring once at model creation. Hooks an onChange
// listener that schedules a reparse ~200ms after the user stops typing.
export function attachFxSymbolTracker(model: monaco.editor.ITextModel): void {
    if (model.getLanguageId() !== 'fadefx') return;
    // Seed the cache immediately so synchronous lookups have data.
    refreshTable(model);

    const subscription = model.onDidChangeContent(() => {
        const prev = pendingTimers.get(model);
        if (prev) clearTimeout(prev);
        const next = setTimeout(() => {
            pendingTimers.delete(model);
            refreshTable(model);
        }, 200);
        pendingTimers.set(model, next);
    });

    model.onWillDispose(() => {
        const prev = pendingTimers.get(model);
        if (prev) clearTimeout(prev);
        pendingTimers.delete(model);
        cache.delete(model);
        subscription.dispose();
    });
}

function refreshTable(model: monaco.editor.ITextModel): FxSymbolTable {
    const parsed = parseFx(model.getValue());
    const table = buildSymbolTable(parsed);
    cache.set(model, { versionId: model.getVersionId(), table });
    return table;
}

// Public read API for the providers. Returns the cached table if it's
// current; otherwise reparses inline so the consumer doesn't see stale data
// (e.g. right after an edit, before the debounce timer fires).
export function getModelSymbols(model: monaco.editor.ITextModel): FxSymbolTable {
    const cached = cache.get(model);
    if (cached && cached.versionId === model.getVersionId()) return cached.table;
    return refreshTable(model);
}

// ── Identifier-at-position helper ──────────────────────────────────────────

// Returns the identifier the cursor is currently on (or null if it's on
// whitespace/punctuation/etc.), plus the half-open offset range so callers
// can build Monaco IRange values.
export interface IdentifierAtPos {
    text: string;
    start: number;       // inclusive byte offset
    end: number;         // exclusive byte offset
}
export function identifierAtOffset(text: string, offset: number): IdentifierAtPos | null {
    const isIdChar = (c: string) => /[A-Za-z0-9_]/.test(c);
    if (offset < 0 || offset > text.length) return null;
    // The cursor sits BETWEEN characters. Try both sides — prefer the
    // character to the left (mirrors VS Code hover behavior).
    let probe = offset;
    if (probe === text.length || !isIdChar(text[probe])) {
        if (probe > 0 && isIdChar(text[probe - 1])) probe = probe - 1;
        else return null;
    }
    if (!isIdChar(text[probe])) return null;
    // Reject pure-digit "identifiers" — those are number literals.
    let start = probe;
    while (start > 0 && isIdChar(text[start - 1])) start--;
    let end = probe + 1;
    while (end < text.length && isIdChar(text[end])) end++;
    const word = text.slice(start, end);
    if (/^\d+$/.test(word)) return null;
    return { text: word, start, end };
}

// Convert half-open byte offsets to a Monaco IRange.
export function rangeFromOffsets(
    model: monaco.editor.ITextModel,
    start: number,
    end: number,
): monaco.IRange {
    const s = model.getPositionAt(start);
    const e = model.getPositionAt(end);
    return {
        startLineNumber: s.lineNumber, startColumn: s.column,
        endLineNumber: e.lineNumber,   endColumn: e.column,
    };
}
