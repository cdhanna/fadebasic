import type { SlashCommand } from './types';

export const mode: SlashCommand = {
    name: 'mode',
    description: 'Show or set how edits are applied: /mode auto | manual.',
    aliases: ['edits'],
    execute(args, ctx) {
        const get = ctx.callbacks.getEditMode;
        const set = ctx.callbacks.setEditMode;
        if (!get || !set) {
            return { title: 'Edit mode', body: 'Edit mode is not available here.' };
        }
        const arg = args.trim().toLowerCase();
        if (arg === 'auto' || arg === 'manual') {
            set(arg);
            return {
                title: 'Edit mode',
                body: arg === 'auto'
                    ? 'Auto-accept ON — code edits apply immediately without a diff prompt.'
                    : 'Manual review ON — you approve each edit diff before it is written.',
            };
        }
        if (arg) {
            return { title: 'Edit mode', variant: 'error', body: `Unknown mode "${arg}". Use /mode auto or /mode manual.` };
        }
        const current = get();
        return {
            title: 'Edit mode',
            body: `Currently: ${current === 'auto' ? 'Auto-accept' : 'Manual review'}.\n`
                + 'Switch with /mode auto or /mode manual (or the Edits button in the toolbar).',
        };
    },
};
