// GhostBot transport — same Trystero/WebRTC stack as live collab.

// GhostBot pairing uses the trystero/nostr strategy, not torrent — the
// WebTorrent tracker ecosystem decayed to too few live trackers for reliable
// rendezvous. Nostr relays are far healthier. Must match ghostBot/src/session.ts.
import { getRelaySockets, joinRoom, selfId as trysteroSelfId } from 'trystero/nostr';
import {
    decodeGhostMessage,
    encodeGhostMessage,
    type GhostInbound,
    type GhostOutbound,
    GHOSTBOT_ACTION,
    GHOSTBOT_APP_ID,
} from './ghostbot-protocol';
import { broadcastGhostSend } from './ghostbot-send';
import {
    selectWorkingIceConfig,
    toTrysteroRtcConfig,
    GHOST_NOSTR_RELAYS,
} from '../../sharing/collab/ice-probe';

export type GhostPeerStatus = 'discovering' | 'connected' | 'closed' | 'error';

export interface GhostPeerRoom {
    readonly joinCode: string;
    readonly selfId: string;
    status: GhostPeerStatus;
    send(msg: GhostInbound): void;
    onMessage(cb: (msg: GhostOutbound) => void): () => void;
    onStatus(cb: (s: GhostPeerStatus) => void): () => void;
    /** Signaling-tracker connectivity: how many relay sockets are open.
     *  open === 0 means peer discovery cannot happen at all. */
    relayCount(): { open: number; total: number };
    leave(): void;
}

export interface GhostIdentity {
    /** Stable per-browser id so GhostBot can trust-on-first-use. */
    clientId: string;
    /** Human-readable label shown in GhostBot's connections list. */
    label: string;
}

/** Join the GhostBot Trystero room — mirrors trystero-transport.ts. On
 *  connect it announces `identity` so GhostBot can apply trust-on-first-use,
 *  and it filters inbound messages addressed to other peers (multi-peer
 *  rooms broadcast; the `to` field scopes each reply). */
export async function joinGhostRoom(joinCode: string, identity: GhostIdentity): Promise<GhostPeerRoom> {
    const normalized = joinCode.trim().toUpperCase();
    const ice = await selectWorkingIceConfig();
    if (ice.note) console.info('[ghostbot]', ice.note);

    let status: GhostPeerStatus = 'discovering';
    let peerId: string | null = null;
    const msgCbs = new Set<(msg: GhostOutbound) => void>();
    const statusCbs = new Set<(s: GhostPeerStatus) => void>();

    const room = joinRoom(
        {
            appId: GHOSTBOT_APP_ID,
            rtcConfig: toTrysteroRtcConfig(ice.config),
            relayUrls: GHOST_NOSTR_RELAYS,
        },
        normalized,
    );
    const [sendRaw, receive] = room.makeAction(GHOSTBOT_ACTION);

    const setStatus = (s: GhostPeerStatus) => {
        status = s;
        for (const cb of statusCbs) cb(s);
    };

    const emit = (msg: GhostOutbound) => {
        for (const cb of msgCbs) cb(msg);
    };

    const sendWire = (msg: GhostInbound) => {
        if (status === 'closed') return;
        broadcastGhostSend(
            sendRaw as (d: Uint8Array, t: string | null | undefined) => void,
            encodeGhostMessage(msg),
        );
    };

    receive((data: unknown, from: string) => {
        let bytes: Uint8Array;
        if (data instanceof Uint8Array) bytes = data;
        else if (data instanceof ArrayBuffer) bytes = new Uint8Array(data);
        else if (ArrayBuffer.isView(data)) {
            bytes = new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
        } else return;
        peerId = from;
        const msg = decodeGhostMessage(bytes) as GhostOutbound | null;
        if (!msg) return;
        // Multi-peer rooms broadcast every reply; ignore ones addressed to a
        // different peer so one Playground never sees another's tokens.
        if (msg.to && msg.to !== trysteroSelfId) return;
        emit(msg);
    });

    room.onPeerJoin((id: string) => {
        peerId = id;
        // WebRTC link is up, but the provider must wait for GhostBot's `auth`
        // (approval) before treating the session as ready — so we only flip
        // the transport-level status; readiness is the provider's call.
        setStatus('connected');
        // Announce who we are so GhostBot can approve us (trust-on-first-use).
        sendWire({ v: 1, type: 'hello', clientId: identity.clientId, label: identity.label });
        sendWire({ v: 1, type: 'ping' });
    });

    room.onPeerLeave((id: string) => {
        if (peerId === id) {
            peerId = null;
            setStatus('discovering');
        }
    });

    return {
        joinCode: normalized,
        selfId: trysteroSelfId,
        get status() { return status; },
        send(msg: GhostInbound) { sendWire(msg); },
        onMessage(cb) {
            msgCbs.add(cb);
            return () => { msgCbs.delete(cb); };
        },
        onStatus(cb) {
            statusCbs.add(cb);
            return () => { statusCbs.delete(cb); };
        },
        relayCount() {
            const sockets = Object.values(getRelaySockets());
            return {
                open: sockets.filter(s => s?.readyState === WebSocket.OPEN).length,
                total: GHOST_NOSTR_RELAYS.length,
            };
        },
        leave() {
            room.leave();
            setStatus('closed');
        },
    };
}
