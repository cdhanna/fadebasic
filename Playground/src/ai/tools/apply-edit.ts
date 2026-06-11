import { z } from 'zod';
import { defineTool } from './index';

export const applyEdit = defineTool({
    name: 'apply_edit',
    description:
        'Replace a range of lines in a file. startLine and endLine are 1-indexed, inclusive. ' +
        'Use this instead of rewriting whole files — much more reliable. ' +
        'User approves the diff before it is written.',
    schema: z.object({
        path: z.string().describe('Filename to edit'),
        startLine: z.number().int().min(1).describe('First line to replace (1-indexed, inclusive)'),
        endLine: z.number().int().min(1).describe('Last line to replace (1-indexed, inclusive)'),
        newText: z.string().describe('Replacement text (may contain newlines)'),
    }),
    async execute(args, ctx) {
        const { path, startLine, endLine, newText } = args;

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
