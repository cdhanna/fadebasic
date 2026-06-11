import { describe, it, expect } from 'vitest';
import { translateHlslToGlsl } from './hlsl-translator';
import type { FxCbufferDecl } from './fx-parser';

const NO_CBUFFERS: FxCbufferDecl[] = [];

const TINT_CBUFFER: FxCbufferDecl = {
    name: 'ps_uniforms_vec4',
    fields: [{
        typeName: 'float4', name: 'Tint',
        arraySize: 0, rows: 1, columns: 4,
        offsetBytes: 0, sizeBytes: 16,
    }],
    sizeInBytes: 16,
    sourceStart: 0,
    sourceEnd: 0,
};

describe('translateHlslToGlsl — type substitution', () => {
    it('replaces float4/float3/float2 with vec equivalents', () => {
        const src = `float4 MainPS(float2 uv : TEXCOORD0) : SV_TARGET { float3 c = float3(1, 0, 0); return float4(c, 1); }`;
        const { glsl } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        expect(glsl).not.toMatch(/\bfloat4\b/);
        expect(glsl).not.toMatch(/\bfloat3\b/);
        expect(glsl).not.toMatch(/\bfloat2\b/);
        expect(glsl).toMatch(/\bvec4\b/);
        expect(glsl).toMatch(/\bvec3\b/);
        expect(glsl).toMatch(/\bvec2\b/);
    });

    it('replaces float4x4/float3x3 with mat4/mat3 before float4/3 substitution kicks in', () => {
        const src = `float4x4 World; float3x3 Norm;
float4 MainPS() : SV_TARGET { return float4(1); }`;
        const { glsl } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        expect(glsl).toMatch(/\bmat4 World\b/);
        expect(glsl).toMatch(/\bmat3 Norm\b/);
        expect(glsl).not.toMatch(/vec4x4|vec3x3/);
    });
});

