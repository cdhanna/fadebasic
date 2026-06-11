// The Live Session dockview panel — the user-facing surface for hosting and
// joining real-time editing sessions. State machine, top to bottom:
//
//   1. Idle           → [Host a session] [Join a session]
//   2. Hosting        → share code, share URL, read-only toggle, peer list
//   3. Joining        → "Connecting…" spinner
//   4. Joined         → host name, peer list, leave button
//
// Lives in a single DOM root. `mountLiveSession(...)` returns a controller
// the bootstrap code uses to drive it.

import type { CollabSession, PeerView, SessionState } from './session';
import type { CollabTransport, TransportId } from './transport';
import type { RoomStatus, Unsubscribe } from './transport';
import { cachedDisplayName, cachedHostPassword, setCachedDisplayName, setCachedHostPassword } from './identity';
import { previewSignalingBlob } from './manual-transport';
import { getEffective, updateUserSetting } from '../../settings';

const CSS_PREFIX = 'fade-live';
const STYLE_ID = `${CSS_PREFIX}-styles`;

export interface LiveSessionPanelHost {
    /** Available transports, in preference order. UI picks the first that
     *  isAvailable; user can override. */
    transports: CollabTransport[];
    /** Start a host session — wires `CollabSession` over the chosen transport. */
    startHost(args: StartHostArgs): Promise<CollabSession>;
    /** Join an existing session by code. */
    startJoin(args: StartJoinArgs): Promise<CollabSession>;
    /** Begin the manual-signaling host wizard. The returned handle's
     *  `.offer` is ready immediately; the wizard shows it for copy,
     *  then drives `acceptAnswer` with the pasted answer. */
    startManualHost(args: ManualStartArgs): Promise<ManualHostFlow>;
    /** Begin the manual-signaling guest wizard. The wizard prompts for
     *  the offer paste, calls `acceptOffer`, displays the answer for
     *  copy, then waits on `sessionPromise`. */
    startManualJoin(args: ManualStartArgs): Promise<ManualJoinFlow>;
    /** Build a share-link the user can copy. roomId + optional password. */
    buildShareLink(roomId: string, password?: string): string;
    /** Detect a pending join from the URL (`?room=…`). Null if absent. */
    pendingJoin(): { roomId: string; password?: string } | null;
    /** Clear `?room=` from the URL after we've consumed it, so reloads don't
     *  retry the join. */
    consumePendingJoin(): void;
    /** GitHub-authed user's login, if available — pre-fills the display
     *  name input. */
    suggestedDisplayName(): string | null;
    /** Re-sync the local debug-state mirror. On the host this re-fetches
     *  frames/scopes from the runtime and re-broadcasts the snapshot to
     *  every observer; on a guest it clears any cached call stack and
     *  pulls a fresh snapshot from the host. Used to recover when
     *  intermittent RPC drops leave the observer's panel stale. */
    forceDebugSync?(): Promise<void>;
}

export interface StartHostArgs {
    displayName: string;
    transportId: TransportId;
    password?: string;
}

export interface StartJoinArgs {
    displayName: string;
    transportId: TransportId;
    roomId: string;
    password?: string;
}

/** Inputs for the multi-step manual signaling flow. Same shape for host
 *  and guest because the manual flow doesn't take a share code (the
 *  pasted SDP envelope carries the session id). */
export interface ManualStartArgs {
    displayName: string;
    password?: string;
}

/** Host-side manual flow handle. The wizard reads `.offer` to display
 *  for copy, then calls `acceptAnswer` once the user has pasted the
 *  guest's answer back in. */
export interface ManualHostFlow {
    /** Identifier for the session, embedded in the offer envelope and
     *  shown in the wizard so both sides can verify they're on the
     *  same call. */
    readonly roomId: string;
    /** Base64-encoded offer blob — ready immediately. */
    readonly offer: string;
    /** Accept the guest's answer; resolves to the live session. Rejects
     *  on parse failure or connection timeout. */
    acceptAnswer(answerBlob: string): Promise<CollabSession>;
    cancel(): void;
}

/** Guest-side manual flow handle. The wizard calls `acceptOffer` with
 *  the host's pasted blob; the result carries the answer blob to copy
 *  back AND a promise that resolves once the host accepts the answer
 *  and the data channel opens. */
export interface ManualJoinFlow {
    acceptOffer(offerBlob: string): Promise<{
        answer: string;
        sessionPromise: Promise<CollabSession>;
        roomId?: string;
    }>;
    cancel(): void;
}

export interface LiveSessionPanelController {
    /** End the current session (if any) and reset the UI to idle. */
    endSession(): Promise<void>;
    /** Programmatically open the join flow with a pre-filled code (e.g.
     *  from a URL deep-link consumed at bootstrap). */
    promptJoin(roomId: string, password?: string): void;
    /** True iff a session is currently running. */
    hasSession(): boolean;
    /** Current session for external code that wants to call notifyFileOpened
     *  etc. Null when idle. */
    getSession(): CollabSession | null;
    /** Test-only: inject a session that was created via panelHost.startHost
     *  directly (bypassing the panel UI). Notifies onSessionChange
     *  subscribers so __fadeCollab etc. get wired up. */
    injectSessionForTesting(session: CollabSession | null): void;
    /** Subscribe to "session started/stopped" events. */
    onSessionChange(cb: (session: CollabSession | null) => void): Unsubscribe;
    dispose(): void;
}

interface MountOptions {
    container: HTMLElement;
    host: LiveSessionPanelHost;
}

