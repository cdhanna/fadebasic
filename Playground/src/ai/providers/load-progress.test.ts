import { describe, it, expect } from 'vitest';
import { ModelLoadProgressTracker } from './load-progress';

describe('ModelLoadProgressTracker', () => {
    it('never decreases when a new file starts at 0%', () => {
        const t = new ModelLoadProgressTracker();
        const a = t.update({ status: 'download', file: 'a.onnx', progress: 100 });
        expect(a.pctInt).toBeGreaterThan(0);

        const b = t.update({ status: 'done', file: 'a.onnx' });
        const afterDone = b.pctInt;

        const c = t.update({ status: 'download', file: 'b.onnx', progress: 0 });
        expect(c.pctInt).toBeGreaterThanOrEqual(afterDone);
    });

    it('caps file-phase progress below 75% even when a single file finishes', () => {
        const t = new ModelLoadProgressTracker();
        const r = t.update({ status: 'download', file: 'model.onnx', progress: 100 });
        expect(r.pctInt).toBeLessThanOrEqual(72);
    });

    it('includes an integer percentage in the label', () => {
        const t = new ModelLoadProgressTracker();
        const r = t.update({ status: 'download', file: 'model.onnx', progress: 50 });
        expect(r.text).toMatch(/^\d+% — /);
        expect(r.pctInt).toBeGreaterThan(0);
    });

    it('reaches 100% on complete()', () => {
        const t = new ModelLoadProgressTracker();
        t.update({ status: 'download', file: 'a.onnx', progress: 40 });
        const done = t.complete();
        expect(done.pctInt).toBe(100);
        expect(done.pct).toBe(1);
    });

    it('advances through a multi-file download', () => {
        const t = new ModelLoadProgressTracker();
        const mid = t.update({ status: 'download', file: 'a.onnx', progress: 80 });
        const done = t.update({ status: 'done', file: 'a.onnx' });
        const next = t.update({ status: 'download', file: 'b.onnx', progress: 10 });
        expect(mid.pctInt).toBeGreaterThan(0);
        expect(done.pctInt).toBeGreaterThanOrEqual(mid.pctInt);
        expect(next.pctInt).toBeGreaterThanOrEqual(done.pctInt);
    });

    it('enters warmup at file-phase ceiling and creeps upward', () => {
        const t = new ModelLoadProgressTracker();
        t.update({ status: 'done', file: 'a.onnx' });
        const warm = t.enterWarmup('initializing WebGPU');
        expect(warm.pctInt).toBeGreaterThanOrEqual(72);
        expect(warm.text).toContain('initializing WebGPU');
        const later = t.tickWarmup();
        expect(later.pctInt).toBeGreaterThan(warm.pctInt);
        expect(later.pctInt).toBeLessThan(100);
    });
});
