import { marked } from 'marked';
import { highlightFadeCodeBlocks, highlightFadeStatic } from '../../snippet-highlight';

// Command set for the synchronous highlighter, set just before each
// marked.parse (which is synchronous, so there's no interleaving risk).
let staticCommandWords: ReadonlySet<string> | undefined;

// Treat bare ``` fences as Fade — models often omit the language tag.
marked.use({
    renderer: {
        code({ text, lang }) {
            const language = (lang || 'fade').toLowerCase();
            const isFade = !lang || language === 'fade' || language === 'basic' || language === 'fbasic';
            // Color Fade synchronously at render time (and while streaming) so
            // code is never shown as flat text waiting on the async LSP pass.
            // Commands color too when the command set is available.
            const body = isFade
                ? highlightFadeStatic(text, staticCommandWords)
                : text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
            return `<pre><code class="language-${language}">${body}</code></pre>\n`;
        },
    },
});

function scrubHtml(html: string): string {
    return html
        .replace(/<\s*(script|style|iframe|object|embed)[^>]*>[\s\S]*?<\s*\/\s*\1\s*>/gi, '')
        .replace(/\son[a-z]+\s*=\s*"[^"]*"/gi, '')
        .replace(/\son[a-z]+\s*=\s*'[^']*'/gi, '')
        .replace(/javascript:/gi, '');
}

export interface AssistantMarkdownOptions {
    tokenize?: (source: string) => Promise<Array<{ line: number; col: number; length: number; type: number }>>;
    /** Open the docs for a Fade symbol (command/keyword) clicked in a snippet.
     *  Returns true if docs were found/opened. */
    onSymbolDocs?: (symbol: string) => void | Promise<void>;
    /** Live streaming render: emit markdown STRUCTURE only and skip the async
     *  LSP highlight, copy buttons, and symbol wiring. Those re-run on every
     *  token and, on still-incomplete code, re-tokenize to different colors
     *  each tick — which reads as flicker. The full pass runs once at finalize. */
    live?: boolean;
    /** Lowercased Fade command tokens, so the synchronous highlighter can color
     *  commands (not just language keywords) immediately. */
    commandWords?: ReadonlySet<string>;
}

/** Render assistant markdown into `el`, optionally LSP-highlighting Fade
 *  fences, adding copy buttons, and making symbols doc-linkable. */
export async function renderAssistantMarkdown(
    el: HTMLElement,
    markdown: string,
    opts: AssistantMarkdownOptions = {},
): Promise<void> {
    const trimmed = markdown.trim();
    if (!trimmed) {
        el.textContent = '';
        return;
    }
    let html: string;
    staticCommandWords = opts.commandWords;
    try {
        html = marked.parse(trimmed, { async: false, gfm: true, breaks: true }) as string;
    } catch {
        el.textContent = markdown;
        return;
    }
    el.innerHTML = scrubHtml(html);
    // While streaming, stop here — structure only. Highlighting partial code
    // every token is what flickers; the final pass below does it once.
    if (opts.live) return;
    if (opts.tokenize) {
        // Re-highlight on each render — prior blocks are replaced by marked.
        for (const code of el.querySelectorAll<HTMLElement>('pre > code')) {
            delete code.dataset.fadeHighlighted;
        }
        await highlightFadeCodeBlocks(el, opts.tokenize);
    }
    decorateCodeBlocks(el);
    if (opts.onSymbolDocs) wireSymbolDocs(el, opts.onSymbolDocs);
}

/** Add a Copy button to every code block (idempotent per render). */
function decorateCodeBlocks(el: HTMLElement): void {
    for (const pre of el.querySelectorAll<HTMLElement>('pre')) {
        if (pre.parentElement?.classList.contains('ai-code-block')) continue;
        const code = pre.querySelector('code');
        if (!code) continue;
        const wrap = document.createElement('div');
        wrap.className = 'ai-code-block';
        pre.replaceWith(wrap);
        wrap.appendChild(pre);

        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'ai-code-copy';
        btn.textContent = 'Copy';
        btn.title = 'Copy code';
        btn.addEventListener('click', () => {
            void navigator.clipboard.writeText(code.textContent ?? '').then(() => {
                btn.textContent = 'Copied';
                setTimeout(() => { btn.textContent = 'Copy'; }, 1400);
            }).catch(() => { btn.textContent = 'Copy failed'; });
        });
        wrap.appendChild(btn);
    }
}

/** Make highlighted command/keyword symbols open their docs. Uses a single
 *  delegated listener per element (survives innerHTML re-renders during
 *  streaming because it's bound to `el`, whose children are replaced but
 *  whose own listeners persist). Right-click OR click both open docs;
 *  contextmenu is suppressed so it acts like a doc jump, not a browser menu. */
function wireSymbolDocs(el: HTMLElement, onSymbolDocs: (symbol: string) => void | Promise<void>): void {
    el.classList.add('ai-symbols-linkable');
    // Tooltip each render (spans are recreated when innerHTML is replaced).
    for (const sym of el.querySelectorAll<HTMLElement>('[data-fade-symbol]')) {
        sym.title = `Open docs for "${sym.dataset.fadeSymbol}" (click or right-click)`;
    }
    if (el.dataset.symbolDocsWired) return;
    el.dataset.symbolDocsWired = '1';
    const open = (e: Event) => {
        const target = (e.target as HTMLElement | null)?.closest('[data-fade-symbol]');
        if (!target) return;
        const symbol = (target as HTMLElement).dataset.fadeSymbol;
        if (!symbol) return;
        e.preventDefault();
        void onSymbolDocs(symbol);
    };
    el.addEventListener('click', open);
    el.addEventListener('contextmenu', open);
}