export function mountLiveSessionPanel(opts: MountOptions): LiveSessionPanelController {
    injectStyles();

    const root = opts.container;
    root.classList.add(`${CSS_PREFIX}-root`);
    root.replaceChildren();

    let session: CollabSession | null = null;
    let sessionUnsubs: Unsubscribe[] = [];
    let lastState: SessionState | null = null;
    const sessionCbs = new Set<(s: CollabSession | null) => void>();

    /** Cancellation token used during the 'generating' phases. The
     *  underlying startManualHost / startManualJoin promise can't be
     *  aborted mid-flight (ICE gathering is internal to the browser),
     *  so we resolve it and then check this flag in the .then() handler
     *  before transitioning the wizard further. */
    type CancelToken = { value: boolean };

    /** Multi-step manual-signaling wizard state. Non-null shows the
     *  wizard UI in place of the idle screen; transitions to null on
     *  cancel or once the resulting CollabSession is set via setSession.
     *  Discriminated by `phase`. */
    type ManualWizardState =
        | { phase: 'host-generating'; cancelled: CancelToken; error: string | null }
        | { phase: 'host-sharing'; flow: ManualHostFlow; error: string | null }
        | { phase: 'host-connecting'; flow: ManualHostFlow; error: string | null }
        | {
            phase: 'guest-generating';
            cancelled: CancelToken;
            offerDraft: string;
            roomId: string | null;
            error: string | null;
        }
        | {
            phase: 'guest-awaiting-host';
            flow: ManualJoinFlow;
            answer: string;
            offerDraft: string;
            roomId: string | null;
            error: string | null;
        };
    let manualWizard: ManualWizardState | null = null;

    function setManualWizard(next: ManualWizardState | null) {
        // If we're tearing down the wizard entirely (next === null),
        // release any in-flight work and the underlying RTCPeerConnection.
        if (manualWizard && !next) {
            if ('flow' in manualWizard) {
                try { manualWizard.flow.cancel(); } catch { /* ignore */ }
            }
            if ('cancelled' in manualWizard) {
                manualWizard.cancelled.value = true;
            }
        }
        manualWizard = next;
        render();
    }

    /** Kick off the host wizard. Sets state to 'host-generating'
     *  immediately and starts the async work (ICE gathering, offer
     *  creation) in the background. Transitions to 'host-sharing' when
     *  the offer is ready, or back to 'host-generating' with an error
     *  if creation failed. */
    function startManualHostWizard(args: ManualStartArgs) {
        const cancelled: CancelToken = { value: false };
        setManualWizard({ phase: 'host-generating', cancelled, error: null });
        opts.host.startManualHost(args).then(
            (flow) => {
                if (cancelled.value) {
                    try { flow.cancel(); } catch { /* ignore */ }
                    return;
                }
                setManualWizard({ phase: 'host-sharing', flow, error: null });
            },
            (e: Error) => {
                if (cancelled.value) return;
                setManualWizard({ phase: 'host-generating', cancelled, error: e.message });
            },
        );
    }

    /** Kick off the guest wizard. Sets state to 'guest-generating'
     *  immediately and starts the chained async work in the background:
     *  startManualJoin → flow.acceptOffer → sessionPromise. Each step
     *  checks the cancel token before transitioning to the next phase
     *  so a Cancel click during ICE gathering doesn't leak resources or
     *  surprise the user with a session that opens after they bailed. */
    function startManualJoinWizard(args: ManualStartArgs, offerBlob: string, parsedRoomId: string | null) {
        const cancelled: CancelToken = { value: false };
        setManualWizard({
            phase: 'guest-generating',
            cancelled,
            offerDraft: offerBlob,
            roomId: parsedRoomId,
            error: null,
        });
        opts.host.startManualJoin(args).then(
            (flow) => {
                if (cancelled.value) {
                    try { flow.cancel(); } catch { /* ignore */ }
                    return;
                }
                flow.acceptOffer(offerBlob).then(
                    ({ answer, sessionPromise, roomId }) => {
                        if (cancelled.value) {
                            try { flow.cancel(); } catch { /* ignore */ }
                            return;
                        }
                        setManualWizard({
                            phase: 'guest-awaiting-host',
                            flow,
                            answer,
                            offerDraft: offerBlob,
                            roomId: roomId ?? parsedRoomId,
                            error: null,
                        });
                        sessionPromise.then(
                            (newSession) => {
                                if (cancelled.value) {
                                    try { void newSession.destroy(); } catch { /* ignore */ }
                                    return;
                                }
                                manualWizard = null;
                                setSession(newSession);
                            },
                            (e: Error) => {
                                if (cancelled.value) return;
                                setManualWizard({
                                    phase: 'guest-awaiting-host',
                                    flow,
                                    answer,
                                    offerDraft: offerBlob,
                                    roomId: roomId ?? parsedRoomId,
                                    error: e.message,
                                });
                            },
                        );
                    },
                    (e: Error) => {
                        if (cancelled.value) {
                            try { flow.cancel(); } catch { /* ignore */ }
                            return;
                        }
                        setManualWizard({
                            phase: 'guest-generating',
                            cancelled,
                            offerDraft: offerBlob,
                            roomId: parsedRoomId,
                            error: e.message,
                        });
                    },
                );
            },
            (e: Error) => {
                if (cancelled.value) return;
                setManualWizard({
                    phase: 'guest-generating',
                    cancelled,
                    offerDraft: offerBlob,
                    roomId: parsedRoomId,
                    error: e.message,
                });
            },
        );
    }

    function setSession(next: CollabSession | null) {
        for (const u of sessionUnsubs) { try { u(); } catch { /* ignore */ } }
        sessionUnsubs = [];
        session = next;
        lastState = next?.getState() ?? null;
        if (next) {
            sessionUnsubs.push(next.onStateChange((s) => {
                lastState = s;
                render();
            }));
        }
        for (const cb of sessionCbs) try { cb(next); } catch { /* ignore */ }
        render();
    }

    async function endSession() {
        if (!session) return;
        const s = session;
        setSession(null);
        try { await s.destroy(); } catch (e) { console.warn('[fade-collab] destroy failed', e); }
    }

    // ─── render ─────────────────────────────────────────────────────────
    function render() {
        root.replaceChildren();
        const header = el('div', `${CSS_PREFIX}-header`);
        header.textContent = 'Live Session';
        root.appendChild(header);

        const body = el('div', `${CSS_PREFIX}-body`);
        root.appendChild(body);

        if (session) {
            body.appendChild(renderActive(session, lastState));
            return;
        }
        if (manualWizard) {
            body.appendChild(renderManualWizard(manualWizard));
            return;
        }
        body.appendChild(renderIdle());
    }

    function renderIdle(): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-idle`);
        wrap.appendChild(infoBlock(
            'Edit together in real time',
            'Host a session to share your workspace with someone, or join a session using a code they shared with you. No accounts needed.',
        ));

        const actions = el('div', `${CSS_PREFIX}-row`);
        const hostBtn = btn('Host a session', () => promptHost());
        const joinBtn = btn('Join a session', () => promptJoin(), 'secondary');
        actions.append(hostBtn, joinBtn);
        wrap.appendChild(actions);

        // If we arrived via ?room=…, surface a one-tap join shortcut.
        const pending = opts.host.pendingJoin();
        if (pending) {
            const banner = el('div', `${CSS_PREFIX}-banner`);
            banner.textContent = `You were invited to a session (${pending.roomId}).`;
            const ok = btn('Join now', () => promptJoin(pending.roomId, pending.password));
            banner.appendChild(ok);
            wrap.appendChild(banner);
        }
        return wrap;
    }

    /** Manual-signaling wizard. Renders one of five phases depending on
     *  the discriminated state. Cancelling at any point returns the
     *  panel to idle and disposes any RTCPeerConnection that's been
     *  created. */
    function renderManualWizard(state: ManualWizardState): HTMLElement {
        switch (state.phase) {
            case 'host-generating': return renderManualHostGenerating(state);
            case 'host-sharing': return renderManualHostSharing(state);
            case 'host-connecting': return renderManualHostConnecting(state);
            case 'guest-generating': return renderManualGuestGenerating(state);
            case 'guest-awaiting-host': return renderManualGuestAwaitingHost(state);
        }
    }

    /** Reusable wizard chrome: title + optional session sub-id + body. */
    function buildWizardFrame(title: string, sessionLabel: string | null): { wrap: HTMLElement; body: HTMLElement } {
        const wrap = el('div', `${CSS_PREFIX}-wizard`);
        const head = el('div', `${CSS_PREFIX}-wizard-head`);
        const t = el('div', `${CSS_PREFIX}-wizard-title`);
        t.textContent = title;
        head.appendChild(t);
        if (sessionLabel) {
            const sub = el('div', `${CSS_PREFIX}-wizard-sub`);
            sub.textContent = sessionLabel;
            head.appendChild(sub);
        }
        wrap.appendChild(head);
        return { wrap, body: wrap };
    }

    /** Spinner + status line shown during long-running async steps. */
    function spinnerStatus(text: string, hint: string | null): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-wizard-status`);
        const spin = el('span', `${CSS_PREFIX}-wizard-spinner`);
        const label = el('span', `${CSS_PREFIX}-wizard-status-label`);
        label.textContent = text;
        wrap.append(spin, label);
        if (hint) {
            const h = el('div', `${CSS_PREFIX}-hint`);
            h.textContent = hint;
            wrap.appendChild(h);
        }
        return wrap;
    }

    function renderManualHostGenerating(state: Extract<ManualWizardState, { phase: 'host-generating' }>): HTMLElement {
        const { wrap } = buildWizardFrame('Manual signaling — host', null);
        const intro = el('div', `${CSS_PREFIX}-hint`);
        intro.textContent = 'Setting up a direct connection. No external signaling — just an offer/answer paste.';
        wrap.appendChild(intro);

        wrap.appendChild(spinnerStatus(
            'Generating offer…',
            'Gathering network candidates (STUN). Usually 1–4 seconds.',
        ));

        if (state.error) {
            const err = el('div', `${CSS_PREFIX}-wizard-error`);
            err.textContent = state.error;
            wrap.appendChild(err);
        }

        const acts = el('div', `${CSS_PREFIX}-row`);
        acts.appendChild(btn('Cancel', () => setManualWizard(null), 'secondary'));
        wrap.appendChild(acts);
        return wrap;
    }

    function renderManualHostSharing(state: Extract<ManualWizardState, { phase: 'host-sharing' }>): HTMLElement {
        const { wrap } = buildWizardFrame('Manual signaling — host', `Session ${state.flow.roomId}`);

        const intro = el('div', `${CSS_PREFIX}-hint`);
        intro.textContent =
            'Step 1 of 2 — Copy the offer below and send it to your collaborator '
            + '(any chat / email works). When they paste their answer back to you, '
            + 'paste it in the box below.';
        wrap.appendChild(intro);

        wrap.appendChild(label('Your offer (send to collaborator)'));
        const offerTa = document.createElement('textarea');
        offerTa.className = `${CSS_PREFIX}-wizard-blob`;
        offerTa.readOnly = true;
        offerTa.value = state.flow.offer;
        offerTa.rows = 4;
        offerTa.spellcheck = false;
        offerTa.addEventListener('focus', () => offerTa.select());
        wrap.appendChild(offerTa);
        const offerActions = el('div', `${CSS_PREFIX}-row`);
        offerActions.appendChild(btn('Copy offer', () => copyToClipboard(state.flow.offer), 'secondary'));
        wrap.appendChild(offerActions);

        wrap.appendChild(label('Their answer (paste here)'));
        const ansTa = document.createElement('textarea');
        ansTa.className = `${CSS_PREFIX}-wizard-blob`;
        ansTa.rows = 4;
        ansTa.spellcheck = false;
        ansTa.placeholder = 'Paste the answer your collaborator sent back…';
        wrap.appendChild(ansTa);

        if (state.error) {
            const errBox = el('div', `${CSS_PREFIX}-wizard-error`);
            errBox.textContent = state.error;
            wrap.appendChild(errBox);
        }

        const acts = el('div', `${CSS_PREFIX}-row`);
        acts.appendChild(btn('Cancel', () => setManualWizard(null), 'secondary'));
        acts.appendChild(btn('Accept answer & connect', () => {
            const blob = ansTa.value.trim();
            if (!blob) {
                setManualWizard({ ...state, error: 'Paste your collaborator’s answer first.' });
                return;
            }
            setManualWizard({ phase: 'host-connecting', flow: state.flow, error: null });
            state.flow.acceptAnswer(blob).then(
                (newSession) => {
                    manualWizard = null;
                    setSession(newSession);
                },
                (e: Error) => {
                    // Bounce back to the sharing step so the user can
                    // see the error and try a different paste.
                    setManualWizard({ phase: 'host-sharing', flow: state.flow, error: e.message });
                },
            );
        }));
        wrap.appendChild(acts);
        return wrap;
    }

    function renderManualHostConnecting(state: Extract<ManualWizardState, { phase: 'host-connecting' }>): HTMLElement {
        const { wrap } = buildWizardFrame('Manual signaling — host', `Session ${state.flow.roomId}`);
        wrap.appendChild(spinnerStatus(
            'Connecting…',
            'Negotiating ICE and opening the data channel. If this hangs for more than 30 seconds, '
                + 'your network may need a TURN server (Settings → Live Session).',
        ));
        const acts = el('div', `${CSS_PREFIX}-row`);
        acts.appendChild(btn('Cancel', () => setManualWizard(null), 'secondary'));
        wrap.appendChild(acts);
        return wrap;
    }

    function renderManualGuestGenerating(state: Extract<ManualWizardState, { phase: 'guest-generating' }>): HTMLElement {
        const { wrap } = buildWizardFrame(
            'Manual signaling — join',
            state.roomId ? `Session ${state.roomId}` : null,
        );
        wrap.appendChild(spinnerStatus(
            'Generating your answer…',
            'Reading the host’s offer and gathering network candidates (STUN). Usually 1–4 seconds.',
        ));
        if (state.error) {
            const err = el('div', `${CSS_PREFIX}-wizard-error`);
            err.textContent = state.error;
            wrap.appendChild(err);
        }
        const acts = el('div', `${CSS_PREFIX}-row`);
        acts.appendChild(btn('Cancel', () => setManualWizard(null), 'secondary'));
        wrap.appendChild(acts);
        return wrap;
    }

    function renderManualGuestAwaitingHost(state: Extract<ManualWizardState, { phase: 'guest-awaiting-host' }>): HTMLElement {
        const { wrap } = buildWizardFrame(
            'Manual signaling — join',
            state.roomId ? `Session ${state.roomId}` : null,
        );

        const intro = el('div', `${CSS_PREFIX}-hint`);
        intro.textContent =
            'Step 2 of 2 — Send this answer back to your host. The connection '
            + 'comes up automatically once they accept it.';
        wrap.appendChild(intro);

        wrap.appendChild(label('Your answer (send to host)'));
        const ansTa = document.createElement('textarea');
        ansTa.className = `${CSS_PREFIX}-wizard-blob`;
        ansTa.readOnly = true;
        ansTa.value = state.answer;
        ansTa.rows = 4;
        ansTa.spellcheck = false;
        ansTa.addEventListener('focus', () => ansTa.select());
        wrap.appendChild(ansTa);
        const ansActs = el('div', `${CSS_PREFIX}-row`);
        ansActs.appendChild(btn('Copy answer', () => copyToClipboard(state.answer), 'secondary'));
        wrap.appendChild(ansActs);

        if (state.error) {
            const errBox = el('div', `${CSS_PREFIX}-wizard-error`);
            errBox.textContent = state.error;
            wrap.appendChild(errBox);
        } else {
            wrap.appendChild(spinnerStatus(
                'Waiting for host to accept…',
                'If this hangs for more than 30 seconds, the host hasn’t pasted yet, '
                    + 'or your network may need a TURN server (Settings → Live Session).',
            ));
        }

        const acts = el('div', `${CSS_PREFIX}-row`);
        acts.appendChild(btn('Cancel', () => setManualWizard(null), 'secondary'));
        wrap.appendChild(acts);
        return wrap;
    }

    function renderActive(s: CollabSession, st: SessionState | null): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-active`);
        const role = st?.role ?? 'host';
        const meta = st?.meta ?? null;
        const status = st?.status ?? 'discovering';

        const banner = el('div', `${CSS_PREFIX}-banner ${CSS_PREFIX}-banner-${role}`);
        banner.append(roleBadge(role), statusPill(status));
        if (role === 'host') {
            banner.appendChild(text(' You are hosting.'));
        } else {
            const who = meta?.hostName ? ` ${meta.hostName}'s session` : ' a session';
            banner.appendChild(text(` You joined${who}.`));
        }
        wrap.appendChild(banner);

        const warning = st?.connectionWarning ?? null;
        if (warning) {
            wrap.appendChild(renderConnectionWarning(warning));
        }

        // Transport-level info (e.g. "fell back to minimal ICE config").
        // Always shown when present, regardless of connection-warning
        // state — the user needs to know they're in a degraded mode even
        // when the session is otherwise working fine.
        const note = st?.transportNote ?? null;
        if (note) {
            wrap.appendChild(renderTransportNote(note));
        }

        const sync = st?.sync ?? null;
        if (sync) {
            wrap.appendChild(renderSyncProgress(sync, st));
        }

        if (role === 'host') {
            wrap.appendChild(renderShare(s));
            wrap.appendChild(renderReadOnlyToggle(s, st));
            wrap.appendChild(renderForceSync(s, sync));
            // Host owns the game-frame stream → expose its knobs here
            // alongside the other host-side controls. Observers don't
            // have anything to stream so the controls don't apply.
            wrap.appendChild(renderStreamSettings());
        }
        // Debug-sync affordance — available for both host and guest because
        // the underlying causes differ: host might want to re-broadcast
        // when a runtime event was missed; guest might want to re-fetch
        // when their cached frames/scopes don't match the host's panel.
        // Only shown when the host wired up forceDebugSync.
        if (opts.host.forceDebugSync) {
            wrap.appendChild(renderForceDebugSync(opts.host.forceDebugSync));
        }
        wrap.appendChild(renderPeers(st));

        const actions = el('div', `${CSS_PREFIX}-row`);
        const leaveLabel = role === 'host' ? 'End session' : 'Leave session';
        actions.appendChild(btn(leaveLabel, () => { void endSession(); }, 'danger'));
        wrap.appendChild(actions);

        return wrap;
    }

    function renderShare(s: CollabSession): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-share`);
        const roomId = (s as any).__roomId as string | undefined;
        const password = (s as any).__password as string | undefined;
        const isManual = Boolean((s as any).__manual);
        if (!roomId) return wrap;
        if (isManual) {
            // No share-link / share-code for manual sessions — the SDP
            // was already exchanged out-of-band. Just confirm the
            // direct-connection state so the host has something to
            // anchor on visually.
            wrap.appendChild(label('Connection'));
            const note = el('div', `${CSS_PREFIX}-hint`);
            note.textContent = `Direct connection via manual signaling (session ${roomId}). No share link — invite by exchanging another offer/answer if you want a second guest.`;
            wrap.appendChild(note);
            return wrap;
        }
        const url = opts.host.buildShareLink(roomId, password);

        wrap.appendChild(label('Share code'));
        const codeRow = el('div', `${CSS_PREFIX}-code-row`);
        const codeBox = el('code', `${CSS_PREFIX}-code-box`);
        codeBox.textContent = roomId;
        codeRow.appendChild(codeBox);
        codeRow.appendChild(btn('Copy code', () => copyToClipboard(roomId), 'secondary'));
        wrap.appendChild(codeRow);

        wrap.appendChild(label('Share link'));
        const urlRow = el('div', `${CSS_PREFIX}-code-row`);
        const urlBox = el('code', `${CSS_PREFIX}-code-box ${CSS_PREFIX}-code-box-url`);
        urlBox.textContent = url;
        urlRow.appendChild(urlBox);
        urlRow.appendChild(btn('Copy link', () => copyToClipboard(url), 'secondary'));
        wrap.appendChild(urlRow);

        if (password) {
            wrap.appendChild(label('Password (already encoded in the link)'));
            const pwBox = el('code', `${CSS_PREFIX}-code-box`);
            pwBox.textContent = password;
            wrap.appendChild(pwBox);
        }
        return wrap;
    }

    function renderReadOnlyToggle(s: CollabSession, st: SessionState | null): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-toggle-row`);
        const ro = Boolean(st?.meta?.readOnly);
        const input = document.createElement('input');
        input.type = 'checkbox';
        input.id = `${CSS_PREFIX}-ro`;
        input.checked = ro;
        input.addEventListener('change', () => s.setReadOnly(input.checked));
        const lab = document.createElement('label');
        lab.htmlFor = input.id;
        lab.textContent = 'Read-only for guests';
        wrap.append(input, lab);
        return wrap;
    }

    /** Debug-sync action. Runs the host-supplied `forceDebugSync` callback —
     *  on the host it re-fetches frames/scopes and re-broadcasts; on a
     *  guest it discards the cached call stack and re-pulls from the
     *  host. Brief "Syncing…" state on the button while in flight so the
     *  user gets feedback that the click registered. */
    function renderForceDebugSync(forceDebugSync: () => Promise<void>): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-sync-action`);
        let inFlight = false;
        const button = btn('Force sync debug data', async () => {
            if (inFlight) return;
            inFlight = true;
            button.textContent = 'Syncing…';
            button.setAttribute('disabled', '');
            try { await forceDebugSync(); }
            catch (e) { console.warn('[fade-collab] forceDebugSync failed', e); }
            finally {
                inFlight = false;
                button.textContent = 'Force sync debug data';
                button.removeAttribute('disabled');
            }
        }, 'secondary');
        const hint = el('div', `${CSS_PREFIX}-hint`);
        hint.textContent = 'Re-broadcasts the current debug snapshot (host) or re-fetches it from the host (guest). Use if frames, scopes, or the current line look stale.';
        wrap.append(button, hint);
        return wrap;
    }

    /** Force-sync action (host-only) — re-reads OPFS and overwrites every
     *  peer's mirror. Disabled while a sync is already in flight. */
    function renderForceSync(s: CollabSession, currentSync: { total: number } | null): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-sync-action`);
        const button = btn(
            currentSync ? 'Syncing…' : 'Force sync from disk',
            () => { void s.forceSync(); },
            'secondary',
        );
        if (currentSync) button.setAttribute('disabled', '');
        const hint = el('div', `${CSS_PREFIX}-hint`);
        hint.textContent = 'Re-reads every file from your workspace and overwrites the mirror on each collaborator. Editing pauses for everyone until it completes.';
        wrap.append(button, hint);
        return wrap;
    }

    /** Host-only stream settings — FPS + JPEG quality for the live
     *  game-frame broadcast. Mirrors the values exposed in the Settings
     *  panel under "Live Session", surfaced inline here so a host can
     *  tune the stream without leaving the session. Reads/writes via
     *  the settings module so the runtime listener picks up changes
     *  and restarts the stream on FPS change. */
    function renderStreamSettings(): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-stream-settings`);
        const title = el('div', `${CSS_PREFIX}-stream-settings-title`);
        title.textContent = 'Game stream';
        wrap.appendChild(title);

        const fpsRow = el('label', `${CSS_PREFIX}-stream-settings-row`);
        const fpsLabel = el('span', `${CSS_PREFIX}-stream-settings-label`);
        fpsLabel.textContent = 'FPS';
        const fpsInput = document.createElement('input');
        fpsInput.type = 'number';
        fpsInput.min = '1';
        fpsInput.max = '30';
        fpsInput.step = '1';
        fpsInput.value = String(Number(getEffective('collab.gameFrameFps')));
        fpsInput.className = `${CSS_PREFIX}-stream-settings-input`;
        const fpsHint = el('span', `${CSS_PREFIX}-stream-settings-hint`);
        fpsHint.textContent = '1–30';
        const commitFps = () => {
            const raw = Number.parseInt(fpsInput.value, 10);
            const clamped = Number.isFinite(raw) ? Math.max(1, Math.min(30, raw)) : 12;
            fpsInput.value = String(clamped);
            void updateUserSetting('collab.gameFrameFps', clamped);
        };
        fpsInput.addEventListener('change', commitFps);
        fpsInput.addEventListener('blur', commitFps);
        fpsRow.append(fpsLabel, fpsInput, fpsHint);
        wrap.appendChild(fpsRow);

        const qRow = el('label', `${CSS_PREFIX}-stream-settings-row`);
        const qLabel = el('span', `${CSS_PREFIX}-stream-settings-label`);
        qLabel.textContent = 'Quality';
        const qInput = document.createElement('input');
        qInput.type = 'number';
        qInput.min = '0.1';
        qInput.max = '1';
        qInput.step = '0.05';
        qInput.value = String(Number(getEffective('collab.gameFrameQuality')));
        qInput.className = `${CSS_PREFIX}-stream-settings-input`;
        const qHint = el('span', `${CSS_PREFIX}-stream-settings-hint`);
        qHint.textContent = '0.1–1.0';
        const commitQ = () => {
            const raw = Number.parseFloat(qInput.value);
            const clamped = Number.isFinite(raw) ? Math.max(0.1, Math.min(1, raw)) : 0.55;
            qInput.value = String(Math.round(clamped * 100) / 100);
            void updateUserSetting('collab.gameFrameQuality', clamped);
        };
        qInput.addEventListener('change', commitQ);
        qInput.addEventListener('blur', commitQ);
        qRow.append(qLabel, qInput, qHint);
        wrap.appendChild(qRow);

        const hint = el('div', `${CSS_PREFIX}-hint`);
        hint.textContent = 'FPS changes apply on the next Run; quality changes on the next frame.';
        wrap.appendChild(hint);
        return wrap;
    }

    /** Connection-trouble banner shown when the watchdog fired — host's
     *  message reads as a hint, guest's reads as a clear failure. */
    function renderConnectionWarning(message: string): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-warning`);
        const icon = el('span', `${CSS_PREFIX}-warning-icon`);
        icon.textContent = '⚠';
        const body = el('div', `${CSS_PREFIX}-warning-body`);
        body.textContent = message;
        wrap.append(icon, body);
        return wrap;
    }

    /** Transport-info banner — informational, lower emphasis than the
     *  red connection-warning. Used to surface "we fell back to minimal
     *  ICE config" so the user knows their connectivity is degraded but
     *  they can still likely connect. */
    function renderTransportNote(message: string): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-note`);
        const icon = el('span', `${CSS_PREFIX}-note-icon`);
        icon.textContent = 'ⓘ';
        const body = el('div', `${CSS_PREFIX}-note-body`);
        body.textContent = message;
        wrap.append(icon, body);
        return wrap;
    }

    /** Progress block shown while `meta.sync` is non-null. Both host and
     *  guest render the same block — only the lead-in copy differs. */
    function renderSyncProgress(sync: NonNullable<SessionState['sync']>, st: SessionState | null): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-sync-progress`);
        const initiator = sync.initiatorClientId != null
            ? (st?.peers.find((p) => p.clientId === sync.initiatorClientId)?.identity.displayName ?? 'Host')
            : 'Host';
        const headline = el('div', `${CSS_PREFIX}-sync-headline`);
        headline.textContent = `${initiator} is syncing the workspace`;
        wrap.appendChild(headline);

        const detail = el('div', `${CSS_PREFIX}-sync-detail`);
        const pct = sync.total > 0 ? Math.min(100, Math.round((sync.completed / sync.total) * 100)) : 0;
        detail.textContent = `${sync.completed} of ${sync.total} files (${pct}%)`
            + (sync.currentFile ? ` — ${sync.currentFile}` : '');
        wrap.appendChild(detail);

        const barWrap = el('div', `${CSS_PREFIX}-sync-bar`);
        const fill = el('div', `${CSS_PREFIX}-sync-bar-fill`);
        fill.style.width = `${pct}%`;
        barWrap.appendChild(fill);
        wrap.appendChild(barWrap);

        const note = el('div', `${CSS_PREFIX}-hint`);
        note.textContent = 'Editing is paused while sync is in flight.';
        wrap.appendChild(note);
        return wrap;
    }

    function renderPeers(st: SessionState | null): HTMLElement {
        const wrap = el('div', `${CSS_PREFIX}-peers`);
        wrap.appendChild(label('Collaborators'));
        const list = el('ul', `${CSS_PREFIX}-peer-list`);
        const peers = st?.peers ?? [];
        if (peers.length === 0) {
            const empty = el('li', `${CSS_PREFIX}-peer-empty`);
            empty.textContent = 'No one here yet.';
            list.appendChild(empty);
        } else {
            for (const peer of peers) {
                list.appendChild(renderPeerRow(peer));
            }
        }
        wrap.appendChild(list);
        return wrap;
    }

    function renderPeerRow(peer: PeerView): HTMLElement {
        const li = el('li', `${CSS_PREFIX}-peer-row`);
        const dot = el('span', `${CSS_PREFIX}-peer-dot`);
        dot.style.backgroundColor = peer.identity.color;
        const name = el('span', `${CSS_PREFIX}-peer-name`);
        name.textContent = peer.identity.displayName + (peer.isSelf ? ' (you)' : '');
        if (peer.identity.githubLogin) name.title = `GitHub: ${peer.identity.githubLogin}`;
        const role = el('span', `${CSS_PREFIX}-peer-role`);
        role.textContent = peer.role;
        const where = el('span', `${CSS_PREFIX}-peer-file`);
        where.textContent = peer.activeFile ?? '—';
        li.append(dot, name, role, where);
        return li;
    }

    // ─── modal flows ────────────────────────────────────────────────────
    function promptHost() {
        openModal({
            title: 'Host a session',
            fields: [
                { name: 'displayName', label: 'Your display name', value: opts.host.suggestedDisplayName() ?? cachedDisplayName() ?? '' },
                // Pre-fill with the last password the user chose when
                // hosting. Saves them retyping for repeated sessions
                // (common when teaching, demoing, etc.). The eye-toggle
                // on the field lets them peek to confirm what got
                // pre-filled.
                { name: 'password', label: 'Password (optional)', value: cachedHostPassword() ?? '', type: 'password' },
            ],
            transports: opts.host.transports,
            submitLabel: 'Start hosting',
            onSubmit: async (vals, transportId) => {
                const name = vals.displayName.trim();
                if (!name) return 'Display name is required.';
                setCachedDisplayName(name);
                // Stash whatever password they ended up with (including
                // empty — they may have deliberately cleared the
                // remembered value to host without one).
                setCachedHostPassword(vals.password);
                if (transportId === 'manual') {
                    // Don't await ICE gathering here — kick the wizard
                    // into its 'host-generating' state immediately so the
                    // user sees a spinner instead of staring at a frozen
                    // modal for 1–4 seconds.
                    startManualHostWizard({
                        displayName: name,
                        password: vals.password || undefined,
                    });
                    return null;
                }
                try {
                    const s = await opts.host.startHost({
                        displayName: name,
                        password: vals.password || undefined,
                        transportId,
                    });
                    setSession(s);
                    return null;
                } catch (e) {
                    return `Failed to start: ${(e as Error).message}`;
                }
            },
        });
    }

    function promptJoin(prefilledRoom?: string, prefilledPassword?: string) {
        openModal({
            title: 'Join a session',
            fields: [
                { name: 'displayName', label: 'Your display name', value: opts.host.suggestedDisplayName() ?? cachedDisplayName() ?? '' },
                {
                    name: 'roomId',
                    label: 'Share code',
                    value: prefilledRoom ?? '',
                    // Manual signaling doesn't use a share code — the
                    // SDP envelope carries everything.
                    hideWhenTransport: 'manual',
                },
                {
                    name: 'offer',
                    label: 'Host’s offer (paste here)',
                    value: '',
                    type: 'textarea',
                    showOnlyWhenTransport: 'manual',
                    placeholder: 'Paste the offer blob your host sent you…',
                    hint: 'The host generates this blob from the Live Session panel after picking the Manual connection.',
                },
                { name: 'password', label: 'Password (if required)', value: prefilledPassword ?? '', type: 'password' },
            ],
            transports: opts.host.transports,
            submitLabel: 'Join',
            onSubmit: async (vals, transportId) => {
                const name = vals.displayName.trim();
                const roomId = vals.roomId.trim();
                if (!name) return 'Display name is required.';
                setCachedDisplayName(name);
                if (transportId === 'manual') {
                    const offerBlob = (vals.offer ?? '').trim();
                    if (!offerBlob) {
                        return 'Paste the offer blob your host sent you.';
                    }
                    // Validate the paste parses synchronously so the user
                    // gets feedback inside the modal, not later in the
                    // wizard. Real setRemoteDescription happens later.
                    let preview;
                    try { preview = previewSignalingBlob(offerBlob); }
                    catch (e) { return `Invalid offer: ${(e as Error).message}`; }
                    if (preview.kind !== 'offer') {
                        return 'That blob is an answer, not an offer. Make sure your host sent the FIRST blob.';
                    }
                    startManualJoinWizard(
                        { displayName: name, password: vals.password || undefined },
                        offerBlob,
                        preview.roomId ?? null,
                    );
                    opts.host.consumePendingJoin();
                    return null;
                }
                if (!roomId) return 'Share code is required.';
                try {
                    const s = await opts.host.startJoin({
                        displayName: name,
                        roomId,
                        password: vals.password || undefined,
                        transportId,
                    });
                    setSession(s);
                    opts.host.consumePendingJoin();
                    return null;
                } catch (e) {
                    return `Failed to join: ${(e as Error).message}`;
                }
            },
        });
    }

    // Auto-prompt the join flow if we arrived from a share link AND the
    // user hasn't already started something. Defer to the next tick so the
    // initial render happens first.
    setTimeout(() => {
        if (session) return;
        const pending = opts.host.pendingJoin();
        if (pending) promptJoin(pending.roomId, pending.password);
    }, 100);

    render();

    return {
        endSession,
        promptJoin: (roomId, password) => promptJoin(roomId, password),
        hasSession: () => session !== null,
        getSession: () => session,
        // Used by test scripts that drive panelHost.startHost / startJoin
        // directly so they need a way to notify the controller too.
        injectSessionForTesting: (s: CollabSession | null) => setSession(s),
        onSessionChange: (cb) => { sessionCbs.add(cb); return () => sessionCbs.delete(cb); },
        dispose: () => {
            for (const u of sessionUnsubs) { try { u(); } catch { /* ignore */ } }
            sessionUnsubs = [];
            // Tear down a half-open manual signaling flow too, so the
            // underlying RTCPeerConnection doesn't leak. setManualWizard
            // knows how to cancel the right resources for whichever
            // phase the wizard is in.
            if (manualWizard) setManualWizard(null);
            void endSession();
            root.replaceChildren();
        },
    };
}

