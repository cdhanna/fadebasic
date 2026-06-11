// Unit tests for RemoteDebugAdapter. The adapter is structurally typed
// against a `RemoteDebugSessionLike` interface — we feed it a hand-rolled
// mock that lets us:
//   * Set arbitrary Y.Map state under `debugState`
//   * Trigger observer callbacks deterministically
//   * Spy on RPC calls (channel, payload, peerId)
//
// The goal is to lock down the synthesized-event behavior so that
// observer UI updates match the host's debug state — the bugs that
// motivated these tests were:
//   1. First BREAKPOINT lost (constructor primed before facade attached)
//   2. Step transitions not emitting BREAKPOINT when paused stays true
//      but location changes
//   3. Host stop not propagating

import { describe, expect, it, vi } from 'vitest';

import { createRemoteDebugAdapter, type RemoteDebugSessionLike } from './remote-adapter';
import type { DebugEvent } from './adapter';

/** Tiny in-memory stand-in for the Y.Map slice that RemoteDebugAdapter
 *  consumes. Stores values and fans them out to registered observers
 *  when `commit()` is called — mirrors the Y.Doc transaction boundary so
 *  setting multiple keys in one update only fires observers once. */
function makeMockDebugState() {
    const data = new Map<string, unknown>();
    const observers = new Set<() => void>();
    let dirty = false;
    const fireObservers = () => {
        // Snapshot to be tolerant of mid-iteration mutations (RemoteAdapter
        // may unobserve from inside its own callback on swap/destroy).
        const list = Array.from(observers);
        for (const cb of list) {
            try { cb(); } catch (e) { console.error('[mock] observer threw', e); }
        }
    };
    return {
        debugState: {
            get(key: string): unknown { return data.get(key); },
            observe(cb: () => void): void { observers.add(cb); },
            unobserve(cb: () => void): void { observers.delete(cb); },
        } satisfies RemoteDebugSessionLike['debugState'],
        /** Bulk-write multiple keys and fire observers once. Pass null
         *  to delete a key (mirrors the host's setDebugState helper). */
        write(updates: Record<string, unknown>): void {
            for (const [k, v] of Object.entries(updates)) {
                if (v === undefined) continue;
                if (v === null) data.delete(k);
                else data.set(k, v);
                dirty = true;
            }
            if (dirty) {
                dirty = false;
                fireObservers();
            }
        },
        /** Clear every key — mirrors host's clearDebugState. */
        clear(): void {
            if (data.size === 0) return;
            data.clear();
            fireObservers();
        },
        observerCount(): number { return observers.size; },
        _setInternal(key: string, value: unknown): void {
            // Used only by setup — populate state BEFORE the adapter is
            // constructed, simulating "observer joins a session that's
            // already paused".
            if (value === null) data.delete(key);
            else data.set(key, value);
        },
    };
}

/** Build a mock session + RPC spy. Default RPC handler resolves to
 *  `{ rpc: channel, payload }` so tests can assert on the call shape
 *  without having to register per-channel responses. */
function makeMockSession() {
    const state = makeMockDebugState();
    const rpcCalls: Array<{ peerId: string; channel: string; payload: unknown }> = [];
    let rpcResponder: (channel: string, payload: unknown) => Promise<unknown> =
        async (channel, payload) => ({ rpc: channel, payload });
    const session: RemoteDebugSessionLike = {
        debugState: state.debugState,
        request: vi.fn(async (peerId: string, channel: string, payload: unknown) => {
            rpcCalls.push({ peerId, channel, payload });
            return rpcResponder(channel, payload);
        }),
    };
    return {
        session,
        state,
        rpcCalls,
        setRpcResponder(fn: (channel: string, payload: unknown) => Promise<unknown>): void {
            rpcResponder = fn;
        },
    };
}

/** Construct an adapter and capture every emitted event into an array
 *  for assertion. */
function makeAdapterAndCaptureEvents(session: RemoteDebugSessionLike): {
    adapter: ReturnType<typeof createRemoteDebugAdapter>;
    events: DebugEvent[];
} {
    const adapter = createRemoteDebugAdapter({ session });
    const events: DebugEvent[] = [];
    adapter.onDebugEvent((e) => events.push(e));
    return { adapter, events };
}

