// Pure-JS block-compression + small-format encoders for Texture2D XNBs.
//
// Formats implemented here are the four the playground macro surface
// exposes today (BC1, BC3, Alpha8, Bgra4444). All four operate on the
// same RGBA8 input the canvas getImageData hands us; each writes a
// different byte payload that the corresponding XNB SurfaceFormat
// header value (4, 6, 12, 3) points at.
//
// Quality vs. rgbcx / bc7enc: we use simple bounding-box endpoint
// selection, no rate-distortion search, no iterative refinement. Plenty
// good for the "drop a sprite, see it work" loop. Swap in a fancier
// encoder later behind the same exported interface — none of the
// callers (xnb-writer.ts) inspect the algorithm.
//
// Block dimensions: BC1/BC3 require source dimensions that are multiples
// of 4. The encoders here pad partial blocks at the right/bottom edge by
// replicating the last valid pixel — same trick MGCB uses — so non-
// multiple-of-4 textures encode without a ragged GPU read. The XNB still
// stores the original Width/Height so UV math at the GPU matches.

// ─── Helpers ─────────────────────────────────────────────────────────

type Pixels = Uint8Array | Uint8ClampedArray;

interface Block16 {
    r: number[]; g: number[]; b: number[]; a: number[];
}

/** Read a 4×4 block starting at (bx*4, by*4). Pixels past the image
 *  bounds clamp to the last valid row/column (edge replication). */
function readBlock(rgba: Pixels, w: number, h: number, bx: number, by: number): Block16 {
    const r = new Array<number>(16);
    const g = new Array<number>(16);
    const b = new Array<number>(16);
    const a = new Array<number>(16);
    for (let py = 0; py < 4; py++) {
        const y = Math.min(by * 4 + py, h - 1);
        for (let px = 0; px < 4; px++) {
            const x = Math.min(bx * 4 + px, w - 1);
            const i = (y * w + x) * 4;
            const k = py * 4 + px;
            r[k] = rgba[i];
            g[k] = rgba[i + 1];
            b[k] = rgba[i + 2];
            a[k] = rgba[i + 3];
        }
    }
    return { r, g, b, a };
}

/** Pack 8-bit RGB into RGB565 (uint16). */
function packRgb565(r: number, g: number, b: number): number {
    return ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
}

/** Unpack RGB565 back to 8-bit per channel using the standard
 *  bit-replication expansion (5→8: top 3 bits replicate into low bits;
 *  6→8: top 2 bits replicate). This is what the GPU does on decode, so
 *  using it for index selection minimises the error visible at runtime. */
function unpackRgb565(c: number): { r: number; g: number; b: number } {
    const r5 = (c >> 11) & 0x1F;
    const g6 = (c >> 5) & 0x3F;
    const b5 = c & 0x1F;
    return {
        r: (r5 << 3) | (r5 >> 2),
        g: (g6 << 2) | (g6 >> 4),
        b: (b5 << 3) | (b5 >> 2),
    };
}

function writeUint16LE(buf: Uint8Array, offset: number, value: number) {
    buf[offset] = value & 0xFF;
    buf[offset + 1] = (value >> 8) & 0xFF;
}

// ─── BC1 / BC3 shared color block ─────────────────────────────────────

/** Encode one 4×4 color block as 8 bytes of BC1 data. `onebitAlphaMode`
 *  flips the endpoint ordering so the 4th index slot becomes transparent
 *  — BC1's only way to represent any alpha at all. */
