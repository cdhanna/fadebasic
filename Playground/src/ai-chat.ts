// AI Chat panel + Model Manager panel — powered by @mlc-ai/web-llm.
//
// mountAiChat(el, workspace) — wires up the chat panel with agent loop.
// mountAiModels(el)          — wires up the model downloader/selector.
//
// Both panels share module-level engine state so switching models from the
// Models tab immediately affects the next message in Chat.

import { CreateWebWorkerMLCEngine, hasModelInCache } from '@mlc-ai/web-llm';
import type { MLCEngineInterface, ChatCompletionTool, InitProgressReport } from '@mlc-ai/web-llm';

// ─── Types ────────────────────────────────────────────────────────────────────

export interface WorkspaceAdapter {
    list(): Promise<string[]>;
    read(name: string): Promise<string>;
    write(name: string, content: string): Promise<void>;
    currentProject(): string;
}

type MsgRole = 'user' | 'assistant' | 'tool' | 'system';

interface ChatMsg {
    role: MsgRole;
    content: string | null;
    tool_calls?: ToolCall[];
    tool_call_id?: string;
    name?: string;
}

interface ToolCall {
    id: string;
    type: 'function';
    function: { name: string; arguments: string };
}

type EngineStatus = 'idle' | 'loading' | 'ready' | 'error';

// ─── Curated model list ───────────────────────────────────────────────────────

interface ModelMeta { id: string; label: string; sizeMb: number; note?: string; supportsTools?: boolean }

const MODELS: ModelMeta[] = [
    { id: 'Qwen2.5-7B-Instruct-q4f16_1-MLC',      label: 'Qwen 2.5 7B',       sizeMb: 4200, note: 'Recommended' },
    { id: 'Hermes-3-Llama-3.1-8B-q4f16_1-MLC',    label: 'Hermes 3 8B',        sizeMb: 4900, note: 'Best tool use', supportsTools: true },
    { id: 'Hermes-2-Pro-Llama-3-8B-q4f16_1-MLC',  label: 'Hermes 2 Pro 8B',    sizeMb: 4900, note: 'Tool use',      supportsTools: true },
    { id: 'Llama-3.1-8B-Instruct-q4f16_1-MLC',    label: 'Llama 3.1 8B',       sizeMb: 4900 },
    { id: 'Qwen2.5-3B-Instruct-q4f16_1-MLC',      label: 'Qwen 2.5 3B',        sizeMb: 2100, note: 'Faster' },
    { id: 'Phi-3.5-mini-instruct-q4f16_1-MLC',    label: 'Phi 3.5 Mini 3.8B',  sizeMb: 2200, note: 'Fast' },
];

// ─── Module-level engine state ────────────────────────────────────────────────

const AI_MODEL_KEY = 'fade.ai.selectedModel';

let engine: MLCEngineInterface | null = null;
(window as unknown as Record<string,unknown>)['ai]'] = engine
let engineModelId: string | null = null;
let engineStatus: EngineStatus = 'idle';
const statusListeners = new Set<(s: EngineStatus, detail?: string) => void>();
const progressListeners = new Set<(text: string, pct: number) => void>();

function notifyStatus(s: EngineStatus, detail?: string) {
    engineStatus = s;
    for (const fn of statusListeners) fn(s, detail);
}

function notifyProgress(text: string, pct: number) {
    for (const fn of progressListeners) fn(text, pct);
}

export function getSelectedModelId(): string {
    return localStorage.getItem(AI_MODEL_KEY) ?? MODELS[0].id;
}

export function setSelectedModelId(id: string) {
    localStorage.setItem(AI_MODEL_KEY, id);
}

export async function loadModel(modelId: string): Promise<void> {
    if (engine && engineModelId === modelId && engineStatus === 'ready') return;
    if (engineStatus === 'loading') return;

    engine = null;
    engineModelId = null;
    notifyStatus('loading', modelId);

    try {
        const worker = new Worker(new URL('./ai-worker.ts', import.meta.url), { type: 'module' });
        engine = await CreateWebWorkerMLCEngine(worker, modelId, {
            initProgressCallback: (report: InitProgressReport) => {
                notifyProgress(report.text, report.progress);
            },
        });
        engineModelId = modelId;
        setSelectedModelId(modelId);
        notifyStatus('ready');
    } catch (err) {
        notifyStatus('error', (err as Error).message ?? String(err));
        throw err;
    }
}

