import { describe, it, expect } from 'vitest';
import { diff3Merge, hasConflictMarkers, parseConflictRegions } from './diff3';

describe('diff3Merge', () => {
    it('all three identical → no conflicts, unchanged output', () => {
        const r = diff3Merge('a\nb\nc\n', 'a\nb\nc\n', 'a\nb\nc\n');
        expect(r.hasConflicts).toBe(false);
        expect(r.merged).toBe('a\nb\nc\n');
    });

    it('only ours changed → take ours', () => {
        const r = diff3Merge('a\nb\nc\n', 'a\nB\nc\n', 'a\nb\nc\n');
        expect(r.hasConflicts).toBe(false);
        expect(r.merged).toBe('a\nB\nc\n');
    });

    it('only theirs changed → take theirs', () => {
        const r = diff3Merge('a\nb\nc\n', 'a\nb\nc\n', 'a\nb\nZ\n');
        expect(r.hasConflicts).toBe(false);
        expect(r.merged).toBe('a\nb\nZ\n');
    });

    it('both changed identically → no conflict, single take', () => {
        const r = diff3Merge('a\nb\nc\n', 'a\nX\nc\n', 'a\nX\nc\n');
        expect(r.hasConflicts).toBe(false);
        expect(r.merged).toBe('a\nX\nc\n');
    });

    it('non-overlapping changes → both applied without conflict', () => {
        // base:   a b c d e
        // ours:   a B c d e   (changed line 2)
        // theirs: a b c D e   (changed line 4)
        const r = diff3Merge('a\nb\nc\n d\ne\n'.replace(' ', ''), 'a\nB\nc\nd\ne\n', 'a\nb\nc\nD\ne\n');
        expect(r.hasConflicts).toBe(false);
        expect(r.merged).toBe('a\nB\nc\nD\ne\n');
    });

    it('overlapping changes → conflict with markers', () => {
        const r = diff3Merge('a\nb\nc\n', 'a\nMINE\nc\n', 'a\nTHEIRS\nc\n');
        expect(r.hasConflicts).toBe(true);
        expect(r.conflicts.length).toBe(1);
        expect(r.merged).toBe('a\n<<<<<<< ours\nMINE\n=======\nTHEIRS\n>>>>>>> theirs\nc\n');
    });

    it('label override appears in the markers', () => {
        const r = diff3Merge('x\n', 'A\n', 'B\n', { oursLabel: 'my-branch', theirsLabel: 'main' });
        expect(r.merged).toContain('<<<<<<< my-branch');
        expect(r.merged).toContain('>>>>>>> main');
    });

    it('pure addition on each side at different ends → merged', () => {
        // base:   b c
        // ours:   A b c     (prepend)
        // theirs: b c Z     (append)
        const r = diff3Merge('b\nc\n', 'A\nb\nc\n', 'b\nc\nZ\n');
        expect(r.hasConflicts).toBe(false);
        expect(r.merged).toBe('A\nb\nc\nZ\n');
    });

    it('one side deletes a line the other left alone → take the deletion', () => {
        const r = diff3Merge('a\nb\nc\n', 'a\nc\n', 'a\nb\nc\n');
        expect(r.hasConflicts).toBe(false);
        expect(r.merged).toBe('a\nc\n');
    });

    it('both sides delete the same line → take the deletion (same change)', () => {
        const r = diff3Merge('a\nb\nc\n', 'a\nc\n', 'a\nc\n');
        expect(r.hasConflicts).toBe(false);
        expect(r.merged).toBe('a\nc\n');
    });

    it('empty base, both sides added different things → conflict', () => {
        const r = diff3Merge('', 'mine\n', 'theirs\n');
        expect(r.hasConflicts).toBe(true);
        expect(r.merged).toContain('mine');
        expect(r.merged).toContain('theirs');
    });

    it('preserves a no-trailing-newline file', () => {
        const r = diff3Merge('a\nb', 'a\nB', 'a\nb');
        expect(r.merged).toBe('a\nB');
    });

    it('multi-region: independent conflicts at top and bottom', () => {
        // base:   a b c d e
        // ours:   A b c d E
        // theirs: a' b c d e'
        // Two non-overlapping conflicts.
        const r = diff3Merge(
            'a\nb\nc\nd\ne\n',
            'A\nb\nc\nd\nE\n',
            "a'\nb\nc\nd\ne'\n",
        );
        // Both sides changed line 1 differently AND line 5 differently.
        expect(r.hasConflicts).toBe(true);
        expect(r.conflicts.length).toBe(2);
    });
});

describe('parseConflictRegions', () => {
    it('finds a single well-formed region', () => {
        const text = [
            'a',
            '<<<<<<< ours',
            'mine1',
            'mine2',
            '=======',
            'theirs1',
            '>>>>>>> theirs',
            'b',
        ].join('\n');
        const r = parseConflictRegions(text);
        expect(r).toHaveLength(1);
        expect(r[0]).toMatchObject({
            startLine: 2,
            midLine: 5,
            endLine: 7,
            ours: ['mine1', 'mine2'],
            theirs: ['theirs1'],
            oursLabel: 'ours',
            theirsLabel: 'theirs',
        });
    });

    it('finds multiple regions in order', () => {
        const text = [
            '<<<<<<< ours',
            'A',
            '=======',
            'B',
            '>>>>>>> theirs',
            'unchanged',
            '<<<<<<< ours',
            'C',
            '=======',
            'D',
            '>>>>>>> theirs',
        ].join('\n');
        const r = parseConflictRegions(text);
        expect(r).toHaveLength(2);
        expect(r[0].ours).toEqual(['A']);
        expect(r[1].ours).toEqual(['C']);
    });

    it('skips malformed regions (start without end)', () => {
        const text = '<<<<<<< ours\nstuff\n(no end marker)';
        expect(parseConflictRegions(text)).toEqual([]);
    });

    it('extracts labels from the marker lines', () => {
        const text = '<<<<<<< feature-branch\nx\n=======\ny\n>>>>>>> main\n';
        const r = parseConflictRegions(text);
        expect(r[0].oursLabel).toBe('feature-branch');
        expect(r[0].theirsLabel).toBe('main');
    });
});

describe('hasConflictMarkers', () => {
    it('detects all three marker lines', () => {
        expect(hasConflictMarkers('a\n<<<<<<< ours\nx\n=======\ny\n>>>>>>> theirs\nb\n')).toBe(true);
    });
    it('ignores text that mentions <<<<<<< inline (not at line start)', () => {
        expect(hasConflictMarkers('this is fine: <<<<<<< inside a comment')).toBe(false);
    });
    it('clean text returns false', () => {
        expect(hasConflictMarkers('print "hi"\n')).toBe(false);
    });
    it('returns true if any single marker line is present', () => {
        expect(hasConflictMarkers('blah\n=======\nblah\n')).toBe(true);
    });
});
