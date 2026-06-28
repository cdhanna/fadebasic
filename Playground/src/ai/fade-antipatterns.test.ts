import { describe, it, expect } from 'vitest';
import { detectFadeAntiPatterns } from './fade-antipatterns';

describe('detectFadeAntiPatterns', () => {
    it('flags spaced block-enders with the one-word fix', () => {
        const out = detectFadeAntiPatterns('function f()\nend function\nwhile 1\nend while');
        expect(out.some(s => s.includes('endfunction'))).toBe(true);
        expect(out.some(s => s.includes('endwhile'))).toBe(true);
    });

    it('does NOT flag compound assignment (it is valid Fade)', () => {
        // `+=` `-=` `*=` `/=` are real Fade shorthand — the parser desugars them.
        expect(detectFadeAntiPatterns('y -= 1\nx += 2')).toEqual([]);
    });

    it('flags while true', () => {
        const out = detectFadeAntiPatterns('while true\n  sync\nend while');
        expect(out.some(s => /true.*false|numeric/i.test(s))).toBe(true);
    });

    it('catches the user\'s spaceship snippet mistakes', () => {
        const code = [
            'function update()',
            '  if key down "up" then y -= 1',
            'end function',
            'while true',
            '  update()',
            'end while',
        ].join('\n');
        const out = detectFadeAntiPatterns(code);
        // endfunction, endwhile, compound assignment, while true
        expect(out.length).toBeGreaterThanOrEqual(3);
    });

    it('flags `wend` as a non-existent WHILE closer', () => {
        const out = detectFadeAntiPatterns('while 1\n  sync\nwend');
        expect(out.some(s => /endwhile/i.test(s) && /wend/i.test(s))).toBe(true);
    });

    it('flags `loop while` / `loop until`', () => {
        expect(detectFadeAntiPatterns('do\n  sync\nloop while x').some(s => /loop while/i.test(s))).toBe(true);
    });

    it('flags a single-line IF/THEN followed by a stray ENDIF', () => {
        const out = detectFadeAntiPatterns('if x > 0 then print "hi"\nendif');
        expect(out.some(s => /single-line.*ENDIF/i.test(s))).toBe(true);
    });

    it('does not flag a block IF that legitimately uses ENDIF', () => {
        expect(detectFadeAntiPatterns('if x > 0\n  print "hi"\nendif')).toEqual([]);
    });

    it('does not flag a single-line IF/THEN with no ENDIF', () => {
        expect(detectFadeAntiPatterns('if x > 0 then print "hi"')).toEqual([]);
    });

    it('returns nothing for clean code', () => {
        expect(detectFadeAntiPatterns('x = 0\nif x > 0\n  print x\nendif')).toEqual([]);
    });
});
