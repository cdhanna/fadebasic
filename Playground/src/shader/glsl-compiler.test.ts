// Tests for the GLSL passthrough compiler. The reference for "what KNI
// accepts" is the ScreenEffect.xnb that's known to work end-to-end —
// see Playground/scripts/probe-screeneffect.mjs for how to extract its
// GLSL. The cardinal rules confirmed from that probe:
//
//   - GLSL ES 1.00 syntax only (no `#version 300 es`, no `layout(std140)
//     uniform { … }` blocks, `varying` not `in`/`out`, `gl_FragColor` not
//     custom out vars, `texture2D` not `texture`).
//   - Uniforms are flat — `uniform vec4 ps_uniforms_vec4[N];`, NOT uniform
//     blocks.
//   - If `#version` is written at all, it must end up on the literal first
//     line of the emitted source (KNI rejects even comments above it).
//
// Tests below pin both the happy path and the corner cases that bit us
// in real usage.

import { describe, it, expect } from 'vitest';
import { createGlslPassthroughCompiler } from './glsl-compiler';

const compiler = createGlslPassthroughCompiler();

describe('GLSL preamble — #version handling', () => {
    it('emits no #version line when the source has none (KNI defaults to ES 1.00)', async () => {
        const src =
`uniform sampler2D ps_s0;
varying vec4 vTexCoord0;
void main() { gl_FragColor = texture2D(ps_s0, vTexCoord0.xy); }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'main', stage: 'pixel', target: 'glsl-es-3.00',
        });
        expect(r.glslSource).not.toMatch(/#version/);
    });

    it('hoists a user-written #version to the literal first line', async () => {
        const src =
`// header comment
// another header comment
#version 100
precision mediump float;
void main() { gl_FragColor = vec4(1.0); }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'main', stage: 'pixel', target: 'glsl-es-3.00',
        });
        const firstLine = r.glslSource.split('\n')[0];
        expect(firstLine).toBe('#version 100');
    });

    it('strips comments and whitespace between #version and where it lives in the source', async () => {
        const src =
`/* multi
   line */
#version 100
void main() {}`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'main', stage: 'pixel', target: 'glsl-es-3.00',
        });
        // #version on line 1, and the source body still appears later.
        expect(r.glslSource.startsWith('#version 100\n')).toBe(true);
    });
});

describe('GLSL preamble — precision auto-insertion', () => {
    it('inserts precision mediump float for pixel shaders missing it', async () => {
        const src = `varying vec4 vTexCoord0;\nvoid main() { gl_FragColor = vTexCoord0; }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'main', stage: 'pixel', target: 'glsl-es-3.00',
        });
        expect(r.glslSource).toMatch(/precision\s+mediump\s+float\s*;/);
    });

    it('leaves an existing precision declaration alone', async () => {
        const src = `precision highp float;\nvoid main() { gl_FragColor = vec4(1.0); }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'main', stage: 'pixel', target: 'glsl-es-3.00',
        });
        // No duplicated precision.
        const matches = r.glslSource.match(/precision\s+\w+\s+float\s*;/g);
        expect(matches?.length).toBe(1);
        expect(matches?.[0]).toContain('highp');
    });

    it('does not insert precision into vertex shaders by default', async () => {
        const src = `attribute vec4 a_pos;\nvoid main() { gl_Position = a_pos; }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'main', stage: 'vertex', target: 'glsl-es-3.00',
        });
        expect(r.glslSource).not.toMatch(/precision\s+\w+\s+float/);
    });
});

describe('Entry-point trampoline — ES 1.00 output', () => {
    it('synthesises gl_FragColor trampoline for non-main pixel entrypoint', async () => {
        const src = `vec4 MainPS() { return vec4(1.0, 0.0, 0.0, 1.0); }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'MainPS', stage: 'pixel', target: 'glsl-es-3.00',
        });
        expect(r.glslSource).toMatch(/void\s+main\s*\(\s*\)\s*\{\s*gl_FragColor\s*=\s*MainPS\s*\(\s*\)\s*;\s*\}/);
        // And NOT the ES 3.00 `out vec4 _fragColor` shape we previously had.
        expect(r.glslSource).not.toMatch(/out\s+vec4\s+_fragColor/);
    });

    it('synthesises gl_Position trampoline for non-main vertex entrypoint', async () => {
        const src = `vec4 MainVS() { return vec4(0.0); }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'MainVS', stage: 'vertex', target: 'glsl-es-3.00',
        });
        expect(r.glslSource).toMatch(/void\s+main\s*\(\s*\)\s*\{\s*gl_Position\s*=\s*MainVS\s*\(\s*\)\s*;\s*\}/);
    });

    it('leaves an existing void main() untouched (no double-main)', async () => {
        const src = `void main() { gl_FragColor = vec4(1.0); }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'main', stage: 'pixel', target: 'glsl-es-3.00',
        });
        const mainMatches = r.glslSource.match(/void\s+main\s*\(/g);
        expect(mainMatches?.length).toBe(1);
    });
});