// ── modal ──────────────────────────────────────────────────────────────

interface ModalField {
    name: string;
    label: string;
    value: string;
    type?: 'text' | 'password' | 'textarea';
    /** Hide (and treat as optional) when the selected transport matches.
     *  Used for the share-code field in the join modal — manual signaling
     *  carries the connection in the pasted SDP envelope and has no
     *  separate share code to type in. */
    hideWhenTransport?: TransportId;
    /** Hide unless the selected transport matches. Inverse of
     *  `hideWhenTransport`. Used for the offer-paste field in the join
     *  modal — only relevant when the user picks Manual. */
    showOnlyWhenTransport?: TransportId;
    /** Optional help text rendered below the input. */
    hint?: string;
    /** Placeholder shown when the input is empty. */
    placeholder?: string;
}

interface ModalOpts {
    title: string;
    fields: ModalField[];
    transports: CollabTransport[];
    submitLabel: string;
    onSubmit(values: Record<string, string>, transportId: TransportId): Promise<string | null>;
}

function openModal(opts: ModalOpts) {
    const backdrop = el('div', `${CSS_PREFIX}-modal-backdrop`);
    const modal = el('div', `${CSS_PREFIX}-modal`);
    const close = () => backdrop.remove();
    backdrop.addEventListener('click', (e) => { if (e.target === backdrop) close(); });

    const h = document.createElement('h3');
    h.textContent = opts.title;
    modal.appendChild(h);

    const form = document.createElement('form');
    form.className = `${CSS_PREFIX}-modal-form`;
    const inputs: Record<string, HTMLInputElement | HTMLTextAreaElement> = {};
    /** Field rows keyed by name, so the transport-picker change handler
     *  can show/hide individual rows (e.g. the share-code row when
     *  switching to manual signaling). */
    const fieldRows: Record<string, { row: HTMLElement; field: ModalField }> = {};
    for (const f of opts.fields) {
        const row = el('label', `${CSS_PREFIX}-modal-field`);
        row.appendChild(text(f.label));
        let input: HTMLInputElement | HTMLTextAreaElement;
        if (f.type === 'textarea') {
            const ta = document.createElement('textarea');
            ta.value = f.value;
            ta.className = `${CSS_PREFIX}-modal-textarea`;
            ta.rows = 4;
            ta.spellcheck = false;
            if (f.placeholder) ta.placeholder = f.placeholder;
            input = ta;
        } else {
            const inp = document.createElement('input');
            inp.type = f.type ?? 'text';
            inp.value = f.value;
            inp.autocomplete = 'off';
            if (f.placeholder) inp.placeholder = f.placeholder;
            input = inp;
        }
        if (f.type === 'password') {
            // Wrap the input + a peek-toggle button so the user can
            // verify what they typed. Mostly useful when pre-filling from
            // localStorage and the host wants to remember which password
            // they're handing out.
            const pwInput = input as HTMLInputElement;
            const peekWrap = el('div', `${CSS_PREFIX}-password-wrap`);
            peekWrap.appendChild(pwInput);
            const peekBtn = document.createElement('button');
            peekBtn.type = 'button';
            peekBtn.className = `${CSS_PREFIX}-password-peek`;
            peekBtn.setAttribute('aria-label', 'Show password');
            peekBtn.title = 'Show password';
            const icon = document.createElement('span');
            icon.className = 'codicon codicon-eye';
            peekBtn.appendChild(icon);
            peekBtn.addEventListener('click', () => {
                const showing = pwInput.type === 'text';
                pwInput.type = showing ? 'password' : 'text';
                icon.className = showing ? 'codicon codicon-eye' : 'codicon codicon-eye-closed';
                peekBtn.title = showing ? 'Show password' : 'Hide password';
                peekBtn.setAttribute('aria-label', showing ? 'Show password' : 'Hide password');
            });
            peekWrap.appendChild(peekBtn);
            row.appendChild(peekWrap);
        } else {
            row.appendChild(input);
        }
        if (f.hint) {
            const hint = el('div', `${CSS_PREFIX}-modal-field-hint`);
            hint.textContent = f.hint;
            row.appendChild(hint);
        }
        form.appendChild(row);
        inputs[f.name] = input;
        fieldRows[f.name] = { row, field: f };
    }

    // Transport picker — only shown when 2+ transports are available.
    let transportId: TransportId = opts.transports[0]?.id ?? 'mock';
    function applyTransportVisibility() {
        for (const { row, field } of Object.values(fieldRows)) {
            const hide =
                (field.hideWhenTransport && field.hideWhenTransport === transportId) ||
                (field.showOnlyWhenTransport && field.showOnlyWhenTransport !== transportId);
            row.style.display = hide ? 'none' : '';
        }
    }
    if (opts.transports.length > 1) {
        const row = el('label', `${CSS_PREFIX}-modal-field`);
        row.appendChild(text('Connection'));
        const sel = document.createElement('select');
        for (const t of opts.transports) {
            const o = document.createElement('option');
            o.value = t.id;
            o.textContent = t.capabilities.label;
            sel.appendChild(o);
        }
        sel.value = transportId;
        sel.addEventListener('change', () => {
            transportId = sel.value as TransportId;
            applyTransportVisibility();
        });
        row.appendChild(sel);
        form.appendChild(row);
    }
    applyTransportVisibility();

    const err = el('div', `${CSS_PREFIX}-modal-error`);
    form.appendChild(err);

    const actions = el('div', `${CSS_PREFIX}-modal-actions`);
    const cancel = btn('Cancel', close, 'secondary');
    const submit = document.createElement('button');
    submit.type = 'submit';
    submit.className = `${CSS_PREFIX}-btn`;
    submit.textContent = opts.submitLabel;
    actions.append(cancel, submit);
    form.appendChild(actions);

    form.addEventListener('submit', async (e) => {
        e.preventDefault();
        submit.disabled = true;
        err.textContent = '';
        const values: Record<string, string> = {};
        for (const [k, inp] of Object.entries(inputs)) values[k] = inp.value;
        const result = await opts.onSubmit(values, transportId);
        if (result === null) close();
        else { err.textContent = result; submit.disabled = false; }
    });

    modal.appendChild(form);
    backdrop.appendChild(modal);
    document.body.appendChild(backdrop);

    // Focus the first input.
    setTimeout(() => { Object.values(inputs)[0]?.focus(); }, 10);
}

