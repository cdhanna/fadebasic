import { describe, it, expect } from 'vitest';
import { levenshtein, suggestCommands, detectUnknownCommands, detectCommandAsVariable, detectMissingValueCallParens, detectAssetExtension, detectAssignToCommandCall } from './fade-command-check';

const COMMANDS = ['key down', 'key up', 'mouse x', 'mouse y', 'print', 'sync', 'sprite', 'load image'];

describe('levenshtein', () => {
    it('is zero for equal strings and counts edits', () => {
        expect(levenshtein('abc', 'abc')).toBe(0);
        expect(levenshtein('keydown', 'key down')).toBe(1); // one inserted space
        expect(levenshtein('', 'abc')).toBe(3);
    });
});

describe('suggestCommands', () => {
    it('matches a collapsed multi-word command', () => {
        expect(suggestCommands('keydown', COMMANDS)).toContain('key down');
    });
    it('returns nothing for an unrelated name', () => {
        expect(suggestCommands('computePlayerScore', COMMANDS)).toEqual([]);
    });
    it('ignores too-short names', () => {
        expect(suggestCommands('go', COMMANDS)).toEqual([]);
    });

    it('does not suggest a distant short word (no misleading "abs → asc")', () => {
        // `abs` vs `asc` is edit-distance 2 — too far for a 3-char name now.
        expect(suggestCommands('abs', ['asc', 'sprite', 'key down'])).toEqual([]);
    });
});

describe('detectUnknownCommands', () => {
    it('flags an invented command that resembles a real one', () => {
        const out = detectUnknownCommands('x = keydown()', COMMANDS);
        expect(out).toHaveLength(1);
        expect(out[0]).toContain('key down');
    });

    it('does not flag a real command called with parens', () => {
        expect(detectUnknownCommands('mx = mouse x()', COMMANDS)).toEqual([]);
    });

    it('does not flag a user-defined function or its call', () => {
        const code = [
            'function computeScore(a, b)',
            'endfunction a + b',
            's = computeScore(1, 2)',
        ].join('\n');
        expect(detectUnknownCommands(code, COMMANDS)).toEqual([]);
    });

    it('does not flag an array access for an array declared in the code', () => {
        const code = 'dim health(10)\nh = health(3)';
        expect(detectUnknownCommands(code, COMMANDS)).toEqual([]);
    });

    it('does not flag a name unrelated to any command (likely a cross-file symbol)', () => {
        expect(detectUnknownCommands('v = computeVelocity(p)', COMMANDS)).toEqual([]);
    });

    it('is a no-op without a command list', () => {
        expect(detectUnknownCommands('x = keydown()', [])).toEqual([]);
    });
});

describe('detectCommandAsVariable', () => {
    it('flags assigning to a command name', () => {
        const out = detectCommandAsVariable('sprite = sprite(0)', COMMANDS);
        expect(out).toHaveLength(1);
        expect(out[0]).toContain('`sprite`');
        expect(out[0]).toContain('command');
    });

    it('flags a GLOBAL/LOCAL-declared command-named variable', () => {
        expect(detectCommandAsVariable('global print = 5', COMMANDS)).toHaveLength(1);
    });

    it('does not flag a normal variable', () => {
        expect(detectCommandAsVariable('ship = 0\nx = 100', COMMANDS)).toEqual([]);
    });

    it('does not flag an array-element write', () => {
        expect(detectCommandAsVariable('sprite(3) = 1', COMMANDS)).toEqual([]);
    });

    it('does not flag a comparison', () => {
        expect(detectCommandAsVariable('if sprite == 0 then end', COMMANDS)).toEqual([]);
    });

    it('is a no-op without a command list', () => {
        expect(detectCommandAsVariable('sprite = 0', [])).toEqual([]);
    });
});

