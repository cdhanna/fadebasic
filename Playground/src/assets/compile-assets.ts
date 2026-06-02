// Per-project asset compile pass: walks the workspace for image
// sources, applies the compression decisions surfaced by the iframe's
// macro pass (CompileForRun → plan), and emits an in-memory XNB per
// asset via the OPFS-backed cache.
//
// The plan is the post-macro snapshot of ContentSystem from the iframe.
// It carries:
//
//   { defaultCompression: 'auto'|'color'|'dxt5'|...,
//     entries: [{ path, name, importer, processor, parameters: {...} }, ...] }
//
// Each entry's per-asset compression lives at parameters['Compression'].
// Anything missing falls back to defaultCompression. Image sources in
// OPFS that aren't mentioned in any entry still get compiled — they use
// defaultCompression alone, so the "upload a PNG and reference by name"
// shortcut keeps working.

import {
    AssetCache,
    sha256Hex,
    type CacheWorkspaceLike,
} from './asset-cache';
import {
    DEFAULT_FONT_SIZE_PX,
    ENCODER_VERSION,
    assetNameForSourcePath,
    isImageSourcePath,
    type AudioCompression,
    type CompiledAsset,
    type CompiledAudioAsset,
    type TextureCompression,
} from './types';
import {
    encodeTexture2dXnb,
    type ConcreteTextureFormat,
} from './xnb-writer';
import {
    decodeAudio,
    encodeAdpcmSoundEffectXnb,
    encodePcmSoundEffectXnb,
    type DecodedAudio,
} from './audio-encoder';
import { rasterizeFont } from './font-rasterizer';
import { encodeSpriteFontXnb } from './sprite-font-writer';
import {
    compileFxToXnb,
    CompileFxError,
    type CompileFxDiagnostic,
} from '../shader/compile-fx';
import { ShaderCompilerNotAvailableError } from '../shader/shader-compiler';
import type { MonoGameContentPlan } from '../monogame-host';

export interface CompileAssetsResult {
    assets: CompiledAsset[];
    diagnostics: AssetDiagnostic[];
}

export interface CompileAudioAssetsResult {
    assets: CompiledAudioAsset[];
    diagnostics: AssetDiagnostic[];
}

export interface CompiledFontAsset {
    assetName: string;
    sourcePath: string;
    sizePx: number;
    /** SpriteFont XNB bytes ready for monoGameHost.registerAsset. */
    bytes: Uint8Array;
    /** Atlas dimensions, surfaced in the logs. */
    atlasWidth: number;
    atlasHeight: number;
    cached: boolean;
}

export interface CompileFontAssetsResult {
    assets: CompiledFontAsset[];
    diagnostics: AssetDiagnostic[];
}

export interface CompiledShaderAsset {
    assetName: string;
    sourcePath: string;
    /** MGFX-v10 XNB bytes ready for monoGameHost.registerAsset. */
    bytes: Uint8Array;
    cached: boolean;
}

export interface CompileShaderAssetsResult {
    assets: CompiledShaderAsset[];
    diagnostics: AssetDiagnostic[];
}

export interface AssetDiagnostic {
    severity: 'info' | 'warn' | 'error';
    assetName?: string;
    sourcePath?: string;
    message: string;
}

const VALID_COMPRESSIONS = new Set<TextureCompression>([
    'auto', 'none', 'color', 'dxt1', 'dxt3', 'dxt5',
]);

function normalizeCompression(raw: string | undefined): TextureCompression | null {
    if (!raw) return null;
    const v = raw.trim().toLowerCase();
    return VALID_COMPRESSIONS.has(v as TextureCompression) ? (v as TextureCompression) : null;
}

