// Stock MonoGame `.fx` shaders we want the playground to handle as
// drop-in replacements. Each fixture is the exact source you'd find
// in a MonoGame project — the SpriteEffect.fx that ships with the
// Content Pipeline templates, plus a few canonical add-ons (tint,
// posterize, gaussian blur) that appear in tutorials.
//
// Tests exercise each through the full pipeline:
//   parseFx → translateHlslToGlsl → transformEs100ToEs310ForValidation
//
// Assertions verify:
//   - Translation produces valid GLSL ES 1.00 shape (varying, gl_FragColor,
//     texture2D, plain uniform decls)
//   - Validation transform produces SPIR-V-strict ES 3.10 (#version 310 es,
//     layout(binding=), layout(location=), no varying/gl_FragColor/texture2D)
//   - Specific shapes that MonoGame's runtime expects (effectKey content
//     hash, cbuffer parameter names, sampler reflection)

// SpriteEffect.fx — the canonical MonoGame stock shader. Every MonoGame
// project using SpriteBatch starts with this (or an edited copy). Tests
// must pass for this exact source; if they don't, drop-in compat is broken.
export const SPRITE_EFFECT_FX = `#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;

sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR
{
    return tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};`;

// Tint screen-effect — exact shape the playground's user has been writing.
// Exercises the cbuffer extraction + sampler_state literal handling +
// .Sample() (DX10 form) instead of tex2D().
export const TINT_EFFECT_FX = `cbuffer ps_uniforms_vec4
{
    float4 Tint;
};

Texture2D ps_s0;
SamplerState ps_s0_sampler;

float4 MainPS(float2 uv : TEXCOORD0) : SV_TARGET
{
    float4 sampled = ps_s0.Sample(ps_s0_sampler, uv);
    return sampled * Tint;
}

technique TintEffect
{
    pass P0
    {
        PixelShader = compile ps_4_0 MainPS();
    }
}`;

// Grayscale conversion — adds a math-heavy intrinsic call (dot product
// with a luminance vector). Exercises that intrinsic translation
// preserves the math correctly through the ES 1.00 → ES 3.10 transform.
export const GRAYSCALE_FX = `#if OPENGL
    #define PS_SHADERMODEL ps_3_0
#else
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state { Texture = <SpriteTexture>; };

struct VS_OUT
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VS_OUT input) : COLOR
{
    float4 c = tex2D(SpriteTextureSampler, input.TextureCoordinates);
    float gray = dot(c.rgb, float3(0.299, 0.587, 0.114));
    return float4(gray, gray, gray, c.a) * input.Color;
}

technique Grayscale
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}`;

// Posterize — exercises saturate() and floor() intrinsics, plus a
// constant-buffer parameter the user would tweak via `set effect param`.
export const POSTERIZE_FX = `cbuffer Settings
{
    float Levels;
};

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state { Texture = <SpriteTexture>; };

struct VS_OUT
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VS_OUT input) : COLOR
{
    float4 c = tex2D(SpriteTextureSampler, input.TextureCoordinates);
    float L = max(Levels, 1.0);
    c.rgb = floor(saturate(c.rgb) * L) / L;
    return c * input.Color;
}

technique Posterize
{
    pass P0
    {
        PixelShader = compile ps_4_0 MainPS();
    }
}`;

// Vignette — exercises length() distance computation + lerp/mix.
// Has TWO cbuffer fields of different types.
export const VIGNETTE_FX = `cbuffer Settings
{
    float Radius;
    float Softness;
};

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state { Texture = <SpriteTexture>; };

struct VS_OUT
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VS_OUT input) : COLOR
{
    float4 c = tex2D(SpriteTextureSampler, input.TextureCoordinates);
    float2 centered = input.TextureCoordinates - float2(0.5, 0.5);
    float d = length(centered);
    float vig = smoothstep(Radius + Softness, Radius - Softness, d);
    return float4(c.rgb * vig, c.a) * input.Color;
}

technique Vignette
{
    pass P0
    {
        PixelShader = compile ps_4_0 MainPS();
    }
}`;

// All-in-one — useful for iterating over every fixture in test suites.
export const ALL_STOCK_FIXTURES: Array<{ name: string; source: string; entry: string; technique: string }> = [
    { name: 'SpriteEffect', source: SPRITE_EFFECT_FX, entry: 'MainPS', technique: 'SpriteDrawing' },
    { name: 'Tint',         source: TINT_EFFECT_FX,   entry: 'MainPS', technique: 'TintEffect' },
    { name: 'Grayscale',    source: GRAYSCALE_FX,     entry: 'MainPS', technique: 'Grayscale' },
    { name: 'Posterize',    source: POSTERIZE_FX,     entry: 'MainPS', technique: 'Posterize' },
    { name: 'Vignette',     source: VIGNETTE_FX,      entry: 'MainPS', technique: 'Vignette' },
];