// ── helpers ────────────────────────────────────────────────────────────

function el(tag: string, className: string): HTMLElement {
    const e = document.createElement(tag);
    e.className = className;
    return e;
}
function text(s: string): Text { return document.createTextNode(s); }
function label(s: string): HTMLElement {
    const e = el('div', `${CSS_PREFIX}-label`);
    e.textContent = s;
    return e;
}
function infoBlock(title: string, body: string): HTMLElement {
    const wrap = el('div', `${CSS_PREFIX}-info`);
    const t = document.createElement('strong');
    t.textContent = title;
    const b = document.createElement('p');
    b.textContent = body;
    wrap.append(t, b);
    return wrap;
}
function btn(label: string, onClick: () => void, variant: 'primary' | 'secondary' | 'danger' = 'primary'): HTMLButtonElement {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = `${CSS_PREFIX}-btn ${CSS_PREFIX}-btn-${variant}`;
    b.textContent = label;
    b.addEventListener('click', onClick);
    return b;
}
function roleBadge(role: string): HTMLElement {
    const b = el('span', `${CSS_PREFIX}-role-badge ${CSS_PREFIX}-role-${role}`);
    b.textContent = role;
    return b;
}
function statusPill(status: RoomStatus): HTMLElement {
    const b = el('span', `${CSS_PREFIX}-status-pill ${CSS_PREFIX}-status-${status}`);
    b.textContent = status;
    return b;
}
async function copyToClipboard(s: string): Promise<void> {
    try { await navigator.clipboard.writeText(s); } catch { /* ignore */ }
}

