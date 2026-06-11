// Shared types for the playground's PNG/JPG → XNB content builder.
//
// `TextureCompression` mirrors the C# enum at
// Fade.MonoGame.Contracts/ContentEntry.cs and the parsed values of the
// `texture compression` / `default texture compression` macros in
// Fade.MonoGame.Lib/AssetMacros.cs. Keep them in lock-step.

export type TextureCompression =
    | 'auto'
    | 'none'
    | 'color'
    | 'dxt1'
    | 'dxt3'
    | 'dxt5'
    | 'alpha8'
    | 'bgra4444';

export const DEFAULT_TEXTURE_COMPRESSION: TextureCompression = 'auto';

// Audio compression options accepted by the `sound compression` macro.
// Today the encoder only emits `pcm`; `adpcm` is reserved for the next
// pass and falls back to PCM with a diagnostic.
export type AudioCompression =
    | 'auto'
    | 'pcm'
    | 'adpcm';

export const DEFAULT_AUDIO_COMPRESSION: AudioCompression = 'auto';

// Bumped whenever the encoder output bytes change for a fixed input — a
// pixel-format tweak, a header field correction, etc. Cache entries
// stamped with an older version are treated as misses and re-encoded.
// Bump aggressively; encodes are cheap relative to subtle staleness bugs.
//   v1: initial (uncompressed, but with reversed channels — R and B swapped).
//   v2: switched type-reader string from XNA's assembly-qualified form
//       to the bare `Microsoft.Xna.Framework.Content.Texture2DReader`
//       that KNI accepts.
//   v3: writes RGBA pixels straight through. SurfaceFormat.Color is
//       packed as r|(g<<8)|(b<<16)|(a<<24) — RGBA in little-endian
//       memory — and getImageData hands us the same byte order. The v1/v2
//       BGRA swap made R and B trade places (yellow displays as blue).
//   v4: real BC1 / BC3 / Alpha8 / Bgra4444 encoders. Prior versions
//       silently downgraded every non-color request to Color, so any
//       cached XNB stamped v1–v3 with format=dxt5 is actually Color
//       under the hood; a re-encode at v4 produces the real format.
//   v5: real MS-ADPCM (wFormatTag=2) audio encoder. Prior versions
//       silently fell back to PCM whenever adpcm or `auto`-with-a-long-
//       clip was requested; v5 actually emits the compressed format.
//   v6: SoundEffect XNB tail fix. KNI's SoundEffectReader consumes
//       THREE trailing int32s (loopStart, loopLength, durationMs); we
//       were writing only two, which made every audio asset crash with
//       EndOfStreamException at load time. v6 includes durationMs.
//   v7: ADPCM predictor truncation fix + `auto` defaults to PCM. The
//       arithmetic right-shift in encodeOne diverged from the decoder's
//       integer-division-toward-zero for negative predictor sums,
//       audible as background hiss/distortion. Same encoder version
//       also flips the auto-resolution so `auto` no longer secretly
//       picks the lossy format for long clips.
//   v8: DXT1 SurfaceFormat tag fix. When the BC1 encoder uses the
//       3-color + transparent mode (any source pixel with alpha < 128),
//       the XNB header now writes SurfaceFormat=20 (Dxt1a) instead of
//       4 (Dxt1). MonoGame/KNI uploads Dxt1 with the RGB-only
//       compressed format (alpha ignored, transparent → opaque black);
//       Dxt1a uses the RGBA variant the 1-bit alpha actually needs.
//   v9: KNI doesn't actually have a Dxt1a SurfaceFormat slot — its enum
//       uses Bgr32=20, which threw "The requested SurfaceFormat `Bgr32`
//       is not supported" at texture-load time. v9 reverts to writing
//       Dxt1=4 unconditionally and upgrades any BC1-with-alpha request
//       to BC3 (Dxt5) in compile-assets.ts, with a diagnostic so the
//       substitution is visible.
//   v10: BC3 color-block endpoints now exclude transparent pixels. PNG
//        decoders typically zero RGB on fully-transparent pixels, and
//        including them in the bounding-box endpoint search dragged the
//        palette toward (0,0,0) — opaque pixels near a transparent edge
//        ended up rendered as interpolated grey/dark colors (visible as
//        grey speckles in sprites' opaque bodies near transparent
//        edges).
//   v11: BC3 4-color mode now guarantees c0 > c1. Single-colour BC3
//        blocks (a sprite's solid interior) used to produce c0 == c1,
//        which strict GPU decoders interpret as BC1's 3-color +
//        transparent mode — index 3 then renders as opaque grey/black,
//        showing up as a grey rectangle in the middle of an otherwise-
//        yellow body. Force a 1-unit RGB565 gap so c0 > c1 always
//        holds. Same version also relaxes the BC3 endpoint-skip
//        threshold so partial-alpha edge pixels contribute to the
//        palette (only α=0 is now skipped).
//   v12: `auto` now defaults to `color` (uncompressed) instead of
//        dxt1/dxt5. The bounding-box BCn encoder has fundamental
//        quality limits on pixel art — production encoders use PCA +
//        rate-distortion search to pick endpoints that fit the source
//        colour cloud, and without that the 4-entry palette includes
//        off-axis interpolated colours that show up as grey/wrong-
//        coloured rectangles on small sprites. Users opt into
//        compression explicitly for textures where size matters.
//   v13: SpriteFont XNB reader manifest now uses KNI's actual assembly
//        names for the generic ListReader type parameters
//        (`Xna.Framework` for Rectangle/Vector3, `System.Private.CoreLib`
//        for Char). The bare form worked for non-generic readers via
//        KNI's short-name fallback but generics need `Type.GetType`
//        with assembly qualification on the inner type.
//   v14: SpriteFont char list + defaultChar now written as UTF-8 (one
//        byte per ASCII char) rather than UTF-16 LE. KNI's
//        BinaryReader.ReadChar() uses UTF-8 encoding, so writing two
//        bytes per char made it deserialize each char as two chars
//        (the real one + a NUL byte). The interleaved NULs broke
//        SpriteFont's "characters must be in ascending order" check.
//   v15: Shader MGFX fixes — EffectParameterClass corrected (Vector=1
//        for vec*, Object=3 for textures; previously emitted Scalar(0)
//        for all, causing InvalidCastException in
//        Effect.Parameters[X].SetValue(Vector4)). Also cbufferRefs now
//        lists every cbuffer in the effect so KNI uploads cbuffer data
//        to the GLSL uniform on Pass.Apply() (previously left empty —
//        uniforms stayed at zero, every `set effect param *` invisible).
//   v16: Shader effectKey now content-hashed (FNV-1a over the shader
//        bytecodes) instead of hard-coded 0. KNI's BlazorGL caches the
//        compiled GL shader program by effectKey across Effect instances;
//        a constant key made every shader reuse the program from the
//        first Effect ever loaded, so editing the .fx silently kept the
//        original GLSL active. Verified against the working ScreenEffect
//        (effectKey 0xe14f7f64) via Playground/scripts/probe-effect-params.mjs.
//   v17: Default ShaderCompiler swapped from GLSL passthrough to the
//        HLSL → GLSL ES 1.00 translator. Users now write float4/Texture2D/
//        Sample/semantics in `.fx` files; the translator emits the GL ES
//        1.00 KNI's BlazorGL backend expects. cbuffer fields auto-expand
//        into a matching `uniform vec4 NAME[N];` + #define aliases, so
//        the user no longer hand-writes the GLSL boilerplate.
//   v18: HLSL translator drop-in compat — struct-typed entry parameters
//        (the SpriteEffect shape), DX9 sampler_state literals (`sampler2D X
//        = sampler_state {…}`), tex2D() intrinsic, POSITION/COLOR semantic
//        aliases, `matrix` type alias, `#if OPENGL / #else / #endif`
//        preprocessor, MonoGame compat macro stripping.
//   v19: HLSL translator renames parameters whose names collide with
//        GLSL ES 1.00 reserved words (`input`/`output`/etc). MonoGame's
//        stock SpriteEffect uses `MainPS(VS_OUT input)` — GL ES 1.00
//        reserves `input` for future use, so KNI rejected the shader with
//        "Illegal use of reserved word". Translator now renames the param
//        to `_input` (and rewrites body references) before emitting GLSL.
//   v21: Semantic→varying mapping for COLOR0 fixed. KNI's BlazorGL built-in
//        SpriteBatch VS outputs the legacy fixed-pipeline name `vFrontColor`
//        for COLOR0, NOT `vColor0` (the obvious guess). Mismatched names
//        caused `Varying 'vColor0' has static-use in frag, but is undeclared
//        in vert shader` at GL link time when running the stock SpriteEffect
//        template. Probed directly from KNI.Platform.dll's embedded shader
//        templates — full list now: COLOR0/COLOR → vFrontColor, COLOR1 →
//        vFrontSecondaryColor, TEXCOORD0/1/2/3 → vTexCoord0/1/2/3.
//   v20: Three translator line-preservation fixes that landed via the new
//        `npm run validate-shaders` real-glslang CLI:
//        - stripMonoGameCompatDefines was eating preceding newlines via
//          `\s*` (matches `\n` under /m flag); switched to `[ \t]*`.
//        - translateStructDecls collapsed `struct Name\n{` into
//          `struct Name {` (dropping the inter-name-brace newline).
//          Now captures + re-emits the whitespace between name and `{`.
//        - saturate() translation was missing the outer closing paren,
//          so `floor(saturate(x) * y)` became `floor(clamp((x), 0.0, 1.0 * y)`
//          (note no `)` for the outer clamp; absorbed `* y` into clamp's
//          third arg).
//        Net effect: glslang validation errors now map back to the right
//        .fx source line via the validator's offset math.
//   v22: hlsl-compiler emits a `[hlsl-compiler] … translated GLSL …`
//        console log per stage on every effect rebuild, so runtime
//        shader issues can be diagnosed against the actual GLSL KNI
//        receives without having to rebuild the toolchain.
//   v23: compile-fx auto-injects a default vertex shader for any pass
//        with `vsShaderIndex = -1` (the canonical MonoGame PS-only
//        SpriteEffect pattern). Mirrors what `MonoGame.Effect.Compiler.exe`
//        does at .fx compile time. The default VS is byte-for-byte the
//        compiled VS from FadeSpriteBatchEffect.xnb: 4 vertex attributes
//        matching FadeSpriteVertex (Position/Color/TexCoord0/TexCoord1),
//        a `vs_uniforms_vec4[4]` cbuffer for MatrixTransform, posFixup
//        tail for DX→GL Y-flip + depth-range. FadeSpriteBatch.Setup
//        pushes its ProjectionMatrix into the user effect's
//        MatrixTransform parameter on every Apply so the screen-space
//        → NDC transform is populated before pass.Apply() uploads the
//        cbuffer to GL. Without this fold-in, KNI links PS-only effects
//        without a VS and the texture sample reads bogus texcoords —
//        the symptom was a black screen for the sprite-fx snippet.
//   v24: HLSL translator now supports user-authored vertex shaders.
//        Highlights:
//        - Entry-function regex accepts struct return types (so
//          `VertexShaderOutput MainVS(VertexShaderInput input)` parses).
//        - VS-with-struct-return trampoline decomposes the returned
//          struct: SV_POSITION → gl_Position, every other semantic →
//          its matching varying (vFrontColor/vTexCoord0/etc.), then
//          appends the posFixup DX→GL coordinate-fixup tail.
//        - `mul(vec, MatrixName)` is expanded to the explicit
//          dot-product form `vec4(dot(vec, M[0]),…dot(vec, M[3]))`
//          (sourced from FadeSpriteBatchEffect.xnb's compiled VS).
//          The expansion is paren-balanced so nested expressions in
//          the first arg survive.
//        - `cbuffer { float4x4 M; }` now emits
//          `#define M cbufferName` (was `cbufferName[0]`), so `M[i]`
//          indexes rows correctly.
//        - VS stage gains a `uniform vec4 posFixup;` declaration —
//          KNI's runtime populates it for every VS that references it.
//        Net effect: the sprite-fx snippet template now includes a
//        full MainVS that the translator fully understands, so users
//        get drop-in tinting + screen-effect support without relying
//        on the v23 auto-VS injection (which stays as a fallback for
//        older PS-only .fx files).
//   v25: VS attribute namespace split + MGFX reflection.
//        - VS inputs declare as `attribute vec4 aPosition0/aColor0/
//          aTexCoord0/…` (was `vPosition0`/`vFrontColor`/`vTexCoord0`).
//          The `a*` namespace prevents the GLSL "redefinition" error
//          you'd get in a VS that passes COLOR0/TEXCOORD0 straight
//          through (because the output varying uses `vFrontColor`/
//          `vTexCoord0` and you can't declare the same name as both
//          attribute and varying).
//        - hlsl-compiler now reports the reflected attributes
//          (name + XNA VertexElementUsage + index + location) so the
//          MGFX writer can bind GL vertex slots correctly. Previously
//          `attributes: []` was hard-coded with a TODO.
//        - Reserved-word rename (`input`/`output` → `_input`/`_output`)
//          is now a global pre-pass instead of being scoped to the
//          entry function's body. A .fx with both `MainVS` and `MainPS`
//          has the reserved name in both parameter lists, and the
//          translator emits both functions in the GLSL output of either
//          stage — so the rename has to cover the whole source.
//        - The static GLSL validator (transformEs100ToEs310ForValidation)
//          now wraps scalar free uniforms — not just array uniforms —
//          in `layout(std140, binding=N) uniform _X_block { … };`
//          blocks. Fixes the "non-opaque uniforms outside a block"
//          glslang error on the new `uniform vec4 posFixup;` line.
//   v26: Global strip of `) : SEMANTIC {` return annotations on every
//        function signature, not just the active entry's. A .fx with
//        both MainVS and MainPS leaves the inactive entry's signature
//        in the GLSL body of either stage's compile; without this
//        strip, the dangling `: COLOR` (PS) or `: SV_TARGET` (PS) on
//        the inactive function tripped a GLSL "unexpected COLON,
//        expecting LEFT_BRACE" syntax error on whichever stage wasn't
//        the active entry.
//   v27: Sampler dedup. `Texture2D X;` declarations referenced by a
//        sampler_state literal's `Texture = <X>` field are now dropped
//        entirely — only the sampler_state's name becomes a GL uniform
//        + MGFX sampler record. Emitting both produced two sampler
//        records with sequential textureSlots; at draw time
//        EffectPass.SetShaderSamplers called `_device.Textures[slot]`
//        on a slot KNI's BlazorGL TextureCollection didn't have,
//        throwing IndexOutOfRangeException. This matches what
//        MonoGame's offline compiler does — the sampler_state owns
//        the GL uniform, and the Texture2D becomes a parameter
//        users set via `Effect.Parameters["X"].SetValue(tex)`.
//   v28: Per-stage sampler reflection. The MGFX VS shader record no
//        longer carries sampler entries — KNI's EffectPass.Apply
//        iterates every shader's samplers and binds textures to
//        `_device.Textures[textureSlot]`. A VS that doesn't perform
//        texture fetches still had a sampler record because the
//        translator emits sampler declarations for both stages
//        (so MainPS's `texture2D(…)` calls parse cleanly in the
//        VS-stage GLSL output). But linking against an unused VS
//        sampler uniform sized BlazorGL's TextureCollection to zero
//        and `Textures[0] = …` threw IndexOutOfRange at draw time.
//        Filter is symmetric with what the offline MonoGame compiler
//        does: GLSL declarations stay in both stages (unused uniforms
//        are silently optimized at link); MGFX sampler records only
//        ship in the pixel stage.
//   v29: Matched float precision across stages. Pixel shaders used
//        to default to `precision mediump float;` while vertex shaders
//        used `highp`. GLSL ES 1.00 uniforms inherit the default float
//        precision, so a shared uniform (e.g. `Globals[4]` from a
//        cbuffer referenced by both MainVS and MainPS) was declared
//        as `highp` in the VS and `mediump` in the PS. WebGL rejects
//        that as "Uniform `Globals` is not linkable between attached
//        shaders" at glLinkProgram. Both stages now default to highp.
//   v30: Corrected XNA VertexElementUsage enum values used in MGFX
//        attribute records. Color was being recorded as usage=3 and
//        TextureCoordinate as usage=5, but the canonical enum is
//        Position=0, Color=1, TextureCoordinate=2, Normal=3. With the
//        old values KNI's MGFX reader couldn't match Color /
//        TextureCoordinate VertexElements to any attribute by
//        (usage,index), so aColor0 / aTexCoord0 read zero in the VS.
//        Symptoms: black screen with `tex2D(...) * input.Color`
//        (input.Color = 0) and a single-texel sample with `tex2D`
//        alone (UV = (0,0)). Verified against the attribute records
//        embedded in FadeSpriteBatchEffect.xnb.
//   v31: fx-parser now extracts top-level uniform declarations.
//        HLSL allows `float4 Tint;` at file scope (outside any
//        cbuffer block), and MonoGame's offline compiler folds these
//        into the implicit `[vs|ps]_uniforms_vec4` cbuffer. Previously
//        these declarations slipped through the parser entirely —
//        `set effect param "Tint", …` failed because Tint never made
//        it into the MGFX parameter list. The parser now collects
//        file-scope `<primitive type> <name> [: SEMANTIC]? ;`
//        declarations (depth==0 only, so struct fields and function
//        locals are excluded) into a synthetic cbuffer named
//        `_TopLevelUniforms`, sized via the same HLSL packing rules
//        as user-authored cbuffers.
//   v32: 16-byte alignment for the synthetic `_TopLevelUniforms`
//        cbuffer. A single `float Alpha;` at file scope used to be
//        emitted with `sizeInBytes: 4`, which is less than one vec4.
//        KNI's MGFX→GL uploader writes via glUniform4fv on the cbuffer
//        array (sized `ceil(sizeInBytes / 16)` slots), so a 4-byte
//        cbuffer rounded down to ZERO slots — the parameter never
//        actually reached the shader. `set effect param "Alpha", …`
//        looked like a silent no-op. The user-authored cbuffer parser
//        already aligns to 16; the synthetic path now matches.
//   v33: cbuffer #define aliases now respect the field's in-slot
//        byte offset, not just the vec4 slot index. HLSL packs
//        multiple sequential scalars into the same vec4 (Time at
//        offset 0 → .x, GlitchAmount at offset 4 → .y, …), but
//        `buildFieldAlias` was emitting `#define <name> cb[slot].x`
//        for EVERY scalar regardless of its byte offset within the
//        slot. So `cbuffer Globals { float Time; float Glitch; }`
//        produced TWO `#define`s both pointing at `cb[0].x` — only
//        the first param's value was visible; the second silently
//        read the first's value. Same applied to vec2/vec3 that
//        follow scalars in the same slot (now correctly aliased
//        to .zw / .yzw etc.). Symptom: only the FIRST scalar
//        parameter in a cbuffer worked; flipping declaration order
//        swapped which one did. Two new regression tests cover the
//        scalar-packing and vec2-after-scalars cases.
export const ENCODER_VERSION = 33;

