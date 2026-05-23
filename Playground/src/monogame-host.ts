// Bridge that drives WebRuntime.MonoGame from an iframe inside #mg-blazor-root.
//
// History: this file used to inline-mount Blazor into the Playground page,
// injecting nkast.Wasm.* shims and calling Blazor.start() directly. After
// mg-export-3.md phase 3 the monogame template runs in its own iframe at
// /runtime/monogame/index.html?preview=1 — same model the web template
// already uses — and this file is a thin postMessage bridge.
//
// Public surface is unchanged from the inline era: loadProgram /
// listTests / runTests / debug* / registerAsset / clearAssets / stop, plus
// the onDebugEvent callback. main.ts's ~30 call sites don't need to change
// when the implementation under them flips from invokeMethodAsync to
// postMessage.
//
// Wire protocol lives in WebRuntime.MonoGame/wwwroot/index.html's
// preview-mode block:
//
//   parent → iframe:                        iframe → parent reply
//   ──────────────────────────────────────────────────────────────────
//   bootstrap                               preview-armed
//   run-start-source { source, id }         run-tick-result { id, result }
//   stop-run                                (no reply)
//   list-tests { source, id }               list-tests-result { id, result }
//   run-tests { source, testName, id }      run-tests-result { id, result }
//   debug-start { source, id }              debug-start-result { id, result }
//   debug-start-test { source, testName, id } debug-start-test-result { id, result }
//   debug-terminate                         (no reply)
//   debug-set-breakpoints { linesJson }     (no reply)
//   debug-step { kind }                     (no reply)
//   debug-continue                          (no reply)
//   debug-pause                             (no reply)
//   debug-stack-frames { id }               debug-stack-frames-result { id, result }
//   debug-scopes { frameId, id }            debug-scopes-result { id, result }
//   debug-variable-expansion { variableId, id } debug-variable-expansion-result { id, result }
//   debug-eval { frameId, expression, id }  debug-eval-result { id, result }
//   debug-repl { frameId, code, id }        debug-repl-result { id, result }
//   debug-set-variable { frameId, variableId, rhs, id } debug-set-variable-result { id, result }
//   register-asset { name, bytes }          (no reply)
//   clear-assets                            (no reply)
//
// Streaming events the iframe pushes unprompted:
//   debug-event { event: { id, type, json } } — drained from TickDotNet
//                                                 each frame
//   error { id?, message }                  — last-resort surface; the
//                                              corresponding pending
//                                              promise (if any) rejects.
//   game-error { message }                  — fatal tick-loop exception.
//                                              The iframe parks its rAF
//                                              loop; the host page
//                                              forwards to the Output
//                                              panel via onGameError.
//   stdout { line }                         — Console.WriteLine output
//                                              from the running fbasic
//                                              program (and other .NET
//                                              status logs). Forwarded
//                                              via onStdout.
//   stderr { line }                         — console.warn / .error
//                                              output. Forwarded via
//                                              onStderr.

const IFRAME_SRC = '/runtime/monogame/index.html?preview=1';
const IFRAME_ELEMENT_ID = 'mg-preview-frame';

export interface MonoGameTestResult {
    name: string;
    passed: boolean;
    duration: number;
    failureMessage: string | null;
    failureReason: string | null;
    failureSourceText: string | null;
}

export interface MonoGameRunTestsResult {
    passed: number;
    failed: number;
    duration?: number;
    results: MonoGameTestResult[];
    error?: string;
}

export interface MonoGameTestEntry {
    name: string;
    isAbstract: boolean;
    fromParent: string | null;
    sourceLine: number;
    sourceChar: number;
}

interface PendingCall {
    resolve: (result: any) => void;
    reject: (err: Error) => void;
}

