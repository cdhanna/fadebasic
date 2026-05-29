// AI Chat panel + Model Manager panel.
//
// mountAiChat(el, workspace) — wires up the chat panel with the new agent.
// mountAiModels(el)          — wires up the provider/model selector.
//
// This file is now a thin UI layer over src/ai/ — the agent loop, tool
// registry, RAG, and provider abstraction all live there. We keep this
// file focused on DOM rendering and chat persistence.

import { Agent, type AgentEvent } from './ai/agent';
import { getLogger } from './log-bus';
import { createDefaultRegistry } from './ai/tools/default-registry';
import { mountDiffApproval, type DiffApprovalHandle } from './ai/ui/diff-approval';
import type { AgentPlan } from './ai/tool-protocol';
import type { DiagnosticsProvider, EditorAdapter, ToolRegistry } from './ai/tools';
import { createDefaultSlashRegistry } from './ai/slash-commands/default-registry';
import { emptySlashState } from './ai/slash-commands/registry';
import type { SlashResult, SlashStateSnapshot } from './ai/slash-commands/types';
import { ensureAnthropicApiKey } from './ai/providers/anthropic';
import {
    PROVIDER_CATALOG,
    createSelectedProvider,
    getSelectedProviderId,
    setSelectedProviderId,
    type ChatProvider,
    type Msg,
} from './ai/providers';

// ─── Types ────────────────────────────────────────────────────────────────────

export interface WorkspaceAdapter {
    list(): Promise<string[]>;
    read(name: string): Promise<string>;
    write(name: string, content: string): Promise<void>;
    currentProject(): string;
}

/** Optional integrations passed in by main.ts. The chat panel works
 *  without any of these — the relevant tools / commands just degrade. */
export interface ChatDependencies {
    diagnostics?: DiagnosticsProvider;
    editor?: EditorAdapter;
    /** Called by /logs to bring the Logs panel forward + filter to a
     *  channel pattern. Optional — when missing, /logs shows a hint. */
    focusLogs?: (channelPattern: RegExp) => void;
    /** Active project's `type` (e.g. 'web', 'monogame'). Forwarded to the
     *  Agent + tool context so RAG retrieval can hide type-scoped chunks
     *  (see docs-sources.mjs `projectTypes`). */
    getProjectType?: () => string | undefined;
}

type EngineStatus = 'idle' | 'loading' | 'ready' | 'error';

// ─── Module-level provider state ──────────────────────────────────────────────

let provider: ChatProvider | null = null;
let engineStatus: EngineStatus = 'idle';
let engineError: string | null = null;
const statusListeners = new Set<(s: EngineStatus, detail?: string) => void>();
const progressListeners = new Set<(text: string, pct: number) => void>();

function notifyStatus(s: EngineStatus, detail?: string) {
    engineStatus = s;
    engineError = s === 'error' ? (detail ?? null) : null;
    for (const fn of statusListeners) fn(s, detail);
}

function notifyProgress(text: string, pct: number) {
    for (const fn of progressListeners) fn(text, pct);
}

export async function loadSelectedProvider(): Promise<void> {
    if (provider && engineStatus === 'ready') return;
    if (engineStatus === 'loading') return;

    notifyStatus('loading');
    try {
        // If the user selected an Anthropic provider, make sure we have a
        // key on hand before we even build the provider. Prompts the user
        // if missing. Throws if they cancel.
        const selectedId = getSelectedProviderId();
        if (selectedId.startsWith('anthropic:')) {
            const key = ensureAnthropicApiKey();
            if (!key) {
                throw new Error('Anthropic API key required. Cancelled by user.');
            }
        }
        provider = createSelectedProvider();
        provider.onProgress(({ text, pct }) => notifyProgress(text, pct));
        await provider.ensureReady();
        notifyStatus('ready');
    } catch (err) {
        provider = null;
        notifyStatus('error', (err as Error).message ?? String(err));
        throw err;
    }
}

// Best-effort cleanup before the tab unloads. Firefox's GPU process holds
// onto WebGPU buffers across page reloads even when the originating renderer
// is gone; explicitly disposing the pipeline here gives the browser a chance
// to release them. Doesn't fully fix the Firefox leak (the GPU process is
// the bug surface, not us), but it removes the in-tab piece of the leak.
//
// `pagehide` fires more reliably than `beforeunload` across browsers and
// also handles bfcache navigations. We don't await — there's no time
// budget during unload, but dispose runs as far as it can before the
// renderer dies.
if (typeof window !== 'undefined') {
    window.addEventListener('pagehide', () => {
        const p = provider;
        if (p) {
            try { void p.reset(); } catch { /* ignore — page is dying */ }
        }
    });
}

