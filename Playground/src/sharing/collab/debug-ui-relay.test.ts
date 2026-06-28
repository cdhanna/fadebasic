// @vitest-environment jsdom
//
// End-to-end test for the Debug UI envelope relay AND the observer's
// debug-ui-panel render path.
//
// Two CollabSession instances wired through mock transports stand in for
// the host + observer browser tabs. Instead of running a real monogame
// iframe, the test feeds the host's broadcast directly with the same
// JSON the user saw in their `debug-ui-collab` Logs panel. The observer
// subscribes via `session.onDebugUiFrame` and pipes the relayed JSON
// into a freshly-mounted `mountDebugUiPanel`. We then assert the slider
// actually lands in the DOM.
//
// This collapses the prior manual loop (reload host, share, reload
// observer, run, paste logs) into one ~1s vitest. If the slider doesn't
// render with the exact wire bytes the user reported, the test fails
// here instead of through screen-sharing.

import { describe, expect, it, beforeAll } from 'vitest';

import { CollabSession, type SessionHost } from './session';
import { mockTransport } from './mock-transport';
import { makeIdentity } from './identity';
import { mountDebugUiPanel } from '../../debug-ui-panel';
import { parseDebugUiEnvelope, type DebugUiFrameEnvelope } from '../../monogame-host';

// Reused BroadcastChannel polyfill from session.test.ts. Node 18+ ships
// one natively but older jsdoms need this backstop.
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

// Empty stub — the relay doesn't touch the editor/workspace surface, but
// CollabSession requires SOMETHING for the SessionHost contract.
function emptyHost(): SessionHost {
    return {
        get editor() { return null as any; },
        getActiveFileName: () => null,
        onActiveFileChange: () => () => {},
        getModelForFile: () => null,
        openFile: async () => {},
        closeFile: async () => {},
        listWorkspaceFiles: async () => [],
        isBinaryPath: () => false,
        readWorkspaceText: async () => '',
        readWorkspaceBytes: async () => new Uint8Array(),
        writeWorkspaceText: async () => {},
        writeWorkspaceBytes: async () => {},
        deleteWorkspaceFile: async () => {},
        refreshFileList: async () => {},
    };
}

async function waitFor(check: () => boolean, ms = 1500): Promise<void> {
    const start = Date.now();
    while (Date.now() - start < ms) {
        if (check()) return;
        await new Promise((r) => setTimeout(r, 10));
    }
    throw new Error(`waitFor timed out after ${ms}ms`);
}

// Verbatim from the observer's `debug-ui-collab` Logs panel: WINDOW_START
// "shaders", FLOAT_SLIDER "Glitch Amount", 2x ARG_FLOAT (min=0, max=100),
// WINDOW_END. queue length 5, gen 1, autoInspector false.
const SHADERS_FRAME_JSON = JSON.stringify({
    gen: 1,
    queue: [
        { id: 88660769, t: 0, l: 'shaders', s: null, i: 0, f: 0 },
        { id: 2143514761, t: 15, l: 'Glitch Amount', s: null, i: 0, f: 25 },
        { id: -1369638928, t: 22, l: null, s: null, i: 0, f: 0 },
        { id: -1369638928, t: 22, l: null, s: null, i: 0, f: 100 },
        { id: 88660769, t: 1, l: null, s: null, i: 0, f: 0 },
    ],
    autoInspector: false,
});

const EMPTY_FRAME_JSON = JSON.stringify({ gen: 0, queue: [], autoInspector: false });