/** Decode bytes (PNG/JPG/…) via the browser to an RGBA pixel buffer. */
async function decodeImageBytes(
    bytes: Uint8Array,
): Promise<{ rgba: Uint8ClampedArray; width: number; height: number }> {
    // Wrap the raw bytes in a Blob — `createImageBitmap` accepts a Blob
    // directly and uses the browser's native decoders, which handle every
    // image format users are likely to upload (PNG / JPG / GIF / WEBP /
    // BMP).
    const blob = new Blob([bytes as BlobPart]);
    const bitmap = await createImageBitmap(blob);
    try {
        // OffscreenCanvas keeps the decode off any DOM canvas + lets us
        // call this from a worker later without refactoring.
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        if (!ctx) throw new Error('OffscreenCanvas: failed to acquire 2d context');
        ctx.drawImage(bitmap, 0, 0);
        const image = ctx.getImageData(0, 0, bitmap.width, bitmap.height);
        return { rgba: image.data, width: bitmap.width, height: bitmap.height };
    } finally {
        bitmap.close();
    }
}

/** Classify an RGBA buffer's alpha usage to drive format selection.
 *  KNI's GPU upload path can't represent 1-bit alpha through Dxt1
 *  (the SurfaceFormat slot MonoGame uses for Dxt1a is repurposed in
 *  KNI as Bgr32), so the distinction the playground cares about is
 *  binary: does the source use ANY transparency, in which case BC1
 *  isn't viable and we fall back to BC3 (Dxt5). */
function classifyAlpha(rgba: Uint8ClampedArray | Uint8Array): {
    hasAnyAlpha: boolean;
    hasPartialAlpha: boolean;
} {
    let hasAny = false;
    let hasPartial = false;
    for (let i = 3; i < rgba.length; i += 4) {
        const a = rgba[i];
        if (a !== 255) hasAny = true;
        if (a !== 0 && a !== 255) { hasPartial = true; break; }
    }
    return { hasAnyAlpha: hasAny || hasPartial, hasPartialAlpha: hasPartial };
}

/** Resolve a requested compression to a concrete format the encoder
 *  emits. `auto` picks `color` (uncompressed) — the playground's BCn
 *  encoder uses bounding-box endpoint selection and produces visible
 *  artifacts on pixel art (the bounding box doesn't fit the source
 *  colours along a line in RGB space, so the 4-entry palette includes
 *  off-axis interpolated colours that some opaque pixels then map to).
 *  Production encoders like rgbcx use PCA + rate-distortion search to
 *  pick better endpoints; until we bundle one, `auto` defaults to the
 *  format with no quality compromise.
 *
 *  Users who want BC compression for size reasons (large background
 *  textures where 4:1 savings actually matter) opt in with an explicit
 *  `# texture compression "X", "dxt5"` macro — the diagnostic on that
 *  path notes the quality trade-off. */
function resolveFormat(
    requested: TextureCompression,
    alpha: { hasAnyAlpha: boolean; hasPartialAlpha: boolean },
    assetName: string,
    diagnostics: AssetDiagnostic[],
): ConcreteTextureFormat {
    if (requested === 'auto') {
        diagnostics.push({
            severity: 'info',
            assetName,
            message: `compression auto → color (set 'dxt5' explicitly to trade ~4× disk size for some quality loss; the playground encoder's BCn quality is mediocre on pixel art)`,
        });
        return 'color';
    }
    if (requested === 'none' || requested === 'color') return 'color';
    if (requested === 'dxt1' && alpha.hasAnyAlpha) {
        diagnostics.push({
            severity: 'warn',
            assetName,
            message: `compression dxt1 → dxt5 (KNI doesn't expose a Dxt1a SurfaceFormat, so BC1 with transparency renders as opaque black; BC3/dxt5 carries alpha cleanly)`,
        });
        return 'dxt5';
    }
    if (requested === 'dxt3') {
        diagnostics.push({
            severity: 'info',
            assetName,
            message: `compression dxt3 → dxt5 (BC2's explicit alpha is strictly worse than BC3's interpolated alpha for anti-aliased edges)`,
        });
        return 'dxt5';
    }
    // dxt1, dxt5, alpha8, bgra4444 all pass through unchanged.
    return requested as ConcreteTextureFormat;
}

/** Look up a cache entry without paying for the decode. */
async function lookupCache(
    cache: AssetCache,
    assetName: string,
    sourceHash: string,
    format: TextureCompression,
): Promise<{ bytes: Uint8Array; width: number; height: number } | null> {
    const hit = await cache.lookup(assetName, sourceHash, format, ENCODER_VERSION);
    return hit ? { bytes: hit.bytes, width: hit.entry.width, height: hit.entry.height } : null;
}