describe('RemoteDebugAdapter — state transitions', () => {
    it('initial state with no debugState is idle', () => {
        const { session } = makeMockSession();
        const { adapter, events } = makeAdapterAndCaptureEvents(session);

        expect(adapter.status).toBe('idle');
        expect(adapter.paused).toBe(false);
        expect(adapter.currentLocation).toBeNull();
        // Subscribed AFTER construction — the constructor's prime had no
        // recipients. So no events here.
        expect(events).toEqual([]);
    });

    it('observer joins a session that is already paused — adapter reflects state but post-subscribe events are empty', () => {
        const { session, state } = makeMockSession();
        // Simulate the Y.Map having state BEFORE the adapter is built —
        // this is "observer joined mid-pause".
        state._setInternal('initiatorClientId', 7);
        state._setInternal('initiatorPeerId', 'host-peer-id');
        state._setInternal('paused', true);
        state._setInternal('currentFile', 'main.fbasic');
        state._setInternal('currentLine', 42);
        state._setInternal('callStack', [{ name: 'MAIN', lineNumber: 41 }]);

        const { adapter, events } = makeAdapterAndCaptureEvents(session);

        // Internal state should be populated.
        expect(adapter.status).toBe('paused');
        expect(adapter.paused).toBe(true);
        expect(adapter.currentLocation).toEqual({ file: 'main.fbasic', line: 42 });

        // The constructor's prime emitted REV_REQUEST_BREAKPOINT, but the
        // subscriber wasn't attached yet — so events is empty. This is
        // the bug that main.ts works around by calling refreshDebugView()
        // explicitly from swapOnDebugStateChange after dbg.setInner.
        expect(events).toEqual([]);
    });

    it('paused: false → true with populated callStack emits REV_REQUEST_BREAKPOINT', () => {
        const { session, state } = makeMockSession();
        // Active session, running (not paused).
        state._setInternal('initiatorClientId', 7);
        state._setInternal('paused', false);
        const { events } = makeAdapterAndCaptureEvents(session);

        state.write({
            paused: true,
            currentFile: 'main.fbasic',
            currentLine: 10,
            callStack: [{ name: 'MAIN', lineNumber: 9 }],
        });

        expect(events.map((e) => e.type)).toEqual(['REV_REQUEST_BREAKPOINT']);
    });

    it('paused: false → true WITHOUT callStack emits NO BREAKPOINT (avoids spurious refreshDebugView RPC)', () => {
        // This is the host startDebug initial broadcast scenario:
        // `{paused: true, initiatorClientId, initiatorPeerId}` with no
        // callStack key. Emitting BREAKPOINT here would trigger
        // refreshDebugView → dbg.stackFrames → cache empty → 15s RPC
        // timeout against a host whose iframe is busy running the
        // program. The subsequent broadcast WITH callStack will fire
        // the BREAKPOINT properly via the locationChangedWhilePaused
        // branch.
        const { session, state } = makeMockSession();
        state._setInternal('initiatorClientId', 7);
        state._setInternal('paused', false);
        const { events } = makeAdapterAndCaptureEvents(session);

        // No callStack in this write.
        state.write({ paused: true });

        expect(events).toEqual([]);
    });

    it('paused: true → false emits REV_REQUEST_RESUMED', () => {
        const { session, state } = makeMockSession();
        state._setInternal('initiatorClientId', 7);
        state._setInternal('paused', true);
        state._setInternal('currentFile', 'main.fbasic');
        state._setInternal('currentLine', 10);
        const { events } = makeAdapterAndCaptureEvents(session);

        state.write({ paused: false });

        expect(events.map((e) => e.type)).toEqual(['REV_REQUEST_RESUMED']);
    });

    it('host step (paused stays true but line changes) emits REV_REQUEST_BREAKPOINT', () => {
        // This is the bug from the user's report: when the host steps,
        // the two writes (`{paused:false}` then `{paused:true, …new line}`)
        // can coalesce into a single observation in which `paused` stays
        // true but the location changes. The adapter MUST treat that as
        // a pause edge — otherwise the observer's panel never refreshes.
        const { session, state } = makeMockSession();
        state._setInternal('initiatorClientId', 7);
        state._setInternal('paused', true);
        state._setInternal('currentFile', 'main.fbasic');
        state._setInternal('currentLine', 10);
        state._setInternal('callStack', [{ name: 'MAIN', lineNumber: 9 }]);
        const { events } = makeAdapterAndCaptureEvents(session);

        // Only the line changes; paused stays true. (Mock writes update
        // atomically, so this is the "coalesced" case from real Y.js.)
        state.write({
            currentLine: 20,
            callStack: [{ name: 'MAIN', lineNumber: 19 }],
        });

        expect(events.map((e) => e.type)).toEqual(['REV_REQUEST_BREAKPOINT']);
    });

    it('host step (paused stays true but file changes) emits REV_REQUEST_BREAKPOINT', () => {
        const { session, state } = makeMockSession();
        state._setInternal('initiatorClientId', 7);
        state._setInternal('paused', true);
        state._setInternal('currentFile', 'main.fbasic');
        state._setInternal('currentLine', 10);
        state._setInternal('callStack', [{ name: 'MAIN', lineNumber: 9 }]);
        const { events } = makeAdapterAndCaptureEvents(session);

        state.write({
            currentFile: 'helper.fbasic',
            currentLine: 5,
            callStack: [{ name: 'HELPER', lineNumber: 4 }],
        });

        expect(events.map((e) => e.type)).toEqual(['REV_REQUEST_BREAKPOINT']);
    });

    it('paused: true → paused: true with no location change emits nothing', () => {
        // Defensive: if the host writes the same paused state twice
        // (e.g., a no-op debugState update), the adapter shouldn't spam
        // BREAKPOINTs.
        const { session, state } = makeMockSession();
        state._setInternal('initiatorClientId', 7);
        state._setInternal('paused', true);
        state._setInternal('currentFile', 'main.fbasic');
        state._setInternal('currentLine', 10);
        const { events } = makeAdapterAndCaptureEvents(session);

        // Touch the map with the SAME values (write needs at least one
        // key to fire the observer; pick a benign one).
        state.write({ callStack: [{ name: 'MAIN', lineNumber: 9 }] });

        expect(events).toEqual([]);
    });

    it('initiator cleared (host stops) emits complete', () => {
        const { session, state } = makeMockSession();
        state._setInternal('initiatorClientId', 7);
        state._setInternal('paused', true);
        state._setInternal('currentFile', 'main.fbasic');
        state._setInternal('currentLine', 10);
        const { adapter, events } = makeAdapterAndCaptureEvents(session);

        state.clear();

        expect(adapter.status).toBe('idle');
        // Order matters — `complete` is what main.ts treats as the final
        // event for cleanup. If the adapter were also paused→false in the
        // same tick we'd see RESUMED first, but since `initiatorClientId`
        // being null forces status=idle BEFORE the paused check, this is
        // the single emitted event.
        expect(events.map((e) => e.type)).toEqual(['complete']);
    });
});

