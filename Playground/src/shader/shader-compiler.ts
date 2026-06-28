// Shader compiler interface.
//
// The compile pipeline for a single shader entry point is:
//
//   HLSL source ── glslang ──► SPIR-V ── spirv-cross ──► GLSL ES 3.00
//
// Both stages run as WASM modules in the browser. This module defines the
// contract and provides a swappable factory so the rest of the pipeline can
// be wired and tested without the WASM modules in tree.
//
// Real WASM integration:
//   - HLSL → SPIR-V via @khronosgroup/glslang (`glslangModule` from
//     glslang.js, called with shader="hlsl", stage="vertex"|"fragment").
//   - SPIR-V → GLSL via spirv-cross-wasm (or KhronosGroup/SPIRV-Cross
//     compiled with emcc). spirv_cross.Compiler.compile() with
//     {version:300, es:true} settings.
//   - Reflection (samplers, attributes, cbuffers) pulled from spirv-cross
//     ShaderResources (`getShaderResources()`, `getActiveBufferRanges()`).
//
// Why a factory + stub?
//   - Real WASM init is a 30+ MB combined download, all-or-nothing, async.
//     We don't want to pay that on Playground boot — only when a user
//     actually tries to compile an .fx file.
//   - The pipeline above (FX parser → compileFxToXnb → MGFX writer) can
//     be exercised end-to-end against the stub for shape validation,
//     while the real WASM ships in a follow-up.

export type ShaderStage = 'vertex' | 'pixel';

export interface CompileHlslOptions {
    source: string;
    entrypoint: string;
    stage: ShaderStage;
    // 'glsl-es-3.00' = WebGL2 / KNI BlazorGL target. Future profiles:
    // 'glsl-330' (desktop GL), 'dxbc' (D3D11 native).
    target: 'glsl-es-3.00';
    // Optional filename string used in diagnostics. Defaults to '<shader>'.
    filename?: string;
}

export interface CompiledSampler {
    name: string;          // e.g. 'SpriteTextureSampler' (separate samplers) or 'Tex' (combined)
    binding: number;       // GL texture unit slot
    samplerSlot: number;   // GL sampler slot (==binding when not split)
    // 0 = Texture2D, 1 = Texture3D, 2 = TextureCube — matches MonoGame's
    // SamplerType enum used in the MGFX wire format.
    samplerType: 0 | 1 | 2;
}

export interface CompiledAttribute {
    name: string;          // GLSL attribute name (post-spirv-cross renaming)
    location: number;      // GL attribute slot (-1 = unbound / unused)
    // MonoGame's VertexElementUsage enum — Position=0, Color=1, TexCoord=2,
    // Normal=3, Binormal=4, Tangent=5, BlendIndices=6, BlendWeight=7,
    // Depth=8, Fog=9, PointSize=10, Sample=11, TessellateFactor=12.
    usage: number;
    // The index within a multi-element usage (e.g. TEXCOORD0 vs TEXCOORD3).
    index: number;
}

export interface CompiledConstantBuffer {
    name: string;
    sizeInBytes: number;
    // Field offsets within the cbuffer, packed under the std140 layout
    // KNI BlazorGL expects. spirv-cross emits std140 for SPIR-V cbuffers
    // by default.
    fields: Array<{ name: string; offsetBytes: number; sizeBytes: number }>;
}

export interface CompiledShaderEntry {
    glslSource: string;
    samplers: CompiledSampler[];
    attributes: CompiledAttribute[];
    cbuffers: CompiledConstantBuffer[];
    diagnostics: ShaderDiagnostic[];
}

export interface ShaderDiagnostic {
    severity: 'error' | 'warning' | 'info';
    message: string;
    // Line/column point into the *original* HLSL source (the framing parser
    // whitespaces out FX-only ranges so positions remain stable). 1-based.
    line?: number;
    column?: number;
}

export interface ShaderCompiler {
    compileHlsl(opts: CompileHlslOptions): Promise<CompiledShaderEntry>;
    // Drop any cached WASM module state. Mainly for tests; production
    // generally wants to hold on.
    dispose?(): void;
}

// ── Factory plumbing ────────────────────────────────────────────────────────

// The active compiler instance — set once on first request, reused thereafter.
let _compilerPromise: Promise<ShaderCompiler> | null = null;

// Allow tests / future WASM integration to override the default factory.
let _factory: () => Promise<ShaderCompiler> = defaultFactory;

export function setShaderCompilerFactory(factory: () => Promise<ShaderCompiler>): void {
    _factory = factory;
    _compilerPromise = null;  // force re-init next access
}

export function getShaderCompiler(): Promise<ShaderCompiler> {
    if (!_compilerPromise) _compilerPromise = _factory();
    return _compilerPromise;
}

// ── Default factory: the HLSL → GLSL ES 1.00 translator ────────────────────

// The default compiler is the hand-rolled HLSL translator (see
// hlsl-translator.ts). It handles the SM4.0+ subset MonoGame's mgfxc
// emits for SpriteBatch-shaped effects: float4/Texture2D/Sample/semantics.
// Zero WASM bundle weight, ships today.
//
// To swap in a different compiler — a WASM-backed full HLSL frontend
// for shaders the translator can't handle (custom struct semantics,
// `mul()` matrix-vector etc.), or the GLSL passthrough for users who
// want to write raw GLSL inside FX framing — call:
//   setShaderCompilerFactory(() => createGlslPassthroughCompiler())
async function defaultFactory(): Promise<ShaderCompiler> {
    const { createHlslCompiler } = await import('./hlsl-compiler');
    return createHlslCompiler();
}

export class ShaderCompilerNotAvailableError extends Error {
    constructor(message: string) {
        super(message);
        this.name = 'ShaderCompilerNotAvailableError';
    }
}
