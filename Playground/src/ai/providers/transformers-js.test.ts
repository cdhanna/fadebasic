import { describe, it, expect } from 'vitest';
import { isRecoverableGenerateError, isOOMError, friendlyError } from './transformers-js';

describe('isRecoverableGenerateError', () => {
    it('flags WebGPU buffer-mapping failures as recoverable', () => {
        const e = new Error('Failed to download data from buffer: Mapping WebGPU buffer failed: Invalid buffer');
        expect(isRecoverableGenerateError(e)).toBe(true);
    });

    it('flags bare "Invalid buffer" as recoverable', () => {
        expect(isRecoverableGenerateError(new Error('Invalid buffer'))).toBe(true);
    });

    it('flags OrtRun failures as recoverable', () => {
        const e = new Error('failed to call OrtRun(). ERROR_CODE: 1');
        expect(isRecoverableGenerateError(e)).toBe(true);
    });

    it('does NOT flag generic errors as recoverable', () => {
        expect(isRecoverableGenerateError(new Error('out of memory'))).toBe(false);
        expect(isRecoverableGenerateError(new Error('Tokenizer not found'))).toBe(false);
        expect(isRecoverableGenerateError(new Error('AbortError'))).toBe(false);
    });

    it('handles errors without a message gracefully', () => {
        const e = new Error();
        expect(isRecoverableGenerateError(e)).toBe(false);
    });
});

describe('isOOMError', () => {
    it('flags allocation failures as OOM', () => {
        expect(isOOMError(new Error("Can't create a session. failed to allocate a buffer of size 1344237070."))).toBe(true);
    });

    it('flags WebGPU OutOfMemoryError as OOM', () => {
        expect(isOOMError(new Error('WebGPU OutOfMemoryError: ...'))).toBe(true);
    });

    it('does NOT flag the buffer-invalidation crash as OOM (different failure mode)', () => {
        expect(isOOMError(new Error('Invalid buffer'))).toBe(false);
        expect(isOOMError(new Error('Mapping WebGPU buffer failed'))).toBe(false);
    });
});

describe('friendlyError', () => {
    it('rewraps OOM errors with actionable text + preserves the cause', () => {
        const original = new Error("Can't create a session. failed to allocate a buffer of size 1344237070.");
        const wrapped = friendlyError(original);
        expect(wrapped).not.toBe(original);
        expect(wrapped.message).toContain('Out of GPU memory');
        expect(wrapped.message).toContain('smaller model');
        expect((wrapped as Error & { cause?: unknown }).cause).toBe(original);
    });

    it('passes non-OOM errors through unchanged', () => {
        const original = new Error('Some other failure');
        expect(friendlyError(original)).toBe(original);
    });
});