// Source extensions we know how to compile into an XNB. Anything else
// in OPFS is either already an XNB (passed through) or non-asset state
// (sources, fade.json, settings).
export const IMAGE_SOURCE_EXTENSIONS = ['.png', '.jpg', '.jpeg', '.gif', '.bmp', '.webp'];

export function isImageSourcePath(path: string): boolean {
    const lower = path.toLowerCase();
    return IMAGE_SOURCE_EXTENSIONS.some((ext) => lower.endsWith(ext));
}

// Source extensions we know how to compile into a SoundEffect XNB.
// Every entry here is something the browser's Web Audio API decodes via
// AudioContext.decodeAudioData — that's the universal lifter we use
// before re-serialising the samples as PCM in the XNB. OGG is included
// but only decodes in Chrome/Firefox/Edge — Safari needs a JS Vorbis
// decoder (a future addition); the compile pass surfaces a diagnostic.
export const AUDIO_SOURCE_EXTENSIONS = ['.wav', '.mp3', '.ogg', '.flac', '.m4a', '.aac'];

export function isAudioSourcePath(path: string): boolean {
    const lower = path.toLowerCase();
    return AUDIO_SOURCE_EXTENSIONS.some((ext) => lower.endsWith(ext));
}

// Source extensions the font compile path accepts. TTF/OTF cover every
// reasonable game-font upload; .woff/.woff2 work fine in browsers but
// are typically aimed at web pages — leaving them off for now so the
// macro-vs-extension matching stays simple. Add later if needed.
export const FONT_SOURCE_EXTENSIONS = ['.ttf', '.otf'];

