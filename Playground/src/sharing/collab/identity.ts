// Per-user identity for the live-session UI. Display name is collected once
// (cached in localStorage) and re-used for every subsequent session. Color
// is derived deterministically from the name so a peer's cursor stays the
// same hue across sessions.

import type { PeerIdentity } from './transport';

const STORAGE_KEY_NAME = 'fade.collab.displayName';
const STORAGE_KEY_HOST_PASSWORD = 'fade.collab.hostPassword';

/** FNV-1a 32-bit hash. Cheap and deterministic — good enough for a color
 *  bucket. We only need stability, not cryptographic spread. */
function hash32(s: string): number {
    let h = 0x811c9dc5;
    for (let i = 0; i < s.length; i++) {
        h ^= s.charCodeAt(i);
        h = Math.imul(h, 0x01000193);
    }
    return h >>> 0;
}

/** Map a hash to an HSL hue, dodging the muddy yellow-green band. */
export function colorForName(name: string): string {
    const h = hash32(name);
    // 12 hue buckets, skipping 50-90° (washed-out yellow-green that reads
    // poorly on most editor backgrounds).
    const buckets = [0, 20, 40, 110, 140, 180, 210, 240, 270, 300, 330];
    const hue = buckets[h % buckets.length];
    return `hsl(${hue}, 70%, 55%)`;
}

export function cachedDisplayName(): string | null {
    try { return localStorage.getItem(STORAGE_KEY_NAME); } catch { return null; }
}

export function setCachedDisplayName(name: string): void {
    try { localStorage.setItem(STORAGE_KEY_NAME, name); } catch { /* ignore */ }
}

/** Last password the user picked when hosting. Pre-fills the host modal
 *  on next session so they don't have to retype it. Joins are NOT cached
 *  — each room they connect to has its own password, so suggesting the
 *  last one would just be confusing. Stored as plaintext in localStorage,
 *  which is fine for this threat model: it's an in-tab E2E key for a
 *  collab session, not a real secret, and anyone with DevTools on the
 *  host's machine already has the whole document. */
export function cachedHostPassword(): string | null {
    try { return localStorage.getItem(STORAGE_KEY_HOST_PASSWORD); } catch { return null; }
}

export function setCachedHostPassword(password: string): void {
    try {
        if (password) localStorage.setItem(STORAGE_KEY_HOST_PASSWORD, password);
        else localStorage.removeItem(STORAGE_KEY_HOST_PASSWORD);
    } catch { /* ignore */ }
}

export function makeIdentity(displayName: string, opts?: { githubLogin?: string }): PeerIdentity {
    const name = displayName.trim() || 'Anonymous';
    return {
        displayName: name,
        color: colorForName(name),
        githubLogin: opts?.githubLogin,
    };
}
