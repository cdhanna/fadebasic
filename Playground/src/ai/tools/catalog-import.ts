import { z } from 'zod';
import { defineTool } from './index';

export const catalogImport = defineTool({
    name: 'import_catalog_asset',
    description:
        'Import a Catalog asset (by its numeric id from search_catalog) into the current '
        + 'project, downloading it into the project files so code can reference it. Returns the '
        + 'workspace path(s) written. Only call this after the user has agreed to add the asset.',
    schema: z.object({
        id: z.number().int().describe('The catalog entry id returned by search_catalog'),
    }),
    // Not read-only: it writes files into the project. Runs sequentially.
    async execute(args, ctx) {
        if (!ctx.catalog) {
            return { ok: false, result: { error: 'Catalog is not available in this context.' } };
        }
        try {
            const { name, paths } = await ctx.catalog.import(args.id);
            return { ok: true, result: { name, paths, imported: paths.length } };
        } catch (e) {
            return { ok: false, result: { error: `Import failed: ${(e as Error).message}` } };
        }
    },
});
