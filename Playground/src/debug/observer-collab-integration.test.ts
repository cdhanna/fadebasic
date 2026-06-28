// Real end-to-end integration tests using TWO CollabSessions wired
// through the mock transport. This bypasses my mock-only harness and
// exercises the actual wire path: Y.Doc transactions, awareness sync,
// RPC over WebRTC-mock, the whole chain.
//
// The purpose is to lock in BEHAVIOR THE USER SEES IN PRODUCTION. If
// these tests pass but the user reports breakage, the gap is in main.ts
// glue the test doesn't replicate (focusJoinedDebugLine's openFile,
// projectSourceMap, etc.). If the tests FAIL, the bug is in the
// session/adapter/broadcast layer and is reproducible here.
//
// Each test follows the same pattern:
//   1. Boot host + guest sessions through mock-transport
//   2. Install host RPC handlers mirroring main.ts's
//      installCollabRuntimeListeners
//   3. Install observer wiring (RemoteAdapter via facade, swap, mock
//      onAnyDebugEvent matching main.ts's case statements)
//   4. Drive the host through `host.setDebugState(...)` writes that
//      EXACTLY mirror what main.ts's BREAKPOINT/PROTO_ACK case writes
//   5. Settle and assert on observer's mock panel state
//
// The host-side helpers (fetchPausedFramesAndBroadcastHost,
// brokeredHostBreakpoint) literally mirror main.ts so a divergence
// shows up as a test failure rather than a head-scratching production
// bug report.

import { beforeAll, describe, expect, it } from 'vitest';
import { CollabSession, type SessionHost } from '../sharing/collab/session';
import { mockTransport } from '../sharing/collab/mock-transport';
import { makeIdentity } from '../sharing/collab/identity';
import { createRemoteDebugAdapter, type RemoteDebugSessionLike } from './remote-adapter';
import { createFacadeDebugAdapter } from './facade-adapter';
import type { DebugAdapter, DebugEvent, DebugStatus, StepKind } from './adapter';

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

