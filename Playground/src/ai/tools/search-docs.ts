import { z } from 'zod';
import { defineTool } from './index';
import { getRetriever } from '../rag/retrieval';

export const searchDocs = defineTool({
    name: 'search_docs',
    description:
        'Search the Fade documentation for relevant info. Returns the top few matching ' +
        'sections with their source paths. Use this when the user asks about Fade language ' +
        'features, commands, or anything else the docs would cover.',
    schema: z.object({
        query: z.string().describe('Natural-language query, e.g. "how do sprites rotate"'),
        k: z.number().int().min(1).max(10).optional().describe('How many results to return (default 4)'),
    }),
    readOnly: true,
    async execute(args, context) {
        const k = args.k ?? 4;
        const retriever = getRetriever();
        const hits = await retriever.search(args.query, k, {
            projectType: context.projectType?.(),
        });
        if (hits.length === 0) {
            return {
                ok: false,
                result: {
                    error: 'No docs index loaded, or no matches found. Answer from prior knowledge.',
                },
            };
        }
        return {
            ok: true,
            result: {
                hits: hits.map(h => ({
                    source: h.chunk.source,
                    heading: h.chunk.heading,
                    score: Number(h.score.toFixed(3)),
                    text: h.chunk.text,
                })),
            },
        };
    },
});