describe('RemoteDebugAdapter — stackFrames cache', () => {
    it('returns cached call stack when populated, no RPC', async () => {
        const { session, state, rpcCalls } = makeMockSession();
        state._setInternal('initiatorClientId', 7);
        state._setInternal('initiatorPeerId', 'host-peer-id');
        state._setInternal('paused', true);
        state._setInternal('currentFile', 'main.fbasic');
        state._setInternal('currentLine', 42);
        const frames = [{ name: 'MAIN', lineNumber: 41 }];
        state._setInternal('callStack', frames);

        const { adapter } = makeAdapterAndCaptureEvents(session);
        const result = await adapter.stackFrames();

        expect(result).toEqual(frames);
        // refreshDebugView relies on `frames.length > 0` so the result
        // must be an Array, not a wrapped object.
        expect(Array.isArray(result)).toBe(true);
        expect(rpcCalls).toHaveLength(0);
    });

    it('falls back to RPC when call stack cache is empty', async () => {
        const { session, state, rpcCalls } = makeMockSession();
        state._setInternal('initiatorClientId', 7);
        state._setInternal('initiatorPeerId', 'host-peer-id');
        state._setInternal('paused', true);
        // No callStack key set — cache will be [].

        const { adapter } = makeAdapterAndCaptureEvents(session);
        await adapter.stackFrames();

        expect(rpcCalls).toHaveLength(1);
        expect(rpcCalls[0]).toMatchObject({
            peerId: 'host-peer-id',
            channel: 'debug:stackFrames',
        });
    });

    it('cache hit survives unrelated debugState updates', async () => {
        const { session, state, rpcCalls } = makeMockSession();
        state._setInternal('initiatorClientId', 7);
        state._setInternal('initiatorPeerId', 'host-peer-id');
        state._setInternal('paused', true);
        state._setInternal('callStack', [{ name: 'MAIN', lineNumber: 0 }]);

        const { adapter } = makeAdapterAndCaptureEvents(session);

        // Some unrelated update (e.g., setDebugState({ paused: true })
        // touched only the paused key). callStack should remain cached.
        state.write({ paused: true });
        await adapter.stackFrames();

        expect(rpcCalls).toHaveLength(0);
    });
});

