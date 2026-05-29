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

/** Optional services the help panel uses if provided. Today: a tokenizer
 *  hook the panel calls to syntax-highlight Fade code blocks in rendered
 *  markdown. Without it, code blocks render as plain monospace text. */
export interface HelpServices {
    /** Asynchronously classify a Fade snippet. Wired from main.ts to
     *  FadeRunner.tokenizeSnippet (which hits the LSP worker). */
    tokenizeSnippet?: (source: string) => Promise<HelpSnippetToken[]>;
}

interface Mounted {
    toc: HTMLElement;
    body: HTMLElement;
    search: HTMLInputElement;
    searchClear: HTMLButtonElement;
    searchResults: HTMLElement;
    tabs: HTMLElement;
    pane: HTMLElement;
    split: HTMLElement;
    splitHandle: HTMLElement;
}

const HELP_TOC_WIDTH_STORAGE_KEY = 'fade.helpTocWidth';
const HELP_TOC_WIDTH_MIN = 120;
/** Leave at least this much room for the body, no matter how wide the
 *  panel is. The body becomes useless below ~200px. */
const HELP_BODY_WIDTH_MIN = 200;

// Keyword-based bucketing for MonoGame commands. FadeMonoGameCommands is
// a single partial class spanning ~15 files (SpriteCommands.cs, TextureCommands.cs, …),
// and the metadata blob doesn't track which file each method came from —
// so we recover the buckets here by matching on the command's call name.
//
// Two rule kinds, evaluated in order:
//   - exclusive: first hit wins, no further rules considered. Reserved
//                for buckets whose commands shouldn't multi-tag (debug
//                widgets, input/math primitives, macro-time asset ops).
//   - multi-tag: a command can land in any/all matching buckets. Used
//                for the spatial/visual buckets where commands like
//                "attach sprite to transform" genuinely belong to two.
//
// Anything that matches no rule falls into 'Other'. With the rule set
// below, this should be empty in practice — keep it as a safety net.
interface GroupRule {
    name: string;
    /** True ⇒ skip evaluating remaining rules once this one matches. */
    exclusive?: boolean;
    /** Tested against the lower-cased call name. */
    match: (lowered: string) => boolean;
}

// Tiny helper: any-of-these-substrings on the lower-cased name. Used by
// most of the buckets where membership is a name-substring relationship.
function hasAny(...needles: string[]) {
    return (n: string) => needles.some(x => n.includes(x));
}

const MONOGAME_GROUP_RULES: GroupRule[] = [
    // ── exclusive (claim outright) ─────────────────────────────────────
    {
        name: 'Debug',
        exclusive: true,
        match: n =>
            n.startsWith('debug ') || n === 'debug'
            || n.startsWith('begin debug') || n.startsWith('end debug')
            || n.startsWith('disable debug') || n.startsWith('enable debug'),
    },
    {
        name: 'Input',
        exclusive: true,
        // Names without explicit "input": downkey, leftKey, spaceKey, mouse x,
        // left click, key down, scanCode, new {downkey/leftKey/…}, etc.
        match: n => /\bkey\b/.test(n) || /\bmouse\b/.test(n) || /\bclick\b/.test(n)
                 || /key$/.test(n) || /code$/.test(n) || n.startsWith('new '),
    },
    {
        name: 'Math',
        exclusive: true,
        // The MathCommands.cs primitives are short, distinct, and don't
        // need substring matching — list them.
        match: n => ['sin', 'cos', 'tan', 'atan', 'atan2', 'deg', 'rad', 'sqrt'].includes(n),
    },
    {
        name: 'Asset (macro)',
        exclusive: true,
        match: hasAny('push asset', 'rename asset'),
    },
    // ── multi-tag ─────────────────────────────────────────────────────
    { name: 'Sprite',    match: hasAny('sprite') },
    { name: 'Texture',   match: n => n.includes('texture') || n === 'font' },
    {
        name: 'Render',
        match: hasAny(
            'render target', 'render width', 'render height', 'render size',
            'effect', 'background color',
            'screen effect', 'screen shake', 'stage sampler', 'screenshot',
        ),
    },
    {
        name: 'Screen',
        match: hasAny(
            'screen size', 'screen width', 'screen height',
            'fullscreen', 'display ', 'window title', 'is os',
        ),
    },
    { name: 'Audio',     match: hasAny('sfx') },
    { name: 'Transform', match: hasAny('transform') },
    { name: 'Collision', match: hasAny('collider', 'collision') },
    // /\btext\b/ avoids accidental matches inside 'texture'. 'drop shadow'
    // is a multi-word substring with no collision risk.
    { name: 'Text',      match: n => /\btext\b/.test(n) || n.includes('drop shadow') },
    { name: 'Tween',     match: hasAny('tween') },
    {
        name: 'Core',
        match: n => ['sync', 'set sync rate', 'frame number', 'game ms', 'print'].includes(n),
    },
];

// Returns the set of TOC sections this entry belongs to. For non-MonoGame
// libraries the server-emitted `group` passes through unchanged (so
// Standard stays "Standard", custom DLLs keep whatever class label
// FadeBridge computed). For MonoGame, the rule list above subdivides
// the 213-entry bucket.
export function groupsForEntry(entry: CommandDocEntry): string[] {
    if (entry.group !== 'FadeMonoGame') {
        return [entry.group || 'Core'];
    }
    const lowered = entry.name.toLowerCase();
    const hits: string[] = [];
    for (const rule of MONOGAME_GROUP_RULES) {
        if (!rule.match(lowered)) continue;
        if (rule.exclusive) return [rule.name];
        hits.push(rule.name);
    }
    return hits.length > 0 ? hits : ['Other'];
}

