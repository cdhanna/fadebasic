// Trystero-backed transport. Uses BitTorrent trackers (the `torrent`
// strategy) for peer discovery — zero infra on our side. Each peer punches
// through their NAT via WebRTC and the session-layer bytes ride the data
// channel.
//
// One thing to know: Trystero exposes a single-listener model
// (`room.onPeerJoin(fn)` overwrites the previous callback). Our CollabRoom
// supports many listeners, so we multiplex internally.

import { joinRoom, selfId as trysteroSelfId } from 'trystero/torrent';
import type {
    CollabRoom,
    CollabTransport,
    JoinOptions,
    RoomStatus,
    Unsubscribe,
} from './transport';
import { selectWorkingIceConfig, toTrysteroRtcConfig, TRACKER_RELAY_URLS } from './ice-probe';

const ACTION = 'y';  // Trystero requires a 12-byte-or-less action ID

class TrysteroRoom implements CollabRoom {
    readonly selfId: string;
    readonly note: string | null;
    status: RoomStatus = 'discovering';

    private trysteroRoom: ReturnType<typeof joinRoom> | null;
    private sendY!: (data: Uint8Array, targetPeers?: string | string[]) => void;
    private peers = new Set<string>();
    private joinCbs = new Set<(id: string) => void>();
    private leaveCbs = new Set<(id: string) => void>();
    private msgCbs = new Set<(id: string, bytes: Uint8Array) => void>();
    private statusCbs = new Set<(s: RoomStatus) => void>();

    constructor(opts: JoinOptions, rtcConfig: RTCConfiguration, note: string | null) {
        this.selfId = trysteroSelfId;
        this.note = note;
        this.trysteroRoom = joinRoom(
            {
                appId: opts.appId,
                password: opts.password,
                rtcConfig: toTrysteroRtcConfig(rtcConfig),
                relayUrls: TRACKER_RELAY_URLS,
            },
            opts.roomId,
        );

        const [send, receive] = this.trysteroRoom.makeAction(ACTION);
        this.sendY = send as typeof this.sendY;
        receive((data: unknown, peerId: string) => {
            // Trystero hands us ArrayBuffer for binary; normalise to Uint8Array.
            let bytes: Uint8Array;
            if (data instanceof Uint8Array) bytes = data;
            else if (data instanceof ArrayBuffer) bytes = new Uint8Array(data);
            else if (ArrayBuffer.isView(data)) bytes = new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
            else return;
            for (const cb of this.msgCbs) cb(peerId, bytes);
        });

        this.trysteroRoom.onPeerJoin((peerId: string) => {
            this.peers.add(peerId);
            for (const cb of this.joinCbs) cb(peerId);
            this.setStatus('connected');
        });
        this.trysteroRoom.onPeerLeave((peerId: string) => {
            this.peers.delete(peerId);
            for (const cb of this.leaveCbs) cb(peerId);
            // Back to "looking for peers" if we ended up alone — without
            // this the UI keeps showing 'connected' after everyone left,
            // which is misleading.
            if (this.peers.size === 0) this.setStatus('discovering');
        });

        // We stay in 'discovering' until a peer actually connects. The
        // previous version auto-flipped to 'connected' after 1500 ms so
        // the UI didn't sit on "discovering" forever — but that was
        // dishonest: it claimed everything was fine when in fact no peer
        // had joined yet, hiding ICE failures behind a green pill. The
        // host's connection-warning watchdog now provides the "something
        // is off" signal after its timeout if no peer ever arrives.
    }

    private setStatus(s: RoomStatus) {
        if (this.status === s) return;
        this.status = s;
        for (const cb of this.statusCbs) cb(s);
    }

    onPeerJoin(cb: (peerId: string) => void): Unsubscribe {
        this.joinCbs.add(cb); return () => this.joinCbs.delete(cb);
    }
    onPeerLeave(cb: (peerId: string) => void): Unsubscribe {
        this.leaveCbs.add(cb); return () => this.leaveCbs.delete(cb);
    }
    onStatusChange(cb: (s: RoomStatus) => void): Unsubscribe {
        this.statusCbs.add(cb); return () => this.statusCbs.delete(cb);
    }
    onMessage(cb: (id: string, bytes: Uint8Array) => void): Unsubscribe {
        this.msgCbs.add(cb); return () => this.msgCbs.delete(cb);
    }
    getPeers(): string[] { return [...this.peers]; }

    sendTo(peerId: string, bytes: Uint8Array): void {
        if (!this.trysteroRoom) return;
        try { this.sendY(bytes, peerId); }
        catch (e) { console.warn('[fade-collab] trystero sendTo failed', e); }
    }
    broadcast(bytes: Uint8Array): void {
        if (!this.trysteroRoom) return;
        try { this.sendY(bytes); }
        catch (e) { console.warn('[fade-collab] trystero broadcast failed', e); }
    }

    async leave(): Promise<void> {
        if (!this.trysteroRoom) return;
        try { await this.trysteroRoom.leave(); } catch { /* ignore */ }
        this.trysteroRoom = null;
        this.peers.clear();
        this.setStatus('closed');
        this.joinCbs.clear();
        this.leaveCbs.clear();
        this.msgCbs.clear();
        this.statusCbs.clear();
    }
}

export const trysteroTransport: CollabTransport = {
    id: 'trystero',
    capabilities: {
        persistent: false,
        requiresAuth: false,
        label: 'Trystero (WebRTC via BitTorrent trackers)',
    },
    async isAvailable(): Promise<boolean> {
        // Modern browsers all have RTCPeerConnection; the trackers
        // themselves may fail at connect time but that surfaces as "no
        // peer ever joined" rather than a hard isAvailable miss.
        return typeof RTCPeerConnection !== 'undefined';
    },
    async join(opts: JoinOptions): Promise<CollabRoom> {
        // Probe what ICE config this browser can actually gather
        // candidates with — some Firefox-on-VPN setups bail on the full
        // config but succeed with a minimal one. Result is cached after
        // the first call so subsequent joins are instant.
        const ice = await selectWorkingIceConfig();
        return new TrysteroRoom(opts, ice.config, ice.note);
    },
};
