// Facade DebugAdapter — forwards every method to a swappable inner
// adapter, and bridges event subscribers across adapter swaps. main.ts
// references this as `dbg` so the existing ~30 call sites don't need
// to know whether the active runtime is local (this peer's runner) or
// remote (another peer's debugger via session.request).
//
// Swapping happens in three cases:
//   1. Bootstrap — initial inner adapter is the local one.
//   2. A remote peer starts debugging — inner becomes the remote adapter.
//   3. The remote initiator stops debugging — inner reverts to local.
//
// Each method that takes args just forwards to `current.<method>(args)`.
// Property-style state reads (`status`, `paused`, `currentLocation`)
// are getters that read from `current` at call time so consumers see a
// consistent view immediately after a swap. The event-subscription
// bridge maintains the facade's own subscriber set; on swap we
// unsubscribe from the old inner and resubscribe to the new one with
// a single fan-out function that visits every facade subscriber.

import type {
    DebugAdapter,
    DebugEvent,
    DebugStatus,
    ResolvedInstruction,
    StepKind,
} from './adapter';

export interface FacadeDebugAdapter extends DebugAdapter {
    /** Swap the inner adapter. Existing subscribers stay attached;
     *  they'll receive events from the new adapter from this point on.
     *  Calling with the same adapter is a no-op. */
    setInner(next: DebugAdapter): void;
    /** Read the currently-active inner adapter. Useful for host-side
     *  RPC handlers that always want to act on the LOCAL adapter even
     *  when the facade is currently pointed at the remote one. */
    getInner(): DebugAdapter;
}

export function createFacadeDebugAdapter(initial: DebugAdapter): FacadeDebugAdapter {
    let current = initial;
    const subscribers = new Set<(event: DebugEvent) => void>();
    let unsubscribeFromInner: (() => void) | null = null;

    const fanOut = (event: DebugEvent) => {
        for (const cb of subscribers) {
            try { cb(event); }
            catch (e) { console.error('[debug-facade] subscriber threw', e); }
        }
    };

    const attachToInner = () => {
        unsubscribeFromInner = current.onDebugEvent(fanOut);
    };
    attachToInner();

    return {
        get kind(): 'local' | 'remote' { return current.kind; },

        // ── Lifecycle ────────────────────────────────────────────────────
        start: (source: string) => current.start(source),
        startTest: (source: string, testName: string) => current.startTest(source, testName),
        terminate: () => current.terminate(),

        // ── Control ──────────────────────────────────────────────────────
        continue: () => current.continue(),
        pause: () => current.pause(),
        step: (kind: StepKind) => current.step(kind),

        // ── Breakpoints ──────────────────────────────────────────────────
        setBreakpoints: (payload: any) => current.setBreakpoints(payload),

        // ── Inspection ───────────────────────────────────────────────────
        stackFrames: () => current.stackFrames(),
        scopes: (frameId: number) => current.scopes(frameId),
        expandVariable: (variableId: number) => current.expandVariable(variableId),
        eval: (frameId: number, expression: string) => current.eval(frameId, expression),
        repl: (frameId: number, code: string) => current.repl(frameId, code),
        setVariable: (frameId: number, variableId: number, rhs: string) =>
            current.setVariable(frameId, variableId, rhs),
        resolveInstruction: (insIndex: number): Promise<ResolvedInstruction | null> =>
            current.resolveInstruction(insIndex),

        // ── State snapshot (read from `current` on every access) ─────────
        get status(): DebugStatus { return current.status; },
        get paused(): boolean { return current.paused; },
        get currentLocation(): { file: string; line: number } | null { return current.currentLocation; },

        // ── Event subscription ───────────────────────────────────────────
        // Subscribers register with the facade, not the inner adapter,
        // so they survive swaps. `fanOut` is attached to whichever
        // adapter is current; swapping moves that attachment.
        onDebugEvent(handler: (event: DebugEvent) => void): () => void {
            subscribers.add(handler);
            return () => subscribers.delete(handler);
        },

        // ── Facade controls ──────────────────────────────────────────────
        setInner(next: DebugAdapter): void {
            if (next === current) return;
            if (unsubscribeFromInner) {
                try { unsubscribeFromInner(); } catch { /* ignore */ }
                unsubscribeFromInner = null;
            }
            current = next;
            attachToInner();
        },
        getInner(): DebugAdapter { return current; },
    };
}
