/** Wire protocol between Fade Playground and GhostBot over WebRTC data channel.
 *  Keep in sync with Playground/src/ai/providers/ghostbot-protocol.ts */

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

/** Playground → GhostBot. `hello` carries a stable per-browser identity so
 *  GhostBot can apply trust-on-first-use (approve a client once, then
 *  auto-accept its future reconnections). */
export type GhostInbound =
    | { v: 1; type: 'hello'; clientId: string; label: string }
    | { v: 1; type: 'stream'; id: number; messages: GhostMsg[]; maxTokens?: number; temperature?: number }
    | { v: 1; type: 'abort'; streamId: number }
    | { v: 1; type: 'ping' };

/** GhostBot → Playground. Every outbound message may carry `to` — the target
 *  peer's id. When GhostBot serves multiple Playgrounds from one room it must
 *  address replies to the requesting peer; messages still go out over a
 *  broadcast channel (unicast was unreliable — see CLAUDE.md gotcha 5), so
 *  each Playground filters on `to`. Absent `to` = intended for everyone. */
export type GhostOutbound = (
    | { v: 1; type: 'auth'; status: 'approved' | 'pending' | 'denied'; detail?: string }
    | { v: 1; type: 'session'; joinCode: string; status: GhostSessionStatus; detail?: string; peerId?: string }
    | { v: 1; type: 'stream-event'; streamId: number; event: GhostStreamEvent }
    | { v: 1; type: 'stream-end'; streamId: number }
    | { v: 1; type: 'stream-error'; streamId: number; message: string }
    | { v: 1; type: 'model-status'; loaded: boolean; name?: string; path?: string }
    | { v: 1; type: 'pong' }
) & { to?: string };

export function encodeMessage(msg: GhostInbound | GhostOutbound): Uint8Array {
    return new TextEncoder().encode(JSON.stringify(msg));
}

export function decodeMessage(bytes: Uint8Array): GhostInbound | GhostOutbound | null {
    try {
        const parsed = JSON.parse(new TextDecoder().decode(bytes));
        if (!parsed || parsed.v !== 1 || typeof parsed.type !== 'string') return null;
        return parsed;
    } catch {
        return null;
    }
}

/** Format chat messages for Qwen2.5 / Qwen3 instruct models. */
export function formatChatPrompt(messages: GhostMsg[]): string {
    const parts: string[] = [];
    for (const m of messages) {
        parts.push(`<|im_start|>${m.role}\n${m.content}`);
    }
    parts.push('<|im_start|>assistant\n');
    return parts.join('\n');
}
