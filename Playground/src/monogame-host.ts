// Lazy bridge that boots WebRuntime.MonoGame inline on the Playground page.
//
// Why inline (not iframe): we want direct function calls between the editor
// and the game runtime. Two WASM runtimes co-exist on the page —
//   1. WebRuntime (Worker)   — LSP + compile + tests + debug for 'web' projects
//   2. WebRuntime.MonoGame   — KNI-backed Game1 for 'monogame' projects, on the
//                              main thread (canvas + Game.Run() require it)
// They publish to public/runtime/ and public/monogame-runtime/ respectively.
//
// Boot sequence on first call to ensureBooted():
//   1. Inject the nkast.Wasm.* static-web-asset <script> tags so KNI's JS
//      shims (Canvas, Audio, Dom, etc.) load before Blazor starts.
//   2. Inject /monogame-runtime/_framework/blazor.webassembly.js with
//      autostart=false, then call Blazor.start() with a loadBootResource hook
//      that points everything at /monogame-runtime/.
//   3. Apply the Blazor-internal-API shims KNI needs:
//        globalThis.Module = Blazor.runtime.Module
//        Blazor.platform.getArrayLength polyfill
//      (fragile — if Blazor moves these we throw with a clear message).
//   4. Set up window.initRenderJS — Pages/Index.razor.cs OnAfterRender calls
//      this with a DotNetObjectReference once Blazor has rendered #mg-blazor-root
//      and the inner #canvasHolder + #theCanvas. We store the ref and start
//      the rAF tick loop.
//   5. ready promise resolves once initRenderJS has fired.
//
// After boot, the page can call loadProgram(source) / listTests(source) /
// runTests(source, name) which delegate to JSInvokables on the Razor page.

const RUNTIME_BASE = '/monogame-runtime';

// Wasm static-asset versions are stamped per release; on a KNI bump these
// suffixes change and 404 in the network panel. Mirror what `dotnet publish`
// drops into wwwroot/_content/nkast.Wasm.*/js/ — match this list to that
// directory listing.
const WASM_SHIM_VERSION = '8.0.11';
const WASM_SHIMS: Array<[string, string]> = [
    ['nkast.Wasm.JSInterop', 'JSObject'],
    ['nkast.Wasm.Dom',       'Window'],
    ['nkast.Wasm.Dom',       'Document'],
    ['nkast.Wasm.Dom',       'Navigator'],
    ['nkast.Wasm.Dom',       'Gamepad'],
    ['nkast.Wasm.Dom',       'Media'],
    ['nkast.Wasm.XHR',       'XHR'],
    ['nkast.Wasm.Canvas',    'Canvas'],
    ['nkast.Wasm.Canvas',    'CanvasGLContext'],
    ['nkast.Wasm.Audio',     'Audio'],
    ['nkast.Wasm.XR',        'XR'],
];

const WATCHDOG_FRAME_MS = 5000;

declare global {
    interface Window {
        // The Blazor JS bootstrapper — present once blazor.webassembly.js loads.
        Blazor?: any;
        // The .NET-side DotNetObjectReference handed to us by Pages/Index.razor.cs
        // via initRenderJS — every invokeMethodAsync goes through this.
        theInstance?: { invokeMethodAsync: (m: string, ...a: any[]) => Promise<any> };
        initRenderJS?: (instance: any) => void;
        // Set by Blazor's runtime; KNI's JS expects it as globalThis.Module
        // (Emscripten convention). We bridge it in applyBlazorKniShims.
        Module?: any;
    }
}

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

class MonoGameHost {
    private bootPromise: Promise<void> | null = null;
    private rafHandle: number | null = null;
    private readyResolve: (() => void) | null = null;

    /** Idempotent — returns the same promise after first call. */
    ensureBooted(): Promise<void> {
        if (this.bootPromise) return this.bootPromise;
        this.bootPromise = this.bootInternal();
        return this.bootPromise;
    }

