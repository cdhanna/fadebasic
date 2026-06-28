// Picks an ICE configuration that this browser + network can actually
// gather candidates with. Some Firefox-on-macOS-with-VPN setups have a
// confused routing table that makes Firefox bail out of ICE gathering
// entirely when a complex iceServers list is provided — so we fall back
// to a minimal "just Google STUN" config in that case. STUN-only connects
// for ~80-85% of users; that's better than nothing-connects.
//
// The probe runs once at app startup (kicked from bootstrap.ts) and the
// result is cached for the rest of the page lifetime. Subsequent
// transport.join() calls use the cached config without re-probing.

/** The single ICE config the live-session feature uses.
 *
 *  We deliberately ship STUN-only — TWO independent STUN endpoints (Google
 *  + Cloudflare, both rock-solid public services) and NO TURN servers.
 *
 *  Why no TURN: the public Open Relay TURN endpoints from metered.ca that
 *  every WebRTC tutorial points at are unreliable in practice — their
 *  credentials get rate-limited, the hostnames change, and including them
 *  in the iceServers list has been observed to make Firefox's gather path
 *  bail out entirely on macOS-with-VPN setups (the gather logic tries to
 *  detect a default outbound address by connecting to one of the listed
 *  servers, and if THAT connect fails the whole gathering aborts before
 *  any candidate is emitted). Removing TURN from the default config
 *  makes the success rate much more consistent at the cost of dropping
 *  the 10-15% of peers behind symmetric NATs / strict firewalls who'd
 *  need a relay to connect.
 *
 *  Users who need TURN for restrictive networks can paste their own
 *  credentials (Cloudflare Realtime TURN free tier, Twilio, self-hosted
 *  coturn, etc.) into the future "custom ICE servers" settings field —
 *  see `customIceServers` consumption below.
 *
 *  Why two STUN endpoints: redundancy. If Google's STUN responds first
 *  (the common case) we get srflx fast; if it's blocked or slow,
 *  Cloudflare picks up the slack. Both are operated as best-effort free
 *  public services with multi-decade track records. */
const BASE_ICE_CONFIG: RTCConfiguration = {
    iceServers: [
        // Google's STUN — multiple endpoints for DNS-level redundancy.
        // Cloudflare's `stun.cloudflare.com:3478` was here previously
        // but Cloudflare doesn't actually run a free public STUN there
        // (their TURN/STUN is paid + auth-gated), and Firefox has been
        // observed to bail the entire ICE gather when any listed
        // iceServer is unreachable.
        //
        // ONE URL PER ENTRY — not the {urls: [array]} form. Older
        // Firefox (≤ 99 and possibly later) silently fails to parse
        // the array-of-URLs shape and leaves gather wedged at
        // `iceGatheringState === 'new'` indefinitely, with no candidate
        // events ever firing. The single-string form is bullet-proof
        // across all browsers. Confirmed via in-console sanity test
        // on a Firefox where the array form produced zero candidates
        // but a single-URL entry produced host + srflx in <1s.
        { urls: 'stun:stun.l.google.com:19302' },
        { urls: 'stun:stun1.l.google.com:19302' },
        { urls: 'stun:stun2.l.google.com:19302' },
    ],
};

/** Explicit signaling-tracker list for every trystero room we open (live
 *  collab + GhostBot pairing). Two reasons not to rely on the defaults:
 *  1. trystero only connects to the FIRST THREE of its four built-in
 *     trackers (relayRedundancy = 3) — and the first one,
 *     tracker.webtorrent.dev, has been dead/flaky for a while, so the
 *     default rendezvous effectively hangs on openwebtorrent + a flaky
 *     btorrent.xyz. files.fm (often the most stable) never got used.
 *  2. Tracker health is volatile; an explicit list uses ALL of them.
 *  Keep this overlapping with trystero's defaults so peers running older
 *  builds still meet us on at least one common tracker.
 *  Keep in sync with TRACKER_RELAY_URLS in ghostBot/src/ice-config.ts. */
export const TRACKER_RELAY_URLS = [
    'wss://tracker.openwebtorrent.com',
    'wss://tracker.files.fm:7073/announce',
    'wss://tracker.btorrent.xyz',
    'wss://tracker.webtorrent.dev',
];

/** Signaling relays for the GhostBot pairing room (trystero/nostr strategy).
 *  We moved GhostBot off the WebTorrent trackers because that ecosystem has
 *  decayed to ~2 reliably-live trackers — if a peer's open trackers don't
 *  overlap the other's, discovery silently never happens (the "stuck on
 *  Looking for Playground" symptom). The Nostr relay network is far healthier
 *  (10+/12 sampled live). This list is the intersection of trystero's curated
 *  nostr defaults (which are known to accept the ephemeral signaling events)
 *  and relays verified live. BOTH peers must use the SAME list — keep in sync
 *  with GHOST_NOSTR_RELAYS in ghostBot/src/ice-config.ts.
 *  Live collab stays on trystero/torrent (it pairs Chrome↔Chrome instantly). */
