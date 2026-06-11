// Manual-signaling transport: zero-infra fallback when Trystero's signaling
// layer (BitTorrent trackers / DHT) fails. The two peers exchange a
// base64-encoded SDP offer and answer by hand — paste into a chat, email,
// signal message, whatever the user has handy — then the WebRTC data
// channel comes up directly between them.
//
// Limitations vs the Trystero transport:
//   • Two-peer only (one host, one guest). Multi-peer manual signaling
//     would require N×N offer/answer exchanges, which is impractical
//     by hand.
//   • Full ICE gathering happens before the offer/answer is emitted
//     (no trickle), so blob generation takes 0.5–4 s.
//   • If either side's NAT is symmetric without TURN, this still won't
//     connect — manual SDP fixes signaling failures, not NAT traversal.
//
// What it DOES fix:
//   • Trystero's signaling failing silently (trackers down, rate-limited,
//     blocked by corporate DNS, etc.). With manual SDP the user delivers
//     the signaling messages themselves, so there's no third party that
//     can break.
//
// The CollabTransport.join shape doesn't fit a multi-step interactive
// flow (the room can't exist until the user has copy-pasted twice), so
// the transport entry exposes `startManualHost` / `startManualJoin`
// directly. The panel wizard drives those.

import type {
    CollabRoom,
    CollabTransport,
    JoinOptions,
    RoomStatus,
    Unsubscribe,
} from './transport';
import { selectWorkingIceConfig } from './ice-probe';

interface SignalingEnvelope {
    /** 'offer' (host → guest) or 'answer' (guest → host). */
    kind: 'offer' | 'answer';
    /** Raw SDP from RTCPeerConnection.localDescription. */
    sdp: string;
    /** Stable id for the side that produced this envelope. Used as the
     *  CollabRoom.selfId on the peer that receives it. */
    selfId: string;
    /** Optional. Mostly informational — surfaced in the UI so the user can
     *  verify they pasted the right blob. */
    roomId?: string;
    /** Format version so older clients can reject incompatible blobs. */
    v: 1;
}