// One row in the global-search dropdown. `navigate` does the right
// thing for whatever kind of hit this is (command vs static-doc section)
// so the click handler doesn't need to know the kind.
interface SearchResult {
    badge: string;        // "Commands" / "Language" / "Playground"
    title: string;        // command name or section title
    snippetHtml: string;  // already-escaped + <mark>-wrapped HTML
    navigate: () => void;
}

interface ScoredResult {
    score: number; // lower is better
    result: SearchResult;
}

// Tabs surface different doc bodies inside the same help panel:
//   - 'commands': the LSP-derived command catalog (existing behavior).
//   - 'language' / 'playground': static markdown fetched from /docs/.
// New tabs slot in by extending this union + STATIC_DOCS.
type Tab = 'commands' | 'language' | 'playground';

interface StaticDocConfig {
    /** Fetched lazily on first activation; cached for the session. */
    url: string;
    /** Human-readable label shown in the empty/error state. */
    label: string;
}

const STATIC_DOCS: Record<Exclude<Tab, 'commands'>, StaticDocConfig> = {
    language:   { url: '/docs/Language.md',   label: 'Language reference' },
    playground: { url: '/docs/Playground.md', label: 'Playground guide' },
};

// Per-token classification returned by FadeBridge.LspTokenizeSnippet. The
// `type` field is the index into SemanticTokensHandler.Legend
// (0=comment, 1=keyword, 2=function, 3=method, 4=macro, 5=parameter,
// 6=struct, 7=type, 8=operator, 9=number, 10=string). Coordinates are
// 0-based to match the LSP-side handler's output.
export interface HelpSnippetToken {
    line: number;
    col: number;
    length: number;
    type: number;
}

// Static docs are sliced into discrete sections so the TOC behaves like
// Commands (click → swap body), not a long-scroll outline. We split on
// H2 boundaries when any H2 exists, otherwise H1. Section 0 collects
// anything before the first boundary heading (typically the H1 intro).
export interface DocSubheading {
    /** Slug we'll write onto the `<h3>` / `<h4>` element so the TOC sub-item
     *  can scroll to it via getElementById within the rendered section. */
    slug: string;
    text: string;
    /** Level relative to the section (1 = H3 if H2s are page boundaries;
     *  2 = H4; etc.). Used to indent the TOC entry. */
    depth: number;
}

export interface DocSection {
    slug: string;
    title: string;
    /** Markdown for just this page, including its own heading line. */
    body: string;
    /** Headings nested under this section (H3+ when H2 is the boundary
     *  level, H2+ when H1 is). Rendered as indented TOC entries when the
     *  section is active. */
    subs: DocSubheading[];
}

