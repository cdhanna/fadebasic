import { describe, expect, it } from 'vitest';
import {
    formatModelStatus,
    hasUsableLocalModel,
    pickDefaultDownloadModel,
    type DownloadableModel,
} from './models';

const sample: DownloadableModel[] = [
    {
        id: 'qwen2.5-7b-q4km',
        label: 'Qwen2.5 7B',
        filename: 'Qwen2.5-7B-Instruct-Q4_K_M.gguf',
        size_label: '~4.7 GB',
        description: 'Recommended',
        recommended: true,
        downloaded: false,
        size_mb: 0,
        incomplete: false,
    },
    {
        id: 'qwen2.5-3b-q4km',
        label: 'Qwen2.5 3B',
        filename: 'Qwen2.5-3B-Instruct-Q4_K_M.gguf',
        size_label: '~2.0 GB',
        description: 'Light',
        recommended: false,
        downloaded: true,
        size_mb: 1900,
        incomplete: false,
    },
];

describe('models helpers', () => {
    it('prefers the recommended catalog entry', () => {
        expect(pickDefaultDownloadModel(sample)).toBe('qwen2.5-7b-q4km');
    });

    it('detects usable on-disk models', () => {
        expect(hasUsableLocalModel(sample)).toBe(true);
        expect(hasUsableLocalModel(sample.map(m => ({ ...m, downloaded: false })))).toBe(false);
    });

    it('formats incomplete downloads', () => {
        expect(formatModelStatus({ ...sample[0], downloaded: true, size_mb: 12, incomplete: true }))
            .toContain('Incomplete');
    });
});
