/** Wire protocol between Fade Playground and GhostBot.
 *  Keep in sync with ghostBot/src/protocol.ts */

export const GHOSTBOT_APP_ID = 'fade-ghostbot';
export const GHOSTBOT_ACTION = 'ghost';

export type GhostSessionStatus = 'idle' | 'waiting' | 'connected' | 'inferring' | 'error';

export type FinishReason = 'stop' | 'tool_calls' | 'length' | 'error' | 'aborted';

export interface GhostMsg {
    role: 'system' | 'user' | 'assistant' | 'tool';
    content: string;
}

export type GhostStreamEvent =
    | { kind: 'text'; delta: string }
    | { kind: 'done'; finishReason: FinishReason };

// Keep in sync with ghostBot/src/protocol.ts.
export type GhostInbound =
    | { v: 1; type: 'hello'; clientId: string; label: string }
    | { v: 1; type: 'stream'; id: number; messages: GhostMsg[]; maxTokens?: number; temperature?: number }
    | { v: 1; type: 'abort'; streamId: number }
    | { v: 1; type: 'ping' };

export type GhostOutbound = (
    | { v: 1; type: 'auth'; status: 'approved' | 'pending' | 'denied'; detail?: string }
    | { v: 1; type: 'session'; joinCode: string; status: GhostSessionStatus; detail?: string; peerId?: string }
    | { v: 1; type: 'stream-event'; streamId: number; event: GhostStreamEvent }
    | { v: 1; type: 'stream-end'; streamId: number }
    | { v: 1; type: 'stream-error'; streamId: number; message: string }
    | { v: 1; type: 'model-status'; loaded: boolean; name?: string; path?: string }
    | { v: 1; type: 'pong' }
) & { to?: string };

export function encodeGhostMessage(msg: GhostInbound | GhostOutbound): Uint8Array {
    return new TextEncoder().encode(JSON.stringify(msg));
}

export function decodeGhostMessage(bytes: Uint8Array): GhostInbound | GhostOutbound | null {
    try {
        const parsed = JSON.parse(new TextDecoder().decode(bytes));
        if (!parsed || parsed.v !== 1 || typeof parsed.type !== 'string') return null;
        return parsed;
    } catch {
        return null;
    }
}

export function generateJoinCode(): string {
    const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
    let code = '';
    for (let i = 0; i < 6; i++) {
        code += alphabet[Math.floor(Math.random() * alphabet.length)];
    }
    return code;
}
