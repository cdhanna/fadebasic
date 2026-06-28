// Logs dockview panel. Subscribes to the app-wide LogBus and renders a
// scrolling, filterable terminal-style log view. Channel chips toggle
// which producers are shown; level chips do the same for severity. New
// entries auto-scroll to the bottom unless the user has scrolled up
// (terminal-style "live tail" UX).
//
// One panel per playground window; mounts into a host div placed by
// index.html. Styles live here under the `fade-log-*` prefix and don't
// leak.

import { appLog, type LogBus, type LogEntry, type LogLevel } from './log-bus';

const CSS_PREFIX = 'fade-log';
const STYLE_ID = `${CSS_PREFIX}-styles`;
const ALL_LEVELS: LogLevel[] = ['debug', 'info', 'warn', 'error'];

export interface LogsPanelOptions {
    container: HTMLElement;
    bus?: LogBus;             // defaults to the app singleton
    /** Initial level filter; entries below the configured min level are
     *  hidden. Default: include everything. */
    minLevel?: LogLevel;
}

export interface LogsPanelHandle {
    dispose(): void;
}

export function mountLogsPanel(opts: LogsPanelOptions): LogsPanelHandle {
    injectStylesOnce();
    const bus = opts.bus ?? appLog;
    const root = opts.container;
    root.classList.add(`${CSS_PREFIX}-root`);
    root.replaceChildren();

    // ─── filter state ──────────────────────────────────────────────────────
    const enabledChannels = new Set<string>();         // empty = all
    const enabledLevels = new Set<LogLevel>(ALL_LEVELS);
    let searchQuery = '';                              // case-insensitive substring; empty = all
    if (opts.minLevel) {
        const i = ALL_LEVELS.indexOf(opts.minLevel);
        if (i >= 0) {
            enabledLevels.clear();
            for (let k = i; k < ALL_LEVELS.length; k++) enabledLevels.add(ALL_LEVELS[k]);
        }
    }

    // ─── DOM ───────────────────────────────────────────────────────────────
    const toolbar = el('div', `${CSS_PREFIX}-toolbar`);
    const channelChips = el('div', `${CSS_PREFIX}-chips`);
    const levelChips = el('div', `${CSS_PREFIX}-chips ${CSS_PREFIX}-chips-levels`);
    const searchInput = document.createElement('input');
    searchInput.type = 'search';
    searchInput.placeholder = 'Filter…';
    searchInput.className = `${CSS_PREFIX}-search`;
    searchInput.spellcheck = false;
    // Debounce so typing fast doesn't re-render on every keystroke when
    // the list is long.
    let searchDebounce: ReturnType<typeof setTimeout> | null = null;
    searchInput.addEventListener('input', () => {
        if (searchDebounce) clearTimeout(searchDebounce);
        searchDebounce = setTimeout(() => {
            searchQuery = searchInput.value.toLowerCase();
            refilter();
        }, 80);
    });
    const clearBtn = button('Clear', () => { bus.clear(); list.replaceChildren(); });
    const copyBtn  = button('Copy', () => {
        const text = bus.snapshot()
            .filter(passesFilter)
            .map(formatPlain)
            .join('\n');
        void navigator.clipboard?.writeText(text);
    });
    toolbar.append(channelChips, sep(), levelChips, sep(), searchInput, sep(), copyBtn, clearBtn);

    const list = el('div', `${CSS_PREFIX}-list`);
    list.setAttribute('role', 'log');
    list.setAttribute('aria-live', 'polite');

    root.append(toolbar, list);

    // Track scroll-pinning so live-tail doesn't fight the user.
    let pinnedToBottom = true;
    list.addEventListener('scroll', () => {
        const slack = 8;
        pinnedToBottom = list.scrollTop + list.clientHeight >= list.scrollHeight - slack;
    });

    function passesFilter(e: LogEntry): boolean {
        if (enabledChannels.size > 0 && !enabledChannels.has(e.channel)) return false;
        if (!enabledLevels.has(e.level)) return false;
        if (searchQuery) {
            // Match against channel + message so users can find a chip
            // value or a substring of the message body without thinking
            // about which field. Cheap toLowerCase — entries are short.
            const msg = e.message ?? '';
            if (!e.channel.toLowerCase().includes(searchQuery)
                && !msg.toLowerCase().includes(searchQuery)) return false;
        }
        return true;
    }

    // ─── rendering ─────────────────────────────────────────────────────────
    function renderChannelChips() {
        channelChips.replaceChildren();
        const channels = bus.channels();
        if (channels.length === 0) {
            const empty = el('span', `${CSS_PREFIX}-empty`);
            empty.textContent = 'no channels yet';
            channelChips.append(empty);
            return;
        }
        for (const c of channels) {
            const active = enabledChannels.size === 0 || enabledChannels.has(c);
            channelChips.append(chip(c, active, () => {
                // Empty-set means "all on." Clicking from there narrows to
                // just this channel. Clicking an active chip removes it.
                // Clicking an inactive chip while others are selected adds it.
                if (enabledChannels.has(c)) enabledChannels.delete(c);
                else enabledChannels.add(c);
                refilter();
                renderChannelChips();
            }));
        }
        // "All" chip — clears the filter.
        const allActive = enabledChannels.size === 0;
        channelChips.append(chip('all', allActive, () => {
            enabledChannels.clear();
            refilter();
            renderChannelChips();
        }));
    }

    function renderLevelChips() {
        levelChips.replaceChildren();
        for (const lvl of ALL_LEVELS) {
            const active = enabledLevels.has(lvl);
            levelChips.append(chip(lvl, active, () => {
                if (active) enabledLevels.delete(lvl);
                else enabledLevels.add(lvl);
                refilter();
                renderLevelChips();
            }, `${CSS_PREFIX}-chip-${lvl}`));
        }
    }

    function refilter() {
        list.replaceChildren();
        const entries = bus.snapshot();
        for (const e of entries) if (passesFilter(e)) list.append(renderEntry(e));
        if (pinnedToBottom) scrollToBottom();
    }

    function renderEntry(e: LogEntry): HTMLElement {
        const row = el('div', `${CSS_PREFIX}-row ${CSS_PREFIX}-row-${e.level}`);
        const t = el('span', `${CSS_PREFIX}-time`);
        t.textContent = formatTime(e.time);
        const ch = el('span', `${CSS_PREFIX}-channel`);
        ch.textContent = e.channel;
        const lvl = el('span', `${CSS_PREFIX}-level ${CSS_PREFIX}-level-${e.level}`);
        lvl.textContent = e.level.toUpperCase().slice(0, 4);
        const msg = el('span', `${CSS_PREFIX}-msg`);
        if (e.progress) {
            msg.textContent = `${e.message} [${e.progress.current}/${e.progress.total}]`;
        } else {
            msg.textContent = e.message;
        }
        row.append(t, ch, lvl, msg);
        return row;
    }

    function scrollToBottom() {
        list.scrollTop = list.scrollHeight;
    }

    // Initial paint.
    renderChannelChips();
    renderLevelChips();
    refilter();

    // ─── live subscription ─────────────────────────────────────────────────
    const unsub = bus.subscribe((entry) => {
        // Refresh chip set if we hit a previously-unseen channel.
        if (!bus.channels().every((c) => channelChips.querySelector(`[data-chip="${c}"]`))) {
            renderChannelChips();
        }
        if (!passesFilter(entry)) return;
        list.append(renderEntry(entry));
        if (pinnedToBottom) scrollToBottom();
    });

    return {
        dispose() {
            unsub();
            root.replaceChildren();
            root.classList.remove(`${CSS_PREFIX}-root`);
        },
    };
}

