// Mock-only tests for the observer debug flow.
//
// These tests deliberately AVOID CollabSession + BroadcastChannel — that
// path is non-deterministic under vitest (BroadcastChannel timing races
// when multiple test files load) and timeouts produced false negatives
// faster than they pinned real bugs. Instead we model the wire as a
// single in-memory Y.Map-like state object that the test mutates
// directly. The RemoteDebugAdapter consumes a structural session
// interface, so we can feed it our mock and exercise the SAME state
// machine the production observer runs.
//
// The fakePanel mirrors what main.ts's Debug panel UI would render
// (frames list, current line decoration, status pill, scopes). It's
// updated by the SAME `onAnyDebugEvent` handler shape main.ts uses, so
// any regression in the event flow or refreshDebugView contract shows
// up here.
//
// What these tests lock in:
//   1. Host hits a bp → observer panel populates with frames + line +
//      scopes (the `variables are working` case the user verified).
//   2. Host steps (paused stays true, location changes) → observer
//      refreshes via the locationChangedWhilePaused branch.
//   3. Observer-driven continue: a sequence of Y.Map writes mirroring
//      what main.ts produces on the host side — paused:false from the
//      button handler, then paused:true with the new bp's snapshot —
//      lands the observer's panel in the new-bp state.
//   4. Host stops → observer panel resets.

import { describe, expect, it, vi } from 'vitest';
import { createRemoteDebugAdapter, type RemoteDebugSessionLike } from './remote-adapter';
import { createFacadeDebugAdapter } from './facade-adapter';
import type { DebugAdapter, DebugEvent, DebugStatus } from './adapter';

interface MockFrame { name: string; lineNumber: number }

/** In-memory stand-in for the Y.Map slice the RemoteAdapter consumes.
 *  Tests can `write({...})` to simulate a host's setDebugState call
 *  (atomic — all observers fire once after the write). */
function makeMockDebugState() {
    const data = new Map<string, unknown>();
    const observers = new Set<() => void>();
    const fire = () => {
        for (const cb of Array.from(observers)) {
            try { cb(); } catch (e) { console.error('[mock] observer threw', e); }
        }
    };
    return {
        debugState: {
            get(key: string): unknown { return data.get(key); },
            observe(cb: () => void): void { observers.add(cb); },
            unobserve(cb: () => void): void { observers.delete(cb); },
        } satisfies RemoteDebugSessionLike['debugState'],
        write(updates: Record<string, unknown>): void {
            let dirty = false;
            for (const [k, v] of Object.entries(updates)) {
                if (v === undefined) continue;
                if (v === null) { data.delete(k); dirty = true; }
                else { data.set(k, v); dirty = true; }
            }
            if (dirty) fire();
        },
        clear(): void {
            if (data.size === 0) return;
            data.clear();
            fire();
        },
        _set(key: string, value: unknown): void {
            // Pre-seed without firing observers (used to set up state
            // BEFORE the adapter is constructed).
            if (value === null) data.delete(key);
            else data.set(key, value);
        },
    };
}

/** Mock RemoteDebugSessionLike — wraps the mock debug state and a
 *  scriptable RPC handler. Tests can set `rpcHandler` to control how
 *  RPC calls (scopes, eval, etc.) resolve. */
function makeMockSession(rpcHandler?: (channel: string, payload: unknown) => Promise<unknown>) {
    const state = makeMockDebugState();
    const rpcCalls: Array<{ peerId: string; channel: string; payload: unknown }> = [];
    const defaultHandler = async (channel: string, payload: unknown) => ({ rpc: channel, payload });
    const session: RemoteDebugSessionLike = {
        debugState: state.debugState,
        request: vi.fn(async (peerId, channel, payload) => {
            rpcCalls.push({ peerId, channel, payload });
            return (rpcHandler ?? defaultHandler)(channel, payload);
        }),
    };
    return { session, state, rpcCalls };
}

interface FakePanel {
    debugSessionActive: boolean;
    debugPaused: boolean;
    status: DebugStatus | string;
    currentLine: number | null;
    renderedFrames: MockFrame[] | null;
    renderedScopes: unknown[] | null;
    callStackRevealed: boolean;
    /** Counts the number of times refreshDebugView completed. Useful
     *  for assertions that a NEW refresh happened after a state
     *  transition. */
    refreshCount: number;
}

/** Build the observer wiring: facade + remote adapter + onAnyDebugEvent
 *  + swap (the equivalent of main.ts's swapOnDebugStateChange).
 *  Tests drive the underlying session and assert on `panel`. */