function injectStyles() {
    if (document.getElementById(STYLE_ID)) return;
    const style = document.createElement('style');
    style.id = STYLE_ID;
    style.textContent = `
        .${CSS_PREFIX}-root { display: flex; flex-direction: column; height: 100%; min-height: 0; }
        .${CSS_PREFIX}-header {
            padding: 6px 10px;
            font-size: 0.7rem;
            text-transform: uppercase;
            letter-spacing: 0.05em;
            color: var(--fg-muted);
            border-bottom: 1px solid var(--border-2);
            user-select: none;
        }
        .${CSS_PREFIX}-body {
            flex: 1; min-height: 0; overflow-y: auto;
            padding: 12px;
            display: flex; flex-direction: column; gap: 12px;
            font-size: 0.78rem;
            color: var(--fg);
        }
        .${CSS_PREFIX}-info p { margin: 4px 0 0; color: var(--fg-muted); line-height: 1.4; }
        .${CSS_PREFIX}-row { display: flex; gap: 8px; flex-wrap: wrap; }
        .${CSS_PREFIX}-banner {
            padding: 8px 10px;
            border-radius: 4px;
            background: var(--bg-3);
            border: 1px solid var(--border-2);
            display: flex; align-items: center; gap: 6px; flex-wrap: wrap;
        }
        .${CSS_PREFIX}-banner-host { border-color: rgba(77,166,255,0.5); }
        .${CSS_PREFIX}-banner-guest { border-color: rgba(184,138,255,0.5); }
        .${CSS_PREFIX}-label {
            font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.05em;
            color: var(--fg-muted); margin-bottom: 4px; margin-top: 8px;
        }
        .${CSS_PREFIX}-code-row { display: flex; gap: 6px; align-items: stretch; }
        .${CSS_PREFIX}-code-box {
            flex: 1;
            font-family: ui-monospace, 'SF Mono', Menlo, monospace;
            background: var(--bg-1);
            border: 1px solid var(--border-2);
            padding: 6px 8px; border-radius: 3px;
            white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
            color: var(--fg);
        }
        .${CSS_PREFIX}-code-box-url { font-size: 0.7rem; }
        .${CSS_PREFIX}-toggle-row { display: flex; align-items: center; gap: 8px; color: var(--fg); }
        .${CSS_PREFIX}-peer-list { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 4px; }
        .${CSS_PREFIX}-peer-row {
            display: grid;
            grid-template-columns: 12px 1fr auto auto;
            gap: 8px; align-items: center;
            padding: 4px 6px;
            background: var(--bg-1);
            border: 1px solid var(--border-2);
            border-radius: 3px;
        }
        .${CSS_PREFIX}-peer-dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
        .${CSS_PREFIX}-peer-name { color: var(--fg); }
        .${CSS_PREFIX}-peer-role,
        .${CSS_PREFIX}-peer-file { color: var(--fg-muted); font-size: 0.7rem; }
        .${CSS_PREFIX}-peer-empty { color: var(--fg-muted); padding: 6px; font-style: italic; }
        .${CSS_PREFIX}-role-badge {
            display: inline-block;
            padding: 1px 6px; border-radius: 9px;
            font-size: 0.65rem; text-transform: uppercase; letter-spacing: 0.05em;
            background: rgba(77,166,255,0.18); color: #4da6ff;
            border: 1px solid rgba(77,166,255,0.35);
        }
        .${CSS_PREFIX}-role-guest { background: rgba(184,138,255,0.18); color: #b88aff; border-color: rgba(184,138,255,0.35); }
        .${CSS_PREFIX}-status-pill {
            display: inline-block; padding: 1px 6px; border-radius: 9px;
            font-size: 0.65rem; text-transform: uppercase; letter-spacing: 0.05em;
            background: rgba(255,255,255,0.06); color: var(--fg-muted); border: 1px solid var(--border-2);
        }
        .${CSS_PREFIX}-status-connected { background: rgba(76,175,80,0.18); color: #6fd178; border-color: rgba(76,175,80,0.4); }
        .${CSS_PREFIX}-status-discovering { background: rgba(77,166,255,0.18); color: #4da6ff; border-color: rgba(77,166,255,0.4); }
        .${CSS_PREFIX}-status-reconnecting { background: rgba(255,183,77,0.18); color: #ffb74d; border-color: rgba(255,183,77,0.4); }
        .${CSS_PREFIX}-status-closed { background: rgba(244,67,54,0.18); color: #f88; border-color: rgba(244,67,54,0.4); }

        .${CSS_PREFIX}-btn {
            padding: 5px 12px;
            background: var(--accent);
            color: white;
            border: none;
            border-radius: var(--btn-radius);
            cursor: pointer;
            font: inherit;
            font-size: 0.76rem;
        }
        .${CSS_PREFIX}-btn:hover:not(:disabled) { background: var(--accent-hover); }
        .${CSS_PREFIX}-btn:disabled { opacity: 0.5; cursor: default; }
        .${CSS_PREFIX}-btn-secondary { background: var(--bg-3); color: var(--fg); border: 1px solid var(--border-2); }
        .${CSS_PREFIX}-btn-secondary:hover:not(:disabled) {
            /* var(--hover-bg) is calibrated for row hovers and ends up
               nearly identical to --bg-3, making secondary-button hover
               invisible. --btn-hover-bg blends the accent into --bg-3
               for a perceptible "press me" tint. */
            background: var(--btn-hover-bg);
        }
        .${CSS_PREFIX}-btn-danger { background: rgba(244,67,54,0.25); color: #f88; border: 1px solid rgba(244,67,54,0.5); }
        .${CSS_PREFIX}-btn-danger:hover:not(:disabled) { background: rgba(244,67,54,0.4); }

        .${CSS_PREFIX}-modal-backdrop {
            position: fixed; inset: 0; background: rgba(0,0,0,0.5);
            display: flex; align-items: center; justify-content: center;
            z-index: 2000;
        }
        .${CSS_PREFIX}-modal {
            background: var(--bg-2);
            border: 1px solid var(--border-2);
            border-radius: 6px;
            padding: 16px 18px;
            min-width: 320px; max-width: 420px;
            box-shadow: 0 8px 24px rgba(0,0,0,0.5);
        }
        .${CSS_PREFIX}-modal h3 { margin: 0 0 12px; font-size: 0.95rem; color: var(--fg); }
        .${CSS_PREFIX}-modal-form { display: flex; flex-direction: column; gap: 10px; }
        .${CSS_PREFIX}-modal-field {
            display: flex; flex-direction: column; gap: 4px;
            font-size: 0.78rem; color: var(--fg-muted);
        }
        .${CSS_PREFIX}-modal-field input,
        .${CSS_PREFIX}-modal-field select {
            background: var(--bg-1);
            border: 1px solid var(--border-2);
            color: var(--fg);
            font: inherit; font-size: 0.82rem;
            padding: 5px 8px; border-radius: 3px;
            outline: none;
        }
        .${CSS_PREFIX}-modal-field input:focus,
        .${CSS_PREFIX}-modal-field select:focus { border-color: var(--accent); }
        .${CSS_PREFIX}-password-wrap {
            position: relative; display: flex; align-items: center;
        }
        .${CSS_PREFIX}-password-wrap input {
            flex: 1; padding-right: 30px;  /* room for the icon button */
        }
        .${CSS_PREFIX}-password-peek {
            position: absolute; right: 4px;
            background: transparent; border: 0; cursor: pointer;
            color: var(--fg-muted);
            display: inline-flex; align-items: center; justify-content: center;
            width: 24px; height: 24px;
            border-radius: 3px;
        }
        .${CSS_PREFIX}-password-peek:hover { color: var(--fg); background: var(--hover-bg); }
        .${CSS_PREFIX}-password-peek .codicon { font-size: 14px; }
        .${CSS_PREFIX}-modal-error { color: #f88; font-size: 0.74rem; min-height: 14px; }
        .${CSS_PREFIX}-modal-actions { display: flex; justify-content: flex-end; gap: 8px; }

        .${CSS_PREFIX}-sync-action {
            display: flex; flex-direction: column; gap: 4px;
            padding: 8px 10px;
            background: var(--bg-3);
            border: 1px solid var(--border-2);
            border-radius: 4px;
        }
        .${CSS_PREFIX}-hint {
            font-size: 0.7rem; color: var(--fg-muted); line-height: 1.4;
        }
        .${CSS_PREFIX}-stream-settings {
            display: flex; flex-direction: column; gap: 6px;
            padding: 8px 10px;
            background: var(--bg-3);
            border: 1px solid var(--border-2);
            border-radius: 4px;
        }
        .${CSS_PREFIX}-stream-settings-title {
            font-size: 0.78rem; font-weight: 600; color: var(--fg);
        }
        .${CSS_PREFIX}-stream-settings-row {
            display: flex; align-items: center; gap: 8px;
            font-size: 0.78rem;
        }
        .${CSS_PREFIX}-stream-settings-label {
            min-width: 60px;
        }
        .${CSS_PREFIX}-stream-settings-input {
            width: 64px;
            padding: 2px 6px;
            background: var(--bg-1);
            border: 1px solid var(--border-2);
            border-radius: 3px;
            color: var(--fg);
            font: inherit;
            font-size: 0.78rem;
        }
        .${CSS_PREFIX}-stream-settings-input:focus {
            outline: 1px solid var(--accent);
        }
        .${CSS_PREFIX}-stream-settings-hint {
            font-size: 0.7rem; color: var(--fg-muted);
        }
        .${CSS_PREFIX}-sync-progress {
            display: flex; flex-direction: column; gap: 6px;
            padding: 10px 12px;
            background: rgba(77, 166, 255, 0.10);
            border: 1px solid rgba(77, 166, 255, 0.45);
            border-radius: 4px;
        }
        .${CSS_PREFIX}-sync-headline {
            font-weight: 600; color: var(--fg);
        }
        .${CSS_PREFIX}-sync-detail {
            font-size: 0.74rem; color: var(--fg-muted);
            font-family: ui-monospace, 'SF Mono', Menlo, monospace;
        }
        .${CSS_PREFIX}-sync-bar {
            height: 6px; background: var(--bg-1); border-radius: 3px;
            overflow: hidden;
            border: 1px solid var(--border-2);
        }
        .${CSS_PREFIX}-sync-bar-fill {
            height: 100%;
            background: linear-gradient(90deg, #4da6ff, #6fd178);
            transition: width 120ms linear;
        }

        .${CSS_PREFIX}-warning {
            display: flex; gap: 8px; align-items: flex-start;
            padding: 10px 12px;
            background: rgba(244, 67, 54, 0.12);
            border: 1px solid rgba(244, 67, 54, 0.5);
            border-radius: 4px;
            color: var(--fg);
            line-height: 1.45;
        }
        .${CSS_PREFIX}-warning-icon {
            color: #f88; font-weight: bold; flex-shrink: 0;
        }
        .${CSS_PREFIX}-warning-body { font-size: 0.78rem; }

        .${CSS_PREFIX}-note {
            display: flex; gap: 8px; align-items: flex-start;
            padding: 10px 12px;
            background: rgba(77, 166, 255, 0.10);
            border: 1px solid rgba(77, 166, 255, 0.40);
            border-radius: 4px;
            color: var(--fg);
            line-height: 1.45;
        }
        .${CSS_PREFIX}-note-icon {
            color: #4da6ff; font-weight: bold; flex-shrink: 0; font-size: 1rem;
        }
        .${CSS_PREFIX}-note-body { font-size: 0.76rem; }

        .${CSS_PREFIX}-wizard {
            display: flex; flex-direction: column; gap: 10px;
        }
        .${CSS_PREFIX}-wizard-head {
            display: flex; align-items: baseline; gap: 8px;
            padding-bottom: 4px;
            border-bottom: 1px solid var(--border-2);
        }
        .${CSS_PREFIX}-wizard-title {
            font-weight: 600; color: var(--fg); font-size: 0.85rem;
        }
        .${CSS_PREFIX}-wizard-sub {
            color: var(--fg-muted); font-family: ui-monospace, 'SF Mono', Menlo, monospace;
            font-size: 0.72rem;
        }
        .${CSS_PREFIX}-wizard-blob {
            width: 100%;
            box-sizing: border-box;
            min-height: 80px;
            font-family: ui-monospace, 'SF Mono', Menlo, monospace;
            font-size: 0.68rem;
            background: var(--bg-1);
            border: 1px solid var(--border-2);
            color: var(--fg);
            padding: 6px 8px; border-radius: 3px;
            resize: vertical;
            outline: none;
            white-space: pre;
            overflow-x: auto;
        }
        .${CSS_PREFIX}-wizard-blob:focus {
            border-color: var(--accent);
        }
        .${CSS_PREFIX}-wizard-error {
            color: #f88;
            background: rgba(244, 67, 54, 0.08);
            border: 1px solid rgba(244, 67, 54, 0.35);
            padding: 6px 8px; border-radius: 3px;
            font-size: 0.74rem;
            line-height: 1.4;
        }
        .${CSS_PREFIX}-wizard-connecting {
            color: var(--fg);
            font-weight: 600;
            padding: 8px 10px;
            background: rgba(77, 166, 255, 0.10);
            border: 1px solid rgba(77, 166, 255, 0.40);
            border-radius: 3px;
        }
        .${CSS_PREFIX}-wizard-status {
            display: grid;
            grid-template-columns: auto 1fr;
            grid-template-rows: auto auto;
            column-gap: 10px;
            row-gap: 4px;
            align-items: center;
            padding: 10px 12px;
            background: rgba(77, 166, 255, 0.10);
            border: 1px solid rgba(77, 166, 255, 0.40);
            border-radius: 4px;
        }
        .${CSS_PREFIX}-wizard-status .${CSS_PREFIX}-hint {
            grid-column: 1 / -1;
        }
        .${CSS_PREFIX}-wizard-status-label {
            color: var(--fg);
            font-weight: 600;
        }
        .${CSS_PREFIX}-wizard-spinner {
            display: inline-block;
            width: 14px; height: 14px;
            border: 2px solid rgba(77, 166, 255, 0.30);
            border-top-color: #4da6ff;
            border-radius: 50%;
            animation: fade-live-spin 0.8s linear infinite;
        }
        @keyframes fade-live-spin {
            to { transform: rotate(360deg); }
        }
        .${CSS_PREFIX}-modal-textarea {
            background: var(--bg-1);
            border: 1px solid var(--border-2);
            color: var(--fg);
            font: inherit;
            font-family: ui-monospace, 'SF Mono', Menlo, monospace;
            font-size: 0.7rem;
            padding: 5px 8px; border-radius: 3px;
            outline: none;
            resize: vertical;
            min-height: 70px;
            box-sizing: border-box;
            white-space: pre;
            overflow-x: auto;
        }
        .${CSS_PREFIX}-modal-textarea:focus {
            border-color: var(--accent);
        }
        .${CSS_PREFIX}-modal-field-hint {
            font-size: 0.7rem;
            color: var(--fg-muted);
            line-height: 1.4;
        }

        /* Remote-peer cursor decorations injected by y-monaco. Per-peer
           coloring would require runtime <style> injection keyed by clientID;
           for v1 just give everyone the same readable highlight. */
        .yRemoteSelection {
            background-color: rgba(77,166,255,0.25);
        }
        .yRemoteSelectionHead {
            position: absolute;
            border-left: 2px solid #4da6ff;
            height: 100%;
        }
    `;
    document.head.appendChild(style);
}
