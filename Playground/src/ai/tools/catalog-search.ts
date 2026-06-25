import { z } from 'zod';
import { defineTool } from './index';

/** Map the words models actually use to the catalog's media categories.
 *  Unknown values return undefined (no filter) rather than erroring. */
function normalizeCategory(raw?: string): 'image' | 'audio' | 'font' | undefined {
    if (!raw) return undefined;
    const v = raw.toLowerCase();
    if (/\b(image|images|sprite|sprites|picture|pictures|art|texture|textures|tile|tiles|icon|icons|png|jpg)\b/.test(v)) return 'image';
    if (/\b(audio|sound|sounds|sfx|music|song|songs|wav|mp3|ogg|clip|clips)\b/.test(v)) return 'audio';
    if (/\b(font|fonts|typeface|text|ttf|otf)\b/.test(v)) return 'font';
    return undefined;
}

function normalizeKind(raw?: string): 'asset' | 'pack' | undefined {
    if (!raw) return undefined;
    const v = raw.toLowerCase();
    if (/\b(pack|packs|bundle|bundles|set|sets|collection)\b/.test(v)) return 'pack';
    if (/\b(asset|assets|single|file|sprite|sound|font|image|audio)\b/.test(v)) return 'asset';
    return undefined;
}

export const catalogSearch = defineTool({
    name: 'search_catalog',
    description:
        'Search the FadeLand asset Catalog for free sprites, sounds, fonts and asset packs '
        + 'the user can pull into their project. Use this when the user wants art/audio/fonts, '
        + 'or when a code snippet needs an asset that is not in their project yet. Returns '
        + 'matching entries with their id, name, kind, mime type, tags and license. To actually '
        + 'add one, call import_catalog_asset with the id.',
    schema: z.object({
        query: z.string().describe('Search terms, e.g. "spaceship sprite" or "explosion sound"'),
        // Accept free text and normalize — models commonly guess "sprite",
        // "sound", "music" etc. rather than the exact enum.
        category: z.string().optional()
            .describe('Media category: image, audio, or font (synonyms like "sprite"/"sound" are understood)'),
        kind: z.string().optional()
            .describe('asset (single file) or pack (bundle)'),
        limit: z.coerce.number().int().min(1).max(25).optional().describe('Max results (default 12)'),
    }),
    readOnly: true,
    async execute(args, ctx) {
        if (!ctx.catalog) {
            return { ok: false, result: { error: 'Catalog is not available in this context.' } };
        }
        const category = normalizeCategory(args.category);
        const kind = normalizeKind(args.kind);
        const limit = args.limit ?? 12;
        try {
            const entries = await ctx.catalog.search(args.query, { category, kind, limit });
            if (entries.length > 0) {
                return { ok: true, result: { matches: entries } };
            }
            // Nothing matched — browse what's available (same filters, no
            // query) so the model can still pick the closest fit instead of
            // giving up. This is the "no results → list everything → choose"
            // behaviour: a loose query should never dead-end.
            const browse = await ctx.catalog.search('', { category, kind, limit: Math.max(limit, 12) });
            return {
                ok: true,
                result: {
                    matches: browse,
                    note: browse.length
                        ? `No direct match for "${args.query}". These are available${category ? ` ${category}` : ''} catalog entries — pick the closest fit by id and import it, or refine the search.`
                        : 'The catalog appears to be empty or unavailable.',
                },
            };
        } catch (e) {
            return { ok: false, result: { error: (e as Error).message } };
        }
    },
});
