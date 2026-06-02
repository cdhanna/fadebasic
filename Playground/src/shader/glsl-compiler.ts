// GLSL-passthrough ShaderCompiler.
//
// This is the v1 compiler we ship by default. It treats the FX-stripped
// source as GLSL ES 3.00 (the format KNI's OpenGL profile actually loads
// into the MGFX bytecode slot), does lightweight text-based reflection
// to extract samplers/uniform-blocks/attributes, and returns the source
// verbatim as the GLSL bytecode.
//
// Why this is enough for now:
//
//   - KNI BlazorGL stores GLSL strings, not SPIR-V, in MGFX `Shader.bytecode`.
//     So no SPIR-V round-trip is required when the user already wrote GLSL.
//   - The Playground's `.fx` framing parser doesn't care whether the inner
//     shader code is HLSL or GLSL — it just strips technique/pass/sampler_state
//     blocks. A user porting from MonoGame desktop has two choices:
//        (a) write the shader body in GLSL inside the .fx framing (works today)
//        (b) wait for the HLSL→GLSL WASM upgrade (works tomorrow)
//
// Upgrade path:
//
//   When HLSL input is needed, swap this module out for a WASM-backed
//   compiler via `setShaderCompilerFactory(() => createHlslWasmCompiler())`.
//   The HLSL pipeline is:
//        HLSL source → glslang(HLSL frontend) → SPIR-V → spirv-cross → GLSL ES
//   Both stages are well-trodden Khronos WASM modules; the only reason
//   they're not shipped today is the ~5-8 MB combined bundle weight, which
//   we'd want behind a lazy import on first .fx compile.
//
//   The reflection layer below (extractReflection) is also good enough as
//   a v1: spirv-cross gives strictly more accurate reflection metadata,
//   but the text scan handles every uniform/sampler/attribute shape we've
//   seen in real-world ScreenEffect-style shaders.

import type {
    CompileHlslOptions,
    CompiledAttribute,
    CompiledConstantBuffer,
    CompiledSampler,
    CompiledShaderEntry,
    ShaderCompiler,
} from './shader-compiler';

export function createGlslPassthroughCompiler(): ShaderCompiler {
    return {
        async compileHlsl(opts: CompileHlslOptions): Promise<CompiledShaderEntry> {
            const reflection = extractReflection(opts.source);
            const wrapped = wrapAsEntrypoint(opts.source, opts.entrypoint, opts.stage);
            const normalized = normalizeGlslPreamble(wrapped, opts.stage);
            const compatDiagnostics = checkEs100Compatibility(normalized);

            return {
                glslSource: normalized,
                samplers: reflection.samplers,
                attributes: reflection.attributes,
                cbuffers: reflection.cbuffers,
                diagnostics: [...reflection.diagnostics, ...compatDiagnostics],
            };
        },
    };
}

// ── ES 1.00 compatibility check ─────────────────────────────────────────────

