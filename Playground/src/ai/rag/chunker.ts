// Markdown-aware chunker. Splits a doc into sub-section chunks of roughly
// `targetChars` characters, respecting heading boundaries so the model
// always retrieves coherent sections.
//
// The strategy:
//   1. Split on lines that look like ATX headings (`# ` ... `###### `).
//   2. Carry the current heading path so each chunk knows its lineage.
//   3. If a section is bigger than targetChars, split on blank lines
//      (paragraph breaks) — never inside a code block.
//   4. Filter out chunks that are too tiny to be useful (< 60 chars).

export interface ChunkInput {
    /** Source path, e.g. "FadeBook/Language.md". */
    source: string;
    /** Raw markdown content. */
    text: string;
}

export interface ChunkOutput {
    id: string;
    source: string;
    heading: string;
    text: string;
}

export interface ChunkerOptions {
    /** Target chunk size in characters. Default ~1200 (~300 tokens). */
    targetChars?: number;
    /** Minimum chunk size — anything smaller gets dropped. Default 60. */
    minChars?: number;
    /** Hard cap so a runaway code block doesn't produce a 50K chunk. */
    maxChars?: number;
}

const DEFAULTS = {
    targetChars: 1200,
    minChars: 60,
    maxChars: 4000,
};

interface Section {
    heading: string;
    body: string;
}

/** Chunk a single markdown file. */
export function chunkMarkdown(input: ChunkInput, opts: ChunkerOptions = {}): ChunkOutput[] {
    const o = { ...DEFAULTS, ...opts };
    const sections = splitSections(input.text);
    const out: ChunkOutput[] = [];
    let counter = 0;

    for (const section of sections) {
        const pieces = section.body.length > o.targetChars
            ? splitOnParagraphs(section.body, o.targetChars, o.maxChars)
            : [section.body];

        for (const piece of pieces) {
            const trimmed = piece.trim();
            if (trimmed.length < o.minChars) continue;

            const slug = makeSlug(section.heading, counter);
            counter++;
            out.push({
                id: `${input.source}#${slug}`,
                source: input.source,
                heading: section.heading,
                text: trimmed,
            });
        }
    }
    return out;
}

/** Split markdown into sections at ATX headings. Tracks the path of
 *  nested headings so each section has a context-friendly heading string. */
function splitSections(text: string): Section[] {
    const lines = text.split('\n');
    const sections: Section[] = [];
    const headingStack: { level: number; text: string }[] = [];
    let buffer: string[] = [];
    let inCodeFence = false;

    const flush = () => {
        const body = buffer.join('\n');
        if (body.trim().length === 0) {
            buffer = [];
            return;
        }
        const heading = headingStack.map(h => h.text).join(' > ');
        sections.push({ heading, body });
        buffer = [];
    };

    for (const line of lines) {
        // Track fenced code blocks so headings inside them don't split.
        if (/^\s*```/.test(line)) {
            inCodeFence = !inCodeFence;
            buffer.push(line);
            continue;
        }
        if (!inCodeFence) {
            const m = line.match(/^(#{1,6})\s+(.+?)\s*#*\s*$/);
            if (m) {
                flush();
                const level = m[1].length;
                const text = m[2].trim();
                // Pop stack down to the parent level.
                while (headingStack.length && headingStack[headingStack.length - 1].level >= level) {
                    headingStack.pop();
                }
                headingStack.push({ level, text });
                continue;
            }
        }
        buffer.push(line);
    }
    flush();
    return sections;
}

/** Greedily pack paragraphs into chunks of ~targetChars, never exceeding
 *  maxChars, never splitting inside a code fence. */
function splitOnParagraphs(text: string, targetChars: number, maxChars: number): string[] {
    const paras = splitParagraphsRespectingCode(text);
    const out: string[] = [];
    let current = '';

    for (const para of paras) {
        if (current.length === 0) {
            current = para;
            continue;
        }
        // If adding this paragraph keeps us under target, append.
        if (current.length + para.length + 2 <= targetChars) {
            current += '\n\n' + para;
        } else if (para.length > maxChars) {
            // Pathological case — a single paragraph (or code block) larger
            // than maxChars. Hard-split by line so we don't drop content.
            if (current) { out.push(current); current = ''; }
            const subLines = para.split('\n');
            let part = '';
            for (const line of subLines) {
                if (part.length + line.length + 1 > maxChars) {
                    out.push(part);
                    part = line;
                } else {
                    part = part ? part + '\n' + line : line;
                }
            }
            if (part) out.push(part);
        } else {
            out.push(current);
            current = para;
        }
    }
    if (current) out.push(current);
    return out;
}

/** Split into paragraphs by blank lines, keeping fenced code blocks atomic. */
function splitParagraphsRespectingCode(text: string): string[] {
    const lines = text.split('\n');
    const out: string[] = [];
    let buf: string[] = [];
    let inCode = false;

    const flush = () => {
        if (buf.length > 0) {
            const joined = buf.join('\n');
            if (joined.trim().length > 0) out.push(joined);
        }
        buf = [];
    };

    for (const line of lines) {
        if (/^\s*```/.test(line)) {
            inCode = !inCode;
            buf.push(line);
            continue;
        }
        if (!inCode && line.trim() === '') {
            flush();
            continue;
        }
        buf.push(line);
    }
    flush();
    return out;
}

function makeSlug(heading: string, fallbackIdx: number): string {
    const base = heading
        .toLowerCase()
        .replace(/[^a-z0-9\s>-]/g, '')
        .replace(/\s*>\s*/g, '-')
        .replace(/\s+/g, '-')
        .replace(/-+/g, '-')
        .replace(/^-|-$/g, '');
    return base ? `${base}-${fallbackIdx}` : `chunk-${fallbackIdx}`;
}
