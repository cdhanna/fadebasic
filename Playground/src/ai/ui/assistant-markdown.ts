import { marked } from 'marked';
import { highlightFadeCodeBlocks } from '../../snippet-highlight';

// Treat bare ``` fences as Fade — models often omit the language tag.
marked.use({
    renderer: {
        code({ text, lang }) {
            const language = lang || 'fade';
            const escaped = text
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;');
            return `<pre><code class="language-${language}">${escaped}</code></pre>\n`;
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

/** Render assistant markdown into `el`, optionally LSP-highlighting Fade fences. */
export async function renderAssistantMarkdown(
    el: HTMLElement,
    markdown: string,
    tokenize?: (source: string) => Promise<Array<{ line: number; col: number; length: number; type: number }>>,
): Promise<void> {
    const trimmed = markdown.trim();
    if (!trimmed) {
        el.textContent = '';
        return;
    }
    let html: string;
    try {
        html = marked.parse(trimmed, { async: false, gfm: true, breaks: true }) as string;
    } catch {
        el.textContent = markdown;
        return;
    }
    el.innerHTML = scrubHtml(html);
    if (tokenize) {
        // Re-highlight on each render — prior blocks are replaced by marked.
        for (const code of el.querySelectorAll<HTMLElement>('pre > code')) {
            delete code.dataset.fadeHighlighted;
        }
        await highlightFadeCodeBlocks(el, tokenize);
    }
}
