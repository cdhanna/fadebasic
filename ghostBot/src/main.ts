import { createGhostHub, type GhostConnection } from './session';
import { installIceMonitor } from './ice-monitor';
import { formatChatPrompt, type GhostStreamEvent } from './protocol';
import { isTauriApp, tauriInvoke, tauriListen } from './tauri-bridge';
import {
    formatModelStatus,
    hasUsableLocalModel,
    pickDefaultDownloadModel,
    type DownloadableModel,
} from './models';
import { formatEta } from './wizard';
import codiconsUrl from '@vscode/codicons/dist/codicon.css?url';

interface SetupState {
    models_dir: string;
    model_count: number;
    loaded_model: string | null;
    recommended_id: string;
    downloadable: DownloadableModel[];
}

interface ModelEntry {
    id: string;
    label: string;
    path: string;
    size_mb: number;
    incomplete: boolean;
}

interface DownloadProgress {
    pct: number;
    downloadedMb: number;
    totalMb: number;
    speedMbps: number;
    etaSec: number;
    phase: string;
    label?: string;
    text: string;
}

// ─── DOM ───────────────────────────────────────────────────────────────────
const browserBlocker = document.getElementById('browser-blocker')!;
const appRoot = document.getElementById('app-root')!;
const appBadge = document.getElementById('app-badge')!;

const activityItems = [1, 2, 3].map(n => document.getElementById(`act-step-${n}`)!);
const tabItems = [1, 2, 3].map(n => document.getElementById(`tab-step-${n}`)!);
const stepPanels = [1, 2, 3].map(n => document.getElementById(`step-${n}`)!);

const modelCatalogEl = document.getElementById('model-catalog')!;
const modelsDirHint = document.getElementById('models-dir-hint')!;
const openModelsFolderBtn = document.getElementById('open-models-folder-btn')!;
const openModelsFolderBtn2 = document.getElementById('open-models-folder-btn-2')!;

const downloadStatusText = document.getElementById('download-status-text')!;
const downloadBtn = document.getElementById('download-btn') as HTMLButtonElement;
const redownloadBtn = document.getElementById('redownload-btn') as HTMLButtonElement;
const skipDownloadBtn = document.getElementById('skip-download-btn') as HTMLButtonElement;
const downloadIdle = document.getElementById('download-idle')!;
const downloadActive = document.getElementById('download-active')!;
const downloadDone = document.getElementById('download-done')!;
const downloadDoneDetail = document.getElementById('download-done-detail')!;
const dlPct = document.getElementById('dl-pct')!;
const dlLabel = document.getElementById('dl-label')!;
const dlMb = document.getElementById('dl-mb')!;
const dlBar = document.getElementById('dl-bar')!;
const dlSpeed = document.getElementById('dl-speed')!;
const dlEta = document.getElementById('dl-eta')!;
const dlPhase = document.getElementById('dl-phase')!;
const gotoStep2 = document.getElementById('goto-step-2') as HTMLButtonElement;

const modelSelect = document.getElementById('model-select') as HTMLSelectElement;
const loadModelBtn = document.getElementById('load-model-btn') as HTMLButtonElement;
const unloadModelBtn = document.getElementById('unload-model-btn') as HTMLButtonElement;
const loadStatus = document.getElementById('load-status')!;
const loadDetail = document.getElementById('load-detail')!;
const loadDone = document.getElementById('load-done')!;
const gotoStep3 = document.getElementById('goto-step-3') as HTMLButtonElement;

// Claude API proxy config
const claudeModelSelect = document.getElementById('claude-model-select') as HTMLSelectElement;
const claudeKeyInput = document.getElementById('claude-key-input') as HTMLInputElement;
const claudeSaveBtn = document.getElementById('claude-save-btn') as HTMLButtonElement;
const claudeActivateBtn = document.getElementById('claude-activate-btn') as HTMLButtonElement;
const claudeDeactivateBtn = document.getElementById('claude-deactivate-btn') as HTMLButtonElement;
const claudeStatus = document.getElementById('claude-status')!;
const claudeActivePill = document.getElementById('claude-active-pill')!;

const ghostCodeEl = document.getElementById('ghost-code')!;
const copyCodeBtn = document.getElementById('copy-code-btn') as HTMLButtonElement;
const connectionsEl = document.getElementById('connections')!;
const sessionStatusEl = document.getElementById('session-status')!;
const sessionDetailEl = document.getElementById('session-detail')!;

const activityCard = document.getElementById('activity-card')!;
const activityDot = document.getElementById('activity-dot')!;
const activityLabel = document.getElementById('activity-label')!;
const activityCount = document.getElementById('activity-count')!;
const activityDetail = document.getElementById('activity-detail')!;
const activityPreview = document.getElementById('activity-preview')!;

