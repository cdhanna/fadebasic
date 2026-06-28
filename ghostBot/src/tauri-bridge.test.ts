import { afterEach, describe, expect, it } from 'vitest';
import { isTauriApp, tauriInvoke } from './tauri-bridge';

describe('tauri bridge', () => {
    afterEach(() => {
        const w = globalThis as typeof globalThis & {
            __TAURI_INTERNALS__?: unknown;
            __TAURI__?: unknown;
        };
        delete w.__TAURI_INTERNALS__;
        delete w.__TAURI__;
    });

    it('detects non-Tauri environments', () => {
        expect(isTauriApp()).toBe(false);
    });

    it('detects Tauri globals', () => {
        const g = globalThis as Record<string, unknown>;
        g.window = globalThis;
        g.__TAURI_INTERNALS__ = {};
        expect(isTauriApp()).toBe(true);
    });

    it('refuses invoke outside the desktop shell', async () => {
        await expect(tauriInvoke('get_setup_state')).rejects.toThrow(/desktop app/i);
    });
});
