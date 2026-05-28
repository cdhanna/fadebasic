// SHA-256 via WebCrypto. Used for both blob names and commit ids. WebCrypto is
// available natively in the browser and in Node 18+, so this module needs no
// polyfill or library dependency.
//
// All hashes are lowercase hex. The first two characters are also used as the
// shard prefix for blobs on the remote (`objects/<ab>/<hash>`) — see sharing.md.

const HEX = '0123456789abcdef';

export async function sha256(bytes: Uint8Array): Promise<Uint8Array> {
    // Defensive copy. Two reasons: (1) callers can mutate the input buffer
    // after the call without affecting the digest, and (2) the project's
    // lib.d.ts includes SharedArrayBuffer (for the vm-worker's prompt$
    // plumbing — see main.ts:906), which makes raw Uint8Array<ArrayBufferLike>
    // fail crypto.subtle.digest's BufferSource constraint. Allocating a fresh
    // Uint8Array guarantees a plain ArrayBuffer backing.
    const copy = new Uint8Array(bytes);
    const buf = await crypto.subtle.digest('SHA-256', copy.buffer as ArrayBuffer);
    return new Uint8Array(buf);
}

export function toHex(bytes: Uint8Array): string {
    let out = '';
    for (let i = 0; i < bytes.length; i++) {
        const b = bytes[i];
        out += HEX[(b >> 4) & 0xf] + HEX[b & 0xf];
    }
    return out;
}

export async function sha256Hex(bytes: Uint8Array): Promise<string> {
    return toHex(await sha256(bytes));
}

// Shard prefix: first 2 hex chars. Used for blob folder bucketing on the remote.
export function shardOf(hash: string): string {
    return hash.slice(0, 2);
}

/**
 * Compute the git blob SHA-1 for a byte string. This is the same id git
 * itself uses: `sha1("blob " + bytes.length + "\0" + bytes)`. Lets us
 * compare local file bytes against git's blob sha without uploading to find
 * out — used by file-status (A/M/D) and by conflict detection.
 *
 * SHA-1 is broken for adversarial collision resistance but fine for our
 * use case: we're matching content, not authenticating it. Git itself
 * still uses SHA-1 for object addressing.
 */
export async function gitBlobSha(bytes: Uint8Array): Promise<string> {
    const header = new TextEncoder().encode(`blob ${bytes.length}\0`);
    const combined = new Uint8Array(header.length + bytes.length);
    combined.set(header, 0);
    combined.set(bytes, header.length);
    // Defensive copy via .buffer cast — same lib.d.ts quirk hash.ts already
    // documents (SharedArrayBuffer leak in the Uint8Array generic).
    const digest = await crypto.subtle.digest('SHA-1', combined.buffer as ArrayBuffer);
    return toHex(new Uint8Array(digest));
}