/** Build a lookup keyed by both asset name and source path so a
 *  `# texture compression "MyPic", "dxt5"` macro matches whether the
 *  user has `MyPic.png` in OPFS or `MyPic.xnb` already, and whether
 *  they've also done `# push asset` (which uses the path as the key). */
function buildPlanIndex(plan: MonoGameContentPlan): Map<string, string> {
    const byKey = new Map<string, string>();
    for (const entry of plan.entries) {
        const value = entry.parameters?.Compression ?? entry.parameters?.compression ?? '';
        if (!value) continue;
        if (entry.name) byKey.set(entry.name, value);
        if (entry.path && entry.path !== entry.name) byKey.set(entry.path, value);
    }
    return byKey;
}

/** Plan + compile every image source in `imageSources`. The plan comes
 *  from the iframe's CompileForRun — its entries dictate per-asset
 *  compression, falling back to defaultCompression for assets not
 *  mentioned. Image sources never mentioned by the plan still get
 *  compiled (under defaultCompression) so the upload-and-reference
 *  shortcut keeps working without macros. */
export async function compileImageAssetsWithPlan(
    workspace: CacheWorkspaceLike,
    imageSources: string[],
    plan: MonoGameContentPlan,
): Promise<CompileAssetsResult> {
    const cache = new AssetCache(workspace);
    const diagnostics: AssetDiagnostic[] = [];
    const assets: CompiledAsset[] = [];

    const planByKey = buildPlanIndex(plan);
    const defaultCompression =
        normalizeCompression(plan.defaultCompression) ?? 'auto';

    const liveSourcePaths = new Set<string>();
    for (const path of imageSources) {
        if (!isImageSourcePath(path)) continue;
        liveSourcePaths.add(path);

        const assetName = assetNameForSourcePath(path);
        const planValue = planByKey.get(assetName) ?? planByKey.get(path) ?? '';
        const requested =
            normalizeCompression(planValue) ?? defaultCompression;

        try {
            const sourceBytes = await workspace.readBytes(path);
            const sourceHash = await sha256Hex(sourceBytes);

            const cached = await lookupCache(cache, assetName, sourceHash, requested);
            if (cached) {
                assets.push({
                    assetName,
                    sourcePath: path,
                    format: requested,
                    requestedFormat: requested,
                    bytes: cached.bytes,
                    width: cached.width,
                    height: cached.height,
                    cached: true,
                });
                continue;
            }

            const decoded = await decodeImageBytes(sourceBytes);
            const concrete = resolveFormat(
                requested,
                classifyAlpha(decoded.rgba),
                assetName,
                diagnostics,
            );
            const xnb = encodeTexture2dXnb({
                rgba: decoded.rgba,
                width: decoded.width,
                height: decoded.height,
                format: concrete,
            });
            await cache.store(
                assetName, path, sourceHash, requested,
                ENCODER_VERSION, xnb, decoded.width, decoded.height,
            );
            assets.push({
                assetName,
                sourcePath: path,
                format: requested,
                requestedFormat: requested,
                bytes: xnb,
                width: decoded.width,
                height: decoded.height,
                cached: false,
            });
        } catch (e: any) {
            diagnostics.push({
                severity: 'error',
                assetName,
                sourcePath: path,
                message: `asset compile failed: ${e?.message ?? e}`,
            });
        }
    }

    // GC is intentionally NOT run here — the cache is shared with the
    // audio compile pass, and a per-pass GC would wipe the other kind's
    // entries because they're not in this pass's liveSourcePaths.
    // The caller (syncAssetsToRuntime) runs garbageCollectAssetCache
    // once with the union of all live source paths.
    return { assets, diagnostics };
}

/** Purge cache entries whose source path no longer exists in the
 *  workspace. Call once per compile cycle, after every per-kind
 *  compile pass has completed and contributed to `liveSourcePaths`. */
export async function garbageCollectAssetCache(
    workspace: CacheWorkspaceLike,
    liveSourcePaths: Set<string>,
): Promise<void> {
    const cache = new AssetCache(workspace);
    await cache.garbageCollect(liveSourcePaths);
}

