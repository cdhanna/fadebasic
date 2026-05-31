// Image → XNB encoder. Produces the same wire shape that Fade's content
// pipeline ships (DesktopGL platform, format version 5, uncompressed)
// and that the KNI/MonoGame runtime's Texture2DReader expects.
//
// File layout we emit (uncompressed only — no LZ4/LZX yet):
//
//   'XNB'                    3 bytes
//   platform byte            'd' (DesktopGL — matches build-runtime.mjs output)
//   format version           5
//   flags                    0  (HiDef off, no compression)
//   uint32 LE  fileSize      total bytes including header
//
//   varint                   reader count (1)
//   varlen UTF-8 string      "Microsoft.Xna.Framework.Content.Texture2DReader"
//   int32  reader version    0
//   varint                   shared resource count (0)
//   varint                   root object type id (1)
//
//   int32   SurfaceFormat    0 = Color, 3 = Bgra4444, 4 = Dxt1, 6 = Dxt5, 12 = Alpha8
//   int32   Width
//   int32   Height
//   int32   MipCount         1 (no mip chain for the playground path)
//   int32   DataSize         depends on format (see bytesForFormat below)
//   bytes   pixel data       format-dependent payload from bcn-encoder.ts
//
// On the byte order for Color: SurfaceFormat.Color is documented as ARGB
// but MonoGame's `Color` struct packs little-endian as
// `r | (g<<8) | (b<<16) | (a<<24)`, which lays out R,G,B,A in memory. The
// canvas getImageData hands us the same byte order, so we copy straight
// through — no swap. Writing BGRA here makes textures appear with R and
// B swapped (yellow → blue).

import {
    encodeBC1,
    encodeBC3,
    encodeAlpha8,
    encodeBgra4444,
    bc1ByteSize,
    bc3ByteSize,
    alpha8ByteSize,
    bgra4444ByteSize,
} from './bcn-encoder';

// Encoder-output formats the writer can actually serialise. Distinct
// from `TextureCompression` (which includes 'auto') so callers must
// resolve 'auto' to a concrete format before reaching the writer.
export type ConcreteTextureFormat = 'color' | 'dxt1' | 'dxt3' | 'dxt5' | 'alpha8' | 'bgra4444';

export interface EncodeImageOptions {
    /** RGBA pixel data straight out of `OffscreenCanvas.getImageData`. */
    rgba: Uint8ClampedArray | Uint8Array;
    width: number;
    height: number;
    format: ConcreteTextureFormat;
}

// KNI / MonoGame look up readers by the bare type name without an
// assembly-qualified suffix. The full `Microsoft.Xna.Framework.Content.
// Texture2DReader, Microsoft.Xna.Framework, Version=…` form (which XNA
// historically used) fails on KNI because its readers live in a
// different assembly identity — see the existing Fade-shipped XNBs at
// Fade.MonoGame/.../Content/Fish/Textures/*.xnb for the on-disk format
// that ContentTypeReader resolution actually accepts.
const TEXTURE_2D_READER =
    'Microsoft.Xna.Framework.Content.Texture2DReader';

// XNB SurfaceFormat enum values. Match KNI's enum (NOT MonoGame's — they
// diverge at 20+, where MonoGame has Dxt1a and KNI has Bgr32). KNI does
// NOT expose a Dxt1a value, so BC1 with 1-bit alpha can't be represented
// on KNI's GPU upload path. Alpha-aware BC1 requests are upgraded to BC3
// (Dxt5) one layer up in compile-assets.ts so this writer never has to
// emit a tag that KNI would reject as "Bgr32 is not supported".
const SURFACE_FORMAT: Record<ConcreteTextureFormat, number> = {
    color: 0,
    bgra4444: 3,
    dxt1: 4,
    dxt3: 5,
    dxt5: 6,
    alpha8: 12,
};

/** Convert source RGBA into the on-disk byte payload for the requested
 *  format. Always allocates a fresh Uint8Array — the caller appends it
 *  to the XNB tail without further copies. */
function encodePayload(opts: EncodeImageOptions): Uint8Array {
    const { rgba, width, height, format } = opts;
    switch (format) {
        case 'color': {
            const out = new Uint8Array(width * height * 4);
            // SurfaceFormat.Color is RGBA8 in memory, exactly what
            // getImageData hands back. Copy straight through.
            if (rgba instanceof Uint8Array) {
                out.set(rgba.subarray(0, out.length));
            } else {
                for (let i = 0; i < out.length; i++) out[i] = rgba[i];
            }
            return out;
        }
        case 'bgra4444': return encodeBgra4444(rgba, width, height);
        case 'alpha8':   return encodeAlpha8(rgba, width, height);
        case 'dxt1':     return encodeBC1(rgba, width, height);
        case 'dxt3':
            // DXT3 (BC2) isn't implemented separately — its color block
            // is identical to BC1's 4-color mode and its alpha quality
            // is strictly worse than DXT5 for anti-aliased edges. Macro
            // callers asking for dxt3 get DXT5 instead, transparently;
            // compile-assets.ts logs a diagnostic so the substitution
            // is visible. The header still writes SurfaceFormat=6.
            return encodeBC3(rgba, width, height);
        case 'dxt5':     return encodeBC3(rgba, width, height);
    }
}

