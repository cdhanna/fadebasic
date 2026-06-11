import { describe, it, expect } from 'vitest';
import {
    looksLikePastedCodeEdit,
    userRequestedCodeChange,
} from './edit-via-tools';

describe('userRequestedCodeChange', () => {
    it('detects edit requests', () => {
        expect(userRequestedCodeChange('fix the error in main.fbasic')).toBe(true);
        expect(userRequestedCodeChange('change the return in my code')).toBe(true);
    });

    it('ignores pure explanations', () => {
        expect(userRequestedCodeChange('what is a for loop')).toBe(false);
    });
});

describe('looksLikePastedCodeEdit', () => {
    it('detects multi-line fenced code', () => {
        const text = 'Here is the fix:\n```fbasic\nline1\nline2\nline3\n```';
        expect(looksLikePastedCodeEdit(text)).toBe(true);
    });

    it('ignores short snippets', () => {
        expect(looksLikePastedCodeEdit('use `print "hi"`')).toBe(false);
    });
});
