import type { SlashCommand } from './types';

export const logs: SlashCommand = {
    name: 'logs',
    description: 'Focus the Logs panel and filter to AI channels.',
    execute(_args, ctx) {
        if (!ctx.callbacks.focusLogs) {
            return {
                title: 'Logs',
                body: 'Logs panel callback not wired by the host. Try opening the Logs tab manually and filtering on channels starting with "ai/".',
            };
        }
        ctx.callbacks.focusLogs(/^ai\//);
        return {
            title: 'Logs',
            body: 'Logs panel focused, filtered to ai/* channels.',
        };
    },
};