function makeObserverHarness(session: RemoteDebugSessionLike, observerClientId = 99) {
    const panel: FakePanel = {
        debugSessionActive: false,
        debugPaused: false,
        status: 'idle',
        currentLine: null,
        renderedFrames: null,
        renderedScopes: null,
        callStackRevealed: false,
        refreshCount: 0,
    };

    const observerLocalAdapter: DebugAdapter = {
        kind: 'local',
        start: async () => ({ ok: true }),
        startTest: async () => ({ ok: true }),
        terminate: async () => {},
        continue: async () => {},
        pause: async () => {},
        step: async () => {},
        setBreakpoints: async () => {},
        stackFrames: async () => [],
        scopes: async () => ({ scopes: [] }),
        expandVariable: async () => ({ scopes: [] }),
        eval: async () => null,
        repl: async () => null,
        setVariable: async () => null,
        resolveInstruction: async () => null,
        status: 'idle',
        paused: false,
        currentLocation: null,
        onDebugEvent: () => () => {},
    };

    const dbg = createFacadeDebugAdapter(observerLocalAdapter);
    let currentRemoteAdapter: ReturnType<typeof createRemoteDebugAdapter> | null = null;
    let lastObservedPaused = false;

    const refreshDebugView = async () => {
        const frames = await dbg.stackFrames();
        if (!panel.debugSessionActive) return;
        panel.renderedFrames = Array.isArray(frames) ? frames : null;
        if (Array.isArray(frames) && frames.length > 0) {
            panel.currentLine = frames[0].lineNumber + 1;
            const scopesResult = await dbg.scopes(0);
            if (!panel.debugSessionActive) return;
            panel.renderedScopes = scopesResult?.scopes ?? [];
        } else {
            panel.currentLine = null;
            panel.renderedScopes = [];
        }
        panel.refreshCount++;
    };

    const onAnyDebugEvent = async (event: DebugEvent) => {
        switch (event.type) {
            case 'REV_REQUEST_BREAKPOINT':
                panel.debugPaused = true;
                panel.status = 'paused on breakpoint';
                panel.callStackRevealed = true;
                await refreshDebugView();
                break;
            case 'REV_REQUEST_RESUMED':
                break; // swap handles the resume edge
            case 'complete':
            case 'REV_REQUEST_EXITED':
                panel.debugSessionActive = false;
                panel.debugPaused = false;
                panel.status = 'program exited';
                panel.currentLine = null;
                panel.renderedFrames = null;
                panel.renderedScopes = null;
                break;
        }
    };

    const swap = () => {
        const initiator = session.debugState.get('initiatorClientId') as number | undefined;
        const observingRemote = initiator != null && initiator !== observerClientId;
        if (observingRemote) {
            const justAttached = !currentRemoteAdapter;
            if (!currentRemoteAdapter) {
                currentRemoteAdapter = createRemoteDebugAdapter({ session });
                dbg.setInner(currentRemoteAdapter);
            }
            const newPaused = Boolean(session.debugState.get('paused'));
            panel.debugSessionActive = true;
            panel.debugPaused = newPaused;
            if (lastObservedPaused && !newPaused) {
                panel.status = 'running';
                panel.currentLine = null;
            }
            lastObservedPaused = newPaused;
            if (justAttached && newPaused) {
                const callStack = session.debugState.get('callStack');
                if (Array.isArray(callStack) && callStack.length > 0) {
                    void refreshDebugView();
                }
            }
        } else {
            if (currentRemoteAdapter) {
                dbg.setInner(observerLocalAdapter);
                (currentRemoteAdapter as any).destroy?.();
                currentRemoteAdapter = null;
                if (initiator !== observerClientId) {
                    panel.debugSessionActive = false;
                    panel.debugPaused = false;
                    panel.status = 'program exited';
                    panel.currentLine = null;
                    panel.renderedFrames = null;
                    panel.renderedScopes = null;
                }
            }
            lastObservedPaused = false;
        }
    };

    session.debugState.observe(swap);
    dbg.onDebugEvent((e) => { void onAnyDebugEvent(e); });
    swap();

    return { panel, dbg, swap };
}

/** Tick the event loop a couple of times so Promise microtasks resolve. */
async function settle() {
    await new Promise((r) => setTimeout(r, 0));
    await new Promise((r) => setTimeout(r, 0));
}

