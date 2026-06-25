import { z } from 'zod';
import { defineTool } from './index';

export const catalogBrowse = defineTool({
    name: 'browse_catalog',
    description:
        'List available Catalog entries (optionally filtered by category) so you can pick the '
        + 'best fit yourself. Use this when search_catalog returns nothing for a query — browse the '
        + 'real entries, choose the closest by id, then import_catalog_asset. Returns id, name, '
        + 'kind, mime, tags.',
    schema: z.object({
        category: z.string().optional().describe('Optional: image, audio, or font (synonyms understood)'),
        limit: z.coerce.number().int().min(1).max(60).optional().describe('Max entries to list (default 40)'),
    }),
    readOnly: true,
    async execute(args, ctx) {
        if (!ctx.catalog) {
            return { ok: false, result: { error: 'Catalog is not available in this context.' } };
        }
        const category = normalizeCategory(args.category);
        try {
            // Empty query = browse; the client returns filter-only matches.
            const entries = await ctx.catalog.search('', { category, limit: args.limit ?? 40 });
            return {
                ok: true,
                result: {
                    entries,
                    note: entries.length
                        ? 'Pick the closest fit by id, then call import_catalog_asset.'
                        : 'The catalog is empty or unavailable.',
                },
            };
        } catch (e) {
            return { ok: false, result: { error: (e as Error).message } };
        }
    },
});

function normalizeCategory(raw?: string): 'image' | 'audio' | 'font' | undefined {
    if (!raw) return undefined;
    const v = raw.toLowerCase();
    if (/\b(image|images|sprite|sprites|picture|art|texture|tile|icon|png|jpg)\b/.test(v)) return 'image';
    if (/\b(audio|sound|sounds|sfx|music|song|wav|mp3|ogg|clip)\b/.test(v)) return 'audio';
    if (/\b(font|fonts|typeface|text|ttf|otf)\b/.test(v)) return 'font';
    return undefined;
}
