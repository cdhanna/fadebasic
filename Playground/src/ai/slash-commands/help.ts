import type { SlashCommand } from './types';

export const help: SlashCommand = {
    name: 'help',
    description: 'List available slash commands.',
    aliases: ['?', 'h'],
    execute(_args, ctx) {
        const cmds = ctx.list();
        const widest = cmds.reduce((w, c) => Math.max(w, c.name.length), 0);
        const lines = cmds.map(c => `/${c.name.padEnd(widest)}   ${c.description}`);
        return {
            title: 'Slash commands',
            body: lines.join('\n'),
        };
    },
};
