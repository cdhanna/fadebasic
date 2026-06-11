import { describe, it, expect } from 'vitest';
import { shouldSuggestPlan } from './plan-suggest';

describe('shouldSuggestPlan', () => {
    it('suggests a plan for long multi-action requests', () => {
        const q = 'Refactor main.fbasic to extract the update loop into its own function, '
            + 'then add a second ship sprite, and also fix the collision code across multiple files.';
        expect(shouldSuggestPlan(q)).toBe(true);
    });

    it('skips short single-action questions', () => {
        expect(shouldSuggestPlan('what files are here?')).toBe(false);
    });
});
