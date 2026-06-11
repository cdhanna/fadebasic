// Wires the live-session pieces together for main.ts: builds the
// `LiveSessionPanelHost` (transport selection, URL parsing, share-link
// formatting) and mounts the panel.
//
// Stays in this file (not main.ts) so the orchestration logic isn't
// scattered through the bootstrap closure — main.ts just hands us the
// `SessionHost` adapter and an optional GitHub-login hint and we own the
// rest.

import {
    CollabSession,
    mockTransport,
    mountLiveSessionPanel,
    trysteroTransport,
    manualTransport,
    startManualHost as startManualHostHandle,
    startManualJoin as startManualJoinHandle,
    makeIdentity,
    type CollabTransport,
    type LiveSessionPanelController,
    type LiveSessionPanelHost,
    type ManualHostFlow,
    type ManualJoinFlow,
    type ManualStartArgs,
    type SessionHost,
    type StartHostArgs,
    type StartJoinArgs,
} from './index';
import { selectWorkingIceConfig } from './ice-probe';

export interface BootstrapOptions {
    container: HTMLElement;
    /** Adapter the session uses to drive the editor / tabs / workspace. */
    sessionHost: SessionHost;
    /** Optional — GitHub-authed user's login. Pre-fills the display name. */
    getGithubLogin?: () => string | null;
    /** Optional — project name to advertise to guests (UI labelling). */
    getProjectName?: () => string | null;
    /** Hooks that the bootstrap calls around a guest session so a
     *  transient OPFS project gets created/torn down. The session itself
     *  doesn't know about projects — keeping that orchestration up here
     *  means the session stays focused on Yjs + sync, and project
     *  management stays in the main bootstrap where it belongs. */
    guestLifecycle?: GuestLifecycle;
    /** Optional callback to re-sync debug state. The panel's "Force sync
     *  debug data" button delegates here. Bootstrap passes it through to
     *  the panel verbatim; main.ts decides whether to re-broadcast
     *  (host) or re-fetch (guest). */
    forceDebugSync?: () => Promise<void>;
    /** appId namespace. Hard-coded to fade-playground for now. */
    appId?: string;
}

export interface GuestLifecycle {
    /** Called once before the guest's CollabSession is started. The host
     *  app should: save the current project name (so we can restore it on
     *  end), create a transient project named after `roomId`, switch to
     *  it, and return that name. The session then mirrors host files into
     *  this project's OPFS folder. */
    onGuestJoinStart(roomId: string): Promise<{ transientProjectName: string; previousProjectName: string | null }>;
    /** Called once after the guest's CollabSession is destroyed. Host
     *  should switch back to `previousProjectName` (if any) and delete the
     *  transient project's OPFS folder. */
    onGuestLeaveEnd(args: { transientProjectName: string; previousProjectName: string | null }): Promise<void>;
}

export interface LiveSessionHandle {
    controller: LiveSessionPanelController;
    /** Current session, if any. Same as `controller.getSession()`. */
    getSession(): CollabSession | null;
    /** Subscribe to session changes. */
    onSessionChange(cb: (s: CollabSession | null) => void): () => void;
    dispose(): void;
}

const APP_ID_DEFAULT = 'fade-playground';

/** What we put in the URL — `?room=<id>` plus an optional `#<password>`
 *  in the fragment so the password never hits the server's logs. */
