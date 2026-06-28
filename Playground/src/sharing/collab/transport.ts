// The transport seam for the live-editing feature.
//
// Each adapter (mock for in-browser tests, Trystero for WebRTC, future Gist
// for GitHub-signaled WebRTC) presents this same surface: "give me a room of
// peers I can sendTo / broadcast to, and tell me when peers come and go."
//
// The session layer (session.ts) sits on top and is transport-agnostic —
// it pumps Yjs sync + awareness messages through whatever room it's given.

export type TransportId = 'mock' | 'trystero' | 'manual';

export type RoomStatus =
    | 'discovering'   // joining — looking for peers via signaling/tracker
    | 'connected'     // at least one peer connected (or room is open, host alone)
    | 'reconnecting'  // lost connection, retrying
    | 'closed';       // leave() called, or transport gave up

export interface PeerIdentity {
    /** Free-form display name shown in the collaborator list. Required —
     *  the join modal won't let the user proceed without one. */
    displayName: string;
    /** HSL color string for cursor + presence dot. Deterministic from the
     *  display name so the same person looks the same across sessions. */
    color: string;
    /** Filled in when GitHub-authed; informational for the UI. */
    githubLogin?: string;
}

export interface JoinOptions {
    /** App-wide namespace. Hard-coded to 'fade-playground' so two unrelated
     *  apps using the same trackers can't collide on a roomId. */
    appId: string;
    /** The share code. Hashed by transports that require fixed-length IDs. */
    roomId: string;
    /** End-to-end secret for traffic encryption (Trystero supports natively;
     *  the mock transport ignores it). Optional. */
    password?: string;
    /** Local peer's identity. Stamped into awareness by the session layer
     *  for the collaborator list / cursor labels. */
    identity: PeerIdentity;
}

export interface TransportCapabilities {
    /** Can the room outlive a disconnect? Mock/Trystero rooms are ephemeral
     *  (no peers = room is gone); a future Gist transport would set true. */
    persistent: boolean;
    /** Does isAvailable() require an upstream auth token (e.g. GitHub)? */
    requiresAuth: boolean;
    /** Free text. UI shows this in the transport-picker for trust signals. */
    label: string;
}

export type Unsubscribe = () => void;

export interface CollabRoom {
    /** Stable ID for this peer inside this room. Used as the awareness
     *  clientID surrogate when needed. Different transports use different
     *  ID schemes — treat as opaque. */
    readonly selfId: string;
    /** Snapshot of current status; mirrors what onStatusChange emits. */
    readonly status: RoomStatus;
    /** Optional human-readable note from the transport about how it
     *  configured itself (e.g. "fell back to STUN-only because the
     *  TURN probe failed"). Surfaced in the Live Session panel so the
     *  user can see degraded connectivity modes. Null when there's
     *  nothing noteworthy to report. */
    readonly note?: string | null;
    /** Fires once per peer that joins after we've joined. The session layer
     *  uses this to kick off Yjs sync handshake with the new peer. */
    onPeerJoin(cb: (peerId: string) => void): Unsubscribe;
    onPeerLeave(cb: (peerId: string) => void): Unsubscribe;
    onStatusChange(cb: (status: RoomStatus) => void): Unsubscribe;
    /** Address a specific peer. Used for sync-step-2 replies. */
    sendTo(peerId: string, bytes: Uint8Array): void;
    /** Send to every other peer in the room. Used for awareness updates
     *  and Yjs document updates. */
    broadcast(bytes: Uint8Array): void;
    /** Fires for every message we receive, regardless of broadcast/sendTo. */
    onMessage(cb: (peerId: string, bytes: Uint8Array) => void): Unsubscribe;
    /** Current peer IDs (not including self). */
    getPeers(): string[];
    /** Leave the room. After this, no more events fire and send/broadcast
     *  are no-ops. Idempotent. */
    leave(): Promise<void>;
}

export interface CollabTransport {
    readonly id: TransportId;
    readonly capabilities: TransportCapabilities;
    /** Quick probe: can this transport even attempt a join right now?
     *  (e.g. Gist transport returns false when there's no GitHub token.) */
    isAvailable(): Promise<boolean>;
    join(opts: JoinOptions): Promise<CollabRoom>;
}
