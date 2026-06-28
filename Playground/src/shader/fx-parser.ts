// FX framing parser.
//
// MonoGame `.fx` files mix two grammars:
//
//   1. Plain HLSL: structs, cbuffers, Texture2D/SamplerState declarations,
//      shader function bodies. glslang's HLSL frontend handles this directly.
//
//   2. FX framing: `technique { pass { VertexShader = compile vs_4_0 …; } }`
//      blocks plus `sampler_state` literals, `BlendState`/`DepthStencilState`/
//      `RasterizerState` declarations. glslang does NOT understand any of this
//      — it has to be stripped before the HLSL compile and re-emitted into
//      MGFX metadata instead.
//
// This parser is the bridge. It walks the source, extracts technique/pass/
// sampler_state/state-block declarations into structured records, and emits
// a strip-set version of the source ready to feed to glslang.
//
// Scope: targets the SM4.0+ profile that `mgfxc` actually uses (`vs_4_0`/
// `ps_4_0` and `_level_9_x` variants). The legacy DX9 SM 2.0 path with
// `sampler2D s = sampler_state {…}` literals is detected and surfaced as
// a parser warning rather than silently mis-compiled.
//
// Not done by this layer:
//   - HLSL semantic analysis (glslang does that).
//   - Constant buffer field offset computation (the cbuffer block is left
//     verbatim in the HLSL — glslang produces the layout via reflection).
//   - State-block byte encoding (deferred until we wire actual states).

export interface FxPassAssign {
    // FX assignments inside a pass body. Three shapes:
    //   VertexShader = compile vs_4_0 MainVS();       → kind='shader'
    //   BlendState   = MyBlendState;                  → kind='state-ref'
    //   AlphaBlendEnable = true;                      → kind='state-inline'
    name: string;
    kind: 'shader' | 'state-ref' | 'state-inline';
    profile?: string;        // 'vs_4_0' etc. (shader only)
    entrypoint?: string;     // function name        (shader only)
    refTarget?: string;      // referenced object    (state-ref only)
    rawValue?: string;       // verbatim RHS        (state-inline only)
}

export interface FxPass {
    name: string;       // empty string when pass is anonymous
    assigns: FxPassAssign[];
    // The character range in the original source — useful for editor
    // diagnostics ("error in pass X at line N").
    sourceStart: number;
    sourceEnd: number;
}

export interface FxTechnique {
    name: string;
    // `technique10` and `technique11` were FX10/FX11 hints that the technique
    // requires SM4+. We accept any of `technique`, `technique10`, `technique11`
    // and only use the version to surface "this looks like a DX9 technique
    // using SM2 shaders" warnings later.
    techniqueLevel: 9 | 10 | 11;
    passes: FxPass[];
    sourceStart: number;
    sourceEnd: number;
}

export interface FxSamplerStateLiteral {
    // Old-school `sampler2D s = sampler_state { Texture = <T>; … };`. We capture
    // the binding (texture reference) and the assignments verbatim. Translation
    // to a separate Texture2D + SamplerState pair happens in a later layer when
    // we wire glslang; for now we just record what we saw.
    samplerName: string;
    samplerType: 'sampler1D' | 'sampler2D' | 'sampler3D' | 'samplerCUBE'
        | 'Sampler' | 'SamplerState' | 'SamplerComparisonState';
    textureRef: string | null;        // value of `Texture = <X>;`
    assigns: Array<{ name: string; rawValue: string }>;
    sourceStart: number;
    sourceEnd: number;
}

export interface FxParseWarning {
    message: string;
    sourceOffset: number;
}

// HLSL-style `cbuffer NAME { float4 Tint; float Time; … };` declaration,
// extracted from the FX source so we can emit the MGFX parameter +
// constant-buffer records the runtime uses for Effect.Parameters["X"]
// lookups. The actual GLSL `uniform vec4 NAME[N];` array stays in the
// shader source — KNI uploads cbuffer data into it via glUniform4fv on
// every Pass.Apply().
//
// Why a separate FX-side declaration (rather than reflecting the GLSL
// uniform array): the array tells us the cbuffer SIZE, but not the
// per-field names users will pass to `set effect param`. The HLSL
// cbuffer block carries that mapping explicitly.
export interface FxCbufferField {
    typeName: string;       // float, float2, float3, float4, float4x4, int, bool
    name: string;
    arraySize: number;      // 0 when not declared as `name[N]`
    rows: number;           // for matrix types; 1 for vectors and scalars
    columns: number;        // 1=scalar, 2/3/4=vec, plus N for matrices
    offsetBytes: number;    // HLSL constant buffer packing offset (16-byte aligned)
    sizeBytes: number;
    // Character offsets into the ORIGINAL `.fx` source where this field's
    // name token starts and ends. Used by the LSP layer (go-to-def, hover,
    // rename). Optional because test fixtures and other synthetic
    // FxCbufferField producers (downstream of compile-fx, validator stubs)
    // don't need them; the LSP code treats absent ranges as "no
    // declaration site available" and skips that symbol.
    nameStart?: number;
    nameEnd?: number;
}

export interface FxCbufferDecl {
    name: string;
    fields: FxCbufferField[];
    sizeInBytes: number;    // total cbuffer size, 16-byte aligned
    sourceStart: number;
    sourceEnd: number;
    // The synthetic cbuffer `_TopLevelUniforms` carries top-level
    // declarations that didn't live in a user-authored cbuffer block.
    // LSP code uses this to decide what label to show in hover ("global
    // uniform" vs "field of cbuffer Globals").
    synthetic?: boolean;
}

