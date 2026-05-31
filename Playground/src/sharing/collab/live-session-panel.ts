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

        if (!session) {
            body.appendChild(renderIdle());
            return;
        }
        body.appendChild(renderActive(session, lastState));
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
        if (!roomId) return wrap;
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
                { name: 'roomId', label: 'Share code', value: prefilledRoom ?? '' },
                { name: 'password', label: 'Password (if required)', value: prefilledPassword ?? '', type: 'password' },
            ],
            transports: opts.host.transports,
            submitLabel: 'Join',
            onSubmit: async (vals, transportId) => {
                const name = vals.displayName.trim();
                const roomId = vals.roomId.trim();
                if (!name) return 'Display name is required.';
                if (!roomId) return 'Share code is required.';
                setCachedDisplayName(name);
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
        onSessionChange: (cb) => { sessionCbs.add(cb); return () => sessionCbs.delete(cb); },
        dispose: () => {
            for (const u of sessionUnsubs) { try { u(); } catch { /* ignore */ } }
            sessionUnsubs = [];
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
    type?: 'text' | 'password';
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
    const inputs: Record<string, HTMLInputElement> = {};
    for (const f of opts.fields) {
        const row = el('label', `${CSS_PREFIX}-modal-field`);
        row.appendChild(text(f.label));
        const input = document.createElement('input');
        input.type = f.type ?? 'text';
        input.value = f.value;
        input.autocomplete = 'off';
        if (f.type === 'password') {
            // Wrap the input + a peek-toggle button so the user can
            // verify what they typed. Mostly useful when pre-filling from
            // localStorage and the host wants to remember which password
            // they're handing out.
            const peekWrap = el('div', `${CSS_PREFIX}-password-wrap`);
            peekWrap.appendChild(input);
            const peekBtn = document.createElement('button');
            peekBtn.type = 'button';
            peekBtn.className = `${CSS_PREFIX}-password-peek`;
            peekBtn.setAttribute('aria-label', 'Show password');
            peekBtn.title = 'Show password';
            const icon = document.createElement('span');
            icon.className = 'codicon codicon-eye';
            peekBtn.appendChild(icon);
            peekBtn.addEventListener('click', () => {
                const showing = input.type === 'text';
                input.type = showing ? 'password' : 'text';
                icon.className = showing ? 'codicon codicon-eye' : 'codicon codicon-eye-closed';
                peekBtn.title = showing ? 'Show password' : 'Hide password';
                peekBtn.setAttribute('aria-label', showing ? 'Show password' : 'Hide password');
            });
            peekWrap.appendChild(peekBtn);
            row.appendChild(peekWrap);
        } else {
            row.appendChild(input);
        }
        form.appendChild(row);
        inputs[f.name] = input;
    }

    // Transport picker — only shown when 2+ transports are available.
    let transportId: TransportId = opts.transports[0]?.id ?? 'mock';
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
        sel.addEventListener('change', () => { transportId = sel.value as TransportId; });
        row.appendChild(sel);
        form.appendChild(row);
    }

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
        .${CSS_PREFIX}-btn-secondary:hover:not(:disabled) { background: var(--hover-bg); }
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
