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
        { urls: 'stun:stun.l.google.com:19302' },
        { urls: 'stun:stun.cloudflare.com:3478' },
    ],
};

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
