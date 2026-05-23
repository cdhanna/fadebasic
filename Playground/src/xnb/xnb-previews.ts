// Per-kind XNB payload decoders. Each takes the post-reader-chain bytes
// (XnbClassification.objectData) and returns either preview-ready data
// (RGBA buffer, WAV bytes) or a metadata-only result with a `notes`
// explaining why the renderable preview is unavailable.
//
// Decoders never throw on malformed input — they return null. The preview
// pane falls back to the metadata card when null comes back.

import { ByteCursor, XnbParseError, classifyXnb, type XnbClassification } from './xnb-reader';

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

// ─── KNI version-skew workaround: MGFX v11 → v10 downgrader ─────────────────
// Desktop MGCB (recent NuGets) emits MGFX header Version = 11. KNI 4.2.9001's
// Effect constructor caps at v10 and throws "This effect seems to be for a
// newer version of KNI." on anything higher.
//
// The v10 → v11 bump (MonoGame PR #8813, commit 08677e96b) added two strings
// per shader record — `SourceFile` and `Entrypoint` — for runtime error
// diagnostics. Mainline MonoGame's v10 reader is otherwise byte-identical,
// so a v11 blob with those two strings spliced out of every shader record is
// a valid v10 blob.
//
// EffectReader payload (objectData) layout:
//   int32  dataSize         ← length of MGFX blob; needs adjustment
//   bytes  'MGFX' (4)
//   byte   version          ← needs to be rewritten to 10
//   byte   profileId
//   int32  effectKey        ← content hash; written by MGFXC, ignored on read
//   ── MGFX body ──
//   int32  cbufferCount
//   cbufferCount × ConstantBuffer { string name, int16 size, int32 paramCount,
//                                    paramCount × (int32 idx, uint16 offset) }
//   int32  shaderCount
//   shaderCount × Shader {
//       bool   isVertexShader
//       string SourceFile      ← v11 only; SPLICED OUT
//       string Entrypoint      ← v11 only; SPLICED OUT
//       int32  shaderLength
//       bytes  shaderBytecode [shaderLength]
//       byte   samplerCount
//       samplerCount × Sampler { byte×3 (type, slots), bool hasState,
//                                 [if state: byte×8 (addr×3, color×4, filter)
//                                            + int32×2 (anis, mip)
//                                            + float (bias)] = 20 bytes,
//                                 string name, byte parameter }
//       byte   cbufferRefCount
//       cbufferRefCount × byte
//       byte   attributeCount
//       attributeCount × Attribute { string name, byte usage, byte index,
//                                     int16 location }
//   }
//   …Parameters + Techniques follow (unchanged, not walked here)
//
// Outputs a fresh Uint8Array with: spliced shader strings removed, MGFX
// version byte set to 10, EffectReader dataSize prefix and XNB header
// fileSize both decremented by the total bytes removed. Idempotent — when
// the input is already v10 or doesn't look like an MGFX effect, returns the
// input unchanged.
const MGFX_VERSION_KNI = 10;

