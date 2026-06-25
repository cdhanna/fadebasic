/**
 * In-memory pairing bus for tests — mirrors Trystero broadcast semantics.
 */
import {
    decodeGhostMessage,
    encodeGhostMessage,
    type GhostInbound,
    type GhostOutbound,
} from './ghostbot-protocol';

type PeerRole = 'playground' | 'ghost';

interface Peer {
    role: PeerRole;
    onData: (bytes: Uint8Array) => void;
}

export class GhostPairingBus {
    private peers = new Set<Peer>();

    constructor(readonly joinCode: string) {}

    join(role: PeerRole, onData: (bytes: Uint8Array) => void): () => void {
        const peer: Peer = { role, onData };
        this.peers.add(peer);
        return () => { this.peers.delete(peer); };
    }

    broadcast(from: PeerRole, bytes: Uint8Array): void {
        for (const p of this.peers) {
            if (p.role === from) continue;
            p.onData(bytes);
        }
    }

    playgroundSend(msg: GhostInbound): void {
        this.broadcast('playground', encodeGhostMessage(msg));
    }

    ghostSend(msg: GhostOutbound): void {
        this.broadcast('ghost', encodeGhostMessage(msg));
    }
}

/** Minimal GhostBot-side handler: answers ping with pong + session connected. */
export function handleGhostInbound(
    bytes: Uint8Array,
    reply: (msg: GhostOutbound) => void,
): void {
    const msg = decodeGhostMessage(bytes);
    if (!msg || !('type' in msg)) return;
    if (msg.type === 'ping') {
        reply({ v: 1, type: 'pong' });
        reply({
            v: 1,
            type: 'session',
            joinCode: 'TEST01',
            status: 'connected',
            detail: 'Playground connected (mock)',
            peerId: 'mock-pg',
        });
    }
}
