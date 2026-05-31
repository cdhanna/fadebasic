// End-to-end-ish test for the collab session. Two CollabSessions are wired
// through two MockTransport rooms on the same BroadcastChannel (jsdom
// gives us a working BroadcastChannel impl), and we verify Yjs state
// propagates from host to guest and back.
//
// We bypass the MonacoBinding side entirely — getModelForFile returns null
// in the test sessionHost so the session never tries to bind to a real
// Monaco model. That keeps the test focused on the wire protocol + Y.Doc
// orchestration without dragging in the heavy editor dependencies.

import { describe, expect, it, beforeAll } from 'vitest';
import * as Y from 'yjs';

import { CollabSession, type SessionHost } from './session';
import { mockTransport } from './mock-transport';
import { makeIdentity } from './identity';

// Polyfill BroadcastChannel only when the host env lacks one. Node 18+
// ships a native implementation backed by worker_threads MessageChannel;
// the polyfill stays as a backstop for environments that don't (older
// Node, some jsdom versions). Note: under heavy vitest load the native
// implementation can occasionally drop or delay messages — the RPC test
// has its own 15s timeout + an awareness-converged precondition to
// tolerate that without false failures.
beforeAll(() => {
    if (typeof globalThis.BroadcastChannel !== 'undefined') return;
    type Listener = (ev: { data: unknown }) => void;
    const channels = new Map<string, Set<{ post: Listener; close: () => void }>>();
    class BC {
        name: string;
        onmessage: Listener | null = null;
        private subs: Set<{ post: Listener; close: () => void }>;
        private self: { post: Listener; close: () => void };
        constructor(name: string) {
            this.name = name;
            if (!channels.has(name)) channels.set(name, new Set());
            this.subs = channels.get(name)!;
            this.self = {
                post: (ev) => { if (this.onmessage) this.onmessage(ev); },
                close: () => { this.subs.delete(this.self); },
            };
            this.subs.add(this.self);
        }
        postMessage(data: unknown) {
            for (const s of this.subs) if (s !== this.self) s.post({ data });
        }
        close() { this.self.close(); }
    }
    (globalThis as any).BroadcastChannel = BC;
});

// Backing store for a fake workspace + the SessionHost stub that reads it.
// Tests can pre-populate `files` (text) and `bytes` (binary) before calling
// host.start() so the host's seed pass picks them up via the workspace API.
interface FakeBacking {
    files: Map<string, string>;
    bytes: Map<string, Uint8Array>;
    refreshes: number;
    tabOpens: string[];
}

function makeFakeBacking(initial?: { files?: Record<string, string>; bytes?: Record<string, Uint8Array> }): FakeBacking {
    return {
        files: new Map(Object.entries(initial?.files ?? {})),
        bytes: new Map(Object.entries(initial?.bytes ?? {})),
        refreshes: 0,
        tabOpens: [],
    };
}

function makeFakeHost(backing: FakeBacking): SessionHost {
    const listeners = new Set<(name: string | null) => void>();
    let active: string | null = null;
    const BINARY_EXTS = new Set(['png', 'jpg', 'wav', 'mp3', 'xnb']);
    return {
        get editor() { return null as any; },
        getActiveFileName: () => active,
        onActiveFileChange: (cb) => { listeners.add(cb); return () => listeners.delete(cb); },
        getModelForFile: () => null,
        openFile: async (name) => { backing.tabOpens.push(name); if (!active) active = name; },
        closeFile: async (name) => { if (active === name) active = null; },
        listWorkspaceFiles: async () => [
            ...backing.files.keys(),
            ...backing.bytes.keys(),
        ],
        isBinaryPath: (path) => {
            const dot = path.lastIndexOf('.');
            if (dot < 0) return false;
            return BINARY_EXTS.has(path.slice(dot + 1).toLowerCase());
        },
        readWorkspaceText: async (path) => {
            const v = backing.files.get(path);
            if (v === undefined) throw new Error(`no text file ${path}`);
            return v;
        },
        readWorkspaceBytes: async (path) => {
            const v = backing.bytes.get(path);
            if (v === undefined) throw new Error(`no binary file ${path}`);
            return v;
        },
        writeWorkspaceText: async (path, content) => { backing.files.set(path, content); },
        writeWorkspaceBytes: async (path, bytes) => { backing.bytes.set(path, bytes); },
        deleteWorkspaceFile: async (path) => {
            backing.files.delete(path);
            backing.bytes.delete(path);
        },
        refreshFileList: async () => { backing.refreshes++; },
    };
}

