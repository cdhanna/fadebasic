import { z } from 'zod';
import { defineTool } from './index';

export const listFiles = defineTool({
    name: 'list_files',
    description: 'List all files in the current workspace project.',
    schema: z.object({}),
    readOnly: true,
    async execute(_args, ctx) {
        const files = await ctx.workspace.list();
        return {
            ok: true,
            result: { files, project: ctx.workspace.currentProject() },
        };
    },
});