    /** True once initRenderJS has fired — invoke* calls will work. */
    isReady(): boolean {
        return !!window.theInstance;
    }

    private async bootInternal(): Promise<void> {
        if (!document.getElementById('mg-blazor-root')) {
            throw new Error(
                '[monogame-host] #mg-blazor-root not in DOM — open the Game panel before booting.',
            );
        }

        // The wait-for-initRenderJS promise — Pages/Index.razor.cs's
        // OnAfterRender fires this once after Blazor mounts + KNI is alive.
        const initSignal = new Promise<void>((resolve) => {
            this.readyResolve = resolve;
        });
        window.initRenderJS = (instance) => this.onInitRenderJS(instance);

        // Step 1: KNI Wasm JS shims — load synchronously so Blazor's WASM
        // loader sees the globals it expects.
        for (const [pkg, name] of WASM_SHIMS) {
            await this.injectScript(
                `${RUNTIME_BASE}/_content/${pkg}/js/${name}.${WASM_SHIM_VERSION}.js`,
            );
        }

        // Step 2: Blazor bootstrapper. autostart=false so we can install the
        // loadBootResource hook before the runtime fetches its WASMs.
        const bootScript = document.createElement('script');
        bootScript.src = `${RUNTIME_BASE}/_framework/blazor.webassembly.js`;
        bootScript.setAttribute('autostart', 'false');
        document.head.appendChild(bootScript);
        await new Promise<void>((resolve, reject) => {
            bootScript.onload = () => resolve();
            bootScript.onerror = () => reject(new Error('[monogame-host] failed to load blazor.webassembly.js'));
        });

        // Step 3: redirect all Blazor boot resources to /monogame-runtime/.
        // Without this, Blazor's loader resolves paths against the page's
        // <base href> (the Playground root) and 404s.
        await window.Blazor.start({
            loadBootResource: (_type: string, name: string, _defaultUri: string, _integrity: string) => {
                return `${RUNTIME_BASE}/_framework/${name}`;
            },
        });

        // Wait for Pages/Index.razor.cs's OnAfterRender to fire initRenderJS.
        // First render happens *after* Blazor.start() resolves, so it should
        // be in-flight at this point.
        await initSignal;
    }

    private onInitRenderJS(instance: any) {
        this.applyBlazorKniShims();
        window.theInstance = instance;

        // Disable browser context menu on the canvas so right-click is
        // available to the game (matches XnaFiddle and WebRuntime.MonoGame's
        // standalone index.html).
        const canvas = document.getElementById('theCanvas');
        canvas?.addEventListener('contextmenu', (e) => e.preventDefault());

        this.startRaf();

        const r = this.readyResolve;
        this.readyResolve = null;
        if (r) r();
    }

    private applyBlazorKniShims() {
        // KNI's JS expects globalThis.Module (Emscripten convention) which
        // Blazor 8+ stopped exposing. Bridge via Blazor.runtime.Module.
        if (!window.Module) {
            const m = window.Blazor?.runtime?.Module;
            if (!m) {
                throw new Error(
                    '[monogame-host] Blazor.runtime.Module not available — the internal API may have changed. Pin Blazor and KNI versions together.',
                );
            }
            window.Module = m;
        }

        // KNI also calls Blazor.platform.getArrayLength, removed in newer
        // Blazor. Polyfill via HEAP32 read of the array-length slot.
        const plat = window.Blazor.platform;
        if (typeof plat.getArrayLength !== 'function') {
            Object.assign(plat, {
                getArrayLength(arr: any) {
                    const arrPtr = plat.getArrayEntryPtr(arr, 0, 4);
                    return window.Module.HEAP32[(arrPtr - 4) >> 2];
                },
            });
        }
    }

