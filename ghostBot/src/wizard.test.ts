import { describe, expect, it } from 'vitest';
import {
    formatEta,
    isWizardStepUnlocked,
    normalizeJoinCode,
    sessionPillClass,
} from './wizard';

describe('wizard helpers', () => {
    it('formats ETA strings', () => {
        expect(formatEta(0)).toBe('—');
        expect(formatEta(45)).toBe('~45s left');
        expect(formatEta(125)).toBe('~2m 5s left');
    });

    it('gates wizard steps on download/load state', () => {
        const fresh = { modelDownloaded: false, modelLoaded: false };
        expect(isWizardStepUnlocked(1, fresh)).toBe(true);
        expect(isWizardStepUnlocked(2, fresh)).toBe(false);
        expect(isWizardStepUnlocked(3, fresh)).toBe(false);

        const downloaded = { modelDownloaded: true, modelLoaded: false };
        expect(isWizardStepUnlocked(2, downloaded)).toBe(true);
        expect(isWizardStepUnlocked(3, downloaded)).toBe(false);

        const loaded = { modelDownloaded: true, modelLoaded: true };
        expect(isWizardStepUnlocked(3, loaded)).toBe(true);
    });

    it('normalizes join codes', () => {
        expect(normalizeJoinCode('  ab12cd  ')).toBe('AB12CD');
    });

    it('maps session status to pill classes', () => {
        expect(sessionPillClass('connected')).toBe('connected');
        expect(sessionPillClass('inferring')).toBe('inferring');
        expect(sessionPillClass('unknown')).toBe('idle');
    });
});