// A user-defined struct declaration. Used for LSP hover / outline / member
// completion (e.g. typing `input.<TAB>` after `MainPS(VertexShaderOutput
// input)` should suggest Position/Color/TextureCoordinates).
export interface FxStructField {
    typeName: string;
    name: string;
    semantic: string | null;
    nameStart: number;
    nameEnd: number;
}
export interface FxStructDecl {
    name: string;
    fields: FxStructField[];
    nameStart: number;
    nameEnd: number;
    sourceStart: number;
    sourceEnd: number;
}

// A top-level function declaration (`VertexShaderOutput MainVS(…)` etc.).
// Enough info to power hover ("function returning float4, two args") and
// go-to-definition. We don't parse function bodies — only the signature.
export interface FxFunctionParam {
    typeName: string;
    name: string;
    semantic: string | null;
    nameStart: number;
    nameEnd: number;
}
export interface FxFunctionDecl {
    name: string;
    returnType: string;
    returnSemantic: string | null;
    params: FxFunctionParam[];
    nameStart: number;
    nameEnd: number;
    sourceStart: number;        // first byte of the return type
    sourceEnd: number;          // byte just past the closing `}`
}

export interface FxParsed {
    techniques: FxTechnique[];
    samplerStateLiterals: FxSamplerStateLiteral[];
    cbuffers: FxCbufferDecl[];

    // LSP-facing extras. Populated by the same single pass that produces
    // techniques/cbuffers/samplers, so consumers get them for free.
    structs: FxStructDecl[];
    functions: FxFunctionDecl[];

    // The original source with FX framing stripped out — ready to pass to
    // glslang's HLSL frontend. Stripped ranges are replaced with whitespace
    // of equal length so line/column numbers match the original (vital for
    // surfacing glslang diagnostics back to the editor).
    hlslStripped: string;

    warnings: FxParseWarning[];
}

// ── Public entrypoint ────────────────────────────────────────────────────────

