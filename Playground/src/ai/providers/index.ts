// Provider registry + default selection. The Models tab uses this to list
// available providers; the agent loop binds to whichever the user picked
// (or the default chosen on first launch).
//
// v2 catalog is 4B-only: smaller local models don't reliably follow the
// in-prompt tool protocol, so we don't offer them as agent backends.

import type { ChatProvider } from './types';
import { WorkerBridgeProvider } from './worker-bridge';
import { GhostBotProvider } from './ghostbot-provider';

export interface ProviderEntry {
    /** Unique key used in localStorage. */
    id: string;
    /** Display label. */
    label: string;
    /** Short note for the UI. */
    note?: string;
    /** Construct a fresh provider instance. Called lazily. */
    factory: () => ChatProvider;
}

const SELECTED_KEY = 'fade.ai.selectedProvider';
/** Set after a successful load; used to auto-warm the model on next visit. */
const LAST_LOADED_KEY = 'fade.ai.lastLoadedProvider';

/** Default provider id — local GhostBot over WebRTC. */
export const DEFAULT_PROVIDER_ID = 'ghostbot:local';

export const PROVIDER_CATALOG: ProviderEntry[] = [
    {
        id: 'ghostbot:local',
        label: 'GhostBot (local llama.cpp)',
        note: 'Default — pairs with the GhostBot desktop app via join code. Best tool use.',
        factory: () => new GhostBotProvider(),
    },
    {
        id: 'transformers-js:qwen3-4b',
        label: 'Qwen 3 4B Instruct (WebGPU)',
        note: 'In-browser fallback — ~2.4 GB. Needs Chrome/Edge WebGPU.',
        factory: () => new WorkerBridgeProvider({
            modelId: 'onnx-community/Qwen3-4B-Instruct-2507-ONNX',
            label: 'Qwen 3 4B Instruct (WebGPU)',
            dtype: 'q4f16',
            device: 'webgpu',
            maxContext: 32_768,
        }),
    },
    {
        id: 'transformers-js:qwen3-4b-wasm',
        label: 'Qwen 3 4B Instruct (WASM)',
        note: 'Slower (~2–5 tok/s) but reliable on Firefox / no-GPU machines.',
        factory: () => new WorkerBridgeProvider({
            modelId: 'onnx-community/Qwen3-4B-Instruct-2507-ONNX',
            label: 'Qwen 3 4B Instruct (WASM)',
            dtype: 'q4',
            device: 'wasm',
            maxContext: 32_768,
        }),
    },
];

export function getSelectedProviderId(): string {
    const stored = localStorage.getItem(SELECTED_KEY);
    if (stored && getProviderEntry(stored)) return stored;
    return DEFAULT_PROVIDER_ID;
}

export function setSelectedProviderId(id: string): void {
    localStorage.setItem(SELECTED_KEY, id);
}

/** Remember that this provider finished loading successfully. */
export function markProviderLoaded(id: string): void {
    localStorage.setItem(LAST_LOADED_KEY, id);
}

/** True when the selected provider was loaded in a previous session and
 *  should be auto-warmed on startup (weights live in IndexedDB — only
 *  the in-memory ORT/WebGPU session needs rebuilding). GhostBot is
 *  excluded — it requires a live desktop peer. */
export function shouldAutoLoadProvider(): boolean {
    if (getSelectedProviderId() === 'ghostbot:local') return false;
    return localStorage.getItem(LAST_LOADED_KEY) === getSelectedProviderId();
}

export function getProviderEntry(id: string): ProviderEntry | undefined {
    return PROVIDER_CATALOG.find(p => p.id === id);
}

/** Construct the currently-selected provider, falling back to the default
 *  if the stored ID is no longer in the catalog (e.g. after we dropped
 *  smaller models). */
export function createSelectedProvider(): ChatProvider {
    const id = getSelectedProviderId();
    const entry = getProviderEntry(id) ?? PROVIDER_CATALOG[0];
    return entry.factory();
}

export { GhostBotProvider } from './ghostbot-provider';
export type { GhostConnectionState, GhostConnectionStatus } from './ghostbot-provider';
export { generateJoinCode } from './ghostbot-protocol';
export { TransformersJSProvider } from './transformers-js';
export { WorkerBridgeProvider, disposeInferenceWorker } from './worker-bridge';
export { MockProvider, mockTurn } from './mock';
export type { ChatProvider, StreamEvent, Msg, Tool, ProviderCapabilities, ProviderProgress } from './types';
