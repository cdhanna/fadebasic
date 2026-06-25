// ChatProvider that streams inference from a local GhostBot peer over WebRTC.

import type {
    ChatProvider,
    FinishReason,
    Msg,
    ProviderCapabilities,
    ProviderProgress,
    StreamEvent,
    StreamOptions,
} from './types';
import { joinGhostRoom, type GhostIdentity, type GhostPeerRoom, type GhostPeerStatus } from './ghostbot-transport';

// The code now identifies the GhostBot to connect TO (its stable address),
// entered once by the user and persisted. A stable clientId lets GhostBot
// trust-on-first-use so we auto-reconnect after the first approval.
const CODE_KEY = 'fade.ai.ghostbot.code';
const CLIENT_ID_KEY = 'fade.ai.ghostbot.clientId';
const PEER_WAIT_MS = 120_000;

export type GhostConnectionStatus =
    | 'idle'
    | 'waiting'
    | 'pending'
    | 'connected'
    | 'disconnected'
    | 'reconnecting'
    | 'error';

function stableClientId(): string {
    let id = localStorage.getItem(CLIENT_ID_KEY);
    if (!id) {
        id = 'pg-' + Math.random().toString(36).slice(2, 10) + Math.random().toString(36).slice(2, 6);
        localStorage.setItem(CLIENT_ID_KEY, id);
    }
    return id;
}

function clientLabel(): string {
    const project = localStorage.getItem('fade.activeProject');
    return project ? `Playground · ${project}` : 'Playground';
}

export interface GhostConnectionState {
    status: GhostConnectionStatus;
    joinCode: string;
    detail?: string;
    peerId?: string;
    /** Reported by GhostBot over the wire (`model-status` messages).
     *  undefined until the first report arrives. */
    modelLoaded?: boolean;
    modelName?: string;
}

export class GhostBotProvider implements ChatProvider {
    readonly id = 'ghostbot:local';
    readonly label = 'GhostBot (local llama.cpp)';
    readonly capabilities: ProviderCapabilities = {
        supportsTools: false,
        maxContext: 32_768,
        isCached: false,
        backend: 'llama.cpp',
    };

    private room: GhostPeerRoom | null = null;
    private joinCode: string;
    private ready = false;
    private everConnected = false;
    private unsubRoom: (() => void) | null = null;
    /** Aborts an in-flight waitForPeer (Load / Reconnect). */
    private waitCancel: (() => void) | null = null;
    /** Auto-reconnect watchdog: refreshes the room after a drop until the
     *  peer returns or attempts are exhausted. Disabled by an explicit stop. */
    private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    private reconnectAttempts = 0;
    private autoReconnect = true;
    private progressListeners = new Set<(p: ProviderProgress) => void>();
    private connectionListeners = new Set<(s: GhostConnectionState) => void>();
    private connectionState: GhostConnectionState;
    private nextStreamId = 1;

    private readonly clientId = stableClientId();

    constructor(joinCode?: string) {
        this.joinCode = (joinCode ?? localStorage.getItem(CODE_KEY) ?? '').trim().toUpperCase();
        if (this.joinCode) localStorage.setItem(CODE_KEY, this.joinCode);
        this.connectionState = {
            status: this.joinCode ? 'idle' : 'idle',
            joinCode: this.joinCode,
            detail: this.joinCode
                ? 'Load model to connect'
                : "Enter your GhostBot's code to connect",
        };
    }

    getJoinCode(): string {
        return this.joinCode;
    }

    /** Set the GhostBot code to connect to (entered by the user). Persisted. */
    setJoinCode(code: string): void {
        this.joinCode = code.trim().toUpperCase();
        localStorage.setItem(CODE_KEY, this.joinCode);
        this.setConnection({ status: 'idle', detail: 'Code set — click Reconnect' });
    }

    hasJoinCode(): boolean {
        return this.joinCode.length > 0;
    }

    private identity(): GhostIdentity {
        return { clientId: this.clientId, label: clientLabel() };
    }

    getConnectionState(): GhostConnectionState {
        return { ...this.connectionState };
    }

    onConnectionState(cb: (s: GhostConnectionState) => void): () => void {
        this.connectionListeners.add(cb);
        try { cb(this.getConnectionState()); } catch { /* ignore */ }
        return () => { this.connectionListeners.delete(cb); };
    }

