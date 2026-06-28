/** Safe wrappers around Tauri IPC — fails clearly when opened in a browser tab. */

export function isTauriApp(): boolean {
    if (typeof window === 'undefined') return false;
    const w = window as Window & { __TAURI_INTERNALS__?: unknown; __TAURI__?: unknown };
    return !!(w.__TAURI_INTERNALS__ ?? w.__TAURI__);
}

const BROWSER_MSG =
    'GhostBot must run as the desktop app, not in a browser tab. '
    + 'From the ghostBot folder run: npm start';

export async function tauriInvoke<T>(cmd: string, args?: Record<string, unknown>): Promise<T> {
    if (!isTauriApp()) {
        throw new Error(BROWSER_MSG);
    }
    const { invoke } = await import('@tauri-apps/api/core');
    return invoke<T>(cmd, args);
}

export async function tauriListen<T>(
    event: string,
    handler: (payload: T) => void,
): Promise<() => void> {
    if (!isTauriApp()) {
        throw new Error(BROWSER_MSG);
    }
    const { listen } = await import('@tauri-apps/api/event');
    return listen<T>(event, (ev) => handler(ev.payload));
}
