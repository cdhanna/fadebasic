import type { SlashCommand } from './types';

export const connection: SlashCommand = {
    name: 'connection',
    description: 'Show the active model provider and GhostBot connection details.',
    aliases: ['conn', 'status'],
    execute(_args, ctx) {
        const lines: string[] = [];
        const p = ctx.provider;
        lines.push(`Provider: ${p ? p.label : '(none loaded)'}`);
        if (p) {
            const caps = p.capabilities;
            lines.push(`Backend: ${caps.backend ?? 'unknown'} · context ${caps.maxContext.toLocaleString()} tokens`);
        }
        const info = ctx.callbacks.getConnectionInfo?.();
        if (info) lines.push('', info);
        return { title: 'Connection', body: lines.join('\n') };
    },
};
