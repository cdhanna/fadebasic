// SpriteFont XNB writer. Wraps a RasterizedFont in the on-disk shape
// MonoGame's `SpriteFontReader` expects, so the existing fade `font`
// and `text` commands light up without any C# changes.
//
// On-disk format (verified against MonoGame's QuartzMS.xnb test
// fixture, see git history of this commit for the python decode):
//
//   XNB header (10 bytes)
//   reader manifest (varint count + per-reader { varint strLen, string, int32 ver })
//   varint sharedResourceCount (0)
//   varint rootObjectTypeId (1)
//   payload (SpriteFontReader.Read consumes):
//     varint(2) + Texture2D fields                — atlas
//     varint(3) + int32 count + Rectangle[count]  — glyphs
//     varint(3) + int32 count + Rectangle[count]  — croppings
//     varint(5) + int32 count + uint16[count]     — chars
//     int32 lineSpacing
//     float spacing
//     varint(7) + int32 count + (float×3)[count]  — kerning (left, width, right)
//     bool hasDefault
//     uint16 defaultChar (if hasDefault)
//
// Reader manifest (the order matters — typeIds are 1-based indices):
//   [1] SpriteFontReader        (root)
//   [2] Texture2DReader
//   [3] ListReader`1[[Rectangle]]
//   [4] RectangleReader         — element binding for [3]
//   [5] ListReader`1[[Char]]
//   [6] CharReader              — element binding for [5]
//   [7] ListReader`1[[Vector3]]
//   [8] Vector3Reader           — element binding for [7]
//
// KNI bare-type-name convention: same fix we use for Texture2DReader.
// Generic syntax uses CLR backtick-arity (`Reader`1`) with the inner
// type wrapped in `[[…]]`.

import type { RasterizedFont } from './font-rasterizer';

// Reader manifest declared in the order they'll be referenced (1-based
// typeIds). KNI resolves ContentTypeReader names with two different
// passes:
//   - Bare names (no assembly) — works for non-generic readers because
//     KNI does a short-name lookup against its built-in reader registry.
//     Texture2DReader, RectangleReader, CharReader, Vector3Reader all
//     resolve fine with no qualifier.
//   - Generic names — KNI parses the `[[...]]` parameter list and runs
//     `Type.GetType(name)` on each inner type, which requires the
//     assembly to be specified explicitly. KNI's assemblies are named
//     `Xna.Framework.*` (the Microsoft.* namespace is kept as a
//     back-compat alias, but the actual DLLs ship under `Xna.Framework`).
//     Char lives in `System.Private.CoreLib` under .NET 8+.
//
// SpriteFontReader is in `Xna.Framework.Graphics` per KNI's layout; left
// bare here so KNI's short-name fallback finds it. If that ever stops
// working it can be qualified to `, Xna.Framework.Graphics`.
const READERS = [
    'Microsoft.Xna.Framework.Content.SpriteFontReader',
    'Microsoft.Xna.Framework.Content.Texture2DReader',
    'Microsoft.Xna.Framework.Content.ListReader`1[[Microsoft.Xna.Framework.Rectangle, Xna.Framework]]',
    'Microsoft.Xna.Framework.Content.RectangleReader',
    'Microsoft.Xna.Framework.Content.ListReader`1[[System.Char, System.Private.CoreLib]]',
    'Microsoft.Xna.Framework.Content.CharReader',
    'Microsoft.Xna.Framework.Content.ListReader`1[[Microsoft.Xna.Framework.Vector3, Xna.Framework]]',
    'Microsoft.Xna.Framework.Content.Vector3Reader',
];
const TYPE_ID_TEXTURE2D = 2;
const TYPE_ID_LIST_RECT = 3;
const TYPE_ID_LIST_CHAR = 5;
const TYPE_ID_LIST_VEC3 = 7;

const SURFACE_FORMAT_COLOR = 0;
const XNB_HEADER_SIZE = 10;

