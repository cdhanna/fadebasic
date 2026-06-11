// HLSL → GLSL ES 1.00 translator.
//
// MonoGame .fx files conventionally use HLSL syntax (`float4`, `Texture2D`,
// `tex.Sample(smp, uv)`, struct-with-semantics function signatures). KNI's
// BlazorGL backend executes GLSL ES 1.00 (no `#version`, `varying`/`uniform`
// declarations, `gl_FragColor`, `texture2D(…)`). This module bridges the
// gap by doing a sequence of focused text transformations:
//
//   1. Type renaming (float4 → vec4, float4x4 → mat4, …)
//   2. Texture/sampler declarations (`Texture2D X[: register(t#)]?;` →
//      `uniform sampler2D X;`; `SamplerState Y;` dropped — GL combines the
//      two into a single sampler binding)
//   3. Method-call syntax (`X.Sample(Y, uv)` → `texture2D(X, uv)`)
//   4. Common intrinsics (saturate/lerp/frac/rsqrt/atan2 → GLSL names)
//   5. Entry-function rewriting — strip semantics off the signature,
//      synthesize a `void main()` trampoline that pulls each parameter
//      from the matching `varying` (PS) or `attribute` (VS) and assigns
//      the return to `gl_FragColor` / `gl_Position`.
//   6. Constant-buffer expansion — for each FX-parsed `cbuffer NAME { … };`
//      block, emit `uniform vec4 NAME[N];` plus per-field `#define`s so
//      the user's HLSL `Tint` references just work.
//
// Scope: targets the SM4.0+ subset MonoGame's mgfxc emits for SpriteBatch-
// shaped effects. NOT supported (yet):
//   - `mul(M, V)` matrix * vector (defer until VS support lands; GL is
//     column-major-by-default, HLSL is row-major-by-default, the
//     transpose needs explicit handling)
//   - struct-typed parameters with semantics (function takes a single
//     parameter that's a user struct rather than scalar/vector params).
//   - `#include` / preprocessor beyond what survives FX parsing.
//   - HLSL-specific intrinsics not in the table below.
//
// Surprises will compile-fail rather than silently mistranslate; the
// diagnostics surface back through the ShaderCompiler interface.

import type { FxCbufferDecl, FxCbufferField, FxSamplerStateLiteral } from './fx-parser';

export interface TranslateOptions {
    source: string;
    entrypoint: string;
    stage: 'vertex' | 'pixel';
    cbuffers: FxCbufferDecl[];
    // DX9-style `sampler2D Smp = sampler_state { Texture = <Tex>; };` blocks
    // the FX parser extracted out of the source. We emit one
    // `uniform sampler2D <samplerName>;` per literal, and the user's
    // `tex2D(smp, uv)` calls (translated to texture2D) resolve to those
    // uniforms.
    samplerStateLiterals?: FxSamplerStateLiteral[];
}

export interface TranslateResult {
    glsl: string;
    samplers: Array<{ name: string; samplerType: 0 | 1 | 2 }>;  // for MGFX records
    // VS-input attributes (POSITION/COLOR/TEXCOORD/etc.) reflected out for
    // the MGFX writer to bind GL vertex array slots by usage. Empty for PS.
    attributes: Array<{ name: string; usage: number; index: number; location: number }>;
    diagnostics: Array<{ severity: 'error' | 'warning' | 'info'; message: string; line?: number }>;
    // Number of lines the translator prepended before the user's body
    // content (precision header, synthesized cbuffers, sampler decls,
    // varying decls). Lets downstream GLSL validators map error-message
    // line numbers back into the .fx source: if a glslang error is at
    // glslLine N, the corresponding .fx line is approximately
    // `N - preambleLineCount`, valid when N > preambleLineCount.
    preambleLineCount: number;
}

// Maps HLSL semantics (TEXCOORD0, COLOR0, etc.) to the GLSL varying name
// KNI's BlazorGL SpriteBatch's built-in VS outputs. The user's PS reads
// these from the same names; mismatched names → "Varying X has static-use
// in the frag shader, but is undeclared in the vert shader" at GL link time.
//
// Probed directly from the strings inside KNI.Platform.dll (the BlazorGL
// runtime's built-in shader source):
//
//   #define vs_oD0 vFrontColor             // COLOR0 (legacy GL fixed-
//   #define vs_oD1 vFrontSecondaryColor    //   pipeline "diffuse" names)
//   #define vs_oT0 vTexCoord0
//   #define vs_oT1 vTexCoord1
//   #define vs_oT2 vTexCoord2
//   #define vs_oPos gl_Position
//
// COLOR0 maps to `vFrontColor` (NOT `vColor0`) because KNI's shader
// templates preserve the legacy OpenGL fixed-pipeline naming where the
// primary color varying was `gl_FrontColor`.
const SEMANTIC_TO_VARYING: Record<string, string> = {
    'TEXCOORD0': 'vTexCoord0',
    'TEXCOORD1': 'vTexCoord1',
    'TEXCOORD2': 'vTexCoord2',
    'TEXCOORD3': 'vTexCoord3',
    'COLOR0':    'vFrontColor',
    'COLOR':     'vFrontColor',
    'COLOR1':    'vFrontSecondaryColor',
    'NORMAL0':   'vNormal0',
    'NORMAL':    'vNormal0',
};

// In GL ES 1.00 the SpriteBatch's interpolated varyings are vec4 even when
// the PS only consumes vec2/vec3 of them. The translator inserts the right
// swizzle (e.g. `.xy` for vec2) at call sites; this map records the
// expected source dimension per semantic. Unknown semantics fall back to
// vec4 → consumer uses the whole vector.
const SEMANTIC_VARYING_TYPE: Record<string, string> = {
    'TEXCOORD0': 'vec4', 'TEXCOORD1': 'vec4', 'TEXCOORD2': 'vec4', 'TEXCOORD3': 'vec4',
    'COLOR0':    'vec4', 'COLOR':     'vec4',
    'NORMAL0':   'vec4', 'NORMAL':    'vec4',
};

// VS-INPUT attribute names. Distinct from the output-varying names above
// because a VS that writes the same semantic it reads (e.g. passes COLOR0
// straight through) would otherwise produce a GLSL "redefinition" error:
// you can't declare `vFrontColor` as both `attribute` and `varying` in
// the same shader. We use semantic-named `a*` for VS inputs and keep the
// `v*` names for VS outputs / PS inputs.
// XNA/MonoGame `VertexElementUsage` enum values — verified against the
// attribute records embedded in FadeSpriteBatchEffect.xnb (the
// offline-compiled effect that ships with KNI BlazorGL and works
// correctly today). Position=0, Color=1, TextureCoordinate=2, Normal=3.
// Getting these wrong silently breaks attribute binding: KNI matches
// VertexElements to MGFX attributes by (usage, index), so a Color
// VertexElement targeting `usage=3` finds nothing and the GL attribute
// reads zero, which collapses any `input.Color` multiply to black and
// any `tex2D(_, input.TextureCoordinates)` sample to the (0,0) texel.
const SEMANTIC_TO_ATTRIBUTE: Record<string, { name: string; usage: number; index: number }> = {
    'POSITION0': { name: 'aPosition0', usage: 0, index: 0 },
    'POSITION':  { name: 'aPosition0', usage: 0, index: 0 },
    'COLOR0':    { name: 'aColor0',    usage: 1, index: 0 },
    'COLOR':     { name: 'aColor0',    usage: 1, index: 0 },
    'COLOR1':    { name: 'aColor1',    usage: 1, index: 1 },
    'TEXCOORD0': { name: 'aTexCoord0', usage: 2, index: 0 },
    'TEXCOORD1': { name: 'aTexCoord1', usage: 2, index: 1 },
    'TEXCOORD2': { name: 'aTexCoord2', usage: 2, index: 2 },
    'TEXCOORD3': { name: 'aTexCoord3', usage: 2, index: 3 },
    'NORMAL0':   { name: 'aNormal0',   usage: 3, index: 0 },
    'NORMAL':    { name: 'aNormal0',   usage: 3, index: 0 },
};

