import { z } from 'zod';
import { defineTool } from './index';
import { lspFixHint } from './lsp-fix-hint';

export const createFile = defineTool({
    name: 'create_file',
    description:
        'Create a new workspace file with the given content. Fails if the file already exists ' +
        '— use apply_edit to modify existing files. New .fbasic/.fade files are automatically ' +
        'added to the project sources (fade.json) so they run. Prefer apply_edit on an existing ' +
        'source file when adding runnable code, rather than creating a new file. User approves first.',
    // Optional at the schema layer so a malformed call reaches execute() for
    // an instructive error rather than a terse Zod stub. content aliases as
    // newText (models reuse the apply_edit field name).
    schema: z.object({
        path: z.string().optional().describe('New filename'),
        content: z.string().optional().describe('Complete file content'),
        newText: z.string().optional(),
    }),
    async execute(args, ctx) {
        const path = args.path;
        const content = args.content ?? args.newText;
        if (!path || content === undefined) {
            const missing = [!path && 'path', content === undefined && 'content'].filter(Boolean).join(', ');
            return {
                ok: false,
                result: {
                    error: `create_file is missing required argument(s): ${missing}.`,
                    correctFormat:
                        '<tool_call>{"name":"create_file","args":{"path":"new.fbasic","content":"print \\"hi\\""}}</tool_call>',
                    hint: 'Put path and content inside "args". Retry now with the complete call.',
                },
            };
        }

        // Reject if it already exists.
        try {
            await ctx.workspace.read(path);
            return {
                ok: false,
                result: { error: `File already exists: ${path}. Use apply_edit to modify.` },
            };
        } catch {
            /* expected — file does not exist */
        }

        if (ctx.reviewEdit) {
            if (ctx.abortSignal?.aborted) {
                return { ok: false, result: { error: 'Cancelled' } };
            }
            ctx.onEditReviewStart?.();
            try {
                const review = await ctx.reviewEdit({ path, oldContent: '', newContent: content });
                if (!review.approved) {
                    return {
                        ok: false,
                        result: {
                            error: 'Code review rejected the new file',
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
            const approved = await ctx.confirmEdit(path, '', content);
            if (!approved) {
                return { ok: false, result: { error: 'User rejected the file creation' } };
            }
        }

        await ctx.workspace.write(path, content);

        // A new .fbasic/.fade file does nothing unless it's in fade.json's
        // `sources` — register it so the code actually compiles and runs.
        let addedToSources = false;
        if (/\.(fbasic|fade)$/i.test(path)) {
            try {
                const cfg = JSON.parse(await ctx.workspace.read('fade.json')) as { sources?: unknown };
                if (Array.isArray(cfg.sources) && !cfg.sources.includes(path)) {
                    cfg.sources.push(path);
                    await ctx.workspace.write('fade.json', JSON.stringify(cfg, null, 2) + '\n');
                    addedToSources = true;
                }
            } catch { /* no/unparseable fade.json — leave it */ }
        }
        return {
            ok: true,
            result: {
                path,
                created: true,
                addedToSources,
                note: addedToSources
                    ? `Added ${path} to fade.json sources. Reload/re-run the project to pick it up.`
                    : undefined,
            },
        };
    },
});
