import type { SlashCommand } from './types';

export const context: SlashCommand = {
    name: 'context',
    description: 'Show what the model is currently seeing: token usage, last retrieved docs, last plan.',
    execute(_args, ctx) {
        const history = ctx.agent?.getHistory() ?? [];
        const provider = ctx.provider;
        const lines: string[] = [];

        // Token budget
        if (provider) {
            const max = provider.capabilities.maxContext;
            let total = 0;
            for (const m of history) total += provider.countTokens(m.content);
            const pct = max > 0 ? Math.round((total / max) * 100) : 0;
            lines.push(`Conversation: ${history.length} message(s), ~${total.toLocaleString()} tokens (${pct}% of ${max.toLocaleString()})`);
        } else {
            lines.push(`Conversation: ${history.length} message(s) (no provider loaded — token count unavailable)`);
        }

        lines.push('');

        // Last retrieved docs
        const docs = ctx.state.lastDocs;
        if (docs && docs.length > 0) {
            lines.push(`Last RAG retrieval (${docs.length} chunk${docs.length === 1 ? '' : 's'}):`);
            for (const h of docs) {
                const cite = h.chunk.heading
                    ? `${h.chunk.source} → ${h.chunk.heading}`
                    : h.chunk.source;
                lines.push(`  • ${cite} (score ${h.score.toFixed(2)})`);
            }
        } else {
            lines.push('Last RAG retrieval: none this session');
        }

        lines.push('');

        // Last plan
        const plan = ctx.state.lastPlan;
        if (plan) {
            lines.push(`Last plan: ${plan.goal}`);
            for (let i = 0; i < plan.steps.length; i++) {
                const step = plan.steps[i];
                const tool = step.tool ? `[${step.tool}] ` : '';
                lines.push(`  ${i + 1}. ${tool}${step.description}`);
            }
        } else {
            lines.push('Last plan: none this session');
        }

        // Recent eviction status
        const lastEviction = [...ctx.state.recentEvents].reverse().find(e => e.kind === 'eviction');
        if (lastEviction && lastEviction.kind === 'eviction') {
            lines.push('');
            const r = lastEviction.result;
            const parts: string[] = [];
            if (r.elided) parts.push(`${r.elided} elided`);
            if (r.summarized) parts.push(`${r.summarized} summarized`);
            if (r.dropped) parts.push(`${r.dropped} dropped`);
            lines.push(`Recent eviction: ${parts.join(', ') || 'no change'} (${lastEviction.tokensBefore} → ${lastEviction.tokensAfter} tokens)`);
        }

        return { title: 'Context', body: lines.join('\n') };
    },
};
