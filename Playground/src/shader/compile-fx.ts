// Top-level FX → XNB compile pipeline.
//
//   .fx source ── parseFx ──► { techniques, stripped HLSL }
//                                    │
//                                    ▼
//                          per pass, per stage:
//                                    │
//                       ShaderCompiler.compileHlsl(stripped HLSL, entry, stage)
//                                    │
//                                    ▼
//                          { GLSL, samplers, attributes, cbuffers }
//                                    │
//                                    ▼
//                    assembled into MgfxEffect ──► mgfx.emitEffect
//                                    │
//                                    ▼
//                          XNB envelope (header + reader chain)
//
// The compiler module (shader-compiler.ts) is intentionally pluggable —
// production uses glslang+spirv-cross WASM; tests inject a stub. Everything
// else in this file is concrete and unit-testable.

import {
    parseFx,
    type FxCbufferDecl,
    type FxParsed,
    type FxPass,
    type FxTechnique,
} from './fx-parser';
import {
    getShaderCompiler,
    type CompiledShaderEntry,
    type ShaderStage,
} from './shader-compiler';
import {
    setFxCbuffersForNextCompile,
    setFxSamplerStateLiteralsForNextCompile,
} from './hlsl-compiler';
import {
    emitEffect,
    EPT,
    type MgfxAttribute,
    type MgfxConstantBuffer,
    type MgfxEffect,
    type MgfxParam,
    type MgfxPass,
    type MgfxSampler,
    type MgfxShader,
    type MgfxTechnique,
} from '../xnb/mgfx';

export interface CompileFxOptions {
    source: string;
    // Asset name (without extension) — used in diagnostics. The asset's
    // identity in OPFS / the runtime is independent of this string.
    assetName?: string;
}

export interface CompileFxResult {
    xnb: Uint8Array;
    fx: FxParsed;
    diagnostics: CompileFxDiagnostic[];
}

export interface CompileFxDiagnostic {
    severity: 'error' | 'warning' | 'info';
    message: string;
    line?: number;
    column?: number;
}

const EFFECT_READER_NAME = 'Microsoft.Xna.Framework.Content.EffectReader';

// ── Public entrypoint ────────────────────────────────────────────────────────

export async function compileFxToXnb(opts: CompileFxOptions): Promise<CompileFxResult> {
    const diagnostics: CompileFxDiagnostic[] = [];
    const fx = parseFx(opts.source);

    for (const w of fx.warnings) {
        diagnostics.push({ severity: 'warning', message: w.message });
    }
    if (fx.techniques.length === 0) {
        throw new CompileFxError('No techniques found in shader source.');
    }

    // Walk all passes and collect (entrypoint, stage) compile requests. Many
    // .fx files reuse the same entry across passes (a common pattern for
    // multi-pass post-processing); dedup so we only compile each unique
    // (entry, stage) once.
    const requests = collectShaderRequests(fx.techniques);

    const compiler = await getShaderCompiler();
    const compiled = new Map<string, CompiledShaderEntry>();
    for (const req of requests) {
        const key = `${req.stage}:${req.entrypoint}`;
        if (compiled.has(key)) continue;
        // HLSL compiler needs the FX-parsed cbuffer declarations + DX9-style
        // sampler_state literal info so it can synthesize matching
        // `uniform vec4 NAME[N];` arrays + #defines + sampler uniforms.
        // Threaded via module-scoped stashes because the public
        // ShaderCompiler interface intentionally doesn't carry FX metadata
        // (so non-HLSL compilers can swap in without seeing it).
        setFxCbuffersForNextCompile(fx.cbuffers);
        setFxSamplerStateLiteralsForNextCompile(fx.samplerStateLiterals);
        const result = await compiler.compileHlsl({
            source: fx.hlslStripped,
            entrypoint: req.entrypoint,
            stage: req.stage,
            target: 'glsl-es-3.00',
            filename: opts.assetName ?? '<shader>',
        });
        for (const d of result.diagnostics) {
            diagnostics.push({
                severity: d.severity,
                message: `[${req.stage} ${req.entrypoint}] ${d.message}`,
                line: d.line,
                column: d.column,
            });
        }
        if (result.diagnostics.some(d => d.severity === 'error')) {
            throw new CompileFxError(
                `Shader '${req.entrypoint}' (${req.stage}) failed to compile — see diagnostics.`,
                diagnostics,
            );
        }
        compiled.set(key, result);
    }

    // Assemble MGFX records from the compiled output.
    const assembled = assembleMgfxFromCompiled(fx, compiled);

    const objectData = emitEffect(assembled);
    const xnb = buildEffectXnbEnvelope(objectData);

    return { xnb, fx, diagnostics };
}

