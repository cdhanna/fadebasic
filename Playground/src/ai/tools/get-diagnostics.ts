import { z } from 'zod';
import { defineTool } from './index';

const SEVERITY_ORDER: Record<string, number> = {
    error: 0,
    warning: 1,
    info: 2,
    hint: 3,
};

export const getDiagnostics = defineTool({
    name: 'get_diagnostics',
    description:
        'Read LSP errors, warnings, and info messages for the workspace. Omit `path` to ' +
        'get diagnostics for all files. Pass `minSeverity` to filter (e.g. "warning" returns ' +
        'errors + warnings, drops info/hint). Returns position info (1-indexed line/column) ' +
        'so you can read the file at that location with read_file.',
    schema: z.object({
        path: z.string().optional().describe('Workspace-relative filename. Omit for all files.'),
        minSeverity: z.enum(['error', 'warning', 'info', 'hint']).optional()
            .describe('Lowest severity to include. Default: include everything.'),
        limit: z.number().int().min(1).max(100).optional()
            .describe('Cap the number of returned diagnostics (default 30).'),
    }),
    readOnly: true,
    async execute(args, ctx) {
        if (!ctx.diagnostics) {
            return {
                ok: false,
                result: { error: 'Diagnostics not available in this context (no LSP adapter).' },
            };
        }

        const all = args.path
            ? await ctx.diagnostics.forFile(args.path)
            : await ctx.diagnostics.getAll();

        const minSev = args.minSeverity ?? 'hint';
        const minRank = SEVERITY_ORDER[minSev];
        const filtered = all.filter(d => SEVERITY_ORDER[d.severity] <= minRank);

        // Sort: severity (errors first), then file, then line.
        filtered.sort((a, b) => {
            const sev = SEVERITY_ORDER[a.severity] - SEVERITY_ORDER[b.severity];
            if (sev !== 0) return sev;
            const path = a.path.localeCompare(b.path);
            if (path !== 0) return path;
            return a.line - b.line;
        });

        const limit = args.limit ?? 30;
        const truncated = filtered.length > limit;
        const slice = filtered.slice(0, limit);

        // Counts by severity — useful summary even when truncated.
        const counts = filtered.reduce<Record<string, number>>((acc, d) => {
            acc[d.severity] = (acc[d.severity] ?? 0) + 1;
            return acc;
        }, {});

        return {
            ok: true,
            result: {
                total: filtered.length,
                returned: slice.length,
                truncated,
                counts,
                diagnostics: slice.map(d => ({
                    path: d.path,
                    severity: d.severity,
                    line: d.line,
                    column: d.column,
                    message: d.message,
                    code: d.code,
                })),
            },
        };
    },
});
