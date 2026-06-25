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
//   get-debug-test-result { id }            get-debug-test-result-result { id, result }
//   compile-for-run { source, id }          compile-for-run-result { id, result }
//   begin-pending-program { id }            begin-pending-program-result { id, result }
//   get-content-build-plan { id }           get-content-build-plan-result { id, result }
//   register-asset { name, bytes }          (no reply)
//   register-audio { name, bytes, id }      register-audio-result { id, result }
//   unregister-asset { name }               (no reply)
//   unregister-audio { name }               (no reply)
//   clear-assets                            (no reply)
//   debug-ui-change { ctrlId, kind, value } (no reply)
//   debug-list-types { id }                 debug-list-types-result { id, result }
//   debug-get-schema { typeName, id }       debug-get-schema-result { id, result }
//   debug-list-entities { typeName, id }    debug-list-entities-result { id, result }
//   debug-get-labels { typeName, id }       debug-get-labels-result { id, result }
//   debug-get-entity { typeName, entityId, id } debug-get-entity-result { id, result }
//   debug-set-field { typeName, entityId, path, valueJson, id }
//                                           debug-set-field-result { id, result }
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
//   debug-ui-frame { json }                 — one-per-frame command queue
//                                              from DebugUISystem (browser).
//                                              Forwarded via onDebugUiFrame
//                                              for the Tweakpane panel.

const IFRAME_SRC = '/runtime/monogame/index.html?preview=1';
const IFRAME_ELEMENT_ID = 'mg-preview-frame';

export interface MonoGameTestResult {
    name: string;
    passed: boolean;
    duration: number;
    failureMessage: string | null;
    failureReason: string | null;
    failureSourceText: string | null;
    // Source-located failure frames (DebugData-resolved call-stack at the
    // point the test failed). Optional because run-tests batches don't
    // include them today — only the debug-test result fetch via
    // GetDebugTestResult on the monogame side does.
    failureFrames?: Array<{
        functionName: string;
        lineNumber: number;
        charNumber: number;
        instructionIndex: number;
    }>;
}

// Snapshot of ContentSystem returned by CompileForRun / GetContentBuildPlan.
// Mirrors the C# ContentEntry struct verbatim so callers can interpret
// per-content-kind parameters the same way the desktop content builder
// does (e.g. parameters['Compression'] for textures).
export interface MonoGameContentEntry {
    path: string;
    name: string;
    importer: string;
    processor: string;
    parameters: Record<string, string>;
}

export interface MonoGameContentPlan {
    defaultCompression: string;
    entries: MonoGameContentEntry[];
}

export interface MonoGameContentPlanResult {
    ok: boolean;
    error: string | null;
    plan: MonoGameContentPlan;
}

function normalizePlan(raw: any): MonoGameContentPlan {
    return {
        defaultCompression:
            typeof raw?.defaultCompression === 'string' ? raw.defaultCompression : 'auto',
        entries: Array.isArray(raw?.entries)
            ? raw.entries.map((e: any) => ({
                path: String(e?.path ?? ''),
                name: String(e?.name ?? ''),
                importer: String(e?.importer ?? 'Auto'),
                processor: String(e?.processor ?? 'Auto'),
                parameters: (e?.parameters && typeof e.parameters === 'object')
                    ? Object.fromEntries(
                        Object.entries(e.parameters).map(([k, v]) => [String(k), String(v ?? '')]),
                    )
                    : {},
            }))
            : [],
    };
}

