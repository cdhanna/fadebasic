// GitHub token persistence — full TokenSet on sessionStorage.
//
// Why not localStorage anymore: the previous design stored a long-lived
// PAT in localStorage. Reading that's still in JS scope, but at least
// sessionStorage limits the blast radius to one browser tab and clears
// on close. Combined with the device flow producing short-lived
// (~8h default) refreshable tokens, the practical risk shrinks
// substantially compared to "an immortal PAT in localStorage."
//
// Persistence layout (one JSON blob, single key):
//   {
//     accessToken: 'ghu_…',
//     refreshToken: 'ghr_…',
//     accessExpiresAt: 1730000000000,
//     refreshExpiresAt: 1745000000000,
//     scope: '',
//     tokenType: 'bearer'
//   }
//
// Legacy migration: an old `fade-playground:github-token` key in
// localStorage (raw PAT string) is read once on first `load()`, copied
// into the new shape with no refresh fields, and the old key removed.
// The migrated PAT keeps working until the user signs in fresh; at
// that point it's overwritten by the device-flow result.

import type { TokenSet } from './github-auth';

export interface StoredTokenSet {
    accessToken: string;
    refreshToken?: string;
    /** ms since epoch. Absent when the upstream response didn't include
     *  `expires_in` (legacy PATs, OAuth-App long-lived tokens). */
    accessExpiresAt?: number;
    refreshExpiresAt?: number;
    scope?: string;
    tokenType?: string;
}

export interface TokenStore {
    load(): StoredTokenSet | null;
    save(set: StoredTokenSet): void;
    clear(): void;
}

const NEW_KEY = 'fade-playground:github-token-set:v1';
const LEGACY_PAT_KEY = 'fade-playground:github-token';

/** sessionStorage-backed store. Per-tab isolation, clears on tab close. */
export class SessionTokenStore implements TokenStore {
    constructor(private readonly key = NEW_KEY) {}

    load(): StoredTokenSet | null {
        // One-time migration: if the legacy PAT key is present (from
        // pre-device-flow versions), wrap it into the new shape and
        // promote it. Reading & writing on every load would be
        // wasteful, so we only do it when the new key is absent.
        try {
            const fresh = sessionStorage.getItem(this.key);
            if (fresh) return JSON.parse(fresh) as StoredTokenSet;
        } catch { /* parse error → fall through to migration */ }

        try {
            if (typeof localStorage === 'undefined') return null;
            const legacy = localStorage.getItem(LEGACY_PAT_KEY);
            if (!legacy) return null;
            const migrated: StoredTokenSet = { accessToken: legacy };
            try {
                sessionStorage.setItem(this.key, JSON.stringify(migrated));
            } catch { /* sessionStorage unavailable — caller can re-auth */ }
            try { localStorage.removeItem(LEGACY_PAT_KEY); } catch { /* ignore */ }
            return migrated;
        } catch {
            return null;
        }
    }

    save(set: StoredTokenSet): void {
        try { sessionStorage.setItem(this.key, JSON.stringify(set)); }
        catch { /* private mode / quota — caller proceeds in-memory only */ }
    }

    clear(): void {
        try { sessionStorage.removeItem(this.key); } catch { /* ignore */ }
        // Belt and suspenders: nuke the legacy key too in case migration
        // partially completed previously.
        try { localStorage.removeItem(LEGACY_PAT_KEY); } catch { /* ignore */ }
    }
}

/** Test / fallback shim. Lives entirely in memory. */
export class MemoryTokenStore implements TokenStore {
    private set: StoredTokenSet | null = null;
    load(): StoredTokenSet | null { return this.set ? { ...this.set } : null; }
    save(set: StoredTokenSet): void { this.set = { ...set }; }
    clear(): void { this.set = null; }
}

// ─── helpers ────────────────────────────────────────────────────────────────

/** Convert the device-flow response (TokenSet, relative expiries) into
 *  the persisted shape (absolute timestamps). `now` is injectable for
 *  tests. */
export function tokenSetToStored(t: TokenSet, now: number = Date.now()): StoredTokenSet {
    const stored: StoredTokenSet = {
        accessToken: t.accessToken,
        refreshToken: t.refreshToken,
        scope: t.scope,
        tokenType: t.tokenType,
    };
    if (typeof t.expiresIn === 'number') {
        stored.accessExpiresAt = now + t.expiresIn * 1000;
    }
    if (typeof t.refreshTokenExpiresIn === 'number') {
        stored.refreshExpiresAt = now + t.refreshTokenExpiresIn * 1000;
    }
    return stored;
}

/** True iff the stored access token will expire within `cushionMs` of
 *  `now`. Returns false when expiry is unknown (legacy PATs etc.) — we
 *  assume those don't expire from our side. */
export function isAccessExpired(
    stored: StoredTokenSet,
    now: number = Date.now(),
    cushionMs = 60_000,
): boolean {
    if (stored.accessExpiresAt === undefined) return false;
    return stored.accessExpiresAt - now <= cushionMs;
}

/** True iff the refresh token is unusable (missing or expired). When
 *  this is true and `isAccessExpired` is also true, the user has to
 *  re-authenticate from scratch. */
export function isRefreshUsable(stored: StoredTokenSet, now: number = Date.now()): boolean {
    if (!stored.refreshToken) return false;
    if (stored.refreshExpiresAt !== undefined && stored.refreshExpiresAt <= now) return false;
    return true;
}
