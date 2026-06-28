// Local DebugAdapter — wraps the existing runner (web) + monoGameHost
// (monogame) debug surfaces. Drop-in replacement for the pre-existing
// `dbg` object literal in main.ts; behaviour is identical, the only
// changes are:
//
//   1. The dispatch is encapsulated as a class so a future RemoteDebugAdapter
//      can implement the same interface and be swapped in by Phase B.
//   2. The adapter owns the runner.onDebugEvent / monoGameHost.onDebugEvent
//      assignments, fanning events out to subscribers. main.ts (and the
//      Playwright test hook) subscribe via `adapter.onDebugEvent(...)`
//      instead of overwriting the runtime's single handler slot.
//   3. The adapter maintains a passive status/paused/currentLocation
//      mirror by sniffing event types. The authoritative debug state
//      still lives in main.ts; these accessors exist for the future
//      remote adapter parity and as cheap "is this thing running"
//      checks.

import type {
    DebugAdapter,
    DebugEvent,
    DebugStatus,
    ResolvedInstruction,
    StepKind,
} from './adapter';

// Both shapes mirror the existing `dbg` object literal's calls with
// `any` returns — same rationale as the adapter interface itself. They
// are intentionally loose so the concrete Runner / MonoGameHost classes
// satisfy them without TypeScript squinting at minor variance like
// `Promise<void>` vs `Promise<any>`.

/** Subset of the worker-backed `Runner` class that the adapter calls. */
export interface RunnerLike {
    debugStart(source: string): Promise<any>;
    debugStartTest(source: string, testName: string): Promise<any>;
    debugContinue(): Promise<any>;
    debugPause(): Promise<any>;
    debugStep(kind: StepKind): Promise<any>;
    debugTerminate(): Promise<any>;
    debugSetBreakpoints(payload: any): Promise<any>;
    debugStackFrames(): Promise<any>;
    debugScopes(frameId: number): Promise<any>;
    debugExpandVariable(variableId: number): Promise<any>;
    debugEval(frameId: number, expression: string): Promise<any>;
    debugRepl(frameId: number, code: string): Promise<any>;
    debugSetVariable(frameId: number, variableId: number, rhs: string): Promise<any>;
    resolveInstruction(insIndex: number): Promise<ResolvedInstruction | null>;
    // `any` rather than DebugEvent because the runner/monogame-host
    // classes type their event with a stricter required-field shape; an
    // `any` here lets both concrete types structurally satisfy this
    // interface without forcing them to relax their declarations.
    onDebugEvent?: ((event: any) => void) | null;
}

/** Subset of the monogame-host bridge — note inspection responses come as
 *  JSON strings here; the adapter parses them so callers see the same
 *  shape as the runner's pre-parsed responses. */
export interface MonoGameHostLike {
    debugStart(source: string): Promise<string>;
    debugStartTest(source: string, testName: string): Promise<string>;
    debugContinue(): Promise<any>;
    debugPause(): Promise<any>;
    debugStep(kind: StepKind): Promise<any>;
    debugTerminate(): Promise<any>;
    debugSetBreakpoints(payloadJson: string): Promise<any>;
    debugStackFrames(): Promise<string>;
    debugScopes(frameId: number): Promise<string>;
    debugVariableExpansion(variableId: number): Promise<string>;
    debugEval(frameId: number, expression: string): Promise<string>;
    debugRepl(frameId: number, code: string): Promise<string>;
    debugSetVariable(frameId: number, variableId: number, rhs: string): Promise<string>;
    resolveInstruction(insIndex: number): Promise<ResolvedInstruction | null>;
    // `any` rather than DebugEvent because the runner/monogame-host
    // classes type their event with a stricter required-field shape; an
    // `any` here lets both concrete types structurally satisfy this
    // interface without forcing them to relax their declarations.
    onDebugEvent?: ((event: any) => void) | null;
}

export interface LocalDebugAdapterOptions {
    runner: RunnerLike;
    monoGameHost: MonoGameHostLike;
    /** Resolve the active project type fresh on every dispatch — project
     *  changes during a session are possible and we want the routing to
     *  track them. Returns 'monogame' for monogame projects; everything
     *  else (web, null/unset) routes to the web runner. */
    getProjectType: () => 'web' | 'monogame' | null;
    /** Boot the web preview iframe before a web debug session. Optional;
     *  if omitted, callers are responsible for arming it themselves. */
    ensureWebVmReady?: () => Promise<void>;
    /** Push OPFS assets into the monogame iframe before a monogame debug
     *  session. Optional; same caller-responsibility note as above. */
    syncMonoGameAssets?: () => Promise<void>;
}

class LocalDebugAdapter implements DebugAdapter {
    readonly kind = 'local' as const;

    private readonly runner: RunnerLike;
    private readonly monoGameHost: MonoGameHostLike;
    private readonly getProjectType: () => 'web' | 'monogame' | null;
    private readonly ensureWebVmReady?: () => Promise<void>;
    private readonly syncMonoGameAssets?: () => Promise<void>;

    private _status: DebugStatus = 'idle';
    private _paused = false;
    private _currentLocation: { file: string; line: number } | null = null;
    private readonly subscribers = new Set<(event: DebugEvent) => void>();

