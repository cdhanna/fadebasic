import { describe, it, expect, vi } from 'vitest';
import { createProjectAwareLspEditValidator } from './lsp-validate-edit';
import { formatDiagnosticFeedback } from '../lsp-diagnostic-format';

describe('createProjectAwareLspEditValidator', () => {
    it('validates project sources on joined doc URI', async () => {
        const checkDiagnostics = vi.fn(async () => [{
            severity: 1,
            range: { start: { line: 0, character: 0 }, end: { line: 0, character: 4 } },
            message: 'unknown identifier prnt',
            code: 'E001',
        }]);
        const restoreProjectDoc = vi.fn();

        const validate = createProjectAwareLspEditValidator({
            projectLspUri: 'file:///workspace/__fade_project__.fbasic',
            readProjectSources: () => [
                { name: 'main.fbasic', text: 'print "a"\n' },
                { name: 'lib.fbasic', text: 'function foo\nend function\n' },
            ],
            isProjectSource: (p) => p === 'main.fbasic',
            checkDiagnostics,
            restoreProjectDoc,
        });

        const entries = await validate('main.fbasic', 'prnt "a"\n');
        expect(checkDiagnostics).toHaveBeenCalledWith(
            'file:///workspace/__fade_project__.fbasic',
            'prnt "a"\nfunction foo\nend function\n',
        );
        expect(entries).toHaveLength(1);
        expect(entries[0].path).toBe('main.fbasic');
        expect(entries[0].line).toBe(1);
        expect(restoreProjectDoc).toHaveBeenCalled();
    });

    it('validates orphan files on per-file URI', async () => {
        const checkDiagnostics = vi.fn(async () => []);
        const validate = createProjectAwareLspEditValidator({
            projectLspUri: 'file:///workspace/__fade_project__.fbasic',
            readProjectSources: () => [],
            isProjectSource: () => false,
            checkDiagnostics,
            restoreProjectDoc: () => {},
        });

        await validate('orphan.fbasic', 'print "x"\n');
        expect(checkDiagnostics).toHaveBeenCalledWith(
            'file:///workspace/orphan.fbasic',
            'print "x"\n',
        );
    });
});

describe('formatDiagnosticFeedback', () => {
    it('formats errors for reviewer feedback', () => {
        const text = formatDiagnosticFeedback([{
            path: 'a.fbasic',
            severity: 'error',
            line: 3,
            column: 2,
            endLine: 3,
            endColumn: 10,
            message: 'expected end function',
            code: 'E001',
        }]);
        expect(text).toContain('LSP compile errors');
        expect(text).toContain('L3: expected end function');
    });

    it('returns empty when no errors', () => {
        expect(formatDiagnosticFeedback([{
            path: 'a.fbasic',
            severity: 'warning',
            line: 1,
            column: 1,
            endLine: 1,
            endColumn: 1,
            message: 'unused',
        }])).toBe('');
    });
});
