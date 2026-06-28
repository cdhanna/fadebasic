import { describe, it, expect } from 'vitest';
import { lineDiff, lineDiffTriState } from './line-diff';

describe('lineDiff', () => {
    it('identical input → no marks, no deletions', () => {
        const r = lineDiff('a\nb\nc\n', 'a\nb\nc\n');
        expect(r.marks).toEqual([]);
        expect(r.deletions).toEqual([]);
    });

    it('pure addition at end', () => {
        const r = lineDiff('a\nb\n', 'a\nb\nc\nd\n');
        expect(r.marks).toEqual([
            { line: 3, kind: 'added' },
            { line: 4, kind: 'added' },
        ]);
        expect(r.deletions).toEqual([]);
    });

    it('pure addition at start', () => {
        const r = lineDiff('b\nc\n', 'a\nb\nc\n');
        expect(r.marks).toEqual([{ line: 1, kind: 'added' }]);
    });

    it('pure deletion at end → recorded as deletion', () => {
        const r = lineDiff('a\nb\nc\n', 'a\n');
        expect(r.marks).toEqual([]);
        expect(r.deletions).toEqual([{ line: 1, count: 2 }]);
    });

    it('pure deletion at start → deletion before line 1', () => {
        const r = lineDiff('a\nb\nc\n', 'c\n');
        expect(r.deletions).toEqual([{ line: 0, count: 2 }]);
    });

    it('single-line modification → mark as modified', () => {
        const r = lineDiff('a\nb\nc\n', 'a\nB\nc\n');
        expect(r.marks).toEqual([{ line: 2, kind: 'modified' }]);
        expect(r.deletions).toEqual([]);
    });

    it('replace 2 lines with 1 → modified mark, no separate deletion', () => {
        const r = lineDiff('a\nx\ny\nb\n', 'a\nz\nb\n');
        expect(r.marks).toEqual([{ line: 2, kind: 'modified' }]);
        expect(r.deletions).toEqual([]);
    });

    it('replace 1 line with 2 → both new lines marked modified', () => {
        const r = lineDiff('a\nx\nb\n', 'a\ny\nz\nb\n');
        expect(r.marks).toEqual([
            { line: 2, kind: 'modified' },
            { line: 3, kind: 'modified' },
        ]);
    });

    it('empty base → every current line is added', () => {
        const r = lineDiff('', 'a\nb\nc\n');
        expect(r.marks).toEqual([
            { line: 1, kind: 'added' },
            { line: 2, kind: 'added' },
            { line: 3, kind: 'added' },
        ]);
    });

    it('empty current → single deletion record covering all base lines', () => {
        const r = lineDiff('a\nb\nc\n', '');
        expect(r.deletions).toEqual([{ line: 0, count: 3 }]);
    });

    it('addition followed by deletion in separate regions', () => {
        // a, b, c → a, NEW, c (line b deleted between c... wait)
        // base:    a b c
        // current: a b X
        const r = lineDiff('a\nb\nc\n', 'a\nb\nX\n');
        expect(r.marks).toEqual([{ line: 3, kind: 'modified' }]);
    });
});

describe('lineDiffTriState', () => {
    it('no saves yet (savedRef === publishedRef) → all changes are "unsaved"', () => {
        const pub = 'a\nb\nc\n';
        const r = lineDiffTriState(pub, pub, 'a\nX\nc\n');
        expect(r.unsavedLines).toEqual([2]);
        expect(r.savedLines).toEqual([]);
    });

    it('saved change only (current === savedRef, differs from publishedRef)', () => {
        // User edited line 2 → saved → no further edits.
        const r = lineDiffTriState(
            'a\nb\nc\n',   // published
            'a\nS\nc\n',   // saved
            'a\nS\nc\n',   // current matches save
        );
        expect(r.unsavedLines).toEqual([]);
        expect(r.savedLines).toEqual([2]);
    });

    it('unsaved change on top of a saved change (different lines)', () => {
        // Published:  a b c d
        // Saved:      a S c d   (line 2 modified, saved)
        // Current:    a S c U   (line 4 modified since save, NOT saved)
        const r = lineDiffTriState(
            'a\nb\nc\nd\n',
            'a\nS\nc\nd\n',
            'a\nS\nc\nU\n',
        );
        expect(r.unsavedLines).toEqual([4]);
        expect(r.savedLines).toEqual([2]);
    });

    it('unsaved edit on top of a saved edit on the SAME line → unsaved wins', () => {
        // Published:  a b c
        // Saved:      a S c
        // Current:    a U c   (re-edited the saved line; now unsaved)
        const r = lineDiffTriState(
            'a\nb\nc\n',
            'a\nS\nc\n',
            'a\nU\nc\n',
        );
        // Line 2 differs from both saved and published.  It's "unsaved"
        // and NOT additionally counted as "saved" (set difference).
        expect(r.unsavedLines).toEqual([2]);
        expect(r.savedLines).toEqual([]);
    });

    it('saved deletion (lines removed in save, current matches save)', () => {
        const r = lineDiffTriState(
            'a\nb\nc\n',    // published
            'a\nc\n',       // saved — b removed
            'a\nc\n',       // current matches save
        );
        expect(r.unsavedLines).toEqual([]);
        expect(r.unsavedDeletions).toEqual([]);
        expect(r.savedDeletions).toEqual([{ line: 1, count: 1 }]);
    });

    it('unsaved deletion stacked on saved edit', () => {
        // Published:  a b c d
        // Saved:      a S c d   (line 2 modified b→S, saved)
        // Current:    a S d     (user deleted line c since save)
        const r = lineDiffTriState(
            'a\nb\nc\nd\n',
            'a\nS\nc\nd\n',
            'a\nS\nd\n',
        );
        // Saved-modified line 2 still tracked, deletion of c is unsaved.
        expect(r.savedLines).toEqual([2]);
        expect(r.unsavedDeletions).toEqual([{ line: 2, count: 1 }]);
    });

    it('matches `lineDiff` semantics when savedRef === publishedRef', () => {
        const pub = 'a\nb\nc\n';
        const cur = 'a\nB\nc\nD\n';
        const tri = lineDiffTriState(pub, pub, cur);
        const single = lineDiff(pub, cur);
        expect(tri.unsavedLines).toEqual(single.marks.map((m) => m.line));
        expect(tri.savedLines).toEqual([]);
    });
});