function encodeEnvelope(env: SignalingEnvelope): string {
    const json = JSON.stringify(env);
    // Base64-encode the UTF-8 bytes; then make it URL-safe and strip
    // padding. The URL-safe form survives copy-paste through chat clients
    // (Slack, Discord) that sometimes auto-link plain base64 by inserting
    // line breaks around / and + characters.
    const utf8 = new TextEncoder().encode(json);
    let bin = '';
    for (let i = 0; i < utf8.length; i++) bin += String.fromCharCode(utf8[i]);
    return btoa(bin)
        .replace(/\+/g, '-')
        .replace(/\//g, '_')
        .replace(/=+$/, '');
}

function decodeEnvelope(blob: string): SignalingEnvelope {
    // Strip *all* whitespace before decoding — Slack, Discord, and email
    // clients love to insert line breaks into long base64 strings.
    const cleaned = blob.replace(/\s+/g, '');
    if (!cleaned) throw new Error('Empty paste.');
    const std = cleaned.replace(/-/g, '+').replace(/_/g, '/');
    const padded = std + '='.repeat((4 - std.length % 4) % 4);
    let bin: string;
    try { bin = atob(padded); }
    catch { throw new Error("Pasted text isn't a valid signaling blob (base64 decode failed)."); }
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    let json: string;
    try { json = new TextDecoder().decode(bytes); }
    catch { throw new Error("Pasted text isn't a valid signaling blob (UTF-8 decode failed)."); }
    let env: any;
    try { env = JSON.parse(json); }
    catch { throw new Error("Pasted text isn't a valid signaling blob (JSON parse failed)."); }
    if (!env || typeof env !== 'object') throw new Error("Pasted blob has the wrong shape.");
    if (env.v !== 1) throw new Error(`Unsupported signaling format version (${env.v}).`);
    if (env.kind !== 'offer' && env.kind !== 'answer') throw new Error("Pasted blob is neither an offer nor an answer.");
    if (typeof env.sdp !== 'string' || !env.sdp.length) throw new Error("Pasted blob is missing the SDP body.");
    if (typeof env.selfId !== 'string' || !env.selfId.length) throw new Error("Pasted blob is missing the selfId.");
    return env as SignalingEnvelope;
}

function newSelfId(): string {
    return 'manual-' + Math.random().toString(36).slice(2, 10);
}

/** Synchronous best-effort parse of a pasted signaling blob. Used by
 *  the modal so the user gets immediate feedback ("that's not a valid
 *  offer") before we close the modal and start a wizard. Doesn't touch
 *  RTCPeerConnection — the actual setRemoteDescription happens later
 *  inside `acceptOffer` / `acceptAnswer`. */
export interface SignalingBlobPreview {
    kind: 'offer' | 'answer';
    roomId?: string;
}
export function previewSignalingBlob(blob: string): SignalingBlobPreview {
    const env = decodeEnvelope(blob);
    return { kind: env.kind, roomId: env.roomId };
}

/** Soft cap on ICE gathering. Chrome usually finishes under 2 s on a
 *  clean network; Firefox is closer to 3 s and can stretch to 5+ on
 *  VPN/symmetric-NAT setups. We give Firefox enough headroom but bail
 *  before the user feels like the wizard is hung. */
const GATHER_TIMEOUT_MS = 8000;

/** Returned by `attachCandidateCollector` — `collected` is mutated as
 *  candidate events arrive, `done` resolves once gathering finishes
 *  (or the timeout fires). Use `buildGatheredSdp` to consume the pair. */
interface CandidateCollector {
    collected: string[];
    done: Promise<void>;
}

/** Begin collecting candidates from `pc.onicecandidate` events.
 *
 *  Critical: call this BEFORE `setLocalDescription`. Firefox emits
 *  candidate events as soon as gathering starts, which can begin
 *  during the await of `setLocalDescription`. If the listener isn't
 *  attached yet, those early candidates are lost — and on Firefox we
 *  rely on these events as the only source of candidates (it does NOT
 *  splice them into `pc.localDescription.sdp` the way Chrome does). */
function attachCandidateCollector(pc: RTCPeerConnection): CandidateCollector {
    const collected: string[] = [];
    const startedAt = performance.now();
    const eventLog: string[] = [];
    const note = (s: string) => {
        const dt = Math.round(performance.now() - startedAt);
        eventLog.push(`+${dt}ms ${s}`);
        console.info('[fade-collab][manual-gather]', `+${dt}ms`, s);
    };
    note(`collector attached; initial gatherState=${pc.iceGatheringState} iceConnState=${pc.iceConnectionState}`);
    const done = new Promise<void>((resolve) => {
        let settled = false;
        const finish = (reason: string) => {
            if (settled) return;
            settled = true;
            note(`finish (${reason}); collected=${collected.length} finalGatherState=${pc.iceGatheringState}`);
            pc.removeEventListener('icecandidate', onCandidate);
            pc.removeEventListener('icegatheringstatechange', onState);
            try { pc.onicecandidate = null; pc.onicegatheringstatechange = null; } catch { /* ignore */ }
            clearTimeout(timer);
            // Stash diagnostic log on the pc for the error path to read.
            (pc as any).__fadeGatherLog = eventLog;
            resolve();
        };
        const onCandidate = (ev: RTCPeerConnectionIceEvent) => {
            if (ev.candidate === null) {
                note('icecandidate null (end-of-candidates)');
                finish('end-of-candidates event');
                return;
            }
            if (ev.candidate.candidate) {
                // Parse "candidate:foo bar baz typ host ..." to extract type
                // for diagnostic purposes only.
                const m = /typ\s+(\S+)/.exec(ev.candidate.candidate);
                const type = m ? m[1] : '?';
                note(`icecandidate typ=${type} (${ev.candidate.candidate.slice(0, 80)}…)`);
                collected.push(ev.candidate.candidate);
            } else {
                note('icecandidate event with empty .candidate string');
            }
        };
        const onState = () => {
            note(`gatheringstate=${pc.iceGatheringState}`);
            if (pc.iceGatheringState === 'complete') finish('gather state complete');
        };
        // Belt-and-suspenders: subscribe via BOTH addEventListener and the
        // legacy on* property. There's a long-standing rumour (and matching
        // behaviour in older Firefox) that the ICE machine only kicks off
        // gather when `onicecandidate` is assigned as a property, not via
        // addEventListener — which would explain a "sanity test in console
        // works but app code doesn't" mismatch. Setting both is harmless
        // (events fire once regardless) and covers the case if true.
        pc.addEventListener('icecandidate', onCandidate);
        pc.addEventListener('icegatheringstatechange', onState);
        pc.onicecandidate = onCandidate;
        pc.onicegatheringstatechange = onState;
        const timer = setTimeout(() => finish('timeout'), GATHER_TIMEOUT_MS);
        if (pc.iceGatheringState === 'complete') finish('already complete at attach time');
    });
    return { collected, done };
}

/** Wait for the collector and build a complete SDP.
 *
 *  Chrome-vs-Firefox trap: Chrome auto-merges every candidate into
 *  `pc.localDescription.sdp` as gather progresses, so reading the SDP
 *  after gather-complete gives a self-contained doc. Firefox doesn't —
 *  it only fires `onicecandidate` events and leaves `localDescription`
 *  as the bare negotiated shell (no `a=candidate:` lines,
 *  `c=IN IP4 0.0.0.0`). Two Firefox peers ship empty shells, both
 *  declare trickle, neither trickles, connection never comes up.
 *
 *  We trust whichever path filled the SDP — for Chrome we use it
 *  as-is (after appending end-of-candidates), for Firefox we splice
 *  the collected candidates in. If we have zero candidates of any
 *  kind, throw so the wizard surfaces a real error rather than a
 *  doomed blob. */
async function buildGatheredSdp(pc: RTCPeerConnection, collector: CandidateCollector): Promise<string> {
    await collector.done;
    const sdp = injectCandidatesIntoSdp(pc.localDescription!.sdp, collector.collected);
    if (!/^a=candidate:/m.test(sdp)) {
        const log = ((pc as any).__fadeGatherLog as string[] | undefined) ?? [];
        console.warn('[fade-collab][manual-gather] no candidates produced. Gather log:\n' + log.join('\n'));
        const ua = navigator.userAgent;
        const isFirefox = /firefox/i.test(ua);
        const hint = isFirefox
            ? 'Firefox often blocks gather entirely when ANY ICE server is unreachable. '
                + 'Try (1) about:config → media.peerconnection.ice.no_host = false (in case host candidates were disabled), '
                + '(2) about:config → media.peerconnection.ice.obfuscate_host_addresses = false (disable mDNS for diagnosis), '
                + '(3) check the browser console for the [fade-collab][manual-gather] event log to see what fired.'
            : 'Your network is likely blocking STUN, or the browser disabled all host candidates. '
                + 'Check the browser console for the [fade-collab][manual-gather] event log.';
        throw new Error(
            'ICE gathering finished without producing any usable candidates. '
            + hint + ' '
            + 'If nothing works, add a TURN server in localStorage key "fade.collab.customIceServers".',
        );
    }
    return sdp;
}

/** Splice candidate lines into the SDP's media section. No-op if the
 *  SDP already contains candidates (Chrome path). For Firefox, we
 *  append the collected candidates + `a=end-of-candidates` so the peer
 *  knows there's nothing else coming. */
function injectCandidatesIntoSdp(baseSdp: string, candidates: string[]): string {
    if (/^a=candidate:/m.test(baseSdp)) {
        // Chrome already inlined candidates. Make sure the
        // end-of-candidates marker is present so the peer doesn't
        // wait for trickled ones that aren't coming.
        if (/^a=end-of-candidates/m.test(baseSdp)) return baseSdp;
        const trimmed = baseSdp.replace(/[\r\n]+$/, '');
        return trimmed + '\r\na=end-of-candidates\r\n';
    }
    if (candidates.length === 0) return baseSdp;

    // Firefox path. We have one m=application section; appending the
    // candidates at the end of the SDP places them inside that section
    // (no other m= line follows). RTCIceCandidate.candidate strings
    // start with "candidate:..." — SDP attribute lines need "a=" prefix.
    const candLines = candidates.map((c) => `a=${c.replace(/^a=/, '')}`);
    const lines = baseSdp.replace(/[\r\n]+$/, '').split(/\r?\n/);
    lines.push(...candLines);
    lines.push('a=end-of-candidates');
    return lines.join('\r\n') + '\r\n';
}

class ManualRoom implements CollabRoom {
    readonly selfId: string;
    readonly note: string | null;
    status: RoomStatus = 'connected';

    private pc: RTCPeerConnection;
    private dc: RTCDataChannel;
    private peerId: string;
    private peers = new Set<string>();
    private joinCbs = new Set<(id: string) => void>();
    private leaveCbs = new Set<(id: string) => void>();
    private msgCbs = new Set<(id: string, bytes: Uint8Array) => void>();
    private statusCbs = new Set<(s: RoomStatus) => void>();
    private gone = false;
    /** Incoming messages received before any `onMessage` subscriber
     *  exists get parked here and replayed on the first subscribe. The
     *  CollabSession races against the data channel: the host's start()
     *  awaits seedHostWorkspace before subscribing, and the guest's
     *  whenConnected→new CollabSession→start path also has gaps. Without
     *  this buffer, the y-sync handshake messages that the other side
     *  fires immediately on its own peer-join can land in those gaps
     *  and be lost, leaving the doc empty (no file sync). */
    private msgBuffer: Array<{ peerId: string; bytes: Uint8Array }> | null = [];
    /** Tracks whether the initial onPeerJoin microtask has fired. New
     *  subscribers attaching after that fire get their own catch-up
     *  fire; subscribers attaching before it ride the initial fire and
     *  must NOT get a catch-up too, or onPeerJoin double-fires and
     *  the session sends double sync handshakes for no reason. */
    private firedInitialJoin = false;

    constructor(selfId: string, peerId: string, pc: RTCPeerConnection, dc: RTCDataChannel) {
        this.selfId = selfId;
        this.peerId = peerId;
        this.pc = pc;
        this.dc = dc;
        this.note = 'Direct connection (manual signaling).';
        this.peers.add(peerId);

        this.dc.binaryType = 'arraybuffer';
        this.dc.onmessage = (ev) => {
            const data = ev.data;
            let bytes: Uint8Array;
            if (data instanceof Uint8Array) bytes = data;
            else if (data instanceof ArrayBuffer) bytes = new Uint8Array(data);
            else if (ArrayBuffer.isView(data)) {
                const view = data as ArrayBufferView;
                bytes = new Uint8Array(view.buffer, view.byteOffset, view.byteLength);
            }
            else return;
            if (this.msgBuffer) {
                this.msgBuffer.push({ peerId: this.peerId, bytes });
                return;
            }
            for (const cb of this.msgCbs) cb(this.peerId, bytes);
        };
        this.dc.onclose = () => this.handleDisconnect();
        this.dc.onerror = () => this.handleDisconnect();
        this.pc.oniceconnectionstatechange = () => {
            const st = this.pc.iceConnectionState;
            if (st === 'failed' || st === 'closed' || st === 'disconnected') {
                this.handleDisconnect();
            }
        };

        // Fire the join callback on the next microtask so subscribers
        // attached during the synchronous construction window all get
        // notified, mirroring how the mock and Trystero rooms behave.
        queueMicrotask(() => {
            if (this.gone) return;
            this.firedInitialJoin = true;
            for (const cb of this.joinCbs) try { cb(this.peerId); } catch { /* ignore */ }
        });
    }

    private handleDisconnect(): void {
        if (this.gone) return;
        this.gone = true;
        if (this.peers.has(this.peerId)) {
            this.peers.delete(this.peerId);
            for (const cb of this.leaveCbs) try { cb(this.peerId); } catch { /* ignore */ }
        }
        this.setStatus('closed');
    }

    private setStatus(s: RoomStatus): void {
        if (this.status === s) return;
        this.status = s;
        for (const cb of this.statusCbs) try { cb(s); } catch { /* ignore */ }
    }

    onPeerJoin(cb: (peerId: string) => void): Unsubscribe {
        this.joinCbs.add(cb);
        // Catch up subscribers that attach AFTER the initial constructor
        // microtask has already fired (e.g. someone re-subscribing late).
        // If we haven't fired yet, this subscriber will ride the initial
        // fire — don't schedule a duplicate microtask, or it'll
        // double-fire and the session will send sync handshakes twice.
        if (this.firedInitialJoin && this.peers.has(this.peerId) && !this.gone) {
            queueMicrotask(() => {
                if (this.joinCbs.has(cb)) try { cb(this.peerId); } catch { /* ignore */ }
            });
        }
        return () => this.joinCbs.delete(cb);
    }
    onPeerLeave(cb: (peerId: string) => void): Unsubscribe {
        this.leaveCbs.add(cb); return () => this.leaveCbs.delete(cb);
    }
    onStatusChange(cb: (s: RoomStatus) => void): Unsubscribe {
        this.statusCbs.add(cb); return () => this.statusCbs.delete(cb);
    }
    onMessage(cb: (id: string, bytes: Uint8Array) => void): Unsubscribe {
        const wasEmpty = this.msgCbs.size === 0;
        this.msgCbs.add(cb);
        // First subscriber drains the buffer (held since construction)
        // so the y-sync handshake messages the other side fired during
        // our construction window actually reach the CollabSession.
        // Replay synchronously to preserve message order.
        if (wasEmpty && this.msgBuffer) {
            const buf = this.msgBuffer;
            this.msgBuffer = null;
            for (const { peerId, bytes } of buf) {
                try { cb(peerId, bytes); } catch (e) {
                    console.warn('[fade-collab] manual replay cb threw', e);
                }
            }
        }
        return () => this.msgCbs.delete(cb);
    }
    getPeers(): string[] { return [...this.peers]; }

    sendTo(_peerId: string, bytes: Uint8Array): void {
        if (this.gone || this.dc.readyState !== 'open') return;
        // Cast through any to bridge TS lib's strict ArrayBufferView<ArrayBuffer>
        // requirement — Uint8Array's buffer is typed as ArrayBufferLike
        // (could be SharedArrayBuffer), which the DOM lib refuses, but at
        // runtime data channels happily accept any Uint8Array.
        try { this.dc.send(bytes as unknown as ArrayBuffer); }
        catch (e) { console.warn('[fade-collab] manual sendTo failed', e); }
    }
    broadcast(bytes: Uint8Array): void {
        if (this.gone || this.dc.readyState !== 'open') return;
        try { this.dc.send(bytes as unknown as ArrayBuffer); }
        catch (e) { console.warn('[fade-collab] manual broadcast failed', e); }
    }

    async leave(): Promise<void> {
        this.gone = true;
        try { this.dc.close(); } catch { /* ignore */ }
        try { this.pc.close(); } catch { /* ignore */ }
        this.peers.clear();
        this.setStatus('closed');
        this.joinCbs.clear();
        this.leaveCbs.clear();
        this.msgCbs.clear();
        this.statusCbs.clear();
    }
}

export interface ManualHostHandle {
    /** Stable id printed alongside the offer so the user can name the
     *  session. Embedded in the envelope so the guest can show it too. */
    readonly roomId: string;
    /** Base64-encoded offer to share with the guest. Ready immediately
     *  after the handle resolves (ICE gathering already completed). */
    readonly offer: string;
    /** Accept the answer the guest copied back. Resolves to a CollabRoom
     *  once the data channel opens. Rejects on parse failure or
     *  connection timeout (~30 s). */
    acceptAnswer(answerBlob: string): Promise<CollabRoom>;
    /** Tear down the half-open connection. Safe to call any time. */
    cancel(): void;
}

export interface ManualJoinHandle {
    /** Accept the host's offer. Returns the answer blob to copy back to
     *  the host AND a promise that resolves once the host has accepted
     *  the answer and the data channel opens. */
    acceptOffer(offerBlob: string): Promise<{ answer: string; whenConnected: Promise<CollabRoom>; roomId?: string }>;
    cancel(): void;
}

/** Start a host-side manual signaling flow. The returned handle's
 *  `.offer` is ready immediately — the user shares it with the guest by
 *  whatever channel they have available, then pastes the guest's answer
 *  blob into `acceptAnswer`. */
export async function startManualHost(opts: { roomId: string }): Promise<ManualHostHandle> {
    const ice = await selectWorkingIceConfig();
    const pc = new RTCPeerConnection(ice.config);
    // The host MUST create the data channel before generating the offer —
    // the SDP's m=application line is what carries the channel descriptor
    // to the guest. If we wait until after, the offer doesn't include it
    // and the guest's answer has nothing to bind to.
    const dc = pc.createDataChannel('fade-collab', { ordered: true });
    const selfId = newSelfId();

    // Attach the collector BEFORE setLocalDescription so we don't
    // miss any candidates Firefox emits during the gather kick-off.
    let offerSdp: string;
    try {
        const collector = attachCandidateCollector(pc);
        console.info('[fade-collab][manual-gather] iceServers in use:', JSON.stringify(ice.config.iceServers));
        const offer = await pc.createOffer();
        console.info('[fade-collab][manual-gather] createOffer resolved; offer.type=%s sdp.length=%d', offer.type, offer.sdp?.length ?? 0);
        await pc.setLocalDescription(offer);
        console.info(
            '[fade-collab][manual-gather] setLocalDescription resolved; signalingState=%s gatheringState=%s connectionState=%s iceConnState=%s localDescription.sdp has ice-ufrag=%s',
            pc.signalingState, pc.iceGatheringState, pc.connectionState, pc.iceConnectionState,
            /a=ice-ufrag:/.test(pc.localDescription?.sdp ?? ''),
        );
        // Kick the gather if it didn't start. Some Firefox configurations
        // appear to leave state at 'new' even after a clean setLocalDescription.
        if (pc.iceGatheringState === 'new') {
            try {
                console.info('[fade-collab][manual-gather] gatherState still "new" after setLocalDescription — calling restartIce() to force the gather machinery on.');
                (pc as any).restartIce?.();
            } catch (e) { console.warn('[fade-collab][manual-gather] restartIce threw', e); }
        }
        offerSdp = await buildGatheredSdp(pc, collector);
    } catch (e) {
        // Critical: close the PC + DC on any failure during setup so we
        // don't leak zombies. Firefox limits concurrent RTCPeerConnections
        // per origin (~5), and accumulated zombies poison subsequent
        // gather attempts in the same tab.
        try { dc.close(); } catch { /* ignore */ }
        try { pc.close(); } catch { /* ignore */ }
        throw e;
    }

    const envelope: SignalingEnvelope = {
        v: 1,
        kind: 'offer',
        sdp: offerSdp,
        selfId,
        roomId: opts.roomId,
    };
    const offerBlob = encodeEnvelope(envelope);

    let cancelled = false;
    const handle: ManualHostHandle = {
        roomId: opts.roomId,
        offer: offerBlob,
        async acceptAnswer(answerBlob: string): Promise<CollabRoom> {
            if (cancelled) throw new Error('This signaling flow was cancelled.');
            const env = decodeEnvelope(answerBlob);
            if (env.kind !== 'answer') {
                throw new Error("That blob is an offer, not an answer. Make sure your collaborator sent you their *answer* (the second blob).");
            }
            if (env.roomId && env.roomId !== opts.roomId) {
                throw new Error(`Answer is for a different session (${env.roomId}). Did you paste the wrong blob?`);
            }
            await pc.setRemoteDescription({ type: 'answer', sdp: env.sdp });
            await new Promise<void>((resolve, reject) => {
                const timer = setTimeout(
                    () => reject(new Error('Connection timed out. Your network may need TURN; see Settings → Live Session.')),
                    30000,
                );
                if (dc.readyState === 'open') { clearTimeout(timer); resolve(); return; }
                dc.addEventListener('open', () => { clearTimeout(timer); resolve(); }, { once: true });
                dc.addEventListener('error', () => { clearTimeout(timer); reject(new Error('Data channel error during handshake.')); }, { once: true });
                // If ICE itself fails, surface that too — otherwise the
                // user just sits at "connecting…" for 30s with no info.
                const onIce = () => {
                    if (pc.iceConnectionState === 'failed') {
                        clearTimeout(timer);
                        pc.removeEventListener('iceconnectionstatechange', onIce);
                        reject(new Error('ICE negotiation failed. Likely a strict NAT on one side; add a TURN server in Settings → Live Session.'));
                    }
                };
                pc.addEventListener('iceconnectionstatechange', onIce);
            });
            return new ManualRoom(selfId, env.selfId, pc, dc);
        },
        cancel(): void {
            if (cancelled) return;
            cancelled = true;
            try { dc.close(); } catch { /* ignore */ }
            try { pc.close(); } catch { /* ignore */ }
        },
    };
    return handle;
}

/** Start a guest-side manual signaling flow. Returns a handle that
 *  accepts the host's offer; the result of that call carries the answer
 *  the guest copies back, plus a promise that resolves once the data
 *  channel opens. */
export async function startManualJoin(_opts: { roomId?: string }): Promise<ManualJoinHandle> {
    const ice = await selectWorkingIceConfig();
    const pc = new RTCPeerConnection(ice.config);
    const selfId = newSelfId();

    let resolveDc: (dc: RTCDataChannel) => void = () => {};
    const dcPromise = new Promise<RTCDataChannel>((r) => { resolveDc = r; });
    pc.ondatachannel = (ev) => resolveDc(ev.channel);

    let cancelled = false;
    const handle: ManualJoinHandle = {
        async acceptOffer(offerBlob: string) {
            if (cancelled) throw new Error('This signaling flow was cancelled.');
            const env = decodeEnvelope(offerBlob);
            if (env.kind !== 'offer') {
                throw new Error("That blob is an answer, not an offer. Make sure your host sent you their *offer* (the first blob).");
            }
            let answerSdp: string;
            try {
                // Attach the collector BEFORE setLocalDescription on the
                // answer side too — same Firefox-misses-early-candidates
                // problem as the host path.
                const collector = attachCandidateCollector(pc);
                await pc.setRemoteDescription({ type: 'offer', sdp: env.sdp });
                const answer = await pc.createAnswer();
                await pc.setLocalDescription(answer);
                console.info(
                    '[fade-collab][manual-gather] (answer) setLocalDescription resolved; signalingState=%s gatheringState=%s connectionState=%s iceConnState=%s',
                    pc.signalingState, pc.iceGatheringState, pc.connectionState, pc.iceConnectionState,
                );
                if (pc.iceGatheringState === 'new') {
                    try {
                        console.info('[fade-collab][manual-gather] (answer) gatherState still "new" — calling restartIce()');
                        (pc as any).restartIce?.();
                    } catch (e) { console.warn('[fade-collab][manual-gather] (answer) restartIce threw', e); }
                }
                answerSdp = await buildGatheredSdp(pc, collector);
            } catch (e) {
                // Same zombie-PC concern as the host path — close the PC
                // on any failure so retries don't exhaust Firefox's
                // per-origin PC budget.
                try { pc.close(); } catch { /* ignore */ }
                throw e;
            }

            const answerEnv: SignalingEnvelope = {
                v: 1,
                kind: 'answer',
                sdp: answerSdp,
                selfId,
                roomId: env.roomId,
            };
            const answerBlob = encodeEnvelope(answerEnv);

            const whenConnected = (async (): Promise<CollabRoom> => {
                const dc = await dcPromise;
                await new Promise<void>((resolve, reject) => {
                    const timer = setTimeout(
                        () => reject(new Error('Connection timed out. Your network may need TURN; see Settings → Live Session.')),
                        30000,
                    );
                    if (dc.readyState === 'open') { clearTimeout(timer); resolve(); return; }
                    dc.addEventListener('open', () => { clearTimeout(timer); resolve(); }, { once: true });
                    dc.addEventListener('error', () => { clearTimeout(timer); reject(new Error('Data channel error during handshake.')); }, { once: true });
                    const onIce = () => {
                        if (pc.iceConnectionState === 'failed') {
                            clearTimeout(timer);
                            pc.removeEventListener('iceconnectionstatechange', onIce);
                            reject(new Error('ICE negotiation failed. Likely a strict NAT on one side; add a TURN server in Settings → Live Session.'));
                        }
                    };
                    pc.addEventListener('iceconnectionstatechange', onIce);
                });
                return new ManualRoom(selfId, env.selfId, pc, dc);
            })();

            return { answer: answerBlob, whenConnected, roomId: env.roomId };
        },
        cancel(): void {
            if (cancelled) return;
            cancelled = true;
            try { pc.close(); } catch { /* ignore */ }
        },
    };
    return handle;
}

/** Transport-registry entry so the picker UI lists manual signaling as
 *  an option. `join()` throws — the panel must call `startManualHost` /
 *  `startManualJoin` directly when the user selects this transport,
 *  because the manual flow is multi-step and interactive (offer/answer
 *  copy-paste) and doesn't fit the single-shot join(opts) → CollabRoom
 *  shape the other transports use. */
export const manualTransport: CollabTransport = {
    id: 'manual',
    capabilities: {
        persistent: false,
        requiresAuth: false,
        label: 'Manual (paste offer/answer — most reliable)',
    },
    async isAvailable(): Promise<boolean> {
        return typeof RTCPeerConnection !== 'undefined';
    },
    async join(_opts: JoinOptions): Promise<CollabRoom> {
        throw new Error('Manual transport requires the signaling wizard; the panel calls startManualHost / startManualJoin directly.');
    },
};
