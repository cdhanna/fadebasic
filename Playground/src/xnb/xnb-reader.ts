// XNB (XNA Binary) file parser. Pure header + reader-chain parser — no
// payload decoders live here. Each format-specific decoder (Texture2D,
// SoundEffect, …) sits in xnb-previews.ts and is fed the post-reader-chain
// data slice that classifyXnb produces.
//
// File layout (XNA Game Studio 4.0 / MonoGame "format version 5"):
//
//   'XNB'           3 bytes
//   platform        1 byte   ('w' Windows, 'd' DesktopGL, 'x' Xbox360, …)
//   format version  1 byte   (5 for XNA 4.0 / MonoGame)
//   flags           1 byte   bit 0 (0x01) HiDef profile,
//                            bit 6 (0x40) LZ4 compressed (MonoGame extension),
//                            bit 7 (0x80) LZX compressed (legacy XNA).
//   file size       uint32 LE  (total file size including header)
//   [decomp size]   uint32 LE  (only when any compression flag is set)
//   payload         remainder, possibly compressed
//
// Payload, after decompression if needed:
//
//   varint   reader count N
//   N × (varlenstr type-reader name, int32 reader version)
//   varint   shared-resource count
//   varint   root object type id  (1-based index into reader array; 0 = null)
//   bytes    object data, written by reader[rootTypeId - 1]
//
// We don't bundle a LZ4 decompressor yet — compressed XNBs surface a
// label-only preview. Fade's own pipeline emits with /compress:False (see
// Fade.MonoGame/Content/Content.mgcb) so its outputs parse directly; user-
// uploaded XNBs from other tools may not.

export type XnbKind =
    | 'texture2d'
    | 'texture3d'
    | 'texture-cube'
    | 'sound-effect'
    | 'song'
    | 'effect'
    | 'sprite-font'
    | 'model'
    | 'video'
    | 'unknown';

// Single-byte platform tag XNA/MonoGame writes after the magic. Friendly
// labels surface in the preview header; unknowns fall back to a hex byte.
export const XNB_PLATFORM_LABELS: Record<string, string> = {
    w: 'Windows (XNA)',
    m: 'Windows Phone',
    x: 'Xbox 360',
    d: 'DesktopGL',
    X: 'macOS',
    i: 'iOS',
    a: 'Android',
    P: 'PlayStation Mobile',
    M: 'Windows Store',
    r: 'Raspberry Pi',
    l: 'Linux',
    b: 'Blazor',
};

export interface XnbHeader {
    platform: string;
    platformLabel: string;
    formatVersion: number;
    flags: number;
    isHiDef: boolean;
    isLz4: boolean;
    isLzx: boolean;
    fileSize: number;
    decompressedSize: number | null;
    payloadOffset: number;
}

export interface XnbReaderEntry {
    rawName: string;
    shortName: string;
    version: number;
}

export interface XnbClassification {
    header: XnbHeader;
    kind: XnbKind;
    rootReader: XnbReaderEntry | null;
    readers: XnbReaderEntry[];
    rootObjectTypeId: number;
    sharedResourceCount: number;
    // Slice of the payload starting at the root object's data — what each
    // format-specific decoder consumes. Null when compression is enabled
    // or the reader chain itself couldn't be parsed.
    objectData: Uint8Array | null;
    parseError?: string;
}

export class XnbParseError extends Error {}

export function parseXnbHeader(bytes: Uint8Array): XnbHeader {
    if (bytes.length < 10) {
        throw new XnbParseError('File is too small to be an XNB (needs at least 10 bytes).');
    }
    if (bytes[0] !== 0x58 || bytes[1] !== 0x4E || bytes[2] !== 0x42) {
        throw new XnbParseError('Magic bytes are not "XNB".');
    }
    const platformByte = bytes[3];
    const platform = String.fromCharCode(platformByte);
    const platformLabel =
        XNB_PLATFORM_LABELS[platform] ??
        `Unknown (0x${platformByte.toString(16).padStart(2, '0')})`;
    const formatVersion = bytes[4];
    const flags = bytes[5];
    // MonoGame's ContentCompiler emits HiDef as bit 0, LZ4 as bit 6, LZX as
    // bit 7. Earlier we had bit 7 mapped to HiDef which made HiDef-profile
    // uncompressed XNBs (like catfish.xnb) look LZX-compressed.
    const isHiDef = (flags & 0x01) !== 0;
    const isLz4 = (flags & 0x40) !== 0;
    const isLzx = (flags & 0x80) !== 0;
    const fileSize = readUint32LE(bytes, 6);
    let payloadOffset = 10;
    let decompressedSize: number | null = null;
    if (isLz4 || isLzx) {
        if (bytes.length < 14) {
            throw new XnbParseError('File header is truncated (missing decompressed size).');
        }
        decompressedSize = readUint32LE(bytes, 10);
        payloadOffset = 14;
    }
    return {
        platform,
        platformLabel,
        formatVersion,
        flags,
        isHiDef,
        isLz4,
        isLzx,
        fileSize,
        decompressedSize,
        payloadOffset,
    };
}

