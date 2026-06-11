// Remote DebugAdapter — sits on the OBSERVER side of a live session and
// presents the same surface as the local adapter, but:
//
//   * Reads state (status, paused, currentLocation, stack frames) from
//     the shared `session.debugState` Y.Map — the host writes there on
//     every pause/step.
//
//   * Routes commands (continue / pause / step / setBreakpoints / eval
//     / etc.) over `session.request(initiatorPeerId, 'debug:<name>', …)`.
//     The host has matching `session.onRequest('debug:<name>', …)`
//     handlers that forward to its local debug runtime.
//
// `start()` / `startTest()` are deliberately not supported remotely — the
// host owns session lifecycle; observers can only attach to a session
// the host already initiated. `terminate()` is allowed because there are
// legitimate "stop the shared session" affordances.
//
// State snapshot fields are populated lazily from the Y.Map; on every
// observed change we synthesise a minimal `DebugEvent` and forward it to
// subscribers so the existing main.ts onAnyDebugEvent handler reacts
// the same way it would to a local runtime event.

import type {
    DebugAdapter,
    DebugEvent,
    DebugStatus,
    ResolvedInstruction,
    StepKind,
} from './adapter';

/** Minimal slice of CollabSession that the remote adapter needs. Declared
 *  structurally so we don't have to import the concrete session class
 *  here — keeps this module easy to unit-test against a stub session. */
export interface RemoteDebugSessionLike {
    /** Y.Map<any> — fields the host writes:
     *    initiatorClientId  (number)   — Yjs awareness clientID of host
     *    initiatorPeerId    (string)   — transport-level peer ID of host
     *    paused             (boolean)
     *    currentFile        (string|null)
     *    currentLine        (number|null)
     *    callStack          (Frame[])
     */
    readonly debugState: {
        get(key: string): unknown;
        observe(cb: () => void): void;
        unobserve(cb: () => void): void;
    };
    request(
        peerId: string,
        channel: string,
        payload: unknown,
        opts?: { timeoutMs?: number },
    ): Promise<unknown>;
}

export interface RemoteDebugAdapterOptions {
    session: RemoteDebugSessionLike;
    /** Optional override for RPC timeouts — debug commands should generally
     *  resolve quickly (the host runs them locally and returns), but slow
     *  networks + the host's own debug overhead can push response time
     *  past Trystero's default. 15s is a comfortable upper bound for
     *  step/continue; expression evaluation gets the same. */
    rpcTimeoutMs?: number;
}

class RemoteDebugAdapter implements DebugAdapter {
    readonly kind = 'remote' as const;

    private readonly session: RemoteDebugSessionLike;
    private readonly rpcTimeoutMs: number;
    private readonly subscribers = new Set<(event: DebugEvent) => void>();
    private readonly onChange = () => this.refreshFromDebugState();

    private _status: DebugStatus = 'idle';
    private _paused = false;
    private _currentLocation: { file: string; line: number } | null = null;
    private _stackFrames: unknown[] = [];

    constructor(opts: RemoteDebugAdapterOptions) {
        this.session = opts.session;
        this.rpcTimeoutMs = opts.rpcTimeoutMs ?? 15_000;
        this.session.debugState.observe(this.onChange);
        // Prime from whatever the debugState already holds at construction.
        this.refreshFromDebugState();
    }

    /** Disconnect from the session's debugState. Called by main.ts when
     *  the remote debug initiator goes away (host stopped debugging) and
     *  we swap back to the local adapter. */
    destroy(): void {
        this.session.debugState.unobserve(this.onChange);
        this.subscribers.clear();
    }

    private refreshFromDebugState(): void {
        const d = this.session.debugState;
        const wasPaused = this._paused;
        const wasStatus = this._status;
        const wasFile = this._currentLocation?.file ?? null;
        const wasLine = this._currentLocation?.line ?? null;

        const initiatorClientId = d.get('initiatorClientId');
        const active = initiatorClientId != null;

        this._paused = Boolean(d.get('paused'));
        const file = d.get('currentFile') as string | null;
        const line = d.get('currentLine') as number | null;
        this._currentLocation = file != null && line != null ? { file, line } : null;
        this._stackFrames = (d.get('callStack') as unknown[]) ?? [];

        if (!active) {
            this._status = 'idle';
        } else if (this._paused) {
            this._status = 'paused';
        } else {
            this._status = 'running';
        }

        // Synthesise minimal events so main.ts's onAnyDebugEvent handler
        // — which drives the panel — fires on the same transitions it
        // would for a local runtime. We don't try to invent rich event
        // payloads here; the handler reads adapter state directly when
        // it needs frames/locals.
        //
        // The pause edge is more than just paused: false → true. When
        // the host steps, the *runtime* flow is paused → running → paused
        // at the new line, but the host's `onAnyDebugEvent` only fires
        // broadcastDebugState on the BREAKPOINT (final landing). The
        // observer's transient `paused: false` write (from the host RPC
        // handler) does come through, but the window is so small that
        // both updates may land within the same observation tick — in
        // which case `paused` appears to stay true and only the location
        // changes. So we also treat "still paused, but at a different
        // file/line" as a pause edge.
        const locationChangedWhilePaused =
            wasPaused && this._paused && (file !== wasFile || line !== wasLine);
        const isPauseEdge = (!wasPaused && this._paused) || locationChangedWhilePaused;
        // Gate the synthesized BREAKPOINT on having a populated call
        // stack to render. The host's startDebug path broadcasts
        // {paused:true, initiator} BEFORE the program hits its first
        // breakpoint — that initial broadcast has no callStack key. If
        // we emit BREAKPOINT for it, main.ts's onAnyDebugEvent will run
        // refreshDebugView → dbg.stackFrames → cache miss → 15s-timeout
        // RPC to a host whose iframe is busy running the program. The
        // subsequent BREAKPOINT broadcast (with callStack) will trigger
        // a real refresh via the locationChangedWhilePaused branch.
        const hasFrames = this._stackFrames.length > 0;
        if (isPauseEdge && hasFrames) {
            this.emit({ type: 'REV_REQUEST_BREAKPOINT' });
        } else if (wasPaused && !this._paused && active) {
            // Resumed from a pause — host stepped or continued.
            this.emit({ type: 'REV_REQUEST_RESUMED' });
        } else if (wasStatus !== 'idle' && this._status === 'idle') {
            this.emit({ type: 'complete' });
        }
    }