const logEl = document.getElementById('log')!;
const clearLogBtn = document.getElementById('clear-log-btn') as HTMLButtonElement;
const statusBarText = document.getElementById('status-bar-text')!;
const statusBarModels = document.getElementById('status-bar-models')!;

// ─── State ─────────────────────────────────────────────────────────────────
const CODE_STORAGE_KEY = 'ghostbot.code';

let hub: ReturnType<typeof createGhostHub> | null = null;
let currentWizardStep = 1;
let modelDownloaded = false;
let modelLoaded = false;
let loadedModelName: string | null = null;

// Which provider serves inference: the local GGUF or the Claude API proxy.
type Provider = 'local' | 'claude';
let activeProvider: Provider = 'local';
let claudeModel = 'claude-sonnet-4-6';
let claudeHasKey = false;

// The Claude models offered in the dropdown. Kept here (not only in Rust) so
// the options are present immediately — the select must never be empty while
// the async config load is in flight.
const CLAUDE_MODELS: { id: string; label: string }[] = [
    { id: 'claude-opus-4-8', label: 'Claude Opus 4.8 — most capable' },
    { id: 'claude-sonnet-4-6', label: 'Claude Sonnet 4.6 — fast & balanced' },
    { id: 'claude-haiku-4-5-20251001', label: 'Claude Haiku 4.5 — fastest' },
];

/** This GhostBot's stable shareable code — its "address". Generated once,
 *  persisted, displayed; Playgrounds enter it to reach this app. */
function getOrCreateCode(): string {
    let code = localStorage.getItem(CODE_STORAGE_KEY);
    if (!code || code.length < 6) {
        const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
        code = '';
        for (let i = 0; i < 6; i++) code += alphabet[Math.floor(Math.random() * alphabet.length)];
        localStorage.setItem(CODE_STORAGE_KEY, code);
    }
    return code;
}
let setupState: SetupState | null = null;
let selectedDownloadId: string | null = null;
let downloadableModels: DownloadableModel[] = [];

function log(line: string): void {
    const ts = new Date().toLocaleTimeString();
    logEl.textContent = `[${ts}] ${line}\n` + (logEl.textContent ?? '');
}

/** Tauri rejects a failing command with a plain STRING (the Rust `Err`), not an
 *  Error — so `(e as Error).message` is `undefined`. Normalise to a readable
 *  message regardless of what was thrown. */
function errMsg(e: unknown): string {
    if (typeof e === 'string') return e;
    if (e instanceof Error) return e.message;
    if (e && typeof e === 'object' && 'message' in e) return String((e as { message: unknown }).message);
    try { return JSON.stringify(e); } catch { return String(e); }
}

function setStatusBar(text: string, models = ''): void {
    statusBarText.textContent = text;
    statusBarModels.textContent = models;
}

// ─── Inference activity feedback ─────────────────────────────────────────────
let requestsServed = 0;

type ActivityState = 'idle' | 'receiving' | 'generating' | 'done' | 'error';

function setActivity(
    state: ActivityState,
    label: string,
    detail = '',
    preview?: string,
): void {
    activityCard.hidden = false;
    activityCard.dataset.state = state;
    activityLabel.textContent = label;
    activityDetail.textContent = detail;
    activityCount.textContent = requestsServed > 0
        ? `${requestsServed} request${requestsServed === 1 ? '' : 's'} served`
        : '';
    if (preview && preview.trim()) {
        activityPreview.hidden = false;
        activityPreview.textContent = preview.length > 240
            ? `…${preview.slice(-240)}`
            : preview;
    } else if (state === 'idle' || state === 'done') {
        // keep last preview visible on done; clear only when idle
        if (state === 'idle') activityPreview.hidden = true;
    }
}

/** Pull the latest user turn out of the formatted chat prompt for display. */
function lastUserLine(prompt: string): string {
    const matches = [...prompt.matchAll(/<\|im_start\|>user\n([\s\S]*?)<\|im_end\|>/g)];
    const last = matches.at(-1)?.[1] ?? prompt;
    return last.replace(/<tool_result[\s\S]*?<\/tool_result>/g, '[tool result]').trim();
}

function showStep(step: number): void {
    currentWizardStep = step;
    stepPanels.forEach((el, i) => {
        const n = i + 1;
        const show = n === step;
        el.hidden = !show;
        const unlocked = n === 1 || (n === 2 && modelDownloaded) || (n === 3 && modelLoaded);
        el.classList.toggle('step-locked', !unlocked);
        el.classList.toggle('unlocked', unlocked);
    });
    activityItems.forEach((el, i) => {
        const n = i + 1;
        el.classList.toggle('active', n === step);
        el.classList.toggle('done', n < step);
    });
    tabItems.forEach((el, i) => {
        const n = i + 1;
        el.classList.toggle('active', n === step);
        el.classList.toggle('done', n < step);
    });
}

