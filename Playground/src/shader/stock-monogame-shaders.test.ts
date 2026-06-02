// End-to-end tests for stock MonoGame shader fixtures. Each fixture
// represents a real `.fx` file a user would drop into the playground —
// stock SpriteEffect, Tint, Grayscale, Posterize, Vignette. The pipeline
// under test:
//
//   parseFx → translateHlslToGlsl → transformEs100ToEs310ForValidation
//                                 → emit MGFX XNB
//
// We don't run real glslang here (the WASM is browser-only); instead we
// assert structural properties of each stage's output. Specifically:
//
//   - parseFx finds the technique + pass + entry binding
//   - hlsl-translator emits valid ES 1.00 GLSL (uses varying, gl_FragColor,
//     texture2D, plain uniform decls — no #version directive)
//   - validation transform produces SPIR-V-strict ES 3.10 (#version 310 es
//     on line 1, layout(binding=N) on samplers, layout(location=N) on I/O,
//     no varying/gl_FragColor/texture2D leftover)
//   - compileFxToXnb produces a valid effect XNB with the right
//     EffectParameterClass values, content-hashed effectKey, and cbuffer
//     references
//
// If glslang surfaces a NEW SPIR-V strictness category we haven't handled,
// these tests stay green (because they check structure, not real
// validation) — but the in-browser path still breaks. The tests below
// are necessary, not sufficient, for "stock MonoGame shaders work".

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { parseFx } from './fx-parser';
import { translateHlslToGlsl } from './hlsl-translator';
import { transformEs100ToEs310ForValidation } from './glsl-validator';
import { compileFxToXnb } from './compile-fx';
import { setShaderCompilerFactory } from './shader-compiler';
import { createHlslCompiler } from './hlsl-compiler';
import { classifyXnb } from '../xnb/xnb-reader';
import { parseEffect } from '../xnb/mgfx';
import { ALL_STOCK_FIXTURES, SPRITE_EFFECT_FX, TINT_EFFECT_FX } from './__fixtures__/stock-monogame-shaders';

// Make sure the HLSL compiler is the active one (it's the default, but
// other test files may have swapped in a stub).
beforeEach(() => {
    setShaderCompilerFactory(async () => createHlslCompiler());
});
afterEach(() => {
    setShaderCompilerFactory(async () => createHlslCompiler());
});

describe('Stock MonoGame shaders — FX framing parse', () => {
    it.each(ALL_STOCK_FIXTURES)('parses $name and finds the technique + entry', ({ source, entry, technique }) => {
        const fx = parseFx(source);
        expect(fx.warnings.filter((w) => w.message.includes('error'))).toEqual([]);
        expect(fx.techniques.map((t) => t.name)).toContain(technique);
        // The entry function must be bound via a `compile … <entry>()` assign.
        const hasEntry = fx.techniques.some((t) =>
            t.passes.some((p) =>
                p.assigns.some((a) => a.kind === 'shader' && a.entrypoint === entry),
            ),
        );
        expect(hasEntry).toBe(true);
    });
});

