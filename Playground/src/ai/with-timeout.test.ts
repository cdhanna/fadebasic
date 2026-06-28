import { describe, it, expect } from 'vitest';
import { withTimeout } from './with-timeout';

describe('withTimeout', () => {
    it('resolves when promise finishes in time', async () => {
        await expect(withTimeout(Promise.resolve(42), 500, 'test')).resolves.toBe(42);
    });

    it('rejects when promise is slow', async () => {
        await expect(
            withTimeout(new Promise(() => { /* hang */ }), 50, 'slow op'),
        ).rejects.toThrow('slow op timed out');
    });
});