// ─── Audio path ──────────────────────────────────────────────────────

const VALID_AUDIO_COMPRESSIONS = new Set<AudioCompression>(['auto', 'pcm', 'adpcm']);

function normalizeAudioCompression(raw: string | undefined): AudioCompression | null {
    if (!raw) return null;
    const v = raw.trim().toLowerCase();
    return VALID_AUDIO_COMPRESSIONS.has(v as AudioCompression) ? (v as AudioCompression) : null;
}

type ConcreteAudioFormat = 'pcm' | 'adpcm';

/** Resolve a requested audio compression to a concrete format.
 *  `auto` always picks PCM today — it's essentially lossless and the
 *  size win of ADPCM (4:1) isn't worth the quality hit for the
 *  playground's default behaviour. Users opt into ADPCM explicitly
 *  via `# sound compression "X", "adpcm"` when they want to shrink
 *  longer clips. */
function resolveAudioFormat(
    requested: AudioCompression,
    _duration: number,
    assetName: string,
    diagnostics: AssetDiagnostic[],
): ConcreteAudioFormat {
    if (requested === 'pcm' || requested === 'adpcm') return requested;
    diagnostics.push({
        severity: 'info',
        assetName,
        message: `compression auto → pcm (set 'adpcm' explicitly to trade ~4× disk size for some quality)`,
    });
    return 'pcm';
}

function encodeAudio(format: ConcreteAudioFormat, decoded: DecodedAudio): Uint8Array {
    return format === 'adpcm'
        ? encodeAdpcmSoundEffectXnb(decoded)
        : encodePcmSoundEffectXnb(decoded);
}

/** Same plan-driven story as compileImageAssetsWithPlan, but for audio
 *  sources. Reads the `SoundCompression` parameter the `sound
 *  compression` / `default sound compression` macros emit; falls back
 *  to the plan's defaultCompression (which the C# side only ships for
 *  textures today — audio just defaults to 'auto' here). */
export async function compileAudioAssetsWithPlan(
    workspace: CacheWorkspaceLike,
    audioSources: string[],
    plan: MonoGameContentPlan,
): Promise<CompileAudioAssetsResult> {
    const cache = new AssetCache(workspace);
    const diagnostics: AssetDiagnostic[] = [];
    const assets: CompiledAudioAsset[] = [];

    // Per-asset overrides keyed by both name and path so a `sound
    // compression` macro that addresses the asset by either handle
    // hits the right entry.
    const planByKey = new Map<string, string>();
    for (const entry of plan.entries) {
        const value =
            entry.parameters?.SoundCompression
            ?? entry.parameters?.soundCompression
            ?? '';
        if (!value) continue;
        if (entry.name) planByKey.set(entry.name, value);
        if (entry.path && entry.path !== entry.name) planByKey.set(entry.path, value);
    }

    for (const path of audioSources) {
        const assetName = assetNameForSourcePath(path);
        const planValue = planByKey.get(assetName) ?? planByKey.get(path) ?? '';
        // The plan's defaultCompression is texture-flavoured today; audio
        // has no global default coming through the macro yet, so we
        // just fall back to 'auto' here.
        const requested = normalizeAudioCompression(planValue) ?? 'auto';
        try {
            const sourceBytes = await workspace.readBytes(path);
            const sourceHash = await sha256Hex(sourceBytes);

            // Cache key uses the *requested* format so 'auto' and 'pcm'
            // cache separately even when auto resolves to PCM — keeps the
            // re-resolve cheap if the user later tightens the macro.
            const cached = await cache.lookup(
                assetName, sourceHash, requested, ENCODER_VERSION,
            );
            if (cached) {
                const m = cached.entry.metadata ?? {};
                assets.push({
                    assetName,
                    sourcePath: path,
                    format: requested,
                    requestedFormat: requested,
                    bytes: cached.bytes,
                    sampleRate: Number(m.sampleRate ?? 0),
                    channels: Number(m.channels ?? 0),
                    duration: Number(m.duration ?? 0),
                    cached: true,
                });
                continue;
            }

            const decoded = await decodeAudio(sourceBytes);
            const concrete = resolveAudioFormat(requested, decoded.duration, assetName, diagnostics);
            const xnb = encodeAudio(concrete, decoded);
            await cache.store(
                assetName, path, sourceHash, requested,
                ENCODER_VERSION, xnb,
                /*width*/ 0, /*height*/ 0,
                {
                    sampleRate: decoded.sampleRate,
                    channels: decoded.channels,
                    duration: decoded.duration,
                    // Record the resolved format alongside the metadata so
                    // log messages on cache hit can report it. Not part of
                    // the cache key — that's `requested`.
                    resolvedFormat: concrete,
                },
            );
            assets.push({
                assetName,
                sourcePath: path,
                format: requested,
                requestedFormat: requested,
                bytes: xnb,
                sampleRate: decoded.sampleRate,
                channels: decoded.channels,
                duration: decoded.duration,
                cached: false,
            });
        } catch (e: any) {
            diagnostics.push({
                severity: 'error',
                assetName,
                sourcePath: path,
                message: `audio compile failed: ${e?.message ?? e}`,
            });
        }
    }

    return { assets, diagnostics };
}

