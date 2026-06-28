// TTF/OTF → SpriteFont glyph atlas + metrics.
//
// Browsers can rasterize any TrueType / OpenType font for free:
//   1. Register the font bytes as a CSS @font-face under a unique name
//   2. Wait for `document.fonts.load()` to finish loading
//   3. Render each glyph onto an OffscreenCanvas via canvas2d.fillText
//   4. Pack into an atlas, capture per-glyph metrics
//
// Output mirrors MonoGame's SpriteFont fields one-for-one so the
// downstream XNB writer can emit a binary the runtime's
// SpriteFontReader will deserialize without any custom code on the
// game side.

export interface GlyphMetric {
    /** Source character. */
    char: string;
    /** Bounding rectangle within the atlas (x, y, w, h). */
    glyph: { x: number; y: number; w: number; h: number };
    /** Cropping = offset from baseline + glyph dimensions. (x, y, w, h) where
     *  (x, y) is the offset from cursor position to the top-left of the glyph
     *  rect, and (w, h) match `glyph`. */
    cropping: { x: number; y: number; w: number; h: number };
    /** Horizontal advance after drawing this glyph, as (left, width, right).
     *  Matches the SpriteFont kerning convention: x = left side bearing,
     *  y = width contribution, z = right side bearing. */
    kerning: { x: number; y: number; z: number };
}

export interface RasterizedFont {
    /** Atlas pixels, RGBA8 row-major, top-down. */
    atlasRgba: Uint8Array;
    atlasWidth: number;
    atlasHeight: number;
    /** Per-glyph metrics, indexed identically to the chars array. */
    glyphs: GlyphMetric[];
    /** All characters present, in the same order as `glyphs`. */
    chars: string[];
    /** Distance between baselines on consecutive lines, in pixels. */
    lineSpacing: number;
    /** Extra horizontal spacing between glyphs (additive on top of the
     *  kerning width). 0 for typical fonts; positive for letter-spacing. */
    spacing: number;
    /** Fallback character for codepoints the atlas doesn't include. Usually
     *  '?' or the font's missing-glyph box. */
    defaultChar: string;
}

export interface RasterizeOptions {
    /** Font face name to register under (must be unique enough not to
     *  collide with other registered fonts in the document). */
    name: string;
    /** Render size in pixels (e.g. 32). The atlas is rasterized at this
     *  size; scale at draw time via `scale text` for larger displays. */
    sizePx: number;
    /** Bytes of the .ttf/.otf source file. */
    bytes: Uint8Array;
    /** Character set to include. Defaults to printable ASCII (32–126)
     *  plus the replacement char '?'. Pass an explicit string when the
     *  user's text needs accented / non-ASCII characters. */
    chars?: string;
    /** Glyph-cell padding inside the atlas (pixels). 1px is enough to
     *  prevent neighbour bleed under bilinear filtering. */
    padding?: number;
}

const DEFAULT_CHARS = (() => {
    let out = '';
    for (let c = 32; c <= 126; c++) out += String.fromCharCode(c);
    return out;
})();

/** Lazy guard so we don't try to re-register the same font face name
 *  across repeated compiles. Browsers tolerate it (subsequent loads are
 *  cache hits) but warns are noisy; track the names we've already
 *  registered for the lifetime of the playground page. */
const _registeredFaces = new Set<string>();

/** Register the font bytes as a CSS @font-face. Returns once the
 *  browser has finished loading + the FontFace object is ready to use
 *  in canvas2d. Throws if the bytes don't parse as a valid font. */
async function registerFontFace(name: string, bytes: Uint8Array): Promise<void> {
    if (_registeredFaces.has(name)) return;
    // FontFace constructor accepts ArrayBuffer. Slice to a fresh copy
    // so the source bytes stay usable after the FontFace consumes them.
    const ab = new ArrayBuffer(bytes.byteLength);
    new Uint8Array(ab).set(bytes);
    const face = new FontFace(name, ab);
    await face.load();
    (document as any).fonts.add(face);
    _registeredFaces.add(name);
}

/** Naive horizontal-strip packing. The atlas grows downward as needed.
 *  Plenty for ~100 glyphs at 32px (the typical text-size envelope).
 *  Worst-case wastes some right-edge pixels per row — fine for our
 *  use case. A skyline packer would be tighter but isn't worth the
 *  complexity at these sizes. */
class StripPacker {
    private cursorX = 0;
    private cursorY = 0;
    private rowHeight = 0;
    private placements: { x: number; y: number; w: number; h: number }[] = [];

    constructor(public readonly width: number, public readonly padding: number) {}

    place(w: number, h: number): { x: number; y: number } {
        const pad = this.padding;
        if (this.cursorX + w + pad > this.width) {
            // Wrap to next row.
            this.cursorX = 0;
            this.cursorY += this.rowHeight + pad;
            this.rowHeight = 0;
        }
        const x = this.cursorX;
        const y = this.cursorY;
        this.placements.push({ x, y, w, h });
        this.cursorX += w + pad;
        if (h > this.rowHeight) this.rowHeight = h;
        return { x, y };
    }

    get totalHeight(): number {
        return this.cursorY + this.rowHeight + this.padding;
    }
}

/** Pick a reasonable atlas width given the number of glyphs and their
 *  size. Aim for power-of-two-ish (GPUs prefer it) and roughly square. */