const GLSL_TYPE_DIMS: Record<string, number> = {
    'float': 1, 'vec2': 2, 'vec3': 3, 'vec4': 4,
};

export function translateHlslToGlsl(opts: TranslateOptions): TranslateResult {
    let body = opts.source;
    const diagnostics: TranslateResult['diagnostics'] = [];

    // Preprocessor: keep `#if OPENGL` branch, drop the `#else` half. Done
    // before everything else so the rest of the translation never sees the
    // non-GL branch (which would have DX9-only syntax we don't support).
    body = stripPlatformConditionals(body);
    // Drop MonoGame's standard compatibility macros — the OPENGL branch
    // typically defines `SV_POSITION` → `POSITION` and `VS_SHADERMODEL` /
    // `PS_SHADERMODEL` for the desktop FX compiler's shader-model arg.
    // None of these have meaning in our GLSL ES 1.00 output and they'd
    // either be ignored or worse, expand `SV_POSITION` → `POSITION` (the
    // wrong direction; we've already aliased `: POSITION` → `: SV_POSITION`
    // in aliasSemantics above).
    body = stripMonoGameCompatDefines(body);
    // Alias DX9 semantics to their DX10 names before the entry-function
    // pass tries to match against the canonical set.
    body = aliasSemantics(body);
    body = replaceTypes(body);
    // Any Texture2D referenced by a `sampler_state { Texture = <X>; }`
    // literal is NOT a standalone GL sampler — the sampler_state's name
    // is what the user's `tex2D(name, uv)` calls reference. Emitting both
    // produces two MGFX sampler records with distinct textureSlots, and
    // KNI tries to bind a texture to a slot that doesn't exist in
    // BlazorGL's TextureCollection → IndexOutOfRangeException at draw time.
    // Pre-collect the names so translateTextureSamplerDecls can suppress
    // the redundant `uniform sampler2D X;` declaration + sampler record.
    const samplerStateLiterals = opts.samplerStateLiterals ?? [];
    const samplerStateAbsorbedTextures = new Set(
        samplerStateLiterals
            .map(l => l.textureRef)
            .filter((t): t is string => !!t),
    );
    const { source: withTextures, samplers: rawSamplers } = translateTextureSamplerDecls(
        body, samplerStateAbsorbedTextures,
    );
    body = withTextures;
    // Add samplers from sampler_state literals (FX parser already stripped
    // those blocks out of the source — we just emit the GLSL counterpart).
    const samplerStateSamplers = emitSamplerStateLiteralSamplers(samplerStateLiterals);

    // VS shaders never perform texture fetches in any of the .fx templates
    // this codebase ships. KNI's EffectPass.Apply walks each shader's MGFX
    // sampler records and binds textures to `_device.Textures[textureSlot]`
    // — if the VS has a sampler record but the linked GL program doesn't
    // reference that uniform (because the VS body doesn't `tex2D`),
    // BlazorGL's TextureCollection is sized to zero and `Textures[0] = …`
    // throws IndexOutOfRange at draw time. The offline MonoGame compiler
    // emits per-stage sampler reflection for the same reason. We still
    // keep the `uniform sampler2D …;` declarations in the VS GLSL because
    // the translator inlines the whole body (including MainPS) into both
    // stages — those declarations let MainPS's `texture2D(...)` calls
    // continue to parse cleanly in the VS-stage output, but the
    // declarations are unused uniforms (silently optimized out at link).
    const samplers = opts.stage === 'pixel' ? rawSamplers : [];
    const samplerRecords = opts.stage === 'pixel' ? samplerStateSamplers.records : [];
    body = translateMethodCalls(body);
    body = translateIntrinsics(body);

    // `mul(vec, matrix)` → explicit dot-product expansion. We need to know
    // which identifiers are matrices to do this correctly — pull them from
    // the FX-parsed cbuffer field list (any field with rows >= 2 is a mat).
    // This must come AFTER intrinsic renames (so `mul` is still the HLSL
    // token, not yet anything else) and BEFORE the entry rewriter (which
    // walks the function body).
    const matrixNames = collectMatrixNames(opts.cbuffers);
    if (matrixNames.size > 0) {
        body = translateMulCalls(body, matrixNames);
    }

    // Rename any GLSL-ES-1.00 reserved word the user used as an identifier
    // — `input` / `output` are the common offenders, since MonoGame's
    // canonical SpriteEffect.fx uses `MainPS(VS_OUT input)`. We do this
    // globally (not just inside the entry function) because a .fx with
    // both MainVS and MainPS has the reserved name in BOTH parameter
    // lists, and the translator emits both functions into the GLSL output
    // regardless of which stage it's compiling for.
    body = renameReservedWordIdentifiers(body);

    // Strip `: SEMANTIC` return annotations from EVERY function signature,
    // not just the active entrypoint's. The translator emits the whole .fx
    // body in the GLSL output regardless of which stage we're compiling
    // for, so a `float4 MainPS(...) : COLOR { … }` signature leaked in the
    // VS-stage output trips a GLSL "unexpected COLON, expecting LEFT_BRACE"
    // syntax error at parse time, even though only MainVS is the entry.
    body = stripFunctionReturnSemantics(body);

    // Extract struct field semantics BEFORE stripping them — once
    // translateStructDecls runs, the `: SEMANTIC` annotations are gone and
    // we can't recover them. The rewriter needs the semantics to wire each
    // struct field to a matching varying.
    const structs = extractStructInfo(body);
    body = translateStructDecls(body);

    const entryRewrite = rewriteEntryFunction(
        body, opts.entrypoint, opts.stage, diagnostics, structs,
    );
    body = entryRewrite.source;

    const cbufferGlsl = synthesizeCbufferGlsl(opts.cbuffers);
    // Use the SAME default float precision in both stages — `highp`. GLSL
    // ES 1.00 uniforms inherit the default float precision, so a `vec4`
    // uniform with the same name declared in two shaders with different
    // defaults links as `highp vec4` vs `mediump vec4` and WebGL rejects
    // them as "not linkable between attached shaders". Pixel-stage
    // implementations are allowed to default to mediump, but the cost of
    // a matched-highp uniform is negligible for the kinds of effects
    // this codebase targets, and the linker incompatibility was breaking
    // every effect that shared a cbuffer across VS and PS.
    const precision = '#ifdef GL_ES\nprecision highp float;\nprecision mediump int;\n#endif\n';

    // KNI's MGFX runtime populates `posFixup` on every VS that declares it
    // (Y-flip + depth-range remap for the DX→GL coordinate-space shift).
    // The pixel stage doesn't need it.
    const posFixupDecl = opts.stage === 'vertex' ? 'uniform vec4 posFixup;' : '';

    const allSamplers = [...samplers, ...samplerRecords];

    // Compose the GLSL output piece by piece, tracking how many lines come
    // BEFORE the user's body content. That count lets glslang errors at
    // GLSL line N be mapped back to .fx line (N - preambleLineCount).
    const pieces = [
        precision,
        posFixupDecl,
        samplerStateSamplers.glsl,
        cbufferGlsl,
        entryRewrite.varyingDecls,
    ].filter((s) => s.length > 0);
    const preamble = pieces.join('\n');
    // `pieces.join('\n')` + final `'\n'` before body → total preamble line
    // count is (newlines in preamble) + 1 (the join trailing newline below).
    const preambleLineCount = preamble.length === 0 ? 0 : countNewlines(preamble) + 1;
    const glsl = preamble.length === 0
        ? `${body}\n${entryRewrite.mainTrampoline}`
        : `${preamble}\n${body}\n${entryRewrite.mainTrampoline}`;

    return {
        glsl,
        samplers: allSamplers,
        attributes: entryRewrite.attributes,
        diagnostics,
        preambleLineCount,
    };
}