// ── Shader request collection ───────────────────────────────────────────────

interface ShaderRequest {
    entrypoint: string;
    stage: ShaderStage;
}

function collectShaderRequests(techniques: FxTechnique[]): ShaderRequest[] {
    const seen = new Set<string>();
    const out: ShaderRequest[] = [];
    for (const t of techniques) {
        for (const p of t.passes) {
            for (const a of p.assigns) {
                if (a.kind !== 'shader') continue;
                const stage = mapShaderAssignToStage(a.name);
                if (!stage) continue;
                if (!a.entrypoint) continue;
                const key = `${stage}:${a.entrypoint}`;
                if (seen.has(key)) continue;
                seen.add(key);
                out.push({ stage, entrypoint: a.entrypoint });
            }
        }
    }
    return out;
}

function mapShaderAssignToStage(name: string): ShaderStage | null {
    if (name === 'VertexShader' || name === 'vertexshader') return 'vertex';
    if (name === 'PixelShader'  || name === 'pixelshader')  return 'pixel';
    // Hull/Domain/Geometry/Compute aren't supported by KNI BlazorGL.
    return null;
}

// ── MGFX assembly ───────────────────────────────────────────────────────────

interface AssembledShaderIndex {
    // Map (stage:entrypoint) → index into the MgfxEffect.shaders array.
    indexByKey: Map<string, number>;
}