    countTokens(text: string): number {
        return Math.ceil(text.length / 4);
    }

    onProgress(cb: (p: ProviderProgress) => void): () => void {
        this.progressListeners.add(cb);
        return () => { this.progressListeners.delete(cb); };
    }

    private emitProgress(text: string, pct: number): void {
        const p = { text, pct };
        for (const l of this.progressListeners) {
            try { l(p); } catch { /* ignore */ }
        }
    }

    private setConnection(patch: Partial<GhostConnectionState> & { status: GhostConnectionStatus }): void {
        this.connectionState = {
            ...this.connectionState,
            ...patch,
            joinCode: this.joinCode,
        };
        for (const l of this.connectionListeners) {
            try { l(this.getConnectionState()); } catch { /* ignore */ }
        }
    }

    private attachRoom(room: GhostPeerRoom): void {
        this.unsubRoom?.();
        const unsubs: Array<() => void> = [];

        unsubs.push(room.onStatus((s: GhostPeerStatus) => {
            this.onTransportStatus(s);
        }));

        unsubs.push(room.onMessage((msg) => {
            if (msg.type === 'model-status') {
                this.setConnection({
                    status: this.connectionState.status,
                    modelLoaded: msg.loaded,
                    modelName: msg.name,
                });
                return;
            }
            // Approval gate (trust-on-first-use): GhostBot tells us whether
            // this Playground is accepted. Only `approved` (or a pong, which
            // GhostBot sends only post-approval) makes us ready.
            if (msg.type === 'auth') {
                if (msg.status === 'approved') {
                    this.markReady();
                } else if (msg.status === 'pending') {
                    this.ready = false;
                    this.setConnection({
                        status: 'pending',
                        detail: msg.detail ?? 'Waiting for approval in GhostBot…',
                    });
                } else if (msg.status === 'denied') {
                    this.ready = false;
                    this.setConnection({
                        status: 'error',
                        detail: msg.detail ?? 'GhostBot denied this connection.',
                    });
                }
                return;
            }
            if (msg.type === 'pong') {
                this.markReady('GhostBot connected');
            }
        }));

        this.unsubRoom = () => {
            for (const u of unsubs) u();
        };
    }

    private markReady(detail = 'GhostBot connected'): void {
        this.ready = true;
        this.everConnected = true;
        this.autoReconnect = true;
        this.clearReconnect();
        this.setConnection({ status: 'connected', detail });
    }

    private onTransportStatus(s: GhostPeerStatus): void {
        if (s === 'connected') {
            // WebRTC linked — but not ready until GhostBot approves us.
            if (!this.ready) {
                this.setConnection({
                    status: 'pending',
                    detail: 'Linked — waiting for GhostBot to accept this connection…',
                });
            }
            return;
        }
        if (s === 'discovering') {
            this.ready = false;
            // A drop AFTER we were connected → auto-reconnect (the room stays
            // open, so trystero may re-pair on its own; the watchdog refreshes
            // the room if it doesn't). Before first connect → just keep waiting.
            if (this.everConnected && this.autoReconnect) {
                this.setConnection({
                    status: 'reconnecting',
                    detail: 'Connection dropped — reconnecting…',
                    modelLoaded: undefined,
                    modelName: undefined,
                });
                this.scheduleReconnect();
            } else {
                this.setConnection({
                    status: 'waiting',
                    detail: `Looking for GhostBot on code ${this.joinCode}…`,
                    modelLoaded: undefined,
                    modelName: undefined,
                });
            }
            return;
        }
        if (s === 'closed') {
            this.ready = false;
            this.setConnection({ status: 'idle', detail: 'Session closed' });
        }
    }

    private clearReconnect(): void {
        if (this.reconnectTimer) { clearTimeout(this.reconnectTimer); this.reconnectTimer = null; }
        this.reconnectAttempts = 0;
    }

