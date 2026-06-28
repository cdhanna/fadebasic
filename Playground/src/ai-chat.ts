// AI Chat panel + Model Manager panel.
//
// mountAiChat(el, workspace) — wires up the chat panel with the new agent.
// mountAiModels(el)          — wires up the provider/model selector.
//
// This file is now a thin UI layer over src/ai/ — the agent loop, tool
// registry, RAG, and provider abstraction all live there. We keep this
// file focused on DOM rendering and chat persistence.

import { Agent, type AgentEvent } from './ai/agent';
import { GrammarAgent } from './ai/loop/grammar-agent';
import { getRetriever } from './ai/rag/retrieval';
import { getLogger } from './log-bus';
import { createDefaultRegistry } from './ai/tools/default-registry';
import { reviewProposedEdit } from './ai/code-reviewer';
import { renderAssistantMarkdown } from './ai/ui/assistant-markdown';
import { createSlashAutocomplete } from './ai/ui/slash-autocomplete';
import { headingTail } from './ai/rag/doc-citation-links';
import { mountDiffApproval, mountConfirm, type DiffApprovalHandle } from './ai/ui/diff-approval';
import type { AgentPlan } from './ai/tool-protocol';
import type { DiagnosticsProvider, EditorAdapter, ToolRegistry, ToolContext } from './ai/tools';
import { createDefaultSlashRegistry } from './ai/slash-commands/default-registry';
import { emptySlashState } from './ai/slash-commands/registry';
import type { SlashResult, SlashStateSnapshot } from './ai/slash-commands/types';
import {
    PROVIDER_CATALOG,
    createSelectedProvider,
    disposeInferenceWorker,
    getSelectedProviderId,
    markProviderLoaded,
    setSelectedProviderId,
    shouldAutoLoadProvider,
    type ChatProvider,
    type Msg,
} from './ai/providers';
import {
    GhostBotProvider,
    type GhostConnectionState,
} from './ai/providers/ghostbot-provider';
import {
    formatProviderLoadError,
    providerErrorSummary,
} from './ai/provider-load-errors';

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
    /** LSP tokenize hook for Fade code fences in assistant markdown. */
    tokenizeSnippet?: (source: string) => Promise<Array<{ line: number; col: number; length: number; type: number }>>;
    /** Open a retrieved doc chunk in the Help panel (command or doc section). */
    openDocCitation?: (source: string, heading: string) => void | Promise<void>;
    /** Open docs for a Fade symbol (command/keyword) clicked in a snippet. */
    openSymbolDocs?: (symbol: string) => void | Promise<void>;
    /** LSP-check proposed Fade source before diff approval. */
    validateEditContent?: (path: string, content: string) => Promise<import('./ai/tools').DiagnosticEntry[]>;
    /** Loaded Fade command names for the agent system prompt. */
    getCommandNames?: () => Promise<string[]>;
    /** Names of commands that RETURN a value (so they must be called with
     *  parens). Derived from command signatures; used by the review pass. */
    getValueReturningCommands?: () => Promise<string[]>;
    /** Full per-command docs (name + raw signature + rendered markdown). Used
     *  by the coder loop to inject the EXACT signatures/params for the commands
     *  a program will actually use. */
    getCommandDocs?: () => Promise<Array<{ name: string; signature: string; markdown: string }>>;
    /** Agent access to the asset Catalog (search + import). */
    catalog?: import('./ai/tools').CatalogToolApi;
}

type EngineStatus = 'idle' | 'loading' | 'ready' | 'error';

// ─── Module-level provider state ──────────────────────────────────────────────

let provider: ChatProvider | null = null;
let engineStatus: EngineStatus = 'idle';
let engineError: string | null = null;
const statusListeners = new Set<(s: EngineStatus, detail?: string) => void>();
const progressListeners = new Set<(text: string, pct: number) => void>();

/** Registered by mountAiChat — binds GhostBot connection UI when the chat panel exists. */
type GhostBotBinder = (p: GhostBotProvider | null) => void;
let ghostBotBinder: GhostBotBinder | null = null;

// ─── Edit-approval mode ────────────────────────────────────────────────────
// 'manual' (default) shows a diff the user approves before writing; 'auto'
// applies edits immediately. Persisted so it survives reloads.
export type EditMode = 'manual' | 'auto';
const EDIT_MODE_KEY = 'fade.ai.editMode';
let editMode: EditMode = (localStorage.getItem(EDIT_MODE_KEY) as EditMode) || 'manual';
const editModeListeners = new Set<(m: EditMode) => void>();

