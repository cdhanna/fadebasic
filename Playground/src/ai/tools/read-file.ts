import { z } from 'zod';
import { defineTool } from './index';

export const readFile = defineTool({
    name: 'read_file',
    description: 'Read the full text of a workspace file.',
    schema: z.object({
        path: z.string().describe('Filename to read'),
    }),
    readOnly: true,
    async execute(args, ctx) {
        try {
            const content = await ctx.workspace.read(args.path);
            return { ok: true, result: { path: args.path, content } };
        } catch {
            return { ok: false, result: { error: `File not found: ${args.path}` } };
        }
    },
});