    /** Refresh the room after a drop, with backoff, until the peer returns
     *  (markReady clears us) or we give up. Cheap when trystero re-pairs on
     *  its own — the first delay gives that a chance before we force a rejoin. */
    private scheduleReconnect(): void {
        if (this.reconnectTimer || !this.autoReconnect) return;
        const delay = Math.min(4000 * 2 ** this.reconnectAttempts, 20_000); // 4s,8s,16s,20s…
        this.reconnectTimer = setTimeout(async () => {
            this.reconnectTimer = null;
            if (this.ready || !this.autoReconnect) { this.reconnectAttempts = 0; return; }
            this.reconnectAttempts++;
            if (this.reconnectAttempts > 6) {
                this.reconnectAttempts = 0;
                this.setConnection({ status: 'disconnected', detail: 'Could not reconnect automatically — press Reconnect.' });
                return;
            }
            this.setConnection({ status: 'reconnecting', detail: `Reconnecting… (attempt ${this.reconnectAttempts})` });
            try { await this.openRoom(); } catch { /* relays down; retry */ }
            // Keep going until markReady() clears us or we hit the cap.
            this.scheduleReconnect();
        }, delay);
    }

    private async openRoom(): Promise<GhostPeerRoom> {
        this.room?.leave();
        const room = await joinGhostRoom(this.joinCode, this.identity());
        this.room = room;
        this.attachRoom(room);
        const via = 'WebRTC';
        this.setConnection({
            status: 'waiting',
            detail: `Connecting via ${via} — enter code ${this.joinCode} in GhostBot, then Connect`,
        });
        room.send({ v: 1, type: 'ping' });
        return room;
    }

    private waitForPeer(timeoutMs: number, label: string): Promise<void> {
        const room = this.room;
        if (!room) return Promise.reject(new Error('No GhostBot session'));

        if (room.status === 'connected' && this.ready) {
            return Promise.resolve();
        }

        return new Promise<void>((resolve, reject) => {
            const timer = setTimeout(() => {
                cleanup();
                const pending = this.connectionState.status === 'pending';
                const err = new Error(pending
                    ? `GhostBot hasn't accepted this connection yet. Open GhostBot and click Approve, then Reconnect.`
                    : `Couldn't reach GhostBot on code ${this.joinCode} within ${timeoutMs / 1000}s. `
                        + `Make sure the app is running and showing that code, then Reconnect.`);
                this.setConnection({ status: 'error', detail: err.message });
                reject(err);
            }, timeoutMs);

            const onApproved = () => {
                cleanup();
                this.markReady();
                this.emitProgress('GhostBot connected', 1);
                resolve();
            };

            // Readiness now requires GhostBot's approval, not just a WebRTC
            // link — resolve on `auth approved` or a post-approval pong.
            const unsubMsg = room.onMessage((msg) => {
                if (msg.type === 'pong') onApproved();
                else if (msg.type === 'auth' && msg.status === 'approved') onApproved();
                else if (msg.type === 'auth' && msg.status === 'denied') {
                    cleanup();
                    const err = new Error(msg.detail ?? 'GhostBot denied this connection.');
                    this.setConnection({ status: 'error', detail: err.message });
                    reject(err);
                }
            });
            const unsubStatus = () => { /* status handled by attachRoom */ };

            // Narrate signaling-tracker connectivity while we wait, so the
            // bar distinguishes "can't reach signaling" from "no peer yet".
            let lastRelayOpen = -1;
            const relayPoll = setInterval(() => {
                const { open, total } = room.relayCount();
                if (open === lastRelayOpen) return;
                lastRelayOpen = open;
                // Don't clobber a 'pending approval' detail with relay chatter.
                if (this.connectionState.status === 'pending') return;
                this.setConnection({
                    status: this.connectionState.status,
                    detail: open === 0
                        ? `No signaling trackers reachable (0/${total}) — check network/firewall.`
                        : `Looking for GhostBot on ${this.joinCode}… (relays: ${open}/${total})`,
                });
            }, 3000);

            const cleanup = () => {
                clearTimeout(timer);
                clearInterval(relayPoll);
                unsubStatus();
                unsubMsg();
                this.waitCancel = null;
            };

            this.waitCancel = () => {
                cleanup();
                const err = new Error('GhostBot session cancelled.');
                err.name = 'GhostBotCancelled';
                reject(err);
            };

            this.emitProgress(label, 0.4);
            room.send({ v: 1, type: 'ping' });
        });
    }