function setWizardStep(step: number): void {
    showStep(step);
}

function setSessionUi(status: string, detail: string, pillClass: string): void {
    sessionStatusEl.textContent = status;
    sessionStatusEl.className = `status-pill ${pillClass}`;
    sessionDetailEl.textContent = detail;
}

function setLoadUi(loaded: boolean, name: string, detail: string): void {
    modelLoaded = loaded;
    loadedModelName = loaded ? name : null;
    hub?.setModelStatus({ loaded, name: loaded ? name : undefined });
    loadStatus.textContent = loaded ? `Loaded: ${name}` : 'No model loaded';
    loadStatus.className = `status-pill ${loaded ? 'connected' : 'idle'}`;
    loadDetail.textContent = detail;
    unloadModelBtn.disabled = !loaded;
    loadDone.hidden = !loaded;
    if (loaded) {
        setWizardStep(3);
        const conns = hub?.listConnections().length ?? 0;
        setSessionUi(
            conns > 0 ? 'Online' : 'Listening',
            conns > 0 ? `${conns} connection${conns === 1 ? '' : 's'}` : 'Share your code with Playground to connect',
            'connected',
        );
        setStatusBar('Model loaded', name);
    }
}

// ─── Claude API proxy config ─────────────────────────────────────────────────

interface ClaudeConfigView {
    model: string;
    has_key: boolean;
    models: { id: string; label: string }[];
}

/** Fill the model dropdown from the static list. Called once at startup so the
 *  select always has options, independent of the async config fetch. */
function populateClaudeModels(): void {
    if (claudeModelSelect.options.length > 0) return;
    for (const m of CLAUDE_MODELS) {
        const opt = document.createElement('option');
        opt.value = m.id;
        opt.textContent = m.label;
        claudeModelSelect.appendChild(opt);
    }
    claudeModelSelect.value = claudeModel;
}

function applyClaudeView(view: ClaudeConfigView): void {
    claudeModel = view.model;
    claudeHasKey = view.has_key;
    populateClaudeModels();
    // Select the saved model if it's one we know about.
    if (CLAUDE_MODELS.some(m => m.id === view.model)) claudeModelSelect.value = view.model;
    updateClaudeUi();
}

function updateClaudeUi(): void {
    const active = activeProvider === 'claude';
    claudeActivePill.hidden = !active;
    claudeActivateBtn.hidden = active;
    claudeDeactivateBtn.hidden = !active;
    claudeActivateBtn.disabled = !claudeHasKey;
    claudeStatus.textContent = active
        ? `Serving ${claudeModel}`
        : claudeHasKey ? 'API key saved — ready to use' : 'No API key set';
    claudeStatus.className = `status-pill ${active ? 'connected' : claudeHasKey ? 'idle' : 'idle'}`;
}

async function refreshClaudeConfig(): Promise<void> {
    if (!isTauriApp()) return;
    try {
        const view = await tauriInvoke<ClaudeConfigView>('get_claude_config');
        applyClaudeView(view);
    } catch (e) {
        log(`claude config load failed: ${errMsg(e)}`);
    }
}

async function saveClaudeConfig(): Promise<void> {
    claudeSaveBtn.disabled = true;
    try {
        const key = claudeKeyInput.value.trim();
        const view = await tauriInvoke<ClaudeConfigView>('set_claude_config', {
            model: claudeModelSelect.value,
            // Only send the key when the user typed one (blank = keep current).
            apiKey: key.length > 0 ? key : null,
        });
        claudeKeyInput.value = '';
        applyClaudeView(view);
        // If Claude is the active provider, reflect the (possibly new) model name.
        if (activeProvider === 'claude') activateClaude();
        log(`claude config saved (${view.model}${view.has_key ? ', key set' : ''})`);
    } catch (e) {
        const msg = errMsg(e);
        log(`claude config save failed: ${msg}`);
        claudeStatus.textContent = `Save failed: ${msg}`;
    } finally {
        claudeSaveBtn.disabled = false;
    }
}

/** Switch GhostBot to serve the Claude API proxy. Marks serving as ready (so
 *  the wizard advances) without needing a local GGUF loaded. */
