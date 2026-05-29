import type { SlashCommand } from './types';

export const clear: SlashCommand = {
    name: 'clear',
    description: 'Clear the current conversation. Equivalent to the Clear button.',
    execute(_args, ctx) {
        if (!ctx.callbacks.clearConversation) {
            return {
                title: 'Clear',
                body: 'Clear callback not wired by the host.',
                variant: 'error',
            };
        }
        ctx.callbacks.clearConversation();
        // Output is the cleared chat itself — returning null suppresses the
        // bubble that the slash command would otherwise produce.
        return null;
    },
};
