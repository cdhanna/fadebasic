import { describe, expect, it } from 'vitest';
import { formatProviderLoadError, providerErrorSummary } from './provider-load-errors';

describe('formatProviderLoadError', () => {
    it('maps internal ReferenceError to a reload hint', () => {
        const msg = formatProviderLoadError(
            new ReferenceError('bindGhostBotConnection is not defined'),
            'ghostbot:local',
        );
        expect(msg).toContain('Reload the Playground');
        expect(msg).not.toContain('bindGhostBotConnection');
    });

    it('keeps actionable GhostBot timeout text', () => {
        const raw = 'GhostBot did not connect within 120s. Open GhostBot, enter join code ABCD12.';
        expect(formatProviderLoadError(new Error(raw), 'ghostbot:local')).toBe(raw);
    });

    it('wraps generic GhostBot failures', () => {
        expect(formatProviderLoadError(new Error('room failed'), 'ghostbot:local'))
            .toContain('signaling session');
    });

    it('prefixes unknown ghostbot errors', () => {
        expect(formatProviderLoadError(new Error('boom'), 'ghostbot:local'))
            .toBe('GhostBot setup failed: boom');
    });
});

describe('providerErrorSummary', () => {
    it('shortens long ghostbot messages for the status chip', () => {
        const detail = formatProviderLoadError(
            new ReferenceError('bindGhostBotConnection is not defined'),
            'ghostbot:local',
        );
        expect(providerErrorSummary(detail, 'ghostbot:local')).toBe('GhostBot UI error');
    });
});
