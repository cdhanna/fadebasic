import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { GhostStreamEvent } from './protocol';

const sendSpy = vi.fn<(data: Uint8Array, target?: string | string[] | null) => void>();
let receiveHandler: ((data: Uint8Array, peerId: string) => void) | null = null;
let peerJoinCb: ((id: string) => void) | null = null;
let peerLeaveCb: ((id: string) => void) | null = null;

vi.mock('trystero/nostr', () => ({
    selfId: 'ghost-self',
    getRelaySockets: () => ({}),
    joinRoom: vi.fn(() => ({
        makeAction: () => [sendSpy, (cb: (data: Uint8Array, peerId: string) => void) => { receiveHandler = cb; }],
        onPeerJoin: (cb: (id: string) => void) => { peerJoinCb = cb; },
        onPeerLeave: (cb: (id: string) => void) => { peerLeaveCb = cb; },
        leave: () => { /* noop */ },
    })),
}));

import { createGhostHub } from './session';
import { encodeMessage } from './protocol';

function sent(): Array<Record<string, unknown>> {
    return sendSpy.mock.calls.map(([bytes]) =>
        JSON.parse(new TextDecoder().decode(bytes)) as Record<string, unknown>);
}
function sentOfType(type: string): Array<Record<string, unknown>> {
    return sent().filter((m) => m.type === type);
}
const inbound = (peerId: string, msg: unknown) =>
    receiveHandler!(encodeMessage(msg as never), peerId);

function newHub() {
    sendSpy.mockClear();
    receiveHandler = null; peerJoinCb = null; peerLeaveCb = null;
    const hub = createGhostHub('CODE99');
    hub.setStreamHandler(async function* () {
        yield { kind: 'text', delta: 'hi' } as GhostStreamEvent;
        yield { kind: 'done', finishReason: 'stop' } as GhostStreamEvent;
    });
    hub.setModelStatus({ loaded: true, name: 'test-model' });
    hub.start();
    return hub;
}

describe('GhostHub — trust on first use', () => {
    beforeEach(() => { sendSpy.mockClear(); });

    it('holds an unknown client as pending until approved', () => {
        const hub = newHub();
        peerJoinCb!('peerA');
        inbound('peerA', { v: 1, type: 'hello', clientId: 'clientA', label: 'Project A' });

        expect(sentOfType('auth').at(-1)).toMatchObject({ status: 'pending', to: 'peerA' });
        expect(hub.listConnections()).toEqual([
            expect.objectContaining({ peerId: 'peerA', label: 'Project A', approved: false, status: 'pending' }),
        ]);

        sendSpy.mockClear();
        hub.approve('peerA');
        expect(sentOfType('auth').at(-1)).toMatchObject({ status: 'approved', to: 'peerA' });
        expect(sentOfType('pong').some((m) => m.to === 'peerA')).toBe(true);
        expect(hub.listConnections()[0]).toMatchObject({ approved: true, status: 'connected' });
    });

    it('auto-approves a client that was approved earlier', () => {
        const hub = newHub();
        peerJoinCb!('peerA');
        inbound('peerA', { v: 1, type: 'hello', clientId: 'clientX', label: 'A' });
        hub.approve('peerA');

        // Same client reconnects on a new peerId — should skip the prompt.
        sendSpy.mockClear();
        peerJoinCb!('peerB');
        inbound('peerB', { v: 1, type: 'hello', clientId: 'clientX', label: 'A-again' });
        expect(sentOfType('auth').at(-1)).toMatchObject({ status: 'approved', to: 'peerB' });
        const b = hub.listConnections().find((c) => c.peerId === 'peerB');
        expect(b).toMatchObject({ approved: true });
    });

    it('ignores stream requests from an unapproved peer', async () => {
        newHub();
        peerJoinCb!('peerA');
        inbound('peerA', { v: 1, type: 'hello', clientId: 'c', label: 'A' });
        sendSpy.mockClear();
        inbound('peerA', { v: 1, type: 'stream', id: 1, messages: [] });
        // No stream-event; just another pending auth nudge.
        await new Promise((r) => setTimeout(r, 10));
        expect(sentOfType('stream-event')).toEqual([]);
        expect(sentOfType('auth').at(-1)).toMatchObject({ status: 'pending' });
    });
});

describe('GhostHub — per-peer isolation', () => {
    beforeEach(() => { sendSpy.mockClear(); });

    it('addresses stream replies only to the requesting peer', async () => {
        const hub = newHub();
        for (const id of ['peerA', 'peerB']) {
            peerJoinCb!(id);
            inbound(id, { v: 1, type: 'hello', clientId: id, label: id });
            hub.approve(id);
        }
        sendSpy.mockClear();
        inbound('peerA', { v: 1, type: 'stream', id: 7, messages: [] });
        await new Promise((r) => setTimeout(r, 20));

        const events = sentOfType('stream-event');
        expect(events.length).toBeGreaterThan(0);
        // Every reply for this stream is addressed to peerA, never peerB.
        expect(events.every((m) => m.to === 'peerA')).toBe(true);
        expect(events.some((m) => m.to === 'peerB')).toBe(false);
    });

    it('tracks multiple connections and drops one on leave', () => {
        const hub = newHub();
        peerJoinCb!('peerA');
        peerJoinCb!('peerB');
        expect(hub.listConnections()).toHaveLength(2);
        peerLeaveCb!('peerA');
        expect(hub.listConnections().map((c) => c.peerId)).toEqual(['peerB']);
    });
});
