// Per-kind XNB payload decoders. Each takes the post-reader-chain bytes
// (XnbClassification.objectData) and returns either preview-ready data
// (RGBA buffer, WAV bytes) or a metadata-only result with a `notes`
// explaining why the renderable preview is unavailable.
//
// Decoders never throw on malformed input — they return null. The preview
// pane falls back to the metadata card when null comes back.

import { ByteCursor, XnbParseError, type XnbClassification } from './xnb-reader';

// MonoGame SurfaceFormat enum values. Mirrors the .NET enum byte-for-byte
// so the int we read at offset 0 of the Texture2D payload maps directly.
export const SurfaceFormat = {
    Color: 0,
    Bgr565: 1,
    Bgra5551: 2,
    Bgra4444: 3,
    Dxt1: 4,
    Dxt3: 5,
    Dxt5: 6,
    NormalizedByte2: 7,
    NormalizedByte4: 8,
    Rgba1010102: 9,
    Rg32: 10,
    Rgba64: 11,
    Alpha8: 12,
    Single: 13,
    Vector2: 14,
    Vector4: 15,
    HalfSingle: 16,
    HalfVector2: 17,
    HalfVector4: 18,
    HdrBlendable: 19,
} as const;

const SURFACE_FORMAT_NAMES = Object.fromEntries(
    Object.entries(SurfaceFormat).map(([k, v]) => [v, k]),
) as Record<number, string>;

export function surfaceFormatLabel(format: number): string {
    return SURFACE_FORMAT_NAMES[format] ?? `Unknown (${format})`;
}

export interface Texture2DPreview {
    width: number;
    height: number;
    mipCount: number;
    surfaceFormat: number;
    surfaceFormatLabel: string;
    // Mip 0 as raw RGBA8 (one byte per channel, row-major) when the
    // surface format is Color; null when we don't know how to decode it.
    rgba: Uint8ClampedArray | null;
    notes?: string;
}

export function decodeTexture2D(cls: XnbClassification): Texture2DPreview | null {
    if (!cls.objectData) return null;
    try {
        const c = new ByteCursor(cls.objectData);
        const surfaceFormat = c.readInt32LE();
        const width = c.readInt32LE();
        const height = c.readInt32LE();
        const mipCount = c.readInt32LE();
        const firstMipSize = c.readUint32LE();
        const firstMipBytes = c.readBytes(firstMipSize);
        const result: Texture2DPreview = {
            width,
            height,
            mipCount,
            surfaceFormat,
            surfaceFormatLabel: surfaceFormatLabel(surfaceFormat),
            rgba: null,
        };
        if (surfaceFormat === SurfaceFormat.Color) {
            const expected = width * height * 4;
            if (firstMipBytes.length < expected) {
                result.notes = `Mip 0 is ${firstMipBytes.length} bytes; expected ${expected} for Color.`;
                return result;
            }
            // Copy into a fresh Uint8ClampedArray so ImageData owns its buffer
            // (the subarray view alias of OPFS bytes would be GC-rooted by
            // the canvas otherwise, and shape-mismatched with ImageData's
            // constructor signature on some browsers).
            const out = new Uint8ClampedArray(expected);
            out.set(firstMipBytes.subarray(0, expected));
            result.rgba = out;
        } else {
            result.notes = `Preview only renders the Color surface format. Got ${result.surfaceFormatLabel}.`;
        }
        return result;
    } catch (e: any) {
        if (e instanceof XnbParseError) return null;
        throw e;
    }
}

export interface SoundEffectPreview {
    formatTag: number;
    formatTagLabel: string;
    channels: number;
    sampleRate: number;
    bitsPerSample: number;
    durationMs: number;
    dataLength: number;
    // Complete, self-contained WAV bytes (RIFF header + fmt + data) the
    // preview pane hands to <audio src=blob:...>. Null when the format
    // tag isn't directly playable — e.g. ADPCM, which would need decoding
    // back to PCM before browsers will touch it.
    wavBytes: Uint8Array | null;
    notes?: string;
}

const WAV_FORMAT_LABELS: Record<number, string> = {
    1: 'PCM',
    2: 'MS ADPCM',
    3: 'IEEE Float',
    17: 'IMA ADPCM',
};

export function decodeSoundEffect(cls: XnbClassification): SoundEffectPreview | null {
    if (!cls.objectData) return null;
    try {
        const c = new ByteCursor(cls.objectData);
        const formatChunkSize = c.readUint32LE();
        const fmtBytes = c.readBytes(formatChunkSize);
        const dataLength = c.readUint32LE();
        const dataBytes = c.readBytes(dataLength);
        // SoundEffectReader writes loopStart/loopLength/durationMs after
        // the data chunk. Tolerant of writers that truncate — we only need
        // the duration for display.
        let durationMs = 0;
        try {
            c.readInt32LE();              // loopStart
            c.readInt32LE();              // loopLength
            durationMs = c.readInt32LE(); // durationMs
        } catch { /* leave as 0 */ }

        // fmt chunk is a WAVEFORMATEX:
        //   formatTag(2) channels(2) samplesPerSec(4) avgBytesPerSec(4)
        //   blockAlign(2) bitsPerSample(2) [cbSize(2) extra(cbSize)]
        const dv = new DataView(fmtBytes.buffer, fmtBytes.byteOffset, fmtBytes.byteLength);
        const formatTag = dv.getUint16(0, true);
        const channels = dv.getUint16(2, true);
        const sampleRate = dv.getUint32(4, true);
        const bitsPerSample = fmtBytes.length >= 16 ? dv.getUint16(14, true) : 0;

        const result: SoundEffectPreview = {
            formatTag,
            formatTagLabel: WAV_FORMAT_LABELS[formatTag] ?? `Unknown (${formatTag})`,
            channels,
            sampleRate,
            bitsPerSample,
            durationMs,
            dataLength,
            wavBytes: null,
        };

        if (formatTag === 1 || formatTag === 3) {
            result.wavBytes = packWavFile(fmtBytes, dataBytes);
        } else {
            result.notes = `Preview only plays PCM. Got ${result.formatTagLabel}.`;
        }
        return result;
    } catch (e: any) {
        if (e instanceof XnbParseError) return null;
        throw e;
    }
}