function getEditMode(): EditMode { return editMode; }
function setEditMode(m: EditMode): void {
    editMode = m;
    localStorage.setItem(EDIT_MODE_KEY, m);
    for (const fn of editModeListeners) fn(m);
}

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
        provider = createSelectedProvider();
        provider.onProgress(({ text, pct }) => notifyProgress(text, pct));
        if (provider instanceof GhostBotProvider) {
            ghostBotBinder?.(provider);
            notifyProgress(
                provider.hasJoinCode()
                    ? `Connecting to GhostBot (${provider.getJoinCode()})…`
                    : "Enter your GhostBot's code below to connect.",
                0.05,
            );
        } else {
            ghostBotBinder?.(null);
        }
        await provider.ensureReady();
        markProviderLoaded(getSelectedProviderId());
        notifyStatus('ready');
    } catch (err) {
        // User pressed Stop while waiting for GhostBot — back to idle, not
        // an error state.
        if ((err as Error)?.name === 'GhostBotCancelled') {
            provider = null;
            ghostBotBinder?.(null);
            notifyStatus('idle');
            return;
        }
        provider = null;
        ghostBotBinder?.(null);
        const detail = formatProviderLoadError(err, getSelectedProviderId());
        notifyStatus('error', detail);
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
        disposeInferenceWorker();
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

function formatToolError(raw: unknown): string {
    if (typeof raw === 'string') return raw;
    if (raw && typeof raw === 'object') {
        const r = raw as Record<string, unknown>;
        const hint = typeof r.hint === 'string' && r.hint.trim() ? `\n→ ${r.hint.trim()}` : '';
        if (typeof r.review === 'string' && r.review.trim()) {
            return `Code review rejected:\n${r.review.trim()}${hint}`;
        }
        if (typeof r.error === 'string') {
            // Surface Zod validation detail — otherwise "Invalid arguments"
            // is useless to the user (and to us debugging).
            const issues = Array.isArray(r.issues) ? r.issues : null;
            let base = r.error;
            if (issues && issues.length) {
                const lines = issues.map((i) => {
                    const o = i as { path?: string; message?: string };
                    return o.path ? `• ${o.path}: ${o.message}` : `• ${o.message}`;
                });
                base = `${r.error}:\n${lines.join('\n')}`;
            }
            const fmt = typeof r.correctFormat === 'string' ? `\nFormat: ${r.correctFormat}` : '';
            return `${base}${fmt}${hint}`;
        }
    }
    return formatJson(raw);
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
    const slashPopup = pane.querySelector<HTMLElement>('.ai-slash-popup')!;
    const stopBtn = pane.querySelector<HTMLButtonElement>('.ai-stop-btn')!;
    const statusEl = pane.querySelector<HTMLElement>('.ai-chat-status')!;
    const loadBtn = pane.querySelector<HTMLButtonElement>('.ai-load-btn')!;
    const clearBtn = pane.querySelector<HTMLButtonElement>('.ai-clear-btn')!;
    const modeBtn = pane.querySelector<HTMLButtonElement>('.ai-mode-btn')!;
    const progressBar = pane.querySelector<HTMLElement>('.ai-progress-bar-fill')!;
    const progressRow = pane.querySelector<HTMLElement>('.ai-progress-row')!;
    const progressPct = pane.querySelector<HTMLElement>('.ai-progress-pct')!;
    const progressDetail = pane.querySelector<HTMLElement>('.ai-progress-detail')!;
    const ghostBotBar = pane.querySelector<HTMLElement>('.ai-ghostbot-bar')!;
    const ghostBotLabel = pane.querySelector<HTMLElement>('.ai-ghostbot-label')!;
    const ghostBotCodeInput = pane.querySelector<HTMLInputElement>('.ai-ghostbot-code-input')!;
    const ghostBotHint = pane.querySelector<HTMLElement>('.ai-ghostbot-hint')!;
    const ghostBotConnectBtn = pane.querySelector<HTMLButtonElement>('.ai-ghostbot-connect')!;
    const ghostBotStopBtn = pane.querySelector<HTMLButtonElement>('.ai-ghostbot-stop')!;
    const historyBtn = pane.querySelector<HTMLButtonElement>('.ai-history-btn')!;
    const historyMenu = pane.querySelector<HTMLElement>('.ai-history-menu')!;
    const historyList = pane.querySelector<HTMLElement>('.ai-history-list')!;
    const historyNewBtn = pane.querySelector<HTMLButtonElement>('.ai-history-new')!;

    let activeChatId: string = newChatId();
    let generating = false;
    let agent: Agent | GrammarAgent | null = null;
    /** Tracks which provider the current agent was built against. */
    let boundProviderId: string | null = null;
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
    /** Active review progress row shown during pre-diff LSP check. */
    let hideReviewing: (() => void) | null = null;
    let reviewProgressTimer: ReturnType<typeof setInterval> | null = null;
    let ghostBotConnUnsub: (() => void) | null = null;
    let ghostBotReconnecting = false;
    let ghostBotConnState: GhostConnectionState | null = null;

    function clearReviewProgress(): void {
        if (reviewProgressTimer) {
            clearInterval(reviewProgressTimer);
            reviewProgressTimer = null;
        }
        hideReviewing?.();
        hideReviewing = null;
    }

    function startReviewProgress(label: string): void {
        clearReviewProgress();
        let elapsed = 0;
        hideReviewing = showThinking(label);
        reviewProgressTimer = setInterval(() => {
            elapsed += 2;
            hideReviewing?.();
            hideReviewing = showThinking(`${label} (${elapsed}s)`);
        }, 2000);
    }

    const store = new ChatStore();
    void store.init().catch(e => console.warn('[fade/ai] chat store init failed:', e));

    // Cache the authoritative command list — used by the deterministic Fade
    // pre-check in code review. Cheap on repeat (one LSP round-trip, then
    // reused); failures degrade to an empty list (pre-check just skips).
    let cachedCommandNames: Promise<string[]> | null = null;
    // Lowercased command tokens for the synchronous snippet highlighter —
    // includes whole single-word names AND the individual words of multi-word
    // commands (so each word of e.g. "key down" colors). Populated lazily.
    const commandWords = new Set<string>();
    const populateCommandWords = (names: string[]): void => {
        for (const name of names) {
            for (const word of name.toLowerCase().split(/\s+/)) {
                if (word.length >= 2) commandWords.add(word);
            }
        }
    };
    const getCommandNamesCached = (): Promise<string[]> => {
        if (!deps.getCommandNames) return Promise.resolve([]);
        if (!cachedCommandNames) {
            cachedCommandNames = deps.getCommandNames().catch(() => []);
            void cachedCommandNames.then(populateCommandWords);
        }
        return cachedCommandNames;
    };
    // Warm the command set so highlighting has it on the first answer.
    void getCommandNamesCached();

    // The explicit decision-tree loop (GrammarAgent) is the DEFAULT — disable
    // with __fadeAiHelpers.setGrammarLoop(false) to fall back to the ReAct Agent.
    const useGrammarLoop = (): boolean => {
        try { return localStorage.getItem('fade.ai.grammarLoop') !== '0'; }
        catch { return true; }
    };

    function buildAgent(): Agent | GrammarAgent | null {
        if (!provider) return null;
        // Capture so the closures below see a narrowed non-null type;
        // the field-level `provider` reference isn't auto-narrowed
        // through the arrow function below.
        const activeProvider = provider;
        toolRegistry = createDefaultRegistry();
        boundProviderId = activeProvider.id;
        const toolLog = getLogger('ai/tool');

        const toolContext: ToolContext = {
                workspace,
                diagnostics: deps.diagnostics,
                editor: deps.editor,
                reviewEdit: activeProvider
                    ? async (req) => reviewProposedEdit(activeProvider, {
                        ...req,
                        validateContent: deps.validateEditContent,
                        commandNames: await getCommandNamesCached(),
                    }, {
                        llmReview: false,
                        signal: agent?.getAbortSignal?.(),
                        onPhase: (phase) => {
                            const label = phase === 'lsp'
                                ? 'Checking syntax (LSP)…'
                                : 'AI code review…';
                            startReviewProgress(label);
                        },
                    })
                    : undefined,
                onEditReviewStart: () => {
                    startReviewProgress('Checking syntax (LSP)…');
                },
                onEditReviewEnd: () => {
                    clearReviewProgress();
                },
                confirmEdit: (path, oldContent, newContent) => {
                    // Log every approval request — when a hang happens
                    // again, the Logs panel shows what file + sizes were
                    // involved (delta=0 usually means a no-op edit).
                    toolLog.info(
                        `confirmEdit: ${path} (old=${oldContent.length}b, new=${newContent.length}b, delta=${newContent.length - oldContent.length}b, mode=${editMode})`,
                    );
                    // Auto mode: skip the diff modal, apply immediately, and
                    // drop a compact "applied" notice so it's still visible.
                    if (editMode === 'auto') {
                        appendAutoEditNotice(path, oldContent, newContent);
                        return Promise.resolve(true);
                    }
                    return requestDiffApproval(path, oldContent, newContent);
                },
                projectType: deps.getProjectType,
                catalog: deps.catalog,
                // Lint a shown-but-not-applied snippet as a standalone file
                // (a scratch name → the validator's non-project path).
                lintFadeSnippet: deps.validateEditContent
                    ? (source: string) => deps.validateEditContent!('__ai_snippet__.fbasic', source)
                    : undefined,
        };

        if (useGrammarLoop()) {
            getLogger('ai/agent').info('using explicit decision-tree loop (GrammarAgent)');
            return new GrammarAgent({
                provider: activeProvider,
                retriever: getRetriever(),
                getCommandNames: deps.getCommandNames,
                getValueReturningCommands: deps.getValueReturningCommands,
                getCommandDocs: deps.getCommandDocs,
                getProjectType: deps.getProjectType,
                tools: toolRegistry,
                toolContext,
                confirmCatalogImport: requestImportApproval,
            });
        }

        return new Agent({
            provider: activeProvider,
            tools: toolRegistry,
            toolContext,
            getProjectType: deps.getProjectType,
            getCommandNames: deps.getCommandNames,
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

    // Starter prompts shown on a fresh, empty chat to get the conversation going.
    const STARTER_CHIPS: ReadonlyArray<{ title: string; prompt: string }> = [
        { title: 'Sprites + arrow keys demo', prompt: 'write me a simple demo using sprites and arrow keys' },
        { title: 'What does this project do?', prompt: 'what does this project do so far?' },
        { title: 'How do I learn Fade BASIC?', prompt: 'how do I start learning fade basic?' },
    ];

    // Show the starter chips only when the transcript is empty (fresh chat) and
    // they're not already on screen — used both on mount and after a reset.
    function showStarterChips(): void {
        if (messagesEl.querySelector('.ai-msg, .ai-suggestions')) return;
        appendSuggestions(STARTER_CHIPS, 'Try:');
    }

    function startNewChat(): void {
        rejectPendingDiffs();
        activeChatId = newChatId();
        agent?.clearHistory();
        messagesEl.innerHTML = '';
        genBar.hide();
        showStarterChips();
    }

    function ensureAgent(): Agent | GrammarAgent | null {
        if (!provider) return null;
        if (!agent || boundProviderId !== provider.id) {
            rejectPendingDiffs();
            agent = buildAgent();
        }
        return agent;
    }

    async function loadChat(id: string): Promise<void> {
        const record = await store.load(id);
        if (!record) return;
        ensureAgent();
        agent?.setHistory(record.messages);
        activeChatId = id;
        renderHistoryToDOM(record.messages);
    }

    function renderHistoryToDOM(msgs: Msg[]): void {
        messagesEl.innerHTML = '';
        genBar.hide();
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

    function isGhostBotSelected(): boolean {
        return getSelectedProviderId() === 'ghostbot:local';
    }

    function ghostBotProvider(): GhostBotProvider | null {
        return provider instanceof GhostBotProvider ? provider : null;
    }

    function isGhostBotConnected(): boolean {
        const gb = ghostBotProvider();
        if (!gb) return true;
        return gb.getConnectionState().status === 'connected';
    }

    function ghostStatusLabel(state: GhostConnectionState): string {
        switch (state.status) {
            case 'connected': {
                if (state.modelLoaded === false) return 'Connected — no model loaded';
                if (state.modelName) {
                    const name = state.modelName.length > 28
                        ? `${state.modelName.slice(0, 27)}…`
                        : state.modelName;
                    return `Connected — ${name}`;
                }
                return 'Connected';
            }
            case 'waiting': return 'Looking for GhostBot…';
            case 'pending': return 'Waiting for approval';
            case 'reconnecting': return 'Reconnecting…';
            case 'disconnected': return 'Disconnected';
            case 'error': return 'Connection error';
            default: return 'GhostBot';
        }
    }

    function storedGhostJoinCode(): string {
        return localStorage.getItem('fade.ai.ghostbot.code') ?? '';
    }

    function bindGhostBotConnection(p: GhostBotProvider | null): void {
        ghostBotConnUnsub?.();
        ghostBotConnUnsub = null;
        if (!p) {
            ghostBotConnState = null;
            renderGhostBotBar();
            return;
        }
        ghostBotConnUnsub = p.onConnectionState((s) => {
            ghostBotConnState = s;
            renderGhostBotBar();
            renderStatus();
        });
    }

    function renderGhostBotBar(): void {
        const show = isGhostBotSelected();
        ghostBotBar.hidden = !show;
        if (!show) return;

        const gb = ghostBotProvider();
        const state = ghostBotConnState ?? gb?.getConnectionState() ?? {
            status: (engineStatus === 'ready' ? 'waiting' : 'idle') as GhostConnectionState['status'],
            joinCode: storedGhostJoinCode(),
            detail: engineStatus === 'ready'
                ? 'Waiting for GhostBot desktop app'
                : 'Click Load Model to open a session',
        };

        const connectedNoModel = state.status === 'connected' && state.modelLoaded === false;
        ghostBotBar.dataset.status = connectedNoModel ? 'connected-nomodel' : state.status;
        ghostBotLabel.textContent = ghostStatusLabel(state);
        // Don't clobber the field while the user is typing into it.
        if (document.activeElement !== ghostBotCodeInput) {
            ghostBotCodeInput.value = state.joinCode ?? '';
        }
        const hint = connectedNoModel
            ? 'GhostBot is paired but has no model loaded — load one in the GhostBot app.'
            : state.detail ?? '';
        ghostBotHint.textContent = hint;
        ghostBotHint.title = hint;

        const busy = ghostBotReconnecting || engineStatus === 'loading';
        ghostBotConnectBtn.disabled = busy;
        ghostBotConnectBtn.textContent = state.status === 'connected' || state.status === 'pending'
            ? 'Reconnect' : 'Connect';

        // Stop ends the session: cancels the wait while pairing, disconnects
        // when paired. Hidden only when there is no session to end.
        const stoppable = state.status === 'waiting'
            || state.status === 'pending'
            || state.status === 'reconnecting'
            || state.status === 'connected'
            || state.status === 'disconnected'
            || engineStatus === 'loading';
        ghostBotStopBtn.hidden = !stoppable;
        ghostBotStopBtn.textContent = state.status === 'connected' ? 'Disconnect' : 'Stop';
        ghostBotStopBtn.title = state.status === 'connected'
            ? 'Disconnect from GhostBot and end the session'
            : 'Stop waiting and end the session';
    }

    async function stopGhostBotSession(): Promise<void> {
        const gb = ghostBotProvider();
        if (!gb) return;
        await gb.reset();          // aborts a pending wait + leaves the room
        provider = null;
        ghostBotReconnecting = false;
        notifyStatus('idle');
        renderGhostBotBar();
        renderStatus();
    }

    async function reconnectGhostBot(): Promise<void> {
        const gb = ghostBotProvider();
        if (!gb || ghostBotReconnecting) return;
        ghostBotReconnecting = true;
        renderGhostBotBar();
        notifyStatus('loading');
        try {
            if (engineStatus !== 'ready' || !provider) {
                provider = gb;
                gb.onProgress(({ text, pct }) => notifyProgress(text, pct));
            }
            await gb.reconnect();
            markProviderLoaded(getSelectedProviderId());
            notifyStatus('ready');
        } catch (err) {
            if ((err as Error)?.name === 'GhostBotCancelled') {
                notifyStatus('idle');
            } else {
                notifyStatus('error', formatProviderLoadError(err, getSelectedProviderId()));
            }
        } finally {
            ghostBotReconnecting = false;
            renderGhostBotBar();
            renderStatus();
        }
    }

    // ── Status rendering ────────────────────────────────────────────────────
    function renderStatus() {
        const providerLabel = provider?.label ?? PROVIDER_CATALOG.find(p => p.id === getSelectedProviderId())?.label ?? '—';
        renderGhostBotBar();
        if (engineStatus === 'ready') {
            const ghostOk = isGhostBotConnected();
            statusEl.textContent = ghostOk ? providerLabel : 'GhostBot disconnected';
            statusEl.className = ghostOk
                ? 'ai-chat-status ai-status-ready'
                : 'ai-chat-status ai-status-error';
            loadBtn.hidden = true;
            sendBtn.disabled = !ghostOk;
            inputEl.disabled = !ghostOk;
            progressRow.hidden = true;
        } else if (engineStatus === 'loading') {
            const pct = progressPct.textContent;
            statusEl.textContent = pct && pct !== '0%' ? `Loading ${pct}` : 'Loading…';
            statusEl.className = 'ai-chat-status ai-status-loading';
            loadBtn.hidden = true;
            sendBtn.disabled = true;
            inputEl.disabled = true;
            progressRow.hidden = false;
        } else if (engineStatus === 'error') {
            const detail = engineError ?? 'Unknown error';
            const summary = providerErrorSummary(detail, getSelectedProviderId());
            statusEl.textContent = summary;
            statusEl.title = detail;
            statusEl.className = 'ai-chat-status ai-status-error';
            loadBtn.hidden = false;
            loadBtn.textContent = 'Retry';
            sendBtn.disabled = true;
            inputEl.disabled = true;
            progressRow.hidden = true;
            if (isGhostBotSelected()) {
                ghostBotHint.textContent = detail;
                ghostBotHint.title = detail;
            }
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

    function onEngineStatusChange(): void {
        bindGhostBotConnection(ghostBotProvider());
        renderStatus();
        // Rebind the agent when a new model finishes loading (or after a
        // failed load recovers) so sends don't hit a stale provider.
        if (engineStatus === 'ready' && provider && boundProviderId !== provider.id) {
            rejectPendingDiffs();
            agent = buildAgent();
        }
        if (engineStatus === 'idle' || engineStatus === 'error') {
            boundProviderId = null;
            agent = null;
        }
    }

    function applyChatLoadProgress(text: string, pct: number): void {
        const pctInt = Math.min(100, Math.max(0, Math.round(pct * 100)));
        progressBar.style.width = `${pctInt}%`;
        progressPct.textContent = `${pctInt}%`;
        progressDetail.textContent = text.replace(/^\d+% — /, '') || 'Loading…';
    }

    statusListeners.add(onEngineStatusChange);
    progressListeners.add((text, pct) => {
        applyChatLoadProgress(text, pct);
    });
    ghostBotBinder = bindGhostBotConnection;
    renderStatus();
    if (isGhostBotSelected()) {
        bindGhostBotConnection(ghostBotProvider());
        renderGhostBotBar();
    }

    // ── Message rendering ───────────────────────────────────────────────────
    function appendUserBubble(text: string): void {
        const div = document.createElement('div');
        div.className = 'ai-msg ai-msg-user';
        div.textContent = text;
        messagesEl.appendChild(div);
        scrollToBottom();
    }

    function appendAssistantBubble(): {
        setText(t: string): void;
        appendText(t: string): void;
        finalize(): Promise<void>;
        el: HTMLElement;
    } {
        const div = document.createElement('div');
        div.className = 'ai-msg ai-msg-assistant ai-msg-markdown ai-msg-hidden';
        // Insert immediately to RESERVE the bubble's position in the
        // transcript — otherwise end-of-turn notices (suggestions, lint)
        // emitted before the deferred render would append ahead of it. The
        // bubble stays hidden (ai-msg-hidden) until it has non-whitespace
        // content, so a tool-only turn never shows an empty box.
        messagesEl.appendChild(div);
        let buf = '';
        let renderTimer: ReturnType<typeof setTimeout> | null = null;

        // Render the accumulated buffer as markdown. `live` (streaming) renders
        // STRUCTURE ONLY — formatted, but no async highlight/copy buttons, which
        // flicker when re-run on incomplete code every token. The final pass
        // (live=false) does the full highlight once.
        const flush = async (live: boolean) => {
            renderTimer = null;
            if (buf.trim()) {
                div.classList.remove('ai-msg-hidden');
                await renderAssistantMarkdown(div, buf, {
                    tokenize: deps.tokenizeSnippet,
                    onSymbolDocs: deps.openSymbolDocs,
                    commandWords,
                    live,
                });
                scrollToBottom();
            } else {
                div.classList.add('ai-msg-hidden');
            }
        };
        // Debounce incremental renders so rapid token deltas don't thrash the
        // markdown renderer — ~90ms still reads as live typing.
        const scheduleRender = () => {
            if (renderTimer) return;
            renderTimer = setTimeout(() => { void flush(true); }, 90);
        };

        return {
            setText(t: string) { buf = t; void flush(false); },
            appendText(t: string) { buf += t; scheduleRender(); },
            async finalize() {
                if (renderTimer) { clearTimeout(renderTimer); renderTimer = null; }
                await flush(false);
                // An assistant turn that produced no prose (only tool calls)
                // leaves nothing to show — drop the reserved node.
                if (!buf.trim()) div.remove();
            },
            el: div,
        };
    }

    // Live generation strip above the input — surfaces token throughput and
    // the current phase (thinking / generating / running a tool) in the
    // Playground itself, mirroring the GhostBot desktop activity card.
    const genBarEl = pane.querySelector<HTMLElement>('.ai-genbar')!;
    const genBarDot = pane.querySelector<HTMLElement>('.ai-genbar-dot')!;
    const genBarLabel = pane.querySelector<HTMLElement>('.ai-genbar-label')!;
    const genBarStats = pane.querySelector<HTMLElement>('.ai-genbar-stats')!;
    void genBarDot;

    function createGenBar() {
        let tokens = 0;
        let genStartMs = 0;
        let statsTimer: ReturnType<typeof setInterval> | null = null;

        const tps = (): number => {
            if (genStartMs === 0) return 0;
            const secs = (performance.now() - genStartMs) / 1000;
            return secs > 0 ? tokens / secs : 0;
        };
        const renderStats = () => {
            genBarStats.textContent = tokens > 0
                ? `${tokens} tok · ${tps().toFixed(1)}/s`
                : '';
        };
        const stopTimer = () => {
            if (statsTimer) { clearInterval(statsTimer); statsTimer = null; }
        };

        return {
            start() {
                tokens = 0;
                genStartMs = 0;
                stopTimer();
                genBarEl.hidden = false;
                genBarEl.dataset.state = 'thinking';
                genBarLabel.textContent = 'Thinking…';
                genBarStats.textContent = '';
            },
            phase(state: 'thinking' | 'generating' | 'tool', label: string) {
                genBarEl.dataset.state = state;
                genBarLabel.textContent = label;
            },
            token() {
                if (genStartMs === 0) {
                    genStartMs = performance.now();
                    // Tick the rate even when deltas are bursty/paused.
                    statsTimer = setInterval(renderStats, 250);
                }
                tokens++;
                if (genBarEl.dataset.state !== 'generating') {
                    genBarEl.dataset.state = 'generating';
                    genBarLabel.textContent = 'Generating…';
                }
                renderStats();
            },
            finalize(state: 'done' | 'error', label?: string) {
                stopTimer();
                genBarEl.dataset.state = state;
                if (state === 'error') {
                    genBarLabel.textContent = label ?? 'Error';
                    return;
                }
                if (tokens > 0) {
                    const secs = ((performance.now() - genStartMs) / 1000).toFixed(1);
                    genBarLabel.textContent = 'Done';
                    genBarStats.textContent = `${tokens} tok · ${secs}s · ${tps().toFixed(1)}/s`;
                } else {
                    // Nothing streamed (e.g. tool-only turn) — no stats to show.
                    genBarEl.hidden = true;
                }
            },
            hide() { stopTimer(); genBarEl.hidden = true; },
        };
    }
    const genBar = createGenBar();

    function showThinking(label = 'Thinking…'): () => void {
        const div = document.createElement('div');
        div.className = 'ai-thinking';
        const dots = document.createElement('span');
        dots.className = 'ai-thinking-dots';
        dots.innerHTML = '<span></span><span></span><span></span>';
        div.appendChild(dots);
        if (label) {
            const text = document.createElement('span');
            text.className = 'ai-thinking-label';
            text.textContent = label;
            div.appendChild(text);
        }
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
            const label = hit.chunk.heading
                ? `${hit.chunk.source} → ${headingTail(hit.chunk.heading)}`
                : hit.chunk.source;
            const link = document.createElement('button');
            link.type = 'button';
            link.className = 'ai-docs-link';
            link.textContent = label;
            link.title = `Open ${hit.chunk.source}`;
            link.addEventListener('click', () => {
                if (deps.openDocCitation) {
                    void deps.openDocCitation(hit.chunk.source, hit.chunk.heading);
                }
            });
            const score = document.createElement('span');
            score.className = 'ai-docs-score';
            score.textContent = ` (${hit.score.toFixed(2)})`;
            li.append(link, score);
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

    function appendReviewNotice(message: string): void {
        if (!message.includes('review rejected') && !message.includes('LSP')) return;
        const row = document.createElement('div');
        row.className = 'ai-msg ai-msg-error ai-review-notice';
        row.textContent = message;
        messagesEl.appendChild(row);
        scrollToBottom();
    }

    function appendAssetReport(
        present: ReadonlyArray<{ category: string; name: string }>,
        missing: ReadonlyArray<{ category: string; name: string }>,
    ): void {
        if (missing.length === 0) return; // only surface problems
        const row = document.createElement('div');
        row.className = 'ai-asset-report';
        const names = missing.map(m => `${m.name} (${m.category})`).join(', ');
        const present_ = present.length ? ` ${present.length} referenced asset${present.length === 1 ? '' : 's'} found.` : '';
        row.innerHTML = '<span class="ai-asset-report-icon">⚠︎</span><span class="ai-asset-report-text"></span>';
        row.querySelector('.ai-asset-report-text')!.textContent =
            `Missing asset${missing.length === 1 ? '' : 's'}: ${names} — not in this project.${present_}`;
        messagesEl.appendChild(row);
        scrollToBottom();
    }

    function appendCodeLint(issues: ReadonlyArray<{ line: number; message: string; code?: string }>): void {
        if (issues.length === 0) return;
        const row = document.createElement('div');
        row.className = 'ai-asset-report'; // reuse the amber-warning styling
        const head = document.createElement('div');
        head.innerHTML = '<span class="ai-asset-report-icon">⚠︎</span><span class="ai-asset-report-text"></span>';
        head.querySelector('.ai-asset-report-text')!.textContent =
            `The code above has ${issues.length} Fade compile error${issues.length === 1 ? '' : 's'}:`;
        row.appendChild(head);
        const list = document.createElement('ul');
        list.className = 'ai-code-lint-list';
        for (const i of issues) {
            const li = document.createElement('li');
            li.textContent = `L${i.line}: ${i.message}`;
            list.appendChild(li);
        }
        row.appendChild(list);
        messagesEl.appendChild(row);
        scrollToBottom();
    }

    function appendSuggestions(
        suggestions: ReadonlyArray<{ title: string; prompt: string }>,
        labelText = 'Next:',
    ): void {
        if (suggestions.length === 0) return;
        const wrap = document.createElement('div');
        wrap.className = 'ai-suggestions';
        const label = document.createElement('span');
        label.className = 'ai-suggestions-label';
        label.textContent = labelText;
        wrap.appendChild(label);
        for (const s of suggestions) {
            const chip = document.createElement('button');
            chip.type = 'button';
            chip.className = 'ai-suggestion-chip';
            chip.textContent = s.title;
            chip.title = s.prompt;
            chip.addEventListener('click', () => {
                if (generating) return;
                // Disable the whole group so a suggestion fires once.
                wrap.querySelectorAll('button').forEach(b => { (b as HTMLButtonElement).disabled = true; });
                inputEl.value = s.prompt;
                void handleSend();
            });
            wrap.appendChild(chip);
        }
        messagesEl.appendChild(wrap);
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

    // A collapsible "thinking" row for an internal agent step (classifying the
    // request, self-reviewing code, …). Mirrors the tool-row look so the loop's
    // internal reasoning is as legible as its tool calls.
    function appendReasoningRow(title: string, detail?: string, links?: import('./ai/agent').ReasoningLink[]): void {
        const row = document.createElement('div');
        row.className = 'ai-tool-row ai-reasoning-row';
        const header = document.createElement('button');
        header.className = 'ai-tool-header';
        const iconEl = document.createElement('span');
        iconEl.className = 'ai-tool-icon';
        iconEl.textContent = '💭';
        const labelEl = document.createElement('span');
        labelEl.className = 'ai-tool-label';
        labelEl.textContent = title;
        const chevron = document.createElement('span');
        chevron.className = 'ai-tool-chevron';
        chevron.textContent = detail ? '▶' : '';
        header.append(iconEl, labelEl, chevron);
        row.append(header);

        if (detail || (links && links.length)) {
            const det = document.createElement('div');
            det.className = 'ai-tool-detail';
            det.hidden = true;
            if (links && links.length) {
                // Render the detail as clickable chips — e.g. command names that
                // open their help-doc entry. Capped + scrollable via CSS.
                const wrap = document.createElement('div');
                wrap.className = 'ai-reasoning-links';
                for (const lk of links) {
                    const chip = document.createElement('button');
                    chip.type = 'button';
                    chip.className = 'ai-reasoning-link';
                    chip.textContent = lk.label;
                    if (lk.symbol && deps.openSymbolDocs) {
                        const sym = lk.symbol;
                        chip.addEventListener('click', () => { void deps.openSymbolDocs!(sym); });
                    } else {
                        chip.disabled = true;
                    }
                    wrap.append(chip);
                }
                det.append(wrap);
            } else {
                const pre = document.createElement('pre');
                pre.className = 'ai-tool-json';
                pre.textContent = detail!;
                det.append(pre);
            }
            row.append(det);
            let expanded = false;
            header.addEventListener('click', () => {
                expanded = !expanded;
                det.hidden = !expanded;
                chevron.textContent = expanded ? '▼' : '▶';
                scrollToBottom();
            });
        } else {
            header.disabled = true;
        }
        messagesEl.appendChild(row);
        scrollToBottom();
    }

    function appendToolRow(name: string, args: unknown): {
        done(result: unknown): void;
        fail(message: string): void;
    } {
        const { verb, target } = describeToolCall(name, args);
        const row = document.createElement('div');
        row.className = 'ai-tool-row';
        const header = document.createElement('button');
        header.className = 'ai-tool-header';
        header.disabled = true;
        const iconEl = document.createElement('span');
        iconEl.className = 'ai-tool-icon';
        iconEl.textContent = toolIcon(name);
        const labelEl = document.createElement('span');
        labelEl.className = 'ai-tool-label';
        // Show what the action is doing, e.g. "Reading" + "main.fbasic".
        labelEl.textContent = verb;
        if (target) {
            const targetEl = document.createElement('span');
            targetEl.className = 'ai-tool-target';
            targetEl.textContent = target;
            labelEl.append(' ', targetEl);
        }
        const badge = document.createElement('span');
        badge.className = 'ai-tool-badge ai-tool-badge-running';
        badge.textContent = 'running…';
        const chevron = document.createElement('span');
        chevron.className = 'ai-tool-chevron';
        chevron.textContent = '▶';
        header.append(iconEl, labelEl, badge, chevron);

        const detail = document.createElement('div');
        detail.className = 'ai-tool-detail';
        detail.hidden = true;

        // Always include what was attempted, so a failure shows the inputs
        // the model passed — not just an opaque error.
        const argsLabel = document.createElement('div');
        argsLabel.className = 'ai-tool-detail-label';
        argsLabel.textContent = 'Arguments';
        const argsPre = document.createElement('pre');
        argsPre.className = 'ai-tool-json';
        argsPre.innerHTML = highlightJson(formatJson(args ?? {}));
        detail.append(argsLabel, argsPre);

        row.append(header, detail);
        messagesEl.appendChild(row);
        scrollToBottom();

        const setExpanded = (on: boolean) => {
            detail.hidden = !on;
            chevron.textContent = on ? '▼' : '▶';
        };
        let expanded = false;
        header.addEventListener('click', () => {
            if (header.disabled) return;
            expanded = !expanded;
            setExpanded(expanded);
            scrollToBottom();
        });

        return {
            done(result: unknown) {
                badge.className = 'ai-tool-badge ai-tool-badge-done';
                badge.textContent = '✓';
                const lbl = document.createElement('div');
                lbl.className = 'ai-tool-detail-label';
                lbl.textContent = 'Result';
                const pre = document.createElement('pre');
                pre.className = 'ai-tool-json';
                pre.innerHTML = highlightJson(formatJson(result));
                detail.append(lbl, pre);
                header.disabled = false;
                scrollToBottom();
            },
            fail(message: string) {
                badge.className = 'ai-tool-badge ai-tool-badge-fail';
                badge.textContent = '✗';
                const lbl = document.createElement('div');
                lbl.className = 'ai-tool-detail-label';
                lbl.textContent = 'Error';
                const pre = document.createElement('pre');
                pre.className = 'ai-tool-json ai-tool-json-fail';
                pre.textContent = message;
                detail.append(lbl, pre);
                header.disabled = false;
                // Auto-expand failures so the reason + attempted args are
                // visible without a click.
                expanded = true;
                setExpanded(true);
                scrollToBottom();
            },
        };
    }

    /** Compact "auto-applied" row shown in place of the diff modal when the
     *  user has chosen auto-accept. Keeps edits visible without a gate. */
    function appendAutoEditNotice(path: string, oldContent: string, newContent: string): void {
        const oldLines = oldContent.split('\n').length;
        const newLines = newContent.split('\n').length;
        const delta = newLines - oldLines;
        const stat = delta === 0 ? '~' : (delta > 0 ? `+${delta}` : `${delta}`);
        const row = document.createElement('div');
        row.className = 'ai-auto-edit';
        row.innerHTML = '<span class="ai-auto-edit-icon">⚡</span><span class="ai-auto-edit-text"></span>';
        row.querySelector('.ai-auto-edit-text')!.textContent = `Auto-applied edit to ${path} (${stat} lines)`;
        messagesEl.appendChild(row);
        scrollToBottom();
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

    /** Confirm a catalog asset import before it downloads into the project
     *  (the GrammarAgent's "confirm before import" gate). Auto edit-mode skips
     *  the prompt, matching the diff-approval behaviour. */
    function requestImportApproval(entry: { name: string; kind: string; mime: string; bytes: number; license: string }): Promise<boolean> {
        if (editMode === 'auto') return Promise.resolve(true);
        return new Promise<boolean>(resolve => {
            let handle: DiffApprovalHandle | null = null;
            const settle = (approved: boolean) => {
                if (handle) pendingDiffApprovals.delete(handle);
                resolve(approved);
            };
            const sizeKb = entry.bytes > 0 ? `${Math.max(1, Math.round(entry.bytes / 1024))} KB · ` : '';
            handle = mountConfirm({
                container: messagesEl,
                title: `Import "${entry.name}" from the Catalog?`,
                detail: `${entry.kind} · ${entry.mime} · ${sizeKb}${entry.license || 'license n/a'}`,
                approveLabel: 'Import',
                rejectLabel: 'Skip',
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
        closeSlashPopup();
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
                    getEditMode,
                    setEditMode,
                    getConnectionInfo: () => {
                        const st = ghostBotConnState ?? ghostBotProvider()?.getConnectionState();
                        if (!st) return 'Not using GhostBot.';
                        const lines = [
                            `GhostBot code: ${st.joinCode || '(none set)'}`,
                            `Status: ${ghostStatusLabel(st)}`,
                        ];
                        if (st.modelLoaded === false) lines.push('Model: none loaded on GhostBot');
                        else if (st.modelName) lines.push(`Model: ${st.modelName}`);
                        if (st.detail) lines.push(st.detail);
                        return lines.join('\n');
                    },
                },
            }));
            if (slashResult) appendSlashResult(slashResult);
            inputEl.focus();
            return;
        }

        if (engineStatus !== 'ready' || !isGhostBotConnected()) return;
        inputEl.value = '';
        inputEl.style.height = 'auto';
        appendUserBubble(text);

        if (!ensureAgent()) return;
        // ensureAgent() returning true implies agent is non-null, but
        // TypeScript can't narrow through a function call on a let
        // binding. Belt-and-suspenders null check narrows the type for
        // the rest of the closure.
        if (!agent) return;

        setGenerating(true);
        inputEl.disabled = true;
        genBar.start();
        let hideThinking: (() => void) | null = showThinking();
        const clearThinking = () => { hideThinking?.(); hideThinking = null; };
        const ensureThinking = (label = 'Thinking…') => {
            clearThinking();
            hideThinking = showThinking(label);
        };
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

            if (ev.kind === 'model_token') {
                // Live throughput — every model token, even tool-call syntax.
                genBar.token();
            } else if (ev.kind === 'text_delta') {
                if (firstDelta) { clearThinking(); firstDelta = false; }
                if (!currentBubble) currentBubble = appendAssistantBubble();
                currentBubble.appendText(ev.delta);
            } else if (ev.kind === 'reasoning') {
                // Internal step (classifying, self-reviewing) — show as a
                // collapsible thinking row, and reflect it in the gen bar.
                appendReasoningRow(ev.title, ev.detail, ev.links);
                genBar.phase('thinking', ev.title);
            } else if (ev.kind === 'revising') {
                // Repair pass starting — clear the bubble so the rewrite streams
                // in cleanly (subsequent text_delta events fill it live).
                if (!currentBubble) currentBubble = appendAssistantBubble();
                currentBubble.setText('');
            } else if (ev.kind === 'answer_revised') {
                // The isolated repair sub-agent fixed the code — swap the
                // streamed (broken) bubble for the corrected, formatted answer.
                if (!currentBubble) currentBubble = appendAssistantBubble();
                currentBubble.setText(ev.text);
            } else if (ev.kind === 'iteration_start') {
                currentBubble = null;
                genBar.phase('thinking', 'Thinking…');
                ensureThinking('Thinking…');
            } else if (ev.kind === 'plan_emitted') {
                if (firstDelta) { clearThinking(); firstDelta = false; }
                genBar.phase('thinking', 'Planning…');
                appendPlanBubble(ev.plan);
                currentBubble = null;
            } else if (ev.kind === 'docs_retrieved') {
                appendDocsCitations(ev.hits);
            } else if (ev.kind === 'post_edit_diagnostics') {
                appendPostEditDiagnostics(ev.path, ev.errors, ev.warnings, ev.clean);
            } else if (ev.kind === 'tool_call_start') {
                if (firstDelta) { clearThinking(); firstDelta = false; }
                const { verb, target } = describeToolCall(ev.name, ev.args);
                genBar.phase('tool', target ? `${verb} ${target}` : verb);
                const row = appendToolRow(ev.name, ev.args);
                toolRows.set(ev.id, row);
            } else if (ev.kind === 'tool_call_result') {
                const row = toolRows.get(ev.id);
                if (!row) return;
                if (ev.ok) row.done(ev.result);
                else {
                    const msg = formatToolError(ev.result);
                    row.fail(msg);
                    if (ev.name === 'apply_edit' || ev.name === 'create_file') {
                        appendReviewNotice(msg);
                    }
                }
                currentBubble = null;
                genBar.phase('thinking', 'Thinking…');
                ensureThinking('Thinking…');
            } else if (ev.kind === 'budget_warning') {
                showBudgetWarning(ev.tokens, ev.max);
            } else if (ev.kind === 'eviction') {
                showEvictionNotice(ev.result, ev.tokensBefore, ev.tokensAfter, ev.max);
            } else if (ev.kind === 'asset_report') {
                appendAssetReport(ev.present, ev.missing);
            } else if (ev.kind === 'code_lint') {
                appendCodeLint(ev.issues);
            } else if (ev.kind === 'suggestion') {
                appendSuggestions(ev.suggestions);
            } else if (ev.kind === 'turn_complete') {
                void currentBubble?.finalize();
                genBar.finalize('done');
            } else if (ev.kind === 'error') {
                if (firstDelta) { clearThinking(); firstDelta = false; }
                genBar.finalize('error', `Error — ${ev.message.slice(0, 60)}`);
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
            // TS narrows `currentBubble` to `null` here because its
            // initial assignment is `null` and the closure
            // reassignments above aren't visible to control-flow
            // analysis. Cast back to the declared type before
            // dereferencing — at runtime the closure has fired by
            // the time the `finally` runs.
            const bubble = currentBubble as ReturnType<typeof appendAssistantBubble> | null;
            if (bubble) await bubble.finalize();
            clearThinking();
            // Safety net: if the turn ended without a turn_complete/error event
            // (e.g. the user hit Stop), settle the live strip so it doesn't
            // keep pulsing.
            if (!genBarEl.hidden && (genBarEl.dataset.state === 'thinking'
                || genBarEl.dataset.state === 'generating'
                || genBarEl.dataset.state === 'tool')) {
                genBar.finalize('done');
            }
            clearReviewProgress();
            saveChat();
            setGenerating(false);
            inputEl.disabled = false;
            inputEl.focus();
        }
    }

    // ── Slash-command autocomplete ───────────────────────────────────────
    const slashAutocomplete = createSlashAutocomplete({
        input: inputEl,
        popup: slashPopup,
        list: () => slashRegistry.list(),
        submit: () => void handleSend(),
    });
    const closeSlashPopup = () => slashAutocomplete.close();

    sendBtn.addEventListener('click', () => void handleSend());
    inputEl.addEventListener('keydown', (e) => {
        if (slashAutocomplete.handleKeydown(e)) return;
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            void handleSend();
        }
    });
    inputEl.addEventListener('input', () => {
        inputEl.style.height = 'auto';
        inputEl.style.height = `${Math.min(inputEl.scrollHeight, 140)}px`;
        slashAutocomplete.refresh();
    });
    inputEl.addEventListener('blur', () => {
        // Delay so a mousedown on an item still registers.
        setTimeout(() => slashAutocomplete.close(), 120);
    });

    clearBtn.addEventListener('click', () => {
        startNewChat();
    });

    loadBtn.addEventListener('click', () => {
        void loadSelectedProvider();
    });

    // Edit-approval mode toggle (also exposed via /mode).
    function renderModeBtn(): void {
        const auto = getEditMode() === 'auto';
        modeBtn.textContent = auto ? 'Edits: Auto' : 'Edits: Manual';
        modeBtn.dataset.mode = auto ? 'auto' : 'manual';
        modeBtn.title = auto
            ? 'Edits apply automatically. Click (or /mode manual) to review each diff.'
            : 'Edits wait for your approval. Click (or /mode auto) to auto-apply.';
    }
    modeBtn.addEventListener('click', () => {
        setEditMode(getEditMode() === 'auto' ? 'manual' : 'auto');
    });
    editModeListeners.add(renderModeBtn);
    renderModeBtn();

    // Connect: set the entered GhostBot code on the provider, then (re)connect.
    function connectWithEnteredCode(): void {
        const code = ghostBotCodeInput.value.trim().toUpperCase();
        if (!code) { ghostBotHint.textContent = "Enter your GhostBot's code first."; return; }
        // Persist first so a freshly-created provider picks it up.
        localStorage.setItem('fade.ai.ghostbot.code', code);
        const gb = ghostBotProvider();
        if (gb) { gb.setJoinCode(code); void reconnectGhostBot(); }
        else { void loadSelectedProvider(); } // creates the provider w/ stored code + connects
    }

    ghostBotConnectBtn.addEventListener('click', connectWithEnteredCode);
    ghostBotCodeInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') { e.preventDefault(); connectWithEnteredCode(); }
    });

    ghostBotStopBtn.addEventListener('click', () => { void stopGhostBotSession(); });

    // Probe / automation hook — mirrors __fadeRunnerHelpers pattern.
    // Auto-warm on return visits. Weights are cached in IndexedDB by
    // transformers.js — this rebuilds the in-memory session only (seconds,
    // not another multi-GB download). Skip the manual "Load Model" click.
    if (shouldAutoLoadProvider()) {
        void loadSelectedProvider().catch(e =>
            console.warn('[fade/ai] auto-load failed:', e),
        );
    }

    // Fresh mount starts on an empty chat — seed the starter chips so the
    // conversation has somewhere to begin.
    showStarterChips();

    (window as Window & { __fadeAiHelpers?: {
        loadModel(): Promise<void>;
        engineStatus(): EngineStatus;
        providerLabel(): string | null;
        toolRowCount(): number;
        sendMessage(text: string): Promise<void>;
        setGrammarLoop(on: boolean): void;
        grammarLoopEnabled(): boolean;
    } }).__fadeAiHelpers = {
        loadModel: () => loadSelectedProvider(),
        engineStatus: () => engineStatus,
        providerLabel: () => provider?.label ?? null,
        toolRowCount: () => messagesEl.querySelectorAll('.ai-tool-row').length,
        sendMessage: (text: string) => {
            inputEl.value = text;
            return handleSend();
        },
        // Flip the explicit decision-tree loop on/off (vs. the ReAct Agent).
        // Rebuilds the agent so the next message uses the choice.
        setGrammarLoop: (on: boolean) => {
            try { localStorage.setItem('fade.ai.grammarLoop', on ? '1' : '0'); } catch { /* ignore */ }
            if (provider) agent = buildAgent();
        },
        grammarLoopEnabled: () => useGrammarLoop(),
    };
}

function toolIcon(name: string): string {
    switch (name) {
        case 'list_files': return '📂';
        case 'read_file': return '📄';
        case 'apply_edit': return '✏️';
        case 'create_file': return '✨';
        case 'search_docs': return '🔍';
        case 'get_diagnostics': return '🩺';
        default: return '⚙️';
    }
}

/** A human-readable phrase for what a tool call is doing, shown in the chat
 *  so each agent action is self-explanatory ("Reading main.fbasic" rather
 *  than a bare "read_file"). */
function describeToolCall(name: string, args: unknown): { verb: string; target: string } {
    const a = (args ?? {}) as Record<string, unknown>;
    const str = (v: unknown): string => (typeof v === 'string' ? v : '');
    switch (name) {
        case 'list_files':
            return { verb: 'Listing project files', target: '' };
        case 'read_file':
            return { verb: 'Reading', target: str(a.path) };
        case 'apply_edit':
            return { verb: 'Editing', target: str(a.path) };
        case 'create_file':
            return { verb: 'Creating', target: str(a.path) };
        case 'search_docs':
            return { verb: 'Searching docs', target: str(a.query) };
        case 'get_diagnostics':
            return { verb: 'Checking diagnostics', target: str(a.path) };
        default:
            return { verb: name, target: '' };
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
        barPct: HTMLElement;
        codeEl?: HTMLElement;
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

            let codeEl: HTMLElement | undefined;
            if (entry.id === 'ghostbot:local') {
                codeEl = document.createElement('div');
                codeEl.className = 'ai-model-join-code';
                codeEl.textContent = 'Join code appears when you click Load';
                info.append(nameEl, noteEl, codeEl);
            } else {
                info.append(nameEl, noteEl);
            }

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
            const barPct = document.createElement('div');
            barPct.className = 'ai-model-bar-pct';
            barPct.textContent = '0%';
            barWrap.appendChild(barPct);

            row.append(info, right, barWrap);
            list.appendChild(row);

            const state: RowState = {
                id: entry.id, label: entry.label, note: entry.note,
                rowEl: row, statusEl, btnEl, barEl: barWrap, barFill, barPct,
                codeEl,
            };
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
            state.statusEl.title = '';
            state.btnEl.textContent = 'Loaded';
            state.btnEl.disabled = true;
            state.barEl.hidden = true;
        } else if (isLoading) {
            const pctLabel = state.barPct.textContent ?? '0%';
            state.statusEl.textContent = pctLabel === '0%' ? 'Loading…' : `Loading ${pctLabel}`;
            state.statusEl.className = 'ai-model-status ai-model-status-loading';
            state.statusEl.title = '';
            state.btnEl.textContent = 'Loading…';
            state.btnEl.disabled = true;
            state.barEl.hidden = false;
        } else if (engineStatus === 'error' && selected) {
            const detail = engineError ?? 'Load failed';
            state.statusEl.textContent = providerErrorSummary(detail, state.id);
            state.statusEl.className = 'ai-model-status ai-model-status-error';
            state.statusEl.title = detail;
            state.btnEl.textContent = 'Retry';
            state.btnEl.disabled = false;
            state.barEl.hidden = true;
        } else {
            state.statusEl.textContent = selected ? 'Selected' : '';
            state.statusEl.title = '';
            state.btnEl.textContent = selected ? 'Load' : 'Use';
            state.btnEl.disabled = false;
            state.barEl.hidden = true;
        }
    }

    function updateRows(): void {
        for (const state of rows) updateRowState(state);
    }

    statusListeners.add(updateRows);
    progressListeners.add((_text, pct) => {
        const sel = getSelectedProviderId();
        const pctInt = Math.min(100, Math.max(0, Math.round(pct * 100)));
        const pctLabel = `${pctInt}%`;
        for (const state of rows) {
            if (state.id !== sel) continue;
            state.barFill.style.width = pctLabel;
            state.barPct.textContent = pctLabel;
            if (engineStatus === 'loading') {
                state.statusEl.textContent = pctInt > 0 ? `Loading ${pctLabel}` : 'Loading…';
            }
            if (state.codeEl && provider instanceof GhostBotProvider) {
                state.codeEl.textContent = provider.hasJoinCode()
                    ? `GhostBot code: ${provider.getJoinCode()}`
                    : "Enter your GhostBot's code in the chat bar";
            }
        }
    });

    renderRows();
}