describe('Reflection — recognises ScreenEffect-shape declarations', () => {
    it('picks up uniform sampler2D as a sampler with the right name and type', async () => {
        const src =
`#ifdef GL_ES
precision mediump float;
#endif
uniform sampler2D ps_s0;
varying vec4 vTexCoord0;
void main() { gl_FragColor = texture2D(ps_s0, vTexCoord0.xy); }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'main', stage: 'pixel', target: 'glsl-es-3.00',
        });
        expect(r.samplers).toEqual([
            { name: 'ps_s0', binding: 0, samplerSlot: 0, samplerType: 0 },
        ]);
    });

    it('handles the #ifdef GL_ES + precision pattern without breaking reflection', async () => {
        // This is exactly the preamble ScreenEffect.xnb uses; make sure we
        // don't trip on the conditional directives.
        const src =
`#ifdef GL_ES
precision mediump float;
precision mediump int;
#endif
uniform vec4 ps_uniforms_vec4[2];
uniform sampler2D ps_s0;
varying vec4 vTexCoord0;
void main() { gl_FragColor = texture2D(ps_s0, vTexCoord0.xy); }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'main', stage: 'pixel', target: 'glsl-es-3.00',
        });
        expect(r.samplers).toHaveLength(1);
        expect(r.samplers[0].name).toBe('ps_s0');
        // No precision duplication should be added since the source has it
        // (gated behind #ifdef GL_ES, which counts).
        const matches = r.glslSource.match(/precision\s+\w+\s+float\s*;/g);
        expect(matches?.length).toBe(1);
    });
});

describe('ES 1.00 compatibility errors — surface clearly, dont let KNI bury them', () => {
    it('emits a clear error when source uses #version 300 es', async () => {
        const src =
`#version 300 es
precision mediump float;
in vec2 v_uv;
out vec4 fragColor;
void main() { fragColor = vec4(0.0, 1.0, 0.0, 1.0); }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'main', stage: 'pixel', target: 'glsl-es-3.00',
        });
        const err = r.diagnostics.find(d => d.severity === 'error');
        expect(err).toBeDefined();
        expect(err!.message).toMatch(/WebGL 1\.0|GLSL ES 1\.00|version/i);
        expect(err!.message).toMatch(/varying|texture2D|gl_FragColor/);
    });

    it('emits a clear error when source uses a uniform block', async () => {
        const src =
`uniform sampler2D ps_s0;
layout(std140) uniform ps_uniforms_vec4 { vec4 Tint; };
varying vec4 vTexCoord0;
void main() { gl_FragColor = texture2D(ps_s0, vTexCoord0.xy) * Tint; }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'main', stage: 'pixel', target: 'glsl-es-3.00',
        });
        const err = r.diagnostics.find(d => d.severity === 'error');
        expect(err).toBeDefined();
        expect(err!.message).toMatch(/uniform block|layout\(std140\)/i);
        expect(err!.message).toMatch(/ps_uniforms_vec4|plain 'uniform'/);
    });

    it('does NOT flag the ScreenEffect-shape preamble (#version-less, plain uniforms)', async () => {
        const src =
`#ifdef GL_ES
precision mediump float;
#endif
uniform vec4 ps_uniforms_vec4[2];
uniform sampler2D ps_s0;
varying vec4 vTexCoord0;
void main() { gl_FragColor = texture2D(ps_s0, vTexCoord0.xy); }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'main', stage: 'pixel', target: 'glsl-es-3.00',
        });
        const errors = r.diagnostics.filter(d => d.severity === 'error');
        expect(errors).toEqual([]);
    });
});

describe('Full output sanity — well-formed ES 1.00', () => {
    it('does NOT emit ES 3.00 features by default (no layout/in/out)', async () => {
        const src = `vec4 MainPS() { return vec4(1.0); }`;
        const r = await compiler.compileHlsl({
            source: src, entrypoint: 'MainPS', stage: 'pixel', target: 'glsl-es-3.00',
        });
        expect(r.glslSource).not.toMatch(/layout\s*\(\s*std140/);
        expect(r.glslSource).not.toMatch(/^out\s+vec4/m);
        expect(r.glslSource).not.toMatch(/^in\s+vec[234]/m);
    });
});
