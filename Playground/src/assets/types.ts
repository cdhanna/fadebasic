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
export const ENCODER_VERSION = 14;

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
