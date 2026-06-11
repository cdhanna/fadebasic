import { describe, it, expect } from 'vitest';
import { needsCommandDocs, shouldAutoRetrieveDocs, shouldPrefetchDocs } from './auto-retrieve';

describe('shouldAutoRetrieveDocs', () => {
    it('skips workspace file listing questions', () => {
        expect(shouldAutoRetrieveDocs('what files are in this project')).toBe(false);
        expect(shouldAutoRetrieveDocs('list every file in my workspace')).toBe(false);
    });

    it('skips edit/read requests', () => {
        expect(shouldAutoRetrieveDocs('read main.fbasic and fix the print')).toBe(false);
        expect(shouldAutoRetrieveDocs('change the code in my project')).toBe(false);
    });

    it('allows conceptual Fade questions', () => {
        expect(shouldAutoRetrieveDocs('how do I write a for loop in Fade?')).toBe(true);
        expect(shouldAutoRetrieveDocs('what does the print command do in fbasic')).toBe(true);
        expect(shouldAutoRetrieveDocs('how do sprites rotate?')).toBe(true);
    });

    it('skips very short prompts', () => {
        expect(shouldAutoRetrieveDocs('hi')).toBe(false);
    });

    it('allows command docs even in project context', () => {
        expect(shouldAutoRetrieveDocs('how do I use the draw sprite command in my project')).toBe(true);
        expect(needsCommandDocs('what command draws a rectangle')).toBe(true);
        expect(shouldPrefetchDocs('how to use input in fbasic')).toBe(true);
    });
});
