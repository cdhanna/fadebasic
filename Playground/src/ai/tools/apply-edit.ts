import { z } from 'zod';
import { defineTool } from './index';
import { lspFixHint } from './lsp-fix-hint';

export const applyEdit = defineTool({
    name: 'apply_edit',
    description:
        'Replace a range of lines in a file. startLine and endLine are 1-indexed, inclusive. ' +
        'Use this instead of rewriting whole files — much more reliable. ' +
        'User approves the diff before it is written.',
    // The protocol teaches the attribute form (start=/end=), so models often
    // pass those names — or string numbers — when they use JSON instead.
    // Accept the aliases and coerce, rather than rejecting with an opaque
    // "Invalid arguments" the model then loops on. Canonical names win.
    // Everything is optional at the SCHEMA layer so a malformed first call
    // (common: the model emits `{"name":"apply_edit"}` with no args) reaches
    // execute() and gets an instructive error showing the exact format —
    // instead of a terse Zod "expected string, received undefined" stub it
    // then loops on. execute() does the real required-field validation.
    schema: z.object({
        path: z.string().optional().describe('Filename to edit'),
        startLine: z.coerce.number().int().min(1).optional().describe('First line to replace (1-indexed, inclusive)'),
        endLine: z.coerce.number().int().min(1).optional().describe('Last line to replace (1-indexed, inclusive)'),
        newText: z.string().optional().describe('Replacement text (may contain newlines)'),
        // Accepted aliases:
        start: z.coerce.number().int().min(1).optional(),
        end: z.coerce.number().int().min(1).optional(),
        content: z.string().optional(),
    }),
    async execute(args, ctx) {
        const path = args.path;
        const startLine = args.startLine ?? args.start;
        const endLine = args.endLine ?? args.end;
        const newText = args.newText ?? args.content;
        if (!path || startLine === undefined || endLine === undefined || newText === undefined) {
            const missing = [
                !path && 'path',
                startLine === undefined && 'startLine',
                endLine === undefined && 'endLine',
                newText === undefined && 'newText',
            ].filter(Boolean).join(', ');
            return {
                ok: false,
                result: {
                    error: `apply_edit is missing required argument(s): ${missing}.`,
                    got: Object.keys(args),
                    correctFormat:
                        '<tool_call>{"name":"apply_edit","args":{"path":"main.fbasic","startLine":2,"endLine":2,"newText":"print \\"hi\\""}}</tool_call>',
                    hint: 'Put ALL of path, startLine, endLine, and newText inside "args". Retry now with the complete call.',
                },
            };
        }

        let rawOldContent: string;
        try {
            rawOldContent = await ctx.workspace.read(path);
        } catch {
            return { ok: false, result: { error: `File not found: ${path}` } };
        }

        // Normalize line endings before splicing + diffing. Without this,
        // a file saved with CRLF + a model that emits LF-only newText
        // produces a newContent with mixed line endings, and the diff
        // viewer then sees almost every line as "changed" because the
        // stored \r doesn't match the new lines. (Observed in the field
        // as a +192/-191 diff for a single-line edit.) We preserve the
        // original line-ending style when writing back, so the user's
        // file isn't reformatted as a side effect.
        const originalUsedCRLF = rawOldContent.includes('\r\n');
        const oldContent = rawOldContent.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
        const newTextNormalized = newText.replace(/\r\n/g, '\n').replace(/\r/g, '\n');

        const lines = oldContent.split('\n');
        if (startLine < 1 || startLine > lines.length + 1) {
            return {
                ok: false,
                result: {
                    error: `startLine ${startLine} out of range (file has ${lines.length} lines)`,
                },
            };
        }
        if (endLine < startLine || endLine > lines.length) {
            return {
                ok: false,
                result: {
                    error: `endLine ${endLine} out of range (must be >= startLine ${startLine}, <= ${lines.length})`,
                },
            };
        }

        const newLines = [
            ...lines.slice(0, startLine - 1),
            ...newTextNormalized.split('\n'),
            ...lines.slice(endLine),
        ];
        const newContent = newLines.join('\n');

        if (ctx.reviewEdit) {
            if (ctx.abortSignal?.aborted) {
                return { ok: false, result: { error: 'Cancelled' } };
            }
            ctx.onEditReviewStart?.();
            try {
                const review = await ctx.reviewEdit({ path, oldContent, newContent });
                if (!review.approved) {
                    return {
                        ok: false,
                        result: {
                            error: 'Code review rejected the edit',
                            review: review.feedback,
                            hint: lspFixHint(review.feedback),
                        },
                    };
                }
            } finally {
                ctx.onEditReviewEnd?.();
            }
        }

        if (ctx.confirmEdit) {
            // Diff against the normalized old content so the user sees a
            // clean diff (no phantom line-ending changes).
            const approved = await ctx.confirmEdit(path, oldContent, newContent);
            if (!approved) {
                return { ok: false, result: { error: 'User rejected the edit' } };
            }
        }

        // Write back using the file's original line-ending style.
        const finalContent = originalUsedCRLF
            ? newContent.replace(/\n/g, '\r\n')
            : newContent;
        await ctx.workspace.write(path, finalContent);
        return {
            ok: true,
            result: { path, linesReplaced: endLine - startLine + 1 },
        };
    },
});