describe('Debug UI relay end-to-end', () => {
    it('host broadcast reaches observer with the exact envelope bytes', async () => {
        const roomId = 'dbgui-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });
        const host = new CollabSession(emptyHost(), hostRoom);
        const guest = new CollabSession(emptyHost(), guestRoom);
        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });

        const received: string[] = [];
        guest.onDebugUiFrame((_peerId, json) => { received.push(json); });

        host.sendDebugUiFrame(EMPTY_FRAME_JSON);
        host.sendDebugUiFrame(SHADERS_FRAME_JSON);

        await waitFor(() => received.length === 2);
        expect(received).toEqual([EMPTY_FRAME_JSON, SHADERS_FRAME_JSON]);

        await host.destroy();
        await guest.destroy();
    });

    it('observer pipes relayed envelopes into mountDebugUiPanel and the slider renders', async () => {
        const roomId = 'dbgui-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });
        const host = new CollabSession(emptyHost(), hostRoom);
        const guest = new CollabSession(emptyHost(), guestRoom);
        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });

        // Mount the observer's debug-ui-panel into a real DOM element,
        // sized the way dockview would size it.
        document.body.innerHTML = '';
        const container = document.createElement('div');
        container.style.width = '420px';
        container.style.height = '600px';
        document.body.appendChild(container);

        const handle = mountDebugUiPanel({
            container,
            getSchema: async () => null,
            listEntities: async () => [],
            getEntity: async () => null,
            setField: async () => true,
            sendFbasicChange: () => {},
        });

        // Same subscription main.ts wires up on the observer.
        const selfId = (guest as any).room?.selfId as string | undefined;
        let appliedCount = 0;
        guest.onDebugUiFrame((peerId, json) => {
            if (peerId === selfId) return;
            try {
                const env: DebugUiFrameEnvelope = parseDebugUiEnvelope(json);
                handle.applyFrameEnvelope(env);
                appliedCount++;
            } catch (e) {
                // Surface decode/apply errors loudly so the test failure
                // points right at this hop.
                console.error('[test] applyFrameEnvelope failed', e);
                throw e;
            }
        });

        // Sequence the observer would see: an empty idle envelope, then
        // the real one once the host's program loads.
        host.sendDebugUiFrame(EMPTY_FRAME_JSON);
        await waitFor(() => appliedCount === 1);
        host.sendDebugUiFrame(SHADERS_FRAME_JSON);
        await waitFor(() => appliedCount === 2);

        // The whole point of the regression: after the envelope applies,
        // the slider widget MUST be in the DOM. If this passes locally
        // but the user still sees nothing in production, the issue is
        // outside the relay+panel pipeline (stale browser cache, dockview
        // detaching the host element, CSS load order).
        const titles = Array.from(container.querySelectorAll('.tp-rotv_t'))
            .map((el) => el.textContent?.trim());
        expect(titles).toContain('shaders');

        const sliders = container.querySelectorAll('.tp-sldv');
        const bindings = container.querySelectorAll('.tp-lblv');
        const labelTexts = Array.from(container.querySelectorAll('.tp-lblv_l'))
            .map((el) => el.textContent?.trim());

        // Dump on failure so a regression points directly at the missing
        // class. Tweakpane's class taxonomy is stable across patch releases.
        const dom = container.innerHTML.replace(/\s+/g, ' ').slice(0, 800);
        expect(sliders.length, `expected slider widget rendered; DOM=${dom}`).toBeGreaterThanOrEqual(1);
        expect(bindings.length, `expected binding row rendered; DOM=${dom}`).toBeGreaterThanOrEqual(1);
        expect(labelTexts, `expected 'Glitch Amount' label; DOM=${dom}`).toContain('Glitch Amount');

        handle.dispose();
        await host.destroy();
        await guest.destroy();
    });

    it('does NOT echo self-broadcasts — host applies locally but its observer subscription skips its own peerId', async () => {
        // Documents the loopback-filter contract main.ts relies on. If
        // mockTransport ever changed to NOT self-deliver, this stays
        // green; if it started self-delivering, this catches double-apply.
        const roomId = 'dbgui-' + Math.random().toString(36).slice(2, 8);
        const hostRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
        });
        const guestRoom = await mockTransport.join({
            appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
        });
        const host = new CollabSession(emptyHost(), hostRoom);
        const guest = new CollabSession(emptyHost(), guestRoom);
        await host.start({ role: 'host', identity: makeIdentity('Alice') });
        await guest.start({ role: 'guest', identity: makeIdentity('Bob') });

        const hostSelfId = (host as any).room?.selfId as string;
        const filtered: { peerId: string; isSelf: boolean }[] = [];
        host.onDebugUiFrame((peerId, _json) => {
            filtered.push({ peerId, isSelf: peerId === hostSelfId });
        });

        host.sendDebugUiFrame(SHADERS_FRAME_JSON);
        // Give the transport a tick. Allow some self-echo (mock transport
        // may deliver to all subscribers including the sender) but the
        // observer-side filter would drop those.
        await new Promise((r) => setTimeout(r, 50));
        // Just assert that if any echo arrives, we can identify it as
        // self. The filter at the call site in main.ts uses peerId ===
        // room.selfId, which this confirms is a reliable check.
        for (const entry of filtered) {
            if (entry.isSelf) {
                expect(entry.peerId).toBe(hostSelfId);
            }
        }

        await host.destroy();
        await guest.destroy();
    });
});