export function patchEffectMgfxVersionForKni(bytes: Uint8Array): Uint8Array {
    let cls;
    try { cls = classifyXnb(bytes); } catch { return bytes; }
    if (cls.kind !== 'effect' || !cls.objectData) return bytes;
    const payloadStart = cls.objectData.byteOffset - bytes.byteOffset;
    const od = cls.objectData;
    // 4-byte dataSize + 4-byte 'MGFX' magic + 1 version + 1 profile minimum
    if (od.length < 10) return bytes;
    if (
        od[4] !== 0x4D || od[5] !== 0x47 || od[6] !== 0x46 || od[7] !== 0x58
    ) {
        return bytes; // not an MGFX blob in the expected slot; bail
    }
    const version = od[8];
    if (version === MGFX_VERSION_KNI) return bytes; // already v10
    if (version !== 11) return bytes; // only v11 → v10 is implemented

    // Walk the MGFX body, recording the byte ranges (in objectData coords)
    // of every (SourceFile, Entrypoint) string pair so we can splice them
    // out. Anything goes wrong, leave bytes alone and let KNI surface the
    // real error.
    const removeRanges: Array<[number, number]> = [];
    try {
        const cur = new ByteCursor(od, /* MGFX body starts after */ 14);

        const cbufferCount = cur.readInt32LE();
        for (let c = 0; c < cbufferCount; c++) {
            cur.read7BitPrefixedString();        // name
            cur.readUint16LE();                  // sizeInBytes (int16)
            const paramCount = cur.readInt32LE();
            for (let p = 0; p < paramCount; p++) {
                cur.readInt32LE();               // paramIdx
                cur.readUint16LE();              // offset
            }
        }

        const shaderCount = cur.readInt32LE();
        for (let s = 0; s < shaderCount; s++) {
            cur.readUint8();                     // isVertexShader bool
            const removeStart = cur.offset;
            cur.read7BitPrefixedString();        // SourceFile (v11)
            cur.read7BitPrefixedString();        // Entrypoint (v11)
            removeRanges.push([removeStart, cur.offset]);

            const shaderLength = cur.readInt32LE();
            cur.readBytes(shaderLength);         // bytecode

            const samplerCount = cur.readUint8();
            for (let i = 0; i < samplerCount; i++) {
                cur.readUint8(); cur.readUint8(); cur.readUint8();
                if (cur.readUint8() !== 0) {     // hasState
                    cur.readBytes(20);           // sampler state body
                }
                cur.read7BitPrefixedString();    // name
                cur.readUint8();                 // parameter index
            }

            const cbufRefCount = cur.readUint8();
            cur.readBytes(cbufRefCount);         // cbuffer indices

            const attrCount = cur.readUint8();
            for (let i = 0; i < attrCount; i++) {
                cur.read7BitPrefixedString();    // name
                cur.readUint8();                 // usage
                cur.readUint8();                 // index
                cur.readUint16LE();              // location (int16; sign-extend
                                                  // unused — we just skip)
            }
        }
    } catch {
        return bytes;
    }

    const totalRemoved = removeRanges.reduce((acc, [a, b]) => acc + (b - a), 0);

    // Build the output. Translate objectData-relative ranges to bytes-relative.
    const absRanges = removeRanges.map(([s, e]) => [
        payloadStart + s, payloadStart + e,
    ] as [number, number]);

    const out = new Uint8Array(bytes.length - totalRemoved);
    let inOff = 0;
    let outOff = 0;
    for (const [absStart, absEnd] of absRanges) {
        const span = absStart - inOff;
        out.set(bytes.subarray(inOff, absStart), outOff);
        outOff += span;
        inOff = absEnd;
    }
    out.set(bytes.subarray(inOff), outOff);

    // Patch the MGFX version byte. All our splice ranges sit AFTER this byte,
    // so the position is the same in both input and output.
    out[payloadStart + 8] = MGFX_VERSION_KNI;

    // Patch the EffectReader's `int32 dataSize` prefix (objectData[0..3]).
    writeUint32LE(out, payloadStart, readUint32LE(bytes, payloadStart) - totalRemoved);

    // Patch the XNB header's `uint32 fileSize` (at byte offset 6 of the
    // file). Compressed XNBs would also have a `decompressedSize` at offset
    // 10, but Fade's content pipeline emits with /compress:False, so we don't
    // chase that here — if a future caller pipes a compressed XNB through,
    // the classifier returns objectData=null and we bail at the top.
    writeUint32LE(out, 6, readUint32LE(bytes, 6) - totalRemoved);

    return out;
}

function readUint32LE(b: Uint8Array, off: number): number {
    return (b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24)) >>> 0;
}
function writeUint32LE(b: Uint8Array, off: number, value: number): void {
    b[off]     = value         & 0xFF;
    b[off + 1] = (value >>> 8)  & 0xFF;
    b[off + 2] = (value >>> 16) & 0xFF;
    b[off + 3] = (value >>> 24) & 0xFF;
}

// Route an XNB through the appropriate KNI patcher based on its kind.
// Replaces the chained patchEffectMgfxVersionForKni(patchSoundEffectForKni(raw)) pattern.
export function patchXnbForKni(bytes: Uint8Array): Uint8Array {
    let cls: ReturnType<typeof classifyXnb>;
    try { cls = classifyXnb(bytes); } catch { return bytes; }
    switch (cls.kind) {
        case 'effect':      return patchEffectMgfxVersionForKni(bytes);
        case 'sound-effect': return patchSoundEffectForKni(bytes);
        default:             return bytes;
    }
}

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
