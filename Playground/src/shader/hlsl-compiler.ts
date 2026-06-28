// HLSL ShaderCompiler — wraps hlsl-translator.ts as a ShaderCompiler
// implementation so the compile-fx pipeline can swap it in for the
// GLSL-passthrough default. This is the compiler users get when they
// write `.fx` files using HLSL syntax (`float4`, `Texture2D`, `Sample`,
// semantics) rather than GLSL syntax.
//
// The translator does the work; this wrapper:
//   1. Threads the FX-parsed cbuffer declarations through so the
//      translator can synthesize matching `uniform vec4 NAME[N];` arrays
//      and `#define` aliases.
//   2. Re-uses the GLSL-passthrough compiler's text-based reflection
//      to extract attributes/cbuffer-layout info from the translated
//      GLSL, since the MGFX side only cares about the final GLSL shape.

import { translateHlslToGlsl } from './hlsl-translator';
import type {
    CompiledShaderEntry,
    CompileHlslOptions,
    ShaderCompiler,
} from './shader-compiler';
import type { FxCbufferDecl, FxSamplerStateLiteral } from './fx-parser';

// Per-stage FX metadata stash. compile-fx populates this via the setters
// below before invoking compileHlsl, since the shader-compiler interface
// itself doesn't carry FX-level data (so non-HLSL compilers can swap in
// without seeing it).
let _nextCompileCbuffers: FxCbufferDecl[] = [];
let _nextCompileSamplerLiterals: FxSamplerStateLiteral[] = [];

export function setFxCbuffersForNextCompile(cbuffers: FxCbufferDecl[]): void {
    _nextCompileCbuffers = cbuffers;
}

export function setFxSamplerStateLiteralsForNextCompile(literals: FxSamplerStateLiteral[]): void {
    _nextCompileSamplerLiterals = literals;
}

export function createHlslCompiler(): ShaderCompiler {
    return {
        async compileHlsl(opts: CompileHlslOptions): Promise<CompiledShaderEntry> {
            const cbuffers = _nextCompileCbuffers;
            const samplerLiterals = _nextCompileSamplerLiterals;
            const translated = translateHlslToGlsl({
                source: opts.source,
                entrypoint: opts.entrypoint,
                stage: opts.stage,
                cbuffers,
                samplerStateLiterals: samplerLiterals,
            });

            // Per-compile dump so we can see what KNI actually got handed.
            // Triggered on every effect rebuild (edit + save). Look for the
            // marker in DevTools to verify the trampoline color hardcode is
            // in place.
            console.log(
                `[hlsl-compiler] ${opts.stage} entry=${opts.entrypoint}\n` +
                `--- translated GLSL begin ---\n${translated.glsl}\n--- translated GLSL end ---`,
            );

            // Sampler bindings — the translator extracted these from the
            // HLSL `Texture2D X;` declarations. Assign sequential binding
            // slots; KNI binds these to GL texture units in order.
            const samplers = translated.samplers.map((s, i) => ({
                name: s.name,
                binding: i,
                samplerSlot: i,
                samplerType: s.samplerType,
            }));

            // Cbuffer reflection — pulled from the FX-side declarations
            // (the same ones we used to synthesize the GLSL uniform array
            // above). Sizes and field offsets match the HLSL packing rules
            // the FX parser implemented.
            const cbufferReflection = cbuffers.map((cb) => ({
                name: cb.name,
                sizeInBytes: cb.sizeInBytes,
                fields: cb.fields.map((f) => ({
                    name: f.name,
                    offsetBytes: f.offsetBytes,
                    sizeBytes: f.sizeBytes,
                })),
            }));

            return {
                glslSource: translated.glsl,
                samplers,
                // VS attributes reflected from struct-input semantics (PS
                // returns an empty list). Names match the GLSL `attribute`
                // declarations the translator emitted; usages are the XNA
                // VertexElementUsage codes the MGFX writer needs to bind
                // GL vertex array slots.
                attributes: translated.attributes,
                cbuffers: cbufferReflection,
                diagnostics: translated.diagnostics,
            };
        },
    };
}
