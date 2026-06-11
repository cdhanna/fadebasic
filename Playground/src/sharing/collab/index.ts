// Public surface for the live-session feature. Bootstrap glue lives in
// `bootstrap.ts` (the function main.ts calls to set everything up); the
// other modules can be imported individually for tests.

export type {
    CollabRoom,
    CollabTransport,
    JoinOptions,
    PeerIdentity,
    RoomStatus,
    TransportCapabilities,
    TransportId,
    Unsubscribe,
} from './transport';
export { mockTransport } from './mock-transport';
export { trysteroTransport } from './trystero-transport';
export {
    manualTransport,
    startManualHost,
    startManualJoin,
    previewSignalingBlob,
} from './manual-transport';
export type { ManualHostHandle, ManualJoinHandle, SignalingBlobPreview } from './manual-transport';
export type {
    SessionHost,
    SessionMeta,
    SessionRole,
    SessionState,
    PeerView,
    StartOptions,
} from './session';
export { CollabSession } from './session';
export type {
    LiveSessionPanelController,
    LiveSessionPanelHost,
    StartHostArgs,
    StartJoinArgs,
    ManualHostFlow,
    ManualJoinFlow,
    ManualStartArgs,
} from './live-session-panel';
export { mountLiveSessionPanel } from './live-session-panel';
export { cachedDisplayName, setCachedDisplayName, colorForName, makeIdentity } from './identity';
export { bootstrapLiveSession } from './bootstrap';
export type { BootstrapOptions, LiveSessionHandle } from './bootstrap';