// ─── Font path ───────────────────────────────────────────────────────

/** Per-asset font settings from the plan. Today the only knob is the
 *  render size in pixels (set via the `# font size` macro). */
function fontSizeFromPlan(plan: MonoGameContentPlan, assetName: string, path: string): number {
    for (const entry of plan.entries) {
        if (entry.name !== assetName && entry.path !== assetName
            && entry.name !== path && entry.path !== path) continue;
        const raw = entry.parameters?.FontSize ?? entry.parameters?.fontSize;
        if (!raw) continue;
        const n = parseInt(raw, 10);
        if (!isNaN(n) && n >= 4 && n <= 512) return n;
    }
    return DEFAULT_FONT_SIZE_PX;
}

/** Compile every TTF/OTF source in `fontSources` into a SpriteFont
 *  XNB, cached by source hash + size. The output bytes are ready for
 *  monoGameHost.registerAsset and resolve under the asset name fade's
 *  `font` command will look them up by. */
export async function compileFontAssetsWithPlan(
    workspace: CacheWorkspaceLike,
    fontSources: string[],
    plan: MonoGameContentPlan,
): Promise<CompileFontAssetsResult> {
    const cache = new AssetCache(workspace);
    const diagnostics: AssetDiagnostic[] = [];
    const assets: CompiledFontAsset[] = [];

    for (const path of fontSources) {
        const assetName = assetNameForSourcePath(path);
        const sizePx = fontSizeFromPlan(plan, assetName, path);
        // Cache format key encodes the chosen size so changing the
        // macro re-rasterizes without colliding with the prior atlas.
        const formatKey = `spritefont-${sizePx}`;

        try {
            const sourceBytes = await workspace.readBytes(path);
            const sourceHash = await sha256Hex(sourceBytes);

            const hit = await cache.lookup(assetName, sourceHash, formatKey, ENCODER_VERSION);
            if (hit) {
                const m = hit.entry.metadata ?? {};
                assets.push({
                    assetName,
                    sourcePath: path,
                    sizePx,
                    bytes: hit.bytes,
                    atlasWidth: Number(m.atlasWidth ?? 0),
                    atlasHeight: Number(m.atlasHeight ?? 0),
                    cached: true,
                });
                continue;
            }

            // FontFace name uniqueness: include the source hash so two
            // files named "MyFont.ttf" in different projects don't
            // collide in the shared document.fonts registry.
            const fontFaceName = `fade__${assetName.replace(/[^A-Za-z0-9_-]/g, '_')}__${sourceHash.slice(0, 8)}`;

            const raster = await rasterizeFont({
                name: fontFaceName,
                sizePx,
                bytes: sourceBytes,
            });
            const xnb = encodeSpriteFontXnb(raster);
            await cache.store(
                assetName, path, sourceHash, formatKey,
                ENCODER_VERSION, xnb,
                /*width*/ raster.atlasWidth, /*height*/ raster.atlasHeight,
                {
                    sizePx,
                    glyphCount: raster.glyphs.length,
                    atlasWidth: raster.atlasWidth,
                    atlasHeight: raster.atlasHeight,
                },
            );
            assets.push({
                assetName,
                sourcePath: path,
                sizePx,
                bytes: xnb,
                atlasWidth: raster.atlasWidth,
                atlasHeight: raster.atlasHeight,
                cached: false,
            });
        } catch (e: any) {
            diagnostics.push({
                severity: 'error',
                assetName,
                sourcePath: path,
                message: `font compile failed: ${e?.message ?? e}`,
            });
        }
    }

    return { assets, diagnostics };
}