export function isFontSourcePath(path: string): boolean {
    const lower = path.toLowerCase();
    return FONT_SOURCE_EXTENSIONS.some((ext) => lower.endsWith(ext));
}

/** Default render size for a font when no `# font size` macro is set.
 *  Reasonable for typical UI text; users scale up/down via the existing
 *  `scale text` command. */
export const DEFAULT_FONT_SIZE_PX = 32;

// Source extensions the shader compile path accepts. `.fx` is MonoGame's
// canonical extension and the one shaders are written in on the desktop
// side; supporting it directly means a desktop-authored .fx works in the
// playground without changes.
export const SHADER_SOURCE_EXTENSIONS = ['.fx'];

export function isShaderSourcePath(path: string): boolean {
    const lower = path.toLowerCase();
    return SHADER_SOURCE_EXTENSIONS.some((ext) => lower.endsWith(ext));
}

// Strip directory + extension to get the asset name a fbasic program
// passes to `texture 1, "..."`. Mirrors the existing .xnb → name rule
// in syncAssetsToRuntime so an uploaded `foo/bar.png` and a previously
// uploaded `foo/bar.xnb` map to the same logical asset.
export function assetNameForSourcePath(path: string): string {
    const slash = path.lastIndexOf('/');
    const base = slash >= 0 ? path.slice(slash + 1) : path;
    const dot = base.lastIndexOf('.');
    return slash >= 0
        ? path.slice(0, slash + 1) + (dot >= 0 ? base.slice(0, dot) : base)
        : (dot >= 0 ? base.slice(0, dot) : base);
}

// Per-asset compilation settings the macro scanner produces. The cache
// uses `format` (post-Auto resolution) + the source hash to key entries.
export interface AssetSettings {
    format: TextureCompression;
}

// Per-asset record the compiler returns. The bytes are the XNB ready
// for monoGameHost.registerAsset; everything else is metadata callers
// (logs panel, problems panel) may surface.
export interface CompiledAsset {
    assetName: string;
    sourcePath: string;
    format: TextureCompression;        // post-Auto resolution
    requestedFormat: TextureCompression; // what the macro asked for
    bytes: Uint8Array;
    width: number;
    height: number;
    cached: boolean;                   // true when served from OPFS cache
}

// Audio counterpart. `format` carries the actual encoder output (today
// always `pcm`); `requestedFormat` records what the macro asked for so
// the diagnostic stream can flag adpcm-→-pcm substitutions.
export interface CompiledAudioAsset {
    assetName: string;
    sourcePath: string;
    format: AudioCompression;
    requestedFormat: AudioCompression;
    bytes: Uint8Array;
    sampleRate: number;
    channels: number;
    /** Duration in seconds, rounded to 3 places. Surfaced in logs. */
    duration: number;
    cached: boolean;
}
