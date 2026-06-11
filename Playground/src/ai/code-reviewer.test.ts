import { describe, it, expect, vi } from 'vitest';
import { parseReviewVerdict, reviewProposedEdit } from './code-reviewer';
import type { ChatProvider } from './providers/types';

describe('reviewProposedEdit', () => {
    it('rejects immediately when LSP reports errors', async () => {
        const provider = { stream: vi.fn() } as unknown as ChatProvider;
        const result = await reviewProposedEdit(provider, {
            path: 'main.fbasic',
            oldContent: 'print "a"',
            newContent: 'prnt "a"',
            validateContent: async () => [{
                path: 'main.fbasic',
                severity: 'error',
                line: 1,
                column: 1,
                endLine: 1,
                endColumn: 5,
                message: 'unknown identifier prnt',
            }],
        });
        expect(result.approved).toBe(false);
        expect(result.feedback).toContain('LSP compile errors');
        expect(provider.stream).not.toHaveBeenCalled();
    });

    it('approves on clean LSP without LLM pass by default', async () => {
        const provider = { stream: vi.fn() } as unknown as ChatProvider;
        const result = await reviewProposedEdit(provider, {
            path: 'main.fbasic',
            oldContent: 'print "a"',
            newContent: 'print "b"',
            validateContent: async () => [],
        });
        expect(result.approved).toBe(true);
        expect(provider.stream).not.toHaveBeenCalled();
    });

    it('surfaces LSP timeout as rejection', async () => {
        const provider = { stream: vi.fn() } as unknown as ChatProvider;
        const result = await reviewProposedEdit(provider, {
            path: 'main.fbasic',
            oldContent: 'print "a"',
            newContent: 'print "b"',
            validateContent: () => new Promise(() => { /* never resolves */ }),
        });
        expect(result.approved).toBe(false);
        expect(result.feedback).toContain('timed out');
    }, 10_000);
});

describe('parseReviewVerdict', () => {
    it('approves explicit APPROVE', () => {
        expect(parseReviewVerdict('APPROVE')).toEqual({ approved: true, feedback: '' });
    });

    it('rejects ISSUES lines', () => {
        const r = parseReviewVerdict('ISSUES: missing end function');
        expect(r.approved).toBe(false);
        expect(r.feedback).toContain('end function');
    });

});