// ─── Chat persistence ─────────────────────────────────────────────────────────

interface ChatRecord {
    id: string;
    title: string;
    createdAt: string;
    updatedAt: string;
    providerId: string | null;
    messages: Msg[];
}

type ChatSummary = Omit<ChatRecord, 'messages'>;

function newChatId(): string {
    return Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
}

function deriveChatTitle(messages: Msg[]): string {
    const first = messages.find(m => m.role === 'user')?.content;
    if (!first) return 'New chat';
    return first.length > 45 ? first.slice(0, 45) + '…' : first;
}

function relTime(iso: string): string {
    const diff = Date.now() - new Date(iso).getTime();
    const m = Math.floor(diff / 60_000);
    if (m < 1) return 'just now';
    if (m < 60) return `${m}m ago`;
    const h = Math.floor(m / 60);
    if (h < 24) return `${h}h ago`;
    return `${Math.floor(h / 24)}d ago`;
}

class ChatStore {
    private dir!: FileSystemDirectoryHandle;

    async init(): Promise<void> {
        const root = await navigator.storage.getDirectory();
        this.dir = await root.getDirectoryHandle('ai-chats', { create: true });
    }

    async save(record: ChatRecord): Promise<void> {
        const fh = await this.dir.getFileHandle(`${record.id}.json`, { create: true });
        const w = await fh.createWritable();
        await w.write(JSON.stringify(record));
        await w.close();
    }

    async load(id: string): Promise<ChatRecord | null> {
        try {
            const fh = await this.dir.getFileHandle(`${id}.json`);
            return JSON.parse(await (await fh.getFile()).text()) as ChatRecord;
        } catch { return null; }
    }

    async list(): Promise<ChatSummary[]> {
        const out: ChatSummary[] = [];
        for await (const entry of (this.dir as any).values()) {
            if (entry.kind !== 'file' || !entry.name.endsWith('.json')) continue;
            try {
                const fh = await this.dir.getFileHandle(entry.name);
                const rec = JSON.parse(await (await fh.getFile()).text()) as ChatRecord;
                out.push({
                    id: rec.id, title: rec.title, createdAt: rec.createdAt,
                    updatedAt: rec.updatedAt, providerId: rec.providerId,
                });
            } catch { /* skip corrupted */ }
        }
        return out.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
    }

    async delete(id: string): Promise<void> {
        try { await this.dir.removeEntry(`${id}.json`); } catch { /* ignore */ }
    }
}

// Line-diff + diff rendering live in ./ai/ui/diff-approval (extracted so
// they can be unit-tested in isolation and exercised in scripts/diff-demo.html).

function escapeHtml(s: string): string {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function formatJson(raw: unknown): string {
    try { return JSON.stringify(raw, null, 2); } catch { return String(raw); }
}

function highlightJson(json: string): string {
    const escaped = escapeHtml(json);
    return escaped.replace(
        /("(\\u[0-9a-fA-F]{4}|\\[^u]|[^\\"])*"(\s*:)?|\b(true|false|null)\b|-?\d+(?:\.\d*)?(?:[eE][+\-]?\d+)?)/g,
        (match) => {
            let cls = 'json-n';
            if (match.startsWith('"')) cls = match.trimEnd().endsWith(':') ? 'json-k' : 'json-s';
            else if (match === 'true' || match === 'false') cls = 'json-b';
            else if (match === 'null') cls = 'json-null';
            return `<span class="${cls}">${match}</span>`;
        },
    );
}

// ─── Chat panel ───────────────────────────────────────────────────────────────

