import { describe, it, expect } from 'vitest';
import { getDiagnostics } from './get-diagnostics';
import type { DiagnosticEntry, DiagnosticsProvider, ToolContext, ToolWorkspace } from './index';

const noopWorkspace: ToolWorkspace = {
    list: async () => [],
    read: async () => '',
    write: async () => {},
    currentProject: () => 'test',
};

function mkProvider(diagnostics: DiagnosticEntry[]): DiagnosticsProvider {
    return {
        async getAll() { return diagnostics; },
        async forFile(path) { return diagnostics.filter(d => d.path === path); },
    };
}

function mkCtx(diagnostics: DiagnosticEntry[]): ToolContext {
    return { workspace: noopWorkspace, diagnostics: mkProvider(diagnostics) };
}

const sampleDiags: DiagnosticEntry[] = [
    { path: 'main.fade', severity: 'error',   line: 12, column: 3,  endLine: 12, endColumn: 8,  message: 'Undefined variable', code: '101' },
    { path: 'main.fade', severity: 'warning', line: 30, column: 1,  endLine: 30, endColumn: 4,  message: 'Unused identifier', code: '202' },
    { path: 'main.fade', severity: 'info',    line: 5,  column: 1,  endLine: 5,  endColumn: 12, message: 'Style nit',         code: '303' },
    { path: 'lib.fade',  severity: 'error',   line: 1,  column: 1,  endLine: 1,  endColumn: 2,  message: 'Syntax error',      code: '104' },
    { path: 'lib.fade',  severity: 'hint',    line: 8,  column: 5,  endLine: 8,  endColumn: 9,  message: 'Could be const',    code: '404' },
];

describe('get_diagnostics tool', () => {
    it('returns all diagnostics when no filters are passed', async () => {
        const ctx = mkCtx(sampleDiags);
        const r = await getDiagnostics.execute({}, ctx);
        expect(r.ok).toBe(true);
        const body = r.result as { total: number; diagnostics: unknown[] };
        expect(body.total).toBe(5);
        expect(body.diagnostics).toHaveLength(5);
    });

    it('filters by path', async () => {
        const ctx = mkCtx(sampleDiags);
        const r = await getDiagnostics.execute({ path: 'lib.fade' }, ctx);
        const body = r.result as { diagnostics: Array<{ path: string }> };
        expect(body.diagnostics).toHaveLength(2);
        expect(body.diagnostics.every(d => d.path === 'lib.fade')).toBe(true);
    });

    it('filters by minSeverity=warning (excludes info/hint)', async () => {
        const ctx = mkCtx(sampleDiags);
        const r = await getDiagnostics.execute({ minSeverity: 'warning' }, ctx);
        const body = r.result as { diagnostics: Array<{ severity: string }>; counts: Record<string, number> };
        // Expect: 2 errors + 1 warning = 3
        expect(body.diagnostics).toHaveLength(3);
        expect(body.counts.error).toBe(2);
        expect(body.counts.warning).toBe(1);
        expect(body.counts.info).toBeUndefined();
        expect(body.counts.hint).toBeUndefined();
    });

    it('sorts errors first, then by path, then by line', async () => {
        const ctx = mkCtx(sampleDiags);
        const r = await getDiagnostics.execute({}, ctx);
        const body = r.result as { diagnostics: Array<{ path: string; severity: string; line: number }> };
        const sigs = body.diagnostics.map(d => `${d.severity}:${d.path}:${d.line}`);
        // Both errors first (lib.fade < main.fade), then warning, info, hint
        expect(sigs[0]).toBe('error:lib.fade:1');
        expect(sigs[1]).toBe('error:main.fade:12');
        expect(sigs[2]).toBe('warning:main.fade:30');
    });

    it('respects the limit + reports truncation', async () => {
        const ctx = mkCtx(sampleDiags);
        const r = await getDiagnostics.execute({ limit: 2 }, ctx);
        const body = r.result as { returned: number; truncated: boolean; total: number };
        expect(body.returned).toBe(2);
        expect(body.truncated).toBe(true);
        expect(body.total).toBe(5);
    });

    it('returns an empty list when the file has no diagnostics', async () => {
        const ctx = mkCtx(sampleDiags);
        const r = await getDiagnostics.execute({ path: 'unknown.fade' }, ctx);
        expect(r.ok).toBe(true);
        const body = r.result as { total: number };
        expect(body.total).toBe(0);
    });

    it('returns an error when no diagnostics adapter is available', async () => {
        const ctx: ToolContext = { workspace: noopWorkspace };  // no diagnostics
        const r = await getDiagnostics.execute({}, ctx);
        expect(r.ok).toBe(false);
        expect((r.result as { error: string }).error).toMatch(/not available/i);
    });
});