function assembleMgfxFromCompiled(
    fx: FxParsed,
    compiled: Map<string, CompiledShaderEntry>,
): MgfxEffect {
    // 1. Collect cbuffers — same-name cbuffer used by both VS and PS gets one
    //    record. Field offsets/sizes have to match across stages (they will
    //    if both stages came from the same source).
    //
    //    Precedence:
    //      (a) HLSL-style `cbuffer NAME { … };` blocks parsed by the FX
    //          parser — these carry user-supplied parameter names + offsets
    //          that `set effect param *` looks up at runtime. Authoritative
    //          when present.
    //      (b) Compiler-discovered uniform blocks (`layout(std140) uniform …`)
    //          — only matter for GLSL ES 3.00; treated as a fallback when no
    //          FX-side cbuffer declared the same name.
    const cbuffersByName = new Map<string, MgfxConstantBuffer>();
    // Parameter indices are referenced from cbuffer.params + sampler.parameterIndex,
    // so we assemble parameters first, then point at them by position.
    const paramList: MgfxParam[] = [];
    const paramIndexByName = new Map<string, number>();

    // 2. Build a flat sampler-name registry — same texture used by multiple
    //    samplers across passes should reuse the same parameter index.
    function paramIndexFor(p: MgfxParam): number {
        const existing = paramIndexByName.get(p.name);
        if (existing !== undefined) return existing;
        const idx = paramList.length;
        paramList.push(p);
        paramIndexByName.set(p.name, idx);
        return idx;
    }

    // Pass (a): FX-declared cbuffers win.
    for (const fxCb of fx.cbuffers) {
        cbuffersByName.set(fxCb.name, buildMgfxCbufferFromFx(fxCb, paramIndexFor));
    }

    for (const entry of compiled.values()) {
        for (const cb of entry.cbuffers) {
            if (cbuffersByName.has(cb.name)) continue;
            // For each field, ensure a top-level parameter exists for it.
            // MonoGame's cbuffer.params point at indices in the effect-level
            // parameter list; the parameter records carry the runtime
            // metadata used by Effect.Parameters["X"].SetValue(...).
            const params: MgfxConstantBuffer['params'] = [];
            for (const field of cb.fields) {
                const paramIdx = paramIndexFor(makeScalarParam(field.name, field.sizeBytes));
                params.push({ paramIdx, offset: field.offsetBytes });
            }
            cbuffersByName.set(cb.name, {
                name: cb.name,
                sizeInBytes: cb.sizeInBytes,
                params,
            });
        }
        for (const samp of entry.samplers) {
            // Ensure a Texture2D/3D/Cube parameter exists for this sampler.
            paramIndexFor(makeTextureParam(samp.name, samp.samplerType));
        }
    }

    const constantBuffers = [...cbuffersByName.values()];

    // 3. Build the shader records and remember their indices.
    const shaders: MgfxShader[] = [];
    const idx: AssembledShaderIndex = { indexByKey: new Map() };

    for (const [key, entry] of compiled.entries()) {
        const stage: ShaderStage = key.startsWith('vertex:') ? 'vertex' : 'pixel';

        const bytecode = new TextEncoder().encode(entry.glslSource);

        const samplers: MgfxSampler[] = entry.samplers.map(s => ({
            type: s.samplerType,
            textureSlot: s.binding,
            samplerSlot: s.samplerSlot,
            stateBytes: null,                            // states come later
            name: s.name,
            parameterIndex: paramIndexByName.get(s.name) ?? 0,
        }));

        // cbufferRefs tells KNI which cbuffers this shader's GL program
        // needs uploaded on Pass.Apply(). We err on the side of including
        // every cbuffer in the effect — the GLSL passthrough doesn't track
        // per-stage cbuffer usage, and uploading a cbuffer the shader
        // doesn't reference is a no-op (GL just ignores the inactive
        // uniform). Missing a referenced cbuffer, on the other hand,
        // means its uniform stays at zero — visible to the user as a
        // black/zero-tint render.
        const cbufferRefs: number[] = constantBuffers.map((_, idx) => idx);

        const attributes: MgfxAttribute[] = entry.attributes.map(a => ({
            name: a.name,
            usage: a.usage,
            index: a.index,
            location: a.location,
        }));

        idx.indexByKey.set(key, shaders.length);
        shaders.push({
            isVertexShader: stage === 'vertex',
            bytecode,
            samplers,
            cbufferRefs,
            attributes,
        });
    }

    // 3b. PS-only passes need a default VS — MonoGame's offline compiler
    //     folds one in at compile time; we do the same here. KNI does NOT
    //     have a runtime fallback: a pass with vsShaderIndex=-1 produces a
    //     GL program with no VS, and either fails to link or pairs with
    //     whatever VS was previously bound (which for FadeSpriteBatch is
    //     FadeSpriteBatchEffect's VS — but that VS assumes its OWN cbuffer
    //     for MatrixTransform, which isn't set on the user's effect).
    //     We inject a default VS that mirrors FadeSpriteBatchEffect's
    //     compiled VS exactly so behavior matches: same attribute layout
    //     (vs_v0..v3 → POSITION/COLOR/TEXCOORD0/TEXCOORD1), same
    //     `vs_uniforms_vec4[4]` cbuffer for MatrixTransform, same
    //     posFixup tail. FadeSpriteBatch sets MatrixTransform on user
    //     effects in its Setup() so the matrix is populated before
    //     pass.Apply() uploads it.
    const needsDefaultVS = fx.techniques.some(t =>
        t.passes.some(p => !p.assigns.some(a =>
            a.kind === 'shader' && a.entrypoint && mapShaderAssignToStage(a.name) === 'vertex',
        )),
    );
    let defaultVsIndex = -1;
    if (needsDefaultVS) {
        defaultVsIndex = injectDefaultVertexShader(
            shaders,
            constantBuffers,
            paramIndexFor,
        );
    }

    // 4. Translate FX techniques/passes → MGFX techniques/passes with the
    //    shader-index pointers we just recorded.
    const techniques: MgfxTechnique[] = fx.techniques.map(t => ({
        name: t.name,
        annotations: [],
        passes: t.passes.map(p => buildPass(p, idx, defaultVsIndex)),
    }));

    return {
        version: 10,
        profileId: 0,                                 // OpenGL profile (KNI)
        // MGFX `effectKey` is a content-derived 32-bit hash. The format
        // spec describes it as "ignored on read", but KNI's BlazorGL uses
        // it as a *cache key* for compiled GL shader programs across Effect
        // instances: two Effects sharing the same effectKey reuse the same
        // GL program. If we emit a constant (e.g. 0) for every shader, KNI
        // serves the cached program from whichever .fx was loaded *first*
        // — every subsequent edit invisibly returns the original program.
        // That's exactly the "screen doesn't update on shader edit" symptom
        // we hit. Hash the GLSL bytecodes so each distinct shader source
        // gets a distinct cache slot.
        effectKey: computeEffectKey(shaders),
        constantBuffers,
        shaders,
        parameters: paramList,
        techniques,
        trailingBody: new Uint8Array(0),
    };
}