function activateClaude(): void {
    if (!claudeHasKey) {
        claudeStatus.textContent = 'Set an API key first';
        return;
    }
    activeProvider = 'claude';
    modelLoaded = true;
    loadedModelName = `Claude (${claudeModel})`;
    hub?.setModelStatus({ loaded: true, name: loadedModelName });
    loadStatus.textContent = `Serving: ${loadedModelName}`;
    loadStatus.className = 'status-pill connected';
    loadDetail.textContent = 'Cloud inference via the Anthropic API.';
    loadDone.hidden = false;
    setWizardStep(3);
    setStatusBar('Serving Claude', loadedModelName);
    updateClaudeUi();
    log(`now serving ${loadedModelName}`);
}

/** Stop using Claude; revert to the local model (loaded or not). */
function deactivateClaude(): void {
    activeProvider = 'local';
    const localLoaded = loadedModelName !== null && !loadedModelName.startsWith('Claude (');
    // Reset serving state to whatever the local model is.
    modelLoaded = false;
    loadedModelName = null;
    hub?.setModelStatus({ loaded: false });
    setLoadUi(false, '', localLoaded ? 'Reloaded local mode — load a model to serve' : 'Load a model or use Claude to serve');
    setWizardStep(2);
    updateClaudeUi();
    log('stopped serving Claude');
}

/** Render the live connections list from the hub. */
function renderConnections(): void {
    if (!hub) { connectionsEl.innerHTML = ''; return; }
    const conns = hub.listConnections();
    connectionsEl.innerHTML = '';
    if (conns.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'conn-empty';
        empty.textContent = 'No connections yet. Enter your code in a Playground to pair.';
        connectionsEl.appendChild(empty);
    }
    for (const c of conns) {
        connectionsEl.appendChild(connectionRow(c));
    }
    // Reflect count in the status pill.
    const active = conns.filter(c => c.approved).length;
    const pending = conns.filter(c => !c.approved).length;
    if (modelLoaded) {
        setSessionUi(
            active > 0 ? 'Online' : 'Listening',
            pending > 0 ? `${pending} awaiting approval` : (active > 0 ? `${active} connected` : 'Share your code with Playground'),
            active > 0 ? 'connected' : 'idle',
        );
    }
    setStatusBar(active > 0 ? `${active} connected` : 'Listening', loadedModelName ?? '');
}

function connectionRow(c: GhostConnection): HTMLElement {
    const row = document.createElement('div');
    row.className = 'conn-row';
    row.dataset.status = c.status;

    const dot = document.createElement('span');
    dot.className = 'conn-dot';

    const main = document.createElement('div');
    main.className = 'conn-main';
    const label = document.createElement('span');
    label.className = 'conn-label';
    label.textContent = c.label || `${c.peerId.slice(0, 6)}…`;
    const sub = document.createElement('span');
    sub.className = 'conn-sub';
    const statusText = !c.approved
        ? 'Wants to connect'
        : c.status === 'inferring'
            ? `Generating${c.activeStreams > 1 ? ` (${c.activeStreams})` : ''}…`
            : 'Connected';
    sub.textContent = modelLoaded && loadedModelName
        ? `${statusText} · ${loadedModelName}`
        : statusText;
    main.append(label, sub);

    const actions = document.createElement('div');
    actions.className = 'conn-actions';
    if (!c.approved) {
        const approve = document.createElement('button');
        approve.className = 'btn primary btn-small';
        approve.textContent = 'Approve';
        approve.addEventListener('click', () => hub?.approve(c.peerId));
        const deny = document.createElement('button');
        deny.className = 'btn secondary btn-small';
        deny.textContent = 'Deny';
        deny.addEventListener('click', () => hub?.deny(c.peerId));
        actions.append(approve, deny);
    } else {
        const disc = document.createElement('button');
        disc.className = 'btn secondary btn-small';
        disc.textContent = 'Disconnect';
        disc.title = 'Disconnect and require re-approval next time';
        disc.addEventListener('click', () => hub?.disconnect(c.peerId));
        actions.append(disc);
    }

    row.append(dot, main, actions);
    return row;
}

