import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { compileFxToXnb } from './compile-fx';
import {
    setShaderCompilerFactory,
    type ShaderCompiler,
    type CompileHlslOptions,
    type CompiledShaderEntry,
} from './shader-compiler';
import { classifyXnb } from '../xnb/xnb-reader';
import { parseEffect } from '../xnb/mgfx';

// Stub compiler — pretends every entrypoint compiled to a tiny GLSL string
// and reports a single cbuffer + sampler matching the SpriteBatch shape.
// Lets us drive the FX→MGFX→XNB plumbing end-to-end without WASM.
function makeStubCompiler(): ShaderCompiler {
    return {
        async compileHlsl(opts: CompileHlslOptions): Promise<CompiledShaderEntry> {
            const cbufferName = opts.stage === 'vertex' ? 'vs_uniforms_vec4' : 'ps_uniforms_vec4';
            return {
                // Echo the original source into the stub output so two
                // distinct inputs produce distinct bytecode (and therefore
                // distinct effectKeys). The shape of the GLSL is otherwise
                // ignored by tests — they only inspect the MGFX-side metadata.
                glslSource:
                    `// stub ${opts.stage} ${opts.entrypoint}\n` +
                    `// source-fingerprint:\n${opts.source}\n` +
                    `void main() {}\n`,
                samplers: opts.stage === 'pixel'
                    ? [{ name: 'SpriteTexture', binding: 0, samplerSlot: 0, samplerType: 0 }]
                    : [],
                attributes: opts.stage === 'vertex'
                    ? [{ name: 'a_position', location: 0, usage: 0, index: 0 }]
                    : [],
                cbuffers: [
                    {
                        name: cbufferName,
                        sizeInBytes: 32,
                        fields: [
                            { name: 'Time', offsetBytes: 0,  sizeBytes: 4 },
                            { name: 'Tint', offsetBytes: 16, sizeBytes: 16 },
                        ],
                    },
                ],
                diagnostics: [],
            };
        },
    };
}

beforeEach(() => {
    setShaderCompilerFactory(async () => makeStubCompiler());
});
afterEach(() => {
    // Reset to the default (throws) factory so unrelated tests don't pick up our stub.
    setShaderCompilerFactory(async () => ({
        compileHlsl: async () => { throw new Error('default not-available'); },
    }));
});

const SAMPLE = `
cbuffer ps_uniforms_vec4 { float Time; float4 Tint; };
Texture2D SpriteTexture;
SamplerState SpriteTextureSampler;

float4 MainPS() : SV_TARGET { return float4(1,1,1,1); }
float4 MainVS() : SV_POSITION { return float4(0,0,0,1); }

technique T {
    pass P {
        VertexShader = compile vs_4_0 MainVS();
        PixelShader  = compile ps_4_0 MainPS();
    }
}
`;