// FNV-1a 32-bit over every shader's bytecode. Deterministic, no Web Crypto
// requirement, and a sufficient cache discriminator — two shaders that
// disagree on a single byte produce different keys. Returned as a signed
// int32 because the MGFX writer expects that representation (the field is
// emitted via writeInt32LE).
function computeEffectKey(shaders: MgfxShader[]): number {
    let hash = 0x811C9DC5;
    for (const s of shaders) {
        // Mix the stage bit so a VS and a PS with identical bytecode don't
        // collide (rare in practice but cheap to defend against).
        hash ^= s.isVertexShader ? 0x01 : 0x02;
        hash = Math.imul(hash, 0x01000193);
        for (let i = 0; i < s.bytecode.length; i++) {
            hash ^= s.bytecode[i];
            hash = Math.imul(hash, 0x01000193);
        }
    }
    // Force into signed int32 territory — the writer uses writeInt32LE.
    return hash | 0;
}

function buildPass(p: FxPass, idx: AssembledShaderIndex, defaultVsIndex: number): MgfxPass {
    let vsIndex = -1;
    let psIndex = -1;
    for (const a of p.assigns) {
        if (a.kind !== 'shader' || !a.entrypoint) continue;
        const stage = mapShaderAssignToStage(a.name);
        if (!stage) continue;
        const shaderIdx = idx.indexByKey.get(`${stage}:${a.entrypoint}`);
        if (shaderIdx === undefined) continue;
        if (stage === 'vertex') vsIndex = shaderIdx;
        else psIndex = shaderIdx;
    }
    // Fall back to the injected default VS when the user only provided a PS
    // (canonical MonoGame SpriteEffect pattern).
    if (vsIndex === -1 && defaultVsIndex >= 0) {
        vsIndex = defaultVsIndex;
    }
    return {
        name: p.name,
        annotations: [],
        vsShaderIndex: vsIndex,
        psShaderIndex: psIndex,
        blendBytes: null,
        depthBytes: null,
        rasterBytes: null,
    };
}