function encodeColorBlock(block: Block16, onebitAlphaMode: boolean): Uint8Array {
    const { r, g, b, a } = block;

    // Endpoint skip threshold depends on the mode we're encoding for:
    //   BC1 1-bit-alpha (onebitAlphaMode=true): skip α<128. Matches the
    //     GPU's transparency cutoff for the 3-color+transparent path —
    //     anything below that gets index 3 and is rendered as fully
    //     transparent, so its RGB shouldn't drag the palette.
    //   BC3 / BC1 opaque (onebitAlphaMode=false): skip only α=0. PNG
    //     decoders zero RGB on fully-transparent pixels, so they'd pull
    //     the bounding box toward (0,0,0). Partial-alpha edge pixels,
    //     though, carry real anti-aliased color the user sees through
    //     the alpha channel — they need to participate in the palette.
    const alphaSkipThreshold = onebitAlphaMode ? 128 : 1;

    let minR = 256, minG = 256, minB = 256, maxR = -1, maxG = -1, maxB = -1;
    for (let i = 0; i < 16; i++) {
        if (a[i] < alphaSkipThreshold) continue;
        const rr = r[i], gg = g[i], bb = b[i];
        if (rr < minR) minR = rr;
        if (gg < minG) minG = gg;
        if (bb < minB) minB = bb;
        if (rr > maxR) maxR = rr;
        if (gg > maxG) maxG = gg;
        if (bb > maxB) maxB = bb;
    }
    // Fully-transparent block: fall back to black endpoints.
    if (maxR < 0) { minR = maxR = 0; minG = maxG = 0; minB = maxB = 0; }

    const cMax = packRgb565(maxR, maxG, maxB);
    const cMin = packRgb565(minR, minG, minB);

    let c0: number, c1: number;
    if (onebitAlphaMode) {
        // 3-color + transparent mode requires c0 <= c1. When the
        // quantised endpoints collide we keep c0=c1 (single-color block,
        // every opaque pixel maps to index 0).
        c0 = Math.min(cMin, cMax);
        c1 = Math.max(cMin, cMax);
    } else {
        // 4-color mode REQUIRES c0 > c1 by the BCn spec. When the source
        // block is a single colour (cMin === cMax — common on a sprite's
        // solid-colour interior), the equal endpoints would tell the GPU
        // to fall into the 3-color + transparent path on strict decoders
        // (which is what KNI/WebGL's BC3 implementation does). That made
        // interior BC3 blocks render as grey/black rectangles inside an
        // otherwise-yellow body. Force a 1-unit RGB565 gap so the GPU
        // stays in 4-color mode — the resulting tiny gradient between
        // the two palette endpoints is invisible at byte-precision.
        c0 = Math.max(cMin, cMax);
        c1 = Math.min(cMin, cMax);
        if (c0 === c1) {
            if (c1 > 0) c1 -= 1;
            else c0 += 1;
        }
    }

    // Build the 4-color palette at the same precision the GPU sees.
    const p = unpackRgb565(c0);
    const q = unpackRgb565(c1);
    const palette: number[][] = [
        [p.r, p.g, p.b],
        [q.r, q.g, q.b],
        [0, 0, 0],
        [0, 0, 0],
    ];
    const fourColor = !onebitAlphaMode || c0 > c1;
    if (fourColor) {
        palette[2] = [
            Math.round((2 * p.r + q.r) / 3),
            Math.round((2 * p.g + q.g) / 3),
            Math.round((2 * p.b + q.b) / 3),
        ];
        palette[3] = [
            Math.round((p.r + 2 * q.r) / 3),
            Math.round((p.g + 2 * q.g) / 3),
            Math.round((p.b + 2 * q.b) / 3),
        ];
    } else {
        // 3-color: index 2 is the midpoint, index 3 means transparent.
        palette[2] = [
            Math.round((p.r + q.r) / 2),
            Math.round((p.g + q.g) / 2),
            Math.round((p.b + q.b) / 2),
        ];
        // palette[3] is unused — the GPU treats index 3 specially.
    }

    // Per-pixel nearest-index in RGB squared distance.
    const indices = new Array<number>(16);
    const maxIdx = fourColor ? 4 : 3;
    for (let i = 0; i < 16; i++) {
        if (onebitAlphaMode && a[i] < 128) { indices[i] = 3; continue; }
        let bestDist = Infinity;
        let bestIdx = 0;
        for (let j = 0; j < maxIdx; j++) {
            const dr = r[i] - palette[j][0];
            const dg = g[i] - palette[j][1];
            const db = b[i] - palette[j][2];
            const dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist) { bestDist = dist; bestIdx = j; }
        }
        indices[i] = bestIdx;
    }

    let packed = 0;
    for (let i = 0; i < 16; i++) packed |= (indices[i] & 3) << (i * 2);

    const out = new Uint8Array(8);
    writeUint16LE(out, 0, c0);
    writeUint16LE(out, 2, c1);
    out[4] = packed & 0xFF;
    out[5] = (packed >>> 8) & 0xFF;
    out[6] = (packed >>> 16) & 0xFF;
    out[7] = (packed >>> 24) & 0xFF;
    return out;
}

// ─── BC3 alpha block (8 bytes, 3-bit indices) ────────────────────────

