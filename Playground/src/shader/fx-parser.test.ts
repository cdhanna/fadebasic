import { describe, it, expect } from 'vitest';
import { parseFx } from './fx-parser';

const SPRITE_EFFECT_SAMPLE = `
// Tiny SpriteBatch-style effect used for SpriteEffect parity testing.

cbuffer ps_uniforms_vec4 : register(b0)
{
    float Time;
    float4 Tint;
};

Texture2D SpriteTexture : register(t0);
SamplerState SpriteTextureSampler : register(s0);

struct VS_INPUT {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

float4 MainPS(VS_INPUT input) : SV_TARGET
{
    return SpriteTexture.Sample(SpriteTextureSampler, input.uv) * Tint;
}

technique SpriteBatch
{
    pass P0
    {
        PixelShader = compile ps_4_0 MainPS();
    }
}
`;

const TWO_TECHNIQUE_SAMPLE = `
float4 V() : SV_POSITION { return 0; }
float4 P() : SV_TARGET   { return 0; }

technique Default
{
    pass A { VertexShader = compile vs_4_0 V(); PixelShader = compile ps_4_0 P(); }
}

technique11 Fancy
{
    pass A { VertexShader = compile vs_4_0 V(); PixelShader = compile ps_4_0 P(); }
}
`;

describe('parseFx — technique/pass extraction', () => {
    it('finds one technique with one pass binding a pixel shader', () => {
        const out = parseFx(SPRITE_EFFECT_SAMPLE);
        expect(out.warnings).toEqual([]);
        expect(out.techniques).toHaveLength(1);
        const t = out.techniques[0];
        expect(t.name).toBe('SpriteBatch');
        expect(t.passes).toHaveLength(1);
        expect(t.passes[0].name).toBe('P0');
        const assigns = t.passes[0].assigns;
        expect(assigns).toHaveLength(1);
        expect(assigns[0]).toMatchObject({
            name: 'PixelShader',
            kind: 'shader',
            profile: 'ps_4_0',
            entrypoint: 'MainPS',
        });
    });

    it('handles multiple techniques including technique11', () => {
        const out = parseFx(TWO_TECHNIQUE_SAMPLE);
        expect(out.warnings).toEqual([]);
        expect(out.techniques).toHaveLength(2);
        expect(out.techniques[0].name).toBe('Default');
        expect(out.techniques[0].techniqueLevel).toBe(9);
        expect(out.techniques[1].name).toBe('Fancy');
        expect(out.techniques[1].techniqueLevel).toBe(11);
    });

    it('whitespaces out the technique + cbuffer blocks from hlslStripped while keeping length', () => {
        const out = parseFx(SPRITE_EFFECT_SAMPLE);
        expect(out.hlslStripped.length).toBe(SPRITE_EFFECT_SAMPLE.length);
        // Texture2D / struct / function-body declarations remain — those
        // are real HLSL that glslang (or the GLSL passthrough) processes.
        expect(out.hlslStripped).toContain('Texture2D SpriteTexture');
        expect(out.hlslStripped).toContain('float4 MainPS');
        // The technique + cbuffer blocks are gone (replaced by spaces) —
        // both are FX framing, not HLSL.
        expect(out.hlslStripped).not.toContain('technique');
        expect(out.hlslStripped).not.toContain('pass P0');
        expect(out.hlslStripped).not.toContain('cbuffer');
    });

    it('preserves line numbers so glslang diagnostics point at the original source', () => {
        const out = parseFx(SPRITE_EFFECT_SAMPLE);
        const origLines = SPRITE_EFFECT_SAMPLE.split('\n');
        const strippedLines = out.hlslStripped.split('\n');
        expect(strippedLines.length).toBe(origLines.length);
    });
});

describe('parseFx — sampler_state literal extraction', () => {
    it('parses a legacy sampler2D = sampler_state literal and strips it', () => {
        const src = `
Texture2D Tex;
sampler2D Smp = sampler_state {
    Texture = <Tex>;
    MinFilter = Linear;
    MagFilter = Linear;
};

technique T { pass P { PixelShader = compile ps_4_0 X(); } }
`;
        const out = parseFx(src);
        expect(out.samplerStateLiterals).toHaveLength(1);
        const lit = out.samplerStateLiterals[0];
        expect(lit.samplerName).toBe('Smp');
        expect(lit.samplerType).toBe('sampler2D');
        expect(lit.textureRef).toBe('Tex');
        expect(lit.assigns.length).toBeGreaterThan(0);
        expect(out.hlslStripped).not.toContain('sampler_state');
    });

    it('does NOT mistake a bare HLSL sampler2D declaration for a literal', () => {
        const src = `
sampler2D Smp;
technique T { pass P { PixelShader = compile ps_4_0 X(); } }
`;
        const out = parseFx(src);
        expect(out.samplerStateLiterals).toHaveLength(0);
        expect(out.hlslStripped).toContain('sampler2D Smp');
    });
});