function pickAtlasWidth(glyphCount: number, glyphSize: number, padding: number): number {
    const totalArea = glyphCount * (glyphSize + padding) * (glyphSize + padding);
    const ideal = Math.sqrt(totalArea);
    // Round up to next power of 2, clamped to 256..2048.
    let w = 256;
    while (w < ideal && w < 2048) w *= 2;
    return w;
}

export async function rasterizeFont(opts: RasterizeOptions): Promise<RasterizedFont> {
    const chars = opts.chars ?? DEFAULT_CHARS;
    const padding = opts.padding ?? 1;
    const sizePx = Math.max(4, Math.floor(opts.sizePx));

    await registerFontFace(opts.name, opts.bytes);

    // Pre-measure every glyph so we know how to lay out the atlas. The
    // canvas2d measureText API gives us advance width + actual bounding
    // box. We use those to pack tight glyph cells, not loose squares.
    const measureCanvas = new OffscreenCanvas(64, 64);
    const measureCtx = measureCanvas.getContext('2d');
    if (!measureCtx) throw new Error('font-rasterizer: failed to acquire OffscreenCanvas 2d context');
    measureCtx.font = `${sizePx}px "${opts.name}"`;
    measureCtx.textBaseline = 'alphabetic';
    measureCtx.fillStyle = '#ffffff';

    interface Measured {
        char: string;
        advance: number;             // horizontal advance after drawing
        boundLeft: number;           // bounding-box left of cursor (often negative for chars like 'j')
        boundRight: number;
        boundTop: number;            // distance ABOVE baseline (positive)
        boundBottom: number;         // distance BELOW baseline (positive when descender)
        cellW: number;               // ceil(boundRight - boundLeft)
        cellH: number;               // ceil(boundTop + boundBottom)
    }
    const measured: Measured[] = [];
    let maxAscent = 0;
    let maxDescent = 0;
    for (const c of chars) {
        const m = measureCtx.measureText(c);
        const boundLeft = m.actualBoundingBoxLeft ?? 0;
        const boundRight = m.actualBoundingBoxRight ?? m.width;
        const boundTop = m.actualBoundingBoxAscent ?? sizePx * 0.8;
        const boundBottom = m.actualBoundingBoxDescent ?? sizePx * 0.2;
        const cellW = Math.max(1, Math.ceil(boundLeft + boundRight));
        const cellH = Math.max(1, Math.ceil(boundTop + boundBottom));
        if (boundTop > maxAscent) maxAscent = boundTop;
        if (boundBottom > maxDescent) maxDescent = boundBottom;
        measured.push({
            char: c, advance: m.width,
            boundLeft, boundRight, boundTop, boundBottom,
            cellW, cellH,
        });
    }

    // Atlas layout: row-strip packing at a power-of-two width.
    const atlasW = pickAtlasWidth(chars.length, Math.ceil(sizePx * 1.2), padding);
    const packer = new StripPacker(atlasW, padding);
    const placements = measured.map((m) => packer.place(m.cellW, m.cellH));
    // Round atlas height up to the next power of 2 for GPU friendliness.
    let atlasH = 256;
    while (atlasH < packer.totalHeight && atlasH < 2048) atlasH *= 2;

    // Render glyphs into the atlas. Each glyph's baseline sits at
    // (placement.x + boundLeft, placement.y + boundTop) — the cursor's
    // visual reference inside its cell.
    const atlas = new OffscreenCanvas(atlasW, atlasH);
    const ctx = atlas.getContext('2d');
    if (!ctx) throw new Error('font-rasterizer: failed to acquire OffscreenCanvas 2d context');
    ctx.font = `${sizePx}px "${opts.name}"`;
    ctx.textBaseline = 'alphabetic';
    ctx.fillStyle = '#ffffff';
    // Clear to fully transparent — SpriteBatch.DrawString picks up the
    // alpha channel as the glyph mask.
    ctx.clearRect(0, 0, atlasW, atlasH);
    for (let i = 0; i < measured.length; i++) {
        const m = measured[i];
        const p = placements[i];
        ctx.fillText(m.char, p.x + m.boundLeft, p.y + m.boundTop);
    }

    const imageData = ctx.getImageData(0, 0, atlasW, atlasH);
    const atlasRgba = new Uint8Array(imageData.data.buffer.slice(0));

    // Build SpriteFont-style metrics.
    // - glyphs[i]: rect inside the atlas
    // - croppings[i]: offset from cursor position to top-left of glyph,
    //   plus the glyph's drawn size (matches MonoGame's convention)
    // - kerning[i]: (leftBearing, width, rightBearing). The runtime uses
    //   width for the per-char advance; left/right bearings refine
    //   spacing for adjacent glyphs.
    const glyphs: GlyphMetric[] = measured.map((m, i) => {
        const p = placements[i];
        return {
            char: m.char,
            glyph: { x: p.x, y: p.y, w: m.cellW, h: m.cellH },
            // Cropping y is offset from the top of the line to the top
            // of the glyph cell — i.e. baseline-from-top minus this
            // glyph's ascent. Cropping x is the left side bearing.
            cropping: {
                x: 0,
                y: Math.round(maxAscent - m.boundTop),
                w: m.cellW,
                h: m.cellH,
            },
            kerning: {
                x: 0,
                y: m.advance,
                z: 0,
            },
        };
    });

    return {
        atlasRgba,
        atlasWidth: atlasW,
        atlasHeight: atlasH,
        glyphs,
        chars: measured.map((m) => m.char),
        lineSpacing: Math.ceil(maxAscent + maxDescent + 1),
        spacing: 0,
        defaultChar: chars.includes('?') ? '?' : chars[0] ?? ' ',
    };
}