describe('compileFxToXnb — end-to-end with stub compiler', () => {
    it('emits a valid XNB header recognized as an effect', async () => {
        const { xnb } = await compileFxToXnb({ source: SAMPLE, assetName: 'test' });
        expect(xnb[0]).toBe(0x58); expect(xnb[1]).toBe(0x4E); expect(xnb[2]).toBe(0x42); // 'XNB'
        const cls = classifyXnb(xnb);
        expect(cls.kind).toBe('effect');
        expect(cls.rootReader?.shortName).toBe('EffectReader');
        expect(cls.parseError).toBeUndefined();
    });

    it('emits parseable MGFX v10 with two shaders (VS+PS) and one technique', async () => {
        const { xnb } = await compileFxToXnb({ source: SAMPLE });
        const cls = classifyXnb(xnb);
        expect(cls.objectData).toBeTruthy();
        const eff = parseEffect(cls.objectData!);
        expect(eff.version).toBe(10);
        expect(eff.profileId).toBe(0);
        expect(eff.shaders).toHaveLength(2);
        const vs = eff.shaders.find(s => s.isVertexShader);
        const ps = eff.shaders.find(s => !s.isVertexShader);
        expect(vs).toBeDefined();
        expect(ps).toBeDefined();

        expect(eff.techniques).toHaveLength(1);
        expect(eff.techniques[0].name).toBe('T');
        expect(eff.techniques[0].passes).toHaveLength(1);
        const pass = eff.techniques[0].passes[0];
        // Both stages bound — exact indices depend on iteration order, but
        // both must be >= 0 and distinct.
        expect(pass.vsShaderIndex).toBeGreaterThanOrEqual(0);
        expect(pass.psShaderIndex).toBeGreaterThanOrEqual(0);
        expect(pass.vsShaderIndex).not.toBe(pass.psShaderIndex);
    });

    it('exposes the pixel shader sampler in the MGFX shader record', async () => {
        const { xnb } = await compileFxToXnb({ source: SAMPLE });
        const eff = parseEffect(classifyXnb(xnb).objectData!);
        const ps = eff.shaders.find(s => !s.isVertexShader)!;
        expect(ps.samplers).toHaveLength(1);
        expect(ps.samplers[0].name).toBe('SpriteTexture');
    });

    it('writes both cbuffers (vs + ps) at the MGFX level', async () => {
        const { xnb } = await compileFxToXnb({ source: SAMPLE });
        const eff = parseEffect(classifyXnb(xnb).objectData!);
        const cbufNames = eff.constantBuffers.map(c => c.name).sort();
        expect(cbufNames).toEqual(['ps_uniforms_vec4', 'vs_uniforms_vec4']);
        for (const cb of eff.constantBuffers) {
            expect(cb.params).toHaveLength(2);   // Time + Tint
        }
    });

    it('emits MGFX cbuffer + parameter records from HLSL cbuffer blocks (FX-side wins)', async () => {
        const src = `
cbuffer ps_uniforms_vec4 {
    float4 Tint;
    float  Time;
};

uniform sampler2D ps_s0;
varying vec4 vTexCoord0;
void main() { gl_FragColor = texture2D(ps_s0, vTexCoord0.xy) * vec4(1.0); }

technique T { pass P { PixelShader = compile ps_4_0 main(); } }`;
        const { xnb } = await compileFxToXnb({ source: src });
        const eff = parseEffect(classifyXnb(xnb).objectData!);

        // The FX-declared cbuffer should win over whatever the stub compiler
        // claims about cbuffers (the stub fabricates `ps_uniforms_vec4` too —
        // ensure the FX-side names + offsets are the ones in the output).
        const cbuf = eff.constantBuffers.find(c => c.name === 'ps_uniforms_vec4');
        expect(cbuf).toBeDefined();
        expect(cbuf!.sizeInBytes).toBe(32);  // 16 (Tint) + 4 (Time) aligned to 32
        expect(cbuf!.params).toHaveLength(2);

        // Walk param indices → top-level params and confirm the names.
        const paramNames = cbuf!.params.map(p => eff.parameters[p.paramIdx].name);
        expect(paramNames).toEqual(['Tint', 'Time']);

        // Offsets match HLSL packing.
        expect(cbuf!.params[0].offset).toBe(0);
        expect(cbuf!.params[1].offset).toBe(16);
    });

    it('emits the right EffectParameterClass per field shape (Scalar/Vector/Matrix/Object)', async () => {
        // Verified against the working ScreenEffect.xnb via probe-effect-params.mjs:
        //   float            → class=Scalar(0)
        //   float4 (vector)  → class=Vector(1)
        //   float4x4 (matrix)→ class=Matrix(2)
        //   Texture2D        → class=Object(3)
        //
        // Wrong class triggers InvalidCastException inside KNI's
        // EffectParameter.SetValue() — the runtime's first guard is
        // `ParameterClass != <expected>`.
        const src = `
cbuffer cb {
    float    Time;
    float4   Tint;
    float4x4 World;
};
uniform sampler2D ps_s0;
void main() { gl_FragColor = texture2D(ps_s0, vec2(0.0)); }
technique T { pass P { PixelShader = compile ps_4_0 main(); } }`;
        const { xnb } = await compileFxToXnb({ source: src });
        const eff = parseEffect(classifyXnb(xnb).objectData!);

        const byName = (n: string) => eff.parameters.find(p => p.name === n);
        expect(byName('Time')?.class_).toBe(0);   // Scalar
        expect(byName('Tint')?.class_).toBe(1);   // Vector
        expect(byName('World')?.class_).toBe(2);  // Matrix
        // Texture name comes from the stub compiler in beforeEach above
        // (it claims one sampler named 'SpriteTexture' on pixel stage).
        // What matters here is the class — Object(3) — not the name.
        expect(byName('SpriteTexture')?.class_).toBe(3);
    });

    it('emits a content-derived effectKey so KNI does not share GL programs across distinct shaders', async () => {
        // Regression test for the staleness bug: when effectKey is constant
        // (e.g. always 0), KNI's BlazorGL caches the compiled GL shader
        // program by that key and reuses it for every subsequently-loaded
        // Effect — so editing the .fx silently keeps the original program.
        // ScreenEffect.xnb in the working reference uses a non-zero hash
        // (probe-effect-params.mjs reports 0xe14f7f64).
        const srcA = `void main() { gl_FragColor = vec4(1.0, 0.0, 0.0, 1.0); }
technique T { pass P { PixelShader = compile ps_4_0 main(); } }`;
        const srcB = `void main() { gl_FragColor = vec4(0.0, 1.0, 0.0, 1.0); }
technique T { pass P { PixelShader = compile ps_4_0 main(); } }`;

        const a = parseEffect(classifyXnb((await compileFxToXnb({ source: srcA })).xnb).objectData!);
        const b = parseEffect(classifyXnb((await compileFxToXnb({ source: srcB })).xnb).objectData!);

        expect(a.effectKey).not.toBe(0);
        expect(b.effectKey).not.toBe(0);
        expect(a.effectKey).not.toBe(b.effectKey);
    });

    it('effectKey is deterministic — same source always hashes to the same value', async () => {
        const src = `void main() { gl_FragColor = vec4(0.5); }
technique T { pass P { PixelShader = compile ps_4_0 main(); } }`;
        const a = parseEffect(classifyXnb((await compileFxToXnb({ source: src })).xnb).objectData!);
        const b = parseEffect(classifyXnb((await compileFxToXnb({ source: src })).xnb).objectData!);
        expect(a.effectKey).toBe(b.effectKey);
    });

    it('emits cbufferRefs on shader records so KNI uploads cbuffer data on Pass.Apply()', async () => {
        // Regression test for the "Tint reads as 0 in GLSL" bug: when the
        // shader record's cbufferRefs is empty, KNI never uploads the
        // cbuffer to the GLSL uniform array, so `Effect.Parameters[X].SetValue`
        // writes data into the MGFX-side buffer but never reaches GPU memory.
        // Symptom: shader runs but every uniform reads as zero.
        const src = `
cbuffer ps_uniforms_vec4 {
    float4 Tint;
};
uniform sampler2D ps_s0;
void main() { gl_FragColor = vec4(1.0); }
technique T { pass P { PixelShader = compile ps_4_0 main(); } }`;
        const { xnb } = await compileFxToXnb({ source: src });
        const eff = parseEffect(classifyXnb(xnb).objectData!);

        // Locate the ps_uniforms_vec4 cbuffer's index.
        const cbufIdx = eff.constantBuffers.findIndex(c => c.name === 'ps_uniforms_vec4');
        expect(cbufIdx).toBeGreaterThanOrEqual(0);

        // Every shader record should reference it.
        for (const s of eff.shaders) {
            expect(s.cbufferRefs).toContain(cbufIdx);
        }
    });

    it('makes "Tint" a top-level effect parameter resolvable by Effect.Parameters lookup', async () => {
        const src = `
cbuffer ps_uniforms_vec4 { float4 Tint; };
uniform sampler2D ps_s0;
void main() { gl_FragColor = texture2D(ps_s0, vec2(0.0)); }
technique T { pass P { PixelShader = compile ps_4_0 main(); } }`;
        const { xnb } = await compileFxToXnb({ source: src });
        const eff = parseEffect(classifyXnb(xnb).objectData!);

        // "Tint" should appear by name in the top-level parameter list — that's
        // what RenderCommands.cs SetEffectParameter_Float4 will look up.
        const tint = eff.parameters.find(p => p.name === 'Tint');
        expect(tint).toBeDefined();
        expect(tint!.rows).toBe(1);
        expect(tint!.columns).toBe(4);
        // 16 bytes of zero-filled initial data (rows*columns*4).
        expect(tint!.data).toBeDefined();
        expect(tint!.data!.length).toBe(16);
    });

    it('auto-injects a default VS into PS-only passes (MonoGame.Effect.Compiler-equivalent fold-in)', async () => {
        // No VertexShader assigned in the pass → compile-fx should inject the
        // synthetic default VS matching FadeSpriteBatchEffect's compiled VS.
        const src = `float4 P() : SV_TARGET { return 0; }
technique T {
    pass A { PixelShader = compile ps_4_0 P(); }
}`;
        const { xnb } = await compileFxToXnb({ source: src });
        const eff = parseEffect(classifyXnb(xnb).objectData!);

        // Two shaders: user PS + injected VS.
        expect(eff.shaders).toHaveLength(2);
        const vs = eff.shaders.find(s => s.isVertexShader)!;
        const ps = eff.shaders.find(s => !s.isVertexShader)!;

        // Pass binds both — no -1 sentinel.
        const pass = eff.techniques[0].passes[0];
        expect(pass.vsShaderIndex).toBeGreaterThanOrEqual(0);
        expect(pass.psShaderIndex).toBeGreaterThanOrEqual(0);
        expect(eff.shaders[pass.vsShaderIndex]).toBe(vs);
        expect(eff.shaders[pass.psShaderIndex]).toBe(ps);

        // Injected VS bytecode is the FadeSpriteBatchEffect-equivalent.
        const vsSrc = new TextDecoder().decode(vs.bytecode);
        expect(vsSrc).toMatch(/uniform vec4 vs_uniforms_vec4\[4\];/);
        expect(vsSrc).toMatch(/uniform vec4 posFixup;/);
        expect(vsSrc).toMatch(/attribute vec4 vs_v0;/);
        expect(vsSrc).toMatch(/attribute vec4 vs_v1;/);
        expect(vsSrc).toMatch(/attribute vec4 vs_v2;/);
        expect(vsSrc).toMatch(/attribute vec4 vs_v3;/);
        expect(vsSrc).toMatch(/vs_o0\.x = dot\(vs_v0, vs_c0\);/);

        // 4 attributes (POSITION/COLOR/TEXCOORD0/TEXCOORD1).
        expect(vs.attributes).toHaveLength(4);
        const posAttr = vs.attributes.find(a => a.name === 'vs_v0')!;
        expect(posAttr.usage).toBe(0);       // VertexElementUsage.Position
        const colorAttr = vs.attributes.find(a => a.name === 'vs_v1')!;
        expect(colorAttr.usage).toBe(1);     // VertexElementUsage.Color
        const tc0Attr = vs.attributes.find(a => a.name === 'vs_v2')!;
        expect(tc0Attr.usage).toBe(2);       // VertexElementUsage.TextureCoordinate
        expect(tc0Attr.index).toBe(0);
        const tc1Attr = vs.attributes.find(a => a.name === 'vs_v3')!;
        expect(tc1Attr.usage).toBe(2);
        expect(tc1Attr.index).toBe(1);

        // MatrixTransform parameter + vs_uniforms_vec4 cbuffer wired up.
        const mtParam = eff.parameters.find(p => p.name === 'MatrixTransform')!;
        expect(mtParam).toBeDefined();
        expect(mtParam.class_).toBe(2);      // EffectParameterClass.Matrix
        expect(mtParam.rows).toBe(4);
        expect(mtParam.columns).toBe(4);
        const cb = eff.constantBuffers.find(cb => cb.name === 'vs_uniforms_vec4')!;
        expect(cb).toBeDefined();
        expect(cb.sizeInBytes).toBe(64);
        expect(cb.params).toHaveLength(1);
        expect(cb.params[0].offset).toBe(0);
    });

    it('does NOT inject a default VS when the pass already supplies one', async () => {
        const src = `float4 V() : SV_POSITION { return 0; }
float4 P() : SV_TARGET   { return 0; }
technique T {
    pass A { VertexShader = compile vs_4_0 V(); PixelShader = compile ps_4_0 P(); }
}`;
        const { xnb } = await compileFxToXnb({ source: src });
        const eff = parseEffect(classifyXnb(xnb).objectData!);
        // Exactly 2 shaders (the user's V and P) — no extra injected VS.
        // The `MatrixTransform` parameter is the unambiguous injection
        // marker; the cbuffer name `vs_uniforms_vec4` is what the stub
        // compiler itself emits for any VS, so we don't assert on it here.
        expect(eff.shaders).toHaveLength(2);
        expect(eff.parameters.find(p => p.name === 'MatrixTransform')).toBeUndefined();
    });

    it('reuses a single shader record when the same entrypoint is used by two passes', async () => {
        const src = `
float4 V() : SV_POSITION { return 0; }
float4 P() : SV_TARGET   { return 0; }
technique T {
    pass A { VertexShader = compile vs_4_0 V(); PixelShader = compile ps_4_0 P(); }
    pass B { VertexShader = compile vs_4_0 V(); PixelShader = compile ps_4_0 P(); }
}`;
        const { xnb } = await compileFxToXnb({ source: src });
        const eff = parseEffect(classifyXnb(xnb).objectData!);
        // Two shaders total (one VS + one PS), each referenced by two passes.
        expect(eff.shaders).toHaveLength(2);
        expect(eff.techniques[0].passes).toHaveLength(2);
        const [pA, pB] = eff.techniques[0].passes;
        expect(pA.vsShaderIndex).toBe(pB.vsShaderIndex);
        expect(pA.psShaderIndex).toBe(pB.psShaderIndex);
    });
});