describe('translateHlslToGlsl — texture + sampler declarations', () => {
    it('rewrites Texture2D X to uniform sampler2D X and drops SamplerState', () => {
        const src = `Texture2D ps_s0 : register(t0);
SamplerState ps_s0_sampler : register(s0);
float4 MainPS(float2 uv : TEXCOORD0) : SV_TARGET { return ps_s0.Sample(ps_s0_sampler, uv); }`;
        const { glsl, samplers } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        expect(glsl).toMatch(/uniform sampler2D ps_s0;/);
        expect(glsl).not.toMatch(/SamplerState/);
        expect(glsl).not.toMatch(/Texture2D/);
        expect(samplers).toEqual([{ name: 'ps_s0', samplerType: 0 }]);
    });

    it('translates the .Sample method call to texture2D', () => {
        const src = `Texture2D tex; SamplerState smp;
float4 MainPS(float2 uv : TEXCOORD0) : SV_TARGET { return tex.Sample(smp, uv); }`;
        const { glsl } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        expect(glsl).toMatch(/texture2D\s*\(\s*tex\s*,\s*uv\s*\)/);
        expect(glsl).not.toMatch(/\.Sample\(/);
    });
});

describe('translateHlslToGlsl — intrinsic renames', () => {
    it('renames lerp/frac/rsqrt/atan2', () => {
        const src = `float4 MainPS() : SV_TARGET {
    return float4(lerp(0.0, 1.0, frac(rsqrt(atan2(1.0, 2.0)))));
}`;
        const { glsl } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        expect(glsl).toMatch(/\bmix\(/);
        expect(glsl).toMatch(/\bfract\(/);
        expect(glsl).toMatch(/\binversesqrt\(/);
        expect(glsl).toMatch(/\batan\(/);
        expect(glsl).not.toMatch(/\blerp\(|\bfrac\(|\brsqrt\(|\batan2\(/);
    });

    it('translates saturate(x) into clamp((x), 0.0, 1.0)', () => {
        const src = `float4 MainPS() : SV_TARGET { return float4(saturate(0.5)); }`;
        const { glsl } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        expect(glsl).toMatch(/clamp\(\(0\.5\), 0\.0, 1\.0\)/);
    });

    it('saturate balances parens for nested expressions', () => {
        const src = `float4 MainPS() : SV_TARGET { return float4(saturate(dot(float3(1,2,3), float3(4,5,6)))); }`;
        const { glsl } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        // The nested dot(…) call has its own parens; saturate's close paren
        // is the OUTER one and 0.0/1.0 should be inserted before it.
        expect(glsl).toMatch(/clamp\(\(dot\(vec3\(1,2,3\), vec3\(4,5,6\)\)\), 0\.0, 1\.0\)/);
    });
});

describe('translateHlslToGlsl — entry function rewriting', () => {
    it('strips semantics, synthesizes main() trampoline for a PS', () => {
        const src = `Texture2D tex; SamplerState smp;
float4 MainPS(float2 uv : TEXCOORD0) : SV_TARGET {
    return tex.Sample(smp, uv);
}`;
        const { glsl } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        // No SV_TARGET / TEXCOORD0 in the output.
        expect(glsl).not.toMatch(/SV_TARGET/);
        expect(glsl).not.toMatch(/TEXCOORD0/);
        // Varying for the TEXCOORD0 input.
        expect(glsl).toMatch(/varying\s+vec4\s+vTexCoord0\s*;/);
        // main() calls MainPS with the varying-derived arg.
        expect(glsl).toMatch(/void\s+main\s*\(\s*\)\s*\{\s*gl_FragColor\s*=\s*MainPS\(vTexCoord0\.xy\)/);
    });

    it('emits a warning when entry function is missing', () => {
        const src = `float4 SomeOtherFunc() : SV_TARGET { return float4(1); }`;
        const { diagnostics } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        expect(diagnostics.some(d => d.message.includes("'MainPS' not found"))).toBe(true);
    });

    it('handles entry with no parameters at all', () => {
        const src = `float4 MainPS() : SV_TARGET { return float4(1, 0, 0, 1); }`;
        const { glsl } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        expect(glsl).toMatch(/void\s+main\s*\(\s*\)\s*\{\s*gl_FragColor\s*=\s*MainPS\(\)\s*;\s*\}/);
        // No spurious varying decls when there are no parameters.
        expect(glsl).not.toMatch(/^varying\s/m);
    });
});

describe('translateHlslToGlsl — cbuffer stub synthesis', () => {
    it('emits uniform vec4 array + #define aliases for a single-field cbuffer', () => {
        const src = `Texture2D ps_s0; SamplerState ps_s0_sampler;
float4 MainPS(float2 uv : TEXCOORD0) : SV_TARGET {
    return ps_s0.Sample(ps_s0_sampler, uv) * Tint;
}`;
        const { glsl } = translateHlslToGlsl({
            source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: [TINT_CBUFFER],
        });
        expect(glsl).toMatch(/uniform vec4 ps_uniforms_vec4\[1\];/);
        expect(glsl).toMatch(/#define Tint ps_uniforms_vec4\[0\]/);
    });

    it('packs multiple scalars into the same vec4 slot with distinct swizzles (.x, .y, .z, .w)', () => {
        // HLSL packs sequential float scalars tightly: Time at offset 0,
        // GlitchAmount at offset 4, Phase at offset 8, Amount at offset 12
        // — all four live in vec4 slot 0. Previously the alias emitter
        // ignored the in-slot byte offset and aliased EVERY scalar to
        // `.x`, so only the first parameter the user `set effect param`'d
        // actually moved — second/third/fourth all read the first one's
        // .x value through the collapsed `#define`.
        const packed: FxCbufferDecl = {
            name: 'cb',
            fields: [
                { typeName: 'float', name: 'Time',         arraySize: 0, rows: 1, columns: 1, offsetBytes:  0, sizeBytes: 4 },
                { typeName: 'float', name: 'GlitchAmount', arraySize: 0, rows: 1, columns: 1, offsetBytes:  4, sizeBytes: 4 },
                { typeName: 'float', name: 'Phase',        arraySize: 0, rows: 1, columns: 1, offsetBytes:  8, sizeBytes: 4 },
                { typeName: 'float', name: 'Amount',       arraySize: 0, rows: 1, columns: 1, offsetBytes: 12, sizeBytes: 4 },
            ],
            sizeInBytes: 16, sourceStart: 0, sourceEnd: 0,
        };
        const src = `float4 MainPS() : SV_TARGET { return float4(Time, GlitchAmount, Phase, Amount); }`;
        const { glsl } = translateHlslToGlsl({
            source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: [packed],
        });
        expect(glsl).toMatch(/uniform vec4 cb\[1\];/);
        expect(glsl).toMatch(/#define Time cb\[0\]\.x/);
        expect(glsl).toMatch(/#define GlitchAmount cb\[0\]\.y/);
        expect(glsl).toMatch(/#define Phase cb\[0\]\.z/);
        expect(glsl).toMatch(/#define Amount cb\[0\]\.w/);
    });

    it('places a vec2 at .zw when it follows two scalars in the same slot', () => {
        const packed: FxCbufferDecl = {
            name: 'cb',
            fields: [
                { typeName: 'float',  name: 'A', arraySize: 0, rows: 1, columns: 1, offsetBytes: 0, sizeBytes: 4 },
                { typeName: 'float',  name: 'B', arraySize: 0, rows: 1, columns: 1, offsetBytes: 4, sizeBytes: 4 },
                { typeName: 'float2', name: 'C', arraySize: 0, rows: 1, columns: 2, offsetBytes: 8, sizeBytes: 8 },
            ],
            sizeInBytes: 16, sourceStart: 0, sourceEnd: 0,
        };
        const src = `float4 MainPS() : SV_TARGET { return float4(A, B, C); }`;
        const { glsl } = translateHlslToGlsl({
            source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: [packed],
        });
        expect(glsl).toMatch(/#define A cb\[0\]\.x/);
        expect(glsl).toMatch(/#define B cb\[0\]\.y/);
        expect(glsl).toMatch(/#define C cb\[0\]\.zw/);
    });

    it('handles mixed-type cbuffer fields with the right swizzle per field size', () => {
        const mixed: FxCbufferDecl = {
            name: 'cb',
            fields: [
                { typeName: 'float',  name: 'Time',  arraySize: 0, rows: 1, columns: 1, offsetBytes:  0, sizeBytes:  4 },
                { typeName: 'float4', name: 'Tint',  arraySize: 0, rows: 1, columns: 4, offsetBytes: 16, sizeBytes: 16 },
                { typeName: 'float2', name: 'Scale', arraySize: 0, rows: 1, columns: 2, offsetBytes: 32, sizeBytes:  8 },
                { typeName: 'float3', name: 'Up',    arraySize: 0, rows: 1, columns: 3, offsetBytes: 48, sizeBytes: 12 },
            ],
            sizeInBytes: 64, sourceStart: 0, sourceEnd: 0,
        };
        const src = `float4 MainPS() : SV_TARGET { return Tint * Time; }`;
        const { glsl } = translateHlslToGlsl({
            source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: [mixed],
        });
        expect(glsl).toMatch(/uniform vec4 cb\[4\];/);
        expect(glsl).toMatch(/#define Time cb\[0\]\.x/);
        expect(glsl).toMatch(/#define Tint cb\[1\]/);
        expect(glsl).toMatch(/#define Scale cb\[2\]\.xy/);
        expect(glsl).toMatch(/#define Up cb\[3\]\.xyz/);
    });
});

describe('translateHlslToGlsl — DX9 / MonoGame compat', () => {
    it('translates tex2D(smp, uv) to texture2D(smp, uv)', () => {
        const src = `Texture2D tex; SamplerState smp;
float4 MainPS(float2 uv : TEXCOORD0) : SV_TARGET { return tex2D(tex, uv); }`;
        const { glsl } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        expect(glsl).toMatch(/texture2D\(tex, uv\)/);
        expect(glsl).not.toMatch(/\btex2D\(/);
    });

    it('aliases DX9 POSITION to SV_POSITION and COLOR to SV_TARGET', () => {
        const src = `float4 MainPS(float2 uv : TEXCOORD0) : COLOR { return float4(1); }`;
        const { glsl } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        // COLOR return semantic gets stripped just like SV_TARGET would.
        expect(glsl).not.toMatch(/:\s*COLOR\b/);
        expect(glsl).toMatch(/void\s+main\s*\(\s*\)/);
    });

    it('aliases `matrix` to mat4', () => {
        const src = `matrix World;
float4 MainPS() : SV_TARGET { return float4(1); }`;
        const { glsl } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        expect(glsl).toMatch(/\bmat4 World\b/);
        expect(glsl).not.toMatch(/\bmatrix\s+World\b/);
    });

    it('keeps the #if OPENGL branch and drops the #else half', () => {
        const src = `
#if OPENGL
    #define KEEP_THIS 1
#else
    #define DROP_THIS 1
#endif
float4 MainPS() : SV_TARGET { return float4(1); }
`;
        const { glsl } = translateHlslToGlsl({ source: src, entrypoint: 'MainPS', stage: 'pixel', cbuffers: NO_CBUFFERS });
        expect(glsl).toContain('KEEP_THIS');
        expect(glsl).not.toContain('DROP_THIS');
        // The user's `#if OPENGL` and its `#else` should be gone. We can't
        // assert `not.toMatch(/#endif/)` because the precision header
        // adds its own `#ifdef GL_ES … #endif` pair.
        expect(glsl).not.toMatch(/#if\s+OPENGL/);
        expect(glsl).not.toMatch(/#else/);
    });

    it('emits uniform sampler2D from DX9 sampler_state literals', () => {
        // The literal-extraction lives in fx-parser; the translator
        // receives the records directly via the samplerStateLiterals option.
        const { glsl, samplers } = translateHlslToGlsl({
            source: `float4 MainPS(float2 uv : TEXCOORD0) : SV_TARGET { return tex2D(SpriteTextureSampler, uv); }`,
            entrypoint: 'MainPS',
            stage: 'pixel',
            cbuffers: NO_CBUFFERS,
            samplerStateLiterals: [{
                samplerName: 'SpriteTextureSampler',
                samplerType: 'sampler2D',
                textureRef: 'SpriteTexture',
                assigns: [],
                sourceStart: 0,
                sourceEnd: 0,
            }],
        });
        expect(glsl).toMatch(/uniform sampler2D SpriteTextureSampler;/);
        expect(samplers.some(s => s.name === 'SpriteTextureSampler')).toBe(true);
    });
});

describe('translateHlslToGlsl — struct-typed entry parameter', () => {
    const SPRITE_FX = `Texture2D SpriteTexture;
sampler2D SpriteTextureSampler;

struct VertexShaderOutput {
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : SV_TARGET {
    return tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
}`;

    it('keeps the struct decl (semantics stripped) and builds the trampoline from per-field varyings', () => {
        const { glsl } = translateHlslToGlsl({
            source: SPRITE_FX,
            entrypoint: 'MainPS',
            stage: 'pixel',
            cbuffers: NO_CBUFFERS,
            samplerStateLiterals: [{
                samplerName: 'SpriteTextureSampler',
                samplerType: 'sampler2D',
                textureRef: 'SpriteTexture',
                assigns: [],
                sourceStart: 0,
                sourceEnd: 0,
            }],
        });
        // Struct stays in the GLSL output, with semantics stripped.
        expect(glsl).toMatch(/struct\s+VertexShaderOutput\s*\{/);
        expect(glsl).not.toMatch(/:\s*SV_POSITION/);
        expect(glsl).not.toMatch(/:\s*COLOR0/);
        expect(glsl).not.toMatch(/:\s*TEXCOORD0/);

        // Varyings for COLOR0 + TEXCOORD0 (SV_POSITION → gl_FragCoord, no varying).
        expect(glsl).toMatch(/varying\s+vec4\s+vFrontColor\s*;/);
        expect(glsl).toMatch(/varying\s+vec4\s+vTexCoord0\s*;/);

        // Main trampoline constructs the struct and passes it. The param is
        // named `input` in HLSL — a GLSL ES 1.00 reserved word — so the
        // translator renames it to `_input` everywhere.
        expect(glsl).toMatch(/VertexShaderOutput\s+_input\s*;/);
        expect(glsl).toMatch(/_input\.Color\s*=\s*vFrontColor\s*;/);
        expect(glsl).toMatch(/_input\.TextureCoordinates\s*=\s*vTexCoord0\.xy\s*;/);
        // PS pulls SV_POSITION from gl_FragCoord.
        expect(glsl).toMatch(/_input\.Position\s*=\s*gl_FragCoord\s*;/);
        expect(glsl).toMatch(/gl_FragColor\s*=\s*MainPS\(_input\)/);
    });
});

describe('translateHlslToGlsl — full SpriteEffect drop-in', () => {
    it('handles the canonical MonoGame SpriteEffect end-to-end', () => {
        // Real MonoGame stock SpriteEffect.fx — copy-paste.
        const src = `#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;

struct VertexShaderOutput {
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR {
    return tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
}`;
        const { glsl, samplers, diagnostics } = translateHlslToGlsl({
            source: src,
            entrypoint: 'MainPS',
            stage: 'pixel',
            cbuffers: NO_CBUFFERS,
            samplerStateLiterals: [{
                samplerName: 'SpriteTextureSampler',
                samplerType: 'sampler2D',
                textureRef: 'SpriteTexture',
                assigns: [],
                sourceStart: 0, sourceEnd: 0,
            }],
        });
        expect(diagnostics.filter(d => d.severity === 'error')).toEqual([]);
        // One sampler: SpriteTextureSampler. The `Texture2D SpriteTexture;`
        // declaration is absorbed by the sampler_state literal that
        // references it (`Texture = <SpriteTexture>`), since emitting both
        // as separate GL uniforms produced two MGFX sampler records and
        // KNI's BlazorGL TextureCollection bounds-checked the extra slot
        // → IndexOutOfRangeException at draw time.
        expect(samplers.some(s => s.name === 'SpriteTextureSampler')).toBe(true);
        expect(samplers.some(s => s.name === 'SpriteTexture')).toBe(false);
        expect(glsl).not.toMatch(/uniform sampler2D SpriteTexture;/);
        // Output should have no HLSL leftovers anywhere.
        expect(glsl).not.toMatch(/Texture2D|SamplerState|sampler_state|SV_TARGET|SV_POSITION|:\s*COLOR\b|TEXCOORD0|\.Sample\(/);
        // tex2D translated.
        expect(glsl).toMatch(/texture2D\(SpriteTextureSampler/);
        // Trampoline wired. The parameter named `input` (HLSL reserved in
        // GLSL ES 1.00) is renamed to `_input` by the translator.
        expect(glsl).toMatch(/void\s+main\s*\(\s*\).*MainPS\(_input\)/s);
    });
});

describe('translateHlslToGlsl — GLSL reserved word collisions', () => {
    it('renames a parameter named `input` (reserved in GLSL ES 1.00) plus all body refs', () => {
        const src = `Texture2D tex;
struct VS_OUT { float4 Color : COLOR0; float2 TextureCoordinates : TEXCOORD0; };
float4 MainPS(VS_OUT input) : SV_TARGET {
    float4 sampled = tex2D(tex, input.TextureCoordinates);
    return sampled * input.Color;
}`;
        const { glsl } = translateHlslToGlsl({
            source: src,
            entrypoint: 'MainPS',
            stage: 'pixel',
            cbuffers: NO_CBUFFERS,
        });
        // The function signature should NOT contain `input` as a bare param name.
        expect(glsl).not.toMatch(/\bMainPS\s*\(\s*VS_OUT\s+input\s*\)/);
        // It should have been renamed (we use `_input`).
        expect(glsl).toMatch(/\bMainPS\s*\(\s*VS_OUT\s+_input\s*\)/);
        // Body references rewritten too.
        expect(glsl).toMatch(/_input\.TextureCoordinates/);
        expect(glsl).toMatch(/_input\.Color/);
        expect(glsl).not.toMatch(/\binput\.(?:TextureCoordinates|Color)\b/);
    });

    it('leaves non-reserved parameter names alone', () => {
        const src = `float4 MainPS(float2 uv : TEXCOORD0) : SV_TARGET { return float4(uv, 0, 1); }`;
        const { glsl } = translateHlslToGlsl({
            source: src,
            entrypoint: 'MainPS',
            stage: 'pixel',
            cbuffers: NO_CBUFFERS,
        });
        // `uv` is not reserved — stay as-is.
        expect(glsl).toMatch(/\bMainPS\s*\(\s*vec2\s+uv\s*\)/);
        expect(glsl).not.toMatch(/\b_uv\b/);
    });
});

describe('translateHlslToGlsl — line-number preservation', () => {
    // Regression test: this used to silently drop one line per struct
    // because `struct Name\n{` was being collapsed to `struct Name {`,
    // dropping the newline between the name and the brace. That made
    // glslang error-line numbers map back to the wrong .fx line in the
    // editor LSP.
    it('preserves total newline count when translating a multi-line struct', () => {
        const src = `Texture2D tex;

struct VS_OUT
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VS_OUT input) : SV_TARGET {
    return tex2D(tex, input.TextureCoordinates) * input.Color;
}`;
        const { glsl } = translateHlslToGlsl({
            source: src,
            entrypoint: 'MainPS',
            stage: 'pixel',
            cbuffers: NO_CBUFFERS,
        });
        // Locate landmark lines in original + translated. Their RELATIVE
        // offset must be identical — translator must preserve the line
        // count between them.
        const origLines = src.split('\n');
        const gLines = glsl.split('\n');
        const origTex = origLines.findIndex((l) => /\bTexture2D\s+tex\b/.test(l));
        const origMainPS = origLines.findIndex((l) => /float4\s+MainPS\b/.test(l));
        const gTex = gLines.findIndex((l) => /\buniform sampler2D tex\b/.test(l));
        const gMainPS = gLines.findIndex((l) => /vec4\s+MainPS\b/.test(l));
        // Both landmarks should be present in the output.
        expect(gTex).toBeGreaterThan(-1);
        expect(gMainPS).toBeGreaterThan(-1);
        // The relative distance MUST match (with no line dropped between them).
        expect(gMainPS - gTex).toBe(origMainPS - origTex);
    });
});

describe('translateHlslToGlsl — preamble line count + source mapping', () => {
    it('reports a non-zero preambleLineCount when emitting precision/cbuffer/varying decls', () => {
        const src = `Texture2D tex;
struct VS_OUT { float4 Color : COLOR0; float2 TextureCoordinates : TEXCOORD0; };
float4 MainPS(VS_OUT input) : SV_TARGET {
    return tex2D(tex, input.TextureCoordinates) * input.Color;
}`;
        const { glsl, preambleLineCount } = translateHlslToGlsl({
            source: src,
            entrypoint: 'MainPS',
            stage: 'pixel',
            cbuffers: NO_CBUFFERS,
        });
        expect(preambleLineCount).toBeGreaterThan(0);
        // The reported count should be the line index at which the user's
        // body begins. The line at preambleLineCount+1 should be the first
        // line of the source body (not a translator-emitted decl).
        const lines = glsl.split('\n');
        // Just under preambleLineCount we expect generated content (varyings
        // or uniform decls); at preambleLineCount we expect the start of
        // the (whitespaced) user content. Check that the preamble
        // doesn't contain user identifiers like `MainPS` or `return`.
        const preamble = lines.slice(0, preambleLineCount).join('\n');
        expect(preamble).not.toMatch(/\bMainPS\b/);
        expect(preamble).not.toMatch(/\breturn\b/);
    });

    it('preserves line counts when #if OPENGL block is stripped', () => {
        // Counting the `#if OPENGL` block as 6 lines (the block + #endif).
        // After stripping, the translator should emit 6 blank/preserved
        // newlines so a glslang error on the next code line still maps to
        // the right .fx line.
        const src = `#if OPENGL
    #define A 1
    #define B 2
#else
    #define A 100
#endif
float4 MainPS() : SV_TARGET { return float4(1); }`;
        const { glsl } = translateHlslToGlsl({
            source: src,
            entrypoint: 'MainPS',
            stage: 'pixel',
            cbuffers: NO_CBUFFERS,
        });
        // The line index of `MainPS` in the translated GLSL should be at
        // least as large as in the source (since we add a preamble), and
        // the relative spacing within the user body should be preserved.
        const userBodyStart = src.split('\n').findIndex(l => l.includes('MainPS'));
        expect(userBodyStart).toBe(6);  // sanity: source has it on line 7 (0-indexed 6)
        // Find the line in the translated GLSL that contains MainPS.
        const gpos = glsl.split('\n').findIndex(l => l.includes('MainPS'));
        // The user-body content should appear at or after the source line
        // index (translator preamble shifts everything forward).
        expect(gpos).toBeGreaterThanOrEqual(userBodyStart);
    });
});

describe('translateHlslToGlsl — full Tint example', () => {
    // The same HLSL we'd hand to the user as the template. Verifies the
    // end-to-end translation produces something KNI's GL backend can compile.
    const HLSL = `Texture2D ps_s0;
SamplerState ps_s0_sampler;

float4 MainPS(float2 uv : TEXCOORD0) : SV_TARGET {
    float4 sampled = ps_s0.Sample(ps_s0_sampler, uv);
    return sampled * Tint;
}`;

    it('produces well-formed GLSL ES 1.00 with the expected components', () => {
        const { glsl } = translateHlslToGlsl({
            source: HLSL,
            entrypoint: 'MainPS',
            stage: 'pixel',
            cbuffers: [TINT_CBUFFER],
        });
        // Precision declaration — matched across both stages (highp) so
        // uniforms shared between VS and PS link cleanly.
        expect(glsl).toMatch(/precision\s+highp\s+float\s*;/);
        // Cbuffer plumbed.
        expect(glsl).toMatch(/uniform vec4 ps_uniforms_vec4\[1\];/);
        expect(glsl).toMatch(/#define Tint ps_uniforms_vec4\[0\]/);
        // Sampler from Texture2D.
        expect(glsl).toMatch(/uniform sampler2D ps_s0;/);
        // Varying for TEXCOORD0.
        expect(glsl).toMatch(/varying vec4 vTexCoord0;/);
        // texture2D call (translated from Sample).
        expect(glsl).toMatch(/texture2D\(ps_s0, uv\)/);
        // Trampoline.
        expect(glsl).toMatch(/void\s+main\s*\(\s*\)\s*\{\s*gl_FragColor\s*=\s*MainPS\(vTexCoord0\.xy\)/);
        // No HLSL leftovers.
        expect(glsl).not.toMatch(/Texture2D|SamplerState|SV_TARGET|TEXCOORD0|\.Sample\(/);
    });
});

describe('translateHlslToGlsl — vertex shaders', () => {
    const SPRITE_VS = `cbuffer Globals
{
    float4x4 MatrixTransform;
};

struct VertexShaderInput {
    float4 Position : POSITION0;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

struct VertexShaderOutput {
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

VertexShaderOutput MainVS(VertexShaderInput input) {
    VertexShaderOutput output;
    output.Position = mul(input.Position, MatrixTransform);
    output.Color = input.Color;
    output.TextureCoordinates = input.TextureCoordinates;
    return output;
}`;

    const SPRITE_VS_CBUFFERS: FxCbufferDecl[] = [
        {
            name: 'Globals',
            sizeInBytes: 64,
            sourceStart: 0,
            sourceEnd: 0,
            fields: [
                { typeName: 'float4x4', name: 'MatrixTransform', offsetBytes: 0, sizeBytes: 64, rows: 4, columns: 4, arraySize: 0 },
            ],
        },
    ];

    it('translates a struct-returning VS into gl_Position + per-semantic varying writes', () => {
        const { glsl, attributes, diagnostics } = translateHlslToGlsl({
            source: SPRITE_VS,
            entrypoint: 'MainVS',
            stage: 'vertex',
            cbuffers: SPRITE_VS_CBUFFERS,
        });
        expect(diagnostics.filter(d => d.severity === 'error')).toEqual([]);

        // VS needs posFixup + the cbuffer + varying outputs declared.
        expect(glsl).toMatch(/uniform vec4 posFixup;/);
        expect(glsl).toMatch(/uniform vec4 Globals\[4\];/);
        // Matrix at offset 0 → `#define` aliases the entire array so
        // `MatrixTransform[i]` resolves to `Globals[i]`.
        expect(glsl).toMatch(/#define MatrixTransform Globals\b/);

        // VS-output varyings carry the same names the PS reads
        // (vFrontColor/vTexCoord0); SV_POSITION goes to gl_Position.
        expect(glsl).toMatch(/varying vec4 vFrontColor;/);
        expect(glsl).toMatch(/varying vec4 vTexCoord0;/);
        expect(glsl).not.toMatch(/varying[^\n]*SV_POSITION/);

        // VS-input attribute declarations use the `a*` namespace so they
        // can't collide with the `v*` output varying names (which would
        // produce a GLSL "redefinition" error in shaders that pass
        // COLOR0/TEXCOORD0 straight through).
        expect(glsl).toMatch(/attribute vec4 aPosition0;/);
        expect(glsl).toMatch(/attribute vec4 aColor0;/);
        expect(glsl).toMatch(/attribute vec4 aTexCoord0;/);
        expect(glsl).not.toMatch(/attribute vec4 vFrontColor;/);
        expect(glsl).not.toMatch(/attribute vec4 vTexCoord0;/);

        // Reflected attributes carry XNA VertexElementUsage codes for
        // MGFX vertex-slot binding. Position=0, Color=1, TexCoord=2 —
        // matches the MonoGame enum order and the records inside
        // FadeSpriteBatchEffect.xnb (the offline-compiled reference).
        expect(attributes).toContainEqual({ name: 'aPosition0', usage: 0, index: 0, location: 0 });
        expect(attributes).toContainEqual({ name: 'aColor0',    usage: 1, index: 0, location: 1 });
        expect(attributes).toContainEqual({ name: 'aTexCoord0', usage: 2, index: 0, location: 2 });

        // mul(input.Position, MatrixTransform) → dot-product expansion.
        expect(glsl).toMatch(/dot\([^,]+,\s*MatrixTransform\[0\]\)/);
        expect(glsl).toMatch(/dot\([^,]+,\s*MatrixTransform\[1\]\)/);
        expect(glsl).toMatch(/dot\([^,]+,\s*MatrixTransform\[2\]\)/);
        expect(glsl).toMatch(/dot\([^,]+,\s*MatrixTransform\[3\]\)/);
        // No `mul(` calls survive translation.
        expect(glsl).not.toMatch(/\bmul\s*\(/);

        // Trampoline: gl_Position from SV_POSITION field; varyings from
        // the rest; posFixup tail.
        expect(glsl).toMatch(/gl_Position\s*=\s*_vs_out\.Position/);
        expect(glsl).toMatch(/vFrontColor\s*=\s*_vs_out\.Color/);
        // TextureCoordinates is float2 → widened to vec4 for the varying.
        expect(glsl).toMatch(/vTexCoord0\s*=\s*vec4\(_vs_out\.TextureCoordinates,\s*0\.0,\s*0\.0\)/);
        expect(glsl).toMatch(/gl_Position\.y\s*=\s*gl_Position\.y\s*\*\s*posFixup\.y/);
    });

    it('expands mul() across all four matrix rows even with non-trivial first arguments', () => {
        const src = `cbuffer G { float4x4 M; };
float4 V(float4 p : POSITION0) : SV_POSITION { return mul(p * 2.0, M); }`;
        const { glsl } = translateHlslToGlsl({
            source: src,
            entrypoint: 'V',
            stage: 'vertex',
            cbuffers: [{
                name: 'G',
                sizeInBytes: 64,
                sourceStart: 0,
                sourceEnd: 0,
                fields: [{ typeName: 'float4x4', name: 'M', offsetBytes: 0, sizeBytes: 64, rows: 4, columns: 4, arraySize: 0 }],
            }],
        });
        // First arg is preserved verbatim (paren-balanced scan, not greedy regex).
        expect(glsl).toMatch(/dot\(p \* 2\.0, M\[0\]\)/);
        expect(glsl).toMatch(/dot\(p \* 2\.0, M\[3\]\)/);
    });

    it('strips the OTHER entry function\'s return semantic so the VS stage parses cleanly even when MainPS is in the body', () => {
        // A VS+PS .fx — when we compile the VS stage, the whole body still
        // contains `float4 MainPS(...) : COLOR { … }`. The `: COLOR` would
        // be left dangling without a global semantic-strip pass, producing
        // "unexpected COLON" at GLSL parse time.
        const src = `struct VSO { float4 Position : SV_POSITION; float2 UV : TEXCOORD0; };
VSO V(float4 p : POSITION0) { VSO o; o.Position = p; o.UV = p.xy; return o; }
float4 P(VSO i) : COLOR { return float4(i.UV, 0, 1); }`;
        const vs = translateHlslToGlsl({
            source: src, entrypoint: 'V', stage: 'vertex', cbuffers: [],
        });
        // MainPS's `: COLOR` should be gone from the VS output too.
        expect(vs.glsl).not.toMatch(/\)\s*:\s*COLOR\s*\{/);
        // And the VS stage should have NO diagnostics about it.
        expect(vs.diagnostics.filter(d => d.severity === 'error')).toEqual([]);
    });

    it('leaves vector-only cbuffers and `mul(scalar, scalar)` style calls untouched', () => {
        const src = `cbuffer ps { float4 Tint; };
float4 P(float2 uv : TEXCOORD0) : SV_TARGET {
    return tex2D(SpriteTextureSampler, uv) * Tint;
}`;
        const { glsl } = translateHlslToGlsl({
            source: src,
            entrypoint: 'P',
            stage: 'pixel',
            cbuffers: [{
                name: 'ps',
                sizeInBytes: 16,
                sourceStart: 0,
                sourceEnd: 0,
                fields: [{ typeName: 'float4', name: 'Tint', offsetBytes: 0, sizeBytes: 16, rows: 1, columns: 4, arraySize: 0 }],
            }],
        });
        // No mul() expansion — Tint isn't a matrix.
        expect(glsl).not.toMatch(/dot\([^,]+,\s*Tint\[/);
    });
});