describe('detectMissingValueCallParens', () => {
    const valueCmds = ['mouse x', 'mouse y', 'leftKey', 'rgb'];

    it('flags multi-word value commands used as args without parens', () => {
        const out = detectMissingValueCallParens('position sprite 1, mouse x, mouse y', valueCmds);
        expect(out.some(s => s.includes('`mouse x`'))).toBe(true);
        expect(out.some(s => s.includes('`mouse y`'))).toBe(true);
    });

    it('does not flag a value command already called with parens', () => {
        expect(detectMissingValueCallParens('mx = mouse x()', valueCmds)).toEqual([]);
    });

    it('flags a value command in a condition', () => {
        expect(detectMissingValueCallParens('if leftKey then x = 1', valueCmds).length).toBe(1);
    });

    it('does NOT flag a void/statement command (not in the value set)', () => {
        // `texture` is not value-returning → never flagged (the old misfire).
        expect(detectMissingValueCallParens('texture 1, "ship"', valueCmds)).toEqual([]);
    });

    it('does not flag a value command invoked at statement position', () => {
        // `rgb` at line start (statement) isn't in value position → not flagged.
        expect(detectMissingValueCallParens('rgb 255, 0, 0', valueCmds)).toEqual([]);
    });

    it('is a no-op without a value-command list', () => {
        expect(detectMissingValueCallParens('position sprite 1, mouse x', [])).toEqual([]);
    });
});

describe('detectAssignToCommandCall', () => {
    const cmds = ['sprite x', 'sprite y', 'mouse x', 'sprite', 'position sprite'];

    it('flags assigning to a value-returning command call', () => {
        const out = detectAssignToCommandCall('sprite x(1) = sprite x(1) + 1', cmds);
        expect(out).toHaveLength(1);
        expect(out[0]).toContain('`sprite x`');
        expect(out[0]).toContain('position sprite');
    });

    it('flags assigning to a no-arg command result', () => {
        expect(detectAssignToCommandCall('mouse x() = 0', cmds)).toHaveLength(1);
    });

    it('does not flag reading a command in an expression', () => {
        expect(detectAssignToCommandCall('x = sprite x(1) + 1', cmds)).toEqual([]);
    });

    it('does not flag a comparison', () => {
        expect(detectAssignToCommandCall('if sprite x(1) == 0 then end', cmds)).toEqual([]);
    });

    it('does not flag a user array-element write', () => {
        expect(detectAssignToCommandCall('dim health(10)\nhealth(3) = 5', cmds)).toEqual([]);
    });

    it('does not flag a normal call statement (no assignment)', () => {
        expect(detectAssignToCommandCall('position sprite 1, x, y', cmds)).toEqual([]);
    });

    it('is a no-op without a command list', () => {
        expect(detectAssignToCommandCall('sprite x(1) = 5', [])).toEqual([]);
    });
});

describe('detectAssetExtension', () => {
    it('flags texture with a .png extension and suggests the bare name', () => {
        const out = detectAssetExtension('texture 1, "ship.png"');
        expect(out).toHaveLength(1);
        expect(out[0]).toContain('"ship"');
        expect(out[0]).toContain('.png');
    });

    it('flags the multi-word `load sfx clip` loader with .wav', () => {
        const out = detectAssetExtension('load sfx clip 1, "laser.wav"');
        expect(out).toHaveLength(1);
        expect(out[0]).toContain('"laser"');
    });

    it('flags font and effect loaders too', () => {
        expect(detectAssetExtension('font 1, "arial.ttf"')).toHaveLength(1);
        expect(detectAssetExtension('effect 1, "blur.fx"')).toHaveLength(1);
    });

    it('does not flag an extension-less asset path', () => {
        expect(detectAssetExtension('texture 1, "ship"')).toEqual([]);
    });

    it('does not flag a string literal in a non-loader statement', () => {
        expect(detectAssetExtension('print "saved to file.png"')).toEqual([]);
    });

    it('does not flag a comment line', () => {
        expect(detectAssetExtension('` load the texture 1, "ship.png"')).toEqual([]);
    });
});