// ─── DOM helpers ────────────────────────────────────────────────────────────

function el<K extends keyof HTMLElementTagNameMap>(tag: K, cls: string): HTMLElementTagNameMap[K] {
    const n = document.createElement(tag);
    n.className = cls;
    return n;
}

function button(label: string, onClick: () => void): HTMLButtonElement {
    const b = document.createElement('button');
    b.className = `${CSS_PREFIX}-btn`;
    b.type = 'button';
    b.textContent = label;
    b.onclick = onClick;
    return b;
}

function chip(label: string, active: boolean, onClick: () => void, extraClass = ''): HTMLButtonElement {
    const c = document.createElement('button');
    c.className = `${CSS_PREFIX}-chip ${active ? `${CSS_PREFIX}-chip-active` : ''} ${extraClass}`.trim();
    c.type = 'button';
    c.textContent = label;
    c.dataset.chip = label;
    c.onclick = onClick;
    return c;
}

function sep(): HTMLElement {
    return el('span', `${CSS_PREFIX}-sep`);
}

function formatTime(ms: number): string {
    const d = new Date(ms);
    const hh = String(d.getHours()).padStart(2, '0');
    const mm = String(d.getMinutes()).padStart(2, '0');
    const ss = String(d.getSeconds()).padStart(2, '0');
    const sub = String(d.getMilliseconds()).padStart(3, '0');
    return `${hh}:${mm}:${ss}.${sub}`;
}

function formatPlain(e: LogEntry): string {
    const t = new Date(e.time).toISOString();
    const prog = e.progress ? ` [${e.progress.current}/${e.progress.total}]` : '';
    return `${t} ${e.channel.padEnd(10)} ${e.level.toUpperCase().padEnd(5)} ${e.message}${prog}`;
}