// ─── Shader path ─────────────────────────────────────────────────────

/** Compile every `.fx` source into a MGFX-v10 effect XNB. Same plan-driven
 *  story as fonts: cache keyed on (sourceHash, ENCODER_VERSION) so unchanged
 *  shader files don't re-invoke the WASM compiler on every Run.
 *
 *  Diagnostics from the underlying shader compiler — both diagnostic messages
 *  and the catchable error types ShaderCompilerNotAvailableError and
 *  CompileFxError — get surfaced as per-asset diagnostics so the user sees
 *  exactly which `.fx` failed and why in the Logs panel. */
export async function compileShaderAssetsWithPlan(
    workspace: CacheWorkspaceLike,
    shaderSources: string[],
    _plan: MonoGameContentPlan,
): Promise<CompileShaderAssetsResult> {
    const cache = new AssetCache(workspace);
    const diagnostics: AssetDiagnostic[] = [];
    const assets: CompiledShaderAsset[] = [];

    for (const path of shaderSources) {
        const assetName = assetNameForSourcePath(path);
        // Single shader format key for now — when we plumb compile flags
        // (debug-vs-optimize, target-profile) through the macro pass this
        // is where the variant tag goes, like the font path's `spritefont-<size>`.
        const formatKey = 'shader-mgfx-v10';

        try {
            const sourceBytes = await workspace.readBytes(path);
            const sourceHash = await sha256Hex(sourceBytes);

            const hit = await cache.lookup(assetName, sourceHash, formatKey, ENCODER_VERSION);
            if (hit) {
                assets.push({
                    assetName,
                    sourcePath: path,
                    bytes: hit.bytes,
                    cached: true,
                });
                continue;
            }

            const source = new TextDecoder('utf-8', { fatal: false }).decode(sourceBytes);
            const result = await compileFxToXnb({ source, assetName });

            for (const d of result.diagnostics) {
                diagnostics.push(mapShaderDiagnostic(d, assetName, path));
            }

            await cache.store(
                assetName, path, sourceHash, formatKey,
                ENCODER_VERSION, result.xnb,
                /*width*/ 0, /*height*/ 0,
                { techniqueCount: result.fx.techniques.length },
            );
            assets.push({ assetName, sourcePath: path, bytes: result.xnb, cached: false });
        } catch (e: any) {
            if (e instanceof ShaderCompilerNotAvailableError) {
                diagnostics.push({
                    severity: 'error',
                    assetName,
                    sourcePath: path,
                    message: e.message,
                });
                continue;
            }
            if (e instanceof CompileFxError) {
                if (e.diagnostics) {
                    for (const d of e.diagnostics) {
                        diagnostics.push(mapShaderDiagnostic(d, assetName, path));
                    }
                }
                diagnostics.push({
                    severity: 'error', assetName, sourcePath: path,
                    message: `shader compile failed: ${e.message}`,
                });
                continue;
            }
            diagnostics.push({
                severity: 'error', assetName, sourcePath: path,
                message: `shader compile failed: ${e?.message ?? e}`,
            });
        }
    }

    return { assets, diagnostics };
}

function mapShaderDiagnostic(
    d: CompileFxDiagnostic,
    assetName: string,
    sourcePath: string,
): AssetDiagnostic {
    const where = d.line ? ` [${d.line}${d.column ? ':' + d.column : ''}]` : '';
    return {
        severity: d.severity === 'error' ? 'error' : d.severity === 'warning' ? 'warn' : 'info',
        assetName,
        sourcePath,
        message: `${d.message}${where}`,
    };
}
