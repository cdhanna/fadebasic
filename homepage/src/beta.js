// Beta gating helper.
//
// `?beta` in the URL unlocks the pre-release docs (Commands + Game Tutorial).
// We keep the flag in the QUERY STRING (location.search), which lives before the
// `#` — so the browser preserves it across every hash-only route change the app
// makes. That means beta "sticks" while you browse, yet editing `?beta` out of
// the URL turns it off immediately (no hidden/sticky storage).
//
// The catch: it's natural to append the flag to the hash instead
// (`#/learn/commands?beta`), which the query string wouldn't see — and which
// would also corrupt the route parser. So normalizeBeta() hoists a hash-borne
// flag into the query string (and strips it from the hash) exactly once. Call it
// at boot and on every hashchange; it's idempotent and returns the live state.

export function normalizeBeta() {
  if (/[?&]beta\b/.test(location.search)) return true;   // already canonical
  const hash = location.hash;
  const q = hash.indexOf('?');
  if (q !== -1 && /[?&]beta\b/.test(hash.slice(q))) {
    const search = (location.search ? location.search + '&' : '?') + 'beta';
    // Drop the whole hash query (this router carries no other hash params).
    history.replaceState(null, '', location.pathname + search + hash.slice(0, q));
    return true;
  }
  return false;
}

// Read-only check (assumes normalizeBeta already ran this session).
export const isBeta = () => /[?&]beta\b/.test(location.search);