function byteSizeForFormat(format: ConcreteTextureFormat, w: number, h: number): number {
    switch (format) {
        case 'color':    return w * h * 4;
        case 'bgra4444': return bgra4444ByteSize(w, h);
        case 'alpha8':   return alpha8ByteSize(w, h);
        case 'dxt1':     return bc1ByteSize(w, h);
        case 'dxt3':     // dxt3 macro alias → dxt5 payload (see encodePayload)
        case 'dxt5':     return bc3ByteSize(w, h);
    }
}

function surfaceFormatFor(format: ConcreteTextureFormat): number {
    // dxt3 macro alias maps to dxt5 wire format; everything else is
    // one-to-one with the SURFACE_FORMAT table.
    if (format === 'dxt3') return SURFACE_FORMAT.dxt5;
    return SURFACE_FORMAT[format];
}

export function encodeTexture2dXnb(opts: EncodeImageOptions): Uint8Array {
    const { width, height, format } = opts;
    if (width <= 0 || height <= 0) {
        throw new Error(`xnb-writer: invalid dimensions ${width}×${height}`);
    }
    if (!(format in SURFACE_FORMAT)) {
        throw new Error(`xnb-writer: unknown format '${format}'`);
    }

    const payload = encodePayload(opts);
    const dataSize = byteSizeForFormat(format, width, height);
    if (payload.length !== dataSize) {
        // The encoder said one size and produced another. Refuse to ship
        // the XNB rather than write a header that disagrees with the
        // tail — KNI would silently read past the data.
        throw new Error(
            `xnb-writer: ${format} payload is ${payload.length} bytes but ` +
            `DataSize would be ${dataSize}.`,
        );
    }

    // Encode the reader-chain section into a temp buffer so we can prefix
    // a correct 7-bit-varint length for the type-reader string. Keeping
    // it separate from the header also keeps the fileSize patch trivial.
    const readerName = utf8Encode(TEXTURE_2D_READER);
    const meta: number[] = [];
    write7BitInt(meta, 1);                       // reader count
    write7BitInt(meta, readerName.length);       // string length
    for (const b of readerName) meta.push(b);
    pushInt32LE(meta, 0);                         // reader version
    write7BitInt(meta, 0);                        // shared resource count
    write7BitInt(meta, 1);                        // root object type id

    pushInt32LE(meta, surfaceFormatFor(format));  // SurfaceFormat
    pushInt32LE(meta, width);
    pushInt32LE(meta, height);
    pushInt32LE(meta, 1);                         // MipCount
    pushInt32LE(meta, dataSize);                  // DataSize

    const headerSize = 10;                        // magic + platform + version + flags + fileSize
    const fileSize = headerSize + meta.length + payload.length;

    const out = new Uint8Array(fileSize);
    out[0] = 0x58; // 'X'
    out[1] = 0x4E; // 'N'
    out[2] = 0x42; // 'B'
    out[3] = 0x64; // 'd' DesktopGL (parity with the existing fade-shipped XNBs)
    out[4] = 5;    // format version
    out[5] = 0;    // flags (no HiDef, no compression)
    writeUint32LE(out, 6, fileSize);

    let offset = headerSize;
    for (let i = 0; i < meta.length; i++) out[offset++] = meta[i];
    out.set(payload, offset);
    return out;
}

// ─── 7-bit varint + LE helpers ─────────────────────────────────────────
// Same routines as ByteCursor in xnb-reader.ts, on the write side. Keeping
// them local so xnb-reader stays read-only and importing this module
// doesn't pull in OPFS, the cache, etc.

function write7BitInt(out: number[], value: number) {
    let v = value >>> 0;
    while (v >= 0x80) {
        out.push((v & 0x7F) | 0x80);
        v >>>= 7;
    }
    out.push(v & 0x7F);
}

function pushInt32LE(out: number[], value: number) {
    out.push(value & 0xFF, (value >>> 8) & 0xFF, (value >>> 16) & 0xFF, (value >>> 24) & 0xFF);
}

function writeUint32LE(buf: Uint8Array, offset: number, value: number) {
    buf[offset]     = value & 0xFF;
    buf[offset + 1] = (value >>> 8) & 0xFF;
    buf[offset + 2] = (value >>> 16) & 0xFF;
    buf[offset + 3] = (value >>> 24) & 0xFF;
}

const utf8 = new TextEncoder();
function utf8Encode(s: string): Uint8Array { return utf8.encode(s); }
