// Multi-peer GhostBot hub. One trystero room (the ghost's stable code) hosts
// any number of Playground peers at once — different tabs, browsers, or
// projects. Each peer is tracked independently: its own identity, approval
// state, and in-flight inference streams. Replies are addressed to the
// requesting peer (the `to` field) so one Playground never sees another's
// tokens — even though the bytes ride a broadcast channel (unicast is
// unreliable; see CLAUDE.md gotcha 5).
//
// Trust-on-first-use: a peer sends a stable `clientId` in its hello. The
// first time we see a clientId the user approves it (persisted); afterwards
// that client auto-connects. A stranger who guessed the code still has to be
// approved, so they can't silently use the GPU.

import { getRelaySockets, joinRoom, selfId as trysteroSelfId } from 'trystero/nostr';
import {
    decodeMessage,
    encodeMessage,
    type GhostInbound,
    type GhostOutbound,
    GHOSTBOT_ACTION,
    GHOSTBOT_APP_ID,
} from './protocol';
import { ghostIceConfig, toTrysteroRtcConfig, GHOST_NOSTR_RELAYS } from './ice-config';
import { broadcastGhostSend } from './send';

export type StreamHandler = (
    req: Extract<GhostInbound, { type: 'stream' }>,
    signal: AbortSignal,
) =>
    | AsyncIterable<import('./protocol').GhostStreamEvent>
    | Promise<AsyncIterable<import('./protocol').GhostStreamEvent>>;

export interface GhostModelStatus {
    loaded: boolean;
    name?: string;
    path?: string;
}

export type ConnStatus = 'pending' | 'connected' | 'inferring';

/** A single connected Playground, as surfaced to the UI. */
export interface GhostConnection {
    peerId: string;
    clientId: string | null;
    label: string;
    status: ConnStatus;
    approved: boolean;
    /** Number of in-flight inference streams. */
    activeStreams: number;
}

export interface GhostHub {
    readonly joinCode: string;
    readonly selfId: string;
    start(): void;
    stop(): void;
    setStreamHandler(handler: StreamHandler | null): void;
    setModelStatus(status: GhostModelStatus): void;
    listConnections(): GhostConnection[];
    relayCount(): { open: number; total: number };
    approve(peerId: string): void;
    deny(peerId: string): void;
    disconnect(peerId: string): void;
    /** Fires whenever the connection set or any connection's state changes. */
    onChange(cb: () => void): () => void;
    /** Fires on human-facing activity (logging). */
    onLog(cb: (line: string) => void): () => void;
}

const APPROVED_KEY = 'ghostbot.approvedClients';

function loadApproved(): Set<string> {
    try {
        const raw = localStorage.getItem(APPROVED_KEY);
        const arr = raw ? JSON.parse(raw) : [];
        return new Set(Array.isArray(arr) ? arr.filter((x) => typeof x === 'string') : []);
    } catch { return new Set(); }
}

function saveApproved(set: Set<string>): void {
    try { localStorage.setItem(APPROVED_KEY, JSON.stringify([...set])); } catch { /* ignore */ }
}

interface PeerState {
    peerId: string;
    clientId: string | null;
    label: string;
    status: ConnStatus;
    approved: boolean;
    streams: Map<number, AbortController>;
}