    private emit(event: DebugEvent): void {
        for (const cb of this.subscribers) {
            try { cb(event); }
            catch (e) { console.error('[remote-debug-adapter] subscriber threw', e); }
        }
    }

    private getInitiatorPeerId(): string {
        const peerId = this.session.debugState.get('initiatorPeerId') as string | undefined;
        if (!peerId) throw new Error('debug initiator is not known (no active host)');
        return peerId;
    }

    private async rpc(channel: string, payload: unknown): Promise<any> {
        // Async wrapper so the "no initiator" lookup error surfaces as a
        // rejected promise (matching every other failure mode) rather
        // than a synchronous throw that callers writing
        // `await adapter.continue()` wouldn't expect to catch via .catch().
        const peerId = this.getInitiatorPeerId();
        return this.session.request(peerId, channel, payload, { timeoutMs: this.rpcTimeoutMs });
    }

    // ── Lifecycle ────────────────────────────────────────────────────────

    start(_source: string): Promise<any> {
        return Promise.reject(new Error('start() is host-only — observers cannot launch a debug session'));
    }

    startTest(_source: string, _testName: string): Promise<any> {
        return Promise.reject(new Error('startTest() is host-only — observers cannot launch a debug session'));
    }

    terminate(): Promise<any> {
        return this.rpc('debug:terminate', null);
    }

    // ── Control ──────────────────────────────────────────────────────────

    continue(): Promise<any> { return this.rpc('debug:continue', null); }
    pause(): Promise<any> { return this.rpc('debug:pause', null); }
    step(kind: StepKind): Promise<any> { return this.rpc('debug:step', { kind }); }

    // ── Breakpoints ──────────────────────────────────────────────────────

    setBreakpoints(payload: any): Promise<any> {
        return this.rpc('debug:setBreakpoints', { payload });
    }

    // ── Inspection ───────────────────────────────────────────────────────
    // For stack frames we prefer the in-Y.Doc snapshot (zero round-trip,
    // already current as of the last pause). Everything else round-trips
    // because the host has the live data — locals/scopes can change
    // between snapshots, and `expandVariable` walks lazily-materialised
    // sub-trees that are too heavy to push pre-emptively.

    async stackFrames(): Promise<any> {
        // Return the array directly (NOT wrapped in `{stackFrames: [...]}`)
        // — that's what the runner / local adapter returns, and the
        // playground's `refreshDebugView` does `frames.length > 0` on
        // the result. Wrapping it would make `.length` undefined, the
        // condition false, and the locals/scopes fetch never fires.
        if (this._stackFrames.length > 0) {
            return this._stackFrames;
        }
        const res = await this.rpc('debug:stackFrames', null);
        // The host-side RPC handler proxies to localDebugAdapter.stackFrames()
        // which already returns the array shape; pass-through.
        return res;
    }

    scopes(frameId: number): Promise<any> {
        return this.rpc('debug:scopes', { frameId });
    }

    expandVariable(variableId: number): Promise<any> {
        return this.rpc('debug:expandVariable', { variableId });
    }

    eval(frameId: number, expression: string): Promise<any> {
        return this.rpc('debug:eval', { frameId, expression });
    }

    repl(frameId: number, code: string): Promise<any> {
        return this.rpc('debug:repl', { frameId, code });
    }

    setVariable(frameId: number, variableId: number, rhs: string): Promise<any> {
        return this.rpc('debug:setVariable', { frameId, variableId, rhs });
    }

    resolveInstruction(insIndex: number): Promise<ResolvedInstruction | null> {
        return this.rpc('debug:resolveInstruction', { insIndex });
    }

    // ── State snapshot ───────────────────────────────────────────────────

    get status(): DebugStatus { return this._status; }
    get paused(): boolean { return this._paused; }
    get currentLocation(): { file: string; line: number } | null { return this._currentLocation; }

    // ── Event subscription ───────────────────────────────────────────────

    onDebugEvent(handler: (event: DebugEvent) => void): () => void {
        this.subscribers.add(handler);
        return () => this.subscribers.delete(handler);
    }
}

export function createRemoteDebugAdapter(
    opts: RemoteDebugAdapterOptions,
): DebugAdapter & { destroy(): void } {
    return new RemoteDebugAdapter(opts);
}
