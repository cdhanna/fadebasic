// LSP token → HTML spans for Fade snippets. Shared by the Help panel and
// AI chat markdown rendering.

export interface SnippetToken {
    line: number;
    col: number;
    length: number;
    type: number;
}

export const TOKEN_TYPE_CLASS: Record<number, string> = {
    0:  'fade-tok-comment',
    1:  'fade-tok-keyword',
    2:  'fade-tok-function',
    3:  'fade-tok-method',
    4:  'fade-tok-macro',
    5:  'fade-tok-parameter',
    6:  'fade-tok-struct',
    7:  'fade-tok-type',
    8:  'fade-tok-operator',
    9:  'fade-tok-number',
    10: 'fade-tok-string',
};

function escapeHtml(s: string): string {
    return s.replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]!));
}

export function renderTokenizedSnippet(source: string, tokens: SnippetToken[]): string {
    if (tokens.length === 0) return escapeHtml(source);
    const lineStarts: number[] = [0];
    for (let i = 0; i < source.length; i++) {
        if (source.charCodeAt(i) === 10) lineStarts.push(i + 1);
    }
    interface Span { start: number; end: number; type: number; }
    const spans: Span[] = [];
    for (const t of tokens) {
        const lineStart = lineStarts[t.line];
        if (lineStart === undefined) continue;
        const start = lineStart + t.col;
        const end = start + t.length;
        if (end > source.length) continue;
        spans.push({ start, end, type: t.type });
    }
    spans.sort((a, b) => a.start - b.start);
    const cleaned: Span[] = [];
    for (const s of spans) {
        if (cleaned.length > 0 && s.start < cleaned[cleaned.length - 1].end) continue;
        cleaned.push(s);
    }
    let out = '';
    let cursor = 0;
    for (const s of cleaned) {
        if (s.start > cursor) out += escapeHtml(source.slice(cursor, s.start));
        const cls = TOKEN_TYPE_CLASS[s.type] ?? 'fade-tok-default';
        out += `<span class="${cls}">${escapeHtml(source.slice(s.start, s.end))}</span>`;
        cursor = s.end;
    }
    if (cursor < source.length) out += escapeHtml(source.slice(cursor));
    return out;
}

/** Upgrade ```fade``` / unspecified-language code blocks in-place. */
export async function highlightFadeCodeBlocks(
    root: HTMLElement,
    tokenize: (source: string) => Promise<SnippetToken[]>,
): Promise<void> {
    const blocks = Array.from(root.querySelectorAll<HTMLElement>('pre > code'));
    for (const code of blocks) {
        const langClass = Array.from(code.classList).find(c => c.startsWith('language-'));
        const lang = langClass ? langClass.slice('language-'.length).toLowerCase() : '';
        if (lang && lang !== 'fade' && lang !== 'basic' && lang !== 'fbasic') continue;
        if (code.dataset.fadeHighlighted) continue;
        code.dataset.fadeHighlighted = '1';
        const source = code.textContent ?? '';
        if (!source.trim()) continue;
        let tokens: SnippetToken[] = [];
        try { tokens = await tokenize(source); }
        catch { continue; }
        code.innerHTML = renderTokenizedSnippet(source, tokens);
    }
}
