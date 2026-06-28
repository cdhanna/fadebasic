import type { SlashCommand } from './types';

export const tools: SlashCommand = {
    name: 'tools',
    description: 'List the tools the agent can call, with their argument schemas.',
    execute(_args, ctx) {
        const descs = ctx.tools.describe();
        if (descs.length === 0) {
            return { title: 'Tools', body: '(no tools registered)' };
        }
        const lines: string[] = [];
        for (const t of descs) {
            const props = (t.schema as { properties?: Record<string, { type?: string; description?: string }> }).properties;
            const argSummary = props
                ? Object.entries(props)
                    .map(([k, v]) => `${k}: ${v.type ?? 'any'}`)
                    .join(', ')
                : '';
            lines.push(`• ${t.name}(${argSummary})`);
            lines.push(`    ${t.description}`);
        }
        return {
            title: `Tools (${descs.length})`,
            body: lines.join('\n'),
        };
    },
};
