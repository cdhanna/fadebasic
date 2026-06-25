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
        const raw = source.slice(s.start, s.end);
        // Tag keyword/function/method tokens with their symbol so consumers
        // (the AI chat) can offer "open docs for this". Consumers opt in via
        // styling/handlers; the attribute is inert elsewhere.
        const symbolAttr = DOC_LINKABLE_TYPES.has(s.type)
            ? ` data-fade-symbol="${escapeHtml(raw)}"`
            : '';
        out += `<span class="${cls}"${symbolAttr}>${escapeHtml(raw)}</span>`;
        cursor = s.end;
    }
    if (cursor < source.length) out += escapeHtml(source.slice(cursor));
    return out;
}

// keyword(1), function(2), method(3) — the tokens that map to a Fade command
// or language keyword with a docs page.
const DOC_LINKABLE_TYPES = new Set([1, 2, 3]);

// Fade language keywords for the synchronous fallback highlighter.
const FADE_KEYWORDS = new Set([
    'if', 'then', 'else', 'elseif', 'endif', 'while', 'endwhile', 'for', 'to',
    'step', 'next', 'repeat', 'until', 'do', 'loop', 'exit', 'skip', 'select',
    'case', 'endcase', 'endselect', 'default', 'function', 'endfunction',
    'exitfunction', 'global', 'local', 'dim', 'as', 'type', 'endtype', 'gosub',
    'goto', 'return', 'and', 'or', 'not', 'mod', 'rem', 'remstart', 'remend',
    'end', 'true', 'false',
]);

function escHtml(s: string): string {
    return s.replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]!));
}

/** Synchronous, dependency-free Fade highlighter — a single-pass lexer for
 *  comments / strings / numbers / keywords, plus COMMAND names when a set is
 *  supplied (the LSP colors commands too, but it's async and often unavailable
 *  in chat). Used as the baseline so code in the AI chat is COLORED the instant
 *  it renders (and while streaming). The LSP pass upgrades it when available; if
 *  the LSP yields nothing, these colors remain.
 *
 *  `commands` should contain lowercased command tokens — both whole single-word
 *  command names and the individual words of multi-word commands (e.g. `key`,
 *  `down` for "key down") so each word of a command phrase colors. */
export function highlightFadeStatic(source: string, commands?: ReadonlySet<string>): string {
    let out = '';
    let i = 0;
    const n = source.length;
    while (i < n) {
        const c = source[i];
        if (c === '`') {
            // Line comment: backtick → end of line.
            let j = i;
            while (j < n && source[j] !== '\n') j++;
            out += `<span class="fade-tok-comment">${escHtml(source.slice(i, j))}</span>`;
            i = j;
        } else if (c === '"') {
            // String literal (closes at the next quote or end of line).
            let j = i + 1;
            while (j < n && source[j] !== '"' && source[j] !== '\n') j++;
            if (j < n && source[j] === '"') j++;
            out += `<span class="fade-tok-string">${escHtml(source.slice(i, j))}</span>`;
            i = j;
        } else if (c >= '0' && c <= '9') {
            let j = i;
            while (j < n && /[0-9.]/.test(source[j])) j++;
            out += `<span class="fade-tok-number">${escHtml(source.slice(i, j))}</span>`;
            i = j;
        } else if (/[A-Za-z_]/.test(c)) {
            let j = i;
            while (j < n && /[A-Za-z0-9_]/.test(source[j])) j++;
            const word = source.slice(i, j);
            const low = word.toLowerCase();
            if (FADE_KEYWORDS.has(low)) {
                out += `<span class="fade-tok-keyword">${escHtml(word)}</span>`;
            } else if (commands && commands.has(low)) {
                out += `<span class="fade-tok-function" data-fade-symbol="${escHtml(word)}">${escHtml(word)}</span>`;
            } else {
                out += escHtml(word);
            }
            i = j;
        } else {
            out += escHtml(c);
            i++;
        }
    }
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
        // No LSP tokens (worker not ready, etc.) — keep the static coloring
        // already applied at render time instead of stripping it to plain text.
        if (tokens.length === 0) continue;
        code.innerHTML = renderTokenizedSnippet(source, tokens);
    }
}
