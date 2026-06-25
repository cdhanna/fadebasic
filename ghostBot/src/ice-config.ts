/** ICE config — keep in sync with Playground/src/sharing/collab/ice-probe.ts */
export const GHOSTBOT_ICE_CONFIG: RTCConfiguration = {
    iceServers: [
        { urls: 'stun:stun.l.google.com:19302' },
        { urls: 'stun:stun1.l.google.com:19302' },
        { urls: 'stun:stun2.l.google.com:19302' },
    ],
};

/** Explicit signaling-tracker list — trystero's defaults only use the first
 *  three of its four built-in trackers and the first (webtorrent.dev) is
 *  dead/flaky, so the default rendezvous hangs on one reliable tracker.
 *  Keep in sync with TRACKER_RELAY_URLS in Playground ice-probe.ts — both
 *  sides must overlap on at least one tracker to find each other. */
export const TRACKER_RELAY_URLS = [
    'wss://tracker.openwebtorrent.com',
    'wss://tracker.files.fm:7073/announce',
    'wss://tracker.btorrent.xyz',
    'wss://tracker.webtorrent.dev',
];

/** Signaling relays for pairing with the Playground (trystero/nostr).
 *  GhostBot moved off WebTorrent trackers — too few stay live for reliable
 *  rendezvous, which is what left the app stuck on "Looking for Playground".
 *  Nostr relays are much healthier. BOTH peers must use the SAME list —
 *  keep in sync with GHOST_NOSTR_RELAYS in Playground ice-probe.ts. */
export const GHOST_NOSTR_RELAYS = [
    'wss://relay.snort.social',
    'wss://nostr.mom',
    'wss://nostr.data.haus',
    'wss://nfdb.noswhere.com',
    'wss://relay.fountain.fm',
    'wss://strfry.openhoofd.nl',
    'wss://nostr.vulpem.com',
];

/** BYO TURN escape hatch for strict NATs — same JSON shape as the
 *  Playground's `fade.collab.customIceServers`: a JSON array of
 *  RTCIceServer objects, e.g.
 *  `[{"urls":"turn:turn.example.com:3478","username":"u","credential":"c"}]` */
const STORAGE_KEY_CUSTOM_ICE = 'ghostbot.customIceServers';

export function ghostIceConfig(): RTCConfiguration {
    let custom: RTCIceServer[] = [];
    try {
        const raw = localStorage.getItem(STORAGE_KEY_CUSTOM_ICE);
        const parsed = raw ? JSON.parse(raw) : [];
        if (Array.isArray(parsed)) {
            custom = parsed.filter(
                (s) => s && typeof s === 'object' && 'urls' in s,
            ) as RTCIceServer[];
        }
    } catch { /* bad JSON → ignore */ }
    return {
        iceServers: [...(GHOSTBOT_ICE_CONFIG.iceServers ?? []), ...custom],
    };
}

/** Cap on how long simple-peer waits for ICE to *signal complete* before
 *  sending its SDP. trystero forces trickle:false, so this gates the whole
 *  handshake; simple-peer's default 5000ms is the main reason Chrome↔GhostBot
 *  pairing is slow and random — WKWebView is unreliable about emitting the
 *  end-of-candidates signal and eats the full 5s. 1500ms still captures host
 *  + STUN srflx candidates. Keep in sync with Playground ice-probe.ts. */
export const ICE_COMPLETE_TIMEOUT_MS = 1500;

/** Trystero 0.20 quirk: the `rtcConfig` value is spread directly into
 *  simple-peer's option object (trystero/src/peer.js), and simple-peer reads
 *  the RTCConfiguration from its `config` key. Passing a bare RTCConfiguration
 *  is silently ignored (simple-peer falls back to its built-in STUN defaults).
 *  Nest it under `config` so the servers actually apply; the same spread lets
 *  us pass simple-peer's `iceCompleteTimeout` through unpatched.
 *  Keep in sync with toTrysteroRtcConfig in Playground ice-probe.ts. */
export function toTrysteroRtcConfig(config: RTCConfiguration): RTCConfiguration {
    return { config, iceCompleteTimeout: ICE_COMPLETE_TIMEOUT_MS } as unknown as RTCConfiguration;
}