class MonoGameHost {
    private bootPromise: Promise<void> | null = null;
    private iframe: HTMLIFrameElement | null = null;
    // Flipped true only AFTER the iframe replies preview-armed, which
    // means its Blazor has mounted and Index.razor.cs's JSInvokables are
    // safe to call. Callers like main.ts's Diagnostics-panel version poll
    // use isReady() to passively skip work until the runtime is up,
    // *without* forcing a boot — so we can't just check `!!this.iframe`
    // (the iframe exists from the moment bootInternal creates it, well
    // before its DOM and Blazor are ready).
    private armed = false;
    private nextCallId = 0;
    private pending = new Map<number, PendingCall>();
    // True once the Playground page's own boot splash has finished hiding.
    // Stored so we can send 'pg-splash-hidden' to the iframe immediately
    // if it boots after the pg-splash has already gone.
    private pgSplashDone = false;

    /** Called by main.ts when the Playground's own boot splash finishes
     *  hiding. Forwards 'pg-splash-hidden' to the iframe so it can unpause
     *  its animations. If the iframe isn't loaded yet the flag is stored and
     *  forwarded as soon as the iframe reports 'preview-ready'. */
    notifyPgSplashHidden(): void {
        this.pgSplashDone = true;
        if (this.iframe?.contentWindow) {
            this.iframe.contentWindow.postMessage({ type: 'pg-splash-hidden' }, '*');
        }
    }

    /** Idempotent — returns the same promise after first call. */
    ensureBooted(): Promise<void> {
        if (this.bootPromise) return this.bootPromise;
        this.bootPromise = this.bootInternal();
        return this.bootPromise;
    }

    /** True only once the iframe has replied preview-armed and round-
     *  trips to the iframe's JSInvokables are safe. False while booting
     *  (so callers that poll without forcing a boot, like the
     *  Diagnostics version-info ticker, don't race the handshake). */
    isReady(): boolean {
        return this.armed;
    }

    private async bootInternal(): Promise<void> {
        const root = document.getElementById('mg-blazor-root');
        if (!root) {
            throw new Error(
                '[monogame-host] #mg-blazor-root not in DOM — open the Game panel before booting.',
            );
        }

        // Hide the splash now that we're booting. Once the iframe armed-up
        // the splash stays hidden; if the iframe ever fails to load the
        // user will see the iframe's own error UI through the transparent
        // frame.
        const splash = document.getElementById('mg-blazor-splash');
        if (splash) splash.style.display = 'none';

        // Create the iframe lazily so the heavy ~8MB monogame WASM doesn't
        // load until the user actually opens a monogame project.
        let frame = document.getElementById(IFRAME_ELEMENT_ID) as HTMLIFrameElement | null;
        if (!frame) {
            frame = document.createElement('iframe');
            frame.id = IFRAME_ELEMENT_ID;
            frame.title = 'MonoGame preview';
            frame.style.border = '0';
            frame.style.width = '100%';
            frame.style.height = '100%';
            frame.style.display = 'block';
            // sandbox without allow-same-origin gives the iframe a null/opaque
            // origin. Chrome's Site Isolation puts null-origin frames in a
            // separate renderer process, so the MonoGame game loop (TickDotNet +
            // KNI GL rendering) no longer competes with Monaco on the main thread.
            // postMessage still works across the boundary — monogame-host.ts
            // already uses '*' as the target origin throughout.
            // allow-same-origin is intentionally omitted; that's what triggers
            // process isolation. In production the runtime loads standalone (no
            // parent Playground page), so no sandbox is needed there.
            frame.setAttribute('sandbox', 'allow-scripts allow-pointer-lock allow-fullscreen allow-autoplay');
            // Append to the content wrapper (below the game toolbar) so the
            // iframe fills #mg-game-content rather than the full #mg-blazor-root.
            const content = document.getElementById('mg-game-content') ?? root;
            content.appendChild(frame);
        }
        this.iframe = frame;

        // Single delegate that fans every iframe message out to the
        // pending-promise resolvers, the debug-event sink, or the floor.
        window.addEventListener('message', this.onWindowMessage);

        // Phase 1: wait for the iframe to report it's loaded.
        const readyPromise = this.waitForOneShot('preview-ready');
        frame.src = IFRAME_SRC;
        await readyPromise;

        // If the Playground's boot splash already finished before the iframe
        // loaded, send the unpause signal now so the iframe doesn't start
        // its animations forever-paused.
        if (this.pgSplashDone) {
            frame.contentWindow?.postMessage({ type: 'pg-splash-hidden' }, '*');
        }

        // Phase 2: bootstrap. The monogame template ignores commandDlls
        // (libraries are statically referenced inside WebRuntime.MonoGame),
        // but we still send the message so the iframe transitions through
        // its armed state. Reply 'preview-armed' tells us round-trips are
        // safe.
        const armedPromise = this.waitForOneShot('preview-armed');
        this.postToIframe({ type: 'bootstrap' });
        await armedPromise;
        this.armed = true;
    }