// ── Pass 0: platform preprocessor ──────────────────────────────────────────

// Strip `#if OPENGL / [ #else ] / #endif` blocks — we always emit OpenGL,
// so keep the OPENGL branch and drop the alternative. Matches the standard
// MonoGame cross-platform pattern:
//
//   #if OPENGL
//       #define PS_SHADERMODEL ps_3_0
//   #else
//       #define PS_SHADERMODEL ps_4_0_level_9_1
//   #endif
//
// We also handle `#if !OPENGL` (keep the #else branch instead).
function stripPlatformConditionals(src: string): string {
    let out = src;
    // Pattern: `#if OPENGL` (with optional `!`) ... `#else` ... `#endif`
    // Brace-counting on `#if` / `#endif` levels.
    const re = /#if\s+(!?)\s*OPENGL\b/g;
    while (true) {
        re.lastIndex = 0;
        const m = re.exec(out);
        if (!m) break;
        const negated = m[1] === '!';
        const ifStart = m.index;
        const ifEnd = m.index + m[0].length;
        // Walk forward through the source counting #if/#endif depth until
        // depth returns to 0, looking for the matching #else (depth 0) and
        // #endif (depth 0).
        let depth = 1;
        let elseAt = -1;
        let endifAt = -1;
        let endifEnd = -1;
        let i = ifEnd;
        while (i < out.length) {
            const c = out[i];
            if (c !== '#') { i++; continue; }
            // Check for #if / #else / #endif at column position c.
            const rest = out.slice(i);
            const mIf  = /^#if(?:def|ndef)?\b/.exec(rest);
            const mEls = /^#else\b/.exec(rest);
            const mEnd = /^#endif\b/.exec(rest);
            if (mIf) { depth++; i += mIf[0].length; continue; }
            if (mEls && depth === 1) { elseAt = i; i += mEls[0].length; continue; }
            if (mEnd) {
                depth--;
                if (depth === 0) { endifAt = i; endifEnd = i + mEnd[0].length; break; }
                i += mEnd[0].length; continue;
            }
            i++;
        }
        if (endifAt < 0) break;  // unterminated — give up
        // Keep block: chunk between ifEnd and (elseAt or endifAt). Discard
        // the rest of the conditional + the #if/#else/#endif tokens.
        let keepStart: number, keepEnd: number;
        if (elseAt < 0) {
            // No #else — body runs from ifEnd to endifAt.
            keepStart = ifEnd;
            keepEnd = endifAt;
            if (negated) { keepStart = ifEnd; keepEnd = ifEnd; }  // skip the body entirely
        } else if (!negated) {
            // Keep the "if" branch.
            keepStart = ifEnd;
            keepEnd = elseAt;
        } else {
            // Keep the "else" branch.
            keepStart = elseAt + '#else'.length;
            keepEnd = endifAt;
        }
        const keptBody = out.slice(keepStart, keepEnd);
        // Replace the whole `#if … #endif` region with the kept body PLUS
        // enough blank lines to preserve the total newline count from the
        // original range. Without this, glslang line numbers in error
        // messages drift from .fx line numbers — making "click error → jump
        // to source" land on the wrong line.
        const removedRange = out.slice(ifStart, endifEnd);
        const removedNewlines = countNewlines(removedRange);
        const keptNewlines = countNewlines(keptBody);
        const pad = '\n'.repeat(Math.max(0, removedNewlines - keptNewlines));
        const newOut = out.slice(0, ifStart) + keptBody + pad + out.slice(endifEnd);
        out = newOut;
    }
    return out;
}

function countNewlines(s: string): number {
    let n = 0;
    for (let i = 0; i < s.length; i++) {
        if (s[i] === '\n') n++;
    }
    return n;
}

// Strip the MonoGame OPENGL-branch compatibility defines. These live in the
// section the user keeps in their `#if OPENGL` block; once we've decided
// to emit GLSL, the macros are noise (and `SV_POSITION → POSITION` would
// actually reverse the alias we apply below).
const MONOGAME_COMPAT_MACROS = new Set([
    'SV_POSITION', 'SV_TARGET',
    'VS_SHADERMODEL', 'PS_SHADERMODEL',
]);
function stripMonoGameCompatDefines(src: string): string {
    // Use `[ \t]*` (NOT `\s*`) before `#define` so the regex doesn't gobble
    // up a preceding newline. `\s` includes `\n`, which under the /m flag
    // meant the match would start at the previous line's `\n`, replace it
    // with a single `\n`, and drop one line from the source. Subtle but
    // critical for line-number preservation: glslang errors then map back
    // to the wrong .fx line.
    return src.replace(/^[ \t]*#define\s+(\w+)\s+[^\n]*\n/gm, (full, name: string) => {
        return MONOGAME_COMPAT_MACROS.has(name) ? '\n' : full;
    });
}

// Replace DX9 semantic names with their DX10 equivalents so the entry-
// function pass has a single set to match against.
function aliasSemantics(src: string): string {
    let out = src;
    // `: POSITION` (DX9) → `: SV_POSITION` (DX10). Only when followed by a
    // word boundary so we don't mangle `POSITIONAL` or similar.
    out = out.replace(/:\s*POSITION\b/g, ': SV_POSITION');
    // `: COLOR` (DX9, in PS return) → `: SV_TARGET`. NOT `COLOR0` etc. —
    // those are still varying semantics. Match `COLOR` only when not
    // followed by a digit.
    out = out.replace(/:\s*COLOR(?!\d)\b/g, ': SV_TARGET');
    return out;
}

// ── Pass 1: type substitution ───────────────────────────────────────────────

