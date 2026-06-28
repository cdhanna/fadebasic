// Hash module unit tests. WebCrypto is available natively in Node 18+, so
// these run against the real implementation — no mocks.

import { describe, expect, it } from 'vitest';
import { gitBlobSha, sha256, sha256Hex, shardOf, toHex } from './hash';

describe('hash', () => {
    it('matches the well-known SHA-256 vector for "abc"', async () => {
        // Standard test vector from FIPS 180-4 §A.1.
        const input = new TextEncoder().encode('abc');
        const got = await sha256Hex(input);
        expect(got).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
    });

    it('matches the well-known SHA-256 vector for the empty input', async () => {
        const got = await sha256Hex(new Uint8Array());
        expect(got).toBe('e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855');
    });

    it('returns identical hex for identical bytes (determinism)', async () => {
        const a = new TextEncoder().encode('hello playground');
        const b = new TextEncoder().encode('hello playground');
        expect(await sha256Hex(a)).toBe(await sha256Hex(b));
    });

    it('returns different hex for different bytes', async () => {
        const a = new TextEncoder().encode('hello');
        const b = new TextEncoder().encode('hello!');
        expect(await sha256Hex(a)).not.toBe(await sha256Hex(b));
    });

    it('toHex round-trips for known fixed bytes', () => {
        const bytes = new Uint8Array([0x00, 0x0f, 0xab, 0xff]);
        expect(toHex(bytes)).toBe('000fabff');
    });

    it('sha256 returns 32 bytes', async () => {
        const out = await sha256(new TextEncoder().encode('anything'));
        expect(out.length).toBe(32);
    });

    it('shardOf returns the first two hex characters', () => {
        expect(shardOf('ab12cd34')).toBe('ab');
        expect(shardOf('00ff')).toBe('00');
    });

    it('gitBlobSha matches git\'s sha1("blob N\\0content") for empty input', async () => {
        // git hash-object < /dev/null  →  e69de29bb2d1d6434b8b29ae775ad8c2e48c5391
        const got = await gitBlobSha(new Uint8Array());
        expect(got).toBe('e69de29bb2d1d6434b8b29ae775ad8c2e48c5391');
    });

    it('gitBlobSha matches git\'s sha for "hello\\n"', async () => {
        // echo hello | git hash-object --stdin  →  ce013625030ba8dba906f756967f9e9ca394464a
        const got = await gitBlobSha(new TextEncoder().encode('hello\n'));
        expect(got).toBe('ce013625030ba8dba906f756967f9e9ca394464a');
    });
});
