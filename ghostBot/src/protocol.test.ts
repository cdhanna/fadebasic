import { describe, expect, it } from 'vitest';
import {
    decodeMessage,
    encodeMessage,
    formatChatPrompt,
    GHOSTBOT_ACTION,
    GHOSTBOT_APP_ID,
    type GhostInbound,
} from './protocol';

describe('ghostbot protocol', () => {
    it('uses stable Trystero room identifiers', () => {
        expect(GHOSTBOT_APP_ID).toBe('fade-ghostbot');
        expect(GHOSTBOT_ACTION).toBe('ghost');
    });

    it('round-trips stream messages', () => {
        const msg: GhostInbound = {
            v: 1,
            type: 'stream',
            id: 7,
            messages: [{ role: 'user', content: 'hi' }],
            maxTokens: 512,
        };
        const decoded = decodeMessage(encodeMessage(msg));
        expect(decoded).toEqual(msg);
    });

    it('rejects malformed wire payloads', () => {
        expect(decodeMessage(new Uint8Array())).toBeNull();
        expect(decodeMessage(new TextEncoder().encode('{"v":2}'))).toBeNull();
        expect(decodeMessage(new TextEncoder().encode('not json'))).toBeNull();
    });

    it('formats Qwen instruct chat prompts', () => {
        const prompt = formatChatPrompt([
            { role: 'system', content: 'You are helpful.' },
            { role: 'user', content: 'Hello' },
        ]);
        expect(prompt).toContain('<|im_start|>system');
        expect(prompt).toContain('<|im_start|>user');
        expect(prompt.endsWith('<|im_start|>assistant\n')).toBe(true);
    });
});