function renderModelCatalog(models: DownloadableModel[]): void {
    modelCatalogEl.innerHTML = '';
    for (const m of models) {
        const card = document.createElement('label');
        card.className = `model-card${selectedDownloadId === m.id ? ' selected' : ''}`;

        const radio = document.createElement('input');
        radio.type = 'radio';
        radio.name = 'download-model';
        radio.value = m.id;
        radio.checked = selectedDownloadId === m.id;

        const main = document.createElement('div');
        main.className = 'model-card-main';

        const title = document.createElement('div');
        title.className = 'model-card-title';
        title.textContent = m.label;
        if (m.recommended) {
            const badge = document.createElement('span');
            badge.className = 'model-badge';
            badge.textContent = 'Recommended';
            title.appendChild(badge);
        }

        const desc = document.createElement('p');
        desc.className = 'model-card-desc';
        desc.textContent = m.description;

        main.append(title, desc);

        const meta = document.createElement('div');
        meta.className = 'model-card-meta';
        if (m.incomplete) meta.classList.add('warn');
        else if (m.downloaded) meta.classList.add('ok');
        meta.innerHTML = `${m.size_label}<br>${formatModelStatus(m)}`;

        card.append(radio, main, meta);
        card.addEventListener('click', () => {
            selectedDownloadId = m.id;
            renderModelCatalog(downloadableModels);
            updateDownloadButtons();
        });
        modelCatalogEl.appendChild(card);
    }
}

function updateDownloadButtons(): void {
    const selected = downloadableModels.find(m => m.id === selectedDownloadId);
    if (!selected) {
        downloadBtn.disabled = true;
        redownloadBtn.hidden = true;
        return;
    }
    downloadBtn.disabled = !!(selected.downloaded && !selected.incomplete);
    downloadBtn.textContent = selected.downloaded && !selected.incomplete
        ? 'Already downloaded'
        : 'Download selected model';
    redownloadBtn.hidden = !(selected.downloaded || selected.incomplete);
}

function showDownloadProgress(p: DownloadProgress): void {
    downloadIdle.hidden = true;
    downloadDone.hidden = true;
    downloadActive.hidden = false;

    const pctInt = Math.round(p.pct * 100);
    dlPct.textContent = `${pctInt}%`;
    dlLabel.textContent = p.label ?? '—';
    dlMb.textContent = p.totalMb > 0
        ? `${p.downloadedMb.toFixed(1)} / ${p.totalMb.toFixed(1)} MB`
        : `${p.downloadedMb.toFixed(1)} MB`;
    dlBar.style.width = `${pctInt}%`;
    dlSpeed.textContent = p.speedMbps > 0 ? `${p.speedMbps.toFixed(1)} MB/s` : '—';
    dlEta.textContent = formatEta(p.etaSec);
    dlPhase.textContent = p.phase === 'complete'
        ? 'Download complete'
        : 'Downloading from Hugging Face…';
}

function showDownloadComplete(detail: string): void {
    modelDownloaded = true;
    downloadActive.hidden = true;
    downloadDone.hidden = false;
    downloadDoneDetail.textContent = detail;
    // Keep the catalog + download button available so additional models can
    // be downloaded — selecting a not-yet-downloaded model re-enables the
    // button (updateDownloadButtons). Without this, the first download hid
    // the picker and you could never add a second model.
    downloadIdle.hidden = false;
    downloadStatusText.textContent = 'To add another model, select it above and Download.';
    updateDownloadButtons();
    setWizardStep(2);
    setStatusBar('Model on disk', detail);
}

async function openModelsFolder(): Promise<void> {
    const dir = await tauriInvoke<string>('open_models_folder');
    log(`opened models folder: ${dir}`);
}

async function refreshModelList(): Promise<void> {
    const models = await tauriInvoke<ModelEntry[]>('list_models');
    modelSelect.innerHTML = '';
    for (const m of models) {
        const opt = document.createElement('option');
        opt.value = m.id;
        const suffix = m.incomplete ? ' — INCOMPLETE' : '';
        opt.textContent = `${m.label} (${m.size_mb} MB)${suffix}`;
        modelSelect.appendChild(opt);
    }
    modelSelect.disabled = models.length === 0;
    loadModelBtn.disabled = models.length === 0;
    if (models.length > 0) {
        const rec = setupState?.recommended_id;
        const usable = models.find(m => !m.incomplete && m.id === rec)
            ?? models.find(m => !m.incomplete);
        if (usable) modelSelect.value = usable.id;
    }
}

async function refreshSetup(): Promise<void> {
    setupState = await tauriInvoke<SetupState>('get_setup_state');
    downloadableModels = setupState.downloadable;
    modelsDirHint.textContent = setupState.models_dir;
    selectedDownloadId = pickDefaultDownloadModel(downloadableModels);
    renderModelCatalog(downloadableModels);
    updateDownloadButtons();

    if (hasUsableLocalModel(downloadableModels) || setupState.model_count > 0) {
        modelDownloaded = true;
        const ready = downloadableModels.filter(m => m.downloaded && !m.incomplete);
        const detail = ready.length > 0
            ? `${ready.length} model(s) in ${setupState.models_dir}`
            : `${setupState.model_count} model(s) found (some may be incomplete)`;
        showDownloadComplete(detail);
        await refreshModelList();
        setWizardStep(2);
    } else {
        const incomplete = downloadableModels.filter(m => m.incomplete);
        if (incomplete.length > 0) {
            downloadStatusText.textContent =
                'Incomplete download detected — open the models folder, delete the partial file, then re-download.';
        } else {
            downloadStatusText.textContent = 'No local model yet — pick one above and download.';
        }
        downloadBtn.disabled = !selectedDownloadId;
    }

    if (setupState.loaded_model) {
        setLoadUi(true, setupState.loaded_model, 'Model is in GPU memory');
    }

    skipDownloadBtn.hidden = !(setupState.model_count > 0 && !hasUsableLocalModel(downloadableModels));
}