    // Wait for a single message of the given type from the iframe and
    // resolve. Removes its own listener on first hit. Used for boot
    // handshakes ('preview-ready', 'preview-armed') that aren't id-
    // correlated.
    private waitForOneShot(type: string): Promise<void> {
        return new Promise<void>((resolve) => {
            const onMsg = (e: MessageEvent) => {
                if (e.source !== this.iframe?.contentWindow) return;
                if (e.data?.type !== type) return;
                window.removeEventListener('message', onMsg);
                resolve();
            };
            window.addEventListener('message', onMsg);
        });
    }

    private onWindowMessage = (e: MessageEvent): void => {
        if (e.source !== this.iframe?.contentWindow) return;
        const m = e.data;
        if (!m || typeof m !== 'object') return;

        // Streaming debug events — wrapped by the iframe relay so they
        // don't collide with id-correlated <op>-result envelopes.
        if (m.type === 'debug-event') {
            if (this.onDebugEvent) {
                try { this.onDebugEvent(m.event); }
                catch (err) { console.error('[monogame-host] onDebugEvent threw:', err); }
            }
            return;
        }

        // Fatal tick-loop exception. The iframe parks its rAF loop
        // before sending this; the host page surfaces the error in its
        // Output panel and is responsible for resetting any "running"
        // UI state.
        if (m.type === 'game-error') {
            if (this.onGameError) {
                try { this.onGameError(m.message ?? 'unknown game error'); }
                catch (err) { console.error('[monogame-host] onGameError threw:', err); }
            }
            return;
        }

        // stdout / stderr streamed from the iframe's hooked console —
        // user `print` output and runtime-side messages. Fan out to
        // separate callbacks so the host can color them differently.
        if (m.type === 'stdout') {
            if (this.onStdout) {
                try { this.onStdout(m.line ?? ''); }
                catch (err) { console.error('[monogame-host] onStdout threw:', err); }
            }
            return;
        }
        if (m.type === 'stderr') {
            if (this.onStderr) {
                try { this.onStderr(m.line ?? ''); }
                catch (err) { console.error('[monogame-host] onStderr threw:', err); }
            }
            return;
        }

        // Generic error envelope — surface as a console message and, if
        // the iframe attached an id, reject the matching pending call so
        // the awaiter sees the failure instead of hanging forever.
        if (m.type === 'error') {
            console.error('[monogame-host] iframe reported error:', m.message);
            if (typeof m.id === 'number') {
                const pc = this.pending.get(m.id);
                if (pc) {
                    this.pending.delete(m.id);
                    pc.reject(new Error(m.message ?? 'iframe error'));
                }
            }
            return;
        }

        // id-correlated reply. Resolve the matching pending call.
        if (typeof m.id === 'number' && this.pending.has(m.id)) {
            const pc = this.pending.get(m.id)!;
            this.pending.delete(m.id);
            pc.resolve(m.result);
            return;
        }
    };

    private postToIframe(payload: any, transfer?: Transferable[]): void {
        const win = this.iframe?.contentWindow;
        if (!win) throw new Error('[monogame-host] iframe contentWindow is null');
        win.postMessage(payload, '*', transfer);
    }

