import { describe, expect, it } from 'vitest';
import { decodeGhostMessage } from './ghostbot-protocol';
import { GhostPairingBus, handleGhostInbound } from './ghostbot-pairing';
import type { GhostOutbound } from './ghostbot-protocol';

describe('ghostbot pairing bus', () => {
    it('playground ping elicits pong and session connected from ghost', () => {
        const bus = new GhostPairingBus('TEST01');
        const pgOut: GhostOutbound[] = [];

        bus.join('ghost', (bytes) => {
            handleGhostInbound(bytes, (msg) => bus.ghostSend(msg));
        });
        bus.join('playground', (bytes) => {
            const msg = decodeGhostMessage(bytes);
            if (msg && 'type' in msg) pgOut.push(msg as GhostOutbound);
        });

        bus.playgroundSend({ v: 1, type: 'ping' });

        expect(pgOut.some(m => m.type === 'pong')).toBe(true);
        expect(pgOut.some(m => m.type === 'session' && m.status === 'connected')).toBe(true);
    });

    it('broadcast reaches ghost without a resolved peer id', () => {
        const bus = new GhostPairingBus('ABCD12');
        let ghostSeen = 0;
        bus.join('ghost', () => { ghostSeen++; });
        bus.playgroundSend({ v: 1, type: 'ping' });
        expect(ghostSeen).toBe(1);
    });
});