// ─── Tool definitions ─────────────────────────────────────────────────────────

const TOOLS: ChatCompletionTool[] = [
    {
        type: 'function',
        function: {
            name: 'list_files',
            description: 'List all files in the current workspace project.',
            parameters: { type: 'object', properties: {}, required: [] },
        },
    },
    {
        type: 'function',
        function: {
            name: 'read_file',
            description: 'Read the full text of a workspace file.',
            parameters: {
                type: 'object',
                properties: {
                    path: { type: 'string', description: 'Filename to read' },
                },
                required: ['path'],
            },
        },
    },
    {
        type: 'function',
        function: {
            name: 'write_file',
            description: 'Propose creating or overwriting a file. The user must approve the diff before it is saved.',
            parameters: {
                type: 'object',
                properties: {
                    path: { type: 'string', description: 'Filename to write' },
                    content: { type: 'string', description: 'Complete new file content' },
                },
                required: ['path', 'content'],
            },
        },
    },
];

const SYSTEM_PROMPT = `You are a terse coding assistant in a code editor. Be direct and pragmatic — no filler, no over-explanation. Only use file tools when the user explicitly asks.`;

const TOOL_GUIDANCE_MSG = `[System note: You are a terse coding assistant with file access. Be direct and pragmatic — skip preamble, skip summaries of what you just did, no over-explanation. Use list_files, read_file, and write_file proactively when helping with code. Show results, not narration.]`;

// ─── Chat persistence ─────────────────────────────────────────────────────────

interface ChatRecord {
    id: string;
    title: string;
    createdAt: string;
    updatedAt: string;
    modelId: string | null;
    messages: ChatMsg[];
}

type ChatSummary = Omit<ChatRecord, 'messages'>;

function newChatId(): string {
    return Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
}

function deriveChatTitle(messages: ChatMsg[]): string {
    const first = messages.find(m => m.role === 'user' && typeof m.content === 'string')?.content as string | undefined;
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
                out.push({ id: rec.id, title: rec.title, createdAt: rec.createdAt,
                           updatedAt: rec.updatedAt, modelId: rec.modelId });
            } catch { /* skip corrupted */ }
        }
        return out.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
    }

    async delete(id: string): Promise<void> {
        try { await this.dir.removeEntry(`${id}.json`); } catch { /* ignore */ }
    }
}

// ─── Line diff ────────────────────────────────────────────────────────────────

type DiffLine = { type: 'same' | 'add' | 'remove'; text: string };

function lineDiff(oldText: string, newText: string): DiffLine[] {
    const a = oldText ? oldText.split('\n') : [];
    const b = newText ? newText.split('\n') : [];
    const m = a.length, n = b.length;
    const dp: number[][] = Array.from({ length: m + 1 }, () => new Array(n + 1).fill(0));
    for (let i = m - 1; i >= 0; i--)
        for (let j = n - 1; j >= 0; j--)
            dp[i][j] = a[i] === b[j] ? 1 + dp[i + 1][j + 1] : Math.max(dp[i + 1][j], dp[i][j + 1]);

    const out: DiffLine[] = [];
    let i = 0, j = 0;
    while (i < m || j < n) {
        if (i < m && j < n && a[i] === b[j]) {
            out.push({ type: 'same', text: a[i] }); i++; j++;
        } else if (j < n && (i >= m || dp[i + 1][j] >= dp[i][j + 1])) {
            out.push({ type: 'add', text: b[j] }); j++;
        } else {
            out.push({ type: 'remove', text: a[i] }); i++;
        }
    }
    return out;
}

// ─── JSON formatting + highlighting ──────────────────────────────────────────

function escapeHtml(s: string): string {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function formatJson(raw: string): string {
    try { return JSON.stringify(JSON.parse(raw), null, 2); } catch { return raw; }
}

function highlightJson(json: string): string {
    // Escape HTML first, then wrap tokens in <span> elements.
    const escaped = escapeHtml(json);
    return escaped.replace(
        /("(\\u[0-9a-fA-F]{4}|\\[^u]|[^\\"])*"(\s*:)?|\b(true|false|null)\b|-?\d+(?:\.\d*)?(?:[eE][+\-]?\d+)?)/g,
        (match) => {
            let cls = 'json-n';  // number
            if (match.startsWith('"')) {
                cls = match.trimEnd().endsWith(':') ? 'json-k' : 'json-s';
            } else if (match === 'true' || match === 'false') {
                cls = 'json-b';
            } else if (match === 'null') {
                cls = 'json-null';
            }
            return `<span class="${cls}">${match}</span>`;
        },
    );
}

