// Help-tab renderer. Builds a grouped TOC + filterable command list +
// markdown reader from the data emitted by `FadeBridge.ListCommandDocs`
// (see FadeBasic.Export.Web/FadeBridge.cs). The markdown is the same text the LSP
// hover provider renders — both surfaces stay in sync because they
// share `HoverHandler.BuildCommandMarkdown`.

import { marked } from 'marked';

export interface CommandDocEntry {
    name: string;
    signature: string;
    group: string;
    markdown: string;
}

export interface HelpController {
    /** Replace the dataset. Safe to call repeatedly; preserves any
     *  current selection when the previously-shown command still exists
     *  in the new dataset. */
    setEntries(entries: CommandDocEntry[]): void;
    /** Reveal a specific command by name and scroll its TOC entry into
     *  view. Used by the hover provider's deep-link. Returns false if
     *  the name isn't known. */
    selectCommand(name: string): boolean;
    /** Current search query (read-only). */
    getQuery(): string;
}

interface Mounted {
    toc: HTMLElement;
    body: HTMLElement;
    search: HTMLInputElement;
    searchClear: HTMLButtonElement;
    count: HTMLElement;
}

// Minimal HTML escape for the empty/error-state strings we render
// without going through `marked`.
function escapeHtml(s: string): string {
    return s.replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]!));
}

// Strip anything that looks like an executable payload from marked's
// output. The command markdown is generated from XML doc comments on
// source we own, but the same scrub the markdown-preview panel uses
// keeps us safe if someone embeds raw HTML in a doc comment.
function scrubHtml(html: string): string {
    return html
        .replace(/<\s*(script|style|iframe|object|embed)[^>]*>[\s\S]*?<\s*\/\s*\1\s*>/gi, '')
        .replace(/\son[a-z]+\s*=\s*"[^"]*"/gi, '')
        .replace(/\son[a-z]+\s*=\s*'[^']*'/gi, '')
        .replace(/javascript:/gi, '');
}

export function mountHelpPanel(): HelpController {
    const m: Mounted = {
        toc:         document.getElementById('help-toc')!,
        body:        document.getElementById('help-body')!,
        search:      document.getElementById('help-search') as HTMLInputElement,
        searchClear: document.getElementById('help-search-clear') as HTMLButtonElement,
        count:       document.getElementById('help-count')!,
    };

    let entries: CommandDocEntry[] = [];
    let byName: Map<string, CommandDocEntry> = new Map();
    let selectedName: string | null = null;
    let query = '';

    function passesQuery(e: CommandDocEntry): boolean {
        if (!query) return true;
        const q = query.toLowerCase();
        // Match against name first (cheap), then markdown body. Body
        // search makes it easy to find a command by parameter name
        // ("alpha channel" → rgb).
        return e.name.toLowerCase().includes(q)
            || e.markdown.toLowerCase().includes(q);
    }

    function renderToc() {
        m.toc.innerHTML = '';
        const visible = entries.filter(passesQuery);
        m.count.textContent = visible.length === entries.length
            ? `${entries.length} commands`
            : `${visible.length} of ${entries.length}`;
        if (visible.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'help-toc-empty';
            empty.textContent = query
                ? `No commands match “${query}”.`
                : 'No commands loaded.';
            m.toc.append(empty);
            return;
        }
        // Bucket entries by group (server-side prefix heuristic). Within
        // a group, entries are already alphabetical (bridge sorts).
        const grouped = new Map<string, CommandDocEntry[]>();
        for (const e of visible) {
            const g = e.group || 'Core';
            const arr = grouped.get(g) ?? [];
            arr.push(e);
            grouped.set(g, arr);
        }
        // Sort groups by size descending so the biggest ("Core", e.g.)
        // surfaces first — feels more like a glossary than a phone book.
        const sortedGroups = Array.from(grouped.entries())
            .sort((a, b) => b[1].length - a[1].length || a[0].localeCompare(b[0]));
        for (const [groupName, items] of sortedGroups) {
            const heading = document.createElement('div');
            heading.className = 'help-toc-group';
            heading.textContent = `${groupName}  (${items.length})`;
            m.toc.append(heading);
            for (const e of items) {
                const link = document.createElement('a');
                link.className = 'help-toc-item' + (e.name === selectedName ? ' active' : '');
                link.dataset.name = e.name;
                link.textContent = e.name;
                link.onclick = (ev) => {
                    ev.preventDefault();
                    selectCommand(e.name, /*scrollIntoView*/ false);
                };
                m.toc.append(link);
            }
        }
    }

    function renderBody() {
        if (!selectedName) {
            // Keep the static placeholder that lives in index.html for
            // a clean reset — but only when there are no entries; once
            // we've loaded commands, render the first one so the panel
            // isn't blank on first activation.
            if (entries.length > 0) {
                const first = entries.find(passesQuery) ?? entries[0];
                if (first) selectedName = first.name;
            }
        }
        if (!selectedName) return; // truly empty (no commands at all)

        const entry = byName.get(selectedName);
        if (!entry) {
            m.body.innerHTML = `<p class="help-empty">Unknown command: ${escapeHtml(selectedName)}</p>`;
            return;
        }
        let html: string;
        try {
            html = marked.parse(entry.markdown, { async: false, gfm: true, breaks: false }) as string;
        } catch (e: any) {
            m.body.innerHTML = `<pre class="md-preview-error">Failed to render docs: ${escapeHtml(e?.message ?? String(e))}</pre>`;
            return;
        }
        m.body.innerHTML = scrubHtml(html);
        // Reflect the selection in the TOC highlight.
        for (const el of Array.from(m.toc.querySelectorAll<HTMLElement>('.help-toc-item'))) {
            el.classList.toggle('active', el.dataset.name === selectedName);
        }
        m.body.scrollTop = 0;
    }

    function selectCommand(name: string, scrollIntoView: boolean): boolean {
        if (!byName.has(name)) return false;
        selectedName = name;
        renderBody();
        if (scrollIntoView) {
            const tocEl = m.toc.querySelector<HTMLElement>(
                `.help-toc-item[data-name="${CSS.escape(name)}"]`,
            );
            tocEl?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
        }
        return true;
    }

    function setEntries(next: CommandDocEntry[]) {
        entries = next;
        byName = new Map(next.map((e) => [e.name, e]));
        // Preserve selection if still valid.
        if (selectedName && !byName.has(selectedName)) selectedName = null;
        renderToc();
        renderBody();
    }

    m.search.addEventListener('input', () => {
        query = m.search.value;
        m.searchClear.hidden = !query;
        renderToc();
    });
    m.search.addEventListener('search', () => {
        query = m.search.value;
        m.searchClear.hidden = !query;
        renderToc();
    });
    m.searchClear.addEventListener('click', () => {
        m.search.value = '';
        query = '';
        m.searchClear.hidden = true;
        renderToc();
        m.search.focus();
    });

    return {
        setEntries,
        selectCommand: (name) => selectCommand(name, true),
        getQuery: () => query,
    };
}