interface StaticDocState {
    /** undefined = not fetched yet; [] = fetch failed or empty doc. */
    sections: DocSection[] | undefined;
    /** True after a fetch attempt that didn't return a usable doc. Lets
     *  the renderer distinguish "still loading" from "loaded, no content". */
    failed: boolean;
    /** Slug of the currently displayed section. null = first section. */
    selectedSlug: string | null;
    /** Slug of the currently-active sub-heading within the displayed
     *  section, if any. Cleared whenever selectedSlug changes. */
    selectedSubSlug: string | null;
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

export function mountHelpPanel(services: HelpServices = {}): HelpController {
    const m: Mounted = {
        toc:           document.getElementById('help-toc')!,
        body:          document.getElementById('help-body')!,
        search:        document.getElementById('help-search') as HTMLInputElement,
        searchClear:   document.getElementById('help-search-clear') as HTMLButtonElement,
        searchResults: document.getElementById('help-search-results')!,
        tabs:          document.getElementById('help-tabs')!,
        pane:          document.getElementById('help-pane')!,
        split:         document.getElementById('help-split')!,
        splitHandle:   document.getElementById('help-split-handle')!,
    };

    let activeTab: Tab = 'commands';
    let entries: CommandDocEntry[] = [];
    let byName: Map<string, CommandDocEntry> = new Map();
    let selectedName: string | null = null;
    const docs: Record<Exclude<Tab, 'commands'>, StaticDocState> = {
        language:   { sections: undefined, failed: false, selectedSlug: null, selectedSubSlug: null },
        playground: { sections: undefined, failed: false, selectedSlug: null, selectedSubSlug: null },
    };
    // Collapsible-TOC state. All parents start collapsed; the user expands
    // what they want to browse. A live search query bypasses these so
    // filtered hits are always visible regardless of expansion.
    const expandedCommandGroups = new Set<string>();
    const expandedDocSections: Record<Exclude<Tab, 'commands'>, Set<string>> = {
        language: new Set(),
        playground: new Set(),
    };

    function renderToc() {
        m.toc.innerHTML = '';
        if (activeTab === 'commands') { renderCommandsToc(); return; }
        renderDocToc(activeTab);
    }

    function renderCommandsToc() {
        if (entries.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'help-toc-empty';
            empty.textContent = 'No commands loaded.';
            m.toc.append(empty);
            return;
        }
        // Bucket entries by their group set. MonoGame commands go through
        // groupsForEntry (keyword rules) and may appear in multiple
        // buckets ("attach sprite to transform" → Sprite + Transform);
        // other libraries pass through with their server-emitted group.
        const grouped = new Map<string, CommandDocEntry[]>();
        for (const e of entries) {
            for (const g of groupsForEntry(e)) {
                const arr = grouped.get(g) ?? [];
                arr.push(e);
                grouped.set(g, arr);
            }
        }
        // Sort groups by size descending so the biggest surfaces first —
        // feels more like a glossary than a phone book. Within a tie,
        // sort alphabetically; pin 'Other' to the bottom so the catch-all
        // doesn't outrank real buckets when they're a similar size.
        const sortedGroups = Array.from(grouped.entries()).sort((a, b) => {
            if (a[0] === 'Other' && b[0] !== 'Other') return 1;
            if (b[0] === 'Other' && a[0] !== 'Other') return -1;
            return b[1].length - a[1].length || a[0].localeCompare(b[0]);
        });
        for (const [groupName, items] of sortedGroups) {
            const expanded = expandedCommandGroups.has(groupName);
            const heading = document.createElement('div');
            heading.className = 'help-toc-group help-toc-collapsible' + (expanded ? ' expanded' : '');
            heading.dataset.group = groupName;
            heading.innerHTML = `<span class="help-toc-chevron"></span><span class="help-toc-group-label">${escapeHtml(groupName)}</span><span class="help-toc-group-count">${items.length}</span>`;
            heading.onclick = () => toggleCommandGroup(groupName);
            m.toc.append(heading);
            if (!expanded) continue;
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

    function toggleCommandGroup(name: string): void {
        if (expandedCommandGroups.has(name)) expandedCommandGroups.delete(name);
        else expandedCommandGroups.add(name);
        renderToc();
    }

    function renderDocToc(tab: Exclude<Tab, 'commands'>) {
        const state = docs[tab];
        if (state.sections === undefined) {
            const loading = document.createElement('div');
            loading.className = 'help-toc-empty';
            loading.textContent = 'Loading…';
            m.toc.append(loading);
            return;
        }
        if (state.failed || state.sections.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'help-toc-empty';
            empty.textContent = state.failed
                ? `Couldn't load ${STATIC_DOCS[tab].label}.`
                : 'No sections.';
            m.toc.append(empty);
            return;
        }
        const activeSlug = state.selectedSlug ?? state.sections[0].slug;
        const activeSubSlug = state.selectedSubSlug;
        const expandedSet = expandedDocSections[tab];
        // Search isn't wired up for static docs, but if we ever add a
        // filter here the same force-expand-on-query logic should apply.
        for (const section of state.sections) {
            const hasSubs = section.subs.length > 0;
            const expanded = expandedSet.has(section.slug);
            const link = document.createElement('a');
            const isActive = section.slug === activeSlug;
            link.className = 'help-toc-item help-toc-collapsible'
                + (hasSubs ? '' : ' help-toc-leaf')
                + (expanded ? ' expanded' : '')
                + (isActive && !activeSubSlug ? ' active' : '');
            link.dataset.slug = section.slug;
            // Chevron span renders even when there are no subs (it's
            // styled invisible via .help-toc-leaf) to keep titles aligned
            // across the whole list.
            link.innerHTML = `<span class="help-toc-chevron"></span><span class="help-toc-section-label">${escapeHtml(section.title)}</span>`;
            link.onclick = (ev) => {
                ev.preventDefault();
                // Single click does both: switch the body to this page
                // AND toggle expansion. Toggling without selecting felt
                // odd because clicking a section is "I want to look at
                // this section's stuff" — opening it is the natural
                // companion action.
                if (hasSubs) toggleDocSection(tab, section.slug);
                selectDocSection(tab, section.slug);
            };
            m.toc.append(link);
            if (!expanded) continue;
            for (const sub of section.subs) {
                const subLink = document.createElement('a');
                subLink.className = 'help-toc-item help-toc-sub'
                    + (sub.slug === activeSubSlug ? ' active' : '');
                subLink.dataset.slug = section.slug;
                subLink.dataset.subSlug = sub.slug;
                subLink.textContent = sub.text;
                // All subs share the same indent (2.85rem — the same one
                // .help-toc-item uses for Commands children). We tried
                // depth-based stepping but it made H4 subs in Language.md
                // sit 0.8rem deeper than H3 subs from any other doc, even
                // when the H4 was effectively a first-level child of its
                // H2 section. A flat indent keeps Commands children and
                // doc subs visually aligned across both tabs.
                subLink.style.paddingLeft = '2.85rem';
                subLink.onclick = (ev) => {
                    ev.preventDefault();
                    selectDocSubheading(tab, section.slug, sub.slug);
                };
                m.toc.append(subLink);
            }
        }
    }

    function toggleDocSection(tab: Exclude<Tab, 'commands'>, slug: string): void {
        const set = expandedDocSections[tab];
        if (set.has(slug)) set.delete(slug);
        else set.add(slug);
        // Don't re-render here — the caller (the click handler) follows
        // up with selectDocSection which re-renders both panes.
    }

    function renderBody() {
        if (activeTab === 'commands') { renderCommandsBody(); return; }
        renderDocBody(activeTab);
    }

    function renderCommandsBody() {
        if (!selectedName) {
            // Keep the static placeholder that lives in index.html for
            // a clean reset — but only when there are no entries; once
            // we've loaded commands, render the first one so the panel
            // isn't blank on first activation.
            if (entries.length > 0) {
                selectedName = entries[0].name;
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
        if (services.tokenizeSnippet) {
            // The very first <h3> is the command name itself; the rest are
            // section headers ("Parameters", "Returns", …). We classify
            // it through the LSP so the title visually matches how the
            // command appears in source — e.g. `print` is colored like a
            // function/method, `wait ms` like a multi-word command.
            const titleEl = m.body.querySelector<HTMLElement>('h3');
            if (titleEl) titleEl.classList.add('help-command-title');
            if (titleEl) void highlightTitleElement(titleEl, services.tokenizeSnippet);
            void highlightFadeCodeBlocks(m.body, services.tokenizeSnippet);
        }
        // Reflect the selection in the TOC highlight.
        for (const el of Array.from(m.toc.querySelectorAll<HTMLElement>('.help-toc-item'))) {
            el.classList.toggle('active', el.dataset.name === selectedName);
        }
        m.body.scrollTop = 0;
    }

    function renderDocBody(tab: Exclude<Tab, 'commands'>) {
        const state = docs[tab];
        const cfg = STATIC_DOCS[tab];
        if (state.sections === undefined) {
            m.body.innerHTML = `<p class="help-empty">Loading ${escapeHtml(cfg.label)}…</p>`;
            return;
        }
        if (state.failed || state.sections.length === 0) {
            m.body.innerHTML = state.failed
                ? `<p class="help-empty">Couldn't load ${escapeHtml(cfg.label)} from <code>${escapeHtml(cfg.url)}</code>.</p>`
                : `<p class="help-empty">No content.</p>`;
            return;
        }
        // Pick the section to render. Falling back to the first section
        // keeps the panel populated when nothing was clicked yet (or when
        // a previously-selected slug no longer exists after a reload).
        const target = state.sections.find(s => s.slug === state.selectedSlug) ?? state.sections[0];
        let html: string;
        try {
            html = marked.parse(target.body, { async: false, gfm: true, breaks: false }) as string;
        } catch (e: any) {
            m.body.innerHTML = `<pre class="md-preview-error">Failed to render docs: ${escapeHtml(e?.message ?? String(e))}</pre>`;
            return;
        }
        // Tag <h3>/<h4> with the slugs we computed during parseDocSections
        // so the TOC's sub-items can scroll to them via plain offsetTop
        // arithmetic (no scrollIntoView — that one bubbled).
        const withAnchors = injectSubAnchors(scrubHtml(html), target.subs);
        m.body.innerHTML = withAnchors;
        // Then run syntax highlighting on any ```fade``` (or unspecified-
        // language) code blocks, async — `marked` runs synchronously so
        // we drop in placeholders first, then upgrade in place. Whole
        // block is best-effort: no tokenizer = blocks stay plain text.
        if (services.tokenizeSnippet) {
            void highlightFadeCodeBlocks(m.body, services.tokenizeSnippet);
        }
        // Default: top of the section. If a sub-anchor is active, scroll
        // to it manually — never call scrollIntoView (which can bubble up
        // and move the panel's outer scroller past its content).
        m.body.scrollTop = 0;
        if (state.selectedSubSlug) {
            const anchor = m.body.querySelector<HTMLElement>(`[data-sub-slug="${state.selectedSubSlug.replace(/"/g, '\\"')}"]`);
            if (anchor) m.body.scrollTop = Math.max(0, anchor.offsetTop - 8);
        }
        // Reflect selection in the TOC — sub-active state wins when set,
        // otherwise the section row itself.
        for (const el of Array.from(m.toc.querySelectorAll<HTMLElement>('.help-toc-item'))) {
            if (state.selectedSubSlug) {
                el.classList.toggle('active', el.dataset.subSlug === state.selectedSubSlug);
            } else {
                el.classList.toggle('active', el.dataset.slug === target.slug && !el.dataset.subSlug);
            }
        }
    }

    function selectCommand(name: string, scrollIntoView: boolean): boolean {
        const entry = byName.get(name);
        if (!entry) return false;
        // Cross-tab nav: clicking a #fade-cmd: link from inside a static
        // doc should also jump back to the Commands tab.
        if (activeTab !== 'commands') {
            switchTab('commands');
        }
        // Make sure at least one of this command's groups is expanded,
        // otherwise the TOC item we're about to scrollIntoView is hidden
        // behind a collapsed parent. We expand all of them when the
        // command multi-tags — feels less arbitrary than picking "first".
        for (const g of groupsForEntry(entry)) expandedCommandGroups.add(g);
        selectedName = name;
        renderToc();
        renderBody();
        if (scrollIntoView) {
            const tocEl = m.toc.querySelector<HTMLElement>(
                `.help-toc-item[data-name="${CSS.escape(name)}"]`,
            );
            tocEl?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
        }
        return true;
    }

    function selectDocSection(tab: Exclude<Tab, 'commands'>, slug: string): void {
        const state = docs[tab];
        state.selectedSlug = slug;
        state.selectedSubSlug = null; // page change clears any in-page anchor
        // Re-render TOC to expand/collapse sub-items under the new active
        // section, then the body. No scrollIntoView — keeps us safe from
        // the bubbling-outer-scroll bug.
        renderToc();
        renderBody();
    }

    function selectDocSubheading(tab: Exclude<Tab, 'commands'>, sectionSlug: string, subSlug: string): void {
        const state = docs[tab];
        // Make sure the parent is expanded so the sub-item the user
        // landed on is visible in the TOC after we re-render.
        expandedDocSections[tab].add(sectionSlug);
        // If clicked from outside this section's expanded sub-items (rare:
        // would need a stale handle), re-activate that section first so
        // renderBody emits the right markdown.
        if (state.selectedSlug !== sectionSlug) {
            state.selectedSlug = sectionSlug;
        }
        state.selectedSubSlug = subSlug;
        renderToc();
        renderBody();
    }

    async function ensureDocLoaded(tab: Exclude<Tab, 'commands'>): Promise<void> {
        const state = docs[tab];
        if (state.sections !== undefined) return; // already loaded (or failed once — don't retry on tab toggles)
        const cfg = STATIC_DOCS[tab];
        try {
            const res = await fetch(cfg.url, { cache: 'no-cache' });
            if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
            const text = await res.text();
            state.sections = parseDocSections(text);
            state.failed = false;
        } catch (e) {
            console.warn(`[help] failed to load ${cfg.url}:`, e);
            state.sections = [];
            state.failed = true;
        }
        if (activeTab === tab) {
            renderToc();
            renderBody();
        }
    }

    function initHelpSplitter(): void {
        let dragging = false;
        // panelLeft is captured at mousedown so we don't read it 60 times
        // a second during the drag. Same for panelWidth — the panel can't
        // resize while the user is dragging the inner handle. Declared
        // BEFORE the restore call below because applyTocWidth reads
        // panelWidth (temporal-dead-zone bug otherwise).
        let panelLeft = 0;
        let panelWidth = 0;

        // Restore last-used width. localStorage stores px as a number;
        // we re-clamp to the current panel width on mount because the
        // panel can be narrower than it was last time (e.g. user moved
        // the dockview divider).
        const stored = readStoredTocWidth();
        if (stored !== null) applyTocWidth(stored, /*persist*/ false);

        const onMove = (ev: MouseEvent) => {
            if (!dragging) return;
            const x = ev.clientX - panelLeft;
            applyTocWidth(x, /*persist*/ false);
        };
        const onUp = () => {
            if (!dragging) return;
            dragging = false;
            m.splitHandle.classList.remove('dragging');
            m.pane.classList.remove('help-resizing');
            window.removeEventListener('mousemove', onMove);
            window.removeEventListener('mouseup', onUp);
            // Persist the final value (mid-drag updates skip storage to
            // avoid hammering localStorage during a fast drag).
            persistTocWidth();
        };

        m.splitHandle.addEventListener('mousedown', (ev) => {
            ev.preventDefault();
            const rect = m.split.getBoundingClientRect();
            panelLeft = rect.left;
            panelWidth = rect.width;
            dragging = true;
            m.splitHandle.classList.add('dragging');
            m.pane.classList.add('help-resizing');
            window.addEventListener('mousemove', onMove);
            window.addEventListener('mouseup', onUp);
        });

        // Double-click resets to the default width — same shortcut Monaco
        // and most IDE splitters use.
        m.splitHandle.addEventListener('dblclick', () => {
            applyTocWidth(220, /*persist*/ true);
        });

        function applyTocWidth(px: number, persist: boolean): void {
            const max = Math.max(HELP_TOC_WIDTH_MIN, panelWidth - HELP_BODY_WIDTH_MIN);
            // If the drag started before panelWidth was captured (only
            // possible via the restore path), fall back to a generous max.
            const upper = panelWidth > 0 ? max : 9999;
            const clamped = Math.round(Math.min(Math.max(px, HELP_TOC_WIDTH_MIN), upper));
            m.split.style.setProperty('--help-toc-width', `${clamped}px`);
            if (persist) {
                try { localStorage.setItem(HELP_TOC_WIDTH_STORAGE_KEY, String(clamped)); }
                catch { /* private mode or storage full — ignore */ }
            }
        }

        function persistTocWidth(): void {
            const current = m.split.style.getPropertyValue('--help-toc-width').trim();
            if (current) {
                try { localStorage.setItem(HELP_TOC_WIDTH_STORAGE_KEY, current.replace('px', '')); }
                catch { /* ignore */ }
            }
        }

        function readStoredTocWidth(): number | null {
            try {
                const raw = localStorage.getItem(HELP_TOC_WIDTH_STORAGE_KEY);
                if (!raw) return null;
                const n = Number(raw);
                return Number.isFinite(n) && n > 0 ? n : null;
            } catch { return null; }
        }
    }

    function switchTab(tab: Tab): void {
        if (tab === activeTab) return;
        activeTab = tab;
        for (const btn of Array.from(m.tabs.querySelectorAll<HTMLButtonElement>('.help-tab'))) {
            const isActive = btn.dataset.tab === tab;
            btn.classList.toggle('active', isActive);
            btn.setAttribute('aria-selected', String(isActive));
        }
        if (tab !== 'commands') {
            void ensureDocLoaded(tab);
        }
        renderToc();
        renderBody();
    }

    function setEntries(next: CommandDocEntry[]) {
        entries = next;
        byName = new Map(next.map((e) => [e.name, e]));
        // Preserve selection if still valid.
        if (selectedName && !byName.has(selectedName)) selectedName = null;
        renderToc();
        renderBody();
    }

    // Intercept clicks on cross-command links rendered into the body.
    // <see cref="X"/> in XML docs becomes `[label](#fade-cmd:<callName>)`
    // (see ProjectDocs.cs ResolveSeeRef) — we route those to selectCommand
    // instead of letting the browser navigate. `#…` URLs are same-page-only
    // so a missed-intercept fallback (middle-click, ctrl-click) just lands
    // on the index with a stray hash, never a 404.
    m.body.addEventListener('click', (ev) => {
        const target = ev.target as HTMLElement | null;
        if (!target) return;
        const link = target.closest('a') as HTMLAnchorElement | null;
        if (!link) return;
        const href = link.getAttribute('href') ?? '';
        const prefix = '#fade-cmd:';
        if (!href.startsWith(prefix)) return;
        ev.preventDefault();
        const name = decodeURIComponent(href.slice(prefix.length));
        selectCommand(name, /*scrollIntoView*/ true);
    });

    // Tabs row at the top of the panel switches between Commands (the
    // LSP-backed catalog) and the static markdown surfaces (Language,
    // Playground). Static surfaces fetch + cache on first activation.
    for (const btn of Array.from(m.tabs.querySelectorAll<HTMLButtonElement>('.help-tab'))) {
        btn.addEventListener('click', () => {
            const tab = btn.dataset.tab as Tab | undefined;
            if (tab) switchTab(tab);
        });
    }

    // TOC ↔ body splitter. Drag the slim handle between them to resize
    // the TOC column. Width is clamped to the panel's own bounds so the
    // body always keeps a usable strip, and persisted to localStorage so
    // the user's preferred width survives reloads.
    initHelpSplitter();

    // Pre-load the static docs so the global search dropdown can hit
    // them before the user has visited those tabs. Cheap — both files
    // are tens of KB and we fetch once per session.
    void ensureDocLoaded('language');
    void ensureDocLoaded('playground');

    // Global search wiring lives at the bottom so all the closures it
    // depends on (selectCommand, selectDocSection, expansion state,
    // entries/docs) are already defined.
    initGlobalSearch();

    return {
        setEntries,
        selectCommand: (name) => selectCommand(name, true),
        getQuery: () => m.search.value,
    };

    // ── global search ───────────────────────────────────────────────────
    function initGlobalSearch(): void {
        let results: SearchResult[] = [];
        let activeIdx = -1;

        const renderResults = () => {
            m.searchResults.innerHTML = '';
            if (results.length === 0) {
                if (m.search.value.trim().length > 0) {
                    const empty = document.createElement('div');
                    empty.className = 'help-search-results-empty';
                    empty.textContent = `No matches for "${m.search.value}".`;
                    m.searchResults.append(empty);
                    m.searchResults.hidden = false;
                } else {
                    m.searchResults.hidden = true;
                }
                return;
            }
            results.forEach((r, i) => {
                const row = document.createElement('div');
                row.className = 'help-search-result' + (i === activeIdx ? ' active' : '');
                row.setAttribute('role', 'option');
                row.innerHTML =
                    `<div class="help-search-result-head">`
                    + `<span class="help-search-result-badge">${escapeHtml(r.badge)}</span>`
                    + `<span class="help-search-result-title">${escapeHtml(r.title)}</span>`
                    + `</div>`
                    + `<div class="help-search-result-snippet">${r.snippetHtml}</div>`;
                row.onmousedown = (ev) => {
                    ev.preventDefault(); // keep focus on input until we navigate
                    activate(i);
                };
                m.searchResults.append(row);
            });
            m.searchResults.hidden = false;
        };

        const activate = (i: number) => {
            const r = results[i];
            if (!r) return;
            r.navigate();
            closeResults();
        };

        const closeResults = () => {
            results = [];
            activeIdx = -1;
            m.searchResults.hidden = true;
            m.searchResults.innerHTML = '';
            m.search.value = '';
            m.searchClear.hidden = true;
        };

        const recompute = () => {
            const q = m.search.value.trim();
            m.searchClear.hidden = q.length === 0;
            if (q.length === 0) {
                results = [];
                activeIdx = -1;
                m.searchResults.hidden = true;
                m.searchResults.innerHTML = '';
                return;
            }
            results = searchAll(q);
            activeIdx = results.length > 0 ? 0 : -1;
            renderResults();
        };

        m.search.addEventListener('input', recompute);
        // Native HTML5 search input also fires a 'search' event on Esc /
        // clear-button. Keep behavior consistent.
        m.search.addEventListener('search', recompute);
        m.searchClear.addEventListener('click', () => {
            closeResults();
            m.search.focus();
        });
        m.search.addEventListener('keydown', (ev) => {
            if (results.length === 0 && ev.key !== 'Escape') return;
            if (ev.key === 'ArrowDown') {
                ev.preventDefault();
                activeIdx = Math.min(activeIdx + 1, results.length - 1);
                renderResults();
            } else if (ev.key === 'ArrowUp') {
                ev.preventDefault();
                activeIdx = Math.max(activeIdx - 1, 0);
                renderResults();
            } else if (ev.key === 'Enter') {
                ev.preventDefault();
                if (activeIdx >= 0) activate(activeIdx);
            } else if (ev.key === 'Escape') {
                ev.preventDefault();
                closeResults();
                m.search.blur();
            }
        });
        // Click anywhere outside the dropdown closes it (the bar itself
        // stays — only the suggestions disappear). Keeps the bar visible
        // while letting the user click into the TOC to navigate manually.
        document.addEventListener('mousedown', (ev) => {
            const t = ev.target as Node | null;
            if (!t) return;
            if (m.searchResults.contains(t) || m.search.contains(t)) return;
            if (!m.searchResults.hidden) {
                m.searchResults.hidden = true;
            }
        });
        // Re-show the dropdown on focus when there's still a query.
        m.search.addEventListener('focus', () => {
            if (m.search.value.trim().length > 0 && results.length > 0) {
                m.searchResults.hidden = false;
            }
        });
    }

    // Build a unified, ranked result set from the Commands catalog and
    // every loaded static doc. Each result carries a `navigate` callback
    // so the dropdown handler doesn't need to branch on result kind.
    function searchAll(query: string): SearchResult[] {
        const q = query.toLowerCase();
        const hits: ScoredResult[] = [];

        // Commands — match against name (high weight) and markdown body.
        for (const e of entries) {
            const nameIdx = e.name.toLowerCase().indexOf(q);
            const bodyIdx = e.markdown.toLowerCase().indexOf(q);
            if (nameIdx < 0 && bodyIdx < 0) continue;
            // Score: title hits beat body hits; earlier offset beats later.
            const score = nameIdx >= 0
                ? 0 + nameIdx * 0.001
                : 1000 + Math.min(bodyIdx, 2000) * 0.001;
            const snippetSource = nameIdx >= 0 ? e.markdown : e.markdown;
            const snippetIdx = nameIdx >= 0 ? snippetSource.toLowerCase().indexOf(q) : bodyIdx;
            hits.push({
                score,
                result: {
                    badge: 'Commands',
                    title: e.name,
                    snippetHtml: buildSnippet(snippetSource, snippetIdx, q.length),
                    navigate: () => selectCommand(e.name, /*scrollIntoView*/ true),
                },
            });
        }

        // Static docs — match against each section's title + body.
        for (const tab of ['language', 'playground'] as const) {
            const sections = docs[tab].sections;
            if (!sections) continue;
            const tabLabel = tab === 'language' ? 'Language' : 'Playground';
            for (const section of sections) {
                const titleIdx = section.title.toLowerCase().indexOf(q);
                const bodyIdx = section.body.toLowerCase().indexOf(q);
                if (titleIdx < 0 && bodyIdx < 0) continue;
                const score = titleIdx >= 0
                    ? 0 + titleIdx * 0.001
                    : 1000 + Math.min(bodyIdx, 2000) * 0.001;
                hits.push({
                    score,
                    result: {
                        badge: tabLabel,
                        title: section.title,
                        snippetHtml: buildSnippet(section.body, titleIdx >= 0 ? bodyIdx >= 0 ? bodyIdx : 0 : bodyIdx, q.length),
                        navigate: () => {
                            switchTab(tab);
                            // selectDocSection re-renders and resets to top.
                            selectDocSection(tab, section.slug);
                            // Also expand the section so its subs show in TOC.
                            expandedDocSections[tab].add(section.slug);
                            renderToc();
                        },
                    },
                });
            }
        }

        hits.sort((a, b) => a.score - b.score);
        return hits.slice(0, 12).map(h => h.result);
    }
}

// ───────────────────────────── helpers ─────────────────────────────

// Pull a small window of context around the matched substring and wrap
// the match in <mark>. The window is sized so two-line clamping in the
// CSS still shows real text on both sides. `matchIdx < 0` (no hit in
// this source) falls back to the start of the source.
export function buildSnippet(source: string, matchIdx: number, matchLen: number): string {
    const window = 80;
    const safeIdx = matchIdx >= 0 ? matchIdx : 0;
    const hasMatch = matchIdx >= 0 && matchLen > 0;
    const start = Math.max(0, safeIdx - Math.floor(window / 2));
    const end = Math.min(source.length, safeIdx + matchLen + Math.floor(window / 2));
    let slice = source.slice(start, end);
    if (start > 0) slice = '…' + slice;
    if (end < source.length) slice = slice + '…';
    // Collapse any inner newlines so the snippet sits on its (clamped) lines.
    slice = slice.replace(/\s+/g, ' ').trim();
    if (!hasMatch) return escapeHtml(slice);
    // Re-locate the match inside the trimmed slice (positions shifted).
    const lowerSlice = slice.toLowerCase();
    const lowerMatch = source.slice(matchIdx, matchIdx + matchLen).toLowerCase();
    const idx = lowerSlice.indexOf(lowerMatch);
    if (idx < 0) return escapeHtml(slice);
    return escapeHtml(slice.slice(0, idx))
         + `<mark>${escapeHtml(slice.slice(idx, idx + matchLen))}</mark>`
         + escapeHtml(slice.slice(idx + matchLen));
}


// Tag the rendered <h3>/<h4>/... elements with the sub-slugs we computed
// from the source markdown so the TOC's sub-items can find them via
// data-sub-slug. We track the IN-section ordinal: marked emits headings
// in source order, so the Nth deeper-than-boundary heading in the
// rendered HTML maps to subs[N].
function injectSubAnchors(html: string, subs: DocSubheading[]): string {
    if (subs.length === 0) return html;
    let i = 0;
    return html.replace(/<h([2-6])\b([^>]*)>/g, (full, lvl, attrs) => {
        // Skip the boundary heading itself (H1 or H2 depending on the doc).
        // We can't know the boundary level inside this helper, but the
        // subs list only contains depth >= 1 entries, so we just consume
        // the next slug whenever we see a level deep enough to plausibly
        // match. False positives are harmless because we only stop when
        // we've consumed all subs.
        if (i >= subs.length) return full;
        // Heuristic: if subs[i].depth == 1, it's the next-deeper-than-boundary
        // level. The boundary level isn't passed in but is the level
        // immediately above; we just consume in source order which matches
        // how parseDocSections walked the markdown.
        const slug = subs[i++].slug;
        return /\bdata-sub-slug\s*=/.test(attrs)
            ? `<h${lvl}${attrs}>`
            : `<h${lvl}${attrs} id="${slug}" data-sub-slug="${slug}">`;
    });
}

// Re-render a single element's text content as LSP-tokenized Fade. Used
// for the command-title <h3> at the top of each command's body so the
// name reads the same as it does in source. Best-effort: on tokenize
// failure we leave the original textContent alone.
async function highlightTitleElement(
    el: HTMLElement,
    tokenize: (source: string) => Promise<HelpSnippetToken[]>,
): Promise<void> {
    if (el.dataset.fadeHighlighted) return;
    el.dataset.fadeHighlighted = '1';
    const source = (el.textContent ?? '').trim();
    if (!source) return;
    let tokens: HelpSnippetToken[] = [];
    try { tokens = await tokenize(source); }
    catch { return; }
    el.innerHTML = renderTokenizedSnippet(source, tokens);
}

// Replace ```fade``` (and unspecified-language) <pre><code> blocks in the
// body with LSP-classified spans. Tolerant of late or failed tokenize
// calls — the block stays as plain text if the worker is slow or errors.
// Languages other than 'fade'/'basic'/empty are skipped so embedded
// ```csharp``` etc. render normally.
async function highlightFadeCodeBlocks(
    root: HTMLElement,
    tokenize: (source: string) => Promise<HelpSnippetToken[]>,
): Promise<void> {
    const blocks = Array.from(root.querySelectorAll<HTMLElement>('pre > code'));
    for (const code of blocks) {
        const langClass = Array.from(code.classList).find(c => c.startsWith('language-'));
        const lang = langClass ? langClass.slice('language-'.length).toLowerCase() : '';
        // Treat no-language and fade-flavored fences as Fade. Skip
        // explicit non-Fade languages (we don't know how to highlight them).
        if (lang && lang !== 'fade' && lang !== 'basic' && lang !== 'fbasic') continue;
        if (code.dataset.fadeHighlighted) continue;
        code.dataset.fadeHighlighted = '1';
        const source = code.textContent ?? '';
        if (!source.trim()) continue;
        let tokens: HelpSnippetToken[] = [];
        try { tokens = await tokenize(source); }
        catch { /* leave as plain text */ continue; }
        code.innerHTML = renderTokenizedSnippet(source, tokens);
    }
}

// Slice the source into spans by token (line, col, length) → CSS class.
// Anything not covered by a token stays as plain text. Newlines are
// preserved so the <pre> layout stays intact.
function renderTokenizedSnippet(source: string, tokens: HelpSnippetToken[]): string {
    if (tokens.length === 0) return escapeHtml(source);
    // Convert (line, col) to absolute string offset once per line.
    const lineStarts: number[] = [0];
    for (let i = 0; i < source.length; i++) {
        if (source.charCodeAt(i) === 10) lineStarts.push(i + 1);
    }
    interface Span { start: number; end: number; type: number; }
    const spans: Span[] = [];
    for (const t of tokens) {
        const lineStart = lineStarts[t.line];
        if (lineStart === undefined) continue;
        const start = lineStart + t.col;
        const end = start + t.length;
        if (end > source.length) continue;
        spans.push({ start, end, type: t.type });
    }
    spans.sort((a, b) => a.start - b.start);
    // Drop overlaps (later span wins): the lexer shouldn't produce them,
    // but defensive.
    const cleaned: Span[] = [];
    for (const s of spans) {
        if (cleaned.length > 0 && s.start < cleaned[cleaned.length - 1].end) continue;
        cleaned.push(s);
    }
    let out = '';
    let cursor = 0;
    for (const s of cleaned) {
        if (s.start > cursor) out += escapeHtml(source.slice(cursor, s.start));
        const cls = TOKEN_TYPE_CLASS[s.type] ?? 'fade-tok-default';
        out += `<span class="${cls}">${escapeHtml(source.slice(s.start, s.end))}</span>`;
        cursor = s.end;
    }
    if (cursor < source.length) out += escapeHtml(source.slice(cursor));
    return out;
}

// Maps SemanticTokensHandler.Legend index → CSS class. Styled in
// index.html to match the editor theme's keyword/comment/string/etc.
// colors (see .fade-tok-* rules).
const TOKEN_TYPE_CLASS: Record<number, string> = {
    0:  'fade-tok-comment',
    1:  'fade-tok-keyword',
    2:  'fade-tok-function',
    3:  'fade-tok-method',
    4:  'fade-tok-macro',
    5:  'fade-tok-parameter',
    6:  'fade-tok-struct',
    7:  'fade-tok-type',
    8:  'fade-tok-operator',
    9:  'fade-tok-number',
    10: 'fade-tok-string',
};


// Split markdown into discrete "pages" the TOC can swap between, plus the
// nested sub-headings the TOC shows under the active page. Boundary level
// is H2 when any H2 exists (typical structure: H1 intro then H2 sections);
// otherwise H1. Section 0 captures everything before the first boundary
// heading. Sub-headings are anything strictly deeper than the boundary
// level (so H3+/H4+ when H2 is the boundary, H2+ when H1 is). Headings
// inside fenced code blocks don't count.
export function parseDocSections(md: string): DocSection[] {
    const lines = md.split(/\r?\n/);

    interface HeadingPos { line: number; level: number; text: string; }
    const headings: HeadingPos[] = [];
    let inFence = false;
    let fenceMarker = '';
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        const fenceMatch = line.match(/^\s*(```+|~~~+)/);
        if (fenceMatch) {
            const marker = fenceMatch[1];
            if (!inFence) { inFence = true; fenceMarker = marker; }
            else if (line.trim().startsWith(fenceMarker)) { inFence = false; fenceMarker = ''; }
            continue;
        }
        if (inFence) continue;
        // Scan H1–H4 so we can decide boundary level AND collect sub-headings
        // beneath it in the same pass.
        const m = line.match(/^(#{1,4})\s+(.+?)\s*#*\s*$/);
        if (!m) continue;
        headings.push({ line: i, level: m[1].length, text: m[2].trim() });
    }
    if (headings.length === 0) {
        const body = md.trimEnd();
        if (!body) return [];
        return [{ slug: 'overview', title: 'Overview', body, subs: [] }];
    }

    // If any H2 exists, that's the boundary; otherwise H1. Sub-headings are
    // everything strictly deeper than the boundary level.
    const boundaryLevel = headings.some(h => h.level === 2) ? 2 : 1;
    const boundaries = headings.filter(h => h.level === boundaryLevel);

    const sections: DocSection[] = [];
    const seenSlugs = new Map<string, number>();
    const makeSlug = (text: string, fallback: string) => {
        const base = text.toLowerCase().replace(/[^\w\s-]+/g, '').trim().replace(/\s+/g, '-') || fallback;
        const n = (seenSlugs.get(base) ?? 0) + 1;
        seenSlugs.set(base, n);
        return n === 1 ? base : `${base}-${n}`;
    };
    // Sub-headings within the range [startLine, endLine) — anything with a
    // level strictly greater than the boundary level.
    const subsFor = (startLine: number, endLine: number): DocSubheading[] => {
        return headings
            .filter(h => h.level > boundaryLevel && h.line >= startLine && h.line < endLine)
            .map(h => ({
                slug: makeSlug(h.text, `sub-${h.line}`),
                text: h.text,
                depth: h.level - boundaryLevel,
            }));
    };

    const firstBoundaryLine = boundaries[0].line;
    if (firstBoundaryLine > 0) {
        const preBody = lines.slice(0, firstBoundaryLine).join('\n').trimEnd();
        if (preBody.length > 0) {
            const introH1 = headings.find(h => h.level === 1 && h.line < firstBoundaryLine);
            const title = introH1?.text ?? 'Overview';
            sections.push({
                slug: makeSlug(title, 'overview'),
                title,
                body: preBody,
                subs: subsFor(0, firstBoundaryLine),
            });
        }
    }
    for (let i = 0; i < boundaries.length; i++) {
        const start = boundaries[i].line;
        const end = i + 1 < boundaries.length ? boundaries[i + 1].line : lines.length;
        const body = lines.slice(start, end).join('\n').trimEnd();
        if (!body) continue;
        sections.push({
            slug: makeSlug(boundaries[i].text, `section-${i}`),
            title: boundaries[i].text,
            body,
            // Skip the boundary line itself; nest everything between it
            // (exclusive) and the next boundary.
            subs: subsFor(start + 1, end),
        });
    }
    return sections;
}