function replaceTypes(src: string): string {
    // Order matters — replace longer types first so `float4x4` doesn't get
    // partially-matched as `float4` + `x4` debris.
    const subs: Array<[RegExp, string]> = [
        [/\bfloat4x4\b/g, 'mat4'],
        [/\bfloat3x3\b/g, 'mat3'],
        [/\bfloat2x2\b/g, 'mat2'],
        // `matrix` (unqualified) is an HLSL alias for `float4x4`.
        [/\bmatrix\b/g,   'mat4'],
        [/\bfloat4\b/g,   'vec4'],
        [/\bfloat3\b/g,   'vec3'],
        [/\bfloat2\b/g,   'vec2'],
        [/\bint4\b/g,     'ivec4'],
        [/\bint3\b/g,     'ivec3'],
        [/\bint2\b/g,     'ivec2'],
        [/\bhalf4\b/g,    'vec4'],
        [/\bhalf3\b/g,    'vec3'],
        [/\bhalf2\b/g,    'vec2'],
        [/\bhalf\b/g,     'float'],
    ];
    let out = src;
    for (const [re, repl] of subs) out = out.replace(re, repl);
    return out;
}

// ── Pass 2: texture + sampler declarations ─────────────────────────────────

function translateTextureSamplerDecls(
    src: string,
    samplerStateAbsorbedTextures: Set<string> = new Set(),
): {
    source: string;
    samplers: Array<{ name: string; samplerType: 0 | 1 | 2 }>;
} {
    const samplers: Array<{ name: string; samplerType: 0 | 1 | 2 }> = [];
    let out = src;

    // Texture2D/3D/Cube declarations → GLSL sampler uniforms.
    //
    // If the name appears in `samplerStateAbsorbedTextures`, a downstream
    // `sampler_state { Texture = <name>; }` literal will emit the GL
    // uniform under the sampler_state's name. In that case the
    // standalone `Texture2D` becomes a non-sampler declaration we just
    // strip — emitting it as a duplicate `uniform sampler2D` would
    // expand the MGFX sampler list and trip BlazorGL's TextureCollection
    // bounds at draw time.
    const texPatterns: Array<[RegExp, string, 0 | 1 | 2]> = [
        [/\bTexture2D(?:<[^>]+>)?\s+(\w+)(?:\s*:\s*register\([^)]*\))?\s*;/g, 'uniform sampler2D $1;', 0],
        [/\bTexture3D(?:<[^>]+>)?\s+(\w+)(?:\s*:\s*register\([^)]*\))?\s*;/g, 'uniform sampler3D $1;', 1],
        [/\bTextureCube(?:<[^>]+>)?\s+(\w+)(?:\s*:\s*register\([^)]*\))?\s*;/g, 'uniform samplerCube $1;', 2],
    ];
    for (const [re, repl, typ] of texPatterns) {
        out = out.replace(re, (_match, name) => {
            if (samplerStateAbsorbedTextures.has(name)) {
                // Drop the declaration entirely; the sampler_state literal
                // owns the GL uniform.
                return '';
            }
            samplers.push({ name, samplerType: typ });
            return repl.replace('$1', name);
        });
    }

    // SamplerState declarations get dropped — GL ES 1.00 doesn't separate
    // textures from samplers, so the texture binding above also covers the
    // sampler. Sampler filter/wrap state has to be configured GL-side via
    // `set effect param` or SpriteBatch's samplerState; we don't translate
    // the HLSL state-block contents into GL calls.
    out = out.replace(
        /\bSampler(?:State|ComparisonState)\s+\w+(?:\s*:\s*register\([^)]*\))?\s*;\s*/g,
        '',
    );

    return { source: out, samplers };
}

// ── Pass 3: method-call syntax ─────────────────────────────────────────────

