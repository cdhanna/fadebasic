import { describe, it, expect } from 'vitest';
import { lspFixHint } from './lsp-fix-hint';

describe('lspFixHint', () => {
    it('points "no overload" errors at search_docs', () => {
        const h = lspFixHint('L4: [0147] No overload for command (147)');
        expect(h).toMatch(/search_docs/i);
        expect(h).toMatch(/argument/i);
    });

    it('explains unknown-symbol errors (declare first / not a command)', () => {
        const h = lspFixHint('L8: [0200] Invalid reference | unknown symbol, x (200)');
        expect(h).toMatch(/before/i);
        expect(h).toMatch(/command/i);
    });

    it('covers ambiguous declaration/assignment', () => {
        const h = lspFixHint('L6: [0107] Statement is ambiguous between a declaration or assignment (107)');
        expect(h).toMatch(/initialize/i);
    });

    it('returns undefined for errors it has no specific advice for', () => {
        expect(lspFixHint('some unrelated error')).toBeUndefined();
    });
});