export function parseFx(source: string): FxParsed {
    const cleaned = stripCommentsAndStrings(source);
    const warnings: FxParseWarning[] = [];
    const techniques: FxTechnique[] = [];
    const samplers: FxSamplerStateLiteral[] = [];
    const cbuffers: FxCbufferDecl[] = [];
    const structs: FxStructDecl[] = [];
    const functions: FxFunctionDecl[] = [];

    // Track ranges we want to whitespace-out in the final stripped source.
    // We collect them as [start, end) half-open ranges and apply once at the
    // end so we don't shift offsets mid-walk.
    const stripRanges: Array<[number, number]> = [];

    // Walk the cleaned source looking for top-level FX constructs. We don't
    // try to track HLSL syntax deeply — we just scan for the few keywords
    // that introduce FX-only blocks, find their matching closing brace, and
    // hand the inner range to a focused sub-parser.
    // Collect top-level (file-scope) `<type> <name>;` declarations into a
    // synthetic cbuffer the runtime can address via Effect.Parameters[name].
    // HLSL allows uniforms at top level (the canonical MonoGame SpriteEffect
    // declares `float4x4 MatrixTransform;` this way), and MonoGame's offline
    // compiler folds them into an implicit cbuffer named `vs_uniforms_vec4`
    // or `ps_uniforms_vec4`. We do the same here under the name
    // `_TopLevelUniforms` (collision-resistant — no user-authored cbuffer
    // would naturally take that name). Without this fold-in, `set effect
    // param "Tint", …` fails because Tint isn't in the parameter list.
    const topLevelFields: FxCbufferField[] = [];
    let topLevelOffset = 0;
    const topLevelRanges: Array<[number, number]> = [];

    // Brace depth — we only collect declarations at depth 0 (file scope).
    // technique/cbuffer/sampler_state blocks are consumed by their dedicated
    // sub-parsers below, so the only braces we'll see naturally are
    // function bodies and struct definitions, both of which we want to skip.
    let depth = 0;

    let i = 0;
    while (i < cleaned.length) {
        const t = nextToken(cleaned, i);
        if (!t) break;
        i = t.end;

        // Track brace depth so we never mistake a struct field or a local
        // variable declaration for a top-level uniform. nextToken returns
        // braces as `punct` tokens — we adjust depth here and skip.
        if (t.kind === 'punct') {
            if (t.text === '{') depth++;
            else if (t.text === '}') depth = Math.max(0, depth - 1);
            continue;
        }

        if (t.kind === 'word' && depth === 0) {
            // Top-level uniform declaration: a recognized HLSL scalar /
            // vector / matrix type followed by an identifier and a
            // semicolon. Reject Texture2D / SamplerState / struct keywords
            // here — those are handled by separate passes (or stay in the
            // HLSL body for the translator to deal with).
            const tl = tryParseTopLevelUniform(cleaned, t);
            if (tl) {
                // Align according to HLSL packing rules — vectors >=3 and
                // matrices/arrays go on 16-byte boundaries.
                const alignment = tl.arraySize > 0 ? 16 : hlslAlignment(tl.typeName);
                topLevelOffset = Math.ceil(topLevelOffset / alignment) * alignment;
                topLevelFields.push({
                    typeName: tl.typeName,
                    name: tl.name,
                    arraySize: tl.arraySize,
                    rows: tl.info.rows,
                    columns: tl.info.columns,
                    offsetBytes: topLevelOffset,
                    sizeBytes: tl.totalSize,
                    nameStart: tl.nameStart,
                    nameEnd: tl.nameEnd,
                });
                topLevelOffset += tl.totalSize;
                topLevelRanges.push([t.start, tl.end]);
                i = tl.end;
                continue;
            }

            // technique[10|11] <name> { pass <name> { … } … }
            if (t.text === 'technique' || t.text === 'technique10' || t.text === 'technique11') {
                const level = t.text === 'technique11' ? 11 : t.text === 'technique10' ? 10 : 9;
                const tech = parseTechniqueBody(cleaned, t.start, level, warnings);
                if (tech) {
                    techniques.push(tech);
                    stripRanges.push([tech.sourceStart, tech.sourceEnd]);
                    i = tech.sourceEnd;
                }
                continue;
            }

            // cbuffer NAME [: register(bN)]? { typed-field-list };
            // We extract this so the runtime side has Effect.Parameters[name]
            // metadata for `set effect param *` calls. The cbuffer block is
            // whitespaced out of the stripped source — the user keeps a
            // matching `uniform vec4 NAME[N];` GLSL declaration to hold the
            // data at runtime (KNI uploads the cbuffer bytes into that array
            // via glUniform4fv on each Pass.Apply()).
            if (t.text === 'cbuffer') {
                const cb = parseCbufferBody(cleaned, t.start, warnings);
                if (cb) {
                    cbuffers.push(cb);
                    stripRanges.push([cb.sourceStart, cb.sourceEnd]);
                    i = cb.sourceEnd;
                }
                continue;
            }

            // sampler[1D|2D|3D|CUBE] <name> = sampler_state { … };
            // SamplerState <name> { … };  (DX10/11 style with a sampler-state body
            // — also FX-only when assigned via `= sampler_state {…}` form)
            //
            // We only care about the `= sampler_state {…}` literal form. The
            // bare `Texture2D Tex;`/`SamplerState Smp;` declarations are valid
            // HLSL and stay in the stripped source untouched.
            if (looksLikeSamplerStateLiteralStart(cleaned, t.start)) {
                const lit = parseSamplerStateLiteral(cleaned, t.start, warnings);
                if (lit) {
                    samplers.push(lit);
                    stripRanges.push([lit.sourceStart, lit.sourceEnd]);
                    i = lit.sourceEnd;
                }
                continue;
            }

            // struct NAME { … };
            // We don't strip these — they need to stay in the GLSL output
            // for the translator. We do collect them for LSP purposes
            // (hover types, member completion, outline panel).
            if (t.text === 'struct') {
                const s = parseStructBody(cleaned, t.start, warnings);
                if (s) {
                    structs.push(s);
                    // Don't push into stripRanges — the translator (and GLSL)
                    // both want this declaration in the body. We've already
                    // captured the LSP metadata.
                    i = s.sourceEnd;
                }
                continue;
            }

            // Top-level function definition: `<retType> <name>(<params>) [: SEMANTIC]? { … }`
            // We don't strip these either — they ARE the user's shader logic.
            // The `tryParseTopLevelUniform` branch already greedily consumed
            // anything that looked like `<type> <name>;` so by the time we get
            // here `t` is some other word — possibly a return type. Look ahead
            // to confirm the function shape, capture the signature, and skip
            // past the body.
            const fn = tryParseFunctionDefinition(cleaned, t);
            if (fn) {
                functions.push(fn);
                i = fn.sourceEnd;
                continue;
            }
        }
    }

    // Emit the synthetic cbuffer for the top-level uniforms, if any. Goes
    // at the end of `cbuffers` so existing tests asserting cbuffer order
    // stay stable. We also whitespace-strip the original declarations so
    // they don't survive in `hlslStripped` (where they'd be invalid GLSL).
    if (topLevelFields.length > 0) {
        cbuffers.push({
            name: '_TopLevelUniforms',
            // KNI's MGFX→GL upload writes via glUniform4fv on the cbuffer
            // array, which is sized `ceil(sizeInBytes / 16)` vec4 slots.
            // If we leave sizeInBytes at the raw byte count (4 for a single
            // `float Alpha;`), the upload writes zero vec4s and the
            // parameter never reaches the shader — `set effect param`
            // appears to be a no-op. Round up to a 16-byte boundary so
            // there's always at least one slot, matching what the
            // user-authored cbuffer parser does (parseCbufferBody, below).
            sizeInBytes: Math.ceil(topLevelOffset / 16) * 16,
            fields: topLevelFields,
            sourceStart: 0,
            sourceEnd: 0,
            synthetic: true,
        });
        for (const r of topLevelRanges) stripRanges.push(r);
    }

    // Whitespace-out the stripped ranges in the *original* source (so the
    // user-facing offsets and tokens in the stripped string still reference
    // their original line/column).
    const hlslStripped = applyWhitespaceRanges(source, stripRanges);

    return {
        techniques,
        samplerStateLiterals: samplers,
        cbuffers,
        structs,
        functions,
        hlslStripped,
        warnings,
    };
}

