import { describe, it, expect, afterEach, vi } from 'vitest';
import { probeWebGPU, formatBytes } from './webgpu-info';

describe('formatBytes', () => {
    it('formats GB / MB / KB / B', () => {
        expect(formatBytes(2 * 1024 ** 3)).toBe('2.00 GB');
        expect(formatBytes(512 * 1024 ** 2)).toBe('512.0 MB');
        expect(formatBytes(64 * 1024)).toBe('64 KB');
        expect(formatBytes(900)).toBe('900 B');
    });
});

describe('probeWebGPU', () => {
    afterEach(() => { vi.unstubAllGlobals(); });

    it('reports unavailable when navigator.gpu is missing (node / older browsers)', async () => {
        vi.stubGlobal('navigator', {});  // no .gpu
        const snap = await probeWebGPU();
        expect(snap.available).toBe(false);
        expect(snap.note).toContain('navigator.gpu');
    });

    it('reports unavailable when requestAdapter returns null', async () => {
        vi.stubGlobal('navigator', {
            gpu: { requestAdapter: async () => null },
        });
        const snap = await probeWebGPU();
        expect(snap.available).toBe(false);
        expect(snap.note).toContain('null');
    });

    it('extracts info + limits when the adapter resolves', async () => {
        vi.stubGlobal('navigator', {
            gpu: {
                async requestAdapter() {
                    return {
                        info: {
                            vendor: 'apple',
                            architecture: 'metal-3',
                            device: 'Apple M2 Pro',
                            description: 'integrated',
                        },
                        limits: {
                            maxBufferSize: 2 * 1024 ** 3,
                            maxStorageBufferBindingSize: 1024 * 1024 ** 2,
                            maxComputeInvocationsPerWorkgroup: 1024,
                        },
                    };
                },
            },
        });
        const snap = await probeWebGPU();
        expect(snap.available).toBe(true);
        expect(snap.vendor).toBe('apple');
        expect(snap.architecture).toBe('metal-3');
        expect(snap.device).toBe('Apple M2 Pro');
        expect(snap.maxBufferSize).toBe(2 * 1024 ** 3);
        expect(snap.maxComputeInvocationsPerWorkgroup).toBe(1024);
    });

    it('catches thrown errors from requestAdapter', async () => {
        vi.stubGlobal('navigator', {
            gpu: {
                async requestAdapter() {
                    throw new Error('GPU process crashed');
                },
            },
        });
        const snap = await probeWebGPU();
        expect(snap.available).toBe(false);
        expect(snap.note).toBe('GPU process crashed');
    });
});