// Synthesized default VS — byte-for-byte equivalent to the VS that
// MonoGame's offline compiler folds into PS-only SpriteEffect.fx files.
// Sourced by extracting the compiled GLSL from FadeSpriteBatchEffect.xnb
// (an effect that compiled cleanly through `MonoGame.Effect.Compiler` and
// runs against KNI on BlazorGL today).
//
// Attribute layout matches FadeSpriteVertex: position (vec3 in the buffer
// → vec4 in the shader with w=1), color, texcoord0, texcoord1.
const DEFAULT_VERTEX_SHADER_GLSL = `#ifdef GL_ES
precision highp float;
precision mediump int;
#endif
uniform vec4 vs_uniforms_vec4[4];
uniform vec4 posFixup;
#define vs_c0 vs_uniforms_vec4[0]
#define vs_c1 vs_uniforms_vec4[1]
#define vs_c2 vs_uniforms_vec4[2]
#define vs_c3 vs_uniforms_vec4[3]
attribute vec4 vs_v0;
#define vs_o0 gl_Position
attribute vec4 vs_v1;
varying vec4 vFrontColor;
#define vs_o1 vFrontColor
attribute vec4 vs_v2;
varying vec4 vTexCoord0;
#define vs_o2 vTexCoord0
attribute vec4 vs_v3;
varying vec4 vTexCoord1;
#define vs_o3 vTexCoord1
void main()
{
    vs_o0.x = dot(vs_v0, vs_c0);
    vs_o0.y = dot(vs_v0, vs_c1);
    vs_o0.z = dot(vs_v0, vs_c2);
    vs_o0.w = dot(vs_v0, vs_c3);
    vs_o1 = vs_v1;
    vs_o2.xy = vs_v2.xy;
    vs_o3 = vs_v3;
    gl_Position.y = gl_Position.y * posFixup.y;
    gl_Position.xy += posFixup.zw * gl_Position.ww;
    gl_Position.z = gl_Position.z * 2.0 - gl_Position.w;
}
`;

// XNA VertexElementUsage enum values. Position is the only one with a
// canonical zero — Color and TextureCoordinate values come from the public
// MonoGame XNA source and match what KNI's MGFX reader binds against.
// XNA/MonoGame VertexElementUsage enum — verified against the attribute
// records inside FadeSpriteBatchEffect.xnb (offline-compiled, known-good).
// Position=0, Color=1, TextureCoordinate=2, Normal=3. Earlier guesses
// used 3 / 5 which don't match any VertexElement in FadeSpriteVertex,
// so the bound color + texcoord attributes silently read zero.
const VEU_POSITION = 0;
const VEU_COLOR = 1;
const VEU_TEXCOORD = 2;

// Inject the default VS, its matching cbuffer + MatrixTransform parameter,
// and return its index in `shaders`. Also adds the new cbuffer to every
// existing shader's `cbufferRefs` (the same "include every cbuffer" rule
// the per-shader loop above uses — KNI silently ignores cbuffers a shader
// doesn't reference).
function injectDefaultVertexShader(
    shaders: MgfxShader[],
    constantBuffers: MgfxConstantBuffer[],
    paramIndexFor: (p: MgfxParam) => number,
): number {
    // Reuse an existing `vs_uniforms_vec4` cbuffer if the user happened to
    // declare one — otherwise create it. (No real user .fx will name a
    // cbuffer that, but defending against the collision is cheap.)
    let cbIdx = constantBuffers.findIndex(cb => cb.name === 'vs_uniforms_vec4');
    if (cbIdx === -1) {
        const mtParamIdx = paramIndexFor(makeMatrixTransformParam());
        constantBuffers.push({
            name: 'vs_uniforms_vec4',
            sizeInBytes: 64,
            params: [{ paramIdx: mtParamIdx, offset: 0 }],
        });
        cbIdx = constantBuffers.length - 1;
        // Mirror the existing "every shader references every cbuffer" rule.
        for (const s of shaders) {
            if (!s.cbufferRefs.includes(cbIdx)) {
                s.cbufferRefs = [...s.cbufferRefs, cbIdx];
            }
        }
    }

    const shaderIdx = shaders.length;
    shaders.push({
        isVertexShader: true,
        bytecode: new TextEncoder().encode(DEFAULT_VERTEX_SHADER_GLSL),
        samplers: [],
        cbufferRefs: constantBuffers.map((_, i) => i),
        attributes: [
            { name: 'vs_v0', usage: VEU_POSITION, index: 0, location: 0 },
            { name: 'vs_v1', usage: VEU_COLOR,    index: 0, location: 1 },
            { name: 'vs_v2', usage: VEU_TEXCOORD, index: 0, location: 2 },
            { name: 'vs_v3', usage: VEU_TEXCOORD, index: 1, location: 3 },
        ],
    });
    return shaderIdx;
}