// Try to read a `<type> <name> [ \[N\] ]? ;` top-level uniform declaration
// starting at the type-keyword token `typeTok`. Returns null if the token
// stream past `typeTok.end` doesn't look like one — caller falls through
// to the other top-level constructs (technique/cbuffer/sampler_state).
function tryParseTopLevelUniform(
    src: string,
    typeTok: { kind: 'word'; text: string; start: number; end: number },
): {
    typeName: string;
    name: string;
    arraySize: number;
    info: { rows: number; columns: number; sizeBytes: number };
    totalSize: number;
    end: number;
    nameStart: number;
    nameEnd: number;
} | null {
    const info = HLSL_TYPE_SIZES[typeTok.text];
    if (!info) return null;       // Not a known HLSL primitive type.

    let bi = skipWhitespace(src, typeTok.end);
    const nameTok = nextToken(src, bi);
    if (!nameTok || nameTok.kind !== 'word') return null;
    // Reject keywords that aren't identifiers (e.g. `register`, `static`).
    // The cheap signal: an actual top-level uniform is followed by `;`
    // (after an optional `[N]`), never by `(` (function), `{` (struct
    // body), or `=` (assignment — those are handled as `sampler_state`
    // literals upstream and don't reach here).
    bi = skipWhitespace(src, nameTok.end);

    let arraySize = 0;
    if (src[bi] === '[') {
        const close = src.indexOf(']', bi);
        if (close < 0) return null;
        arraySize = parseInt(src.slice(bi + 1, close).trim(), 10) || 0;
        bi = skipWhitespace(src, close + 1);
    }

    // Optional `: SEMANTIC` or `: register(b0)` — accept and skip.
    if (src[bi] === ':') {
        const semiOrBrace = src.slice(bi).search(/[;{]/);
        if (semiOrBrace < 0) return null;
        bi += semiOrBrace;
    }

    if (src[bi] !== ';') return null;        // Not a declaration we recognize.

    const elemSize = arraySize > 0 ? Math.max(info.sizeBytes, 16) : info.sizeBytes;
    const totalSize = arraySize > 0 ? arraySize * elemSize : elemSize;
    return {
        typeName: typeTok.text,
        name: nameTok.text,
        arraySize,
        info,
        totalSize,
        end: bi + 1,        // past the `;`
        nameStart: nameTok.start,
        nameEnd: nameTok.end,
    };
}

// ── cbuffer block parsing ───────────────────────────────────────────────────

// Per the HLSL packing rules (matching mgfxc's MGFX writer), each field
// is laid out at 16-byte alignment for vector types of length >= 3 and at
// natural alignment otherwise. Arrays always stride at 16 bytes per element.
const HLSL_TYPE_SIZES: Record<string, { rows: number; columns: number; sizeBytes: number }> = {
    float:    { rows: 1, columns: 1, sizeBytes: 4 },
    int:      { rows: 1, columns: 1, sizeBytes: 4 },
    uint:     { rows: 1, columns: 1, sizeBytes: 4 },
    bool:     { rows: 1, columns: 1, sizeBytes: 4 },
    float2:   { rows: 1, columns: 2, sizeBytes: 8 },
    float3:   { rows: 1, columns: 3, sizeBytes: 12 },
    float4:   { rows: 1, columns: 4, sizeBytes: 16 },
    int2:     { rows: 1, columns: 2, sizeBytes: 8 },
    int3:     { rows: 1, columns: 3, sizeBytes: 12 },
    int4:     { rows: 1, columns: 4, sizeBytes: 16 },
    float3x3: { rows: 3, columns: 3, sizeBytes: 48 },
    float4x4: { rows: 4, columns: 4, sizeBytes: 64 },
};

function hlslAlignment(typeName: string): number {
    // HLSL constant-buffer packing: scalars and short vectors align to their
    // natural size, but a field can't straddle a 16-byte boundary. We model
    // this with a simple rule: vec3/vec4/matrix types → 16; everything else
    // → its size.
    const info = HLSL_TYPE_SIZES[typeName];
    if (!info) return 16;
    if (info.sizeBytes >= 12) return 16;
    return info.sizeBytes;
}

function parseCbufferBody(
    src: string,
    keywordStart: number,
    warnings: FxParseWarning[],
): FxCbufferDecl | null {
    let i = keywordStart + 'cbuffer'.length;
    i = skipWhitespace(src, i);

    const nameTok = nextToken(src, i);
    if (!nameTok || nameTok.kind !== 'word') {
        warnings.push({ message: 'Expected name after `cbuffer`', sourceOffset: i });
        return null;
    }
    const name = nameTok.text;
    i = skipWhitespace(src, nameTok.end);

    // Optional `: register(bN)` binding. We don't consume the binding into
    // the MGFX (KNI doesn't need it), but we have to step past it.
    if (src[i] === ':') {
        const open = src.indexOf('(', i);
        const close = open >= 0 ? src.indexOf(')', open) : -1;
        if (close < 0) {
            warnings.push({ message: `Unterminated register() on cbuffer '${name}'`, sourceOffset: i });
            return null;
        }
        i = close + 1;
        i = skipWhitespace(src, i);
    }

    if (src[i] !== '{') {
        warnings.push({ message: `Expected '{' after cbuffer '${name}'`, sourceOffset: i });
        return null;
    }
    const bodyEnd = findMatching(src, i, '{', '}');
    if (bodyEnd < 0) {
        warnings.push({ message: `Unterminated cbuffer '${name}'`, sourceOffset: i });
        return null;
    }

    const fields: FxCbufferField[] = [];
    let offset = 0;
    let bi = i + 1;
    while (bi < bodyEnd) {
        bi = skipWhitespace(src, bi);
        if (bi >= bodyEnd) break;
        if (src[bi] === ';' || src[bi] === ',') { bi++; continue; }

        // Type name.
        const typeTok = nextToken(src, bi);
        if (!typeTok || typeTok.kind !== 'word') break;
        const typeName = typeTok.text;
        bi = skipWhitespace(src, typeTok.end);

        // Field name (possibly with `[N]` array suffix and `: SEMANTIC`).
        const fieldTok = nextToken(src, bi);
        if (!fieldTok || fieldTok.kind !== 'word') break;
        const fieldName = fieldTok.text;
        bi = skipWhitespace(src, fieldTok.end);

        let arraySize = 0;
        if (src[bi] === '[') {
            const close = src.indexOf(']', bi);
            if (close < 0 || close >= bodyEnd) {
                warnings.push({ message: `Unterminated array on '${fieldName}'`, sourceOffset: bi });
                break;
            }
            arraySize = parseInt(src.slice(bi + 1, close).trim(), 10);
            bi = close + 1;
            bi = skipWhitespace(src, bi);
        }

        // Optional `: SEMANTIC` — accept and skip until `;`.
        // We also accept and skip register() annotations.

        // Step to the next `;` to close the field declaration.
        const semi = src.indexOf(';', bi);
        if (semi < 0 || semi >= bodyEnd) {
            warnings.push({ message: `Field '${fieldName}' missing terminating semicolon`, sourceOffset: bi });
            break;
        }
        bi = semi + 1;

        const info = HLSL_TYPE_SIZES[typeName];
        if (!info) {
            warnings.push({
                message: `Unknown HLSL type '${typeName}' on cbuffer field '${fieldName}' — skipping`,
                sourceOffset: typeTok.start,
            });
            continue;
        }

        // Align the field's offset.
        const alignment = arraySize > 0 ? 16 : hlslAlignment(typeName);
        offset = Math.ceil(offset / alignment) * alignment;

        const elemSize = arraySize > 0
            ? Math.max(info.sizeBytes, 16)
            : info.sizeBytes;
        const totalSize = arraySize > 0 ? arraySize * elemSize : elemSize;

        fields.push({
            typeName,
            name: fieldName,
            arraySize,
            rows: info.rows,
            columns: info.columns,
            offsetBytes: offset,
            sizeBytes: totalSize,
            nameStart: fieldTok.start,
            nameEnd: fieldTok.end,
        });
        offset += totalSize;
    }

    // Trailing `;` after the closing brace (HLSL allows it; mgfxc emits it).
    let endOff = bodyEnd + 1;
    const after = skipWhitespace(src, endOff);
    if (src[after] === ';') endOff = after + 1;

    return {
        name,
        fields,
        sizeInBytes: Math.ceil(offset / 16) * 16,
        sourceStart: keywordStart,
        sourceEnd: endOff,
    };
}

// ── struct / function parsing (LSP metadata) ───────────────────────────────

// Parse a top-level `struct NAME { fields… };` block. Returns the parsed
// struct OR null if the shape doesn't match (caller falls through to the
// other top-level dispatch). The struct itself stays in the source for the
// translator — we only collect the names + ranges.
function parseStructBody(
    src: string,
    keywordStart: number,
    warnings: FxParseWarning[],
): FxStructDecl | null {
    // Advance past `struct`.
    let i = skipWhitespace(src, keywordStart + 'struct'.length);
    const nameTok = nextToken(src, i);
    if (!nameTok || nameTok.kind !== 'word') return null;
    i = skipWhitespace(src, nameTok.end);
    if (src[i] !== '{') return null;            // Could be a use, not a definition.
    const bodyOpen = i;
    const bodyEnd = findMatching(src, bodyOpen, '{', '}');
    if (bodyEnd < 0) {
        warnings.push({
            message: `Unterminated struct '${nameTok.text}'`,
            sourceOffset: nameTok.start,
        });
        return null;
    }

    // Walk the body for `<type> <name> [: SEMANTIC]? ;` fields. Permissive —
    // we ignore anything we don't recognize so the LSP doesn't die on
    // unusual constructs the translator may still cope with.
    const fields: FxStructField[] = [];
    let bi = bodyOpen + 1;
    while (bi < bodyEnd) {
        bi = skipWhitespace(src, bi);
        if (bi >= bodyEnd) break;
        if (src[bi] === ';' || src[bi] === ',') { bi++; continue; }

        const typeTok = nextToken(src, bi);
        if (!typeTok || typeTok.kind !== 'word') { bi++; continue; }
        bi = skipWhitespace(src, typeTok.end);

        const nameTokF = nextToken(src, bi);
        if (!nameTokF || nameTokF.kind !== 'word') { bi = typeTok.end; continue; }
        bi = skipWhitespace(src, nameTokF.end);

        let semantic: string | null = null;
        if (src[bi] === ':') {
            bi = skipWhitespace(src, bi + 1);
            const semTok = nextToken(src, bi);
            if (semTok && semTok.kind === 'word') {
                semantic = semTok.text;
                bi = semTok.end;
            }
        }

        // Step past the terminating `;`.
        const semi = src.indexOf(';', bi);
        if (semi < 0 || semi > bodyEnd) break;
        bi = semi + 1;

        fields.push({
            typeName: typeTok.text,
            name: nameTokF.text,
            semantic,
            nameStart: nameTokF.start,
            nameEnd: nameTokF.end,
        });
    }

    // HLSL permits a trailing `;` after the closing brace; accept it so
    // sourceEnd includes it.
    let endOff = bodyEnd + 1;
    const after = skipWhitespace(src, endOff);
    if (src[after] === ';') endOff = after + 1;

    return {
        name: nameTok.text,
        fields,
        nameStart: nameTok.start,
        nameEnd: nameTok.end,
        sourceStart: keywordStart,
        sourceEnd: endOff,
    };
}

// Try to read a top-level function definition starting at `typeTok`:
//
//     <returnType> <name>(<param>, <param>, …) [: SEMANTIC]? { … }
//
// Returns null if the shape doesn't match (which is the common case —
// most top-level words are NOT function definitions, e.g. a stray `if`).
// Caller falls through to the next dispatch.
function tryParseFunctionDefinition(
    src: string,
    typeTok: { kind: 'word'; text: string; start: number; end: number },
): FxFunctionDecl | null {
    // Quick reject: return type must look like a type name (identifier).
    // We don't restrict to a known list because the user may legitimately
    // return a user-defined struct.
    let i = skipWhitespace(src, typeTok.end);
    const nameTok = nextToken(src, i);
    if (!nameTok || nameTok.kind !== 'word') return null;
    i = skipWhitespace(src, nameTok.end);
    if (src[i] !== '(') return null;
    const parenOpen = i;
    const parenClose = findMatching(src, parenOpen, '(', ')');
    if (parenClose < 0) return null;

    // Optional `: SEMANTIC` between `)` and `{`.
    let postParen = skipWhitespace(src, parenClose + 1);
    let returnSemantic: string | null = null;
    if (src[postParen] === ':') {
        postParen = skipWhitespace(src, postParen + 1);
        const semTok = nextToken(src, postParen);
        if (semTok && semTok.kind === 'word') {
            returnSemantic = semTok.text;
            postParen = skipWhitespace(src, semTok.end);
        }
    }

    if (src[postParen] !== '{') return null;     // Forward decl or something else.
    const bodyEnd = findMatching(src, postParen, '{', '}');
    if (bodyEnd < 0) return null;

    const params = parseFunctionParamList(src, parenOpen + 1, parenClose);

    return {
        name: nameTok.text,
        returnType: typeTok.text,
        returnSemantic,
        params,
        nameStart: nameTok.start,
        nameEnd: nameTok.end,
        sourceStart: typeTok.start,
        sourceEnd: bodyEnd + 1,
    };
}

function parseFunctionParamList(src: string, start: number, end: number): FxFunctionParam[] {
    const out: FxFunctionParam[] = [];
    const raw = src.slice(start, end);
    if (raw.trim().length === 0) return out;

    // Split on commas at depth 0. (Function parameters can't contain
    // top-level commas — the only nesting would be array dimensions like
    // `float v[4]` which use square brackets.)
    const pieces: Array<[number, number]> = [];
    let segStart = 0;
    let depth = 0;
    for (let p = 0; p < raw.length; p++) {
        const c = raw[p];
        if (c === '(' || c === '[' || c === '<') depth++;
        else if (c === ')' || c === ']' || c === '>') depth--;
        else if (c === ',' && depth === 0) {
            pieces.push([segStart, p]);
            segStart = p + 1;
        }
    }
    pieces.push([segStart, raw.length]);

    for (const [s, e] of pieces) {
        const segment = raw.slice(s, e);
        // Skip leading qualifiers (`inout`, `in`, `out`, `uniform`).
        const m = /^\s*(?:in(?:out)?\s+|out\s+|uniform\s+)?(\w+)\s+(\w+)(?:\s*\[\s*\d+\s*\])?(?:\s*:\s*(\w+))?\s*$/.exec(segment);
        if (!m) continue;
        const [, typeName, paramName, semantic] = m;
        // Find the param-name position in the original source.
        const localNameOffset = segment.lastIndexOf(paramName);
        const nameStart = start + s + localNameOffset;
        out.push({
            typeName,
            name: paramName,
            semantic: semantic ?? null,
            nameStart,
            nameEnd: nameStart + paramName.length,
        });
    }
    return out;
}

// ── Strip comments and strings ──────────────────────────────────────────────

// Replace the contents of //, /* */, and " " runs with spaces (preserving
// length and newlines) so subsequent keyword/brace scanning doesn't trip
// on tokens that live inside comments or strings. We only call this on the
// internal `cleaned` buffer used for parsing — the original source is what
// we hand back to glslang (after FX-block whitespacing).
function stripCommentsAndStrings(src: string): string {
    const out = src.split('');
    let i = 0;
    while (i < src.length) {
        const c = src[i];
        const next = src[i + 1];
        if (c === '/' && next === '/') {
            // Line comment. Wipe until newline.
            while (i < src.length && src[i] !== '\n') { out[i] = ' '; i++; }
            continue;
        }
        if (c === '/' && next === '*') {
            out[i] = ' '; out[i + 1] = ' '; i += 2;
            while (i < src.length && !(src[i] === '*' && src[i + 1] === '/')) {
                if (src[i] !== '\n') out[i] = ' ';
                i++;
            }
            if (i < src.length) { out[i] = ' '; out[i + 1] = ' '; i += 2; }
            continue;
        }
        if (c === '"' || c === '\'') {
            const quote = c;
            out[i] = ' '; i++;
            while (i < src.length && src[i] !== quote) {
                // Honor backslash-escapes by skipping the next char too.
                if (src[i] === '\\' && i + 1 < src.length) {
                    if (src[i] !== '\n') out[i] = ' ';
                    if (src[i + 1] !== '\n') out[i + 1] = ' ';
                    i += 2; continue;
                }
                if (src[i] !== '\n') out[i] = ' ';
                i++;
            }
            if (i < src.length) { out[i] = ' '; i++; }
            continue;
        }
        i++;
    }
    return out.join('');
}

// ── Tokenizer ────────────────────────────────────────────────────────────────

type FxToken =
    | { kind: 'word'; text: string; start: number; end: number }
    | { kind: 'punct'; text: string; start: number; end: number };

function nextToken(src: string, from: number): FxToken | null {
    let i = skipWhitespace(src, from);
    if (i >= src.length) return null;
    const ch = src[i];
    if (isIdStart(ch)) {
        let j = i + 1;
        while (j < src.length && isIdCont(src[j])) j++;
        return { kind: 'word', text: src.slice(i, j), start: i, end: j };
    }
    // Single-char punctuation is enough for the FX shapes we care about.
    return { kind: 'punct', text: ch, start: i, end: i + 1 };
}

function skipWhitespace(src: string, i: number): number {
    while (i < src.length && /\s/.test(src[i])) i++;
    return i;
}
function isIdStart(c: string): boolean { return /[A-Za-z_]/.test(c); }
function isIdCont(c: string): boolean { return /[A-Za-z0-9_]/.test(c); }

// FX accepts boolean literals in any case (`true`, `TRUE`, `True` etc.).
const BOOL_LITERAL_RE = /^(?:true|false)$/i;

// Locate the index of the next occurrence of `ch` in src starting at i,
// skipping over any nested balanced { } groups. Returns -1 if not found.
function findMatching(src: string, i: number, openChar: string, closeChar: string): number {
    if (src[i] !== openChar) return -1;
    let depth = 1;
    i++;
    while (i < src.length && depth > 0) {
        const c = src[i];
        if (c === openChar) depth++;
        else if (c === closeChar) depth--;
        if (depth === 0) return i;
        i++;
    }
    return -1;
}

// ── technique / pass ─────────────────────────────────────────────────────────

function parseTechniqueBody(
    src: string,
    keywordStart: number,
    level: 9 | 10 | 11,
    warnings: FxParseWarning[],
): FxTechnique | null {
    // Skip the keyword (length depends on level): 'technique' | 'technique10' | 'technique11'
    let i = keywordStart + (level === 9 ? 'technique'.length : 'technique10'.length);
    i = skipWhitespace(src, i);

    // Optional name. (FX-style anonymous techniques are rare but legal.)
    let name = '';
    if (i < src.length && isIdStart(src[i])) {
        const t = nextToken(src, i);
        if (t && t.kind === 'word') { name = t.text; i = t.end; }
    }

    i = skipWhitespace(src, i);
    // Optional `<annotations>` block (`< … >`). Skip without parsing for now.
    if (src[i] === '<') {
        const close = src.indexOf('>', i + 1);
        if (close < 0) {
            warnings.push({ message: `Unterminated <…> annotation block on technique '${name}'`, sourceOffset: i });
            return null;
        }
        i = close + 1;
        i = skipWhitespace(src, i);
    }

    if (src[i] !== '{') {
        warnings.push({ message: `Expected '{' after technique '${name}'`, sourceOffset: i });
        return null;
    }
    const bodyEnd = findMatching(src, i, '{', '}');
    if (bodyEnd < 0) {
        warnings.push({ message: `Unterminated technique '${name}'`, sourceOffset: i });
        return null;
    }

    const passes: FxPass[] = [];
    // Inside the technique body, walk top-level tokens looking for `pass`.
    let pi = i + 1;
    while (pi < bodyEnd) {
        const t = nextToken(src, pi);
        if (!t || t.end > bodyEnd) break;
        pi = t.end;
        if (t.kind === 'word' && t.text === 'pass') {
            const pass = parsePassBody(src, t.start, warnings);
            if (pass) {
                passes.push(pass);
                pi = pass.sourceEnd;
            }
        }
    }

    // Consume optional trailing `;` after the closing `}`. MonoGame's
    // stock shaders end techniques with `};` and without consuming the
    // semicolon, the stripping leaves a stray `;` as a top-level
    // statement that downstream GLSL validators reject as
    // "extraneous semicolon".
    let endOff = bodyEnd + 1;
    let after = skipWhitespace(src, endOff);
    if (src[after] === ';') endOff = after + 1;

    return {
        name,
        techniqueLevel: level,
        passes,
        sourceStart: keywordStart,
        sourceEnd: endOff,
    };
}

function parsePassBody(
    src: string,
    keywordStart: number,
    warnings: FxParseWarning[],
): FxPass | null {
    let i = keywordStart + 'pass'.length;
    i = skipWhitespace(src, i);

    let name = '';
    if (i < src.length && isIdStart(src[i])) {
        const t = nextToken(src, i);
        if (t && t.kind === 'word') { name = t.text; i = t.end; }
    }

    i = skipWhitespace(src, i);
    if (src[i] === '<') {
        const close = src.indexOf('>', i + 1);
        if (close < 0) {
            warnings.push({ message: `Unterminated <…> on pass '${name}'`, sourceOffset: i });
            return null;
        }
        i = close + 1;
        i = skipWhitespace(src, i);
    }

    if (src[i] !== '{') {
        warnings.push({ message: `Expected '{' after pass '${name}'`, sourceOffset: i });
        return null;
    }
    const bodyEnd = findMatching(src, i, '{', '}');
    if (bodyEnd < 0) {
        warnings.push({ message: `Unterminated pass '${name}'`, sourceOffset: i });
        return null;
    }

    const assigns = parsePassAssigns(src, i + 1, bodyEnd, warnings);
    return { name, assigns, sourceStart: keywordStart, sourceEnd: bodyEnd + 1 };
}

// Parse `Name = <rhs> ;` statements inside a pass body. Three RHS shapes:
//
//   compile <profile> <entry>(…)        — shader binding
//   <Identifier>                        — reference to a top-level state block
//   <literal-or-expression>             — inline state assignment (true/false/0xFF/etc.)
function parsePassAssigns(
    src: string,
    bodyStart: number,
    bodyEnd: number,
    warnings: FxParseWarning[],
): FxPassAssign[] {
    const out: FxPassAssign[] = [];
    let i = bodyStart;
    while (i < bodyEnd) {
        i = skipWhitespace(src, i);
        if (i >= bodyEnd) break;
        // Skip stray punctuation (commas, semicolons left over after a previous assign).
        if (src[i] === ';' || src[i] === ',') { i++; continue; }

        const lhsTok = nextToken(src, i);
        if (!lhsTok || lhsTok.kind !== 'word' || lhsTok.end > bodyEnd) break;
        i = lhsTok.end;
        i = skipWhitespace(src, i);

        // Some FX assigns have indexed LHS like `VertexShader[0] = ...`. Skip the index.
        if (src[i] === '[') {
            const close = src.indexOf(']', i);
            if (close < 0 || close >= bodyEnd) {
                warnings.push({ message: `Unterminated index in pass assign for '${lhsTok.text}'`, sourceOffset: i });
                break;
            }
            i = close + 1;
            i = skipWhitespace(src, i);
        }

        if (src[i] !== '=') {
            // Not an assignment — skip to the next semicolon and continue.
            const semi = src.indexOf(';', i);
            if (semi < 0 || semi >= bodyEnd) break;
            i = semi + 1;
            continue;
        }
        i++;
        i = skipWhitespace(src, i);

        // Find the end of the RHS (closing semicolon).
        const semi = src.indexOf(';', i);
        const rhsEnd = semi < 0 || semi >= bodyEnd ? bodyEnd : semi;
        const rhs = src.slice(i, rhsEnd).trim();
        i = (rhsEnd < bodyEnd ? rhsEnd + 1 : bodyEnd);

        // Classify RHS.
        // compile <profile> <entry>(…)
        const compileMatch = /^compile\s+([A-Za-z0-9_]+)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(/.exec(rhs);
        if (compileMatch) {
            out.push({
                name: lhsTok.text,
                kind: 'shader',
                profile: compileMatch[1],
                entrypoint: compileMatch[2],
            });
            continue;
        }
        // Boolean literals look like bare identifiers but are state values,
        // not references to a top-level state-block declaration.
        if (BOOL_LITERAL_RE.test(rhs)) {
            out.push({ name: lhsTok.text, kind: 'state-inline', rawValue: rhs });
            continue;
        }
        // bare identifier → state reference
        if (/^[A-Za-z_][A-Za-z0-9_]*$/.test(rhs)) {
            out.push({ name: lhsTok.text, kind: 'state-ref', refTarget: rhs });
            continue;
        }
        // inline state value (0xFF/numeric/expression)
        out.push({ name: lhsTok.text, kind: 'state-inline', rawValue: rhs });
    }
    return out;
}

// ── sampler_state literal ────────────────────────────────────────────────────

function looksLikeSamplerStateLiteralStart(src: string, wordStart: number): boolean {
    // Cheap pre-screen: does the word look like a sampler type and is it
    // followed (after an identifier) by `= sampler_state`? We avoid claiming
    // bare `sampler2D Smp;` (valid HLSL — should NOT be stripped).
    const tok = nextToken(src, wordStart);
    if (!tok || tok.kind !== 'word') return false;
    if (!SAMPLER_TYPE_KEYWORDS.has(tok.text)) return false;

    let i = skipWhitespace(src, tok.end);
    const nameTok = nextToken(src, i);
    if (!nameTok || nameTok.kind !== 'word') return false;
    i = skipWhitespace(src, nameTok.end);
    if (src[i] !== '=') return false;
    i = skipWhitespace(src, i + 1);
    const next = nextToken(src, i);
    return !!(next && next.kind === 'word' && next.text === 'sampler_state');
}

const SAMPLER_TYPE_KEYWORDS = new Set<string>([
    'sampler', 'sampler1D', 'sampler2D', 'sampler3D', 'samplerCUBE',
    'Sampler', 'SamplerState', 'SamplerComparisonState',
]);

function parseSamplerStateLiteral(
    src: string,
    wordStart: number,
    warnings: FxParseWarning[],
): FxSamplerStateLiteral | null {
    const typeTok = nextToken(src, wordStart)!;
    let i = skipWhitespace(src, typeTok.end);
    const nameTok = nextToken(src, i);
    if (!nameTok || nameTok.kind !== 'word') return null;
    i = skipWhitespace(src, nameTok.end);
    if (src[i] !== '=') return null;
    i = skipWhitespace(src, i + 1);
    // `sampler_state` keyword
    const kw = nextToken(src, i);
    if (!kw || kw.kind !== 'word' || kw.text !== 'sampler_state') return null;
    i = skipWhitespace(src, kw.end);
    if (src[i] !== '{') return null;
    const bodyEnd = findMatching(src, i, '{', '}');
    if (bodyEnd < 0) {
        warnings.push({ message: `Unterminated sampler_state body on '${nameTok.text}'`, sourceOffset: i });
        return null;
    }

    const assigns: Array<{ name: string; rawValue: string }> = [];
    let textureRef: string | null = null;

    let bi = i + 1;
    while (bi < bodyEnd) {
        bi = skipWhitespace(src, bi);
        if (bi >= bodyEnd) break;
        if (src[bi] === ';' || src[bi] === ',') { bi++; continue; }
        const lhs = nextToken(src, bi);
        if (!lhs || lhs.kind !== 'word' || lhs.end > bodyEnd) break;
        bi = skipWhitespace(src, lhs.end);
        if (src[bi] !== '=') {
            const semi = src.indexOf(';', bi);
            if (semi < 0 || semi >= bodyEnd) break;
            bi = semi + 1; continue;
        }
        bi++;
        bi = skipWhitespace(src, bi);
        const semi = src.indexOf(';', bi);
        const rhsEnd = semi < 0 || semi >= bodyEnd ? bodyEnd : semi;
        const rhs = src.slice(bi, rhsEnd).trim();
        bi = rhsEnd < bodyEnd ? rhsEnd + 1 : bodyEnd;

        assigns.push({ name: lhs.text, rawValue: rhs });
        if (lhs.text === 'Texture') {
            // `Texture = <Foo>;` — strip the angle brackets if present.
            const m = /^<\s*([A-Za-z_][A-Za-z0-9_]*)\s*>$/.exec(rhs);
            textureRef = m ? m[1] : rhs;
        }
    }

    // Trailing `;` after the closing brace.
    let endOff = bodyEnd + 1;
    const after = skipWhitespace(src, endOff);
    if (src[after] === ';') endOff = after + 1;

    return {
        samplerName: nameTok.text,
        samplerType: typeTok.text as FxSamplerStateLiteral['samplerType'],
        textureRef,
        assigns,
        sourceStart: wordStart,
        sourceEnd: endOff,
    };
}

// ── Stripping helper ────────────────────────────────────────────────────────

function applyWhitespaceRanges(src: string, ranges: Array<[number, number]>): string {
    if (ranges.length === 0) return src;
    // Sort and coalesce overlapping ranges. (Coalescing matters when a nested
    // FX block was found twice — shouldn't happen given top-level scanning,
    // but cheap insurance.)
    const sorted = [...ranges].sort((a, b) => a[0] - b[0]);
    const merged: Array<[number, number]> = [];
    for (const r of sorted) {
        const last = merged[merged.length - 1];
        if (last && r[0] <= last[1]) last[1] = Math.max(last[1], r[1]);
        else merged.push([r[0], r[1]]);
    }
    // Build the output, replacing each stripped range with whitespace of
    // equal length (preserving newlines so line numbers don't shift).
    const out = src.split('');
    for (const [start, end] of merged) {
        for (let i = start; i < end; i++) {
            if (out[i] !== '\n') out[i] = ' ';
        }
    }
    return out.join('');
}
