// Provider registry + default selection. The Models tab uses this to list
// available providers; the agent loop binds to whichever the user picked
// (or the default chosen on first launch).

import type { ChatProvider } from './types';
import { TransformersJSProvider } from './transformers-js';
import { AnthropicProvider } from './anthropic';

export interface ProviderEntry {
    /** Unique key used in localStorage. */
    id: string;
    /** Display label. */
    label: string;
    /** Short note for the UI: "default", "fast but 4K context", etc. */
    note?: string;
    /** Construct a fresh provider instance. Called lazily. */
    factory: () => ChatProvider;
}

const SELECTED_KEY = 'fade.ai.selectedProvider';

export const PROVIDER_CATALOG: ProviderEntry[] = [
    // ── WebGPU variants ─────────────────────────────────────────────────────
    // Fastest path. Chrome/Edge ship stable WebGPU. Firefox has WebGPU but
    // its GPU-process resource lifetime is buggy with transformers.js right
    // now: buffers leak across page reloads (every model reload adds ~2 GB
    // to the GPU process). Firefox users should pick a WASM variant below.
    {
        id: 'transformers-js:qwen-coder-1.5b',
        label: 'Qwen 2.5 Coder 1.5B (WebGPU)',
        note: 'Default for Chrome/Edge — 32K context, fast',
        factory: () => new TransformersJSProvider({
            modelId: 'onnx-community/Qwen2.5-Coder-1.5B-Instruct',
            label: 'Qwen 2.5 Coder 1.5B (WebGPU)',
            dtype: 'q4f16',
            device: 'webgpu',
            maxContext: 32_768,
        }),
    },
    {
        id: 'transformers-js:qwen3-0.6b',
        label: 'Qwen 3 0.6B (WebGPU)',
        note: 'Tiny — fast on low-end hardware, weaker reasoning',
        factory: () => new TransformersJSProvider({
            modelId: 'onnx-community/Qwen3-0.6B-ONNX',
            label: 'Qwen 3 0.6B (WebGPU)',
            dtype: 'q4f16',
            device: 'webgpu',
            maxContext: 32_768,
        }),
    },
    {
        id: 'transformers-js:llama-3.2-1b',
        label: 'Llama 3.2 1B (WebGPU)',
        note: '128K context, broad knowledge',
        factory: () => new TransformersJSProvider({
            modelId: 'onnx-community/Llama-3.2-1B-Instruct',
            label: 'Llama 3.2 1B (WebGPU)',
            dtype: 'q4f16',
            device: 'webgpu',
            maxContext: 131_072,
        }),
    },
    {
        id: 'transformers-js:qwen3-4b',
        label: 'Qwen 3 4B Instruct (WebGPU)',
        note: 'Bigger — follows multi-step instructions / tool calling reliably. ~2.4 GB',
        factory: () => new TransformersJSProvider({
            modelId: 'onnx-community/Qwen3-4B-Instruct-2507-ONNX',
            label: 'Qwen 3 4B Instruct (WebGPU)',
            dtype: 'q4f16',
            device: 'webgpu',
            maxContext: 32_768,
        }),
    },

    // ── WASM variants ───────────────────────────────────────────────────────
    // Slower per-token (~3-5x) but immune to the Firefox GPU-process leak
    // and works on any device with no WebGPU at all. Pick one of these if
    // you're on Firefox or seeing repeated WebGPU buffer crashes.
    {
        id: 'transformers-js:qwen-coder-1.5b-wasm',
        label: 'Qwen 2.5 Coder 1.5B (WASM)',
        note: 'Slower than WebGPU but reliable on Firefox / no-GPU machines',
        factory: () => new TransformersJSProvider({
            modelId: 'onnx-community/Qwen2.5-Coder-1.5B-Instruct',
            label: 'Qwen 2.5 Coder 1.5B (WASM)',
            dtype: 'q4',
            device: 'wasm',
            maxContext: 32_768,
        }),
    },
    {
        id: 'transformers-js:qwen3-0.6b-wasm',
        label: 'Qwen 3 0.6B (WASM)',
        note: 'Tiny + WASM — fastest combination on Firefox',
        factory: () => new TransformersJSProvider({
            modelId: 'onnx-community/Qwen3-0.6B-ONNX',
            label: 'Qwen 3 0.6B (WASM)',
            dtype: 'q4',
            device: 'wasm',
            maxContext: 32_768,
        }),
    },
    {
        id: 'transformers-js:qwen3-4b-wasm',
        label: 'Qwen 3 4B Instruct (WASM)',
        note: 'Best instruction-following on Firefox — but SLOW (~2-5 tok/s). ~2.4 GB',
        factory: () => new TransformersJSProvider({
            modelId: 'onnx-community/Qwen3-4B-Instruct-2507-ONNX',
            label: 'Qwen 3 4B Instruct (WASM)',
            dtype: 'q4',
            device: 'wasm',
            maxContext: 32_768,
        }),
    },

    // ── Claude (Anthropic API) ─────────────────────────────────────────────
    // Network providers — require an Anthropic API key in localStorage.
    // No local weights, no GPU pressure, no WebGPU bugs. Used as a "what
    // happens with a top-tier instruction-follower" comparison point.
    // Still uses the same in-prompt <tool_call> protocol as the local
    // models so the test is apples-to-apples.
    {
        id: 'anthropic:claude-haiku-4-5',
        label: 'Claude Haiku 4.5 (Anthropic API)',
        note: 'Fastest + cheapest Claude. Needs API key — prompted on first load.',
        factory: () => new AnthropicProvider({
            modelId: 'claude-haiku-4-5',
            label: 'Claude Haiku 4.5',
            maxContext: 200_000,
        }),
    },
    {
        id: 'anthropic:claude-sonnet-4-6',
        label: 'Claude Sonnet 4.6 (Anthropic API)',
        note: 'Best speed/quality balance. Needs API key.',
        factory: () => new AnthropicProvider({
            modelId: 'claude-sonnet-4-6',
            label: 'Claude Sonnet 4.6',
            maxContext: 1_000_000,
        }),
    },
    {
        id: 'anthropic:claude-opus-4-7',
        label: 'Claude Opus 4.7 (Anthropic API)',
        note: 'Most capable Claude — slower + more expensive. Needs API key.',
        factory: () => new AnthropicProvider({
            modelId: 'claude-opus-4-7',
            label: 'Claude Opus 4.7',
            maxContext: 1_000_000,
        }),
    },
];

export function getSelectedProviderId(): string {
    return localStorage.getItem(SELECTED_KEY) ?? PROVIDER_CATALOG[0].id;
}

export function setSelectedProviderId(id: string): void {
    localStorage.setItem(SELECTED_KEY, id);
}

export function getProviderEntry(id: string): ProviderEntry | undefined {
    return PROVIDER_CATALOG.find(p => p.id === id);
}

/** Construct the currently-selected provider, falling back to the first
 *  entry if the stored ID is no longer in the catalog. */
export function createSelectedProvider(): ChatProvider {
    const id = getSelectedProviderId();
    const entry = getProviderEntry(id) ?? PROVIDER_CATALOG[0];
    return entry.factory();
}

export { TransformersJSProvider } from './transformers-js';
export { MockProvider, mockTurn } from './mock';
export type { ChatProvider, StreamEvent, Msg, Tool, ProviderCapabilities, ProviderProgress } from './types';
