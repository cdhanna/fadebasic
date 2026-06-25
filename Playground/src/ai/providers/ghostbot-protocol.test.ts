import { describe, it, expect } from 'vitest';
import { decodeGhostMessage, encodeGhostMessage, generateJoinCode } from './ghostbot-protocol';

describe('ghostbot-protocol', () => {
    it('round-trips stream messages', () => {
        const msg = {
            v: 1 as const,
            type: 'stream' as const,
            id: 1,
            messages: [{ role: 'user' as const, content: 'hi' }],
        };
        const decoded = decodeGhostMessage(encodeGhostMessage(msg));
        expect(decoded).toEqual(msg);
    });

    it('generates 6-char join codes', () => {
        const code = generateJoinCode();
        expect(code).toMatch(/^[A-Z2-9]{6}$/);
    });
});