// MatrixTransform parameter: a 4×4 Matrix, initialized to identity so the
// first frame (before FadeSpriteBatch.Setup pushes the projection in)
// degrades to "draw in NDC space" instead of "draw at origin" (zero
// matrix → all positions collapse to zero → fully off-screen black).
function makeMatrixTransformParam(): MgfxParam {
    const data = new Uint8Array(64);
    const dv = new DataView(data.buffer);
    dv.setFloat32(0,  1, true);    // m00
    dv.setFloat32(20, 1, true);    // m11
    dv.setFloat32(40, 1, true);    // m22
    dv.setFloat32(60, 1, true);    // m33
    return {
        class_: PARAM_CLASS_MATRIX,
        type: EPT.Single,
        name: 'MatrixTransform',
        semantic: '',
        annotations: [],
        rows: 4,
        columns: 4,
        elements: [],
        members: [],
        data,
    };
}

// Build a scalar/vector parameter record. We don't currently distinguish
// float/int/bool at this level — KNI's OpenGL reader treats all three as
// float32 storage anyway (see mgfx.ts EPT comment), and parameters whose
// values are pushed via Effect.Parameters[].SetValue() come from the
// caller's type, not the .fx declaration.
// MonoGame's EffectParameterClass enum — confirmed against the working
// ScreenEffect.xnb via probe-effect-params.mjs. Anything else triggers
// `InvalidCastException` from EffectParameter.SetValue() at runtime
// because SetValue's first check is `ParameterClass != Vector` (or
// whatever the call signature implies).
const PARAM_CLASS_SCALAR = 0;
const PARAM_CLASS_VECTOR = 1;
const PARAM_CLASS_MATRIX = 2;
const PARAM_CLASS_OBJECT = 3;

// Pick the EffectParameterClass for a parameter from its dimensions.
// Empty (0×0) means it's a texture/sampler reference and belongs to
// Object; 1×1 is Scalar; 1×N or N×1 (N>1) is Vector; everything else
// is Matrix.
function paramClassFor(rows: number, columns: number): number {
    if (rows === 0 && columns === 0) return PARAM_CLASS_OBJECT;
    if (rows === 1 && columns === 1) return PARAM_CLASS_SCALAR;
    if (rows === 1 || columns === 1) return PARAM_CLASS_VECTOR;
    return PARAM_CLASS_MATRIX;
}

// Build a `MgfxConstantBuffer` from an FX-side `cbuffer` declaration. Each
// field becomes a top-level parameter (so `Effect.Parameters["Tint"]`
// resolves), with rows/columns drawn from the HLSL type and offsetBytes
// from the FX parser's layout pass.
function buildMgfxCbufferFromFx(
    fxCb: FxCbufferDecl,
    paramIndexFor: (p: MgfxParam) => number,
): MgfxConstantBuffer {
    const params: MgfxConstantBuffer['params'] = [];
    for (const field of fxCb.fields) {
        const paramIdx = paramIndexFor(makeHlslParam(field.name, field.rows, field.columns, field.sizeBytes));
        params.push({ paramIdx, offset: field.offsetBytes });
    }
    return {
        name: fxCb.name,
        sizeInBytes: fxCb.sizeInBytes,
        params,
    };
}

// A scalar/vector/matrix parameter sized from the HLSL `cbuffer` field's
// type. Rows*columns determines the size of the inline data slot the MGFX
// writer emits (and the GL uniform write count when `set effect param *`
// fires).
function makeHlslParam(name: string, rows: number, columns: number, _sizeBytes: number): MgfxParam {
    return {
        class_: paramClassFor(rows, columns),
        type: EPT.Single,
        name,
        semantic: '',
        annotations: [],
        rows,
        columns,
        elements: [],
        members: [],
        // MGFX stores rows*columns*4 bytes of initial data for scalar/vector
        // params. Zero-fill — the user's first `set effect param *` overwrites
        // these bytes.
        data: new Uint8Array(Math.max(4, rows * columns * 4)),
    };
}