async function waitFor(check: () => boolean, ms = 1000): Promise<void> {
    const start = Date.now();
    while (Date.now() - start < ms) {
        if (check()) return;
        await new Promise((r) => setTimeout(r, 20));
    }
    throw new Error(`waitFor timed out after ${ms}ms`);
}

describe('CollabSession + mock transport', () => {
    it('host seeds files into Y.Doc from workspace and guest receives via sync', async () => {
        const roomId = 'test-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test',
            roomId,
            identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test',
            roomId,
            identity: makeIdentity('Bob'),
        });

        const hostBacking = makeFakeBacking({
            files: { 'main.fbasic': 'PRINT "hi"', 'level.fbasic': 'LEVEL_DATA = 42' },
        });
        const guestBacking = makeFakeBacking();

        const host = new CollabSession(makeFakeHost(hostBacking), hostRoom);
        const guest = new CollabSession(makeFakeHost(guestBacking), guestRoom);

        await host.start({ role: 'host', identity: makeIdentity('Alice'), projectName: 'demo' });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });

        // Guest should receive the host's files via sync handshake.
        await waitFor(() => guest.doc.getMap('files').size === 2);

        const guestFiles = guest.doc.getMap<Y.Text>('files');
        expect(guestFiles.get('main.fbasic')?.toString()).toBe('PRINT "hi"');
        expect(guestFiles.get('level.fbasic')?.toString()).toBe('LEVEL_DATA = 42');

        // Verify meta replicated.
        await waitFor(() => guest.getState().meta?.projectName === 'demo');
        expect(guest.getState().meta?.hostName).toBe('Alice');

        // Guest should have mirrored files into its (fake) workspace.
        await waitFor(() => guestBacking.files.size === 2);
        expect(guestBacking.files.get('main.fbasic')).toBe('PRINT "hi"');
        expect(guestBacking.files.get('level.fbasic')).toBe('LEVEL_DATA = 42');
        expect(guestBacking.refreshes).toBeGreaterThan(0);

        await host.destroy();
        await guest.destroy();
    });

    it('host seeds binary assets and guest mirrors them as bytes', async () => {
        const roomId = 'test-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });

        const png = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        const hostBacking = makeFakeBacking({
            files: { 'main.fbasic': 'PRINT 1' },
            bytes: { 'art/lee.png': png },
        });
        const guestBacking = makeFakeBacking();

        const host = new CollabSession(makeFakeHost(hostBacking), hostRoom);
        const guest = new CollabSession(makeFakeHost(guestBacking), guestRoom);

        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });

        await waitFor(() => guestBacking.bytes.size === 1, 2000);
        const mirrored = guestBacking.bytes.get('art/lee.png');
        expect(mirrored).toBeDefined();
        expect(Array.from(mirrored!)).toEqual(Array.from(png));

        await host.destroy();
        await guest.destroy();
    });

    it('edits propagate bidirectionally', async () => {
        const roomId = 'test-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });

        const hostBacking = makeFakeBacking({ files: { 'main.fbasic': 'start' } });
        const guestBacking = makeFakeBacking();
        const host = new CollabSession(makeFakeHost(hostBacking), hostRoom);
        const guest = new CollabSession(makeFakeHost(guestBacking), guestRoom);

        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });

        await waitFor(() => guest.doc.getMap('files').has('main.fbasic'));

        // Host edit propagates to guest.
        host.doc.getMap<Y.Text>('files').get('main.fbasic')!.insert(0, 'A:');
        await waitFor(() =>
            guest.doc.getMap<Y.Text>('files').get('main.fbasic')?.toString() === 'A:start',
        );

        // Guest edit propagates to host.
        guest.doc.getMap<Y.Text>('files').get('main.fbasic')!.insert(0, 'B:');
        await waitFor(() =>
            host.doc.getMap<Y.Text>('files').get('main.fbasic')?.toString() === 'B:A:start',
        );

        await host.destroy();
        await guest.destroy();
    });

    it('awareness propagates display names and roles', async () => {
        const roomId = 'test-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });

        const host = new CollabSession(makeFakeHost(makeFakeBacking()), hostRoom);
        const guest = new CollabSession(makeFakeHost(makeFakeBacking()), guestRoom);

        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });

        // Eventually each side should see both peers in its state.
        await waitFor(() => host.getState().peers.length === 2);
        await waitFor(() => guest.getState().peers.length === 2);

        const hostPeers = host.getState().peers;
        const guestPeers = guest.getState().peers;
        expect(hostPeers.map((p) => p.identity.displayName).sort()).toEqual(['Alice', 'Bob']);
        expect(guestPeers.map((p) => p.identity.displayName).sort()).toEqual(['Alice', 'Bob']);
        // Host sees itself as host, guest sees itself as guest.
        expect(hostPeers.find((p) => p.isSelf)?.role).toBe('host');
        expect(guestPeers.find((p) => p.isSelf)?.role).toBe('guest');

        await host.destroy();
        await guest.destroy();
    });

    it('forceSync overwrites guest files with current host workspace and deletes orphans', async () => {
        const roomId = 'test-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });

        // Host starts with 3 files; guest has none.
        const hostBacking = makeFakeBacking({
            files: { 'main.fbasic': 'v1', 'a.fbasic': 'v1a', 'doomed.fbasic': 'gone soon' },
        });
        const guestBacking = makeFakeBacking();
        const host = new CollabSession(makeFakeHost(hostBacking), hostRoom);
        const guest = new CollabSession(makeFakeHost(guestBacking), guestRoom);

        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });
        await waitFor(() => guestBacking.files.size === 3);

        // Host changes the workspace out-of-band: edits main, deletes
        // doomed, adds new.
        hostBacking.files.set('main.fbasic', 'v2');
        hostBacking.files.delete('doomed.fbasic');
        hostBacking.files.set('newfile.fbasic', 'fresh');

        await host.forceSync();
        // Sync runs as an async loop with per-step Y.Doc updates; give
        // the guest a tick to receive them all.
        await waitFor(() => guestBacking.files.size === 3 &&
            guestBacking.files.get('main.fbasic') === 'v2' &&
            !guestBacking.files.has('doomed.fbasic') &&
            guestBacking.files.get('newfile.fbasic') === 'fresh',
        2000);

        await host.destroy();
        await guest.destroy();
    });

    it('forceSync sets meta.sync progress while running and clears it on completion', async () => {
        const roomId = 'test-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });

        const hostBacking = makeFakeBacking({
            files: { 'a.fbasic': '1', 'b.fbasic': '2', 'c.fbasic': '3' },
        });
        const host = new CollabSession(makeFakeHost(hostBacking), hostRoom);
        const guest = new CollabSession(makeFakeHost(makeFakeBacking()), guestRoom);

        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });
        await waitFor(() => guest.doc.getMap('files').size === 3);

        // Capture the host's own state changes — the host's meta observer
        // fires synchronously inside each `this.meta.set` call inside
        // forceSync, so we're guaranteed to see the progress values
        // regardless of cross-peer propagation timing.
        const hostSnaps: Array<{ total: number; completed: number } | null> = [];
        host.onStateChange((st) => {
            hostSnaps.push(st.sync
                ? { total: st.sync.total, completed: st.sync.completed }
                : null);
        });

        await host.forceSync();

        const sawProgress = hostSnaps.some((s) => s != null && s.total > 0);
        const sawCleared = hostSnaps.some((s) => s == null);
        expect(sawProgress).toBe(true);
        expect(sawCleared).toBe(true);

        expect(host.getState().sync).toBeNull();
        await waitFor(() => guest.getState().sync == null, 2000);
        expect(guest.getState().sync).toBeNull();

        await host.destroy();
        await guest.destroy();
    });

    it('guest cannot trigger a force sync', async () => {
        const roomId = 'test-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });
        const host = new CollabSession(makeFakeHost(makeFakeBacking()), hostRoom);
        const guest = new CollabSession(makeFakeHost(makeFakeBacking()), guestRoom);
        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });
        await expect(guest.forceSync()).rejects.toThrow(/only the host/i);
        await host.destroy();
        await guest.destroy();
    });

    it('host-driven read-only flag propagates to guest meta', async () => {
        const roomId = 'test-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });

        const host = new CollabSession(makeFakeHost(makeFakeBacking()), hostRoom);
        const guest = new CollabSession(makeFakeHost(makeFakeBacking()), guestRoom);

        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });

        await waitFor(() => guest.getState().meta?.readOnly === false);

        host.setReadOnly(true);
        await waitFor(() => guest.getState().meta?.readOnly === true);
        expect(host.isReadOnly()).toBe(true);

        // Guest cannot flip it back.
        guest.setReadOnly(false);
        // After a beat, host still reads as read-only.
        await new Promise((r) => setTimeout(r, 50));
        expect(host.isReadOnly()).toBe(true);

        await host.destroy();
        await guest.destroy();
    });

    it('game frames broadcast from one peer to all listeners', async () => {
        const roomId = 'test-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });
        const host = new CollabSession(makeFakeHost(makeFakeBacking()), hostRoom);
        const guest = new CollabSession(makeFakeHost(makeFakeBacking()), guestRoom);
        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });

        // Subscribe on the guest, then send a frame from the host.
        const received: Array<{ peerId: string; bytes: Uint8Array }> = [];
        const off = guest.onGameFrame((peerId, bytes) => { received.push({ peerId, bytes }); });

        const frame = new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0xde, 0xad, 0xbe, 0xef]); // JPEG header
        host.sendGameFrame(frame);

        await waitFor(() => received.length > 0, 2000);
        expect(received.length).toBe(1);
        expect(Array.from(received[0].bytes)).toEqual(Array.from(frame));
        expect(received[0].peerId).toBe(hostRoom.selfId);

        off();
        await host.destroy();
        await guest.destroy();
    });

    // 15s vitest timeout (vs default 5s). The internal RPC timeout is
    // 10s; we need vitest to outlast it so an unexpected reject lands
    // as an assertion failure rather than vitest's "test timed out".
    it('request/response RPC: payload round-trips and error responses reject', { timeout: 15_000 }, async () => {
        const roomId = 'test-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });
        const host = new CollabSession(makeFakeHost(makeFakeBacking()), hostRoom);
        const guest = new CollabSession(makeFakeHost(makeFakeBacking()), guestRoom);
        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });

        // Host registers handlers BEFORE we wait for full handshake —
        // the request below assumes the handler is up before the message
        // arrives, and registration is synchronous so it lands first.
        host.onRequest('ping', (_peerId, payload) => {
            const v = (payload as { value: number }).value;
            return { value: v + 1 };
        });
        host.onRequest('boom', () => { throw new Error('intentional'); });

        // Wait for awareness to converge so both peers have actually seen
        // each other. Node's native BroadcastChannel delivers messages
        // asynchronously through worker_threads.MessageChannel; without
        // this wait, `guest.request(...)` can race the awareness/sync
        // handshake and the RPC message can arrive at the host *before*
        // its onMessage subscriber has finished setup, leading to a
        // 10-second timeout. Awareness propagation is the latest event
        // the start sequence emits, so seeing both peers in each side's
        // peer list is a reliable "both sides are fully ready" signal.
        await waitFor(() => host.getState().peers.length === 2 && guest.getState().peers.length === 2, 4000);

        const ok = await guest.request(hostRoom.selfId, 'ping', { value: 41 });
        expect(ok).toEqual({ value: 42 });

        await expect(guest.request(hostRoom.selfId, 'boom', null))
            .rejects.toThrow(/intentional/);

        // Unknown channel → "no handler registered" error.
        await expect(guest.request(hostRoom.selfId, 'no-such-channel', null))
            .rejects.toThrow(/no handler registered/);

        await host.destroy();
        await guest.destroy();
    });

    it('debugState replicates host writes to guest observers', async () => {
        const roomId = 'test-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });
        const host = new CollabSession(makeFakeHost(makeFakeBacking()), hostRoom);
        const guest = new CollabSession(makeFakeHost(makeFakeBacking()), guestRoom);
        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });

        // Host writes a typical "paused on breakpoint" snapshot.
        host.setDebugState({
            initiatorClientId: host.doc.clientID,
            paused: true,
            currentFile: 'main.fbasic',
            currentLine: 42,
            callStack: [{ name: 'MAIN', file: 'main.fbasic', line: 42 }],
        });

        await waitFor(() => guest.debugState.get('paused') === true);
        expect(guest.debugState.get('currentFile')).toBe('main.fbasic');
        expect(guest.debugState.get('currentLine')).toBe(42);
        expect(guest.debugState.get('initiatorClientId')).toBe(host.doc.clientID);

        // Host clears on program exit.
        host.clearDebugState();
        await waitFor(() => guest.debugState.size === 0);
        expect(host.debugState.size).toBe(0);

        await host.destroy();
        await guest.destroy();
    });

    it('pending RPC requests reject on session destroy', async () => {
        const roomId = 'test-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });
        const host = new CollabSession(makeFakeHost(makeFakeBacking()), hostRoom);
        const guest = new CollabSession(makeFakeHost(makeFakeBacking()), guestRoom);
        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });

        // Handler that never resolves so the request is genuinely pending.
        host.onRequest('hang', () => new Promise(() => { /* never */ }));
        const requestPromise = guest.request(hostRoom.selfId, 'hang', null, { timeoutMs: 60_000 });

        // Destroy the guest mid-flight — the pending request should reject.
        await new Promise((r) => setTimeout(r, 20));
        await guest.destroy();
        await expect(requestPromise).rejects.toThrow(/session destroyed/);

        await host.destroy();
    });
});