describe('observer panel flow (mock-only)', () => {
    it('host hits first bp → observer panel populates with frames + line + scopes', async () => {
        const { session, state } = makeMockSession(async (channel, _payload) => {
            if (channel === 'debug:scopes') {
                return { scopes: [{ scopeName: 'locals', variables: [{ name: 'x', value: '42' }] }] };
            }
            return null;
        });
        const { panel } = makeObserverHarness(session);

        // Initial broadcast carries only initiator + paused — NO callStack.
        // The adapter's BREAKPOINT-emission gate (`hasFrames`) suppresses
        // any synthesized event for this incomplete state.
        state.write({
            initiatorClientId: 7,
            initiatorPeerId: 'host-peer',
            paused: true,
        });
        await settle();
        expect(panel.debugSessionActive).toBe(true);
        expect(panel.renderedFrames).toBeNull();

        // Host's BREAKPOINT case broadcasts the full snapshot.
        state.write({
            paused: true,
            currentFile: 'main.fbasic',
            currentLine: 9,
            callStack: [{ name: 'MAIN', lineNumber: 9 }],
        });
        await settle();

        expect(panel.renderedFrames).toEqual([{ name: 'MAIN', lineNumber: 9 }]);
        expect(panel.currentLine).toBe(10);
        expect(panel.debugPaused).toBe(true);
        expect(panel.status).toBe('paused on breakpoint');
        expect(panel.renderedScopes).toEqual([
            { scopeName: 'locals', variables: [{ name: 'x', value: '42' }] },
        ]);
    });

    it('host step (paused stays true, location changes) → observer refreshes', async () => {
        const { session, state } = makeMockSession(async () => ({ scopes: [] }));
        const { panel } = makeObserverHarness(session);

        state.write({
            initiatorClientId: 7,
            initiatorPeerId: 'host-peer',
            paused: true,
            currentFile: 'main.fbasic',
            currentLine: 9,
            callStack: [{ name: 'MAIN', lineNumber: 9 }],
        });
        await settle();
        const initialRefreshCount = panel.refreshCount;
        expect(panel.currentLine).toBe(10);

        // Host step lands at line 14 — same file, new line, paused stays true.
        state.write({
            currentLine: 14,
            callStack: [{ name: 'MAIN', lineNumber: 14 }],
        });
        await settle();

        expect(panel.refreshCount).toBeGreaterThan(initialRefreshCount);
        expect(panel.currentLine).toBe(15);
        expect(panel.renderedFrames).toEqual([{ name: 'MAIN', lineNumber: 14 }]);
    });

    it('observer-driven continue → wire sequence (paused:false then paused:true,new-bp) → panel ends at new bp', async () => {
        // Models the user's `observer hits continue / doesn't loop back`
        // scenario. The host's onObserverContinue writes paused:false,
        // then later (when the runtime hits the next bp) the BREAKPOINT
        // case writes paused:true with the new snapshot. Sandwich with
        // a settle() between them so the observer processes paused:false
        // separately (firing the resume edge).
        const { session, state } = makeMockSession(async () => ({
            scopes: [{ scopeName: 'locals', variables: [{ name: 'y', value: '99' }] }],
        }));
        const { panel } = makeObserverHarness(session);

        // Setup: host paused at line 10.
        state.write({
            initiatorClientId: 7,
            initiatorPeerId: 'host-peer',
            paused: true,
            currentFile: 'main.fbasic',
            currentLine: 9,
            callStack: [{ name: 'MAIN', lineNumber: 9 }],
        });
        await settle();
        expect(panel.currentLine).toBe(10);

        // Host's onObserverContinue broadcast: paused:false (no callStack
        // wipe — that key stays populated with the OLD frames).
        state.write({ paused: false });
        await settle();
        expect(panel.debugPaused).toBe(false);
        expect(panel.status).toBe('running');
        expect(panel.currentLine).toBeNull();

        // Host's runtime hits the next bp. BREAKPOINT case broadcasts
        // the full new snapshot.
        state.write({
            paused: true,
            currentFile: 'main.fbasic',
            currentLine: 24,
            callStack: [{ name: 'MAIN', lineNumber: 24 }],
        });
        await settle();

        expect(panel.debugPaused).toBe(true);
        expect(panel.status).toBe('paused on breakpoint');
        expect(panel.currentLine).toBe(25);
        expect(panel.renderedFrames).toEqual([{ name: 'MAIN', lineNumber: 24 }]);
        expect(panel.renderedScopes).toEqual([
            { scopeName: 'locals', variables: [{ name: 'y', value: '99' }] },
        ]);
    });

    it('observer-driven continue → coalesced wire updates (paused:false then paused:true land in same tick) → panel still ends at new bp', async () => {
        // Pessimistic scenario: the two host broadcasts coalesce into a
        // single observer-side tick. The observer's adapter must see
        // paused:false-then-paused:true as a NET pause at the new line
        // (which differs from the prior line). Without locking this in,
        // a future tightening of `locationChangedWhilePaused` could
        // silently break it.
        const { session, state } = makeMockSession(async () => ({ scopes: [] }));
        const { panel } = makeObserverHarness(session);

        state.write({
            initiatorClientId: 7,
            initiatorPeerId: 'host-peer',
            paused: true,
            currentFile: 'main.fbasic',
            currentLine: 9,
            callStack: [{ name: 'MAIN', lineNumber: 9 }],
        });
        await settle();

        // No settle() between the writes — they coalesce.
        state.write({ paused: false });
        state.write({
            paused: true,
            currentFile: 'main.fbasic',
            currentLine: 24,
            callStack: [{ name: 'MAIN', lineNumber: 24 }],
        });
        await settle();

        expect(panel.debugPaused).toBe(true);
        expect(panel.currentLine).toBe(25);
        expect(panel.renderedFrames).toEqual([{ name: 'MAIN', lineNumber: 24 }]);
    });

    it('rapid sequential bps → observer panel ends at the LATEST bp (no lag from stale broadcasts)', async () => {
        // The user's reported lag: the host's `debugState.currentLine`
        // showed a line behind the actual debugger position. Root cause
        // was the BREAKPOINT case calling stackFrames TWICE (once for
        // the local render, once for the broadcast) — the iframe's
        // pumpDebugTick could advance the VM between the two calls,
        // putting the broadcast and the editor at DIFFERENT VM instants.
        //
        // After the refactor, the BREAKPOINT case fetches frames ONCE
        // and feeds them to BOTH consumers. From the observer's wire-
        // sequence point of view, this is just multiple atomic
        // broadcasts of (paused:true, currentFile, currentLine,
        // callStack). The observer must always end at the LATEST
        // broadcasted line.
        const { session, state } = makeMockSession(async () => ({ scopes: [] }));
        const { panel } = makeObserverHarness(session);

        // Pause at line 9.
        state.write({
            initiatorClientId: 7,
            initiatorPeerId: 'host-peer',
            paused: true,
            currentFile: 'main.fbasic',
            currentLine: 9,
            callStack: [{ name: 'MAIN', lineNumber: 9 }],
        });
        await settle();

        // Three rapid step landings, each carrying its own atomic
        // snapshot. The host's new BREAKPOINT case writes ALL of
        // {paused, currentFile, currentLine, callStack} together so
        // the observer sees the same VM instant in every key.
        state.write({
            paused: true,
            currentFile: 'main.fbasic',
            currentLine: 14,
            callStack: [{ name: 'MAIN', lineNumber: 14 }],
        });
        state.write({
            paused: true,
            currentFile: 'main.fbasic',
            currentLine: 19,
            callStack: [{ name: 'MAIN', lineNumber: 19 }],
        });
        state.write({
            paused: true,
            currentFile: 'main.fbasic',
            currentLine: 24,
            callStack: [{ name: 'MAIN', lineNumber: 24 }],
        });
        await settle();

        // Final state must match the LATEST broadcast — no lag.
        expect(panel.currentLine).toBe(25);
        expect(panel.renderedFrames).toEqual([{ name: 'MAIN', lineNumber: 24 }]);
        expect(panel.debugPaused).toBe(true);
    });

    it('host stops (debugState cleared) → observer panel resets', async () => {
        const { session, state } = makeMockSession(async () => ({ scopes: [] }));
        const { panel } = makeObserverHarness(session);

        state.write({
            initiatorClientId: 7,
            initiatorPeerId: 'host-peer',
            paused: true,
            currentFile: 'main.fbasic',
            currentLine: 9,
            callStack: [{ name: 'MAIN', lineNumber: 9 }],
        });
        await settle();
        expect(panel.renderedFrames).not.toBeNull();

        state.clear();
        await settle();
        expect(panel.debugSessionActive).toBe(false);
        expect(panel.debugPaused).toBe(false);
        expect(panel.currentLine).toBeNull();
        expect(panel.renderedFrames).toBeNull();
    });
});