    // id-correlated round-trip. Use for messages where the iframe sends
    // back an <op>-result envelope.
    private call<T = any>(payload: { type: string; [k: string]: any }, transfer?: Transferable[]): Promise<T> {
        const id = ++this.nextCallId;
        return new Promise<T>((resolve, reject) => {
            this.pending.set(id, { resolve, reject });
            try {
                this.postToIframe({ ...payload, id }, transfer);
            } catch (e: any) {
                this.pending.delete(id);
                reject(e instanceof Error ? e : new Error(String(e)));
            }
        });
    }

    // Fire-and-forget post. Use for messages where the iframe doesn't
    // reply (stop-run, debug-step, debug-set-breakpoints, …).
    private post(payload: any): void {
        if (!this.iframe?.contentWindow) return;
        this.postToIframe(payload);
    }

    // ─── JSInvokable bridges (now postMessage round-trips) ─────────────

    async loadProgram(source: string): Promise<boolean> {
        await this.ensureBooted();
        const resultJson = await this.call<string>({ type: 'run-start-source', source });
        try {
            const parsed = JSON.parse(resultJson);
            return !!parsed.ok;
        } catch {
            return false;
        }
    }

    // Push a single asset (bare name, no extension) + its XNB bytes into the
    // runtime's BrowserContentManager. Page-side glue (main.ts pushAssets)
    // calls this once per `.xnb` in the project before LoadProgram so any
    // `texture`/`load sfx clip` commands fbasic runs resolve through stock
    // ContentManager.Load<T> against the in-memory dict.
    async registerAsset(name: string, bytes: Uint8Array): Promise<void> {
        await this.ensureBooted();
        // Transfer the underlying buffer when possible. structuredClone
        // works either way, but transfer avoids a copy for big .xnbs.
        // Slice to a fresh buffer so any subsequent caller-side reads
        // don't see a detached array.
        const copy = bytes.slice();
        this.postToIframe({ type: 'register-asset', name, bytes: copy }, [copy.buffer]);
    }

    // Wipe the dict — used when the editor switches projects so stale assets
    // from the prior project don't bleed into the next run.
    async clearAssets(): Promise<void> {
        if (!this.isReady()) return;
        this.post({ type: 'clear-assets' });
    }

    /** Pauses the game tick (no VM work) but keeps KNI + Game1 warm so the
     *  next loadProgram is an instant reload, not a full re-boot. No-op if
     *  the iframe hasn't booted yet. */
    async stop(): Promise<void> {
        if (!this.isReady()) return;
        this.post({ type: 'stop-run' });
    }

    /** Suspend or resume all AudioContexts inside the iframe via
     *  Web Audio API. No-op if the iframe hasn't booted. */
    setMuted(muted: boolean): void {
        if (!this.isReady()) return;
        this.post({ type: 'set-muted', muted });
    }

    /** Voluntarily suspend the rAF game loop (e.g. editor has focus).
     *  The loop will die after the current frame; resume() re-arms it. */
    pauseTick(): void {
        if (!this.isReady()) return;
        this.post({ type: 'pause-tick' });
    }

    /** Re-arm the rAF loop suspended by pauseTick(). No-op if not paused
     *  or if the loop was halted by a fatal tick error. */
    resumeTick(): void {
        if (!this.isReady()) return;
        this.post({ type: 'resume-tick' });
    }

    // ─── Debug bridge ──────────────────────────────────────────────────
    // Thin passthroughs over the iframe's debug-* op set. The active
    // session's outbound messages drain on every TickDotNet (forwarded
    // via the iframe's `debug-event` envelope) — these methods only
    // push *requests* into the session.

    /** Compile + load the source, then immediately pause so the editor
     *  can set breakpoints before any user code runs. Mirrors
     *  FadeRunner.debugStart's contract — same `{ok, error, statementLines}`
     *  JSON envelope. */
    async debugStart(source: string): Promise<string> {
        await this.ensureBooted();
        return await this.call<string>({ type: 'debug-start', source });
    }