describe('RemoteDebugAdapter — RPC routing', () => {
    it('continue / pause / step RPC the initiator peer with the right channel', async () => {
        const { session, state, rpcCalls } = makeMockSession();
        state._setInternal('initiatorClientId', 7);
        state._setInternal('initiatorPeerId', 'host-peer-id');
        state._setInternal('paused', true);

        const { adapter } = makeAdapterAndCaptureEvents(session);

        await adapter.continue();
        await adapter.pause();
        await adapter.step('over');
        await adapter.step('in');
        await adapter.step('out');

        expect(rpcCalls.map((c) => c.channel)).toEqual([
            'debug:continue',
            'debug:pause',
            'debug:step',
            'debug:step',
            'debug:step',
        ]);
        expect(rpcCalls.every((c) => c.peerId === 'host-peer-id')).toBe(true);
        // step payloads carry { kind }
        expect(rpcCalls[2].payload).toEqual({ kind: 'over' });
        expect(rpcCalls[3].payload).toEqual({ kind: 'in' });
        expect(rpcCalls[4].payload).toEqual({ kind: 'out' });
    });

    it('inspection RPCs (scopes/expand/eval/repl/setVariable) pass their args', async () => {
        const { session, state, rpcCalls } = makeMockSession();
        state._setInternal('initiatorClientId', 7);
        state._setInternal('initiatorPeerId', 'host-peer-id');
        state._setInternal('paused', true);

        const { adapter } = makeAdapterAndCaptureEvents(session);

        await adapter.scopes(0);
        await adapter.expandVariable(123);
        await adapter.eval(0, 'foo + 1');
        await adapter.repl(0, 'print 5');
        await adapter.setVariable(0, 42, '"hello"');
        await adapter.resolveInstruction(99);

        expect(rpcCalls.map((c) => ({ channel: c.channel, payload: c.payload }))).toEqual([
            { channel: 'debug:scopes', payload: { frameId: 0 } },
            { channel: 'debug:expandVariable', payload: { variableId: 123 } },
            { channel: 'debug:eval', payload: { frameId: 0, expression: 'foo + 1' } },
            { channel: 'debug:repl', payload: { frameId: 0, code: 'print 5' } },
            { channel: 'debug:setVariable', payload: { frameId: 0, variableId: 42, rhs: '"hello"' } },
            { channel: 'debug:resolveInstruction', payload: { insIndex: 99 } },
        ]);
    });

    it('setBreakpoints wraps the payload under `payload` so the host handler can re-extract it', async () => {
        // The host's RPC route is keyed on `(p: { payload }) =>
        // localDebugAdapter.setBreakpoints(p.payload)`, so the observer
        // must wrap rather than pass-through.
        const { session, state, rpcCalls } = makeMockSession();
        state._setInternal('initiatorClientId', 7);
        state._setInternal('initiatorPeerId', 'host-peer-id');
        state._setInternal('paused', true);

        const { adapter } = makeAdapterAndCaptureEvents(session);
        const bpRequest = { breakpoints: [{ file: 'main.fbasic', line: 10 }] };
        await adapter.setBreakpoints(bpRequest);

        expect(rpcCalls[0]).toMatchObject({
            channel: 'debug:setBreakpoints',
            payload: { payload: bpRequest },
        });
    });

    it('start() / startTest() reject — observers cannot launch a debug session', async () => {
        const { session } = makeMockSession();
        const { adapter } = makeAdapterAndCaptureEvents(session);
        await expect(adapter.start('source')).rejects.toThrow(/host-only/);
        await expect(adapter.startTest('source', 't')).rejects.toThrow(/host-only/);
    });

    it('control RPCs throw when no initiator is known (defensive)', async () => {
        const { session } = makeMockSession();
        // No initiatorPeerId set.
        const { adapter } = makeAdapterAndCaptureEvents(session);
        await expect(adapter.continue()).rejects.toThrow(/debug initiator is not known/);
    });
});

describe('RemoteDebugAdapter — destroy', () => {
    it('unsubscribes the Y.Map observer and stops emitting', () => {
        const { session, state } = makeMockSession();
        state._setInternal('initiatorClientId', 7);
        state._setInternal('paused', false);
        const { adapter, events } = makeAdapterAndCaptureEvents(session);

        expect(state.observerCount()).toBe(1);
        (adapter as any).destroy();
        expect(state.observerCount()).toBe(0);

        // After destroy, even a pause edge shouldn't reach subscribers.
        state.write({ paused: true, currentFile: 'foo', currentLine: 1 });
        expect(events).toEqual([]);
    });
});
