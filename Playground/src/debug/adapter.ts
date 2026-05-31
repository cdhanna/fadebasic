// Debug adapter interface. The contract everything in the playground's
// debug UI sees, regardless of whether the actual debug runtime is in
// this browser ("local" — runner / monoGameHost iframe) or driven by
// another peer in a live session ("remote" — sit on the receiving end of
// session.debugState + RPC commands).
//
// Phase A scope (this commit): just the local adapter, used as a no-op
// wrapper around the existing runner / monoGameHost dispatch logic. The
// interface is designed for both modes so Phase B (remote adapter) can
// drop in without UI churn.

export type DebugStatus = 'idle' | 'starting' | 'running' | 'paused' | 'completed';
export type StepKind = 'over' | 'in' | 'out';

/** Raw debug event from the underlying runtime. Type-opaque from the
 *  adapter's perspective — the UI / `onAnyDebugEvent` in main.ts inspects
 *  `event.type` + `event.json` to decide what to do. Kept shapeless on
 *  purpose so the runner-side and monogame-side event variants don't
 *  need their schema centralised here (they're already coupled to the
 *  consumer). */
export interface DebugEvent {
    type: string;
    id?: number;
    json?: string;
    [key: string]: unknown;
}

/** Reverse-resolved source coordinates returned by `resolveInstruction`. */
export interface ResolvedInstruction {
    insIndex: number;
    lineNumber: number;
    charNumber: number;
}

export interface DebugAdapter {
    /** Identifier for which implementation is currently active. UI uses
     *  this to gate "you're observing" affordances vs. "you're driving"
     *  ones. */
    readonly kind: 'local' | 'remote';

    // ── Lifecycle ────────────────────────────────────────────────────────

    // Return types are `Promise<any>` throughout the inspection /
    // command surface to match the pre-refactor `dbg` object literal —
    // existing call sites consume runtime-shaped JSON-ish blobs that
    // aren't easy to type narrowly, and tightening this would force a
    // sweep of every reader in main.ts. Keep the `any` here; the cost
    // of stricter types lives at the call sites that already use them.

    /** Start a normal debug session against the given source. Resolves
     *  with the runtime's start response (shape varies; existing code
     *  treats it as a JSON-ish blob). */
    start(source: string): Promise<any>;
    /** Start a debug session focused on a single test by name. */
    startTest(source: string, testName: string): Promise<any>;
    /** Tear the session down. */
    terminate(): Promise<any>;

    // ── Control ──────────────────────────────────────────────────────────

    continue(): Promise<any>;
    pause(): Promise<any>;
    step(kind: StepKind): Promise<any>;

    // ── Breakpoints ──────────────────────────────────────────────────────

    /** Push the current breakpoint set to the runtime. `payload` is the
     *  runtime-specific request shape that runner.debugSetBreakpoints /
     *  monoGameHost.debugSetBreakpoints already accept; callers compose
     *  it once and hand it to whichever adapter is active. */
    setBreakpoints(payload: any): Promise<any>;

    // ── Inspection (queries that fetch state) ─────────────────────────────

    stackFrames(): Promise<any>;
    scopes(frameId: number): Promise<any>;
    expandVariable(variableId: number): Promise<any>;
    eval(frameId: number, expression: string): Promise<any>;
    repl(frameId: number, code: string): Promise<any>;
    setVariable(frameId: number, variableId: number, rhs: string): Promise<any>;
    /** Resolve a VM instruction index to a source line/column. Used by
     *  the crash overlay. Null when there's no active mapping. */
    resolveInstruction(insIndex: number): Promise<ResolvedInstruction | null>;

    // ── State snapshot (cheap reads used to gate UI) ─────────────────────
    // These are passive mirrors maintained by the adapter from its event
    // stream; the source of truth lives wherever the adapter decided
    // (runtime events for local, Y.Map updates for remote). main.ts still
    // owns the "is this peer running a debug session" gating boolean
    // (`debugSessionActive`) and uses these for cross-checks /
    // observability only.

    readonly status: DebugStatus;
    readonly paused: boolean;
    readonly currentLocation: { file: string; line: number } | null;

    // ── Event subscription ───────────────────────────────────────────────

    /** Subscribe to raw debug events. Returns an unsubscribe function.
     *  The local adapter forwards events from runner.onDebugEvent +
     *  monoGameHost.onDebugEvent; the remote adapter (Phase B) will
     *  synthesize equivalent events from session.debugState changes. */
    onDebugEvent(handler: (event: DebugEvent) => void): () => void;
}