export function mountAiChat(
    container: HTMLElement,
    workspace: WorkspaceAdapter,
    deps: ChatDependencies = {},
): void {
    const pane = container.querySelector<HTMLElement>('#ai-chat-pane')!;
    const messagesEl = pane.querySelector<HTMLElement>('.ai-messages')!;
    const inputEl = pane.querySelector<HTMLTextAreaElement>('.ai-input')!;
    const sendBtn = pane.querySelector<HTMLButtonElement>('.ai-send-btn')!;
    const stopBtn = pane.querySelector<HTMLButtonElement>('.ai-stop-btn')!;
    const statusEl = pane.querySelector<HTMLElement>('.ai-chat-status')!;
    const loadBtn = pane.querySelector<HTMLButtonElement>('.ai-load-btn')!;
    const clearBtn = pane.querySelector<HTMLButtonElement>('.ai-clear-btn')!;
    const progressBar = pane.querySelector<HTMLElement>('.ai-progress-bar-fill')!;
    const progressRow = pane.querySelector<HTMLElement>('.ai-progress-row')!;
    const progressText = pane.querySelector<HTMLElement>('.ai-progress-text')!;
    const historyBtn = pane.querySelector<HTMLButtonElement>('.ai-history-btn')!;
    const historyMenu = pane.querySelector<HTMLElement>('.ai-history-menu')!;
    const historyList = pane.querySelector<HTMLElement>('.ai-history-list')!;
    const historyNewBtn = pane.querySelector<HTMLButtonElement>('.ai-history-new')!;

    let activeChatId: string = newChatId();
    let generating = false;
    let agent: Agent | null = null;
    /** Kept on the panel so /tools and /context can inspect them without
     *  reaching into the Agent's internals. Refreshed when the agent is
     *  rebuilt (e.g. after a model swap). */
    let toolRegistry: ToolRegistry = createDefaultRegistry();
    /** Recent agent-event snapshot consumed by slash commands. */
    const slashState: SlashStateSnapshot = emptySlashState();
    const slashRegistry = createDefaultSlashRegistry();
    /** Mounted diff-approval handles. Tracked so we can force-reject
     *  them when the user aborts — otherwise the agent hangs forever on
     *  a Promise that nothing else can resolve. */
    const pendingDiffApprovals = new Set<DiffApprovalHandle>();

    const store = new ChatStore();
    void store.init().catch(e => console.warn('[fade/ai] chat store init failed:', e));

    function buildAgent(): Agent | null {
        if (!provider) return null;
        toolRegistry = createDefaultRegistry();
        const toolLog = getLogger('ai/tool');
        return new Agent({
            provider,
            tools: toolRegistry,
            toolContext: {
                workspace,
                diagnostics: deps.diagnostics,
                editor: deps.editor,
                confirmEdit: (path, oldContent, newContent) => {
                    // Log every approval request — when a hang happens
                    // again, the Logs panel shows what file + sizes were
                    // involved (delta=0 usually means a no-op edit).
                    toolLog.info(
                        `confirmEdit: ${path} (old=${oldContent.length}b, new=${newContent.length}b, delta=${newContent.length - oldContent.length}b)`,
                    );
                    return requestDiffApproval(path, oldContent, newContent);
                },
                projectType: deps.getProjectType,
            },
            getProjectType: deps.getProjectType,
        });
    }

    function saveChat(): void {
        if (!agent) return;
        const history = agent.getHistory();
        if (history.length === 0) return;
        const record: ChatRecord = {
            id: activeChatId,
            title: deriveChatTitle(history as Msg[]),
            createdAt: activeChatId,
            updatedAt: new Date().toISOString(),
            providerId: provider?.id ?? null,
            messages: history as Msg[],
        };
        void store.save(record).catch(e => console.warn('[fade/ai] save chat failed:', e));
    }

    // ── History menu ────────────────────────────────────────────────────────
    function openHistoryMenu(): void {
        historyMenu.hidden = false;
        void store.list().then(summaries => {
            historyList.innerHTML = '';
            if (summaries.length === 0) {
                const empty = document.createElement('div');
                empty.className = 'ai-history-empty';
                empty.textContent = 'No saved chats yet.';
                historyList.appendChild(empty);
                return;
            }
            for (const s of summaries) {
                const row = document.createElement('div');
                row.className = 'ai-history-item' + (s.id === activeChatId ? ' active' : '');

                const label = document.createElement('button');
                label.className = 'ai-history-label';
                const title = document.createElement('span');
                title.className = 'ai-history-title';
                title.textContent = s.title;
                const time = document.createElement('span');
                time.className = 'ai-history-time';
                time.textContent = relTime(s.updatedAt);
                label.append(title, time);
                label.addEventListener('click', () => {
                    closeHistoryMenu();
                    void loadChat(s.id);
                });

                const del = document.createElement('button');
                del.className = 'ai-history-del';
                del.textContent = '×';
                del.title = 'Delete';
                del.addEventListener('click', (e) => {
                    e.stopPropagation();
                    void store.delete(s.id).then(() => {
                        row.remove();
                        if (s.id === activeChatId) startNewChat();
                    });
                });

                row.append(label, del);
                historyList.appendChild(row);
            }
        });
    }

    function closeHistoryMenu(): void { historyMenu.hidden = true; }

    historyBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        if (historyMenu.hidden) openHistoryMenu(); else closeHistoryMenu();
    });
    document.addEventListener('click', (e) => {
        if (!historyMenu.hidden && !historyMenu.contains(e.target as Node) &&
            e.target !== historyBtn) closeHistoryMenu();
    });
    historyNewBtn.addEventListener('click', () => { closeHistoryMenu(); startNewChat(); });

    function startNewChat(): void {
        rejectPendingDiffs();
        activeChatId = newChatId();
        agent?.clearHistory();
        messagesEl.innerHTML = '';
    }

    async function loadChat(id: string): Promise<void> {
        const record = await store.load(id);
        if (!record) return;
        if (!agent) agent = buildAgent();
        agent?.setHistory(record.messages);
        activeChatId = id;
        renderHistoryToDOM(record.messages);
    }

    function renderHistoryToDOM(msgs: Msg[]): void {
        messagesEl.innerHTML = '';
        for (const msg of msgs) {
            if (msg.role === 'user') {
                // Skip tool_result re-injections — these are protocol noise
                if (msg.content.startsWith('<tool_result')) continue;
                appendUserBubble(msg.content);
            } else if (msg.role === 'assistant') {
                const bubble = appendAssistantBubble();
                bubble.setText(msg.content);
            }
        }
        scrollToBottom();
    }

    stopBtn.addEventListener('click', () => {
        if (!generating || !agent) return;
        // Reject any pending diff approvals first — otherwise the agent
        // hangs on the unresolved Promise forever and abort() can't break
        // the loop.
        rejectPendingDiffs();
        agent.abort();
    });

    /** Reject every pending diff-approval Promise with false (treats as
     *  "user rejected"). Called from Stop, /clear, and at the start of a
     *  new turn — anything that should unblock a hung confirmEdit. */
    function rejectPendingDiffs(): void {
        if (pendingDiffApprovals.size === 0) return;
        const stuck = [...pendingDiffApprovals];
        pendingDiffApprovals.clear();
        for (const handle of stuck) {
            try { handle.forceReject(); } catch { /* swallow */ }
        }
    }

    function setGenerating(on: boolean) {
        generating = on;
        sendBtn.hidden = on;
        stopBtn.hidden = !on;
    }
    setGenerating(false);

    // ── Status rendering ────────────────────────────────────────────────────
    function renderStatus() {
        const providerLabel = provider?.label ?? PROVIDER_CATALOG.find(p => p.id === getSelectedProviderId())?.label ?? '—';
        if (engineStatus === 'ready') {
            statusEl.textContent = providerLabel;
            statusEl.className = 'ai-chat-status ai-status-ready';
            loadBtn.hidden = true;
            sendBtn.disabled = false;
            inputEl.disabled = false;
            progressRow.hidden = true;
        } else if (engineStatus === 'loading') {
            statusEl.textContent = 'Loading…';
            statusEl.className = 'ai-chat-status ai-status-loading';
            loadBtn.hidden = true;
            sendBtn.disabled = true;
            inputEl.disabled = true;
            progressRow.hidden = false;
        } else if (engineStatus === 'error') {
            statusEl.textContent = `Error: ${engineError ?? ''}`;
            statusEl.className = 'ai-chat-status ai-status-error';
            loadBtn.hidden = false;
            loadBtn.textContent = 'Retry';
            sendBtn.disabled = true;
            inputEl.disabled = true;
            progressRow.hidden = true;
        } else {
            statusEl.textContent = 'No model loaded';
            statusEl.className = 'ai-chat-status';
            loadBtn.hidden = false;
            loadBtn.textContent = 'Load Model';
            sendBtn.disabled = true;
            inputEl.disabled = true;
            progressRow.hidden = true;
        }
    }

    statusListeners.add(renderStatus);
    progressListeners.add((text, pct) => {
        progressBar.style.width = `${Math.round(pct * 100)}%`;
        progressText.textContent = text;
    });
    renderStatus();

    // ── Message rendering ───────────────────────────────────────────────────
    function appendUserBubble(text: string): void {
        const div = document.createElement('div');
        div.className = 'ai-msg ai-msg-user';
        div.textContent = text;
        messagesEl.appendChild(div);
        scrollToBottom();
    }

    function appendAssistantBubble(): { setText(t: string): void; appendText(t: string): void; el: HTMLElement } {
        const div = document.createElement('div');
        div.className = 'ai-msg ai-msg-assistant';
        messagesEl.appendChild(div);
        scrollToBottom();
        let buf = '';
        return {
            setText(t: string) { buf = t; div.textContent = t; scrollToBottom(); },
            appendText(t: string) { buf += t; div.textContent = buf; scrollToBottom(); },
            el: div,
        };
    }

    function showThinking(): () => void {
        const div = document.createElement('div');
        div.className = 'ai-thinking';
        div.innerHTML = '<span></span><span></span><span></span>';
        messagesEl.appendChild(div);
        scrollToBottom();
        return () => div.remove();
    }

    function appendSlashResult(result: SlashResult): void {
        const wrap = document.createElement('div');
        wrap.className = `ai-slash${result.variant === 'error' ? ' ai-slash-error' : ''}`;
        const header = document.createElement('div');
        header.className = 'ai-slash-header';
        header.textContent = result.title;
        wrap.appendChild(header);

        const body = document.createElement('div');
        body.className = 'ai-slash-body';
        if (typeof result.body === 'string') {
            body.textContent = result.body;
        } else {
            body.appendChild(result.body);
        }
        wrap.appendChild(body);
        messagesEl.appendChild(wrap);
        scrollToBottom();
    }

    function appendDocsCitations(hits: ReadonlyArray<{ chunk: { source: string; heading: string }; score: number }>): void {
        if (hits.length === 0) return;
        const wrap = document.createElement('div');
        wrap.className = 'ai-docs';
        const header = document.createElement('div');
        header.className = 'ai-docs-header';
        header.innerHTML = `<span class="ai-docs-icon">📚</span><span>Retrieved docs</span>`;
        wrap.appendChild(header);
        const list = document.createElement('ul');
        list.className = 'ai-docs-list';
        for (const hit of hits) {
            const li = document.createElement('li');
            const cite = hit.chunk.heading
                ? `${hit.chunk.source} → ${hit.chunk.heading}`
                : hit.chunk.source;
            li.textContent = `${cite} (${hit.score.toFixed(2)})`;
            list.appendChild(li);
        }
        wrap.appendChild(list);
        messagesEl.appendChild(wrap);
        scrollToBottom();
    }

    function appendPostEditDiagnostics(path: string, errors: number, warnings: number, clean: boolean): void {
        // Quiet status row that surfaces the self-healing probe. Clean
        // edits get a subdued green chip; errors get a red one the user
        // can correlate with whatever the agent does next.
        const row = document.createElement('div');
        row.className = 'ai-post-edit-diags';
        const icon = clean ? '✓' : (errors > 0 ? '⚠︎' : '·');
        const text = clean
            ? `Diagnostics clean for ${path}`
            : errors > 0
                ? `${errors} error${errors === 1 ? '' : 's'}${warnings > 0 ? `, ${warnings} warning${warnings === 1 ? '' : 's'}` : ''} in ${path} — agent will react`
                : `${warnings} warning${warnings === 1 ? '' : 's'} in ${path}`;
        row.dataset.state = clean ? 'clean' : (errors > 0 ? 'error' : 'warning');
        row.innerHTML = `<span class="ai-post-edit-icon"></span><span class="ai-post-edit-text"></span>`;
        row.querySelector('.ai-post-edit-icon')!.textContent = icon;
        row.querySelector('.ai-post-edit-text')!.textContent = text;
        messagesEl.appendChild(row);
        scrollToBottom();
    }

    function appendPlanBubble(plan: AgentPlan): void {
        const wrap = document.createElement('div');
        wrap.className = 'ai-plan';

        const header = document.createElement('div');
        header.className = 'ai-plan-header';
        header.innerHTML = `<span class="ai-plan-icon">📋</span><span class="ai-plan-goal"></span>`;
        header.querySelector('.ai-plan-goal')!.textContent = plan.goal;
        wrap.appendChild(header);

        if (plan.steps.length > 0) {
            const list = document.createElement('ol');
            list.className = 'ai-plan-steps';
            for (const step of plan.steps) {
                const li = document.createElement('li');
                if (step.tool) {
                    const tag = document.createElement('code');
                    tag.className = 'ai-plan-tool';
                    tag.textContent = step.tool;
                    li.appendChild(tag);
                    li.appendChild(document.createTextNode(' ' + (step.description || '')));
                } else {
                    li.textContent = step.description;
                }
                list.appendChild(li);
            }
            wrap.appendChild(list);
        }

        messagesEl.appendChild(wrap);
        scrollToBottom();
    }

    function showBudgetWarning(tokens: number, max: number): void {
        const warn = document.createElement('div');
        warn.className = 'ai-msg ai-msg-warn';
        warn.textContent = `Context budget at ${Math.round((tokens / max) * 100)}% (${tokens.toLocaleString()} of ${max.toLocaleString()} tokens). Long chats may degrade — consider clearing.`;
        messagesEl.appendChild(warn);
        scrollToBottom();
    }

    function showEvictionNotice(
        result: { elided: number; summarized: number; dropped: number; saved: number },
        tokensBefore: number,
        tokensAfter: number,
        max: number,
    ): void {
        const wrap = document.createElement('div');
        wrap.className = 'ai-eviction';
        const header = document.createElement('div');
        header.className = 'ai-eviction-header';
        header.innerHTML = '<span class="ai-eviction-icon">🪶</span><span>Context trimmed</span>';
        wrap.appendChild(header);

        const parts: string[] = [];
        if (result.elided > 0) parts.push(`${result.elided} tool result${result.elided === 1 ? '' : 's'} elided`);
        if (result.summarized > 0) parts.push(`${result.summarized} message${result.summarized === 1 ? '' : 's'} summarized`);
        if (result.dropped > 0) parts.push(`${result.dropped} oldest message${result.dropped === 1 ? '' : 's'} dropped`);

        const detail = document.createElement('div');
        detail.className = 'ai-eviction-detail';
        const pctBefore = Math.round((tokensBefore / max) * 100);
        const pctAfter = Math.round((tokensAfter / max) * 100);
        detail.textContent = `${parts.join(' · ')} (${pctBefore}% → ${pctAfter}%)`;
        wrap.appendChild(detail);
        messagesEl.appendChild(wrap);
        scrollToBottom();
    }

    function appendToolRow(icon: string, label: string): {
        done(result: unknown): void;
        fail(message: string): void;
    } {
        const row = document.createElement('div');
        row.className = 'ai-tool-row';
        const header = document.createElement('button');
        header.className = 'ai-tool-header';
        header.disabled = true;
        const iconEl = document.createElement('span');
        iconEl.className = 'ai-tool-icon';
        iconEl.textContent = icon;
        const labelEl = document.createElement('span');
        labelEl.className = 'ai-tool-label';
        labelEl.textContent = label;
        const badge = document.createElement('span');
        badge.className = 'ai-tool-badge ai-tool-badge-running';
        const chevron = document.createElement('span');
        chevron.className = 'ai-tool-chevron';
        chevron.textContent = '▶';
        header.append(iconEl, labelEl, badge, chevron);

        const detail = document.createElement('div');
        detail.className = 'ai-tool-detail';
        detail.hidden = true;

        row.append(header, detail);
        messagesEl.appendChild(row);
        scrollToBottom();

        let expanded = false;
        header.addEventListener('click', () => {
            if (header.disabled) return;
            expanded = !expanded;
            detail.hidden = !expanded;
            chevron.textContent = expanded ? '▼' : '▶';
            scrollToBottom();
        });

        return {
            done(result: unknown) {
                badge.className = 'ai-tool-badge ai-tool-badge-done';
                badge.textContent = '✓';
                const pre = document.createElement('pre');
                pre.className = 'ai-tool-json';
                pre.innerHTML = highlightJson(formatJson(result));
                detail.appendChild(pre);
                header.disabled = false;
                scrollToBottom();
            },
            fail(message: string) {
                badge.className = 'ai-tool-badge ai-tool-badge-fail';
                badge.textContent = '✗';
                const pre = document.createElement('pre');
                pre.className = 'ai-tool-json ai-tool-json-fail';
                pre.textContent = message;
                detail.appendChild(pre);
                header.disabled = false;
                scrollToBottom();
            },
        };
    }

    function requestDiffApproval(path: string, oldContent: string, newContent: string): Promise<boolean> {
        return new Promise<boolean>(resolve => {
            let handle: DiffApprovalHandle | null = null;
            const settle = (approved: boolean) => {
                if (handle) pendingDiffApprovals.delete(handle);
                resolve(approved);
            };
            handle = mountDiffApproval({
                container: messagesEl,
                path,
                oldContent,
                newContent,
                onApprove: () => settle(true),
                onReject: () => settle(false),
            });
            pendingDiffApprovals.add(handle);
            scrollToBottom();
        });
    }

    function scrollToBottom(): void {
        messagesEl.scrollTop = messagesEl.scrollHeight;
    }

    // ── Send handler ────────────────────────────────────────────────────────
    async function handleSend(): Promise<void> {
        const text = inputEl.value.trim();
        if (!text || generating) return;

        // Slash commands are dispatched client-side and never reach the
        // model. Engine doesn't need to be loaded for /help, /tools, etc.
        if (text.startsWith('/')) {
            inputEl.value = '';
            inputEl.style.height = 'auto';
            appendUserBubble(text);
            const slashResult = await slashRegistry.run(text, () => ({
                agent,
                provider,
                tools: toolRegistry,
                state: slashState,
                callbacks: {
                    clearConversation: () => startNewChat(),
                    focusLogs: deps.focusLogs,
                },
            }));
            if (slashResult) appendSlashResult(slashResult);
            inputEl.focus();
            return;
        }

        if (engineStatus !== 'ready') return;
        inputEl.value = '';
        inputEl.style.height = 'auto';
        appendUserBubble(text);

        if (!agent) agent = buildAgent();
        if (!agent) return;

        setGenerating(true);
        inputEl.disabled = true;
        const hideThinking = showThinking();
        let firstDelta = true;
        let currentBubble: ReturnType<typeof appendAssistantBubble> | null = null;
        const toolRows = new Map<string, ReturnType<typeof appendToolRow>>();

        const unbind = agent.on((ev: AgentEvent) => {
            // Snapshot recent events for /context to inspect. Cap the
            // buffer so a long session doesn't grow it unbounded.
            slashState.recentEvents.push(ev);
            if (slashState.recentEvents.length > 200) slashState.recentEvents.shift();
            if (ev.kind === 'docs_retrieved') slashState.lastDocs = ev.hits;
            else if (ev.kind === 'plan_emitted') slashState.lastPlan = ev.plan;

            if (ev.kind === 'text_delta') {
                if (firstDelta) { hideThinking(); firstDelta = false; }
                if (!currentBubble) currentBubble = appendAssistantBubble();
                currentBubble.appendText(ev.delta);
            } else if (ev.kind === 'iteration_start' && ev.iteration > 1) {
                currentBubble = null;
            } else if (ev.kind === 'plan_emitted') {
                if (firstDelta) { hideThinking(); firstDelta = false; }
                appendPlanBubble(ev.plan);
                currentBubble = null;
            } else if (ev.kind === 'docs_retrieved') {
                appendDocsCitations(ev.hits);
            } else if (ev.kind === 'post_edit_diagnostics') {
                appendPostEditDiagnostics(ev.path, ev.errors, ev.warnings, ev.clean);
            } else if (ev.kind === 'tool_call_start') {
                if (firstDelta) { hideThinking(); firstDelta = false; }
                const icon = toolIcon(ev.name);
                const row = appendToolRow(icon, ev.name);
                toolRows.set(ev.id, row);
            } else if (ev.kind === 'tool_call_result') {
                const row = toolRows.get(ev.id);
                if (!row) return;
                if (ev.ok) row.done(ev.result);
                else row.fail(typeof ev.result === 'string' ? ev.result : formatJson(ev.result));
            } else if (ev.kind === 'budget_warning') {
                showBudgetWarning(ev.tokens, ev.max);
            } else if (ev.kind === 'eviction') {
                showEvictionNotice(ev.result, ev.tokensBefore, ev.tokensAfter, ev.max);
            } else if (ev.kind === 'error') {
                if (firstDelta) { hideThinking(); firstDelta = false; }
                const err = document.createElement('div');
                err.className = 'ai-msg ai-msg-error';
                err.textContent = `Error: ${ev.message}`;
                messagesEl.appendChild(err);
                scrollToBottom();
            }
        });

        try {
            await agent.send(text);
        } finally {
            unbind();
            if (firstDelta) hideThinking();
            saveChat();
            setGenerating(false);
            inputEl.disabled = false;
            inputEl.focus();
        }
    }

    sendBtn.addEventListener('click', () => void handleSend());
    inputEl.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            void handleSend();
        }
    });
    inputEl.addEventListener('input', () => {
        inputEl.style.height = 'auto';
        inputEl.style.height = `${Math.min(inputEl.scrollHeight, 140)}px`;
    });

    clearBtn.addEventListener('click', () => {
        startNewChat();
    });

    loadBtn.addEventListener('click', () => {
        void loadSelectedProvider();
    });
}

