import { z } from 'zod';
import { defineTool } from './index';

export const createFile = defineTool({
    name: 'create_file',
    description:
        'Create a new workspace file with the given content. Fails if the file already exists ' +
        '— use apply_edit to modify existing files. User approves before the file is written.',
    schema: z.object({
        path: z.string().describe('New filename'),
        content: z.string().describe('Complete file content'),
    }),
    async execute(args, ctx) {
        const { path, content } = args;

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

        if (ctx.confirmEdit) {
            const approved = await ctx.confirmEdit(path, '', content);
            if (!approved) {
                return { ok: false, result: { error: 'User rejected the file creation' } };
            }
        }

        await ctx.workspace.write(path, content);
        return { ok: true, result: { path, created: true } };
    },
});
