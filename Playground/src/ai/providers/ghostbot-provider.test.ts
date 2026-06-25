import { describe, it, expect, beforeEach, vi } from 'vitest';
import { GhostBotProvider } from './ghostbot-provider';

const store = new Map<string, string>();

beforeEach(() => {
    store.clear();
    vi.stubGlobal('localStorage', {
        getItem: (k: string) => store.get(k) ?? null,
        setItem: (k: string, v: string) => { store.set(k, v); },
        removeItem: (k: string) => { store.delete(k); },
    });
});

describe('GhostBotProvider connection state', () => {
    it('starts idle with a join code', () => {
        const p = new GhostBotProvider('TEST01');
        expect(p.getConnectionState().status).toBe('idle');
        expect(p.getJoinCode()).toBe('TEST01');
    });

    it('notifies connection listeners', () => {
        const p = new GhostBotProvider('ABCD12');
        const states: string[] = [];
        p.onConnectionState(s => states.push(s.status));
        expect(states).toContain('idle');
    });

    it('setJoinCode updates and persists the GhostBot code', () => {
        const p = new GhostBotProvider('OLD123');
        p.setJoinCode('new99');
        expect(p.getJoinCode()).toBe('NEW99'); // normalized to uppercase
        const p2 = new GhostBotProvider();
        expect(p2.getJoinCode()).toBe('NEW99'); // read back from storage
    });

    it('reports whether a code is set', () => {
        expect(new GhostBotProvider('CODE12').hasJoinCode()).toBe(true);
        expect(new GhostBotProvider('').hasJoinCode()).toBe(false);
    });
});
