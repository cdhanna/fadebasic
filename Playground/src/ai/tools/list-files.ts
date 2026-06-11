import { z } from 'zod';
import { defineTool } from './index';

/** Workspace paths visible to the agent (hides build cache). */
export function filterWorkspacePaths(files: string[]): string[] {
    return files
        .filter(p => p !== '.fade-cache' && !p.startsWith('.fade-cache/'))
        .sort();
}

export const listFiles = defineTool({
    name: 'list_files',
    description: 'List all files in the current workspace project.',
    schema: z.object({}),
    readOnly: true,
    async execute(_args, ctx) {
        const files = filterWorkspacePaths(await ctx.workspace.list());
        return {
            ok: true,
            result: { files, project: ctx.workspace.currentProject() },
        };
    },
});