function parseUrlJoin(): { roomId: string; password?: string } | null {
    try {
        const url = new URL(window.location.href);
        const room = url.searchParams.get('room');
        if (!room) return null;
        const password = url.hash ? decodeURIComponent(url.hash.replace(/^#/, '')) : undefined;
        return { roomId: room, password };
    } catch { return null; }
}

function clearUrlJoin(): void {
    try {
        const url = new URL(window.location.href);
        url.searchParams.delete('room');
        url.hash = '';
        window.history.replaceState(null, '', url.toString());
    } catch { /* ignore */ }
}

function buildShareLink(roomId: string, password?: string): string {
    try {
        const url = new URL(window.location.href);
        url.searchParams.set('room', roomId);
        if (password) url.hash = encodeURIComponent(password);
        else url.hash = '';
        return url.toString();
    } catch {
        return `?room=${encodeURIComponent(roomId)}` + (password ? `#${encodeURIComponent(password)}` : '');
    }
}

/** 8-char alphanumeric code. Avoids 0/O/1/I to make it speakable. */
function generateRoomId(): string {
    const alphabet = 'abcdefghjkmnpqrstuvwxyz23456789';
    const buf = new Uint8Array(8);
    crypto.getRandomValues(buf);
    let s = '';
    for (let i = 0; i < buf.length; i++) s += alphabet[buf[i] % alphabet.length];
    return s.slice(0, 4) + '-' + s.slice(4);
}

export function bootstrapLiveSession(opts: BootstrapOptions): LiveSessionHandle {
    const appId = opts.appId ?? APP_ID_DEFAULT;

    // Kick off the ICE-config probe at panel mount so the result is
    // typically ready before the user clicks "Host" or "Join". The probe
    // takes up to ~4s for the full → minimal fallback path; deferring
    // it until session-start would make the first session noticeably
    // slow. selectWorkingIceConfig is idempotent + cached, so calling
    // it here and again at join is fine — the second call is a no-op.
    void selectWorkingIceConfig().catch((e) => {
        console.warn('[fade-collab] ICE probe failed', e);
    });

    // Transport preference: real network first, manual second (most
    // reliable fallback when signaling is broken), mock last (test-only).
    const allTransports: CollabTransport[] = [trysteroTransport, manualTransport, mockTransport];

    const panelHost: LiveSessionPanelHost = {
        transports: allTransports,
        suggestedDisplayName: () => opts.getGithubLogin?.() ?? null,
        pendingJoin: () => parseUrlJoin(),
        consumePendingJoin: () => clearUrlJoin(),
        buildShareLink: (roomId, password) => buildShareLink(roomId, password),
        forceDebugSync: opts.forceDebugSync,

        async startHost(args: StartHostArgs): Promise<CollabSession> {
            const transport = allTransports.find((t) => t.id === args.transportId) ?? allTransports[0];
            const roomId = generateRoomId();
            const identity = makeIdentity(args.displayName, {
                githubLogin: opts.getGithubLogin?.() ?? undefined,
            });
            const room = await transport.join({
                appId,
                roomId,
                password: args.password,
                identity,
            });
            const session = new CollabSession(opts.sessionHost, room);
            await session.start({
                role: 'host',
                identity,
                projectName: opts.getProjectName?.() ?? undefined,
            });
            // Stash for the panel's share-link UI (panel reads these off the
            // session via the `(s as any).__roomId` escape hatch).
            (session as any).__roomId = roomId;
            if (args.password) (session as any).__password = args.password;
            return session;
        },

        async startJoin(args: StartJoinArgs): Promise<CollabSession> {
            const transport = allTransports.find((t) => t.id === args.transportId) ?? allTransports[0];
            const identity = makeIdentity(args.displayName, {
                githubLogin: opts.getGithubLogin?.() ?? undefined,
            });
            // Create the transient OPFS project BEFORE joining the room.
            // The session's guest-side mirror logic writes into the active
            // workspace, so we need the workspace pointing at the new
            // sandbox before any Y.Doc sync messages arrive.
            let transientArgs: { transientProjectName: string; previousProjectName: string | null } | null = null;
            if (opts.guestLifecycle) {
                transientArgs = await opts.guestLifecycle.onGuestJoinStart(args.roomId);
            }
            const room = await transport.join({
                appId,
                roomId: args.roomId,
                password: args.password,
                identity,
            });
            const session = new CollabSession(opts.sessionHost, room);
            await session.start({ role: 'guest', identity });
            (session as any).__roomId = args.roomId;
            if (args.password) (session as any).__password = args.password;
            if (transientArgs) (session as any).__transient = transientArgs;
            return session;
        },

        async startManualHost(args: ManualStartArgs): Promise<ManualHostFlow> {
            const roomId = generateRoomId();
            const identity = makeIdentity(args.displayName, {
                githubLogin: opts.getGithubLogin?.() ?? undefined,
            });
            // Generate the offer eagerly — the wizard wants to display
            // it as soon as the host clicks "Start hosting (manual)".
            const handle = await startManualHostHandle({ roomId });
            return {
                roomId,
                offer: handle.offer,
                async acceptAnswer(answerBlob: string): Promise<CollabSession> {
                    const room = await handle.acceptAnswer(answerBlob);
                    const session = new CollabSession(opts.sessionHost, room);
                    await session.start({
                        role: 'host',
                        identity,
                        projectName: opts.getProjectName?.() ?? undefined,
                    });
                    (session as any).__roomId = roomId;
                    if (args.password) (session as any).__password = args.password;
                    (session as any).__manual = true;
                    return session;
                },
                cancel: () => handle.cancel(),
            };
        },

        async startManualJoin(args: ManualStartArgs): Promise<ManualJoinFlow> {
            const identity = makeIdentity(args.displayName, {
                githubLogin: opts.getGithubLogin?.() ?? undefined,
            });
            const handle = await startManualJoinHandle({});
            return {
                async acceptOffer(offerBlob: string) {
                    const inner = await handle.acceptOffer(offerBlob);
                    const sessionPromise = (async () => {
                        // The roomId we learn from the offer envelope
                        // doubles as the transient OPFS project name —
                        // same shape as the trystero join above. If
                        // missing (older host blob), fall back to a
                        // synthesised name so the guest still gets an
                        // isolated workspace.
                        const projectKey = inner.roomId ?? `manual-${Math.random().toString(36).slice(2, 10)}`;
                        let transientArgs: { transientProjectName: string; previousProjectName: string | null } | null = null;
                        if (opts.guestLifecycle) {
                            transientArgs = await opts.guestLifecycle.onGuestJoinStart(projectKey);
                        }
                        const room = await inner.whenConnected;
                        const session = new CollabSession(opts.sessionHost, room);
                        await session.start({ role: 'guest', identity });
                        (session as any).__roomId = projectKey;
                        if (args.password) (session as any).__password = args.password;
                        (session as any).__manual = true;
                        if (transientArgs) (session as any).__transient = transientArgs;
                        return session;
                    })();
                    return { answer: inner.answer, sessionPromise, roomId: inner.roomId };
                },
                cancel: () => handle.cancel(),
            };
        },
    };

    const controller = mountLiveSessionPanel({
        container: opts.container,
        host: panelHost,
    });

    // Wrap the controller's endSession so we tear down the transient
    // project AFTER the underlying session is destroyed. The panel
    // already calls session.destroy() — we run the lifecycle hook on top.
    const inner = controller.onSessionChange((next) => {
        if (next != null) return;
        // Session just ended. If it was a guest session with a transient
        // project, dispose of it.
        // Note: by the time this fires, the session reference is gone
        // from the panel — but we stashed the lifecycle args on the
        // session object earlier. We can't reach it from here. Solution
        // is to wrap startJoin to keep its own reference. Done via the
        // listener installed at startJoin time.
        void inner;  // placate unused-binding warning
    });

    // Track active guest lifecycle args via an external wrapper. When the
    // session becomes null we know we just ended; check if the previous
    // session had transient args and run cleanup. Keeping a separate
    // pointer is simpler than threading state through the panel.
    let pendingTransient: { transientProjectName: string; previousProjectName: string | null } | null = null;
    controller.onSessionChange((next) => {
        if (next) {
            pendingTransient = (next as any).__transient ?? null;
            return;
        }
        const args = pendingTransient;
        pendingTransient = null;
        if (args && opts.guestLifecycle) {
            void opts.guestLifecycle.onGuestLeaveEnd(args).catch((e) => {
                console.warn('[fade-collab] onGuestLeaveEnd failed', e);
            });
        }
    });

    // Expose the host shim for headless probes — drives startHost /
    // startJoin without touching the panel UI. We also inject the new
    // session into the panel controller so onSessionChange subscribers
    // (incl. __fadeCollab, installCollabRuntimeListeners) wire up the
    // same way they would after a real button-click flow.
    (window as any).__fadeCollabBootstrap = {
        startHost: async (args: StartHostArgs) => {
            const session = await panelHost.startHost(args);
            controller.injectSessionForTesting(session);
            return session;
        },
        startJoin: async (args: StartJoinArgs) => {
            const session = await panelHost.startJoin(args);
            controller.injectSessionForTesting(session);
            return session;
        },
    };
    return {
        controller,
        getSession: () => controller.getSession(),
        onSessionChange: (cb) => controller.onSessionChange(cb),
        dispose: () => controller.dispose(),
    };
}