function parsePlanResult(resultJson: string): MonoGameContentPlanResult {
    try {
        const parsed = JSON.parse(resultJson);
        return {
            ok: !!parsed?.ok,
            error: typeof parsed?.error === 'string' ? parsed.error : null,
            plan: normalizePlan(parsed?.plan),
        };
    } catch (e) {
        return {
            ok: false,
            error: 'CompileForRun reply was not valid JSON: ' + (e as Error).message,
            plan: { defaultCompression: 'auto', entries: [] },
        };
    }
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
    /** Canonical state for the per-frame debug-UI envelope, kept up to
     *  date by merging deltas from the iframe's diff producer (the
     *  'debug-ui-frame-delta' message handler below). The full
     *  envelope is needed for the live-session relay path, which
     *  broadcasts JSON bytes to observers; we re-serialize lazily
     *  whenever onDebugUiFrame fires. Reset on iframe rebind so a
     *  stale envelope from a previous program doesn't leak into the
     *  next run. */
    private canonicalDebugUiEnv: DebugUiFrameEnvelope | null = null;
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
            // Sandbox intent: keep top-navigation / popups / modals off-limits
            // so the monogame template can't escape into the host page. We
            // include allow-same-origin because Blazor's dotnet.js reads
            // `Window.caches` (and `sessionStorage`) during boot — a null-origin
            // iframe throws a SecurityError on either access and aborts with
            // "Failed to start platform". (The previous version of this code
            // tried to omit allow-same-origin for Chrome Site-Isolation, but
            // that path also breaks Blazor; if/when we want process isolation
            // back, the route is COOP/COEP headers on a real origin, not the
            // null-origin sandbox trick.)
            // postMessage still works across the sandbox — monogame-host.ts
            // already uses '*' as the target origin throughout.
            frame.setAttribute('sandbox', 'allow-scripts allow-pointer-lock allow-same-origin');
            // Fullscreen + autoplay aren't sandbox tokens; they're
            // Permissions-Policy features granted via `allow=`. Chrome
            // (and the spec) reject them as sandbox values with a parse
            // error in the console. Express them on the right attribute.
            frame.setAttribute('allow', 'fullscreen; autoplay');
            // Legacy fullscreen attribute too — some older Chromium builds
            // honor `allowfullscreen` separately from Permissions Policy.
            frame.setAttribute('allowfullscreen', '');
            // Append to the content wrapper (below the game toolbar) so the
            // iframe fills #mg-game-content rather than the full #mg-blazor-root.
            const content = document.getElementById('mg-game-content') ?? root;
            content.appendChild(frame);
        }
        this.iframe = frame;
        // New iframe → reset the canonical debug-UI envelope so the
        // next 'debug-ui-frame-delta' baseline starts clean. Without
        // this, leftover state from a previous program could persist
        // and confuse the merge if the new iframe's first delta is
        // partial (shouldn't happen — the iframe sends baseline=true
        // on its first emission — but defensive).
        this.canonicalDebugUiEnv = null;

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
        // Replay the subscription state to the freshly-armed iframe so
        // it knows whether the parent's Debug UI panel currently wants
        // envelopes. Without this, the iframe defaults to subscribed=
        // true (safe default) and sends frames even when the panel is
        // hidden. The call is cheap (one postMessage) and idempotent.
        this.flushDebugUiSubscribed();
    }

    /** Tell the iframe whether to bother sending per-frame debug-ui
     *  envelopes. Called from main.ts whenever the dockview Debug UI
     *  panel toggles visibility. The iframe stops sending postMessages
     *  when `active === false` AND no in-iframe overlay panel is
     *  mounted; the bigger win is that C# can poll `window.fadeDebugUi
     *  .isSubscribed()` to skip the snapshot generation entirely
     *  (the JS-side gate only saves transport + parse). */
    setDebugUiSubscribed(active: boolean): void {
        if (this.debugUiSubscribed === active) return;
        this.debugUiSubscribed = active;
        this.flushDebugUiSubscribed();
    }

    private debugUiSubscribed: boolean = true;
    private flushDebugUiSubscribed(): void {
        if (!this.iframe?.contentWindow) return;
        try {
            this.iframe.contentWindow.postMessage({
                type: 'debug-ui-subscribe',
                active: this.debugUiSubscribed,
            }, '*');
        } catch { /* ignore */ }
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

        // Debug UI per-frame envelope (browser DebugUISystem). The iframe
        // ships `{type: 'debug-ui-frame', json: '<envelope>'}` once per
        // frame. The envelope shape is:
        //   { gen: int, queue: [...], autoInspector: bool,
        //     metadata?: {...}, entities?: {typeName: number[]} }
        // We parse here so each subscriber doesn't redo the work, and
        // tolerate the legacy "just a queue array" shape in case an old
        // runtime ships an unwrapped queue.
        if (m.type === 'debug-ui-frame') {
            if (this.onDebugUiFrame) {
                try {
                    const rawJson = typeof m.json === 'string' ? m.json : '';
                    const env = parseDebugUiEnvelope(rawJson);
                    // Pass the raw json alongside the parsed env so the
                    // collab relay can broadcast the original bytes
                    // without re-serialising; observers reuse the same
                    // parseDebugUiEnvelope path.
                    this.onDebugUiFrame(env, rawJson);
                    // Sync the canonical env so a later switch to the
                    // delta protocol picks up from the right baseline.
                    this.canonicalDebugUiEnv = env;
                }
                catch (err) { console.error('[monogame-host] onDebugUiFrame threw:', err); }
            }
            return;
        }

        // Same channel as 'debug-ui-frame', but the iframe ships only
        // the fields that changed since the last emission (see the
        // diff producer in monogame/index.html). Saves the parent's
        // postMessage clone + JSON.parse cost for stable subfields —
        // typically queue + entities, which don't change every frame
        // even though metadata does. First emission carries baseline:
        // true and represents a full envelope; subsequent emissions
        // omit unchanged fields and we merge them into the canonical
        // state below. If iframe and parent ever fall out of sync
        // (shouldn't happen but defensive), the iframe will re-send a
        // full baseline on its next gen change.
        if (m.type === 'debug-ui-frame-delta') {
            if (this.onDebugUiFrame) {
                try {
                    const delta = m.delta as (Partial<DebugUiFrameEnvelope> & { baseline?: boolean }) | undefined;
                    if (!delta) return;
                    let merged: DebugUiFrameEnvelope;
                    if (delta.baseline || !this.canonicalDebugUiEnv) {
                        merged = {
                            gen: delta.gen ?? 0,
                            queue: delta.queue ?? [],
                            autoInspector: !!delta.autoInspector,
                            metadata: delta.metadata ?? null,
                            entities: delta.entities,
                        };
                    } else {
                        merged = { ...this.canonicalDebugUiEnv };
                        if ('gen' in delta && typeof delta.gen === 'number') merged.gen = delta.gen;
                        if ('autoInspector' in delta) merged.autoInspector = !!delta.autoInspector;
                        if ('queue' in delta && Array.isArray(delta.queue)) merged.queue = delta.queue;
                        if ('metadata' in delta) merged.metadata = delta.metadata ?? null;
                        if ('entities' in delta) merged.entities = delta.entities;
                    }
                    this.canonicalDebugUiEnv = merged;
                    // Re-serialise the merged envelope for the collab
                    // relay path. The iframe DIDN'T send us a usable
                    // raw JSON since the delta is a subset, so we
                    // rebuild it here. Cost is comparable to what the
                    // iframe used to stringify per frame; net we still
                    // win on the structured-clone + parent-parse side.
                    const rawJson = JSON.stringify(merged);
                    this.onDebugUiFrame(merged, rawJson);
                }
                catch (err) { console.error('[monogame-host] onDebugUiFrame (delta) threw:', err); }
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

    // Two-phase run for the asset pipeline. The playground calls these
    // in sequence:
    //
    //   const plan = await mg.compileForRun(source);
    //   await registerAssetsForPlan(plan);   // PNG → XNB + registerAsset
    //   await mg.beginPendingProgram();
    //
    // CompileForRun on the C# side resets ContentSystem.entries, runs the
    // macro pass, and stashes the FadeRuntimeContext so BeginPendingProgram
    // can hand it to Game1 once assets are in place. Compile errors and an
    // empty plan are both surfaced through the returned envelope.
    async compileForRun(source: string): Promise<MonoGameContentPlanResult> {
        await this.ensureBooted();
        const resultJson = await this.call<string>({ type: 'compile-for-run', source });
        return parsePlanResult(resultJson);
    }

    /** Read the most recent post-compile plan without re-running the
     *  compile. Useful when the editor needs to retry asset registration
     *  after a transient failure. Returns the empty plan when no compile
     *  is pending. */
    async getContentBuildPlan(): Promise<MonoGameContentPlan> {
        if (!this.isReady()) return { defaultCompression: 'auto', entries: [] };
        const resultJson = await this.call<string>({ type: 'get-content-build-plan' });
        try {
            const parsed = JSON.parse(resultJson);
            return normalizePlan(parsed);
        } catch {
            return { defaultCompression: 'auto', entries: [] };
        }
    }

    async beginPendingProgram(): Promise<boolean> {
        await this.ensureBooted();
        // BeginPendingProgram is declared `public bool` on the C# side.
        // Blazor's JSInterop hands primitive bool returns back as a JS
        // boolean — NOT a JSON string. Earlier code here treated the
        // reply as a string and called .trim() on it, which threw
        // "(intermediate value).trim is not a function" and prevented
        // every monogame Run from actually starting (the assets got
        // registered but the iframe's _pendingContext was never swapped
        // into Game1). Tolerate both shapes defensively in case a future
        // refactor routes through JSON.
        const result = await this.call<boolean | string>({ type: 'begin-pending-program' });
        if (typeof result === 'boolean') return result;
        return String(result ?? '').trim().toLowerCase() === 'true';
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

    /** Evict a single texture/font asset from the iframe's
     *  BrowserContentManager — both the registered bytes and any
     *  cached Texture2D/SpriteFont. Use after the source bytes have
     *  changed (or the asset was deleted) so a follow-up
     *  registerAsset is picked up by `Content.Load`. Fire-and-forget;
     *  the runtime side handles missing names gracefully. */
    async unregisterAsset(name: string): Promise<void> {
        if (!this.isReady()) return;
        this.post({ type: 'unregister-asset', name });
    }

    /** Same idea for audio — drop the decoded AudioBuffer keyed by
     *  `name`. Unchanged audio assets stay cached across Runs so we
     *  don't re-run `decodeAudioData` on every Run. */
    async unregisterAudio(name: string): Promise<void> {
        if (!this.isReady()) return;
        this.post({ type: 'unregister-audio', name });
    }

    /** Push raw audio source bytes (MP3/OGG/WAV/FLAC/AAC) to the
     *  iframe's Web Audio host. The iframe decodes via Web Audio's
     *  decodeAudioData and stores the AudioBuffer keyed by `name` — the
     *  same name fbasic later passes to `load sfx clip`. The decode is
     *  async, so this returns a Promise the caller awaits before
     *  unblocking BeginPendingProgram. Replaces the previous XNB-wrapped
     *  audio path entirely; the iframe's window.fadeAudio is the new
     *  audio backend on browser builds. */
    async registerAudio(name: string, bytes: Uint8Array): Promise<boolean> {
        await this.ensureBooted();
        const copy = bytes.slice();
        const result = await this.call<unknown>(
            { type: 'register-audio', name, bytes: copy },
            [copy.buffer],
        );
        // The iframe replies with a JS boolean (Blazor-style primitive
        // return) — tolerate both shapes defensively.
        if (typeof result === 'boolean') return result;
        return String(result ?? '').trim().toLowerCase() === 'true';
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
    // Map a VM instruction index → joined-source location, via the
    // canvas DebugSession's IndexCollection. Used by the crash overlay
    // to translate `ins=[N]` in REV_REQUEST_EXPLODE messages.
    async resolveInstruction(insIndex: number): Promise<{ insIndex: number; lineNumber: number; charNumber: number } | null> {
        if (!this.isReady()) return null;
        try {
            const json = await this.call<string>({ type: 'debug-resolve-instruction', insIndex });
            if (!json || json === 'null') return null;
            return JSON.parse(json);
        } catch { return null; }
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

    /** Snapshot the most recent debug-test outcome. Index.Debug.cs's
     *  QueueTestForDebugAsync stashes the FadeTestResult before sending
     *  REV_REQUEST_EXITED, so by the time the editor's 'complete' handler
     *  calls this on the back of that event the result is available.
     *  Returns null when no debug-test has completed since the last
     *  DebugStartTest — the caller treats that as the safety branch
     *  (row stays 'stopped' rather than flipping to a guessed status). */
    async debugGetTestResult(): Promise<MonoGameTestResult | null> {
        if (!this.isReady()) return null;
        const json = await this.call<string>({ type: 'get-debug-test-result' });
        if (!json || json === 'null') return null;
        try { return JSON.parse(json) as MonoGameTestResult; }
        catch (e) {
            console.error('[monogame-host] debugGetTestResult parse failed:', e, json);
            return null;
        }
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

    // Debug UI sink — receives the parsed per-frame envelope plus the
    // raw JSON string the iframe shipped (so the collab relay can
    // broadcast the original bytes without re-serialising the env).
    // Wired by main.ts to the Tweakpane debug-ui-panel. Optional;
    // unset means the iframe's frames are simply dropped.
    onDebugUiFrame?: (envelope: DebugUiFrameEnvelope, rawJson: string) => void;

    /** Push a user-driven change (slider move, button click, etc.)
     *  back to the running game. `ctrlId` is the ControlId echoed
     *  back from the most recent debug-ui-frame; `kind` is 0=bool,
     *  1=int, 2=float, 3=string (DebugUISystem.KIND_*); `value` is
     *  the stringified payload. No-op until the iframe is armed. */
    sendDebugUiChange(ctrlId: number, kind: number, value: string): void {
        if (!this.isReady()) return;
        this.post({ type: 'debug-ui-change', ctrlId, kind, value });
    }

    // ─── Debug Inspector RPC ────────────────────────────────────────
    // Generic provider-driven inspector surface — the Playground
    // Tweakpane panel uses these to enumerate entities, snapshot
    // their state, and write edits back. Each method round-trips
    // through the iframe to a [JSInvokable] on Index.razor.cs that
    // delegates to DebugRegistry on the C# side. JSON strings come
    // back verbatim — the caller parses with JSON.parse.

    /** Return all registered provider type names. */
    async debugListTypes(): Promise<string[]> {
        if (!this.isReady()) return [];
        const json = await this.call<string>({ type: 'debug-list-types' });
        try { return JSON.parse(json); } catch { return []; }
    }

    /** Return the field schema for one provider (drives Tweakpane
     *  widget choice). */
    async debugGetSchema(typeName: string): Promise<DebugFieldSchema[] | null> {
        if (!this.isReady()) return null;
        const json = await this.call<string>({ type: 'debug-get-schema', typeName });
        if (!json || json === 'null') return null;
        try { return JSON.parse(json) as DebugFieldSchema[]; } catch { return null; }
    }

    /** Per-entity schema. Used by the panel for provider types whose
     *  field list varies per id (e.g. effects with shader parameters). */
    async debugGetEntitySchema(typeName: string, entityId: number): Promise<DebugFieldSchema[] | null> {
        if (!this.isReady()) return null;
        const json = await this.call<string>({ type: 'debug-get-entity-schema', typeName, entityId });
        if (!json || json === 'null') return null;
        try { return JSON.parse(json) as DebugFieldSchema[]; } catch { return null; }
    }

    /** Return the live id list for one provider. */
    async debugListEntities(typeName: string): Promise<number[]> {
        if (!this.isReady()) return [];
        const json = await this.call<string>({ type: 'debug-list-entities', typeName });
        try { return JSON.parse(json); } catch { return []; }
    }

    /** Return per-id display labels for one provider — e.g. for
     *  the texture picker, maps `1 → "Images/Player"`. Only entries
     *  with non-empty labels are present; callers fall back to the
     *  generic `<type> #<id>` form for missing keys. */
    async debugGetLabels(typeName: string): Promise<Record<string, string>> {
        if (!this.isReady()) return {};
        const json = await this.call<string>({ type: 'debug-get-labels', typeName });
        try {
            const parsed = JSON.parse(json);
            return parsed && typeof parsed === 'object' ? parsed as Record<string, string> : {};
        } catch { return {}; }
    }

    /** Snapshot one entity's current state. Returns a plain JSON
     *  object whose keys match the schema's Path values (top-level
     *  for non-nested fields, dotted access for vec2/color). */
    async debugGetEntity(typeName: string, entityId: number): Promise<Record<string, unknown> | null> {
        if (!this.isReady()) return null;
        const json = await this.call<string>({ type: 'debug-get-entity', typeName, entityId });
        if (!json || json === 'null') return null;
        try { return JSON.parse(json); } catch { return null; }
    }

    /** Apply one field change. valueJson must be a JSON-encoded leaf
     *  value (number, bool, or string — never an object/array). */
    async debugSetField(typeName: string, entityId: number, path: string, valueJson: string): Promise<boolean> {
        if (!this.isReady()) return false;
        const result = await this.call<boolean | string>({
            type: 'debug-set-field', typeName, entityId, path, valueJson,
        });
        if (typeof result === 'boolean') return result;
        return String(result ?? '').toLowerCase() === 'true';
    }
}

/** One DebugUICommand as serialized by DebugUISystem.Browser.cs. */
export interface DebugUiCommand {
    id: number;
    t: number;
    l?: string | null;
    s?: string | null;
    i: number;
    f: number | null;
}

/** Parsed per-frame envelope produced by DebugUISystem.Browser.cs.
 *  `gen` increments on every NotifyProgramReset; subscribers wipe
 *  their UI state when they see it change. `autoInspector` mirrors
 *  the fbasic `enable debug inspector` flag — false on a fresh
 *  program, true once the source opts in.
 *
 *  `metadata` and `entities` are present only when autoInspector is
 *  true (the C# side gates the work behind the same flag). */
export interface DebugUiFrameEnvelope {
    gen: number;
    queue: DebugUiCommand[];
    autoInspector: boolean;
    metadata?: Record<string, unknown> | null;
    entities?: Record<string, number[]>;
}

export function parseDebugUiEnvelope(json: string): DebugUiFrameEnvelope {
    const empty: DebugUiFrameEnvelope = { gen: 0, queue: [], autoInspector: false };
    if (!json) return empty;
    let raw: any;
    try { raw = JSON.parse(json); }
    catch { return empty; }
    // Tolerate the legacy "bare array of commands" wire shape in case
    // an older runtime serializer is in play — wrap it as a gen-0
    // envelope so downstream code only has to deal with one shape.
    if (Array.isArray(raw)) return { gen: 0, queue: raw, autoInspector: false };
    if (!raw || typeof raw !== 'object') return empty;
    return {
        gen: typeof raw.gen === 'number' ? raw.gen : 0,
        queue: Array.isArray(raw.queue) ? raw.queue : [],
        autoInspector: !!raw.autoInspector,
        metadata: raw.metadata && typeof raw.metadata === 'object' ? raw.metadata : null,
        entities: raw.entities && typeof raw.entities === 'object' ? raw.entities : undefined,
    };
}

/** Mirrors C# DebugField — used by the panel to pick the right
 *  Tweakpane widget per field. */
export interface DebugFieldSchema {
    path: string;
    type: 'int' | 'float' | 'bool' | 'string' | 'vec2' | 'vec3' | 'color' | 'image' | string;
    label: string;
    min?: number | null;
    max?: number | null;
    readOnly?: boolean;
    /** Set on int fields whose value is a foreign-key into another
     *  provider's entity list. Panel renders these as a <select>
     *  populated from listEntities(referenceType). */
    referenceType?: string;
}

export const monoGameHost = new MonoGameHost();