    private startRaf() {
        if (this.rafHandle != null) return;
        const tick = async () => {
            const instance = window.theInstance;
            if (!instance) {
                // Game was torn down by OnGameTimedOut — keep the rAF alive
                // so a future LoadProgram can re-engage without a page reload.
                this.rafHandle = window.requestAnimationFrame(tick);
                return;
            }
            const canvas = document.getElementById('theCanvas') as HTMLCanvasElement | null;
            const holder = document.getElementById('canvasHolder') as HTMLElement | null;
            if (canvas && holder) {
                const w = holder.clientWidth;
                const h = holder.clientHeight;
                if (canvas.width !== w || canvas.height !== h) {
                    canvas.width = w;
                    canvas.height = h;
                    void instance.invokeMethodAsync('OnCanvasResized', w, h);
                }
                const tickStart = performance.now();
                // TickDotNet now returns a JSON array of drained debug
                // events (REQUEST_*-ack / breakpoint-hit / step-landed /
                // etc.). The editor's debug control bar subscribes via
                // onDebugEvent so it can flip pause/run state, surface
                // the stack, etc.
                try {
                    const eventsJson = await instance.invokeMethodAsync('TickDotNet') as string;
                    if (eventsJson && eventsJson !== '[]' && this.onDebugEvent) {
                        try {
                            const events = JSON.parse(eventsJson);
                            for (const ev of events) this.onDebugEvent(ev);
                        } catch { /* malformed event payload — ignore */ }
                    }
                } catch (e) {
                    // Surfaced via console; the rAF keeps ticking so a
                    // subsequent LoadProgram can recover.
                    console.error('[monogame-host] TickDotNet failed', e);
                }
                const tickMs = performance.now() - tickStart;
                if (tickMs > WATCHDOG_FRAME_MS) {
                    void instance.invokeMethodAsync('OnGameTimedOut', tickMs);
                }
            }
            this.rafHandle = window.requestAnimationFrame(tick);
        };
        this.rafHandle = window.requestAnimationFrame(tick);
    }

    // Debug-event sink — wired from main.ts so editor UI can react to
    // pause/resume/stop/breakpoint-hit messages drained from the canvas
    // DebugSession each frame.
    onDebugEvent?: (event: { id: number; type: string; json: string }) => void;

    private injectScript(src: string): Promise<void> {
        return new Promise<void>((resolve, reject) => {
            const existing = document.querySelector<HTMLScriptElement>(`script[src="${src}"]`);
            if (existing) { resolve(); return; }
            const tag = document.createElement('script');
            tag.src = src;
            tag.onload = () => resolve();
            tag.onerror = () => reject(new Error(`[monogame-host] failed to load ${src}`));
            document.head.appendChild(tag);
        });
    }

    // ─── JSInvokable bridges ───────────────────────────────────────────

    async loadProgram(source: string): Promise<boolean> {
        await this.ensureBooted();
        return await window.theInstance!.invokeMethodAsync('LoadProgram', source) as boolean;
    }

    // Push a single asset (bare name, no extension) + its XNB bytes into the
    // runtime's BrowserContentManager. Page-side glue (main.ts pushAssets)
    // calls this once per `.xnb` in the project before LoadProgram so any
    // `texture`/`load sfx clip` commands fbasic runs resolve through stock
    // ContentManager.Load<T> against the in-memory dict.
    async registerAsset(name: string, bytes: Uint8Array): Promise<void> {
        await this.ensureBooted();
        await window.theInstance!.invokeMethodAsync('RegisterAsset', name, bytes);
    }

    // Wipe the dict — used when the editor switches projects so stale assets
    // from the prior project don't bleed into the next run.
    async clearAssets(): Promise<void> {
        if (!this.isReady()) return;
        await window.theInstance!.invokeMethodAsync('ClearAssets');
    }

    /** Pauses the game tick (no VM work) but keeps KNI + Game1 warm so the
     *  next loadProgram is an instant reload, not a full re-boot. No-op if
     *  the runtime hasn't booted yet. */
    async stop(): Promise<void> {
        if (!this.isReady()) return;
        await window.theInstance!.invokeMethodAsync('StopGame');
    }