export const GHOST_NOSTR_RELAYS = [
    'wss://relay.snort.social',
    'wss://nostr.mom',
    'wss://nostr.data.haus',
    'wss://nfdb.noswhere.com',
    'wss://relay.fountain.fm',
    'wss://strfry.openhoofd.nl',
    'wss://nostr.vulpem.com',
];

/** How long simple-peer waits for ICE gathering to *signal complete* before
 *  sending the offer/answer anyway. trystero forces `trickle: false`, so the
 *  SDP can't go out until this fires — and simple-peer's default is a brutal
 *  5000ms (@thaunknown/simple-peer ICECOMPLETE_TIMEOUT). Chrome emits the
 *  end-of-candidates signal in a few hundred ms so it never waits; WKWebView
 *  (GhostBot's Tauri webview) is unreliable about emitting it and routinely
 *  eats the full 5s — that's the "slow and random" Chrome↔GhostBot pairing.
 *  Capping at 1500ms still captures host (instant) + STUN srflx (~100-300ms
 *  from Google) candidates, so remote connectivity is preserved while
 *  same-machine pairing drops from ~5s to ~1s. Lower it further if you only
 *  ever pair on the same machine/LAN (host candidates are immediate). */
export const ICE_COMPLETE_TIMEOUT_MS = 1500;

/** Trystero 0.20 quirk: the `rtcConfig` value handed to joinRoom is spread
 *  directly into simple-peer's option object (trystero/src/peer.js), and
 *  simple-peer reads the RTCConfiguration from its `config` key. Passing a
 *  bare RTCConfiguration is silently ignored — simple-peer falls back to its
 *  built-in defaults (Google + Twilio STUN), which is why custom ICE servers
 *  (including the localStorage TURN escape hatch) never took effect. Nest the
 *  configuration under `config` so it actually applies. The same spread lets
 *  us pass simple-peer's own `iceCompleteTimeout` option through unpatched.
 *  Keep in sync with toTrysteroRtcConfig in ghostBot/src/ice-config.ts. */
export function toTrysteroRtcConfig(config: RTCConfiguration): RTCConfiguration {
    return { config, iceCompleteTimeout: ICE_COMPLETE_TIMEOUT_MS } as unknown as RTCConfiguration;
}

export type IceMode = 'stun-only' | 'custom';

export interface IceSelection {
    config: RTCConfiguration;
    mode: IceMode;
    /** Human-readable note about the current setup. Null when the
     *  default STUN-only config is in use (no message needed for the
     *  expected path). Non-null when the user added custom servers so
     *  the UI can label the mode. */
    note: string | null;
}

const STORAGE_KEY_CUSTOM_ICE = 'fade.collab.customIceServers';

/** Read user-supplied extra ICE servers from localStorage. Persisted as
 *  a JSON array of RTCIceServer objects so a future settings UI can
 *  drop "bring your own TURN" credentials in here without changing
 *  this file. Bad JSON falls back to an empty list rather than throwing. */
function loadCustomIceServers(): RTCIceServer[] {
    try {
        const raw = localStorage.getItem(STORAGE_KEY_CUSTOM_ICE);
        if (!raw) return [];
        const parsed = JSON.parse(raw);
        if (!Array.isArray(parsed)) return [];
        return parsed.filter((s) => s && typeof s === 'object' && 'urls' in s) as RTCIceServer[];
    } catch { return []; }
}

let cached: IceSelection | null = null;

/** Returns the ICE config to hand to Trystero. Synchronous now — the
 *  earlier dynamic-probe approach (trying full then minimal then
 *  host-only) was a source of nondeterminism: Firefox's gather path is
 *  timing-sensitive enough that two consecutive runs could pick
 *  different configs, leading to "it worked once but now it doesn't"
 *  reports. Predictable beats clever — every session uses the same
 *  config, and connectivity problems surface via the 30s session
 *  watchdog rather than a flaky pre-flight check.
 *
 *  Kept as a Promise return type because TrysteroTransport.join is
 *  already async and the call sites are wired up that way. The work
 *  inside is synchronous. */
export function selectWorkingIceConfig(): Promise<IceSelection> {
    if (cached) return Promise.resolve(cached);
    const custom = loadCustomIceServers();
    const baseServers = (BASE_ICE_CONFIG.iceServers ?? []) as RTCIceServer[];
    const merged: RTCConfiguration = {
        iceServers: [...baseServers, ...custom],
    };
    const note = custom.length > 0
        ? `Using ${custom.length} custom ICE server(s) alongside STUN. Remove via localStorage key "${STORAGE_KEY_CUSTOM_ICE}".`
        : null;
    cached = {
        config: merged,
        mode: custom.length > 0 ? 'custom' : 'stun-only',
        note,
    };
    console.info(`[fade-collab] ICE config: ${cached.mode} (${baseServers.length} default + ${custom.length} custom server(s))`);
    return Promise.resolve(cached);
}

/** Synchronously return the current ICE selection. */
export function getCachedIceSelection(): IceSelection | null {
    return cached;
}

/** Reset the in-memory cache so the next `selectWorkingIceConfig` call
 *  re-reads localStorage. Used by a future settings UI to apply changes
 *  without a reload. */
export function resetIceConfigCache(): void {
    cached = null;
}
