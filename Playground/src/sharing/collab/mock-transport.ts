// In-browser fake of a peer mesh built on BroadcastChannel. Two tabs of the
// same origin joining the same roomId become "peers" of each other — no
// network involved. Drives every unit-level test of the session layer and
// also doubles as the cold-start demo mode (open two browser tabs, type).
//
// Wire format on the channel is JSON-encoded for easy debugging; messages
// addressed to a specific peer are dropped by everyone else.

import type {
    CollabRoom,
    CollabTransport,
    JoinOptions,
    RoomStatus,
    Unsubscribe,
} from './transport';

type ChannelMessage =
    | { kind: 'hello'; from: string }
    | { kind: 'bye'; from: string }
    | { kind: 'msg'; from: string; to: string | null; b64: string };

function channelNameFor(opts: JoinOptions): string {
    return `fade-collab:mock:${opts.appId}:${opts.roomId}`;
}

function b64encode(bytes: Uint8Array): string {
    let s = '';
    for (let i = 0; i < bytes.byteLength; i++) s += String.fromCharCode(bytes[i]);
    return btoa(s);
}
function b64decode(s: string): Uint8Array {
    const bin = atob(s);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
}

function newSelfId(): string {
    return 'mock-' + Math.random().toString(36).slice(2, 10);
}

class MockRoom implements CollabRoom {
    readonly selfId: string;
    status: RoomStatus = 'discovering';

    private channel: BroadcastChannel | null;
    private peers = new Set<string>();
    private joinCbs = new Set<(id: string) => void>();
    private leaveCbs = new Set<(id: string) => void>();
    private msgCbs = new Set<(id: string, bytes: Uint8Array) => void>();
    private statusCbs = new Set<(s: RoomStatus) => void>();

    constructor(opts: JoinOptions) {
        this.selfId = newSelfId();
        this.channel = new BroadcastChannel(channelNameFor(opts));
        this.channel.onmessage = (ev) => this.onWire(ev.data as ChannelMessage);

        // Announce ourselves so existing peers respond with their own hellos,
        // and we mark the room connected once anyone responds (or instantly
        // when we're the first, since at that point there's nothing to wait
        // for and the host should be able to start editing).
        this.channel.postMessage({ kind: 'hello', from: this.selfId } satisfies ChannelMessage);
        setTimeout(() => this.setStatus('connected'), 0);
    }

    private setStatus(s: RoomStatus) {
        if (this.status === s) return;
        this.status = s;
        for (const cb of this.statusCbs) cb(s);
    }

    private onWire(msg: ChannelMessage) {
        if (!msg || typeof msg !== 'object') return;
        if (msg.from === this.selfId) return; // our own echoes — BroadcastChannel doesn't echo, but be defensive
        switch (msg.kind) {
            case 'hello':
                if (!this.peers.has(msg.from)) {
                    this.peers.add(msg.from);
                    for (const cb of this.joinCbs) cb(msg.from);
                    // Respond so the newcomer learns about us too. Without
                    // this the first-joiner only sees us when they get a
                    // message from us, which delays the "peer joined" event
                    // until the session layer happens to broadcast.
                    this.channel?.postMessage({ kind: 'hello', from: this.selfId } satisfies ChannelMessage);
                }
                break;
            case 'bye':
                if (this.peers.has(msg.from)) {
                    this.peers.delete(msg.from);
                    for (const cb of this.leaveCbs) cb(msg.from);
                }
                break;
            case 'msg':
                if (msg.to !== null && msg.to !== this.selfId) return;
                if (!this.peers.has(msg.from)) {
                    // First time seeing this peer — treat as join too.
                    this.peers.add(msg.from);
                    for (const cb of this.joinCbs) cb(msg.from);
                }
                {
                    const bytes = b64decode(msg.b64);
                    for (const cb of this.msgCbs) cb(msg.from, bytes);
                }
                break;
        }
    }

    onPeerJoin(cb: (peerId: string) => void): Unsubscribe {
        this.joinCbs.add(cb);
        return () => this.joinCbs.delete(cb);
    }
    onPeerLeave(cb: (peerId: string) => void): Unsubscribe {
        this.leaveCbs.add(cb);
        return () => this.leaveCbs.delete(cb);
    }
    onStatusChange(cb: (s: RoomStatus) => void): Unsubscribe {
        this.statusCbs.add(cb);
        return () => this.statusCbs.delete(cb);
    }
    onMessage(cb: (id: string, bytes: Uint8Array) => void): Unsubscribe {
        this.msgCbs.add(cb);
        return () => this.msgCbs.delete(cb);
    }
    getPeers(): string[] { return [...this.peers]; }

    sendTo(peerId: string, bytes: Uint8Array): void {
        if (!this.channel) return;
        this.channel.postMessage({
            kind: 'msg', from: this.selfId, to: peerId, b64: b64encode(bytes),
        } satisfies ChannelMessage);
    }
    broadcast(bytes: Uint8Array): void {
        if (!this.channel) return;
        this.channel.postMessage({
            kind: 'msg', from: this.selfId, to: null, b64: b64encode(bytes),
        } satisfies ChannelMessage);
    }

    async leave(): Promise<void> {
        if (!this.channel) return;
        try { this.channel.postMessage({ kind: 'bye', from: this.selfId } satisfies ChannelMessage); }
        catch { /* channel might already be closed */ }
        this.channel.close();
        this.channel = null;
        this.peers.clear();
        this.setStatus('closed');
        this.joinCbs.clear();
        this.leaveCbs.clear();
        this.msgCbs.clear();
        this.statusCbs.clear();
    }
}

export const mockTransport: CollabTransport = {
    id: 'mock',
    capabilities: {
        persistent: false,
        requiresAuth: false,
        label: 'Mock (BroadcastChannel — same browser only)',
    },
    async isAvailable(): Promise<boolean> {
        return typeof BroadcastChannel !== 'undefined';
    },
    async join(opts: JoinOptions): Promise<CollabRoom> {
        return new MockRoom(opts);
    },
};
