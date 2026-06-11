import { describe, it, expect } from 'vitest';
import { buildToolRouteHint, looksLikeAccessDenial } from './tool-awareness';

describe('buildToolRouteHint', () => {
    it('suggests read_file for code tasks', () => {
        expect(buildToolRouteHint('fix the error in main.fbasic')).toContain('read_file');
    });

    it('suggests list_files for project inventory', () => {
        expect(buildToolRouteHint('what is in this project')).toContain('list_files');
    });
});

describe('looksLikeAccessDenial', () => {
    it('detects false access claims', () => {
        expect(looksLikeAccessDenial("I don't have access to your source code.")).toBe(true);
    });

    it('ignores normal answers', () => {
        expect(looksLikeAccessDenial('Here is the function on line 5.')).toBe(false);
    });
});
