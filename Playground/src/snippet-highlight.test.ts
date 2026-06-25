import { describe, it, expect } from 'vitest';
import { highlightFadeStatic } from './snippet-highlight';

describe('highlightFadeStatic', () => {
    it('colors keywords, strings, numbers, and comments', () => {
        const html = highlightFadeStatic('if x > 0 then print "hi" ` a comment\nx = 42');
        expect(html).toContain('<span class="fade-tok-keyword">if</span>');
        expect(html).toContain('<span class="fade-tok-keyword">then</span>');
        expect(html).toContain('<span class="fade-tok-string">&quot;hi&quot;</span>');
        expect(html).toContain('<span class="fade-tok-number">42</span>');
        expect(html).toContain('<span class="fade-tok-comment">` a comment</span>');
    });

    it('escapes HTML so code cannot inject markup', () => {
        const html = highlightFadeStatic('x = "<script>" & y');
        expect(html).not.toContain('<script>');
        expect(html).toContain('&lt;script&gt;');
    });

    it('handles an unterminated string mid-stream without throwing', () => {
        const html = highlightFadeStatic('print "half typed');
        expect(html).toContain('<span class="fade-tok-string">&quot;half typed</span>');
    });

    it('leaves plain identifiers uncolored', () => {
        const html = highlightFadeStatic('myVar = otherVar');
        expect(html).toBe('myVar = otherVar');
    });

    it('colors command names when a command set is supplied', () => {
        const cmds = new Set(['sync', 'sprite', 'print']);
        const html = highlightFadeStatic('sprite 1, x, y\nsync', cmds);
        expect(html).toContain('<span class="fade-tok-function" data-fade-symbol="sprite">sprite</span>');
        expect(html).toContain('<span class="fade-tok-function" data-fade-symbol="sync">sync</span>');
        // A non-command identifier stays plain.
        expect(html).toContain('y\n');
        expect(html).not.toContain('data-fade-symbol="y"');
    });
});