    constructor(opts: LocalDebugAdapterOptions) {
        this.runner = opts.runner;
        this.monoGameHost = opts.monoGameHost;
        this.getProjectType = opts.getProjectType;
        this.ensureWebVmReady = opts.ensureWebVmReady;
        this.syncMonoGameAssets = opts.syncMonoGameAssets;

        // Take ownership of the runtime onDebugEvent slots. Callers must
        // subscribe via `this.onDebugEvent(...)` from now on — anyone
        // who assigns to `runner.onDebugEvent` directly after the
        // adapter is constructed will silently shadow our forwarder.
        this.runner.onDebugEvent = (event) => this.dispatchEvent(event);
        this.monoGameHost.onDebugEvent = (event) => this.dispatchEvent(event);
    }

    private dispatchEvent(event: DebugEvent): void {
        this.updateStateMirror(event);
        for (const cb of this.subscribers) {
            try { cb(event); }
            catch (e) { console.error('[local-debug-adapter] subscriber threw', e); }
        }
    }

    /** Best-effort state tracking. The main.ts debug code is the
     *  authoritative source for the run/debug flags it uses to gate UI;
     *  this mirror exists so the adapter interface can offer
     *  `status` / `paused` / `currentLocation` to consumers that want
     *  them without coupling back to main.ts internals. */
    private updateStateMirror(event: DebugEvent): void {
        switch (event?.type) {
            case 'REV_REQUEST_BREAKPOINT':
                this._status = 'paused';
                this._paused = true;
                break;
            case 'REV_REQUEST_EXITED':
            case 'complete':
                this._status = 'completed';
                this._paused = false;
                this._currentLocation = null;
                break;
            // REV_REQUEST_EXPLODE and other transient events don't carry
            // state we'd want to mirror — leave existing fields alone.
        }
    }

    // ── Lifecycle ────────────────────────────────────────────────────────

    async start(source: string): Promise<any> {
        this._status = 'starting';
        if (this.getProjectType() === 'monogame') {
            // Push assets first; the canvas runtime needs the dict
            // populated *before* the user program's texture/sfx commands
            // run inside Game1.LoadProgram.
            if (this.syncMonoGameAssets) await this.syncMonoGameAssets();
            const s = await this.monoGameHost.debugStart(source);
            return JSON.parse(s);
        }
        if (this.ensureWebVmReady) await this.ensureWebVmReady();
        return this.runner.debugStart(source);
    }

    async startTest(source: string, testName: string): Promise<any> {
        this._status = 'starting';
        if (this.getProjectType() === 'monogame') {
            if (this.syncMonoGameAssets) await this.syncMonoGameAssets();
            const s = await this.monoGameHost.debugStartTest(source, testName);
            return JSON.parse(s);
        }
        if (this.ensureWebVmReady) await this.ensureWebVmReady();
        return this.runner.debugStartTest(source, testName);
    }

    terminate(): Promise<any> {
        return this.getProjectType() === 'monogame'
            ? this.monoGameHost.debugTerminate()
            : this.runner.debugTerminate();
    }

    // ── Control ──────────────────────────────────────────────────────────

    continue(): Promise<any> {
        return this.getProjectType() === 'monogame'
            ? this.monoGameHost.debugContinue()
            : this.runner.debugContinue();
    }

    pause(): Promise<any> {
        return this.getProjectType() === 'monogame'
            ? this.monoGameHost.debugPause()
            : this.runner.debugPause();
    }

    step(kind: StepKind): Promise<any> {
        return this.getProjectType() === 'monogame'
            ? this.monoGameHost.debugStep(kind)
            : this.runner.debugStep(kind);
    }

    // ── Breakpoints ──────────────────────────────────────────────────────

    setBreakpoints(payload: unknown): Promise<any> {
        return this.getProjectType() === 'monogame'
            ? this.monoGameHost.debugSetBreakpoints(JSON.stringify(payload))
            : this.runner.debugSetBreakpoints(payload);
    }

    // ── Inspection ───────────────────────────────────────────────────────

    stackFrames(): Promise<any> {
        return this.getProjectType() === 'monogame'
            ? this.monoGameHost.debugStackFrames().then((s) => JSON.parse(s))
            : this.runner.debugStackFrames();
    }

    scopes(frameId: number): Promise<any> {
        return this.getProjectType() === 'monogame'
            ? this.monoGameHost.debugScopes(frameId).then((s) => JSON.parse(s))
            : this.runner.debugScopes(frameId);
    }

    expandVariable(variableId: number): Promise<any> {
        return this.getProjectType() === 'monogame'
            ? this.monoGameHost.debugVariableExpansion(variableId).then((s) => JSON.parse(s))
            : this.runner.debugExpandVariable(variableId);
    }

    eval(frameId: number, expression: string): Promise<any> {
        return this.getProjectType() === 'monogame'
            ? this.monoGameHost.debugEval(frameId, expression).then((s) => JSON.parse(s))
            : this.runner.debugEval(frameId, expression);
    }

    repl(frameId: number, code: string): Promise<any> {
        return this.getProjectType() === 'monogame'
            ? this.monoGameHost.debugRepl(frameId, code).then((s) => JSON.parse(s))
            : this.runner.debugRepl(frameId, code);
    }

    setVariable(frameId: number, variableId: number, rhs: string): Promise<any> {
        return this.getProjectType() === 'monogame'
            ? this.monoGameHost.debugSetVariable(frameId, variableId, rhs).then((s) => JSON.parse(s))
            : this.runner.debugSetVariable(frameId, variableId, rhs);
    }

    resolveInstruction(insIndex: number): Promise<ResolvedInstruction | null> {
        return this.getProjectType() === 'monogame'
            ? this.monoGameHost.resolveInstruction(insIndex)
            : this.runner.resolveInstruction(insIndex);
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

export function createLocalDebugAdapter(opts: LocalDebugAdapterOptions): DebugAdapter {
    return new LocalDebugAdapter(opts);
}