// Build a complete RIFF/WAVE file in memory from the fmt + data chunks
// extracted from the SoundEffect payload. Output is a single Uint8Array
// suitable for `new Blob([bytes], { type: 'audio/wav' })`.
function packWavFile(fmtBytes: Uint8Array, dataBytes: Uint8Array): Uint8Array {
    const fmtLen = fmtBytes.length;
    const dataLen = dataBytes.length;
    // 'WAVE' (4) + 'fmt ' header (8) + fmt (fmtLen) + 'data' header (8) + data (dataLen)
    const ridFileSize = 4 + (8 + fmtLen) + (8 + dataLen);
    const out = new Uint8Array(8 + ridFileSize);
    const dv = new DataView(out.buffer);
    let p = 0;
    writeAscii(out, p, 'RIFF'); p += 4;
    dv.setUint32(p, ridFileSize, true); p += 4;
    writeAscii(out, p, 'WAVE'); p += 4;
    writeAscii(out, p, 'fmt '); p += 4;
    dv.setUint32(p, fmtLen, true); p += 4;
    out.set(fmtBytes, p); p += fmtLen;
    writeAscii(out, p, 'data'); p += 4;
    dv.setUint32(p, dataLen, true); p += 4;
    out.set(dataBytes, p);
    return out;
}

function writeAscii(out: Uint8Array, offset: number, s: string) {
    for (let i = 0; i < s.length; i++) out[offset + i] = s.charCodeAt(i);
}

// ─── KNI Blazor workaround: SoundEffect loopLength patcher ──────────────────
// KNI's Platforms/Audio/Blazor/ConcreteSoundEffect.PlatformInitializePcm uses
// `loopLength` as the AudioBuffer's frame count:
//
//     _audioBuffer = ctx.CreateBuffer(numOfChannels, loopLength, sampleRate);
//
// MGCB writes loopLength=0 for non-looping sounds (the common case). KNI then
// asks WebAudio to make a 0-frame buffer, which throws:
//
//     NotSupportedError: The number of frames provided (0) is less than or
//     equal to the minimum bound (0).
//
// Fix is upstream in KNI; in the meantime we rewrite the loopLength int32 to
// `dataSize / (channels * bytesPerSample)` so KNI allocates a full-length
// buffer. Idempotent — if loopLength already matches (or exceeds), we leave
// it. Returns a *new* Uint8Array when patched so callers don't accidentally
// mutate the OPFS-backed view; returns the same array unchanged otherwise.
//
// SoundEffect payload layout (after the reader chain, at cls.objectData):
//   int32  headerSize
//   bytes  header  [headerSize]  (WAVEFORMATEX: format, channels, rate, …)
//   int32  dataSize
//   bytes  data    [dataSize]
//   int32  loopStart
//   int32  loopLength   ← patched
//   int32  durationMs

import { classifyXnb } from './xnb-reader';

export function patchSoundEffectForKni(bytes: Uint8Array): Uint8Array {
    let cls;
    try { cls = classifyXnb(bytes); } catch { return bytes; }
    if (cls.kind !== 'sound-effect' || !cls.objectData) return bytes;

    // We need the absolute byte offset of `loopLength` inside the original
    // file. `cls.objectData` is a subarray view starting at the same buffer
    // as `bytes`, so the offset delta gives us the position.
    const payloadStart = cls.objectData.byteOffset - bytes.byteOffset;
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    let off = payloadStart;
    try {
        const headerSize = dv.getUint32(off, true); off += 4;
        if (headerSize < 16) return bytes;
        const channels = dv.getUint16(off + 2, true);
        const bitsPerSample = dv.getUint16(off + 14, true);
        off += headerSize;
        const dataSize = dv.getUint32(off, true); off += 4;
        off += dataSize;            // skip past data
        off += 4;                   // skip loopStart
        const loopLengthOffset = off;
        const loopLength = dv.getInt32(loopLengthOffset, true);
        const bytesPerFrame = (bitsPerSample / 8) * channels;
        if (bytesPerFrame <= 0) return bytes;
        const totalFrames = Math.floor(dataSize / bytesPerFrame);
        if (loopLength >= totalFrames) return bytes;  // already fine
        // Copy + patch. Keep original bytes untouched in case the caller
        // also passes them to the preview pane or other readers.
        const out = new Uint8Array(bytes);
        const outDv = new DataView(out.buffer, out.byteOffset, out.byteLength);
        outDv.setInt32(loopLengthOffset, totalFrames, true);
        return out;
    } catch {
        return bytes;
    }
}
