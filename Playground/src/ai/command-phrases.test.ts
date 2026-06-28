import { describe, it, expect } from 'vitest';
import { extractCommandPhrases, detectMissingCallParens } from './command-phrases';

describe('detectMissingCallParens', () => {
    const cmds = ['leftKey', 'rightKey', 'mouseX', 'print', 'sync'];

    it('flags a bare value-returning command in a condition', () => {
        const out = detectMissingCallParens('if leftKey then x = x - 1', cmds);
        expect(out).toHaveLength(1);
        expect(out[0]).toContain('leftKey()');
    });

    it('does not flag a command already called with parens', () => {
        expect(detectMissingCallParens('if leftKey() then x = 1', cmds)).toEqual([]);
    });

    it('does not flag plain variables or statement commands', () => {
        expect(detectMissingCallParens('if x > 0 then print x', cmds)).toEqual([]);
        expect(detectMissingCallParens('sync', cmds)).toEqual([]);
    });

    it('flags a command on an assignment RHS', () => {
        const out = detectMissingCallParens('mx = mouseX', cmds);
        expect(out[0]).toContain('mouseX()');
    });
});

describe('extractCommandPhrases', () => {
    it('extracts a multi-word command from inside an IF', () => {
        const code = 'IF key down "left" THEN x = x - 1';
        expect(extractCommandPhrases(code)).toEqual(['key down']);
    });

    it('handles the ship-movement example (only the real command guess)', () => {
        const code = [
            'GLOBAL x = 0 : GLOBAL y = 0',
            'FOR t = 1 TO 1000',
            '  IF key down "left" THEN x = x - 1',
            '  IF key down "right" THEN x = x + 1',
            '  print x, y',
            'NEXT t',
        ].join('\n');
        // "key down" surfaces; assignments and bare `print x` do not.
        expect(extractCommandPhrases(code)).toEqual(['key down']);
    });

    it('extracts commands with numeric / paren / string args', () => {
        const code = [
            'load image 1, "ship.png"',
            'texture 1, "Images/Ball"',
            'clip = reserve sfx clip id(0)',
        ].join('\n');
        const phrases = extractCommandPhrases(code);
        expect(phrases).toContain('load image');
        expect(phrases).toContain('texture');
        expect(phrases).toContain('reserve sfx clip id');
    });

    it('ignores comments and pure assignments', () => {
        expect(extractCommandPhrases('` just a comment\nx = 5\ny = x + 1')).toEqual([]);
    });
});