async function runDownload(force: boolean): Promise<void> {
    if (!selectedDownloadId) return;
    const selected = downloadableModels.find(m => m.id === selectedDownloadId);
    downloadBtn.disabled = true;
    redownloadBtn.disabled = true;
    log(`starting download: ${selected?.label ?? selectedDownloadId}${force ? ' (replace)' : ''}`);
    showDownloadProgress({
        pct: 0, downloadedMb: 0, totalMb: 0, speedMbps: 0, etaSec: 0,
        phase: 'starting', label: selected?.label, text: '0 MB',
    });

    const unlisten = await tauriListen<DownloadProgress>('download-progress', (p) => {
        showDownloadProgress(p);
    });

    try {
        await tauriInvoke('download_model', { modelId: selectedDownloadId, force });
        setupState = await tauriInvoke<SetupState>('get_setup_state');
        downloadableModels = setupState.downloadable;
        renderModelCatalog(downloadableModels);
        updateDownloadButtons();
        await refreshModelList();
        const saved = downloadableModels.find(m => m.id === selectedDownloadId);
        showDownloadComplete(
            saved
                ? `${saved.label} (${saved.size_mb} MB) saved`
                : 'Download complete',
        );
        log('download complete');
    } catch (e) {
        downloadIdle.hidden = false;
        downloadActive.hidden = true;
        downloadStatusText.textContent = (e as Error).message;
        log(`download failed: ${(e as Error).message}`);
        updateDownloadButtons();
    } finally {
        redownloadBtn.disabled = false;
        unlisten();
    }
}

async function streamFromRust(
    streamId: number,
    prompt: string,
    maxTokens: number,
    temperature: number,
    signal?: AbortSignal,
): Promise<AsyncIterable<GhostStreamEvent>> {
    await tauriInvoke('start_stream', {
        streamId, prompt, maxTokens, temperature,
    });
    return consumeGhostStream(streamId, signal);
}

/** Forward inference to the Anthropic API (the Claude proxy provider). Sends the
 *  STRUCTURED messages (Claude wants system + alternating turns, not the flat
 *  llama prompt) and consumes the same `ghost-token` event channel. */
async function streamFromClaude(
    streamId: number,
    messages: { role: string; content: string }[],
    maxTokens: number,
    temperature: number,
    signal?: AbortSignal,
): Promise<AsyncIterable<GhostStreamEvent>> {
    await tauriInvoke('start_claude_stream', { streamId, messages, maxTokens, temperature });
    return consumeGhostStream(streamId, signal);
}

/** Shared `ghost-token` consumer for both the local and Claude providers. */
function consumeGhostStream(streamId: number, signal?: AbortSignal): Promise<AsyncIterable<GhostStreamEvent>> {
    return (async () => {
    const queue: GhostStreamEvent[] = [];
    let done = false;
    let error: string | null = null;
    let notify: (() => void) | null = null;

    const unlisten = await tauriListen<{
        streamId: number;
        delta?: string;
        done?: boolean;
        error?: string;
    }>('ghost-token', (payload) => {
        if (payload.streamId !== streamId) return;
        if (payload.error) {
            error = payload.error;
            done = true;
        } else if (payload.done) {
            queue.push({ kind: 'done', finishReason: 'stop' });
            done = true;
        } else if (payload.delta) {
            queue.push({ kind: 'text', delta: payload.delta });
        }
        notify?.();
    });

    // On abort (peer pressed Stop): tell the Rust engine to stop generating —
    // breaking the JS loop alone leaves the model running in the background,
    // burning GPU and blocking the next request. Then wake the iterator so it
    // returns promptly instead of waiting for the next (never-coming) token.
    const onAbort = () => {
        void tauriInvoke('abort_stream').catch(() => { /* engine already idle */ });
        done = true;
        notify?.();
    };
    if (signal) {
        if (signal.aborted) onAbort();
        else signal.addEventListener('abort', onAbort, { once: true });
    }

    return {
        async *[Symbol.asyncIterator]() {
            try {
                while (true) {
                    while (queue.length > 0) {
                        const ev = queue.shift()!;
                        yield ev;
                        if (ev.kind === 'done') return;
                    }
                    if (error) throw new Error(error);
                    if (done) return;
                    await new Promise<void>(r => { notify = r; });
                }
            } finally {
                unlisten();
                signal?.removeEventListener('abort', onAbort);
            }
        },
    };
    })();
}

