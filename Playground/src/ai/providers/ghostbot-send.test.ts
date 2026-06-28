import { describe, expect, it, vi } from 'vitest';
import { broadcastGhostSend, GHOST_BROADCAST_TARGET } from './ghostbot-send';

describe('ghostbot broadcast send', () => {
    it('uses null target so Trystero broadcasts to every peer', () => {
        expect(GHOST_BROADCAST_TARGET).toBeNull();
        const send = vi.fn();
        const bytes = new Uint8Array([1, 2, 3]);
        broadcastGhostSend(send, bytes);
        expect(send).toHaveBeenCalledWith(bytes, null);
    });
});