describe('parseFx — pass state assignments', () => {
    it('classifies state-ref vs state-inline alongside shader assigns', () => {
        const src = `
BlendState MyBlend;
technique T {
    pass P {
        VertexShader = compile vs_4_0 V();
        PixelShader  = compile ps_4_0 P();
        BlendState = MyBlend;
        AlphaBlendEnable = true;
    }
}
`;
        const out = parseFx(src);
        const assigns = out.techniques[0].passes[0].assigns;
        expect(assigns.find(a => a.name === 'VertexShader')?.kind).toBe('shader');
        expect(assigns.find(a => a.name === 'PixelShader')?.kind).toBe('shader');
        expect(assigns.find(a => a.name === 'BlendState')).toMatchObject({
            kind: 'state-ref',
            refTarget: 'MyBlend',
        });
        expect(assigns.find(a => a.name === 'AlphaBlendEnable')).toMatchObject({
            kind: 'state-inline',
            rawValue: 'true',
        });
    });
});

describe('parseFx — cbuffer declarations', () => {
    it('extracts a single cbuffer with one float4 field at offset 0', () => {
        const src = `
cbuffer ps_uniforms_vec4 {
    float4 Tint;
};
technique T { pass P { PixelShader = compile ps_4_0 main(); } }`;
        const out = parseFx(src);
        expect(out.warnings).toEqual([]);
        expect(out.cbuffers).toHaveLength(1);
        const cb = out.cbuffers[0];
        expect(cb.name).toBe('ps_uniforms_vec4');
        expect(cb.fields).toHaveLength(1);
        expect(cb.fields[0]).toMatchObject({
            typeName: 'float4',
            name: 'Tint',
            rows: 1,
            columns: 4,
            offsetBytes: 0,
            sizeBytes: 16,
        });
        expect(cb.sizeInBytes).toBe(16);
    });

    it('aligns mixed-type fields per HLSL packing rules', () => {
        const src = `
cbuffer Mixed {
    float  Time;
    float4 Tint;
    float2 Scale;
};
technique T { pass P { PixelShader = compile ps_4_0 main(); } }`;
        const out = parseFx(src);
        const cb = out.cbuffers[0];
        expect(cb.fields).toHaveLength(3);
        // Time: offset 0, size 4
        // Tint: rounded up to next 16-byte boundary → offset 16, size 16
        // Scale: at 32 (next-after-Tint), float2 needs 8-byte alignment → 32
        expect(cb.fields[0]).toMatchObject({ name: 'Time',  offsetBytes:  0, sizeBytes:  4 });
        expect(cb.fields[1]).toMatchObject({ name: 'Tint',  offsetBytes: 16, sizeBytes: 16 });
        expect(cb.fields[2]).toMatchObject({ name: 'Scale', offsetBytes: 32, sizeBytes:  8 });
        expect(cb.sizeInBytes).toBe(48);  // 32 + 8 = 40, padded to 16-byte boundary = 48
    });

    it('whitespaces the cbuffer block out of hlslStripped', () => {
        const src = `
cbuffer ps_uniforms_vec4 { float4 Tint; };

uniform sampler2D ps_s0;
void main() { gl_FragColor = vec4(1.0); }

technique T { pass P { PixelShader = compile ps_4_0 main(); } }`;
        const out = parseFx(src);
        expect(out.hlslStripped).not.toContain('cbuffer');
        expect(out.hlslStripped).not.toContain('float4 Tint');
        // But the actual GLSL stays.
        expect(out.hlslStripped).toContain('uniform sampler2D ps_s0;');
        expect(out.hlslStripped).toContain('void main()');
    });

    it('handles the register(bN) annotation', () => {
        const src = `
cbuffer ps_uniforms_vec4 : register(b0) { float4 Tint; };
technique T { pass P { PixelShader = compile ps_4_0 main(); } }`;
        const out = parseFx(src);
        expect(out.warnings).toEqual([]);
        expect(out.cbuffers).toHaveLength(1);
        expect(out.cbuffers[0].name).toBe('ps_uniforms_vec4');
    });

    it('preserves line numbers when stripping cbuffer blocks', () => {
        const src = `// line 1
cbuffer X { float4 Y; };
// line 3 — stays
`;
        const out = parseFx(src);
        expect(out.hlslStripped.split('\n').length).toBe(src.split('\n').length);
    });

    it('warns on unknown type and continues parsing the rest', () => {
        const src = `
cbuffer Mixed {
    weirdtype Foo;
    float4 Bar;
};`;
        const out = parseFx(src);
        expect(out.warnings.length).toBeGreaterThan(0);
        expect(out.warnings[0].message).toMatch(/Unknown HLSL type 'weirdtype'/);
        // 'Bar' still gets through.
        expect(out.cbuffers[0].fields).toHaveLength(1);
        expect(out.cbuffers[0].fields[0].name).toBe('Bar');
    });
});

describe('parseFx — robustness', () => {
    it('ignores `technique` mentions inside comments', () => {
        const src = `
// This is the only technique we care about — see the docstring.
/* technique InsideBlockComment { pass X { } } */
"technique InsideString { pass X { } }"
technique RealTechnique { pass P { PixelShader = compile ps_4_0 X(); } }
`;
        const out = parseFx(src);
        expect(out.techniques).toHaveLength(1);
        expect(out.techniques[0].name).toBe('RealTechnique');
    });

    it('returns warnings (not exceptions) on a malformed technique', () => {
        const src = `
technique Broken {
    pass P {
        PixelShader = compile ps_4_0 X();
    `;  // intentional: missing closing braces
        const out = parseFx(src);
        expect(out.warnings.length).toBeGreaterThan(0);
        expect(out.techniques).toHaveLength(0);
    });
});