export function createGhostHub(joinCode: string): GhostHub {
    const normalized = joinCode.trim().toUpperCase();
    const approvedClients = loadApproved();
    const peers = new Map<string, PeerState>();
    const changeCbs = new Set<() => void>();
    const logCbs = new Set<(line: string) => void>();

    let room: ReturnType<typeof joinRoom> | null = null;
    let sendGhost: ((data: Uint8Array, target?: string | string[] | null) => void) | null = null;
    let streamHandler: StreamHandler | null = null;
    let modelStatus: GhostModelStatus = { loaded: false };
    let gone = false;

    const notify = () => { for (const cb of changeCbs) { try { cb(); } catch { /* ignore */ } } };
    const log = (line: string) => { for (const cb of logCbs) { try { cb(line); } catch { /* ignore */ } } };

    /** Send a message to one peer (addressed) or everyone (to omitted). */
    const send = (msg: GhostOutbound, to?: string) => {
        if (!sendGhost || gone) return;
        broadcastGhostSend(sendGhost, encodeMessage(to ? { ...msg, to } : msg));
    };

    const peerLabel = (p: PeerState) => p.label || `${p.peerId.slice(0, 6)}…`;

    const sendModelStatusTo = (peerId: string) => {
        send({ v: 1, type: 'model-status', ...modelStatus }, peerId);
    };

    /** Accept a peer: confirm auth, greet, share model status. */
    const accept = (p: PeerState) => {
        p.approved = true;
        p.status = 'connected';
        send({ v: 1, type: 'auth', status: 'approved' }, p.peerId);
        send({ v: 1, type: 'pong' }, p.peerId);
        sendModelStatusTo(p.peerId);
        notify();
    };

    const handleHello = (p: PeerState, msg: Extract<GhostInbound, { type: 'hello' }>) => {
        p.clientId = msg.clientId;
        p.label = msg.label || p.label;
        if (msg.clientId && approvedClients.has(msg.clientId)) {
            log(`✓ ${peerLabel(p)} reconnected (known client)`);
            accept(p);
        } else {
            p.status = 'pending';
            send({ v: 1, type: 'auth', status: 'pending', detail: 'Awaiting approval in GhostBot' }, p.peerId);
            log(`• ${peerLabel(p)} wants to connect — approve in the Connections panel`);
            notify();
        }
    };

    const runStream = async (p: PeerState, msg: Extract<GhostInbound, { type: 'stream' }>) => {
        if (!streamHandler) {
            send({ v: 1, type: 'stream-error', streamId: msg.id, message: 'No model loaded' }, p.peerId);
            send({ v: 1, type: 'stream-end', streamId: msg.id }, p.peerId);
            return;
        }
        const ac = new AbortController();
        p.streams.set(msg.id, ac);
        p.status = 'inferring';
        notify();
        try {
            for await (const event of await streamHandler(msg, ac.signal)) {
                if (ac.signal.aborted) break;
                send({ v: 1, type: 'stream-event', streamId: msg.id, event }, p.peerId);
                if (event.kind === 'done') break;
            }
            if (!ac.signal.aborted) {
                send({ v: 1, type: 'stream-event', streamId: msg.id, event: { kind: 'done', finishReason: 'stop' } }, p.peerId);
            }
        } catch (e) {
            send({ v: 1, type: 'stream-error', streamId: msg.id, message: (e as Error).message ?? String(e) }, p.peerId);
        } finally {
            p.streams.delete(msg.id);
            send({ v: 1, type: 'stream-end', streamId: msg.id }, p.peerId);
            p.status = p.streams.size > 0 ? 'inferring' : 'connected';
            notify();
        }
    };

    const handleInbound = (peerId: string, msg: GhostInbound) => {
        const p = peers.get(peerId);
        if (!p) return;

        if (msg.type === 'hello') { handleHello(p, msg); return; }

        // Everything below requires approval — a guesser who reaches the room
        // but isn't approved gets nothing but a reminder it's pending.
        if (!p.approved) {
            send({ v: 1, type: 'auth', status: 'pending', detail: 'Awaiting approval in GhostBot' }, peerId);
            return;
        }

        if (msg.type === 'ping') {
            send({ v: 1, type: 'pong' }, peerId);
            sendModelStatusTo(peerId);
            return;
        }
        if (msg.type === 'abort') {
            p.streams.get(msg.streamId)?.abort();
            p.streams.delete(msg.streamId);
            return;
        }
        if (msg.type === 'stream') {
            void runStream(p, msg);
        }
    };

    return {
        joinCode: normalized,
        selfId: trysteroSelfId,

        start() {
            if (room || gone) return;
            room = joinRoom(
                {
                    appId: GHOSTBOT_APP_ID,
                    rtcConfig: toTrysteroRtcConfig(ghostIceConfig()),
                    relayUrls: GHOST_NOSTR_RELAYS,
                },
                normalized,
            );
            const [s, receive] = room.makeAction(GHOSTBOT_ACTION);
            sendGhost = s as typeof sendGhost;
            log(`Listening on code ${normalized}`);

            receive((data: unknown, peerId: string) => {
                let bytes: Uint8Array;
                if (data instanceof Uint8Array) bytes = data;
                else if (data instanceof ArrayBuffer) bytes = new Uint8Array(data);
                else if (ArrayBuffer.isView(data)) bytes = new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
                else return;
                const msg = decodeMessage(bytes);
                if (!msg || !('type' in msg)) return;
                // Only Playground→Ghost message types are handled here.
                if (msg.type === 'hello' || msg.type === 'ping' || msg.type === 'stream'
                    || msg.type === 'abort') {
                    handleInbound(peerId, msg as GhostInbound);
                }
            });

            room.onPeerJoin((peerId: string) => {
                if (!peers.has(peerId)) {
                    peers.set(peerId, {
                        peerId, clientId: null, label: '', status: 'pending', approved: false,
                        streams: new Map(),
                    });
                }
                // Wait for hello to learn identity; nudge the peer to send it.
                notify();
            });

            room.onPeerLeave((peerId: string) => {
                const p = peers.get(peerId);
                if (p) { for (const ac of p.streams.values()) ac.abort(); }
                peers.delete(peerId);
                log(`× ${peerId.slice(0, 6)}… disconnected`);
                notify();
            });
        },

        stop() {
            gone = true;
            for (const p of peers.values()) for (const ac of p.streams.values()) ac.abort();
            peers.clear();
            room?.leave();
            room = null;
            sendGhost = null;
            notify();
        },

        setStreamHandler(handler) { streamHandler = handler; },

        setModelStatus(status) {
            modelStatus = status;
            for (const p of peers.values()) if (p.approved) sendModelStatusTo(p.peerId);
        },

        listConnections() {
            return [...peers.values()].map((p) => ({
                peerId: p.peerId,
                clientId: p.clientId,
                label: p.label,
                status: p.status,
                approved: p.approved,
                activeStreams: p.streams.size,
            }));
        },

        relayCount() {
            const sockets = Object.values(getRelaySockets());
            return {
                open: sockets.filter((s) => s?.readyState === WebSocket.OPEN).length,
                total: GHOST_NOSTR_RELAYS.length,
            };
        },

        approve(peerId) {
            const p = peers.get(peerId);
            if (!p) return;
            if (p.clientId) { approvedClients.add(p.clientId); saveApproved(approvedClients); }
            log(`✓ Approved ${peerLabel(p)}`);
            accept(p);
        },

        deny(peerId) {
            const p = peers.get(peerId);
            if (!p) return;
            send({ v: 1, type: 'auth', status: 'denied', detail: 'GhostBot owner denied this connection' }, peerId);
            for (const ac of p.streams.values()) ac.abort();
            peers.delete(peerId);
            log(`✗ Denied ${peerLabel(p)}`);
            notify();
        },

        disconnect(peerId) {
            const p = peers.get(peerId);
            if (!p) return;
            // Forget the client so it must be re-approved next time.
            if (p.clientId) { approvedClients.delete(p.clientId); saveApproved(approvedClients); }
            send({ v: 1, type: 'auth', status: 'denied', detail: 'Disconnected by GhostBot owner' }, peerId);
            for (const ac of p.streams.values()) ac.abort();
            peers.delete(peerId);
            log(`× Disconnected ${peerLabel(p)}`);
            notify();
        },

        onChange(cb) { changeCbs.add(cb); return () => changeCbs.delete(cb); },
        onLog(cb) { logCbs.add(cb); return () => logCbs.delete(cb); },
    };
}