// KNI's BlazorGL backend runs on WebGL 1.0 / GLSL ES 1.00 only — anything
// declaring a 3xx `#version`, using uniform blocks, or using ES 3.00-only
// keywords (`in`/`out` as global qualifiers, `texture(…)` instead of
// `texture2D(…)`) will fail at GL link time with an opaque "Shader
// Compilation Failed" / "unsupported shader version" error. Surface these
// up-front so the user sees the actual cause.
function checkEs100Compatibility(source: string) {
    const diagnostics: Array<{
        severity: 'error' | 'warning' | 'info';
        message: string;
        line?: number;
    }> = [];

    const versionMatch = source.match(/#version\s+(\d+)(?:\s+es)?/);
    if (versionMatch) {
        const v = parseInt(versionMatch[1], 10);
        if (v >= 110) {
            diagnostics.push({
                severity: 'error',
                message:
                    `KNI's BlazorGL runs WebGL 1.0 (GLSL ES 1.00). Found "${versionMatch[0]}" — ` +
                    `remove the #version directive (ES 1.00 is the default), or use "#version 100". ` +
                    `Also migrate ES 3.00 syntax to ES 1.00: ` +
                    `replace 'in'/'out' globals with 'varying', 'texture()' with 'texture2D()', ` +
                    `'layout(std140) uniform { … }' with plain 'uniform' declarations, ` +
                    `and custom 'out vec4 …' with 'gl_FragColor'.`,
            });
        }
    }

    if (/\blayout\s*\(\s*std140/.test(source)) {
        diagnostics.push({
            severity: 'error',
            message:
                `Uniform blocks ('layout(std140) uniform { … }') are GLSL ES 3.00 only — ` +
                `KNI's BlazorGL backend rejects them. Use plain 'uniform vec4 …[N];' arrays instead, ` +
                `optionally with #define aliases (see ScreenEffect's 'ps_uniforms_vec4[N]' pattern).`,
        });
    }

    return diagnostics;
}

// ── GLSL preamble normalization ─────────────────────────────────────────────

// KNI's BlazorGL backend runs on WebGL 1.0 / GLSL ES 1.00 — `#version 300 es`
// gets rejected as "unsupported shader version". GLSL ES 1.00 doesn't need
// an explicit `#version` directive (it's the default), and `#version 100`
// is also accepted; everything higher fails.
//
// This pass:
//   1. If the user wrote a `#version` directive, hoist it to the literal
//      first line (some GL implementations are strict about leading
//      whitespace/comments above #version).
//   2. If they didn't, leave the version unset so the GL driver defaults
//      to ES 1.00.
//   3. Ensure `precision mediump float;` precedes any pixel shader body —
//      mandatory in ES, and the driver complains if it's missing or follows
//      the first declaration.
function normalizeGlslPreamble(source: string, stage: 'vertex' | 'pixel'): string {
    let body = source;
    let versionLine: string | null = null;

    const versionMatch = body.match(/#version\s+[^\n]*/);
    if (versionMatch) {
        versionLine = versionMatch[0];
        body = body.replace(versionMatch[0], '').replace(/^\s*\n/, '');
    }

    const hasPrecision = /\bprecision\s+(low|medium|high)p\s+float\s*;/.test(body);
    const precisionLine = !hasPrecision && stage === 'pixel'
        ? 'precision mediump float;\n'
        : '';

    const versionPrefix = versionLine ? `${versionLine}\n` : '';
    return `${versionPrefix}${precisionLine}${body}`;
}

// ── Reflection ──────────────────────────────────────────────────────────────

interface ExtractedReflection {
    samplers: CompiledSampler[];
    attributes: CompiledAttribute[];
    cbuffers: CompiledConstantBuffer[];
    diagnostics: { severity: 'error' | 'warning' | 'info'; message: string; line?: number }[];
}

// Lightweight GLSL/HLSL reflection: scan the source for the four declaration
// shapes MGFX cares about. This is *not* a full GLSL parser — it walks the
// text line by line, classifies each declaration, and ignores anything inside
// braces (function bodies, struct interiors). Adequate for the post-processing
// and SpriteEffect-shaped shaders that motivate the v1.
//
// Recognised shapes:
//
//   - GLSL samplers:
//       `uniform sampler2D Foo;`
//       `uniform sampler3D Foo;`
//       `uniform samplerCube Foo;`
//
//   - HLSL-style samplers (recognised but passed through to GLSL renaming
//     when the WASM HLSL compiler isn't enabled — we record them anyway so
//     the MGFX side has matching parameter records):
//       `Texture2D Foo : register(t0);`
//       `SamplerState Bar : register(s0);`
//
//   - Uniform blocks (GLSL std140 layout):
//       `layout(std140) uniform ps_uniforms_vec4 {
//          float Time;
//          vec4  Tint;
//        };`
//     Field byte sizes use the std140 packing convention.
//
//   - Vertex attributes (GL ES 3.00 prefers `in` over `attribute`):
//       `in vec3 a_position;`
//       `in vec2 a_uv;`
//     Usage tagging falls back to a heuristic on the attribute name (any
//     name containing "pos" → Position, "uv"/"texcoord" → TexCoord, etc.).
function extractReflection(src: string): ExtractedReflection {
    const samplers: CompiledSampler[] = [];
    const attributes: CompiledAttribute[] = [];
    const cbuffers: CompiledConstantBuffer[] = [];
    const diagnostics: ExtractedReflection['diagnostics'] = [];

    // Wipe comments + string literals so they don't trip the regexes below.
    const cleaned = stripCommentsAndStrings(src);

    // Strip function bodies. We do a brace-depth walk: anything inside the
    // outermost { … } of a function declaration gets blanked out. Uniform
    // blocks are an exception — we handle those before stripping.
    const cbufferRanges = extractUniformBlocks(cleaned, cbuffers, diagnostics);
    const declSpace = blankBraceBodies(cleaned, cbufferRanges);

    // ── Samplers ────────────────────────────────────────────────────────
    let samplerBinding = 0;
    // GLSL form
    for (const m of declSpace.matchAll(/(?:^|\s)uniform\s+(sampler2D|sampler3D|samplerCube)\s+([A-Za-z_][A-Za-z0-9_]*)\s*;/g)) {
        const typeStr = m[1];
        samplers.push({
            name: m[2],
            binding: samplerBinding,
            samplerSlot: samplerBinding,
            samplerType: typeStr === 'sampler3D' ? 1 : typeStr === 'samplerCube' ? 2 : 0,
        });
        samplerBinding++;
    }
    // HLSL form — Texture2D/3D/Cube. SamplerState declarations are paired
    // with a Texture* on the GL side via combined-image-sampler conventions,
    // so we only record the texture (the sampler name in MGFX matches the
    // texture parameter name anyway when KNI does the combined binding).
    for (const m of declSpace.matchAll(/(?:^|\s)(Texture2D|Texture3D|TextureCube)(?:<[^>]+>)?\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?::\s*register\([^)]*\))?\s*;/g)) {
        const typeStr = m[1];
        samplers.push({
            name: m[2],
            binding: samplerBinding,
            samplerSlot: samplerBinding,
            samplerType: typeStr === 'Texture3D' ? 1 : typeStr === 'TextureCube' ? 2 : 0,
        });
        samplerBinding++;
    }

    // ── Vertex attributes ───────────────────────────────────────────────
    let attribLocation = 0;
    for (const m of declSpace.matchAll(/(?:^|\s)(?:layout\s*\(\s*location\s*=\s*(\d+)\s*\)\s*)?(?:in|attribute)\s+(?:lowp\s+|mediump\s+|highp\s+)?(vec[234]|float|int)\s+([A-Za-z_][A-Za-z0-9_]*)\s*;/g)) {
        const loc = m[1] ? parseInt(m[1], 10) : attribLocation;
        const name = m[3];
        const usage = guessAttributeUsage(name);
        attributes.push({
            name,
            location: loc,
            usage,
            index: 0,
        });
        if (!m[1]) attribLocation++;
        else attribLocation = Math.max(attribLocation, loc + 1);
    }

    return { samplers, attributes, cbuffers, diagnostics };
}

// std140 byte-aligned sizes for the primitive types we recognise. Anything
// fancier (mat2, arrays, nested structs) gets a best-effort fallback to
// the next std140 16-byte alignment boundary.
const STD140_SIZES: Record<string, number> = {
    float: 4, int: 4, uint: 4, bool: 4,
    vec2: 8, ivec2: 8, uvec2: 8, bvec2: 8,
    vec3: 12, ivec3: 12, uvec3: 12, bvec3: 12,
    vec4: 16, ivec4: 16, uvec4: 16, bvec4: 16,
    mat3: 48, mat4: 64,
};

// std140 requires each field to be aligned to its base alignment (vec2 → 8,
// vec3/vec4 → 16, struct/array → 16). For the primitives above, base
// alignment == size after rounding vec3 up to 16.
function std140Align(typeName: string): number {
    if (typeName === 'vec3' || typeName === 'ivec3' || typeName === 'uvec3' || typeName === 'bvec3') return 16;
    if (typeName === 'mat3' || typeName === 'mat4') return 16;
    return STD140_SIZES[typeName] ?? 16;
}

function alignTo(offset: number, alignment: number): number {
    return Math.ceil(offset / alignment) * alignment;
}

function extractUniformBlocks(
    src: string,
    out: CompiledConstantBuffer[],
    diagnostics: ExtractedReflection['diagnostics'],
): Array<[number, number]> {
    // Find each `layout(std140) uniform <Name> { … };` block. We use brace
    // depth tracking so nested struct declarations inside the block don't
    // confuse the regex.
    const ranges: Array<[number, number]> = [];
    const re = /(?:layout\s*\([^)]*\)\s*)?uniform\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(src)) !== null) {
        const name = m[1];
        const blockStart = m.index;
        const braceStart = m.index + m[0].length - 1;
        const braceEnd = findMatchingBrace(src, braceStart);
        if (braceEnd < 0) {
            diagnostics.push({ severity: 'warning', message: `Unterminated uniform block '${name}'` });
            continue;
        }
        const body = src.slice(braceStart + 1, braceEnd);
        const fields: CompiledConstantBuffer['fields'] = [];
        let offset = 0;
        for (const fm of body.matchAll(/\b(float|int|uint|bool|vec[234]|ivec[234]|uvec[234]|bvec[234]|mat3|mat4)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:\[(\d+)\])?\s*;/g)) {
            const typeName = fm[1];
            const fieldName = fm[2];
            const arraySize = fm[3] ? parseInt(fm[3], 10) : 0;
            const baseSize = STD140_SIZES[typeName] ?? 16;
            const alignment = std140Align(typeName);
            const sizeBytes = arraySize > 0
                ? arraySize * alignTo(baseSize, 16)  // arrays: stride = max(baseSize, 16)
                : baseSize;
            offset = alignTo(offset, arraySize > 0 ? 16 : alignment);
            fields.push({ name: fieldName, offsetBytes: offset, sizeBytes });
            offset += sizeBytes;
        }
        out.push({
            name,
            sizeInBytes: alignTo(offset, 16),
            fields,
        });
        ranges.push([blockStart, braceEnd + 1]);
    }
    return ranges;
}

