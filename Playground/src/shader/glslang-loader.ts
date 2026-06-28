// Lazy-loader for the @webgpu/glslang WASM module.
//
// glslang is Khronos's official GLSL→SPIR-V validator/compiler. We use
// it ONLY for validation — feed it the GLSL our translator produces,
// ignore the SPIR-V output, and parse the error log to surface compile
// errors as Monaco markers before the user has to click Reset.
//
// Loading strategy:
//
//   The @webgpu/glslang `dist/web-devel/glslang.js` file is published as
//   ESM (`export default …` at the bottom, despite older CommonJS/AMD
//   compatibility cruft above it). We dynamic-import the CDN URL and
//   call the default export — a memoized factory that returns a Promise
//   resolving to `{ compileGLSL, compileGLSLZeroCopy }`.
//
//   The WASM is located via `import.meta.url` inside the module, so it
//   loads from the same CDN as the JS. No `locateFile` config needed.
//
//   Error log capture: the Emscripten module's `printErr` defaults to
//   `console.warn` and there's NO config hook to override it once
//   `Module()` is instantiated. To capture the per-line `ERROR: 0:N: …`
//   log that's the whole point of using glslang, we temporarily swap
//   `console.warn` around each compileGLSL call.
//
// API:
//
//   ensureGlslang(): Promise<GlslangInstance>
//   isGlslangReady(): boolean
//   __setGlslangInstanceForTest(): test seam for stubs

const GLSLANG_URL =
    'https://cdn.jsdelivr.net/npm/@webgpu/glslang@0.0.15/dist/web-devel/glslang.js';

export interface GlslangInstance {
    compileGLSL(
        source: string,
        stage: 'vertex' | 'fragment' | 'compute',
        debug?: boolean,
        spirvVersion?: string,
    ): Uint32Array;
    _printedErrors: string[];
    _resetPrintedErrors(): void;
}

interface RawGlslangInstance {
    compileGLSL: (
        source: string,
        stage: 'vertex' | 'fragment' | 'compute',
        debug?: boolean,
        spirvVersion?: string,
    ) => Uint32Array;
    compileGLSLZeroCopy?: unknown;
}

let _instance: GlslangInstance | null = null;
let _loadPromise: Promise<GlslangInstance> | null = null;
let _loadError: Error | null = null;

export function isGlslangReady(): boolean {
    return _instance !== null;
}

export function lastGlslangLoadError(): Error | null {
    return _loadError;
}

export async function ensureGlslang(): Promise<GlslangInstance> {
    if (_instance) return _instance;
    if (_loadError) {
        console.error('[glslang] previously failed to load:', _loadError.message);
        throw _loadError;
    }
    if (_loadPromise) return _loadPromise;

    _loadPromise = loadGlslangViaEsmImport()
        .then((inst) => {
            _instance = inst;
            console.log('[glslang] loaded successfully');
            return inst;
        })
        .catch((err) => {
            _loadError = err instanceof Error ? err : new Error(String(err));
            _loadPromise = null;
            console.error('[glslang] LOAD FAILED — live validation is off until this is fixed:', _loadError);
            throw _loadError;
        });
    return _loadPromise;
}

async function loadGlslangViaEsmImport(): Promise<GlslangInstance> {
    console.log('[glslang] starting ESM import from', GLSLANG_URL);
    // Vite's static analyzer flags dynamic import URLs; the @vite-ignore
    // comment tells it to leave this alone so the URL is fetched at
    // runtime exactly as written.
    const mod = await import(/* @vite-ignore */ GLSLANG_URL);
    const getGlslang = (mod as { default?: () => Promise<RawGlslangInstance> }).default;
    if (typeof getGlslang !== 'function') {
        throw new Error(
            `glslang module loaded but default export is not a function ` +
            `(got ${typeof getGlslang}). Package version may have changed.`,
        );
    }

    // Hijack console.warn BEFORE init so glslang's `E = console.warn.bind(console)`
    // (which runs synchronously inside the Module factory at init time)
    // captures our buffer-pusher instead of the real console.warn. After
    // init we restore the global, but glslang's bound `E` still points
    // at our hijack — Function.prototype.bind captures the function
    // reference at bind time, not the lookup slot. Without this trick,
    // glslang's per-line ERROR log goes to console.warn (visible in
    // devtools) but never to our buffer, and we fall back to the useless
    // "GLSL compilation failed" thrown message with no line info.
    const buffer: string[] = [];
    const origWarn = console.warn;
    console.warn = (...args: unknown[]) => {
        buffer.push(
            args.map((a) => (typeof a === 'string' ? a : String(a))).join(' '),
        );
    };

    let raw: RawGlslangInstance;
    try {
        raw = await getGlslang();
    } finally {
        // Restore the global console.warn — only the bind-captured reference
        // inside glslang keeps pointing at our buffer-pusher from here on.
        console.warn = origWarn;
    }
    if (typeof raw.compileGLSL !== 'function') {
        throw new Error('glslang instance has no compileGLSL method');
    }

    return {
        compileGLSL(source, stage, debug, spirvVersion) {
            // No per-call hijack needed — glslang's printErr was captured
            // into our buffer-pusher at init time and stays bound there.
            return raw.compileGLSL(source, stage, debug, spirvVersion);
        },
        _printedErrors: buffer,
        _resetPrintedErrors() { buffer.length = 0; },
    };
}

export function __setGlslangInstanceForTest(inst: GlslangInstance | null): void {
    _instance = inst;
    _loadError = null;
    _loadPromise = null;
}
