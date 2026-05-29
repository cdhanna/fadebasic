import { describe, it, expect } from 'vitest';
import { chunkMarkdown } from './chunker';

describe('chunkMarkdown', () => {
    it('splits at ATX headings and carries the heading path', () => {
        const text = `
# Top

intro text under top

## Section A

text under A

## Section B

text under B
`;
        const chunks = chunkMarkdown({ source: 'x.md', text }, { minChars: 0 });
        const headings = chunks.map(c => c.heading);
        expect(headings).toContain('Top');
        expect(headings).toContain('Top > Section A');
        expect(headings).toContain('Top > Section B');
    });

    it('packs paragraphs to roughly targetChars', () => {
        const paras = Array.from({ length: 10 }, (_, i) =>
            `Paragraph ${i} ${'word '.repeat(50)}`,
        );
        const text = `# Big\n\n${paras.join('\n\n')}`;
        const chunks = chunkMarkdown({ source: 'x.md', text }, { targetChars: 600 });

        // Should produce multiple chunks, none egregiously over target
        expect(chunks.length).toBeGreaterThan(1);
        for (const c of chunks) {
            // Allow some slack — last paragraph may push slightly over.
            expect(c.text.length).toBeLessThan(1500);
        }
    });

    it('keeps fenced code blocks atomic', () => {
        const text = `# Demo

\`\`\`
function foo()
  return 1
end function

function bar()
  return 2
end function
\`\`\`

after the code
`;
        const chunks = chunkMarkdown({ source: 'x.md', text });
        const withCode = chunks.find(c => c.text.includes('function foo'));
        expect(withCode).toBeDefined();
        // bar should be in the same chunk as foo — the fence is atomic
        expect(withCode!.text).toContain('function bar');
    });

    it('does not treat # inside a code fence as a heading', () => {
        const text = `# Real Heading

\`\`\`
# this is a comment, not a heading
print "hi"
\`\`\`
`;
        const chunks = chunkMarkdown({ source: 'x.md', text }, { minChars: 0 });
        // Only one heading should be recognized
        const uniqueHeadings = new Set(chunks.map(c => c.heading));
        expect(uniqueHeadings.size).toBe(1);
        expect([...uniqueHeadings][0]).toBe('Real Heading');
    });

    it('drops tiny chunks below minChars', () => {
        const text = `# A\n\nlong section ${'word '.repeat(40)}\n\n## B\n\nhi\n`;
        const chunks = chunkMarkdown({ source: 'x.md', text }, { minChars: 30 });
        // The "hi" section is too small to keep
        const headings = chunks.map(c => c.heading);
        expect(headings).not.toContain('A > B');
    });

    it('generates unique IDs per chunk', () => {
        const text = `# A\n\n${'big '.repeat(500)}\n\n${'more '.repeat(500)}`;
        const chunks = chunkMarkdown({ source: 'x.md', text }, { targetChars: 400 });
        const ids = new Set(chunks.map(c => c.id));
        expect(ids.size).toBe(chunks.length);
    });
});