// ─── Navigation ────────────────────────────────────────────────────────────
for (const el of [...activityItems, ...tabItems]) {
    el.addEventListener('click', () => {
        const step = Number((el as HTMLElement).dataset.step);
        if (step === 2 && !modelDownloaded) return;
        if (step === 3 && !modelLoaded) return;
        setWizardStep(step);
    });
}

openModelsFolderBtn.addEventListener('click', () => { void openModelsFolder(); });
openModelsFolderBtn2.addEventListener('click', () => { void openModelsFolder(); });

downloadBtn.addEventListener('click', () => { void runDownload(false); });
redownloadBtn.addEventListener('click', () => { void runDownload(true); });

skipDownloadBtn.addEventListener('click', () => {
    modelDownloaded = true;
    setWizardStep(2);
    log('skipped download — using existing models');
});

gotoStep2.addEventListener('click', () => setWizardStep(2));
gotoStep3.addEventListener('click', () => setWizardStep(3));

loadModelBtn.addEventListener('click', async () => {
    const id = modelSelect.value;
    if (!id) return;
    loadModelBtn.disabled = true;
    loadStatus.textContent = 'Loading…';
    loadStatus.className = 'status-pill loading';
    log(`loading ${id}…`);
    try {
        const info = await tauriInvoke<{ name: string; path: string }>('load_model', { modelId: id });
        activeProvider = 'local';
        setLoadUi(true, info.name, info.path);
        updateClaudeUi();
        log(`loaded ${info.name}`);
    } catch (e) {
        const msg = (e as Error).message;
        setLoadUi(false, '', msg);
        log(`load failed: ${msg}`);
        setStatusBar('Load failed — try re-downloading');
    } finally {
        loadModelBtn.disabled = false;
    }
});

unloadModelBtn.addEventListener('click', async () => {
    unloadModelBtn.disabled = true;
    try {
        await tauriInvoke('unload_model');
        setLoadUi(false, '', 'VRAM released — load a model to serve connections');
        setWizardStep(2);
        setStatusBar('Model unloaded');
        log('model unloaded');
    } catch (e) {
        log(`unload failed: ${(e as Error).message}`);
    } finally {
        unloadModelBtn.disabled = false;
    }
});

claudeSaveBtn.addEventListener('click', () => { void saveClaudeConfig(); });
claudeActivateBtn.addEventListener('click', () => activateClaude());
claudeDeactivateBtn.addEventListener('click', () => deactivateClaude());
claudeModelSelect.addEventListener('change', () => {
    claudeModel = claudeModelSelect.value;
    // Persist the model choice immediately (no key change).
    void tauriInvoke('set_claude_config', { model: claudeModelSelect.value, apiKey: null }).catch(() => {});
    if (activeProvider === 'claude') activateClaude();
});

copyCodeBtn.addEventListener('click', () => {
    const code = ghostCodeEl.textContent ?? '';
    void navigator.clipboard.writeText(code).then(() => {
        const prev = copyCodeBtn.textContent;
        copyCodeBtn.textContent = 'Copied';
        setTimeout(() => { copyCodeBtn.textContent = prev ?? 'Copy'; }, 1500);
    }).catch(() => { /* clipboard blocked — code is visible anyway */ });
});

clearLogBtn.addEventListener('click', () => { logEl.textContent = ''; });

/** The inference stream handler shared by all connected peers. Wraps the
 *  Rust stream with the activity-card feedback (latest request shown). */