    // ─── Debug bridge ──────────────────────────────────────────────────
    // Thin passthroughs over the JSInvokable surface in Pages/Index.Debug.cs.
    // Each one is `await invokeMethodAsync('X', ...)` and returns the C#-
    // serialized JSON envelope as a string for the caller to parse. The
    // active session's outbound messages drain on every TickDotNet (via
    // onDebugEvent) — these methods only push *requests* into the session.

    /** Compile + load the source, then immediately pause so the editor
     *  can set breakpoints before any user code runs. Mirrors
     *  FadeRunner.debugStart's contract — same `{ok, error, statementLines}`
     *  JSON envelope. */
    async debugStart(source: string): Promise<string> {
        await this.ensureBooted();
        const compileOk = await window.theInstance!.invokeMethodAsync('LoadProgram', source) as boolean;
        if (!compileOk) {
            return JSON.stringify({ ok: false, error: 'compile failed', statementLines: [] });
        }
        return await window.theInstance!.invokeMethodAsync('DebugStart') as string;
    }
    async debugTerminate(): Promise<void> {
        if (!this.isReady()) return;
        await window.theInstance!.invokeMethodAsync('DebugTerminate');
    }
    async debugSetBreakpoints(linesJson: string): Promise<void> {
        if (!this.isReady()) return;
        await window.theInstance!.invokeMethodAsync('DebugSetBreakpoints', linesJson);
    }
    async debugStep(kind: 'over' | 'in' | 'out'): Promise<void> {
        if (!this.isReady()) return;
        await window.theInstance!.invokeMethodAsync('DebugStep', kind);
    }
    async debugContinue(): Promise<void> {
        if (!this.isReady()) return;
        await window.theInstance!.invokeMethodAsync('DebugContinue');
    }
    async debugPause(): Promise<void> {
        if (!this.isReady()) return;
        await window.theInstance!.invokeMethodAsync('DebugPause');
    }
    async debugStackFrames(): Promise<string> {
        if (!this.isReady()) return '[]';
        return await window.theInstance!.invokeMethodAsync('DebugStackFrames') as string;
    }
    async debugScopes(frameId: number): Promise<string> {
        if (!this.isReady()) return '{"scopes":[]}';
        return await window.theInstance!.invokeMethodAsync('DebugScopes', frameId) as string;
    }
    async debugVariableExpansion(variableId: number): Promise<string> {
        if (!this.isReady()) return '{"scopes":[]}';
        return await window.theInstance!.invokeMethodAsync('DebugVariableExpansion', variableId) as string;
    }
    async debugEval(frameId: number, expression: string): Promise<string> {
        if (!this.isReady()) return 'null';
        return await window.theInstance!.invokeMethodAsync('DebugEval', frameId, expression) as string;
    }
    async debugRepl(frameId: number, code: string): Promise<string> {
        if (!this.isReady()) return 'null';
        return await window.theInstance!.invokeMethodAsync('DebugRepl', frameId, code) as string;
    }
    async debugSetVariable(frameId: number, variableId: number, rhs: string): Promise<string> {
        if (!this.isReady()) return 'null';
        return await window.theInstance!.invokeMethodAsync('DebugSetVariable', frameId, variableId, rhs) as string;
    }

    async listTests(source: string): Promise<MonoGameTestEntry[]> {
        await this.ensureBooted();
        const json = await window.theInstance!.invokeMethodAsync('ListTests', source) as string;
        try { return JSON.parse(json); } catch { return []; }
    }

    async runTests(source: string, testName = ''): Promise<MonoGameRunTestsResult> {
        await this.ensureBooted();
        const json = await window.theInstance!.invokeMethodAsync('RunTests', source, testName) as string;
        try { return JSON.parse(json); } catch { return { passed: 0, failed: 0, results: [], error: 'bridge JSON parse failed' }; }
    }
}

export const monoGameHost = new MonoGameHost();