function toolIcon(name: string): string {
    switch (name) {
        case 'list_files': return '📂';
        case 'read_file': return '📄';
        case 'apply_edit': return '✏️';
        case 'create_file': return '✨';
        case 'search_docs': return '🔍';
        default: return '⚙️';
    }
}

// ─── Models panel ─────────────────────────────────────────────────────────────

export function mountAiModels(container: HTMLElement): void {
    const list = container.querySelector<HTMLElement>('.ai-models-list')!;

    interface RowState {
        id: string;
        label: string;
        note?: string;
        rowEl: HTMLElement;
        statusEl: HTMLElement;
        btnEl: HTMLButtonElement;
        barEl: HTMLElement;
        barFill: HTMLElement;
    }
    const rows: RowState[] = [];

    function renderRows(): void {
        list.innerHTML = '';
        rows.length = 0;

        for (const entry of PROVIDER_CATALOG) {
            const row = document.createElement('div');
            row.className = 'ai-model-row';

            const info = document.createElement('div');
            info.className = 'ai-model-info';

            const nameEl = document.createElement('div');
            nameEl.className = 'ai-model-name';
            nameEl.textContent = entry.label;

            const noteEl = document.createElement('div');
            noteEl.className = 'ai-model-note';
            noteEl.textContent = entry.note ?? '';

            info.append(nameEl, noteEl);

            const right = document.createElement('div');
            right.className = 'ai-model-right';

            const statusEl = document.createElement('span');
            statusEl.className = 'ai-model-status';

            const btnEl = document.createElement('button');
            btnEl.className = 'ai-model-btn';

            right.append(statusEl, btnEl);

            const barWrap = document.createElement('div');
            barWrap.className = 'ai-model-bar';
            barWrap.hidden = true;
            const barFill = document.createElement('div');
            barFill.className = 'ai-model-bar-fill';
            barWrap.appendChild(barFill);

            row.append(info, right, barWrap);
            list.appendChild(row);

            const state: RowState = { id: entry.id, label: entry.label, note: entry.note, rowEl: row, statusEl, btnEl, barEl: barWrap, barFill };
            rows.push(state);

            btnEl.addEventListener('click', () => {
                if (engineStatus === 'loading') return;
                setSelectedProviderId(entry.id);
                // Force re-creation of the provider on next load
                provider = null;
                updateRows();
                void loadSelectedProvider().catch(() => { /* error shown via status */ });
            });

            updateRowState(state);
        }
    }

    function updateRowState(state: RowState): void {
        const selected = getSelectedProviderId() === state.id;
        const isLoading = engineStatus === 'loading' && selected;
        const isReady = engineStatus === 'ready' && selected;

        state.rowEl.classList.toggle('ai-model-active', isReady);

        if (isReady) {
            state.statusEl.textContent = 'Active';
            state.statusEl.className = 'ai-model-status ai-model-status-ready';
            state.btnEl.textContent = 'Loaded';
            state.btnEl.disabled = true;
            state.barEl.hidden = true;
        } else if (isLoading) {
            state.statusEl.textContent = 'Loading…';
            state.statusEl.className = 'ai-model-status ai-model-status-loading';
            state.btnEl.textContent = 'Loading…';
            state.btnEl.disabled = true;
            state.barEl.hidden = false;
        } else {
            state.statusEl.textContent = selected ? 'Selected' : '';
            state.btnEl.textContent = selected ? 'Load' : 'Use';
            state.btnEl.disabled = false;
            state.barEl.hidden = true;
        }
    }

    function updateRows(): void {
        for (const state of rows) updateRowState(state);
    }

    statusListeners.add(updateRows);
    progressListeners.add((_, pct) => {
        const sel = getSelectedProviderId();
        for (const state of rows) {
            if (state.id === sel) state.barFill.style.width = `${Math.round(pct * 100)}%`;
        }
    });

    renderRows();
}