function makeStreamHandler() {
    return async (
        req: Parameters<Parameters<NonNullable<typeof hub>['setStreamHandler']>[0] & object>[0],
        signal: AbortSignal,
    ) => {
        const prompt = formatChatPrompt(req.messages);
        const userLine = lastUserLine(prompt);
        const useClaude = activeProvider === 'claude';
        const provNote = useClaude ? ` [claude: ${claudeModel}]` : '';
        setActivity('receiving', 'Request received', `Prompt: ${userLine.slice(0, 80)}${provNote}`, userLine);
        log(`▶ request #${req.id}${provNote}: ${userLine.slice(0, 60)}`);

        const inner = useClaude
            ? await streamFromClaude(req.id, req.messages, req.maxTokens ?? 4096, req.temperature ?? 0.2, signal)
            : await streamFromRust(req.id, prompt, req.maxTokens ?? 2048, req.temperature ?? 0.2, signal);
        const startedAt = Date.now();
        let tokens = 0;
        let acc = '';

        return {
            async *[Symbol.asyncIterator]() {
                try {
                    for await (const ev of inner) {
                        if (ev.kind === 'text') {
                            tokens++;
                            acc += ev.delta;
                            if (tokens % 4 === 0 || tokens < 4) {
                                const secs = (Date.now() - startedAt) / 1000;
                                const tps = secs > 0 ? (tokens / secs).toFixed(1) : '—';
                                setActivity('generating', 'Generating reply…', `${tokens} tokens · ${tps} tok/s`, acc);
                            }
                        }
                        yield ev;
                    }
                    const secs = ((Date.now() - startedAt) / 1000).toFixed(1);
                    if (signal.aborted) {
                        setActivity('done', 'Stopped by peer', `${tokens} tokens · ${secs}s`, acc);
                        log(`■ request #${req.id} stopped after ${tokens} tokens`);
                    } else {
                        requestsServed++;
                        setActivity('done', 'Reply sent', `${tokens} tokens · ${secs}s`, acc);
                        log(`✓ request #${req.id} done: ${tokens} tokens in ${secs}s`);
                    }
                } catch (e) {
                    setActivity('error', 'Inference error', (e as Error).message);
                    log(`✗ request #${req.id} failed: ${(e as Error).message}`);
                    throw e;
                }
            },
        };
    };
}

/** Create the hub on the stable code, wire it to the UI, and start listening.
 *  `autoApprove` is used only by the dev `?probe=` hook. */
function startHub(code: string, autoApprove = false): void {
    ghostCodeEl.textContent = code;
    hub = createGhostHub(code);
    hub.setStreamHandler(makeStreamHandler());
    hub.setModelStatus({ loaded: modelLoaded, name: loadedModelName ?? undefined });
    hub.onLog((line) => log(line));
    hub.onChange(() => {
        renderConnections();
        if (autoApprove) {
            for (const c of hub!.listConnections()) {
                if (!c.approved) hub!.approve(c.peerId);
            }
        }
    });
    hub.start();
    renderConnections();
}

/** Dev-only automation hook: `?probe=CODE` listens on CODE and auto-approves
 *  every peer with a canned stream handler (no model needed) so external
 *  probes can test the real WKWebView WebRTC path. */
function maybeStartProbeSession(): boolean {
    if (!import.meta.env.DEV) return false;
    const code = new URLSearchParams(location.search).get('probe');
    if (!code) return false;
    ghostCodeEl.textContent = code.toUpperCase();
    hub = createGhostHub(code);
    hub.setModelStatus({ loaded: true, name: 'probe-canned-model' });
    hub.setStreamHandler(async (req) => ({
        async *[Symbol.asyncIterator]() {
            void req;
            yield { kind: 'text' as const, delta: 'hello from tauri ghost' };
            yield { kind: 'done' as const, finishReason: 'stop' as const };
        },
    }));
    hub.onChange(() => {
        renderConnections();
        for (const c of hub!.listConnections()) if (!c.approved) hub!.approve(c.peerId);
    });
    hub.start();
    setSessionUi('Probe', `Listening on ${code}`, 'waiting');
    log(`probe mode: listening on ${code} (auto-approving)`);
    return true;
}

async function boot(): Promise<void> {
    if (!document.getElementById('vscode-codicon-stylesheet')) {
        const link = document.createElement('link');
        link.id = 'vscode-codicon-stylesheet';
        link.rel = 'stylesheet';
        link.href = codiconsUrl;
        document.head.appendChild(link);
    }

    if (!isTauriApp()) {
        browserBlocker.hidden = false;
        appRoot.hidden = true;
        appBadge.textContent = 'Browser';
        appBadge.classList.add('browser');
        return;
    }

    appBadge.textContent = 'Desktop';
    installIceMonitor(log);
    populateClaudeModels();   // options present before the async config load
    setWizardStep(1);
    setStatusBar('GhostBot ready');
    log('GhostBot desktop ready');

    if (maybeStartProbeSession()) return;

    // Start listening on our stable code immediately — peers can pair (and be
    // approved) before or after a model is loaded; requests just wait for one.
    startHub(getOrCreateCode());

    try {
        await refreshSetup();
    } catch (e) {
        downloadStatusText.textContent = (e as Error).message;
        log(`setup failed: ${(e as Error).message}`);
    }

    // Load the saved Claude config (model + whether a key is stored).
    await refreshClaudeConfig();
}

void boot();
