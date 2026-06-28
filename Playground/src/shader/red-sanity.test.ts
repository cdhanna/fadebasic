// End-to-end test for the red sanity shader: real FX parser + real
// GlslPassthrough compiler + real MGFX writer. Validates that the
// emitted XNB has the same structural shape as the known-working
// ScreenEffect.xnb the runtime already loads successfully.
//
// Probed values come from scripts/probe-effect-params.mjs against
// the working ScreenEffect.xnb:
//   - version=10 (after KNI patch), profile=0 (OpenGL)
//   - psShaderIndex=0
//   - sampler textureSlot=0 (binds to SpriteBatch's mainBuffer slot)
//
// PS-only effects also pick up a synthetic default VS (added by
// compile-fx; see injectDefaultVertexShader). That VS mirrors
// FadeSpriteBatchEffect's compiled VS exactly so behavior matches the
// stock SpriteBatch pipeline. The pass's vsShaderIndex therefore points
// at that injected VS, not -1.

import { describe, it, expect, beforeEach } from 'vitest';
import { compileFxToXnb } from './compile-fx';
import { setShaderCompilerFactory } from './shader-compiler';
import { createGlslPassthroughCompiler } from './glsl-compiler';
import { classifyXnb } from '../xnb/xnb-reader';
import { parseEffect } from '../xnb/mgfx';

beforeEach(() => {
    setShaderCompilerFactory(async () => createGlslPassthroughCompiler());
});

const RED_SANITY = `#ifdef GL_ES
precision mediump float;
#endif

void main() {
    gl_FragColor = vec4(1.0, 0.0, 0.0, 1.0);
}

technique T {
    pass P {
        PixelShader = compile ps_4_0 main();
    }
}`;

describe('Red sanity — end-to-end with REAL GlslPassthroughCompiler', () => {
    it('emits a valid effect XNB classifiable as kind=effect', async () => {
        const { xnb } = await compileFxToXnb({ source: RED_SANITY });
        const cls = classifyXnb(xnb);
        expect(cls.kind).toBe('effect');
        expect(cls.parseError).toBeUndefined();
        expect(cls.objectData).toBeTruthy();
    });

    it('emits MGFX v10 with OpenGL profile (matches ScreenEffect.xnb post-patch)', async () => {
        const { xnb } = await compileFxToXnb({ source: RED_SANITY });
        const eff = parseEffect(classifyXnb(xnb).objectData!);
        expect(eff.version).toBe(10);
        expect(eff.profileId).toBe(0);
    });

    it('has the user PS + an auto-injected default VS, both wired into the pass', async () => {
        const { xnb } = await compileFxToXnb({ source: RED_SANITY });
        const eff = parseEffect(classifyXnb(xnb).objectData!);
        expect(eff.shaders).toHaveLength(2);
        const ps = eff.shaders.find(s => !s.isVertexShader)!;
        const vs = eff.shaders.find(s => s.isVertexShader)!;
        expect(ps).toBeDefined();
        expect(vs).toBeDefined();

        expect(eff.techniques).toHaveLength(1);
        expect(eff.techniques[0].passes).toHaveLength(1);
        const pass = eff.techniques[0].passes[0];
        // Both indices resolve to real shader records — no -1 sentinel now.
        expect(pass.vsShaderIndex).toBeGreaterThanOrEqual(0);
        expect(pass.psShaderIndex).toBeGreaterThanOrEqual(0);
        expect(eff.shaders[pass.vsShaderIndex].isVertexShader).toBe(true);
        expect(eff.shaders[pass.psShaderIndex].isVertexShader).toBe(false);
    });

    it('shader bytecode is valid UTF-8 GLSL containing the red output', async () => {
        const { xnb } = await compileFxToXnb({ source: RED_SANITY });
        const eff = parseEffect(classifyXnb(xnb).objectData!);
        const glsl = new TextDecoder('utf-8').decode(eff.shaders[0].bytecode);
        // Contains the expected output line.
        expect(glsl).toMatch(/gl_FragColor\s*=\s*vec4\s*\(\s*1\.0\s*,\s*0\.0\s*,\s*0\.0\s*,\s*1\.0/);
        // No errant `#version` directive (KNI defaults to ES 1.00).
        expect(glsl).not.toMatch(/#version\s+3\d\d/);
        // Precision declaration is present (otherwise GL ES rejects the PS).
        expect(glsl).toMatch(/precision\s+(low|medium|high)p\s+float\s*;/);
        // The technique block must NOT have leaked into the bytecode — only
        // the trailing whitespace from stripping it.
        expect(glsl).not.toContain('technique');
        expect(glsl).not.toContain('pass P');
    });

    it('exposes a MatrixTransform parameter + vs_uniforms_vec4 cbuffer from the injected VS', async () => {
        const { xnb } = await compileFxToXnb({ source: RED_SANITY });
        const eff = parseEffect(classifyXnb(xnb).objectData!);
        expect(eff.parameters.find(p => p.name === 'MatrixTransform')).toBeDefined();
        expect(eff.constantBuffers.find(cb => cb.name === 'vs_uniforms_vec4')).toBeDefined();
    });

    it('PS shader has empty samplers/attributes; VS has 4 vertex attributes', async () => {
        const { xnb } = await compileFxToXnb({ source: RED_SANITY });
        const eff = parseEffect(classifyXnb(xnb).objectData!);
        const ps = eff.shaders.find(s => !s.isVertexShader)!;
        const vs = eff.shaders.find(s => s.isVertexShader)!;
        expect(ps.samplers).toHaveLength(0);
        expect(ps.attributes).toHaveLength(0);
        // Default VS reads position + color + texcoord0 + texcoord1.
        expect(vs.attributes).toHaveLength(4);
    });

    it('XNB envelope: fileSize matches actual byte length', async () => {
        const { xnb } = await compileFxToXnb({ source: RED_SANITY });
        // bytes 6..9 are uint32 LE fileSize. Should equal xnb.length.
        const claimed = xnb[6] | (xnb[7] << 8) | (xnb[8] << 16) | (xnb[9] << 24);
        expect(claimed).toBe(xnb.length);
    });
});