export function classifyXnb(bytes: Uint8Array): XnbClassification {
    const header = parseXnbHeader(bytes);
    const base: XnbClassification = {
        header,
        kind: 'unknown',
        rootReader: null,
        readers: [],
        rootObjectTypeId: 0,
        sharedResourceCount: 0,
        objectData: null,
    };
    if (header.isLz4 || header.isLzx) {
        return {
            ...base,
            parseError:
                'Compressed XNBs cannot yet be inspected (LZ4/LZX decompressor not bundled).',
        };
    }
    try {
        const cursor = new ByteCursor(bytes, header.payloadOffset);
        const readerCount = cursor.read7BitInt();
        const readers: XnbReaderEntry[] = [];
        for (let i = 0; i < readerCount; i++) {
            const rawName = cursor.read7BitPrefixedString();
            const version = cursor.readInt32LE();
            readers.push({ rawName, shortName: shortReaderName(rawName), version });
        }
        const sharedResourceCount = cursor.read7BitInt();
        const rootObjectTypeId = cursor.read7BitInt();
        const rootReader =
            rootObjectTypeId > 0 && rootObjectTypeId <= readers.length
                ? readers[rootObjectTypeId - 1]
                : null;
        const objectData = bytes.subarray(cursor.offset);
        return {
            ...base,
            kind: classifyByReaderName(rootReader?.rawName ?? ''),
            rootReader,
            readers,
            rootObjectTypeId,
            sharedResourceCount,
            objectData,
        };
    } catch (e: any) {
        return { ...base, parseError: e?.message ?? 'XNB payload parse failed' };
    }
}

function shortReaderName(rawName: string): string {
    // Raw form: "Microsoft.Xna.Framework.Content.Texture2DReader, Microsoft.Xna.Framework, Version=4.0.0.0, …"
    const comma = rawName.indexOf(',');
    const head = comma >= 0 ? rawName.slice(0, comma) : rawName;
    const dot = head.lastIndexOf('.');
    return dot >= 0 ? head.slice(dot + 1) : head;
}

function classifyByReaderName(rawName: string): XnbKind {
    if (!rawName) return 'unknown';
    const head = rawName.split(',')[0].trim();
    if (head.endsWith('.Texture2DReader')) return 'texture2d';
    if (head.endsWith('.Texture3DReader')) return 'texture3d';
    if (head.endsWith('.TextureCubeReader')) return 'texture-cube';
    if (head.endsWith('.SoundEffectReader')) return 'sound-effect';
    if (head.endsWith('.SongReader')) return 'song';
    if (head.endsWith('.EffectReader')) return 'effect';
    if (head.endsWith('.SpriteFontReader')) return 'sprite-font';
    if (head.endsWith('.ModelReader')) return 'model';
    if (head.endsWith('.VideoReader')) return 'video';
    return 'unknown';
}

export function kindLabel(kind: XnbKind): string {
    switch (kind) {
        case 'texture2d':    return 'Texture2D';
        case 'texture3d':    return 'Texture3D';
        case 'texture-cube': return 'TextureCube';
        case 'sound-effect': return 'SoundEffect';
        case 'song':         return 'Song';
        case 'effect':       return 'Effect';
        case 'sprite-font':  return 'SpriteFont';
        case 'model':        return 'Model';
        case 'video':        return 'Video';
        case 'unknown':      return 'Unknown';
    }
}

// ─── Low-level cursor + helpers ─────────────────────────────────────────
export class ByteCursor {
    constructor(public readonly bytes: Uint8Array, public offset: number = 0) {}

    require(n: number) {
        if (this.offset + n > this.bytes.length) {
            throw new XnbParseError(
                `Unexpected end of payload at offset ${this.offset} (needed ${n} more bytes).`,
            );
        }
    }

    readUint8(): number {
        this.require(1);
        return this.bytes[this.offset++];
    }
    readUint16LE(): number {
        this.require(2);
        const v = this.bytes[this.offset] | (this.bytes[this.offset + 1] << 8);
        this.offset += 2;
        return v;
    }
    readUint32LE(): number {
        this.require(4);
        const b = this.bytes;
        const i = this.offset;
        const v =
            (b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)) >>> 0;
        this.offset += 4;
        return v;
    }
    readInt32LE(): number {
        this.require(4);
        const b = this.bytes;
        const i = this.offset;
        const v = b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
        this.offset += 4;
        return v;
    }
    readBytes(n: number): Uint8Array {
        this.require(n);
        const out = this.bytes.subarray(this.offset, this.offset + n);
        this.offset += n;
        return out;
    }
    read7BitInt(): number {
        // .NET BinaryReader.Read7BitEncodedInt: low 7 bits each byte, high
        // bit "continue". Max 5 bytes for a 32-bit value.
        let value = 0;
        let shift = 0;
        for (let i = 0; i < 5; i++) {
            const b = this.readUint8();
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) === 0) return value >>> 0;
            shift += 7;
        }
        throw new XnbParseError('Malformed 7-bit varint (more than 5 bytes).');
    }
    read7BitPrefixedString(): string {
        const n = this.read7BitInt();
        const bytes = this.readBytes(n);
        return UTF8.decode(bytes);
    }
}

function readUint32LE(bytes: Uint8Array, offset: number): number {
    return (
        bytes[offset] |
        (bytes[offset + 1] << 8) |
        (bytes[offset + 2] << 16) |
        (bytes[offset + 3] << 24)
    ) >>> 0;
}

const UTF8 = new TextDecoder('utf-8');