    /** Compile + start a debug session at a specific test's entry point.
     *  Mirrors FadeRunner.debugStartTest's contract — same
     *  `{ok, error, statementLines}` JSON envelope — but the test runs
     *  through Game1's main tick loop so MonoGame commands (sprite,
     *  texture, sync, audio, …) actually have a live GraphicsDevice. */
    async debugStartTest(source: string, testName: string): Promise<string> {
        await this.ensureBooted();
        return await this.call<string>({ type: 'debug-start-test', source, testName });
    }
    async debugTerminate(): Promise<void> {
        if (!this.isReady()) return;
        this.post({ type: 'debug-terminate' });
    }
    async debugSetBreakpoints(linesJson: string): Promise<void> {
        if (!this.isReady()) return;
        this.post({ type: 'debug-set-breakpoints', linesJson });
    }
    async debugStep(kind: 'over' | 'in' | 'out'): Promise<void> {
        if (!this.isReady()) return;
        this.post({ type: 'debug-step', kind });
    }
    async debugContinue(): Promise<void> {
        if (!this.isReady()) return;
        this.post({ type: 'debug-continue' });
    }
    async debugPause(): Promise<void> {
        if (!this.isReady()) return;
        this.post({ type: 'debug-pause' });
    }
    async debugStackFrames(): Promise<string> {
        if (!this.isReady()) return '[]';
        return await this.call<string>({ type: 'debug-stack-frames' });
    }
    async debugScopes(frameId: number): Promise<string> {
        if (!this.isReady()) return '{"scopes":[]}';
        return await this.call<string>({ type: 'debug-scopes', frameId });
    }
    async debugVariableExpansion(variableId: number): Promise<string> {
        if (!this.isReady()) return '{"scopes":[]}';
        return await this.call<string>({ type: 'debug-variable-expansion', variableId });
    }
    async debugEval(frameId: number, expression: string): Promise<string> {
        if (!this.isReady()) return 'null';
        return await this.call<string>({ type: 'debug-eval', frameId, expression });
    }
    async debugRepl(frameId: number, code: string): Promise<string> {
        if (!this.isReady()) return 'null';
        return await this.call<string>({ type: 'debug-repl', frameId, code });
    }
    async debugSetVariable(frameId: number, variableId: number, rhs: string): Promise<string> {
        if (!this.isReady()) return 'null';
        return await this.call<string>({ type: 'debug-set-variable', frameId, variableId, rhs });
    }

    async listTests(source: string): Promise<MonoGameTestEntry[]> {
        await this.ensureBooted();
        const json = await this.call<string>({ type: 'list-tests', source });
        try { return JSON.parse(json); } catch { return []; }
    }

    /** Returns FadeBasic + KNI + .NET version strings for the Diagnostics
     *  panel. Resolves the iframe's GetVersionInfo JSInvokable. */
    async getVersionInfo(): Promise<{ fadeBasic: string; kni: string; dotnet: string } | null> {
        if (!this.isReady()) return null;
        try {
            const json = await this.call<string>({ type: 'get-version-info' });
            return JSON.parse(json);
        } catch { return null; }
    }

    async runTests(source: string, testName = ''): Promise<MonoGameRunTestsResult> {
        await this.ensureBooted();
        const json = await this.call<string>({ type: 'run-tests', source, testName });
        try { return JSON.parse(json); }
        catch { return { passed: 0, failed: 0, results: [], error: 'bridge JSON parse failed' }; }
    }

    // Debug-event sink — wired from main.ts so editor UI can react to
    // pause/resume/stop/breakpoint-hit messages drained from the canvas
    // DebugSession each frame.
    onDebugEvent?: (event: { id: number; type: string; json: string }) => void;

    // Fatal tick-loop error sink. The iframe forwards .NET-side
    // exceptions caught around TickDotNet here so the host page can
    // surface them in its Output panel and reset its run-active UI
    // state. Without a registered handler, the error is dropped.
    onGameError?: (message: string) => void;

    // stdout / stderr sinks. Wired from main.ts to pipe iframe-side
    // Console.WriteLine output into the Playground's Output panel.
    onStdout?: (line: string) => void;
    onStderr?: (line: string) => void;
}

export const monoGameHost = new MonoGameHost();
