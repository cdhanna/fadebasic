// One-shot WebGPU adapter probe. Used by /model to surface what hardware
// the runtime sees, what the buffer limits are, and whether the
// adapter even resolves — which doubles as a "WebGPU is alive" health
// check after a session crash.
//
// Deliberately does NOT cache. Each call requests a fresh adapter so
// transient GPU process restarts are visible to the user.
//
// Memory: WebGPU does not expose current allocation. That's a deliberate
// browser security decision (side-channel risk). The closest signal you
// can get is `chrome://gpu` and the Chrome Task Manager — see /model
// output for the pointer.

export interface WebGPUSnapshot {
    /** True iff navigator.gpu exists AND requestAdapter() returned an adapter. */
    available: boolean;
    /** Human-readable reason when `available === false`. */
    note?: string;
    /** Fields from GPUAdapterInfo. All optional per spec. */
    vendor?: string;
    architecture?: string;
    device?: string;
    description?: string;
    /** Selected GPULimits values that matter for LLM weight loading. */
    maxBufferSize?: number;
    maxStorageBufferBindingSize?: number;
    maxComputeInvocationsPerWorkgroup?: number;
}

export async function probeWebGPU(): Promise<WebGPUSnapshot> {
    const nav = (globalThis as { navigator?: { gpu?: unknown } }).navigator;
    if (!nav?.gpu) {
        return { available: false, note: 'navigator.gpu not present (browser does not expose WebGPU)' };
    }
    try {
        const gpu = nav.gpu as { requestAdapter(): Promise<GpuAdapterShape | null> };
        const adapter = await gpu.requestAdapter();
        if (!adapter) {
            return {
                available: false,
                note: 'requestAdapter() returned null (no compatible GPU, or WebGPU disabled)',
            };
        }
        // adapter.info is the modern API (Chrome 119+). Older browsers
        // had requestAdapterInfo() — fall back to that if present.
        const info: AdapterInfoShape | undefined = adapter.info
            ?? (typeof adapter.requestAdapterInfo === 'function'
                ? await adapter.requestAdapterInfo()
                : undefined);
        const limits = adapter.limits;
        return {
            available: true,
            vendor: info?.vendor || undefined,
            architecture: info?.architecture || undefined,
            device: info?.device || undefined,
            description: info?.description || undefined,
            maxBufferSize: limits?.maxBufferSize,
            maxStorageBufferBindingSize: limits?.maxStorageBufferBindingSize,
            maxComputeInvocationsPerWorkgroup: limits?.maxComputeInvocationsPerWorkgroup,
        };
    } catch (e) {
        return { available: false, note: (e as Error).message ?? String(e) };
    }
}

/** Format a byte count for /model output. */
export function formatBytes(n: number): string {
    if (n >= 1024 ** 3) return `${(n / (1024 ** 3)).toFixed(2)} GB`;
    if (n >= 1024 ** 2) return `${(n / (1024 ** 2)).toFixed(1)} MB`;
    if (n >= 1024) return `${(n / 1024).toFixed(0)} KB`;
    return `${n} B`;
}

// Minimal type shims — the WebGPU types may not be in scope depending on
// tsconfig.lib. We type just what we read.
interface GpuAdapterShape {
    info?: AdapterInfoShape;
    requestAdapterInfo?: () => Promise<AdapterInfoShape>;
    limits?: {
        maxBufferSize?: number;
        maxStorageBufferBindingSize?: number;
        maxComputeInvocationsPerWorkgroup?: number;
    };
}

interface AdapterInfoShape {
    vendor?: string;
    architecture?: string;
    device?: string;
    description?: string;
}