function translateMethodCalls(src: string): string {
    // `tex.Sample(smp, uv)` → `texture2D(tex, uv)` — drop the sampler arg
    // since GL ES 1.00 binds sampler state to the texture unit, not to a
    // separate SamplerState object.
    let out = src;
    out = out.replace(
        /\b(\w+)\.Sample\s*\(\s*\w+\s*,\s*([^)]+?)\s*\)/g,
        'texture2D($1, $2)',
    );
    // SampleLevel: `tex.SampleLevel(smp, uv, lod)` → `texture2DLod(tex, uv, lod)`
    out = out.replace(
        /\b(\w+)\.SampleLevel\s*\(\s*\w+\s*,\s*([^,]+)\s*,\s*([^)]+)\s*\)/g,
        'texture2DLod($1, $2, $3)',
    );
    // DX9-era intrinsic `tex2D(smp, uv)` → `texture2D(smp, uv)`. Same arg
    // order; just a rename. Common in MonoGame's stock SpriteEffect.
    out = out.replace(/\btex2D\s*\(/g, 'texture2D(');
    out = out.replace(/\btex2Dlod\s*\(/g, 'texture2DLod(');
    return out;
}

// Emit `uniform sampler2D <name>;` for each DX9-style sampler_state literal
// the FX parser extracted, and build the matching MGFX sampler records.
// Without this, a shader like `sampler2D Smp = sampler_state { Texture = <T>; };`
// would survive as a dropped-block in the source (FX parser strips it) but
// have no GL counterpart — the user's `tex2D(Smp, uv)` would fail to link
// against an undefined uniform.
function emitSamplerStateLiteralSamplers(literals: FxSamplerStateLiteral[]): {
    glsl: string;
    records: Array<{ name: string; samplerType: 0 | 1 | 2 }>;
} {
    if (literals.length === 0) return { glsl: '', records: [] };
    const lines: string[] = [];
    const records: Array<{ name: string; samplerType: 0 | 1 | 2 }> = [];
    for (const lit of literals) {
        // Map the HLSL sampler type to GLSL.
        let glslType = 'sampler2D';
        let samplerType: 0 | 1 | 2 = 0;
        const t = lit.samplerType;
        if (t === 'sampler3D') { glslType = 'sampler3D'; samplerType = 1; }
        else if (t === 'samplerCUBE') { glslType = 'samplerCube'; samplerType = 2; }
        lines.push(`uniform ${glslType} ${lit.samplerName};`);
        records.push({ name: lit.samplerName, samplerType });
    }
    return { glsl: lines.join('\n'), records };
}

// ── Pass 4: intrinsic renames ──────────────────────────────────────────────

function translateIntrinsics(src: string): string {
    let out = src;
    // saturate(x) → clamp(x, 0.0, 1.0). We wrap the arg in parens to be
    // safe against precedence surprises in user-supplied expressions.
    out = out.replace(/\bsaturate\s*\(/g, 'clamp((');
    // Pair the opening `clamp((` with the matching `)` we'd otherwise close
    // — handled by appending `, 0.0, 1.0)` before the closing paren. Brace-
    // counting walk:
    out = closeSaturate(out);

    out = out.replace(/\blerp\s*\(/g, 'mix(');
    out = out.replace(/\bfrac\s*\(/g, 'fract(');
    out = out.replace(/\brsqrt\s*\(/g, 'inversesqrt(');
    out = out.replace(/\batan2\s*\(/g, 'atan(');
    return out;
}

// Walk the source and finish each `clamp((<expr>` (from saturate
// translation) by inserting `, 0.0, 1.0)` — the 3 clamp args plus the
// closing paren for the outer `clamp(`. We balance parens to find the
// end of the inner expression (matching saturate's original `)`).
//
// Original:  saturate(<expr>)                  — 1 open, 1 close
// Step 1:    clamp((<expr>)                    — 2 open, 1 close (after `saturate(` → `clamp((`)
// Step 2:    clamp((<expr>), 0.0, 1.0)         — 2 open, 2 close
//
// Without the closing `)` inserted in step 2, the outer `clamp(` never
// gets balanced and downstream tokens get consumed as part of clamp's
// arg list. That was the Posterize bug: `floor(saturate(c) * L)` became
// `floor(clamp((c), 0.0, 1.0 * L)` — `* L` got absorbed into the
// outermost clamp arg, breaking the math AND leaving floor's `)` unmatched.
function closeSaturate(src: string): string {
    const marker = 'clamp((';
    let result = src;
    let idx = result.indexOf(marker);
    while (idx >= 0) {
        const exprStart = idx + marker.length;
        let depth = 1;
        let i = exprStart;
        while (i < result.length && depth > 0) {
            const c = result[i];
            if (c === '(') depth++;
            else if (c === ')') {
                depth--;
                if (depth === 0) break;
            }
            i++;
        }
        if (i >= result.length) break;  // unbalanced — bail
        // result[i] is the closing `)` of saturate's original arg, which
        // becomes the closing of clamp's INNER paren. Insert `, 0.0, 1.0)`
        // after it — that's the remaining two clamp args plus the OUTER
        // close paren for the new clamp call.
        const insertion = ', 0.0, 1.0)';
        result = result.slice(0, i + 1) + insertion + result.slice(i + 1);
        idx = result.indexOf(marker, i + insertion.length + 1);
    }
    return result;
}

// ── Struct declarations ────────────────────────────────────────────────────

interface StructDecl {
    name: string;
    fields: Array<{ type: string; name: string; semantic: string | null }>;
}

// Walk the source for top-level `struct Name { Type field : SEMANTIC; … };`
// blocks and record their field-level semantics. The semantics get stripped
// from the source by `translateStructDecls` (so GLSL doesn't choke), and
// the recorded info is used by `rewriteEntryFunction` to wire each field
// to a matching `varying` (PS) or `attribute` (VS) in the main() trampoline.
function extractStructInfo(src: string): StructDecl[] {
    const out: StructDecl[] = [];
    const re = /\bstruct\s+(\w+)\s*\{([^}]*)\}/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(src)) !== null) {
        const name = m[1];
        const body = m[2];
        const fields: StructDecl['fields'] = [];
        // Each field: `<type> <name> [: SEMANTIC]? ;`
        const fre = /(\w+)\s+(\w+)(?:\s*:\s*(\w+))?\s*;/g;
        let fm: RegExpExecArray | null;
        while ((fm = fre.exec(body)) !== null) {
            fields.push({ type: fm[1], name: fm[2], semantic: fm[3] ?? null });
        }
        out.push({ name, fields });
    }
    return out;
}

// Strip the per-field `: SEMANTIC` annotations from struct declarations so
// the GLSL parser accepts the body. The struct keyword itself stays.
//
// PRESERVE the whitespace between `name` and `{` (captured as `between`).
// MonoGame stock shaders use `struct Name\n{\n…\n};` on multiple lines;
// collapsing the newline before `{` shifts every subsequent line up by 1,
// which made glslang error-line numbers map back to the wrong .fx line.
function translateStructDecls(src: string): string {
    return src.replace(
        /\bstruct\s+(\w+)(\s*)\{([^}]*)\}/g,
        (_full, name: string, between: string, body: string) => {
            const cleaned = body.replace(/(\w+\s+\w+)\s*:\s*\w+\s*;/g, '$1;');
            return `struct ${name}${between}{${cleaned}}`;
        },
    );
}

// ── Pass 5: entry-function rewriting ───────────────────────────────────────

interface EntryRewriteResult {
    source: string;            // body with entry signature stripped of semantics
    varyingDecls: string;      // `varying vec4 vTexCoord0;` lines
    mainTrampoline: string;    // `void main() { gl_FragColor = MainPS(vTexCoord0.xy); }`
    attributes: TranslateResult['attributes'];   // VS input reflection (empty for PS)
}

// GLSL ES 1.00 reserves these for future use. If a user's HLSL source
// uses one of these as an identifier — typically `MainPS(VS_OUT input)`
// — KNI's GL compiler rejects with "Illegal use of reserved word". We
// rewrite the identifier with a leading underscore everywhere it appears
// (as a word boundary), so even when the translator emits both MainVS
// and MainPS into the GLSL output of either stage, neither hits the
// reserved-word check.
function renameReservedWordIdentifiers(src: string): string {
    let out = src;
    for (const word of GLSL_RESERVED_WORDS) {
        const re = new RegExp(`\\b${word}\\b`, 'g');
        out = out.replace(re, '_' + word);
    }
    return out;
}

// Strip `) : SEMANTIC {` → `) {` from every function signature. The
// entry-function rewriter handles the active stage's entry separately
// (because it also needs the semantic to wire the return value to
// gl_FragColor / gl_Position), but the OTHER stage's entry still lives in
// the body of the GLSL output and its `: COLOR` / `: SV_TARGET` would
// otherwise reach the GLSL parser.
function stripFunctionReturnSemantics(src: string): string {
    return src.replace(
        /(\))\s*:\s*\w+(\s*\{)/g,
        (_full, close: string, brace: string) => `${close}${brace}`,
    );
}

const GLSL_RESERVED_WORDS = new Set([
    'input', 'output',
    'common', 'partition', 'active', 'asm', 'class', 'union', 'enum',
    'typedef', 'template', 'this', 'goto', 'inline', 'noinline', 'public',
    'static', 'extern', 'external', 'interface', 'long', 'short', 'double',
    'fixed', 'unsigned', 'superp', 'cast', 'namespace', 'using',
    'layout', 'packed',
]);

function rewriteEntryFunction(
    src: string,
    entrypoint: string,
    stage: 'vertex' | 'pixel',
    diagnostics: TranslateResult['diagnostics'],
    structs: StructDecl[],
): EntryRewriteResult {
    // Locate the entry function declaration. Expected shape, with types
    // already substituted to GLSL:
    //
    //   <retType> <entrypoint>( <params> ) [ : <retSemantic> ] {
    //
    // <params> can have HLSL-style `: SEMANTIC` after each parameter name.
    // We need to:
    //   (a) capture each param's name + type + semantic
    //   (b) strip the per-param `: SEMANTIC` annotations
    //   (c) strip the return `: SEMANTIC` annotation
    //   (d) generate `varying` declarations for each semantic
    //   (e) emit a `void main()` trampoline that calls the entry function
    //       and assigns the return to `gl_FragColor` / `gl_Position`.

    // Permissive type pattern — entry can return any GLSL primitive OR any
    // user-declared struct name. We require the match to be followed by `{`
    // (after optional `: SEMANTIC` annotation) so identifiers that happen to
    // share a name with the entrypoint elsewhere in the body don't get
    // mistaken for the entry's signature.
    const structNames = structs.map(s => escapeRe(s.name)).join('|');
    const knownTypes = `vec4|vec3|vec2|float|mat4|mat3${structNames ? '|' + structNames : ''}`;
    const re = new RegExp(
        `\\b(${knownTypes})\\s+(${escapeRe(entrypoint)})\\s*\\(([^)]*)\\)`,
    );
    const match = re.exec(src);
    if (!match) {
        diagnostics.push({
            severity: 'warning',
            message: `Entry point '${entrypoint}' not found — leaving source unchanged. ` +
                     `Make sure the function name in 'compile ps_4_0 ${entrypoint}();' ` +
                     `matches a function declaration in the shader body.`,
        });
        return { source: src, varyingDecls: '', mainTrampoline: '', attributes: [] };
    }
    const [fullMatch, retType, fname, paramList] = match;
    const sigStart = match.index;
    const sigEnd = sigStart + fullMatch.length;

    // Look past the close-paren for an optional `: SEMANTIC`. Skip whitespace.
    let after = sigEnd;
    while (after < src.length && /\s/.test(src[after])) after++;
    let retSemantic: string | null = null;
    let retSemanticEnd = sigEnd;
    if (src[after] === ':') {
        let semStart = after + 1;
        while (semStart < src.length && /\s/.test(src[semStart])) semStart++;
        let semEnd = semStart;
        while (semEnd < src.length && /[A-Za-z0-9_]/.test(src[semEnd])) semEnd++;
        retSemantic = src.slice(semStart, semEnd);
        retSemanticEnd = semEnd;
    }

    // Parse params: each `<type> <name> [ : SEMANTIC ]?`, comma-separated.
    const rawParams = parseParamList(paramList);

    // Rename any parameter whose name collides with a GLSL ES 1.00 reserved
    // word (notably `input` and `output`). We rename to `_<name>` here AND
    // walk the function body to apply the same rename to references like
    // `input.Color`. Without this pass, the user's `MainPS(VS_OUT input)`
    // compiles to GLSL that KNI rejects with "Illegal use of reserved word".
    const renames = new Map<string, string>();
    const params = rawParams.map((p) => {
        if (GLSL_RESERVED_WORDS.has(p.name)) {
            const newName = '_' + p.name;
            renames.set(p.name, newName);
            return { ...p, name: newName };
        }
        return p;
    });

    // Build the cleaned signature (no per-param semantics, no return
    // semantic). Note: if a param's type is a known struct, the struct
    // itself doesn't carry a semantic — its fields do, and the trampoline
    // builds the struct from per-field varyings below.
    const cleanedParams = params
        .map((p) => `${p.type} ${p.name}`)
        .join(', ');
    const cleanedSig = `${retType} ${fname}(${cleanedParams})`;

    // Splice the cleaned signature back into the source, then walk the
    // function body to apply rename mappings if any.
    let newSource =
        src.slice(0, sigStart) +
        cleanedSig +
        src.slice(retSemanticEnd);
    if (renames.size > 0) {
        // After the splice, the body's `{` is somewhere right after the
        // cleaned signature. Find it, match the closing `}`, rewrite refs
        // inside (scoped — don't touch other functions that happen to use
        // the same identifier).
        const bodyOpen = newSource.indexOf('{', sigStart);
        if (bodyOpen >= 0) {
            const bodyClose = findMatchingBrace(newSource, bodyOpen);
            if (bodyClose > 0) {
                let body = newSource.slice(bodyOpen, bodyClose + 1);
                for (const [from, to] of renames) {
                    body = body.replace(
                        new RegExp(`\\b${escapeRe(from)}\\b`, 'g'),
                        to,
                    );
                }
                newSource = newSource.slice(0, bodyOpen) + body + newSource.slice(bodyClose + 1);
            }
        }
    }

    // Per-stage cross-stage I/O declarations.
    //   - PS reads from `varying` (no outputs declared; gl_FragColor is built-in)
    //   - VS reads from `attribute` (per-vertex), writes to `varying` (interpolated)
    //
    // VS attribute names come from SEMANTIC_TO_ATTRIBUTE (`aPosition0`,
    // `aColor0`, `aTexCoord0`, …) while VS-output / PS-input varyings come
    // from SEMANTIC_TO_VARYING (`vFrontColor`, `vTexCoord0`, …). Distinct
    // namespaces prevent the GLSL "X : redefinition" error you'd get if
    // a VS that passed COLOR0 through declared `attribute vec4 vFrontColor`
    // AND `varying vec4 vFrontColor` in the same shader.
    const varyingLines: string[] = [];
    const declaredInputs = new Set<string>();
    const declaredOutputs = new Set<string>();
    const reflectedAttributes: TranslateResult['attributes'] = [];
    const trampolineArgs: string[] = [];
    const trampolinePrelude: string[] = [];     // local construction statements

    // Direction-aware declaration emitter. `direction` describes the role
    // for the current stage:
    //   'in'  → PS varying input / VS attribute input
    //   'out' → VS varying output (PS has no equivalent — gl_FragColor only)
    const ensureVarying = (
        sem: string,
        direction: 'in' | 'out' = 'in',
    ): { varName: string; varType: string } => {
        const isVsInput = direction === 'in' && stage === 'vertex';
        const attrInfo = isVsInput ? SEMANTIC_TO_ATTRIBUTE[sem] : undefined;
        const varName = attrInfo
            ? attrInfo.name
            : (SEMANTIC_TO_VARYING[sem] ?? `v${capitalize(sem)}`);
        const varType = SEMANTIC_VARYING_TYPE[sem] ?? 'vec4';
        const keyword = isVsInput ? 'attribute' : 'varying';
        const seen = direction === 'in' ? declaredInputs : declaredOutputs;
        if (!seen.has(varName)) {
            // `SV_POSITION` in a PS input comes from `gl_FragCoord`, and in
            // a VS output goes to `gl_Position` — neither needs a user-side
            // declaration. The struct-assembly code below covers both cases.
            if (sem !== 'SV_POSITION') {
                varyingLines.push(`${keyword} ${varType} ${varName};`);
                if (attrInfo) {
                    reflectedAttributes.push({
                        name: attrInfo.name,
                        usage: attrInfo.usage,
                        index: attrInfo.index,
                        location: reflectedAttributes.length,
                    });
                }
            }
            seen.add(varName);
        }
        return { varName, varType };
    };

    for (const p of params) {
        // Is this a struct type? If so, emit varyings for each struct
        // field's semantic and assemble the struct from those varyings in
        // the trampoline prelude.
        const structDecl = structs.find(s => s.name === p.type);
        if (structDecl) {
            // Each field of the struct gets its own varying (semantic-keyed).
            // The trampoline declares a local of the struct type, fills each
            // field, then passes it to the entry function.
            trampolinePrelude.push(`    ${p.type} ${p.name};`);
            for (const field of structDecl.fields) {
                if (!field.semantic) continue;
                const sem = field.semantic.toUpperCase();
                if (sem === 'SV_POSITION') {
                    // PS input: pull from gl_FragCoord. (For VS this would
                    // be the output, handled differently — we don't generate
                    // a VS trampoline for this case yet.)
                    if (stage === 'pixel') {
                        trampolinePrelude.push(`    ${p.name}.${field.name} = gl_FragCoord;`);
                    }
                    continue;
                }
                const { varName, varType } = ensureVarying(sem);
                const swizzle = buildSwizzle(varType, field.type);
                trampolinePrelude.push(`    ${p.name}.${field.name} = ${varName}${swizzle};`);
            }
            trampolineArgs.push(p.name);
            continue;
        }

        // Scalar / vector parameter with its own semantic.
        if (!p.semantic) {
            diagnostics.push({
                severity: 'warning',
                message: `Parameter '${p.name}' on entry '${fname}' has no semantic; ` +
                         `defaulting to TEXCOORD0. Add ': TEXCOORDn' or another semantic to silence this.`,
            });
        }
        const sem = (p.semantic ?? 'TEXCOORD0').toUpperCase();
        const { varName, varType } = ensureVarying(sem);
        const swizzle = buildSwizzle(varType, p.type);
        trampolineArgs.push(`${varName}${swizzle}`);
    }

    // Trampoline. The shape depends on (stage, return type):
    //
    //   pixel + primitive return → gl_FragColor = MainPS(args);
    //   pixel + struct return   → not used in practice (skip)
    //   vertex + primitive return → gl_Position = MainVS(args); + posFixup tail
    //   vertex + struct return  → call returns a local struct, decompose
    //                              fields into gl_Position + varyings by
    //                              their semantics, then posFixup tail.
    //
    // `varying` declarations cover both PS inputs (sources) and VS outputs
    // (sinks) — GLSL ES 1.00 uses the same keyword on both sides of the
    // link.
    const returnStruct = structs.find(s => s.name === retType);
    const callExpr = `${fname}(${trampolineArgs.join(', ')})`;
    const trampolineTail: string[] = [];

    if (stage === 'vertex' && returnStruct) {
        trampolineTail.push(`    ${retType} _vs_out = ${callExpr};`);
        for (const field of returnStruct.fields) {
            if (!field.semantic) continue;
            const sem = field.semantic.toUpperCase();
            if (sem === 'SV_POSITION') {
                trampolineTail.push(`    gl_Position = _vs_out.${field.name};`);
                continue;
            }
            // Output varying (written by VS, read by PS).
            const { varName, varType } = ensureVarying(sem, 'out');
            const rhs = widenToVarying(`_vs_out.${field.name}`, field.type, varType);
            trampolineTail.push(`    ${varName} = ${rhs};`);
        }
        emitPosFixupTail(trampolineTail);
    } else if (stage === 'vertex') {
        // Primitive (vec4) return → already a position. Best-effort widen.
        const assignExpr = retType === 'vec4' ? callExpr : `vec4(${callExpr})`;
        trampolineTail.push(`    gl_Position = ${assignExpr};`);
        emitPosFixupTail(trampolineTail);
    } else {
        // Pixel stage, primitive return.
        const assignExpr = retType === 'vec4' ? callExpr : `vec4(${callExpr})`;
        trampolineTail.push(`    gl_FragColor = ${assignExpr};`);
    }

    const preludeLines = trampolinePrelude.length > 0
        ? trampolinePrelude.join('\n') + '\n'
        : '';
    const mainTrampoline =
        `void main() {\n${preludeLines}${trampolineTail.join('\n')}\n}`;

    // Silently consume the unused retSemantic — only used to know we
    // *had* one so we could splice past it. Type-narrow the var so TS
    // doesn't flag it as unread.
    void retSemantic;

    return {
        source: newSource,
        varyingDecls: varyingLines.join('\n'),
        mainTrampoline,
        attributes: reflectedAttributes,
    };
}

// DirectX → OpenGL coordinate-space fixup that KNI's runtime drives
// through the `posFixup` uniform (Y flip + depth-range remap).
// Mirrors the tail that MonoGame's offline compiler emits at the end of
// every compiled vertex shader.
function emitPosFixupTail(out: string[]): void {
    out.push(`    gl_Position.y = gl_Position.y * posFixup.y;`);
    out.push(`    gl_Position.xy += posFixup.zw * gl_Position.ww;`);
    out.push(`    gl_Position.z = gl_Position.z * 2.0 - gl_Position.w;`);
}

// When the struct field's GLSL type is narrower than the varying type
// (e.g. struct holds `float2 TexCoord` but the carrier varying is vec4),
// pad with zeros so the assignment is well-typed.
function widenToVarying(expr: string, srcType: string, dstType: string): string {
    const srcDim = GLSL_TYPE_DIMS[srcType] ?? 4;
    const dstDim = GLSL_TYPE_DIMS[dstType] ?? 4;
    if (srcDim >= dstDim) return expr;
    const padCount = dstDim - srcDim;
    return `vec${dstDim}(${expr}${', 0.0'.repeat(padCount)})`;
}

interface ParsedParam {
    type: string;
    name: string;
    semantic: string | null;
}

function parseParamList(list: string): ParsedParam[] {
    const trimmed = list.trim();
    if (trimmed.length === 0) return [];
    // Split on commas; HLSL params can't contain commas at this level
    // (templates aren't supported in our subset).
    return trimmed.split(',').map((part) => {
        const m = /^\s*(\w+)\s+(\w+)(?:\s*:\s*(\w+))?\s*$/.exec(part);
        if (!m) return { type: 'vec4', name: 'unknown', semantic: null };
        return { type: m[1], name: m[2], semantic: m[3] ?? null };
    });
}

function buildSwizzle(srcType: string, dstType: string): string {
    const srcDim = GLSL_TYPE_DIMS[srcType] ?? 4;
    const dstDim = GLSL_TYPE_DIMS[dstType] ?? 4;
    if (srcDim === dstDim) return '';
    if (srcDim < dstDim) return '';  // can't widen by swizzle; let GL fail
    const map = ['x', 'y', 'z', 'w'];
    return '.' + map.slice(0, dstDim).join('');
}

function capitalize(s: string): string {
    return s.length === 0 ? s : s[0].toUpperCase() + s.slice(1).toLowerCase();
}

function escapeRe(s: string): string {
    return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

// Find the index of the `}` that pairs with the `{` at `openIdx`. Returns
// -1 if unbalanced (don't rewrite — leave the source alone rather than
// corrupt it). Brace-only counting; doesn't peek inside strings/comments
// because by this point in the pipeline both are already stripped or
// were never present (FX bodies don't typically contain string literals).
function findMatchingBrace(src: string, openIdx: number): number {
    if (src[openIdx] !== '{') return -1;
    let depth = 1;
    for (let i = openIdx + 1; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}') {
            if (--depth === 0) return i;
        }
    }
    return -1;
}

// ── Pass 6: synthesize cbuffer GLSL stubs ─────────────────────────────────

// For each FX-parsed cbuffer, emit a GLSL uniform array sized to fit all
// fields, plus `#define`s aliasing each field to the right slot. The
// runtime uploads cbuffer data into the array via glUniform4fv on each
// Pass.Apply(); the user's HLSL field references (`Tint`, `Time`, …) are
// rewritten by the C preprocessor to `ps_uniforms_vec4[k]` accesses.
function synthesizeCbufferGlsl(cbuffers: FxCbufferDecl[]): string {
    const lines: string[] = [];
    for (const cb of cbuffers) {
        const slotCount = Math.max(1, Math.ceil(cb.sizeInBytes / 16));
        lines.push(`uniform vec4 ${cb.name}[${slotCount}];`);
        for (const field of cb.fields) {
            const slot = Math.floor(field.offsetBytes / 16);
            // HLSL packs multiple scalars / short vectors into a single
            // vec4 slot. A vec2 can live at .xy or .zw of one slot; four
            // scalars can share .x/.y/.z/.w. The previous alias logic
            // ignored the in-slot byte offset entirely, so any second
            // scalar in the same vec4 (e.g. `float Time; float Glitch;`)
            // got aliased to `.x` exactly like the first one — only the
            // first scalar's value was actually visible to the shader.
            const byteOffsetInSlot = field.offsetBytes - slot * 16;
            lines.push(buildFieldAlias(cb.name, slot, byteOffsetInSlot, field));
        }
    }
    return lines.join('\n');
}

// Build a `#define` macro that aliases a cbuffer field to the right slot
// of the uniform array. The slot/swizzle depends on the field's size:
//
//   float4 at offset 0  → #define Tint  ps_uniforms_vec4[0]
//   float  at offset 16 → #define Time  ps_uniforms_vec4[1].x
//   float2 at offset 32 → #define Scale ps_uniforms_vec4[2].xy
//   float3 at offset 48 → #define Up    ps_uniforms_vec4[3].xyz
//
// Matrices need a different shape because `MatrixTransform[i]` has to keep
// indexing meaningful (each `i` selects row `i` of the matrix). When the
// matrix lives at offset 0 we can simply alias the field name to the whole
// cbuffer array: `#define MatrixTransform ps_uniforms_vec4` makes
// `MatrixTransform[i]` expand to `ps_uniforms_vec4[i]`, which is exactly
// what `mul(vec, matrix)` expansion expects.
function buildFieldAlias(
    cbName: string,
    slot: number,
    byteOffsetInSlot: number,
    field: FxCbufferField,
): string {
    const cols = field.columns;
    const rows = field.rows;
    if (rows > 1) {
        // Matrix at offset 0: aliasing the whole array works.
        if (slot === 0) return `#define ${field.name} ${cbName}`;
        // Matrix at offset > 0: not common — fall back to slot reference
        // (the user's `mul(v, M)` expansion still needs M[i]; if M doesn't
        // start at slot 0, the user has to manually unpack rows for now).
        return `#define ${field.name} ${cbName}[${slot}]`;
    }
    if (field.arraySize > 0) {
        return `#define ${field.name} ${cbName}[${slot}]`;
    }
    // Pick the swizzle for vec1/vec2/vec3 based on which 4-byte lane
    // inside the vec4 slot the field starts at. HLSL packs scalars
    // tightly (.x → .y → .z → .w) and vec2 may land at .xy OR .zw of
    // a slot it shares with two preceding scalars.
    const laneIndex = byteOffsetInSlot / 4;   // 0, 1, 2, or 3
    const swizzle = swizzleForLaneAndWidth(laneIndex, cols);
    if (cols === 4 && swizzle === '.xyzw') {
        // Whole-slot vec4 reads as the slot itself, no swizzle needed.
        return `#define ${field.name} ${cbName}[${slot}]`;
    }
    return `#define ${field.name} ${cbName}[${slot}]${swizzle}`;
}

// Compose the GLSL swizzle string for a field that occupies `width`
// components starting at lane `start` (0/1/2/3) within a vec4. `width`
// is the HLSL columns count (1=scalar, 2=vec2, 3=vec3, 4=vec4). The
// returned string starts with `.` (e.g. `.x`, `.yz`, `.yzw`).
function swizzleForLaneAndWidth(start: number, width: number): string {
    const lanes = ['x', 'y', 'z', 'w'];
    const safeStart = Math.max(0, Math.min(3, start));
    const safeWidth = Math.max(1, Math.min(4 - safeStart, width));
    return '.' + lanes.slice(safeStart, safeStart + safeWidth).join('');
}

// Names of cbuffer fields whose type is a matrix (rows >= 2). Only these
// need `mul(v, M)` expansion. Plain vectors / scalars are passed through.
function collectMatrixNames(cbuffers: FxCbufferDecl[]): Set<string> {
    const out = new Set<string>();
    for (const cb of cbuffers) {
        for (const f of cb.fields) {
            if (f.rows >= 2) out.add(f.name);
        }
    }
    return out;
}

// Replace `mul(<expr>, MatrixName)` with the explicit dot-product expansion
// across 4 rows of the matrix. Mirrors what MonoGame's offline compiler
// emits — sourced from FadeSpriteBatchEffect.xnb's compiled VS, which uses
// the same `dot(vs_v0, vs_c0..3)` pattern KNI's MGFX writer feeds into the
// runtime.
//
// `<expr>` is the entire token-balanced left arg up to the matching `,`
// before `MatrixName`. We use a small paren-aware scanner instead of a
// regex so calls like `mul(mul(p, A), MatrixTransform)` round-trip.
function translateMulCalls(src: string, matrixNames: Set<string>): string {
    if (matrixNames.size === 0) return src;
    const matrixAlt = [...matrixNames].map(escapeRe).join('|');
    // Anchor on the `, MatrixName)` tail; walk back to find the matching
    // opening paren of the surrounding `mul(`.
    const tailRe = new RegExp(`,\\s*(${matrixAlt})\\s*\\)`, 'g');
    let out = '';
    let cursor = 0;
    let m: RegExpExecArray | null;
    tailRe.lastIndex = 0;
    while ((m = tailRe.exec(src))) {
        const matName = m[1];
        const commaIdx = m.index;
        const closeIdx = commaIdx + m[0].length - 1;
        // Find the matching `mul(` to the left of commaIdx.
        const openIdx = findMatchingMulOpen(src, commaIdx);
        if (openIdx < 0) continue;
        // Confirm the keyword right before `(` is exactly `mul` (no other
        // function name).
        if (!isMulCallStart(src, openIdx)) continue;
        const argStart = openIdx + 1;     // just past `(`
        const argEnd = commaIdx;          // just before `,`
        const argExpr = src.slice(argStart, argEnd).trim();
        const expansion =
            `vec4(` +
            `dot(${argExpr}, ${matName}[0]), ` +
            `dot(${argExpr}, ${matName}[1]), ` +
            `dot(${argExpr}, ${matName}[2]), ` +
            `dot(${argExpr}, ${matName}[3]))`;
        const mulStart = openIdx - 'mul'.length;
        out += src.slice(cursor, mulStart) + expansion;
        cursor = closeIdx + 1;
        // Continue scanning past the expansion site.
        tailRe.lastIndex = cursor;
    }
    out += src.slice(cursor);
    return out;
}

// Walk left from `commaIdx` to find the `(` whose argument list this
// `, MatrixName)` is closing. We need paren-depth awareness so that nested
// `mul()` or `vec4(...)` inside the first arg doesn't confuse us.
function findMatchingMulOpen(src: string, commaIdx: number): number {
    let depth = 0;
    for (let i = commaIdx - 1; i >= 0; i--) {
        const c = src[i];
        if (c === ')') depth++;
        else if (c === '(') {
            if (depth === 0) return i;
            depth--;
        }
    }
    return -1;
}

// `(` is at `openIdx`. The identifier ending at `openIdx-1` should be
// exactly `mul` (with no longer name like `_mul` matching by accident).
function isMulCallStart(src: string, openIdx: number): boolean {
    // Skip any whitespace right before the `(`.
    let i = openIdx - 1;
    while (i >= 0 && /\s/.test(src[i])) i--;
    if (i < 2 || src[i] !== 'l' || src[i - 1] !== 'u' || src[i - 2] !== 'm') return false;
    // Make sure the char before `m` isn't another identifier char (so we
    // don't latch onto `someMul(...)`).
    const before = i - 3;
    if (before < 0) return true;
    return !/[A-Za-z0-9_]/.test(src[before]);
}
