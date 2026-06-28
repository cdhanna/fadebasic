import { describe, it, expect } from 'vitest';
import { encodeTexture2dXnb } from './xnb-writer';
import { classifyXnb } from '../xnb/xnb-reader';

describe('encodeTexture2dXnb', () => {
    it('produces an XNB the existing classifier recognises as Texture2D', () => {
        const w = 2, h = 2;
        // 2×2 with one of each: red, green, blue, half-alpha-grey.
        const rgba = new Uint8Array([
            255, 0,   0,   255,
            0,   255, 0,   255,
            0,   0,   255, 255,
            128, 128, 128, 128,
        ]);
        const xnb = encodeTexture2dXnb({ rgba, width: w, height: h, format: 'color' });
        const c = classifyXnb(xnb);
        expect(c.kind).toBe('texture2d');
        expect(c.header.flags & 0xC0).toBe(0);    // not compressed
        expect(c.rootReader?.shortName).toBe('Texture2DReader');
    });

    it('writes pixels straight through as RGBA (SurfaceFormat.Color layout)', () => {
        const rgba = new Uint8Array([10, 20, 30, 40]); // 1×1
        const xnb = encodeTexture2dXnb({ rgba, width: 1, height: 1, format: 'color' });
        // The pixel data sits at the tail of the file (just 4 bytes); the
        // last 4 bytes should match the source RGBA byte order exactly —
        // MonoGame's Color packs as r | (g<<8) | (b<<16) | (a<<24), which
        // is R,G,B,A in little-endian memory.
        const tail = xnb.slice(xnb.length - 4);
        expect(Array.from(tail)).toEqual([10, 20, 30, 40]);
    });

    it('writes a fileSize that matches the buffer length', () => {
        const rgba = new Uint8Array(4 * 4 * 4);
        const xnb = encodeTexture2dXnb({ rgba, width: 4, height: 4, format: 'color' });
        const fileSize = xnb[6] | (xnb[7] << 8) | (xnb[8] << 16) | (xnb[9] << 24);
        expect(fileSize).toBe(xnb.length);
    });

    it('stamps SurfaceFormat=Dxt1 (4) for plain BC1 (compile-assets upgrades transparent BC1 to BC3 one layer up)', () => {
        const w = 4, h = 4;
        const rgba = new Uint8Array(w * h * 4);
        for (let i = 0; i < rgba.length; i += 4) {
            rgba[i] = 200; rgba[i + 1] = 100; rgba[i + 2] = 50; rgba[i + 3] = 255;
        }
        const xnb = encodeTexture2dXnb({ rgba, width: w, height: h, format: 'dxt1' });
        const c = classifyXnb(xnb);
        const od = c.objectData!;
        const surfaceFormat = od[0] | (od[1] << 8) | (od[2] << 16) | (od[3] << 24);
        expect(surfaceFormat).toBe(4);   // Dxt1 always — KNI has no Dxt1a slot
    });

    it('emits a valid DXT5 XNB with the expected SurfaceFormat + DataSize', () => {
        // 4×4 RGBA so DXT5's 16-byte block aligns to one block.
        const rgba = new Uint8Array(4 * 4 * 4);
        for (let i = 0; i < rgba.length; i += 4) {
            rgba[i] = 200; rgba[i + 1] = 100; rgba[i + 2] = 50; rgba[i + 3] = 255;
        }
        const xnb = encodeTexture2dXnb({ rgba, width: 4, height: 4, format: 'dxt5' });
        const c = classifyXnb(xnb);
        expect(c.kind).toBe('texture2d');
        // SurfaceFormat (int32) sits at the start of the post-reader-chain
        // payload — the byte before Width.
        const od = c.objectData!;
        const surfaceFormat = od[0] | (od[1] << 8) | (od[2] << 16) | (od[3] << 24);
        expect(surfaceFormat).toBe(6);   // Dxt5
        // DataSize for one 4×4 DXT5 block = 16 bytes.
        const dataSize = od[16] | (od[17] << 8) | (od[18] << 16) | (od[19] << 24);
        expect(dataSize).toBe(16);
    });

    it('rejects unknown formats with a clear error', () => {
        const rgba = new Uint8Array(4);
        expect(() => encodeTexture2dXnb({ rgba, width: 1, height: 1, format: 'astc' as any }))
            .toThrowError(/unknown format/);
    });
});
