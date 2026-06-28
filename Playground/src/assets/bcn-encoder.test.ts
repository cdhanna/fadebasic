import { describe, it, expect } from 'vitest';
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

function solidBlock(r: number, g: number, b: number, a: number, w = 4, h = 4): Uint8Array {
    const out = new Uint8Array(w * h * 4);
    for (let i = 0; i < out.length; i += 4) {
        out[i] = r; out[i + 1] = g; out[i + 2] = b; out[i + 3] = a;
    }
    return out;
}

function readUint16LE(buf: Uint8Array, off: number): number {
    return buf[off] | (buf[off + 1] << 8);
}

describe('byte-size helpers', () => {
    it('BC1 is 8 bytes/block (aligned)', () => {
        expect(bc1ByteSize(4, 4)).toBe(8);
        expect(bc1ByteSize(16, 16)).toBe(16 * 8);   // 4×4 blocks
        expect(bc1ByteSize(8, 16)).toBe(8 * 8);     // 2×4 blocks
    });
    it('BC1 rounds up for non-multiples of 4', () => {
        expect(bc1ByteSize(5, 5)).toBe(2 * 2 * 8);  // ceil(5/4) = 2
    });
    it('BC3 is 16 bytes/block', () => {
        expect(bc3ByteSize(4, 4)).toBe(16);
        expect(bc3ByteSize(16, 16)).toBe(16 * 16);
    });
    it('Alpha8 is 1 byte/pixel', () => {
        expect(alpha8ByteSize(10, 10)).toBe(100);
    });
    it('Bgra4444 is 2 bytes/pixel', () => {
        expect(bgra4444ByteSize(7, 5)).toBe(70);
    });
});

describe('encodeBC1', () => {
    it('emits one 8-byte block per 4×4 region', () => {
        const out = encodeBC1(solidBlock(255, 0, 0, 255), 4, 4);
        expect(out.length).toBe(8);
    });

    it('handles non-aligned dimensions via edge padding', () => {
        const out = encodeBC1(solidBlock(0, 255, 0, 255, 5, 5), 5, 5);
        expect(out.length).toBe(2 * 2 * 8);          // 4 blocks total
    });

    it('uses 4-color mode for fully-opaque blocks (c0 > c1)', () => {
        // Two-tone red/blue checkerboard at full alpha — needs interpolation.
        const w = 4, h = 4;
        const rgba = new Uint8Array(w * h * 4);
        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                const i = (y * w + x) * 4;
                const red = (x + y) % 2 === 0;
                rgba[i] = red ? 255 : 0;
                rgba[i + 1] = 0;
                rgba[i + 2] = red ? 0 : 255;
                rgba[i + 3] = 255;
            }
        }
        const out = encodeBC1(rgba, w, h);
        const c0 = readUint16LE(out, 0);
        const c1 = readUint16LE(out, 2);
        expect(c0).toBeGreaterThan(c1);              // 4-color mode invariant
    });

    it('guarantees c0 > c1 for fully-opaque solid-colour blocks (so the GPU stays in 4-colour mode)', () => {
        // Solid yellow opaque block — historically came out with c0 == c1,
        // which strict BC3/BC1 decoders interpret as 3-color + transparent
        // and render index 3 as opaque grey.
        const w = 4, h = 4;
        const rgba = new Uint8Array(w * h * 4);
        for (let i = 0; i < rgba.length; i += 4) {
            rgba[i] = 240; rgba[i + 1] = 200; rgba[i + 2] = 80; rgba[i + 3] = 255;
        }
        const out = encodeBC1(rgba, w, h);
        const c0 = readUint16LE(out, 0);
        const c1 = readUint16LE(out, 2);
        expect(c0).toBeGreaterThan(c1);
    });

    it('flips to 3-color+transparent mode when any pixel is fully transparent', () => {
        const rgba = solidBlock(255, 255, 255, 255);
        // Knock pixel 0 transparent.
        rgba[3] = 0;
        const out = encodeBC1(rgba, 4, 4);
        const c0 = readUint16LE(out, 0);
        const c1 = readUint16LE(out, 2);
        expect(c0).toBeLessThanOrEqual(c1);          // 3-color mode invariant
        // Pixel 0's 2-bit index should be 3 (transparent slot).
        const idxByte = out[4];
        expect(idxByte & 0b11).toBe(0b11);
    });
});

describe('encodeBC3', () => {
    it('emits 16 bytes per 4×4 block', () => {
        const out = encodeBC3(solidBlock(0, 0, 0, 200), 4, 4);
        expect(out.length).toBe(16);
    });

    it('records min/max alpha in the first two bytes of the alpha block', () => {
        // Build a block where alpha varies 0..255 in a known pattern.
        const w = 4, h = 4;
        const rgba = new Uint8Array(w * h * 4);
        for (let i = 0; i < 16; i++) {
            rgba[i * 4] = 128;
            rgba[i * 4 + 1] = 128;
            rgba[i * 4 + 2] = 128;
            rgba[i * 4 + 3] = i === 0 ? 0 : i === 15 ? 255 : 128;
        }
        const out = encodeBC3(rgba, w, h);
        const aMax = out[0];
        const aMin = out[1];
        expect(aMax).toBe(255);
        expect(aMin).toBe(0);
    });
});

describe('encodeAlpha8', () => {
    it('emits one byte per pixel from the alpha channel', () => {
        const w = 4, h = 4;
        const rgba = new Uint8Array(w * h * 4);
        for (let i = 0; i < 16; i++) {
            rgba[i * 4 + 3] = i * 16; // 0, 16, 32, …
        }
        const out = encodeAlpha8(rgba, w, h);
        expect(out.length).toBe(16);
        for (let i = 0; i < 16; i++) expect(out[i]).toBe(i * 16);
    });
});

describe('encodeBgra4444', () => {
    it('packs RGBA → 16-bit LE with the MonoGame Bgra4444 layout', () => {
        // (R=0xF0 G=0x80 B=0x10 A=0xFF) → top-nibbles (F, 8, 1, F).
        // Packed: (A<<12)|(R<<8)|(G<<4)|B = (F<<12)|(F<<8)|(8<<4)|1 = 0xFF81
        const rgba = new Uint8Array([0xF0, 0x80, 0x10, 0xFF]);
        const out = encodeBgra4444(rgba, 1, 1);
        expect(out.length).toBe(2);
        // Little-endian: low byte first.
        expect(out[0]).toBe(0x81);
        expect(out[1]).toBe(0xFF);
    });
});
