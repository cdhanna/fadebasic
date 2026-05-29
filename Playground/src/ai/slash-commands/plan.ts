import type { SlashCommand } from './types';

export const plan: SlashCommand = {
    name: 'plan',
    description: 'Re-display the most recent plan the agent emitted.',
    execute(_args, ctx) {
        const p = ctx.state.lastPlan;
        if (!p) {
            return { title: 'Plan', body: 'No plan emitted yet this session.' };
        }
        const lines: string[] = [`Goal: ${p.goal}`, ''];
        for (let i = 0; i < p.steps.length; i++) {
            const step = p.steps[i];
            const tool = step.tool ? `[${step.tool}] ` : '';
            lines.push(`  ${i + 1}. ${tool}${step.description}`);
        }
        return { title: 'Last plan', body: lines.join('\n') };
    },
};