describe('Stock MonoGame shaders — HLSL → GLSL ES 1.00 translation', () => {
    it.each(ALL_STOCK_FIXTURES)('$name translates without errors', ({ source, entry }) => {
        const fx = parseFx(source);
        const out = translateHlslToGlsl({
            source: fx.hlslStripped,
            entrypoint: entry,
            stage: 'pixel',
            cbuffers: fx.cbuffers,
            samplerStateLiterals: fx.samplerStateLiterals,
        });
        expect(out.diagnostics.filter((d) => d.severity === 'error')).toEqual([]);
    });

    it.each(ALL_STOCK_FIXTURES)('$name produces ES 1.00-shaped GLSL (no HLSL leftovers)', ({ source, entry }) => {
        const fx = parseFx(source);
        const out = translateHlslToGlsl({
            source: fx.hlslStripped,
            entrypoint: entry,
            stage: 'pixel',
            cbuffers: fx.cbuffers,
            samplerStateLiterals: fx.samplerStateLiterals,
        });
        // No HLSL types or intrinsics left in the GLSL.
        expect(out.glsl).not.toMatch(/\bfloat4\b/);
        expect(out.glsl).not.toMatch(/\bfloat3\b/);
        expect(out.glsl).not.toMatch(/\bfloat2\b/);
        expect(out.glsl).not.toMatch(/\bTexture2D\b/);
        expect(out.glsl).not.toMatch(/\bSamplerState\b/);
        expect(out.glsl).not.toMatch(/\.Sample\(/);
        expect(out.glsl).not.toMatch(/\btex2D\(/);
        expect(out.glsl).not.toMatch(/:\s*(SV_TARGET|SV_POSITION|COLOR|TEXCOORD)/);
        // No FX framing leftovers.
        expect(out.glsl).not.toMatch(/\btechnique\b/);
        expect(out.glsl).not.toMatch(/\bcbuffer\b/);
        expect(out.glsl).not.toMatch(/\bsampler_state\b/);
    });

    it.each(ALL_STOCK_FIXTURES)('$name has a void main() that calls the entry', ({ source, entry }) => {
        const fx = parseFx(source);
        const out = translateHlslToGlsl({
            source: fx.hlslStripped,
            entrypoint: entry,
            stage: 'pixel',
            cbuffers: fx.cbuffers,
            samplerStateLiterals: fx.samplerStateLiterals,
        });
        expect(out.glsl).toMatch(/void\s+main\s*\(\s*\)/);
        // The trampoline calls the entry function with some args.
        expect(out.glsl).toMatch(new RegExp(`${entry}\\s*\\(`));
        // Output is via gl_FragColor (ES 1.00 form).
        expect(out.glsl).toMatch(/gl_FragColor\s*=/);
    });
});

describe('Stock MonoGame shaders — ES 1.00 → ES 3.10 validation transform', () => {
    it.each(ALL_STOCK_FIXTURES)('$name transforms into SPIR-V-strict ES 3.10', ({ source, entry }) => {
        const fx = parseFx(source);
        const out = translateHlslToGlsl({
            source: fx.hlslStripped,
            entrypoint: entry,
            stage: 'pixel',
            cbuffers: fx.cbuffers,
            samplerStateLiterals: fx.samplerStateLiterals,
        });
        const { source: validated, addedLines } =
            transformEs100ToEs310ForValidation(out.glsl, 'pixel');

        // #version 310 es MUST be the first line — glslang errors if not.
        const lines = validated.split('\n');
        expect(lines[0]).toBe('#version 310 es');

        // Precision declarations included.
        expect(validated).toMatch(/precision highp float;/);
        expect(validated).toMatch(/precision highp int;/);

        // No ES 1.00 keywords leftover.
        expect(validated).not.toMatch(/\bvarying\b/);
        expect(validated).not.toMatch(/\bgl_FragColor\b/);
        expect(validated).not.toMatch(/\btexture2D\s*\(/);

        // Every uniform sampler declaration has layout(binding=).
        const samplerLines = validated.match(/uniform\s+(sampler2D|sampler3D|samplerCube)\s+\w+\s*;/g) ?? [];
        for (const line of samplerLines) {
            // Find the line in context to verify layout(binding=) prefix.
            const idx = validated.indexOf(line);
            const before = validated.slice(Math.max(0, idx - 60), idx);
            expect(before).toMatch(/layout\(binding=\d+\)\s*$/);
        }

        // Every uniform block has layout(std140, binding=).
        const uboLines = validated.match(/uniform\s+\w+\s*\{/g) ?? [];
        for (const line of uboLines) {
            const idx = validated.indexOf(line);
            const before = validated.slice(Math.max(0, idx - 80), idx);
            expect(before).toMatch(/layout\(std140,\s*binding=\d+\)\s*$/);
        }

        // Every in/out declaration has layout(location=).
        // We check the full transformed source has no bare `in vec*` or
        // `out vec*` without a layout(location=) prefix.
        const inOutMatches = validated.matchAll(/(^|[^A-Za-z0-9_)])(in|out)\s+(vec[234]|float|int|mat[234])\s+\w+\s*;/g);
        for (const m of inOutMatches) {
            const idx = m.index ?? 0;
            const surroundingStart = Math.max(0, idx - 60);
            const surrounding = validated.slice(surroundingStart, idx + m[0].length);
            // A `layout(location=N)` must appear immediately before the in/out keyword.
            expect(surrounding).toMatch(/layout\(location=\d+\)\s+(in|out)\b/);
        }

        // addedLines must be > 0 (we always prepend at least #version + 2 precision lines).
        expect(addedLines).toBeGreaterThanOrEqual(3);
    });
});

describe('Stock MonoGame shaders — full XNB emission', () => {
    it.each(ALL_STOCK_FIXTURES)('$name compiles end-to-end to a valid effect XNB', async ({ source }) => {
        const { xnb } = await compileFxToXnb({ source, assetName: 'test' });

        // XNB magic + classification.
        const cls = classifyXnb(xnb);
        expect(cls.kind).toBe('effect');
        expect(cls.parseError).toBeUndefined();

        // MGFX shape.
        const eff = parseEffect(cls.objectData!);
        expect(eff.version).toBe(10);
        expect(eff.profileId).toBe(0);                       // OpenGL profile
        // Content-hashed effectKey — must be non-zero so KNI's program
        // cache doesn't dedup distinct shaders.
        expect(eff.effectKey).not.toBe(0);
        // At least one technique with at least one pass that binds a PS.
        expect(eff.techniques.length).toBeGreaterThan(0);
        expect(eff.techniques[0].passes.length).toBeGreaterThan(0);
        expect(eff.techniques[0].passes[0].psShaderIndex).toBeGreaterThanOrEqual(0);
        // PS-only effects get an auto-injected default VS (see
        // injectDefaultVertexShader in compile-fx), so vsShaderIndex now
        // points at that VS rather than -1.
        expect(eff.techniques[0].passes[0].vsShaderIndex).toBeGreaterThanOrEqual(0);
    });

    it('SpriteEffect only carries the auto-injected default-VS cbuffer (no user-declared cbuffers)', async () => {
        const { xnb } = await compileFxToXnb({ source: SPRITE_EFFECT_FX, assetName: 'SpriteEffect' });
        const eff = parseEffect(classifyXnb(xnb).objectData!);
        // The stock SpriteEffect has no user cbuffer block — but a PS-only
        // effect ends up with exactly the synthetic `vs_uniforms_vec4`
        // cbuffer that backs MatrixTransform for the injected VS.
        expect(eff.constantBuffers).toHaveLength(1);
        expect(eff.constantBuffers[0].name).toBe('vs_uniforms_vec4');
        // SpriteTextureSampler param emitted as an Object (texture) param.
        const samplerParam = eff.parameters.find((p) => p.name === 'SpriteTextureSampler');
        expect(samplerParam).toBeDefined();
        expect(samplerParam!.class_).toBe(3);  // EffectParameterClass.Object
    });

    it('Tint emits the Tint parameter with class=Vector and cbuffer cbufferRefs', async () => {
        const { xnb } = await compileFxToXnb({ source: TINT_EFFECT_FX, assetName: 'Tint' });
        const eff = parseEffect(classifyXnb(xnb).objectData!);
        const tint = eff.parameters.find((p) => p.name === 'Tint');
        expect(tint).toBeDefined();
        expect(tint!.class_).toBe(1);   // Vector
        expect(tint!.rows).toBe(1);
        expect(tint!.columns).toBe(4);
        // ps_uniforms_vec4 cbuffer present, referenced by the PS shader.
        const cbuf = eff.constantBuffers.find((c) => c.name === 'ps_uniforms_vec4');
        expect(cbuf).toBeDefined();
        const ps = eff.shaders.find((s) => !s.isVertexShader)!;
        expect(ps.cbufferRefs.length).toBeGreaterThan(0);
    });
});
