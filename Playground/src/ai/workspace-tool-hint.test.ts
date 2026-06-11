import { describe, it, expect } from 'vitest';
import { shouldRequireWorkspaceTools } from './workspace-tool-hint';

describe('shouldRequireWorkspaceTools', () => {
    it('matches project inventory questions', () => {
        expect(shouldRequireWorkspaceTools('what is in this project')).toBe(true);
        expect(shouldRequireWorkspaceTools("what's in the workspace")).toBe(true);
        expect(shouldRequireWorkspaceTools('what files are in this project?')).toBe(true);
    });

    it('skips generic file questions (model should tool-call normally)', () => {
        expect(shouldRequireWorkspaceTools('what files are here?')).toBe(false);
        expect(shouldRequireWorkspaceTools('what files?')).toBe(false);
    });

    it('skips conceptual questions', () => {
        expect(shouldRequireWorkspaceTools('how do I write a for loop?')).toBe(false);
    });
});