    async ensureReady(): Promise<void> {
        if (this.ready && this.room?.status === 'connected') return;
        if (!this.joinCode) {
            this.setConnection({ status: 'idle', detail: "Enter your GhostBot's code to connect" });
            throw new Error("Enter your GhostBot's code (shown in the GhostBot app) to connect.");
        }

        this.emitProgress('Opening GhostBot session…', 0.1);
        if (!this.room || this.room.status === 'closed') {
            await this.openRoom();
        }
        await this.waitForPeer(PEER_WAIT_MS, `Waiting for GhostBot (code: ${this.joinCode})…`);
    }

    /** Re-join the signaling room and wait for GhostBot (same join code). */
    async reconnect(): Promise<void> {
        this.ready = false;
        this.autoReconnect = true;
        this.clearReconnect();
        this.setConnection({
            status: 'reconnecting',
            detail: 'Reconnecting…',
        });
        this.emitProgress('Reconnecting to GhostBot…', 0.2);
        await this.openRoom();
        await this.waitForPeer(PEER_WAIT_MS, `Reconnecting (code: ${this.joinCode})…`);
    }

    async reset(): Promise<void> {
        this.waitCancel?.();
        this.autoReconnect = false; // explicit stop — don't fight the user
        this.clearReconnect();
        this.ready = false;
        this.everConnected = false;
        this.unsubRoom?.();
        this.unsubRoom = null;
        this.room?.leave();
        this.room = null;
        this.setConnection({
            status: 'idle',
            detail: 'Session ended — click Load Model to start again',
            peerId: undefined,
            modelLoaded: undefined,
            modelName: undefined,
        });
    }

    async *stream(opts: StreamOptions): AsyncIterable<StreamEvent> {
        // Fail loudly: a thrown error reaches the agent loop, which renders
        // it as a visible error bubble. The old silent done/error made a
        // broken GhostBot look like the model just said nothing.
        if (!this.room || !this.ready) {
            throw new Error(
                `GhostBot is not connected (code ${this.joinCode}). `
                + 'Open the GhostBot app, load a model, enter the code, then press Connect.',
            );
        }

        const streamId = this.nextStreamId++;
        const queue: StreamEvent[] = [];
        let finished = false;
        let errorMessage: string | null = null;
        let waiter: (() => void) | null = null;

        const unsubMsg = this.room.onMessage((msg) => {
            if (msg.type === 'stream-event' && msg.streamId === streamId) {
                if (msg.event.kind === 'text') {
                    queue.push({ kind: 'text', delta: msg.event.delta });
                } else if (msg.event.kind === 'done') {
                    queue.push({ kind: 'done', finishReason: msg.event.finishReason as FinishReason });
                    finished = true;
                }
                waiter?.();
            } else if (msg.type === 'stream-error' && msg.streamId === streamId) {
                errorMessage = `GhostBot inference failed: ${msg.message}`;
                finished = true;
                waiter?.();
            } else if (msg.type === 'stream-end' && msg.streamId === streamId) {
                finished = true;
                waiter?.();
            }
        });

        const unsubStatus = this.room.onStatus((s) => {
            if (s !== 'connected') {
                errorMessage = 'GhostBot disconnected mid-stream.';
                finished = true;
                waiter?.();
            }
        });

        opts.signal?.addEventListener('abort', () => {
            this.room?.send({ v: 1, type: 'abort', streamId });
        }, { once: true });

        try {
            this.room.send({
                v: 1,
                type: 'stream',
                id: streamId,
                messages: opts.messages as Msg[],
                maxTokens: opts.maxTokens ?? 2048,
                temperature: opts.temperature ?? 0.2,
            });

            while (true) {
                if (queue.length > 0) {
                    const ev = queue.shift()!;
                    yield ev;
                    if (ev.kind === 'done') return;
                    continue;
                }
                if (errorMessage) {
                    this.setConnection({
                        status: this.connectionState.status,
                        detail: errorMessage,
                    });
                    throw new Error(errorMessage);
                }
                if (finished) {
                    yield { kind: 'done', finishReason: 'stop' };
                    return;
                }
                await new Promise<void>(r => { waiter = r; });
            }
        } finally {
            unsubMsg();
            unsubStatus();
        }
    }
}
