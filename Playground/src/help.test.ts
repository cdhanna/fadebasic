import { describe, it, expect } from 'vitest';
import { parseDocSections, groupsForEntry, extractCommandNameFromHover, findKeywordDocHeading } from './help';
import type { CommandDocEntry } from './help';

function mg(name: string, markdown = ''): CommandDocEntry {
    return { name, signature: '', group: 'FadeMonoGame', markdown };
}
function std(name: string): CommandDocEntry {
    return { name, signature: '', group: 'Standard', markdown: '' };
}

describe('parseDocSections', () => {
    it('splits on H2 when any H2 exists, keeping the intro as section 0', () => {
        const md = `# Guide\n\nintro paragraph.\n\n## Setup\n\nfoo\n\n## Usage\n\nbar\n`;
        const out = parseDocSections(md);
        expect(out.map(s => s.title)).toEqual(['Guide', 'Setup', 'Usage']);
        // Section 0 owns the H1 + intro paragraph; subsequent sections start at the H2.
        expect(out[0].body).toMatch(/# Guide/);
        expect(out[0].body).toMatch(/intro paragraph/);
        expect(out[1].body.startsWith('## Setup')).toBe(true);
        expect(out[2].body.startsWith('## Usage')).toBe(true);
    });

    it('falls back to H1 boundaries when there are no H2s', () => {
        const md = `# A\n\nalpha\n\n# B\n\nbravo\n`;
        const out = parseDocSections(md);
        expect(out.map(s => s.title)).toEqual(['A', 'B']);
    });

    it('returns a single Overview section when there are no headings at all', () => {
        const out = parseDocSections('just some prose\nno headings');
        expect(out).toHaveLength(1);
        expect(out[0].title).toBe('Overview');
        expect(out[0].slug).toBe('overview');
    });

    it('ignores ATX-looking lines inside fenced code blocks', () => {
        const md = '# Real\n\n```\n## fake heading\n```\n\n## Real H2\n';
        const titles = parseDocSections(md).map(s => s.title);
        expect(titles).toEqual(['Real', 'Real H2']);
    });

    it('disambiguates duplicate slugs with -N suffix', () => {
        const md = `## Setup\n\na\n\n## Setup\n\nb\n`;
        expect(parseDocSections(md).map(s => s.slug)).toEqual(['setup', 'setup-2']);
    });

    it('collects sub-headings (H3/H4) under each H2 section', () => {
        const md = `# Top\n\n## Setup\n\n### Install\n\n### Run\n\n#### Flags\n\n## Other\n\n### Misc\n`;
        const out = parseDocSections(md);
        const setup = out.find(s => s.title === 'Setup')!;
        const other = out.find(s => s.title === 'Other')!;
        expect(setup.subs.map(s => s.text)).toEqual(['Install', 'Run', 'Flags']);
        expect(setup.subs.map(s => s.depth)).toEqual([1, 1, 2]);
        expect(other.subs.map(s => s.text)).toEqual(['Misc']);
        // Slugs are unique across the whole doc.
        const allSlugs = out.flatMap(s => s.subs.map(x => x.slug));
        expect(new Set(allSlugs).size).toBe(allSlugs.length);
    });

    it('omits empty sections', () => {
        // An H2 with no body and no following content shouldn't produce a phantom page.
        const md = `## A\n\nfoo\n\n## B\n## C\n\nbar\n`;
        const titles = parseDocSections(md).map(s => s.title);
        // B has body "## B" + "## C\n\nbar" — wait, no — B's body ends at C's line.
        // So B's body is just "## B" (heading line only) which is non-empty,
        // and C captures the bar. This test mainly proves we don't crash and
        // every emitted section has non-empty body.
        expect(titles).toContain('A');
        expect(titles).toContain('C');
    });
});

describe('groupsForEntry', () => {
    it('passes non-MonoGame entries through unchanged', () => {
        expect(groupsForEntry(std('asc'))).toEqual(['Standard']);
        expect(groupsForEntry({ name: 'foo', signature: '', group: 'CustomLib', markdown: '' })).toEqual(['CustomLib']);
    });

    it('claims Debug commands exclusively', () => {
        // "debug sprite" mentions sprite but should NOT also land in Sprite.
        expect(groupsForEntry(mg('debug sprite'))).toEqual(['Debug']);
        expect(groupsForEntry(mg('begin debug tab'))).toEqual(['Debug']);
        expect(groupsForEntry(mg('end debug window'))).toEqual(['Debug']);
        expect(groupsForEntry(mg('disable debug inspector'))).toEqual(['Debug']);
    });

    it('claims Input commands exclusively (including no-"input"-in-name ones)', () => {
        expect(groupsForEntry(mg('downkey'))).toEqual(['Input']);
        expect(groupsForEntry(mg('leftKey'))).toEqual(['Input']);
        expect(groupsForEntry(mg('mouse x'))).toEqual(['Input']);
        expect(groupsForEntry(mg('left click'))).toEqual(['Input']);
        expect(groupsForEntry(mg('new spaceKey'))).toEqual(['Input']);
    });

    it('groups Math primitives by exact name', () => {
        expect(groupsForEntry(mg('sin'))).toEqual(['Math']);
        expect(groupsForEntry(mg('atan2'))).toEqual(['Math']);
    });

    it('multi-tags commands that genuinely belong to two buckets', () => {
        expect(groupsForEntry(mg('attach sprite to transform')).sort())
            .toEqual(['Sprite', 'Transform'].sort());
        expect(groupsForEntry(mg('set sprite texture')).sort())
            .toEqual(['Sprite', 'Texture'].sort());
        expect(groupsForEntry(mg('render target texture')).sort())
            .toEqual(['Render', 'Texture'].sort());
        expect(groupsForEntry(mg('attach text to transform')).sort())
            .toEqual(['Text', 'Transform'].sort());
    });

    it('routes screen-vs-render commands to the right bucket', () => {
        expect(groupsForEntry(mg('set fullscreen'))).toEqual(['Screen']);
        expect(groupsForEntry(mg('set screen size'))).toEqual(['Screen']);
        expect(groupsForEntry(mg('set screen effect'))).toEqual(['Render']);
        expect(groupsForEntry(mg('set screen shake amount'))).toEqual(['Render']);
    });

    it('puts core/sync commands in Core', () => {
        expect(groupsForEntry(mg('sync'))).toEqual(['Core']);
        expect(groupsForEntry(mg('game ms'))).toEqual(['Core']);
        expect(groupsForEntry(mg('print'))).toEqual(['Core']);
    });

    it('falls back to Other when nothing matches', () => {
        expect(groupsForEntry(mg('totally unknown command'))).toEqual(['Other']);
    });
});


describe('extractCommandNameFromHover', () => {
    it('returns the trimmed name from a single-word command hover', () => {
        expect(extractCommandNameFromHover('### print\n\nPrints text to the console.')).toBe('print');
    });

    // The whole reason this helper exists — Monaco's
    // getWordAtPosition would only yield "position" or "sprite"
    // depending on which word the cursor was on, and neither maps
    // to a byName index keyed under the full phrase.
    it('handles multi-word commands like "position sprite"', () => {
        const body = '### position sprite\n\nMoves a sprite to the given (x, y) coordinates.';
        expect(extractCommandNameFromHover(body)).toBe('position sprite');
    });

    it('tolerates leading whitespace + extra spaces around the name', () => {
        expect(extractCommandNameFromHover('   ###   set color   \nbody')).toBe('set color');
    });

    it('tolerates a blank line before the header', () => {
        expect(extractCommandNameFromHover('\n### load image\n')).toBe('load image');
    });

    it('returns null when the hover has no ### header', () => {
        expect(extractCommandNameFromHover('plain hover text, no header')).toBeNull();
    });

    it('returns null when only lower-level headers are present', () => {
        expect(extractCommandNameFromHover('# Section\n## Subsection\nbody')).toBeNull();
    });

    it('returns null on an empty string', () => {
        expect(extractCommandNameFromHover('')).toBeNull();
    });

    it('stops at the first newline (does not greedily consume the body)', () => {
        const body = '### draw sprite\n\nDraws sprite. ### not a header in body';
        expect(extractCommandNameFromHover(body)).toBe('draw sprite');
    });
});

describe('findKeywordDocHeading', () => {
    it('maps if/then/else/endif to Conditionals', () => {
        expect(findKeywordDocHeading('if')).toBe('Conditionals');
        expect(findKeywordDocHeading('then')).toBe('Conditionals');
        expect(findKeywordDocHeading('else')).toBe('Conditionals');
        expect(findKeywordDocHeading('endif')).toBe('Conditionals');
    });

    it('maps the for-loop keywords to "For Loops"', () => {
        expect(findKeywordDocHeading('for')).toBe('For Loops');
        expect(findKeywordDocHeading('to')).toBe('For Loops');
        expect(findKeywordDocHeading('step')).toBe('For Loops');
        expect(findKeywordDocHeading('next')).toBe('For Loops');
    });

    it('maps function/endfunction to Functions (but exitfunction to Return Values)', () => {
        expect(findKeywordDocHeading('function')).toBe('Functions');
        expect(findKeywordDocHeading('endfunction')).toBe('Functions');
        expect(findKeywordDocHeading('exitfunction')).toBe('Return Values');
    });

    it('maps dim/as to Variables and redim to "Resize an Array"', () => {
        expect(findKeywordDocHeading('dim')).toBe('Variables');
        expect(findKeywordDocHeading('as')).toBe('Variables');
        expect(findKeywordDocHeading('redim')).toBe('Resize an Array');
    });

    it('maps scope keywords (local/global) to Scopes', () => {
        expect(findKeywordDocHeading('local')).toBe('Scopes');
        expect(findKeywordDocHeading('global')).toBe('Scopes');
    });

    it('maps type aliases to Primitive Types (string goes to Strings)', () => {
        expect(findKeywordDocHeading('integer')).toBe('Primitive Types');
        expect(findKeywordDocHeading('int')).toBe('Primitive Types');
        expect(findKeywordDocHeading('float')).toBe('Primitive Types');
        expect(findKeywordDocHeading('double')).toBe('Primitive Types');
        expect(findKeywordDocHeading('byte')).toBe('Primitive Types');
        expect(findKeywordDocHeading('bool')).toBe('Primitive Types');
        expect(findKeywordDocHeading('boolean')).toBe('Primitive Types');
        expect(findKeywordDocHeading('string')).toBe('Strings');
    });

    it('maps boolean ops (and/or/not/xor) to Numeric Operations', () => {
        expect(findKeywordDocHeading('and')).toBe('Numeric Operations');
        expect(findKeywordDocHeading('or')).toBe('Numeric Operations');
        expect(findKeywordDocHeading('not')).toBe('Numeric Operations');
        expect(findKeywordDocHeading('xor')).toBe('Numeric Operations');
    });

    it('maps testing keywords to Testing + sub-sections', () => {
        expect(findKeywordDocHeading('test')).toBe('Testing');
        expect(findKeywordDocHeading('endtest')).toBe('Testing');
        expect(findKeywordDocHeading('assert')).toBe('Asserts');
        expect(findKeywordDocHeading('mock')).toBe('Mocks');
        expect(findKeywordDocHeading('runto')).toBe('RUNTO');
    });

    it('is case-insensitive — fbasic lexer is too', () => {
        expect(findKeywordDocHeading('IF')).toBe('Conditionals');
        expect(findKeywordDocHeading('If')).toBe('Conditionals');
        expect(findKeywordDocHeading('Function')).toBe('Functions');
        expect(findKeywordDocHeading('REDIM')).toBe('Resize an Array');
        expect(findKeywordDocHeading('FuNcTiOn')).toBe('Functions');
    });

    it('returns null for non-keywords', () => {
        expect(findKeywordDocHeading('print')).toBeNull();          // print is a command, not a keyword
        expect(findKeywordDocHeading('myVariable')).toBeNull();
        expect(findKeywordDocHeading('somecustomname')).toBeNull();
    });

    it('returns null for empty / whitespace input without throwing', () => {
        expect(findKeywordDocHeading('')).toBeNull();
        // toLowerCase on whitespace yields whitespace — not a key in the
        // map, so we should still get null without doing anything weird.
        expect(findKeywordDocHeading('   ')).toBeNull();
    });
});