function blankBraceBodies(src: string, skip: Array<[number, number]>): string {
    // Strip the interior of every top-level brace pair except the ranges in
    // `skip` (which are uniform blocks we already parsed). Done so the
    // sampler/attribute regexes don't pick up locals declared inside
    // function bodies.
    const out = src.split('');
    let i = 0;
    while (i < src.length) {
        if (skip.some(([s, e]) => i >= s && i < e)) { i++; continue; }
        if (src[i] === '{') {
            const end = findMatchingBrace(src, i);
            if (end < 0) break;
            for (let j = i + 1; j < end; j++) {
                if (out[j] !== '\n') out[j] = ' ';
            }
            i = end + 1;
            continue;
        }
        i++;
    }
    return out.join('');
}

function findMatchingBrace(src: string, openIdx: number): number {
    if (src[openIdx] !== '{') return -1;
    let depth = 1;
    for (let i = openIdx + 1; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}') {
            depth--;
            if (depth === 0) return i;
        }
    }
    return -1;
}

function stripCommentsAndStrings(src: string): string {
    const out = src.split('');
    let i = 0;
    while (i < src.length) {
        if (src[i] === '/' && src[i + 1] === '/') {
            while (i < src.length && src[i] !== '\n') { out[i] = ' '; i++; }
            continue;
        }
        if (src[i] === '/' && src[i + 1] === '*') {
            out[i] = ' '; out[i + 1] = ' '; i += 2;
            while (i < src.length && !(src[i] === '*' && src[i + 1] === '/')) {
                if (src[i] !== '\n') out[i] = ' ';
                i++;
            }
            if (i < src.length) { out[i] = ' '; out[i + 1] = ' '; i += 2; }
            continue;
        }
        if (src[i] === '"') {
            out[i] = ' '; i++;
            while (i < src.length && src[i] !== '"') {
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

// MonoGame VertexElementUsage enum values. We surface the most common four;
// the rest fall back to 0 (Position) with index = an incrementing counter
// so the runtime at least gets unique binding points.
const USAGE_POSITION  = 0;
const USAGE_COLOR     = 1;
const USAGE_TEXCOORD  = 2;
const USAGE_NORMAL    = 3;

function guessAttributeUsage(name: string): number {
    const n = name.toLowerCase();
    if (n.includes('pos')) return USAGE_POSITION;
    if (n.includes('color') || n.includes('colour')) return USAGE_COLOR;
    if (n.includes('uv') || n.includes('texcoord') || n.includes('tex_coord')) return USAGE_TEXCOORD;
    if (n.includes('normal') || n.includes('nrm')) return USAGE_NORMAL;
    return USAGE_POSITION;
}

// ── Entrypoint wrapping ─────────────────────────────────────────────────────

// KNI's BlazorGL effect loader expects each shader bytecode slot to contain
// a *complete* GLSL ES 3.00 source, including its own `main()`. When the
// user wrote a function named e.g. `MainPS` and bound it via FX with
// `compile ps_4_0 MainPS()`, we need to synthesize a `main()` that calls
// it. For the GLSL-passthrough compiler this means appending a tiny
// trampoline.
//
// If the source already declares `void main()`, we don't add another one —
// the user is writing pure GLSL and we just pass it through.
function wrapAsEntrypoint(source: string, entrypoint: string, stage: 'vertex' | 'pixel'): string {
    const cleaned = stripCommentsAndStrings(source);
    if (/\bvoid\s+main\s*\(\s*\)/.test(cleaned)) return source;
    if (entrypoint === 'main') return source;

    // Best-effort trampoline for KNI's WebGL 1.0 / GLSL ES 1.00 target:
    //
    //    Pixel:  void main() { gl_FragColor = MainPS(); }
    //    Vertex: void main() { gl_Position  = MainVS(); }
    //
    // ES 1.00 has built-in `gl_FragColor` / `gl_Position` (no custom out
    // varyings), so the trampoline stays trivial. Users who need a richer
    // shape (multi-target output, custom in/out varyings) should write
    // `void main()` directly — or wait for the HLSL→GLSL WASM compiler
    // that synthesises this from the HLSL signature.
    if (stage === 'pixel') {
        return source + `\n\nvoid main() { gl_FragColor = ${entrypoint}(); }\n`;
    }
    return source + `\n\nvoid main() { gl_Position = ${entrypoint}(); }\n`;
}