function makeScalarParam(name: string, sizeBytes: number): MgfxParam {
    // Approximate (rows, columns) from the field size assuming 4-byte
    // float lanes. SPIRV-Cross reports byte sizes for std140-laid-out
    // fields; the rows/columns are convenience metadata for the runtime
    // and don't have to match the layout exactly.
    const lanes = Math.max(1, Math.floor(sizeBytes / 4));
    const rows = 1;
    const columns = lanes;
    // Single/Int32/Bool params with no elements/members carry rows*cols*4
    // bytes of inline data in the MGFX wire format (read back at runtime
    // as the initial value before any SetValue call). Allocate zeros — the
    // user's calls overwrite this on first frame anyway.
    const data = new Uint8Array(rows * columns * 4);
    return {
        class_: paramClassFor(rows, columns),
        type: EPT.Single,
        name,
        semantic: '',
        annotations: [],
        rows,
        columns,
        elements: [],
        members: [],
        data,
    };
}

function makeTextureParam(name: string, samplerType: 0 | 1 | 2): MgfxParam {
    const type =
        samplerType === 1 ? EPT.Texture3D
        : samplerType === 2 ? EPT.TextureCube
        : EPT.Texture2D;
    return {
        class_: PARAM_CLASS_OBJECT,
        type,
        name,
        semantic: '',
        annotations: [],
        rows: 0,
        columns: 0,
        elements: [],
        members: [],
        data: null,
    };
}

// ── XNB envelope wrap ───────────────────────────────────────────────────────

// Wrap the EffectReader's objectData bytes in the full XNB header + reader
// chain so the result is a valid `.xnb` that BrowserContentManager can load
// directly (after patchEffectMgfxVersionForKni — but that's a no-op when we
// already emit v10).
function buildEffectXnbEnvelope(objectData: Uint8Array): Uint8Array {
    const meta: number[] = [];
    const readerName = new TextEncoder().encode(EFFECT_READER_NAME);
    write7BitInt(meta, 1);                              // reader count
    write7BitInt(meta, readerName.length);
    for (const b of readerName) meta.push(b);
    pushInt32LE(meta, 0);                                // reader version
    write7BitInt(meta, 0);                                // shared resource count
    write7BitInt(meta, 1);                                // root object type id

    const xnbHeaderSize = 10;
    const fileSize = xnbHeaderSize + meta.length + objectData.length;
    const out = new Uint8Array(fileSize);
    out[0] = 0x58; out[1] = 0x4E; out[2] = 0x42;        // 'XNB'
    out[3] = 0x64;                                        // 'd' DesktopGL — same platform byte
    out[4] = 5;                                           // XNB format version
    out[5] = 0;                                           // flags (uncompressed, Reach profile)
    // fileSize little-endian at [6..9]
    out[6] = fileSize & 0xFF;
    out[7] = (fileSize >>> 8) & 0xFF;
    out[8] = (fileSize >>> 16) & 0xFF;
    out[9] = (fileSize >>> 24) & 0xFF;

    let offset = xnbHeaderSize;
    for (let i = 0; i < meta.length; i++) out[offset++] = meta[i];
    out.set(objectData, offset);
    return out;
}

function write7BitInt(out: number[], value: number): void {
    let v = value >>> 0;
    while (v >= 0x80) {
        out.push((v & 0x7F) | 0x80);
        v >>>= 7;
    }
    out.push(v);
}

function pushInt32LE(out: number[], value: number): void {
    out.push(
        value & 0xFF,
        (value >>> 8) & 0xFF,
        (value >>> 16) & 0xFF,
        (value >>> 24) & 0xFF,
    );
}

// ── Error type ─────────────────────────────────────────────────────────────

export class CompileFxError extends Error {
    diagnostics?: CompileFxDiagnostic[];
    constructor(message: string, diagnostics?: CompileFxDiagnostic[]) {
        super(message);
        this.name = 'CompileFxError';
        this.diagnostics = diagnostics;
    }
}