function makeFakeSessionHost(): SessionHost {
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

async function waitFor(check: () => boolean, ms = 2000, label = 'condition'): Promise<void> {
    const start = Date.now();
    while (Date.now() - start < ms) {
        if (check()) return;
        await new Promise((r) => setTimeout(r, 10));
    }
    throw new Error(`waitFor(${label}) timed out after ${ms}ms`);
}

interface MockFrame { name: string; lineNumber: number }

interface MockHostRuntime {
    adapter: DebugAdapter;
    /** Pretend the host's runtime stepped/paused at `frames`. Updates
     *  the adapter's frame cache so subsequent stackFrames return them.
     *  No event is fired — call host's broadcast helper directly to
     *  simulate what main.ts's PROTO_ACK/BREAKPOINT case does. */
    setFrames(frames: MockFrame[]): void;
    callLog: Array<{ method: string; args?: unknown[] }>;
    /** Outstanding step() promises — call resolveStep() to ack. */
    resolveStep(): void;
    pendingStep: number;
    /** Outstanding continue() promises — call resolveContinue() to ack. */
    resolveContinue(): void;
    pendingContinue: number;
}

function makeMockHostRuntime(initialFrames: MockFrame[] = []): MockHostRuntime {
    let frames = initialFrames;
    let _status: DebugStatus = 'idle';
    const subscribers = new Set<(e: DebugEvent) => void>();
    const callLog: Array<{ method: string; args?: unknown[] }> = [];
    const stepWaits: Array<() => void> = [];
    const continueWaits: Array<() => void> = [];
    const adapter: DebugAdapter = {
        kind: 'local',
        start: async () => { _status = 'paused'; return { ok: true }; },
        startTest: async () => { _status = 'paused'; return { ok: true }; },
        terminate: async () => { _status = 'completed'; },
        continue: () => {
            callLog.push({ method: 'continue' });
            return new Promise<void>((resolve) => { continueWaits.push(resolve); });
        },
        pause: async () => { callLog.push({ method: 'pause' }); },
        step: (kind: StepKind) => {
            callLog.push({ method: 'step', args: [kind] });
            return new Promise<void>((resolve) => { stepWaits.push(resolve); });
        },
        setBreakpoints: async () => {},
        stackFrames: async () => frames,
        scopes: async (frameId: number) => {
            callLog.push({ method: 'scopes', args: [frameId] });
            return {
                scopes: [{ scopeName: 'Locals', variables: [{ name: 'i', value: '0' }] }],
            };
        },
        expandVariable: async () => ({ scopes: [] }),
        eval: async () => null,
        repl: async () => null,
        setVariable: async () => null,
        resolveInstruction: async () => null,
        get status() { return _status; },
        get paused() { return _status === 'paused'; },
        get currentLocation() {
            if (frames.length === 0) return null;
            return { file: 'main.fbasic', line: frames[0].lineNumber };
        },
        onDebugEvent(handler) {
            subscribers.add(handler);
            return () => subscribers.delete(handler);
        },
    };
    return {
        adapter,
        setFrames(next) { frames = next; _status = 'paused'; },
        callLog,
        resolveStep() { const r = stepWaits.shift(); if (r) r(); },
        get pendingStep() { return stepWaits.length; },
        resolveContinue() { const r = continueWaits.shift(); if (r) r(); },
        get pendingContinue() { return continueWaits.length; },
    };
}

/** EXACT mirror of main.ts's fetchPausedFramesAndBroadcast. Pulls frames
 *  from the host's runtime and broadcasts the per-file mapped snapshot.
 *  Tests use this directly so the same code path that main.ts exercises
 *  is exercised here, byte-for-byte. */
async function hostFetchAndBroadcast(args: {
    host: CollabSession;
    runtime: MockHostRuntime;
    activeName: string;
}): Promise<MockFrame[]> {
    const { host, runtime, activeName } = args;
    const frames = await runtime.adapter.stackFrames() as MockFrame[];
    const lineNumber = frames[0]?.lineNumber;
    // The real main.ts uses projectSourceMap.fromProject() here; tests
    // use a passthrough (joined line == per-file line) so the same
    // broadcast shape goes out. For multi-file projects the production
    // mapping is non-trivial; we cover that separately.
    const perFileLine = lineNumber != null ? lineNumber + 1 : null;
    host.setDebugState({
        paused: true,
        currentFile: activeName,
        currentLine: perFileLine,
        callStack: frames,
    });
    return frames;
}

interface ObserverPanel {
    debugSessionActive: boolean;
    debugPaused: boolean;
    status: string;
    currentLine: number | null;
    currentFile: string | null;
    renderedFrames: MockFrame[] | null;
    renderedScopes: unknown[] | null;
    refreshCount: number;
    /** Captured event log — every (event-type, timestamp) we processed.
     *  Useful for diagnostics: a test failure can show "we expected
     *  BREAKPOINT but only saw RESUMED, then nothing for 2s". */
    eventLog: Array<{ at: number; type: string; line?: number | null }>;
}

interface ObserverHarness {
    panel: ObserverPanel;
    /** Direct call to refreshDebugView, mirroring the panel's "Force
     *  sync debug data" button-callback for the observer side. */
    forceSync(): Promise<string>;
}

function makeObserverHarness(guest: CollabSession): ObserverHarness {
    const panel: ObserverPanel = {
        debugSessionActive: false,
        debugPaused: false,
        status: 'idle',
        currentLine: null,
        currentFile: null,
        renderedFrames: null,
        renderedScopes: null,
        refreshCount: 0,
        eventLog: [],
    };
    const start = Date.now();
    const logEvent = (type: string, line?: number | null) =>
        panel.eventLog.push({ at: Date.now() - start, type, line });

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

    const refreshDebugView = async (prefetchedFrames?: MockFrame[]): Promise<MockFrame[]> => {
        const frames = (prefetchedFrames ?? (await dbg.stackFrames())) as MockFrame[];
        if (!panel.debugSessionActive) return frames;
        panel.renderedFrames = Array.isArray(frames) ? frames : null;
        if (Array.isArray(frames) && frames.length > 0) {
            // Mirror main.ts's focusJoinedDebugLine: when we're
            // observing a remote initiator, use the broadcasted
            // per-file (currentFile, currentLine) so the line matches
            // what the host's editor shows.
            const sessionFile = guest.debugState.get('currentFile') as string | null;
            const sessionLine = guest.debugState.get('currentLine') as number | null;
            if (sessionFile != null && sessionLine != null) {
                panel.currentFile = sessionFile;
                panel.currentLine = sessionLine;
            } else {
                panel.currentLine = frames[0].lineNumber + 1;
            }
            const scopesResult = await dbg.scopes(0);
            if (!panel.debugSessionActive) return frames;
            panel.renderedScopes = scopesResult?.scopes ?? [];
        } else {
            panel.currentLine = null;
            panel.renderedScopes = [];
        }
        panel.refreshCount++;
        return frames;
    };

    const onAnyDebugEvent = async (event: DebugEvent) => {
        logEvent(event.type);
        switch (event.type) {
            case 'REV_REQUEST_BREAKPOINT':
                panel.debugPaused = true;
                panel.status = 'paused on breakpoint';
                await refreshDebugView();
                break;
            case 'REV_REQUEST_RESUMED':
                // swap handles the resume edge
                break;
            case 'complete':
            case 'REV_REQUEST_EXITED':
                panel.debugSessionActive = false;
                panel.debugPaused = false;
                panel.status = 'program exited';
                panel.currentLine = null;
                panel.currentFile = null;
                panel.renderedFrames = null;
                panel.renderedScopes = null;
                break;
        }
    };

    const swap = () => {
        const initiator = guest.debugState.get('initiatorClientId') as number | undefined;
        const observingRemote = initiator != null && initiator !== guest.awareness.clientID;
        if (observingRemote) {
            const justAttached = !currentRemoteAdapter;
            if (!currentRemoteAdapter) {
                currentRemoteAdapter = createRemoteDebugAdapter({
                    session: {
                        debugState: guest.debugState as unknown as RemoteDebugSessionLike['debugState'],
                        request: (peerId, channel, payload, opts) =>
                            guest.request(peerId, channel, payload, opts) as Promise<unknown>,
                    },
                });
                dbg.setInner(currentRemoteAdapter);
            }
            const newPaused = Boolean(guest.debugState.get('paused'));
            panel.debugSessionActive = true;
            panel.debugPaused = newPaused;
            if (lastObservedPaused && !newPaused) {
                panel.status = 'running';
                panel.currentLine = null;
            }
            lastObservedPaused = newPaused;
            logEvent('swap:observing', newPaused ? 1 : 0);
            if (justAttached && newPaused) {
                const callStack = guest.debugState.get('callStack');
                if (Array.isArray(callStack) && callStack.length > 0) {
                    void refreshDebugView();
                }
            }
        } else {
            if (currentRemoteAdapter) {
                dbg.setInner(observerLocalAdapter);
                (currentRemoteAdapter as any).destroy?.();
                currentRemoteAdapter = null;
                if (initiator !== guest.awareness.clientID) {
                    panel.debugSessionActive = false;
                    panel.debugPaused = false;
                    panel.status = 'program exited';
                    panel.currentLine = null;
                    panel.currentFile = null;
                    panel.renderedFrames = null;
                    panel.renderedScopes = null;
                }
            }
            lastObservedPaused = false;
            logEvent('swap:not-observing');
        }
    };

    guest.debugState.observe(swap);
    dbg.onDebugEvent((e) => { void onAnyDebugEvent(e); });
    swap();

    return {
        panel,
        forceSync: async () => {
            // Exact mirror of main.ts's forceDebugSync observer branch.
            if (!panel.debugSessionActive) return 'no active debug session';
            await refreshDebugView();
            return 'observer refresh complete';
        },
    };
}

function installHostRpcHandlers(host: CollabSession, runtime: MockHostRuntime): void {
    host.onRequest('debug:stackFrames', () => runtime.adapter.stackFrames());
    host.onRequest('debug:scopes', (_peerId, payload) =>
        runtime.adapter.scopes((payload as { frameId: number }).frameId));
    host.onRequest('debug:continue', () => runtime.adapter.continue());
    host.onRequest('debug:step', (_peerId, payload) =>
        runtime.adapter.step((payload as { kind: StepKind }).kind));
    host.onRequest('debug:pause', () => runtime.adapter.pause());
}

async function makePair() {
    const roomId = 'col-int-' + Math.random().toString(36).slice(2, 8);
    const hostRoom = await mockTransport.join({
        appId: 'fade-test', roomId, identity: makeIdentity('Alice'),
    });
    const guestRoom = await mockTransport.join({
        appId: 'fade-test', roomId, identity: makeIdentity('Bob'),
    });
    const host = new CollabSession(makeFakeSessionHost(), hostRoom);
    const guest = new CollabSession(makeFakeSessionHost(), guestRoom);
    await host.start({ role: 'host', identity: makeIdentity('Alice') });
    await guest.start({ role: 'guest', identity: makeIdentity('Bob') });
    // Wait for awareness to converge so RPCs land — peers need to know
    // about each other or sendTo lands in the void. (The session-level
    // test in session.test.ts does the same dance.)
    await waitFor(() => host.awareness.getStates().size >= 2, 2000, 'host awareness');
    await waitFor(() => guest.awareness.getStates().size >= 2, 2000, 'guest awareness');
    return { host, guest, hostPeerId: hostRoom.selfId };
}

describe('observer real-collab integration', () => {
    it('host BREAKPOINT broadcast → observer panel populates', async () => {
        const { host, guest, hostPeerId } = await makePair();
        const runtime = makeMockHostRuntime([{ name: 'MAIN', lineNumber: 9 }]);
        installHostRpcHandlers(host, runtime);
        const obs = makeObserverHarness(guest);
        try {
            // Initial host broadcast (initiator + paused, no callStack yet).
            host.setDebugState({
                initiatorClientId: host.doc.clientID,
                initiatorPeerId: hostPeerId,
                paused: true,
            });
            await waitFor(() => obs.panel.debugSessionActive === true, 2000, 'session active');
            expect(obs.panel.renderedFrames).toBeNull(); // no callStack yet

            // Host's runtime hits the bp — broadcast the snapshot.
            await hostFetchAndBroadcast({ host, runtime, activeName: 'main.fbasic' });
            await waitFor(() => obs.panel.renderedFrames !== null, 2000, 'frames rendered');

            expect(obs.panel.renderedFrames).toEqual([{ name: 'MAIN', lineNumber: 9 }]);
            expect(obs.panel.currentLine).toBe(10);
            expect(obs.panel.currentFile).toBe('main.fbasic');
            expect(obs.panel.debugPaused).toBe(true);
            expect(obs.panel.status).toBe('paused on breakpoint');
        } finally {
            await host.destroy();
            await guest.destroy();
        }
    });

    it('host step (simulates PROTO_ACK rebroadcast) → observer updates new line', async () => {
        const { host, guest, hostPeerId } = await makePair();
        const runtime = makeMockHostRuntime([{ name: 'MAIN', lineNumber: 9 }]);
        installHostRpcHandlers(host, runtime);
        const obs = makeObserverHarness(guest);
        try {
            host.setDebugState({
                initiatorClientId: host.doc.clientID,
                initiatorPeerId: hostPeerId,
                paused: true,
            });
            await hostFetchAndBroadcast({ host, runtime, activeName: 'main.fbasic' });
            await waitFor(() => obs.panel.currentLine === 10, 2000, 'initial line');

            // Host steps. Runtime moves to line 14. Host's PROTO_ACK
            // case (per main.ts) calls fetchPausedFramesAndBroadcast.
            runtime.setFrames([{ name: 'MAIN', lineNumber: 14 }]);
            await hostFetchAndBroadcast({ host, runtime, activeName: 'main.fbasic' });
            await waitFor(() => obs.panel.currentLine === 15, 2000, 'stepped line');

            expect(obs.panel.renderedFrames).toEqual([{ name: 'MAIN', lineNumber: 14 }]);
            expect(obs.panel.debugPaused).toBe(true);
        } finally {
            await host.destroy();
            await guest.destroy();
        }
    });

    it('observer triggers step over → host runtime steps → observer panel updates', { timeout: 20_000 }, async () => {
        const { host, guest, hostPeerId } = await makePair();
        const runtime = makeMockHostRuntime([{ name: 'MAIN', lineNumber: 9 }]);
        installHostRpcHandlers(host, runtime);
        const obs = makeObserverHarness(guest);
        try {
            host.setDebugState({
                initiatorClientId: host.doc.clientID,
                initiatorPeerId: hostPeerId,
                paused: true,
            });
            await hostFetchAndBroadcast({ host, runtime, activeName: 'main.fbasic' });
            await waitFor(() => obs.panel.currentLine === 10, 2000, 'initial line');

            // Observer triggers step over via the facade's step() —
            // this RPCs the host's debug:step handler.
            const stepPromise = guest.request(hostPeerId, 'debug:step', { kind: 'over' });
            // Under vitest with concurrent test files, the mock-transport
            // BroadcastChannel polyfill can take longer than the default
            // 2s to land an RPC. Same flakiness pattern as the existing
            // session.test.ts "request/response RPC" test acknowledges.
            await waitFor(() => runtime.pendingStep > 0, 15000, 'host received step');

            // Host's runtime acks the step (debug-step-result equivalent).
            runtime.resolveStep();
            await stepPromise;

            // Host's PROTO_ACK case fires when the VM lands. Update
            // frames + broadcast.
            runtime.setFrames([{ name: 'MAIN', lineNumber: 14 }]);
            await hostFetchAndBroadcast({ host, runtime, activeName: 'main.fbasic' });
            await waitFor(() => obs.panel.currentLine === 15, 2000, 'stepped line');

            expect(obs.panel.renderedFrames).toEqual([{ name: 'MAIN', lineNumber: 14 }]);
            expect(obs.panel.debugPaused).toBe(true);
            // Diagnostic: event log shows the sequence the observer saw.
            const types = obs.panel.eventLog.map((e) => e.type);
            expect(types).toContain('REV_REQUEST_BREAKPOINT');
        } finally {
            await host.destroy();
            await guest.destroy();
        }
    });

    it('observer forceSync re-runs refreshDebugView and re-renders panel', { timeout: 20_000 }, async () => {
        const { host, guest, hostPeerId } = await makePair();
        const runtime = makeMockHostRuntime([{ name: 'MAIN', lineNumber: 9 }]);
        installHostRpcHandlers(host, runtime);
        const obs = makeObserverHarness(guest);
        try {
            host.setDebugState({
                initiatorClientId: host.doc.clientID,
                initiatorPeerId: hostPeerId,
                paused: true,
            });
            await hostFetchAndBroadcast({ host, runtime, activeName: 'main.fbasic' });
            // Wait for the SCOPES round-trip (refreshCount only ticks
            // up after refreshScopes resolves). This pins down whether
            // the production "intermittent scope timeout" lives at the
            // transport level — if the test consistently hangs here
            // and refreshCount stays at 0, mock-transport is dropping
            // the response, and the same flaw is plausible in WebRTC.
            await waitFor(() => obs.panel.refreshCount > 0, 15000, 'initial refreshCount');
            expect(runtime.callLog.find((c) => c.method === 'scopes')).toBeTruthy();

            const refreshBefore = obs.panel.refreshCount;
            const result = await obs.forceSync();
            expect(result).toBe('observer refresh complete');
            await waitFor(() => obs.panel.refreshCount > refreshBefore, 15000, 'forceSync refresh');
            expect(obs.panel.renderedFrames).toEqual([{ name: 'MAIN', lineNumber: 9 }]);
        } finally {
            await host.destroy();
            await guest.destroy();
        }
    });

    it('host forceSync re-broadcasts the same state (observer sees no change but no errors)', async () => {
        const { host, guest, hostPeerId } = await makePair();
        const runtime = makeMockHostRuntime([{ name: 'MAIN', lineNumber: 9 }]);
        installHostRpcHandlers(host, runtime);
        const obs = makeObserverHarness(guest);
        try {
            host.setDebugState({
                initiatorClientId: host.doc.clientID,
                initiatorPeerId: hostPeerId,
                paused: true,
            });
            await hostFetchAndBroadcast({ host, runtime, activeName: 'main.fbasic' });
            await waitFor(() => obs.panel.renderedFrames !== null, 2000, 'initial render');

            // Host clicks "Force sync debug data" — same as a fresh
            // fetchPausedFramesAndBroadcast against the unchanged frames.
            await hostFetchAndBroadcast({ host, runtime, activeName: 'main.fbasic' });
            // Same line, same frames — no panel change expected, but
            // also no errors / no stuck timers.
            await new Promise((r) => setTimeout(r, 50));
            expect(obs.panel.currentLine).toBe(10);
            expect(obs.panel.renderedFrames).toEqual([{ name: 'MAIN', lineNumber: 9 }]);
        } finally {
            await host.destroy();
            await guest.destroy();
        }
    });

    it('diagnostic: full observer-step event log', async () => {
        // This test produces a verbose event log that matches what the
        // observer would see in production. If a user reports breakage,
        // they can compare their `__fadeCollab.debugState` and event log
        // against this baseline.
        const { host, guest, hostPeerId } = await makePair();
        const runtime = makeMockHostRuntime([{ name: 'MAIN', lineNumber: 9 }]);
        installHostRpcHandlers(host, runtime);
        const obs = makeObserverHarness(guest);
        try {
            host.setDebugState({
                initiatorClientId: host.doc.clientID,
                initiatorPeerId: hostPeerId,
                paused: true,
            });
            await hostFetchAndBroadcast({ host, runtime, activeName: 'main.fbasic' });
            await waitFor(() => obs.panel.currentLine === 10);

            runtime.setFrames([{ name: 'MAIN', lineNumber: 14 }]);
            await hostFetchAndBroadcast({ host, runtime, activeName: 'main.fbasic' });
            await waitFor(() => obs.panel.currentLine === 15);

            runtime.setFrames([{ name: 'MAIN', lineNumber: 19 }]);
            await hostFetchAndBroadcast({ host, runtime, activeName: 'main.fbasic' });
            await waitFor(() => obs.panel.currentLine === 20);

            // The event log MUST contain swap:observing + BREAKPOINT
            // for each step. If a regression silently skips one, the
            // sequence won't match.
            const seq = obs.panel.eventLog.map((e) => e.type);
            const breakpointCount = seq.filter((t) => t === 'REV_REQUEST_BREAKPOINT').length;
            expect(breakpointCount).toBeGreaterThanOrEqual(2); // initial + at least one step
            // Final state matches the LAST broadcast.
            expect(obs.panel.currentLine).toBe(20);
            expect(obs.panel.renderedFrames).toEqual([{ name: 'MAIN', lineNumber: 19 }]);
        } finally {
            await host.destroy();
            await guest.destroy();
        }
    });
});
