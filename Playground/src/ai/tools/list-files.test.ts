import { describe, it, expect } from 'vitest';
import { listFiles } from './list-files';
import type { ToolContext } from './index';

function makeCtx(files: string[]): ToolContext {
    return {
        workspace: {
            async list() { return files; },
            async read() { return ''; },
            async write() {},
            currentProject() { return 'test'; },
        },
    };
}

describe('list_files', () => {
    it('filters .fade-cache entries from the listing', async () => {
        const result = await listFiles.execute({}, makeCtx([
            'main.fbasic',
            '.fade-cache/index.json',
            '.fade-cache/blobs/abc.xnb',
            'fade.json',
        ]));
        expect(result.ok).toBe(true);
        if (!result.ok) return;
        // result.result is typed `unknown` because execute() returns a
        // discriminated union with `unknown` on the ok branch; assert
        // the shape this specific tool returns.
        const files = (result.result as { files: string[] }).files;
        expect(files).toEqual(['fade.json', 'main.fbasic']);
    });
});