function encodeAlphaBlockBC3(a: number[]): Uint8Array {
    let aMin = 255, aMax = 0;
    for (let i = 0; i < 16; i++) {
        if (a[i] < aMin) aMin = a[i];
        if (a[i] > aMax) aMax = a[i];
    }

    // Use 8-interp mode (a0 > a1). The 6-interp + 0/255 special mode is
    // marginally better for blocks containing both 0 and 255 exactly,
    // but the simpler choice is competitive and easier to reason about.
    const a0 = aMax;
    const a1 = aMin;

    const palette = new Array<number>(8);
    if (a0 > a1) {
        palette[0] = a0;
        palette[1] = a1;
        for (let i = 1; i <= 6; i++) {
            palette[i + 1] = Math.round(((7 - i) * a0 + i * a1) / 7);
        }
    } else {
        // Constant-alpha block.
        for (let i = 0; i < 8; i++) palette[i] = a0;
    }

    const indices = new Array<number>(16);
    for (let i = 0; i < 16; i++) {
        let bestDist = Infinity;
        let bestIdx = 0;
        for (let j = 0; j < 8; j++) {
            const d = Math.abs(a[i] - palette[j]);
            if (d < bestDist) { bestDist = d; bestIdx = j; }
        }
        indices[i] = bestIdx;
    }

    // Pack: 16 × 3-bit = 48 bits = 6 bytes, little-endian, lowest-index
    // bits first. Use BigInt-free buffered packing.
    const out = new Uint8Array(8);
    out[0] = a0;
    out[1] = a1;
    let buf = 0;
    let bits = 0;
    let outIdx = 2;
    for (let i = 0; i < 16; i++) {
        buf |= (indices[i] & 7) << bits;
        bits += 3;
        while (bits >= 8) {
            out[outIdx++] = buf & 0xFF;
            buf >>>= 8;
            bits -= 8;
        }
    }
    // 16×3=48 bits flush cleanly; no trailing partial byte.
    return out;
}

// ─── Public encoders ─────────────────────────────────────────────────

function blocksFor(w: number, h: number): { bw: number; bh: number } {
    return { bw: Math.ceil(w / 4), bh: Math.ceil(h / 4) };
}

/** Compute the DXT byte size for a (w, h) image. Useful for the XNB
 *  DataSize field; callers in xnb-writer use it without re-deriving. */
export function bc1ByteSize(w: number, h: number): number {
    const { bw, bh } = blocksFor(w, h);
    return bw * bh * 8;
}
export function bc3ByteSize(w: number, h: number): number {
    const { bw, bh } = blocksFor(w, h);
    return bw * bh * 16;
}
export function alpha8ByteSize(w: number, h: number): number { return w * h; }
export function bgra4444ByteSize(w: number, h: number): number { return w * h * 2; }

export function encodeBC1(rgba: Pixels, w: number, h: number): Uint8Array {
    // Decide 1-bit alpha mode by inspecting the source for any pixel
    // below half-transparency. BC1 can't carry partial alpha — callers
    // wanting full alpha gradients should pick BC3 instead.
    let needsAlpha = false;
    for (let i = 3; i < rgba.length; i += 4) {
        if (rgba[i] < 128) { needsAlpha = true; break; }
    }
    const { bw, bh } = blocksFor(w, h);
    const out = new Uint8Array(bw * bh * 8);
    let off = 0;
    for (let by = 0; by < bh; by++) {
        for (let bx = 0; bx < bw; bx++) {
            const block = readBlock(rgba, w, h, bx, by);
            const enc = encodeColorBlock(block, needsAlpha);
            out.set(enc, off);
            off += 8;
        }
    }
    return out;
}

export function encodeBC3(rgba: Pixels, w: number, h: number): Uint8Array {
    const { bw, bh } = blocksFor(w, h);
    const out = new Uint8Array(bw * bh * 16);
    let off = 0;
    for (let by = 0; by < bh; by++) {
        for (let bx = 0; bx < bw; bx++) {
            const block = readBlock(rgba, w, h, bx, by);
            const alpha = encodeAlphaBlockBC3(block.a);
            // BC3 carries alpha separately, so the color block always
            // runs in 4-color mode (no transparent index).
            const color = encodeColorBlock(block, /*onebitAlphaMode*/ false);
            out.set(alpha, off);
            out.set(color, off + 8);
            off += 16;
        }
    }
    return out;
}

/** Single-channel alpha texture. Writes the source alpha byte for each
 *  pixel — luminance is intentionally ignored. If the source is fully
 *  opaque the result will be a solid white mask; the macro user almost
 *  certainly meant to upload a transparent PNG instead. */
export function encodeAlpha8(rgba: Pixels, w: number, h: number): Uint8Array {
    const out = new Uint8Array(w * h);
    for (let i = 0, j = 3; i < out.length; i++, j += 4) out[i] = rgba[j];
    return out;
}

/** 12-bit color + 4-bit alpha, packed little-endian as
 *  `(A4 << 12) | (R4 << 8) | (G4 << 4) | B4`. Matches MonoGame's
 *  Bgra4444 IPackedVector exactly. */
export function encodeBgra4444(rgba: Pixels, w: number, h: number): Uint8Array {
    const out = new Uint8Array(w * h * 2);
    let outIdx = 0;
    for (let i = 0; i < rgba.length; i += 4) {
        const r4 = rgba[i] >> 4;
        const g4 = rgba[i + 1] >> 4;
        const b4 = rgba[i + 2] >> 4;
        const a4 = rgba[i + 3] >> 4;
        const packed = (a4 << 12) | (r4 << 8) | (g4 << 4) | b4;
        out[outIdx++] = packed & 0xFF;
        out[outIdx++] = (packed >> 8) & 0xFF;
    }
    return out;
}