function renderDiff(diff: DiffLine[]): HTMLElement {
    const pre = document.createElement('pre');
    pre.className = 'ai-diff';
    for (const line of diff) {
        const span = document.createElement('span');
        span.className = `ai-diff-line ai-diff-${line.type}`;
        const prefix = line.type === 'add' ? '+ ' : line.type === 'remove' ? '- ' : '  ';
        span.textContent = prefix + line.text;
        pre.appendChild(span);
        pre.appendChild(document.createTextNode('\n'));
    }
    return pre;
}

// ─── Chat panel ───────────────────────────────────────────────────────────────

const MAX_AGENT_ITERATIONS = 8;

export function mountAiChat(container: HTMLElement, workspace: WorkspaceAdapter): void {
    // ── DOM refs ────────────────────────────────────────────────────────────
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

    // ── State ───────────────────────────────────────────────────────────────
    let history: ChatMsg[] = [];
    let activeChatId: string = newChatId();
    let generating = false;

    // ── Chat store ──────────────────────────────────────────────────────────
    const store = new ChatStore();
    void store.init().catch(e => console.warn('[fade/ai] chat store init failed:', e));

    function saveChat(): void {
        if (history.length === 0) return;
        const record: ChatRecord = {
            id: activeChatId,
            title: deriveChatTitle(history),
            createdAt: activeChatId,   // id encodes creation time
            updatedAt: new Date().toISOString(),
            modelId: engineModelId,
            messages: history,
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
        history = [];
        activeChatId = newChatId();
        messagesEl.innerHTML = '';
        console.log('[fade/ai] new chat, id:', activeChatId);
    }

    async function loadChat(id: string): Promise<void> {
        const record = await store.load(id);
        if (!record) return;
        history = record.messages;
        activeChatId = id;
        renderHistoryToDOM(record.messages);
        console.log('[fade/ai] loaded chat:', id, '—', record.messages.length, 'messages');
    }

    function renderHistoryToDOM(msgs: ChatMsg[]): void {
        messagesEl.innerHTML = '';
        for (const msg of msgs) {
            if (msg.role === 'user' && msg.content) {
                appendUserBubble(msg.content as string);
            } else if (msg.role === 'assistant' && msg.content) {
                const b = appendAssistantBubble();
                b.setText(msg.content as string);
            } else if (msg.role === 'assistant' && msg.tool_calls?.length) {
                const div = document.createElement('div');
                div.className = 'ai-tool-row';
                const hdr = document.createElement('div');
                hdr.className = 'ai-tool-header';
                hdr.style.cursor = 'default';
                hdr.innerHTML = `<span class="ai-tool-icon">⚙️</span><span class="ai-tool-label">${msg.tool_calls.map(tc => tc.function.name).join(', ')}</span><span class="ai-tool-badge" style="color:var(--fg-muted);font-size:0.68rem">restored</span>`;
                div.appendChild(hdr);
                messagesEl.appendChild(div);
            }
        }
        scrollToBottom();
    }


    stopBtn.addEventListener('click', () => {
        if (!generating || !engine) return;
        console.log('[fade/ai] user requested stop');
        void (engine as any).interruptGenerate?.();
    });

    function setGenerating(on: boolean) {
        generating = on;
        sendBtn.hidden = on;
        stopBtn.hidden = !on;
    }
    setGenerating(false);

    // ── Status rendering ────────────────────────────────────────────────────
    function renderStatus() {
        const modelLabel = MODELS.find(m => m.id === engineModelId)?.label ?? engineModelId ?? '—';
        if (engineStatus === 'ready') {
            statusEl.textContent = modelLabel;
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
            statusEl.textContent = 'Error';
            statusEl.className = 'ai-chat-status ai-status-error';
            loadBtn.hidden = false;
            loadBtn.textContent = 'Retry';
            sendBtn.disabled = true;
            inputEl.disabled = true;
            progressRow.hidden = true;
        } else {
            // idle
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

    function showThinking(): () => void {
        const div = document.createElement('div');
        div.className = 'ai-thinking';
        div.innerHTML = '<span></span><span></span><span></span>';
        messagesEl.appendChild(div);
        scrollToBottom();
        return () => div.remove();
    }

    function appendAssistantBubble(): { setText(t: string): void; el: HTMLElement } {
        const div = document.createElement('div');
        div.className = 'ai-msg ai-msg-assistant';
        messagesEl.appendChild(div);
        scrollToBottom();
        return {
            setText(t: string) {
                div.textContent = t;
                scrollToBottom();
            },
            el: div,
        };
    }

    function appendToolRow(icon: string, label: string): {
        done(resultJson: string): void;
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
            done(resultJson: string) {
                badge.className = 'ai-tool-badge ai-tool-badge-done';
                badge.textContent = '✓';
                const pre = document.createElement('pre');
                pre.className = 'ai-tool-json';
                pre.innerHTML = highlightJson(formatJson(resultJson));
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

    function appendDiffApproval(
        path: string,
        oldContent: string,
        newContent: string,
        onApply: () => void,
        onReject: () => void,
    ): void {
        const wrapper = document.createElement('div');
        wrapper.className = 'ai-diff-wrapper';

        const header = document.createElement('div');
        header.className = 'ai-diff-header';
        header.textContent = `Proposed edit: ${path}`;
        wrapper.appendChild(header);

        const diff = lineDiff(oldContent, newContent);
        wrapper.appendChild(renderDiff(diff));

        const actions = document.createElement('div');
        actions.className = 'ai-diff-actions';

        const applyBtn = document.createElement('button');
        applyBtn.className = 'ai-diff-apply';
        applyBtn.textContent = 'Apply';
        applyBtn.addEventListener('click', () => {
            wrapper.classList.add('ai-diff-accepted');
            applyBtn.disabled = true;
            rejectBtn.disabled = true;
            console.log('[fade/ai] write_file approved:', path);
            onApply();
        });

        const rejectBtn = document.createElement('button');
        rejectBtn.className = 'ai-diff-reject';
        rejectBtn.textContent = 'Reject';
        rejectBtn.addEventListener('click', () => {
            wrapper.classList.add('ai-diff-rejected');
            applyBtn.disabled = true;
            rejectBtn.disabled = true;
            console.log('[fade/ai] write_file rejected:', path);
            onReject();
        });

        actions.appendChild(applyBtn);
        actions.appendChild(rejectBtn);
        wrapper.appendChild(actions);

        const hint = document.createElement('div');
        hint.className = 'ai-approval-hint';
        hint.textContent = 'Review the diff above, then Apply or Reject to continue.';
        messagesEl.appendChild(wrapper);
        messagesEl.appendChild(hint);
        scrollToBottom();
    }

    function scrollToBottom(): void {
        messagesEl.scrollTop = messagesEl.scrollHeight;
    }

    // ── Tool execution ──────────────────────────────────────────────────────
    async function executeTool(tc: ToolCall): Promise<string> {
        let args: Record<string, unknown> = {};
        try {
            args = tc.function.arguments ? JSON.parse(tc.function.arguments) : {};
        } catch {
            console.warn('[fade/ai] could not parse tool arguments:', tc.function.arguments);
            return JSON.stringify({ error: 'Could not parse tool arguments — invalid JSON.' });
        }
        console.log('[fade/ai] tool call:', tc.function.name, args);

        // Validate required args before touching the UI or workspace.
        const REQUIRED: Record<string, string[]> = {
            read_file:  ['path'],
            write_file: ['path', 'content'],
        };
        const missing = (REQUIRED[tc.function.name] ?? []).filter(k => args[k] == null || args[k] === '');
        if (missing.length > 0) {
            const msg = `Missing required argument(s): ${missing.join(', ')}`;
            console.warn('[fade/ai] tool arg validation failed:', tc.function.name, msg);
            const row = appendToolRow('⚠️', tc.function.name);
            row.fail(msg);
            return JSON.stringify({ error: msg });
        }

        switch (tc.function.name) {
            case 'list_files': {
                const row = appendToolRow('📂', 'list_files');
                const files = await workspace.list();
                const result = { files, project: workspace.currentProject() };
                const json = JSON.stringify(result);
                row.done(json);
                console.log('[fade/ai] list_files result:', result);
                return json;
            }
            case 'read_file': {
                const path = args.path as string;
                const row = appendToolRow('📄', `read_file  ${path}`);
                try {
                    const content = await workspace.read(path);
                    const result = { path, content };
                    const json = JSON.stringify(result);
                    row.done(json);
                    console.log('[fade/ai] read_file: path=%s, %d chars', path, content.length);
                    return json;
                } catch {
                    const result = { error: `File not found: ${path}` };
                    const json = JSON.stringify(result);
                    row.fail(`File not found: ${path}`);
                    console.warn('[fade/ai] read_file: not found:', path);
                    return json;
                }
            }
            case 'write_file': {
                const path = args.path as string;
                const newContent = args.content as string;
                let oldContent = '';
                try { oldContent = await workspace.read(path); } catch { /* new file */ }
                const isNew = oldContent === '';
                console.log('[fade/ai] write_file: proposing %s (%s, %d chars)',
                    path, isNew ? 'new file' : 'edit', newContent.length);
                const row = appendToolRow('✏️', `write_file  ${path}`);
                return new Promise<string>(resolve => {
                    appendDiffApproval(
                        path, oldContent, newContent,
                        async () => {
                            await workspace.write(path, newContent);
                            const result = { success: true, path };
                            const json = JSON.stringify(result);
                            row.done(json);
                            console.log('[fade/ai] write_file applied:', path);
                            resolve(json);
                        },
                        () => {
                            const result = { success: false, reason: 'User rejected the change.' };
                            const json = JSON.stringify(result);
                            row.done(json);
                            console.log('[fade/ai] write_file rejected:', path);
                            resolve(json);
                        },
                    );
                });
            }
            default:
                console.warn('[fade/ai] unknown tool:', tc.function.name);
                return JSON.stringify({ error: `Unknown tool: ${tc.function.name}` });
        }
    }

    // ── Agent loop ──────────────────────────────────────────────────────────
    async function runLoop(userText: string): Promise<void> {
        if (!engine) return;

        setGenerating(true);
        inputEl.disabled = true;

        history.push({ role: 'user', content: userText });

        console.log('[fade/ai] starting agent loop, model:', engineModelId,
            'history:', history.length, 'msgs');

        const modelMeta = MODELS.find(m => m.id === engineModelId);
        const useTools = modelMeta?.supportsTools === true;

        // Hermes models bake a system prompt into their tool-calling template and
        // reject any custom one (CustomSystemPromptError). Skip the system prompt
        // upfront for tool-capable models; use a user-role note instead.
        // Non-tool models (Qwen, Phi, etc.) accept a system prompt normally.
        let includeSystemMsg = !useTools;

        const TOOL_GUIDANCE: ChatMsg = { role: 'user', content: TOOL_GUIDANCE_MSG };

        const buildMsgs = (): ChatMsg[] => {
            if (includeSystemMsg) return [{ role: 'system', content: SYSTEM_PROMPT }, ...history];
            return useTools ? [TOOL_GUIDANCE, ...history] : history;
        };

        const msgs = buildMsgs();

        // In agent mode, prime the first user message with real workspace context.
        // We enrich the user message text rather than injecting fake tool-call
        // messages into history — injecting assistant tool_calls confuses WebLLM's
        // Hermes parser, which then expects every subsequent assistant turn to also
        // be a tool call and throws ToolCallOutputParseError on plain-text replies.
        if (useTools && history.filter(m => m.role === 'user').length === 1) {
            try {
                const files = await workspace.list();
                const listResult = JSON.stringify({ files, project: workspace.currentProject() });
                const row = appendToolRow('📂', 'list_files');
                row.done(listResult);
                console.log('[fade/ai] agent primed with file list:', files);
                const firstUserIdx = msgs.findIndex(m => m.role === 'user');
                if (firstUserIdx >= 0) {
                    const orig = msgs[firstUserIdx].content as string;
                    msgs[firstUserIdx] = {
                        ...msgs[firstUserIdx],
                        content: `[Workspace: project="${workspace.currentProject()}", files: ${files.join(', ')}]\n\n${orig}`,
                    };
                }
            } catch (e) {
                console.warn('[fade/ai] auto list_files failed:', e);
            }
        }

        const hideThinking = showThinking();

        try {
            let iteration = 0;
            while (true) {
                iteration++;
                console.log('[fade/ai] iteration %d — sending %d messages to model', iteration, msgs.length);

                const bubble = appendAssistantBubble();
                let textAcc = '';
                const tcAcc: ToolCall[] = [];
                let finishReason: string | null = null;
                let firstToken = false;
                let stream: any;

                const createOpts: any = {
                    messages: msgs,
                    stream: true,
                    temperature: 0.6,
                };
                if (useTools) {
                    createOpts.tools = TOOLS;
                    createOpts.tool_choice = 'auto';
                } else if (!useTools && iteration === 1 && modelMeta) {
                    // Model loaded but doesn't support tools — warn once per run.
                    const noteDiv = document.createElement('div');
                    noteDiv.className = 'ai-msg ai-msg-error';
                    noteDiv.textContent = `${modelMeta.label} doesn't support tool calling. Switch to a Hermes model to use file tools.`;
                    messagesEl.appendChild(noteDiv);
                    console.warn('[fade/ai] model lacks tool support:', engineModelId);
                }
                try {
                    stream = await engine.chat.completions.create(createOpts);
                } catch (e) {
                    if (String(e).includes('CustomSystemPromptError') || (e as any)?.name === 'CustomSystemPromptError') {
                        console.warn('[fade/ai] CustomSystemPromptError — retrying without system message');
                        includeSystemMsg = false;
                        msgs.splice(0, msgs.length, ...buildMsgs());
                        stream = await engine.chat.completions.create(createOpts);
                    } else if (String(e).includes('UnsupportedModelIdError')) {
                        console.warn('[fade/ai] UnsupportedModelIdError — model does not support tools, retrying without');
                        delete createOpts.tools;
                        delete createOpts.tool_choice;
                        stream = await engine.chat.completions.create(createOpts);
                    } else {
                        throw e;
                    }
                }

                try {
                    for await (const chunk of stream) {
                        if (!firstToken) {
                            hideThinking();
                            firstToken = true;
                        }
                        const choice = chunk.choices[0];
                        if (!choice) continue;
                        finishReason = choice.finish_reason;

                        const delta = choice.delta;
                        if (delta.content) {
                            textAcc += delta.content;
                            bubble.setText(textAcc);
                        }

                        if ((delta as any).tool_calls) {
                            for (const tc of (delta as any).tool_calls) {
                                while (tcAcc.length <= tc.index) tcAcc.push({ id: '', type: 'function', function: { name: '', arguments: '' } });
                                if (tc.id) tcAcc[tc.index].id += tc.id;
                                if (tc.function?.name) tcAcc[tc.index].function.name += tc.function.name;
                                if (tc.function?.arguments) tcAcc[tc.index].function.arguments += tc.function.arguments;
                            }
                        }
                    }
                } catch (streamErr) {
                    // WebLLM's Hermes parser throws ToolCallOutputParseError when the
                    // model replies with plain text in a tool-calling context. The actual
                    // response is embedded in the error message — extract and display it.
                    if (String(streamErr).includes('ToolCallOutputParseError')) {
                        const raw = (streamErr as Error).message ?? String(streamErr);
                        const match = raw.match(/Got outputMessage:\s*([\s\S]*?)(?:\nGot error:|$)/);
                        const extracted = (match?.[1] ?? '').trim();
                        if (!firstToken) { hideThinking(); firstToken = true; }
                        if (extracted) { textAcc = extracted; bubble.setText(extracted); }
                        finishReason = 'stop';
                        console.warn('[fade/ai] ToolCallOutputParseError — extracted response (%d chars)', extracted.length);
                    } else {
                        throw streamErr;
                    }
                }

                console.log('[fade/ai] stream done — finish_reason=%s text=%d chars tool_calls=%d',
                    finishReason, textAcc.length, tcAcc.length);

                // Remove the empty placeholder bubble if the model only called tools
                if (!textAcc && tcAcc.length > 0) {
                    bubble.el.remove();
                }

                const assistantMsg: ChatMsg = { role: 'assistant', content: textAcc || '' };
                if (tcAcc.length > 0) assistantMsg.tool_calls = tcAcc;
                history.push(assistantMsg);
                msgs.push(assistantMsg);

                if (!tcAcc.length || finishReason === 'stop') {
                    console.log('[fade/ai] agent loop complete after %d iteration(s)', iteration);
                    break;
                }

                if (iteration >= MAX_AGENT_ITERATIONS) {
                    console.warn('[fade/ai] hit max iterations (%d) — stopping to avoid loop', MAX_AGENT_ITERATIONS);
                    const warnDiv = document.createElement('div');
                    warnDiv.className = 'ai-msg ai-msg-error';
                    warnDiv.textContent = `Stopped after ${MAX_AGENT_ITERATIONS} iterations. The model may be looping — try rephrasing or disabling agent mode.`;
                    messagesEl.appendChild(warnDiv);
                    break;
                }

                // Execute tool calls in parallel (write_file still pauses per-call
                // for diff approval, but list_files / read_file can overlap).
                console.log('[fade/ai] executing %d tool call(s) in parallel', tcAcc.length);
                const toolResults = await Promise.all(tcAcc.map(tc => executeTool(tc)));
                for (let i = 0; i < tcAcc.length; i++) {
                    console.log('[fade/ai] tool result for', tcAcc[i].function.name, ':', toolResults[i].slice(0, 120));
                    const toolMsg: ChatMsg = {
                        role: 'tool',
                        tool_call_id: tcAcc[i].id,
                        content: toolResults[i],
                    };
                    history.push(toolMsg);
                    msgs.push(toolMsg);
                }
            }
        } catch (err) {
            hideThinking();
            console.error('[fade/ai] agent loop error:', err);
            const errDiv = document.createElement('div');
            errDiv.className = 'ai-msg ai-msg-error';
            errDiv.textContent = `Error: ${(err as Error).message ?? String(err)}`;
            messagesEl.appendChild(errDiv);
        }

        saveChat();
        setGenerating(false);
        inputEl.disabled = false;
        inputEl.focus();
    }

    // ── Send handler ────────────────────────────────────────────────────────
    function handleSend(): void {
        const text = inputEl.value.trim();
        if (!text || engineStatus !== 'ready' || generating) return;
        inputEl.value = '';
        inputEl.style.height = 'auto';
        appendUserBubble(text);
        void runLoop(text);
    }

    sendBtn.addEventListener('click', handleSend);
    inputEl.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            handleSend();
        }
    });
    inputEl.addEventListener('input', () => {
        inputEl.style.height = 'auto';
        inputEl.style.height = `${Math.min(inputEl.scrollHeight, 140)}px`;
    });

    clearBtn.addEventListener('click', () => {
        history = [];
        messagesEl.innerHTML = '';
    });

    loadBtn.addEventListener('click', () => {
        const modelId = getSelectedModelId();
        void loadModel(modelId);
    });

    // Auto-load if a model was previously selected and is cached
    void (async () => {
        const modelId = getSelectedModelId();
        try {
            const cached = await hasModelInCache(modelId);
            if (cached) void loadModel(modelId);
        } catch { /* hasModelInCache may not be available in all builds */ }
    })();
}

// ─── Models panel ─────────────────────────────────────────────────────────────

async function evictModelFromCache(modelId: string): Promise<number> {
    let count = 0;
    try {
        const names = await caches.keys();
        for (const name of names) {
            const cache = await caches.open(name);
            for (const req of await cache.keys()) {
                if (req.url.includes(modelId)) {
                    await cache.delete(req);
                    count++;
                }
            }
        }
    } catch (e) {
        console.error('[fade/ai] evictModelFromCache error:', e);
    }
    console.log('[fade/ai] evicted %d cache entries for model:', count, modelId);
    return count;
}

export function mountAiModels(container: HTMLElement): void {
    const list = container.querySelector<HTMLElement>('.ai-models-list')!;

    interface RowState {
        meta: ModelMeta;
        rowEl: HTMLElement;
        statusEl: HTMLElement;
        btnEl: HTMLButtonElement;
        delEl: HTMLButtonElement;
        barEl: HTMLElement;
        barFill: HTMLElement;
    }
    const rows: RowState[] = [];

    function renderRows(): void {
        list.innerHTML = '';
        rows.length = 0;

        for (const meta of MODELS) {
            const row = document.createElement('div');
            row.className = 'ai-model-row';

            const info = document.createElement('div');
            info.className = 'ai-model-info';

            const nameEl = document.createElement('div');
            nameEl.className = 'ai-model-name';
            nameEl.textContent = meta.label;

            const noteEl = document.createElement('div');
            noteEl.className = 'ai-model-note';
            const tags = [meta.note, meta.supportsTools ? '🔧 tools' : null, `~${Math.round(meta.sizeMb / 100) / 10} GB`].filter(Boolean);
            noteEl.textContent = tags.join(' · ');

            info.appendChild(nameEl);
            info.appendChild(noteEl);

            const right = document.createElement('div');
            right.className = 'ai-model-right';

            const statusEl = document.createElement('span');
            statusEl.className = 'ai-model-status';

            const btnEl = document.createElement('button');
            btnEl.className = 'ai-model-btn';

            const delEl = document.createElement('button');
            delEl.className = 'ai-model-del';
            delEl.title = 'Remove from cache';
            delEl.textContent = '🗑';
            delEl.hidden = true;

            const barWrap = document.createElement('div');
            barWrap.className = 'ai-model-bar';
            barWrap.hidden = true;
            const barFill = document.createElement('div');
            barFill.className = 'ai-model-bar-fill';
            barWrap.appendChild(barFill);

            right.appendChild(statusEl);
            right.appendChild(btnEl);
            right.appendChild(delEl);
            row.appendChild(info);
            row.appendChild(right);
            row.appendChild(barWrap);
            list.appendChild(row);

            const state: RowState = { meta, rowEl: row, statusEl, btnEl, delEl, barEl: barWrap, barFill };
            rows.push(state);

            btnEl.addEventListener('click', async () => {
                if (engineStatus === 'loading') return;
                setSelectedModelId(meta.id);
                updateRows();
                try {
                    await loadModel(meta.id);
                } catch { /* error shown via status listener */ }
            });

            delEl.addEventListener('click', async () => {
                if (!confirm(`Remove "${meta.label}" from browser cache?\n\nYou'll need to re-download it (~${Math.round(meta.sizeMb / 100) / 10} GB) to use it again.`)) return;
                delEl.disabled = true;
                delEl.textContent = '…';
                const n = await evictModelFromCache(meta.id);
                if (n === 0) {
                    console.warn('[fade/ai] no cache entries found for', meta.id);
                }
                state.rowEl.dataset.cached = '0';
                updateRowState(state);
            });

            // Check cache status asynchronously
            void hasModelInCache(meta.id).then(cached => {
                state.rowEl.dataset.cached = cached ? '1' : '0';
                updateRowState(state);
            }).catch(() => { /* not supported */ });

            updateRowState(state);
        }
    }

    function updateRowState(state: RowState): void {
        const isLoading = engineStatus === 'loading' && getSelectedModelId() === state.meta.id;
        const isReady = engineStatus === 'ready' && engineModelId === state.meta.id;
        const isCached = state.rowEl.dataset.cached === '1';

        state.rowEl.classList.toggle('ai-model-active', isReady);

        if (isReady) {
            state.statusEl.textContent = 'Active';
            state.statusEl.className = 'ai-model-status ai-model-status-ready';
            state.btnEl.textContent = 'Loaded';
            state.btnEl.disabled = true;
            state.delEl.hidden = true;
        } else if (isLoading) {
            state.statusEl.textContent = 'Loading…';
            state.statusEl.className = 'ai-model-status ai-model-status-loading';
            state.btnEl.textContent = 'Loading…';
            state.btnEl.disabled = true;
            state.barEl.hidden = false;
            state.delEl.hidden = true;
        } else if (isCached) {
            state.statusEl.textContent = 'Cached';
            state.statusEl.className = 'ai-model-status ai-model-status-cached';
            state.btnEl.textContent = 'Load';
            state.btnEl.disabled = false;
            state.barEl.hidden = true;
            state.delEl.hidden = false;
            state.delEl.disabled = false;
            state.delEl.textContent = '🗑';
        } else {
            state.statusEl.textContent = '';
            state.btnEl.textContent = 'Download & Load';
            state.btnEl.disabled = false;
            state.barEl.hidden = true;
            state.delEl.hidden = true;
        }
    }

    function updateRows(): void {
        for (const state of rows) updateRowState(state);
    }

    statusListeners.add(updateRows);
    progressListeners.add((_, pct) => {
        for (const state of rows) {
            if (getSelectedModelId() === state.meta.id) {
                state.barFill.style.width = `${Math.round(pct * 100)}%`;
            }
        }
    });

    renderRows();
}
