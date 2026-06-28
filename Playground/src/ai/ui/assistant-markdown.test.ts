// @vitest-environment jsdom
import { describe, it, expect, vi } from 'vitest';
import { renderAssistantMarkdown } from './assistant-markdown';

// Fake tokenizer: marks the word "print" as a function token (type 2) so the
// highlighter tags it as a doc-linkable symbol.
const tokenizePrint = async (source: string) => {
    const idx = source.indexOf('print');
    if (idx < 0) return [];
    const before = source.slice(0, idx);
    const line = before.split('\n').length - 1;
    const col = idx - (before.lastIndexOf('\n') + 1);
    return [{ line, col, length: 'print'.length, type: 2 }];
};

describe('renderAssistantMarkdown', () => {
    it('adds a copy button that copies the code text', async () => {
        const writeText = vi.fn().mockResolvedValue(undefined);
        vi.stubGlobal('navigator', { clipboard: { writeText } });

        const el = document.createElement('div');
        await renderAssistantMarkdown(el, 'Here:\n\n```fade\nprint "hi"\n```');

        const block = el.querySelector('.ai-code-block');
        expect(block).not.toBeNull();
        const copy = block!.querySelector<HTMLButtonElement>('.ai-code-copy');
        expect(copy).not.toBeNull();

        copy!.click();
        expect(writeText).toHaveBeenCalledWith(expect.stringContaining('print "hi"'));
    });

    it('makes command symbols doc-linkable (click + right-click)', async () => {
        const onSymbolDocs = vi.fn();
        const el = document.createElement('div');
        await renderAssistantMarkdown(el, '```fade\nprint "hi"\n```', {
            tokenize: tokenizePrint,
            onSymbolDocs,
        });

        const sym = el.querySelector<HTMLElement>('[data-fade-symbol="print"]');
        expect(sym).not.toBeNull();
        expect(sym!.title).toContain('print');

        sym!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
        expect(onSymbolDocs).toHaveBeenCalledWith('print');

        sym!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true }));
        expect(onSymbolDocs).toHaveBeenCalledTimes(2);
    });

    it('does not double-wrap code blocks across re-renders (streaming)', async () => {
        const el = document.createElement('div');
        await renderAssistantMarkdown(el, '```fade\nprint 1\n```');
        await renderAssistantMarkdown(el, '```fade\nprint 1\nprint 2\n```');
        expect(el.querySelectorAll('.ai-code-block')).toHaveLength(1);
        expect(el.querySelectorAll('.ai-code-copy')).toHaveLength(1);
    });
});