// ─── styles ────────────────────────────────────────────────────────────────

function injectStylesOnce(): void {
    if (document.getElementById(STYLE_ID)) return;
    const style = document.createElement('style');
    style.id = STYLE_ID;
    style.textContent = `
.${CSS_PREFIX}-root {
    display: flex; flex-direction: column;
    height: 100%; box-sizing: border-box;
    overflow: hidden;
    color: var(--fg);
    background: var(--bg-2);
    font: 12px/1.4 ui-sans-serif, system-ui, sans-serif;
}
.${CSS_PREFIX}-toolbar {
    display: flex; align-items: center; gap: 6px; flex-wrap: wrap;
    padding: 6px 8px;
    border-bottom: 1px solid var(--border-2);
    flex-shrink: 0;
}
.${CSS_PREFIX}-chips {
    display: inline-flex; gap: 4px; flex-wrap: wrap;
}
.${CSS_PREFIX}-empty { opacity: 0.5; font-style: italic; font-size: 11px; }
.${CSS_PREFIX}-chip {
    appearance: none; border: 1px solid var(--border-2);
    background: transparent; color: inherit;
    padding: 1px 8px; border-radius: 999px;
    font: inherit; font-size: 11px; cursor: pointer;
    transition: filter 0.1s;
    opacity: 0.55;
}
.${CSS_PREFIX}-chip:hover { background: var(--hover-bg); }
.${CSS_PREFIX}-chip-active {
    opacity: 1;
    background: var(--hover-bg);
    border-color: var(--accent);
}
/* Level-coded text colors threaded through CSS vars so light themes can
   substitute darker hues. Defaults are the pastel-on-dark originals. */
.${CSS_PREFIX}-chip-debug.${CSS_PREFIX}-chip-active { color: var(--log-debug-fg, #8cf); }
.${CSS_PREFIX}-chip-info.${CSS_PREFIX}-chip-active  { color: var(--log-info-fg,  #ddd); }
.${CSS_PREFIX}-chip-warn.${CSS_PREFIX}-chip-active  { color: var(--log-warn-fg,  #fc6); }
.${CSS_PREFIX}-chip-error.${CSS_PREFIX}-chip-active { color: var(--log-error-fg, #f88); }
.${CSS_PREFIX}-sep {
    width: 1px; align-self: stretch;
    background: var(--border-2);
    margin: 0 2px;
}
.${CSS_PREFIX}-btn {
    appearance: none; border: 1px solid var(--border-2);
    background: transparent; color: inherit;
    padding: 2px 10px; border-radius: 4px;
    font: inherit; font-size: 11px; cursor: pointer;
}
.${CSS_PREFIX}-btn:hover { background: var(--hover-bg); }
.${CSS_PREFIX}-search {
    appearance: none; border: 1px solid var(--border-2);
    background: var(--bg); color: inherit;
    padding: 2px 8px; border-radius: 4px;
    font: inherit; font-size: 11px;
    min-width: 140px; flex: 0 1 200px;
}
.${CSS_PREFIX}-search::placeholder { opacity: 0.55; }
.${CSS_PREFIX}-search:focus { outline: 1px solid var(--accent); }
.${CSS_PREFIX}-list {
    flex: 1 1 auto; overflow-y: auto;
    padding: 4px 0;
    font-family: ui-monospace, monospace; font-size: 11px;
    background: var(--bg);
    color: var(--fg);
}
.${CSS_PREFIX}-row {
    display: grid;
    grid-template-columns: 90px 80px 44px 1fr;
    gap: 8px;
    padding: 1px 8px;
    white-space: pre-wrap; word-break: break-word;
}
.${CSS_PREFIX}-row:hover { background: var(--hover-bg); }
.${CSS_PREFIX}-time    { opacity: 0.55; }
.${CSS_PREFIX}-channel { opacity: 0.75; }
.${CSS_PREFIX}-level   { font-weight: 700; opacity: 0.85; }
.${CSS_PREFIX}-level-debug { color: var(--log-debug-fg, #8cf); }
.${CSS_PREFIX}-level-info  { color: var(--log-info-fg,  #ddd); }
.${CSS_PREFIX}-level-warn  { color: var(--log-warn-fg,  #fc6); }
.${CSS_PREFIX}-level-error { color: var(--log-error-fg, #f88); }
.${CSS_PREFIX}-row-error { background: rgba(255,80,80,0.06); }
.${CSS_PREFIX}-row-warn  { background: rgba(255,200,90,0.05); }
.${CSS_PREFIX}-msg { }
`;
    document.head.appendChild(style);
}