export function encodeSpriteFontXnb(font: RasterizedFont): Uint8Array {
    const out: number[] = [];

    // Reader manifest.
    write7BitInt(out, READERS.length);
    for (const name of READERS) {
        const utf8 = textEncoder.encode(name);
        write7BitInt(out, utf8.length);
        for (const b of utf8) out.push(b);
        pushInt32LE(out, 0);              // reader version
    }
    write7BitInt(out, 0);                  // shared resource count
    write7BitInt(out, 1);                  // root object type id (SpriteFontReader)

    // ── Texture2D atlas ──────────────────────────────────────────────
    write7BitInt(out, TYPE_ID_TEXTURE2D);
    pushInt32LE(out, SURFACE_FORMAT_COLOR);
    pushInt32LE(out, font.atlasWidth);
    pushInt32LE(out, font.atlasHeight);
    pushInt32LE(out, 1);                   // mip count
    pushInt32LE(out, font.atlasRgba.length); // mip 0 data size
    for (let i = 0; i < font.atlasRgba.length; i++) out.push(font.atlasRgba[i]);

    // ── glyphs: List<Rectangle> ──────────────────────────────────────
    write7BitInt(out, TYPE_ID_LIST_RECT);
    pushInt32LE(out, font.glyphs.length);
    for (const g of font.glyphs) {
        pushInt32LE(out, g.glyph.x);
        pushInt32LE(out, g.glyph.y);
        pushInt32LE(out, g.glyph.w);
        pushInt32LE(out, g.glyph.h);
    }

    // ── croppings: List<Rectangle> ───────────────────────────────────
    write7BitInt(out, TYPE_ID_LIST_RECT);
    pushInt32LE(out, font.glyphs.length);
    for (const g of font.glyphs) {
        pushInt32LE(out, g.cropping.x);
        pushInt32LE(out, g.cropping.y);
        pushInt32LE(out, g.cropping.w);
        pushInt32LE(out, g.cropping.h);
    }

    // ── charMap: List<Char> ──────────────────────────────────────────
    // KNI's BinaryReader.ReadChar() decodes UTF-8, not UTF-16 — so each
    // char is 1 byte for ASCII and grows to 2–4 bytes for higher code
    // points. Writing UTF-16 LE here makes the deserializer read every
    // char as TWO chars (`' '`, `'\0'`, `'!'`, `'\0'`, …), which trips
    // SpriteFont's "Character map must be in ascending order" check
    // because `'\0'` interleaves with the real characters.
    write7BitInt(out, TYPE_ID_LIST_CHAR);
    pushInt32LE(out, font.chars.length);
    for (const c of font.chars) {
        const utf8 = textEncoder.encode(c);
        for (let i = 0; i < utf8.length; i++) out.push(utf8[i]);
    }

    // ── lineSpacing, spacing ────────────────────────────────────────
    pushInt32LE(out, font.lineSpacing);
    pushFloat32LE(out, font.spacing);

    // ── kerning: List<Vector3> ──────────────────────────────────────
    write7BitInt(out, TYPE_ID_LIST_VEC3);
    pushInt32LE(out, font.glyphs.length);
    for (const g of font.glyphs) {
        pushFloat32LE(out, g.kerning.x);
        pushFloat32LE(out, g.kerning.y);
        pushFloat32LE(out, g.kerning.z);
    }

    // ── defaultChar ──────────────────────────────────────────────────
    // Same UTF-8-via-ReadChar story as the charMap above — emit as
    // UTF-8 so KNI reads a single character back.
    out.push(font.defaultChar ? 1 : 0);
    if (font.defaultChar) {
        const utf8 = textEncoder.encode(font.defaultChar);
        for (let i = 0; i < utf8.length; i++) out.push(utf8[i]);
    }

    // Build the file with the XNB header prepended.
    const fileSize = XNB_HEADER_SIZE + out.length;
    const buf = new Uint8Array(fileSize);
    buf[0] = 0x58; buf[1] = 0x4E; buf[2] = 0x42;  // 'XNB'
    buf[3] = 0x64;                                 // 'd' DesktopGL
    buf[4] = 5;                                    // format version
    buf[5] = 0;                                    // flags (no HiDef, no compression)
    writeUint32LE(buf, 6, fileSize);
    for (let i = 0; i < out.length; i++) buf[XNB_HEADER_SIZE + i] = out[i];
    return buf;
}

// ─── LE / varint helpers (mirrors xnb-writer.ts's locals) ──────────────

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

const _f32 = new Float32Array(1);
const _u8 = new Uint8Array(_f32.buffer);
function pushFloat32LE(out: number[], value: number) {
    _f32[0] = value;
    out.push(_u8[0], _u8[1], _u8[2], _u8[3]);
}

function writeUint32LE(buf: Uint8Array, offset: number, value: number) {
    buf[offset]     = value & 0xFF;
    buf[offset + 1] = (value >>> 8) & 0xFF;
    buf[offset + 2] = (value >>> 16) & 0xFF;
    buf[offset + 3] = (value >>> 24) & 0xFF;
}

const textEncoder = new TextEncoder();
