import { describe, expect, it, vi } from 'vitest';
import { broadcastGhostSend } from './send';

describe('ghostbot broadcast send', () => {
    it('broadcasts with a null Trystero target', () => {
        const send = vi.fn();
        broadcastGhostSend(send, new Uint8Array([9]));
        expect(send).toHaveBeenCalledWith(new Uint8Array([9]), null);
    });
});
