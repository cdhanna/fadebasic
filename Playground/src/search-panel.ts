// Workspace-wide find-in-files. Iterates every file via OpfsWorkspace.list(),
// skips binaries, matches each line, and groups results under a collapsible
// file row (VSCode "Search" sidebar shape). Clicking a match opens the file
// and reveals the line.
//
// Search is re-run on every input/flag change after a small debounce. For
// typical workspaces (<100 small text files) a full re-scan is well under
// 200ms; we cap total matches at MAX_MATCHES_TOTAL to keep pathological
// queries (e.g. "e" with no flags) responsive.

import { isBinaryFileName } from './binary-preview';

export interface SearchWorkspace {
    list(): Promise<string[]>;
    read(path: string): Promise<string>;
}

export interface SearchOpenTarget {
    path: string;
    lineNumber: number;
    column: number;
    length: number;
}

export interface SearchPanelDeps {
    container: HTMLElement;
    workspace: SearchWorkspace;
    openMatch: (target: SearchOpenTarget) => void;
    // Glob patterns whose matches are skipped during scan. Read lazily on
    // each run so settings changes take effect on the next query without
    // re-mounting the panel.
    getExcludeGlobs?: () => string[];
}

interface LineMatch {
    line: number;     // 1-based
    column: number;   // 1-based, start of match
    length: number;
    text: string;     // full line text (trimmed for display)
    leadTrim: number; // how many chars trimmed from the left for display
}

interface FileResult {
    path: string;
    matches: LineMatch[];
    collapsed: boolean;
}

interface SearchFlags {
    caseSensitive: boolean;
    wholeWord: boolean;
    regex: boolean;
}

const MAX_MATCHES_PER_FILE = 1000;
const MAX_MATCHES_TOTAL = 5000;
const DEBOUNCE_MS = 180;
const SNIPPET_MAX_LEN = 220;

export function mountSearchPanel(deps: SearchPanelDeps): { focus(): void; dispose(): void } {
    const { container, workspace, openMatch, getExcludeGlobs } = deps;

    container.innerHTML = '';
    const root = document.createElement('div');
    root.className = 'search-pane';

    // Toolbar: input + flag toggles + summary
    const toolbar = document.createElement('div');
    toolbar.className = 'search-toolbar';

    const inputRow = document.createElement('div');
    inputRow.className = 'search-input-row';

    const input = document.createElement('input');
    input.type = 'search';
    input.placeholder = 'Search';
    input.spellcheck = false;
    input.setAttribute('aria-label', 'Search workspace');
    inputRow.appendChild(input);

    const flagBar = document.createElement('div');
    flagBar.className = 'search-flag-bar';
    const flags: SearchFlags = { caseSensitive: false, wholeWord: false, regex: false };
    const flagBtns: Record<keyof SearchFlags, HTMLButtonElement> = {} as never;
    const flagDefs: Array<{ key: keyof SearchFlags; label: string; title: string }> = [
        { key: 'caseSensitive', label: 'Aa', title: 'Match case' },
        { key: 'wholeWord',     label: 'ab', title: 'Match whole word' },
        { key: 'regex',         label: '.*', title: 'Use regular expression' },
    ];
    for (const f of flagDefs) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'search-flag';
        btn.textContent = f.label;
        btn.title = f.title;
        btn.addEventListener('click', () => {
            flags[f.key] = !flags[f.key];
            btn.classList.toggle('active', flags[f.key]);
            scheduleRun();
        });
        flagBtns[f.key] = btn;
        flagBar.appendChild(btn);
    }
    inputRow.appendChild(flagBar);
    toolbar.appendChild(inputRow);

    const summary = document.createElement('div');
    summary.className = 'search-summary';
    toolbar.appendChild(summary);

    root.appendChild(toolbar);

    const resultsEl = document.createElement('div');
    resultsEl.className = 'search-results';
    resultsEl.setAttribute('role', 'tree');
    root.appendChild(resultsEl);

    container.appendChild(root);

    let runToken = 0;
    let pendingTimer: number | undefined;
    let lastQuery = '';
    let lastResults: FileResult[] = [];

    function scheduleRun() {
        if (pendingTimer != null) window.clearTimeout(pendingTimer);
        pendingTimer = window.setTimeout(() => { void run(); }, DEBOUNCE_MS);
    }

    input.addEventListener('input', scheduleRun);
    input.addEventListener('search', scheduleRun);
    input.addEventListener('keydown', (ev) => {
        if (ev.key === 'Escape' && input.value !== '') {
            ev.preventDefault();
            input.value = '';
            scheduleRun();
        }
    });

    async function run() {
        const query = input.value;
        lastQuery = query;
        if (query.length === 0) {
            summary.textContent = '';
            resultsEl.innerHTML = '';
            lastResults = [];
            return;
        }

        const token = ++runToken;
        const matcher = buildMatcher(query, flags);
        if (!matcher) {
            summary.textContent = 'Invalid regular expression';
            resultsEl.innerHTML = '';
            lastResults = [];
            return;
        }

        summary.textContent = 'Searching…';

        let files: string[];
        try {
            files = await workspace.list();
        } catch (e) {
            summary.textContent = `Error: ${(e as Error).message}`;
            return;
        }
        if (token !== runToken) return;

        const excludes = (getExcludeGlobs?.() ?? []).map(compileGlob);
        const textFiles = files.filter((p) => {
            if (isBinaryFileName(p)) return false;
            for (const re of excludes) if (re.test(p)) return false;
            return true;
        });
        const results: FileResult[] = [];
        let totalMatches = 0;
        let stopped = false;

        for (const path of textFiles) {
            if (token !== runToken) return;
            let text: string;
            try {
                text = await workspace.read(path);
            } catch {
                continue;
            }
            const matches = matchFile(text, matcher);
            if (matches.length === 0) continue;
            results.push({ path, matches, collapsed: false });
            totalMatches += matches.length;
            if (totalMatches >= MAX_MATCHES_TOTAL) {
                stopped = true;
                break;
            }
        }

        if (token !== runToken) return;

        lastResults = results;
        renderResults(stopped);
    }

    function renderResults(truncated: boolean) {
        resultsEl.innerHTML = '';

        if (lastResults.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'search-empty';
            empty.textContent = lastQuery
                ? `No results for "${lastQuery}".`
                : '';
            resultsEl.appendChild(empty);
            summary.textContent = lastQuery ? 'No results' : '';
            return;
        }

        const totalMatches = lastResults.reduce((acc, r) => acc + r.matches.length, 0);
        const fileWord = lastResults.length === 1 ? 'file' : 'files';
        const matchWord = totalMatches === 1 ? 'result' : 'results';
        summary.textContent = `${totalMatches} ${matchWord} in ${lastResults.length} ${fileWord}`
            + (truncated ? ' (truncated)' : '');

        for (const fr of lastResults) {
            const fileRow = renderFileRow(fr);
            resultsEl.appendChild(fileRow);
        }
    }

    function renderFileRow(fr: FileResult): HTMLElement {
        const wrap = document.createElement('div');

        const head = document.createElement('div');
        head.className = 'search-file-row';
        head.setAttribute('role', 'treeitem');

        const chevron = document.createElement('span');
        chevron.className = 'file-chevron';
        chevron.textContent = fr.collapsed ? '▸' : '▾';
        head.appendChild(chevron);

        const slash = fr.path.lastIndexOf('/');
        const baseName = slash >= 0 ? fr.path.slice(slash + 1) : fr.path;
        const dir = slash >= 0 ? fr.path.slice(0, slash) : '';

        const nameEl = document.createElement('span');
        nameEl.className = 'file-name';
        nameEl.textContent = baseName;
        head.appendChild(nameEl);

        if (dir) {
            const dirEl = document.createElement('span');
            dirEl.className = 'file-dir';
            dirEl.textContent = dir;
            head.appendChild(dirEl);
        } else {
            const spacer = document.createElement('span');
            spacer.className = 'file-dir';
            head.appendChild(spacer);
        }

        const count = document.createElement('span');
        count.className = 'file-count';
        count.textContent = String(fr.matches.length);
        head.appendChild(count);

        const list = document.createElement('ul');
        list.className = 'search-match-list';
        list.hidden = fr.collapsed;

        head.addEventListener('click', () => {
            fr.collapsed = !fr.collapsed;
            chevron.textContent = fr.collapsed ? '▸' : '▾';
            list.hidden = fr.collapsed;
        });

        for (const m of fr.matches) {
            list.appendChild(renderMatchRow(fr.path, m));
        }

        wrap.appendChild(head);
        wrap.appendChild(list);
        return wrap;
    }

    function renderMatchRow(path: string, m: LineMatch): HTMLLIElement {
        const li = document.createElement('li');
        li.className = 'search-match-row';
        li.setAttribute('role', 'treeitem');

        const lineNo = document.createElement('span');
        lineNo.className = 'match-line-no';
        lineNo.textContent = String(m.line);
        li.appendChild(lineNo);

        const snippet = document.createElement('span');
        snippet.className = 'match-snippet';
        // The trimmed snippet starts `leadTrim` chars into the original line.
        // Convert the absolute (1-based) column to a snippet-relative offset.
        const offsetInSnippet = m.column - 1 - m.leadTrim;
        appendHighlighted(snippet, m.text, offsetInSnippet, m.length);
        li.appendChild(snippet);

        li.addEventListener('click', () => {
            openMatch({
                path,
                lineNumber: m.line,
                column: m.column,
                length: m.length,
            });
        });
        return li;
    }

    return {
        focus() { input.focus(); input.select(); },
        dispose() {
            if (pendingTimer != null) window.clearTimeout(pendingTimer);
            runToken++; // invalidate any in-flight scan
        },
    };
}

// ─── Matching ──────────────────────────────────────────────────────────────

interface CompiledMatcher {
    // Returns all matches in a single line. Each result is [columnStart, length].
    // Column is 1-based to match Monaco's IPosition.
    matchLine(line: string): Array<[number, number]>;
}

function buildMatcher(query: string, flags: SearchFlags): CompiledMatcher | null {
    let pattern: string;
    if (flags.regex) {
        pattern = query;
    } else {
        pattern = escapeRegex(query);
    }
    if (flags.wholeWord) {
        pattern = `\\b(?:${pattern})\\b`;
    }
    let regexFlags = 'g';
    if (!flags.caseSensitive) regexFlags += 'i';

    let re: RegExp;
    try {
        re = new RegExp(pattern, regexFlags);
    } catch {
        return null;
    }

    return {
        matchLine(line: string) {
            const out: Array<[number, number]> = [];
            // Reset lastIndex each call — single shared regex is safe because
            // matchLine fully drains it before returning.
            re.lastIndex = 0;
            let safety = 0;
            // eslint-disable-next-line no-constant-condition
            while (true) {
                const m = re.exec(line);
                if (!m) break;
                const matchLen = m[0].length;
                // Empty matches (e.g. `.*` with global) would loop forever —
                // advance lastIndex manually to break out.
                if (matchLen === 0) {
                    re.lastIndex++;
                    if (re.lastIndex > line.length) break;
                    continue;
                }
                out.push([m.index + 1, matchLen]);
                if (++safety > 1000) break;
            }
            return out;
        },
    };
}

function matchFile(text: string, matcher: CompiledMatcher): LineMatch[] {
    const out: LineMatch[] = [];
    // Splitting on \n is faster than a regex split here; \r is stripped per-line.
    const lines = text.split('\n');
    for (let i = 0; i < lines.length; i++) {
        let line = lines[i];
        if (line.endsWith('\r')) line = line.slice(0, -1);
        const hits = matcher.matchLine(line);
        if (hits.length === 0) continue;
        for (const [col, len] of hits) {
            const { snippet, leadTrim } = makeSnippet(line, col - 1, len);
            out.push({
                line: i + 1,
                column: col,
                length: len,
                text: snippet,
                leadTrim,
            });
            if (out.length >= MAX_MATCHES_PER_FILE) return out;
        }
    }
    return out;
}

// Trim very long lines around the match so the rendered row stays a sensible
// width. Keeps roughly 40 chars of left context plus the match plus the rest
// of the line, capped at SNIPPET_MAX_LEN.
function makeSnippet(line: string, startIdx: number, _matchLen: number): { snippet: string; leadTrim: number } {
    const LEFT_CTX = 40;
    if (line.length <= SNIPPET_MAX_LEN) {
        return { snippet: line, leadTrim: 0 };
    }
    let leadTrim = Math.max(0, startIdx - LEFT_CTX);
    let snippet = line.slice(leadTrim);
    if (snippet.length > SNIPPET_MAX_LEN) {
        snippet = snippet.slice(0, SNIPPET_MAX_LEN) + '…';
    }
    if (leadTrim > 0) {
        snippet = '…' + snippet;
        leadTrim -= 1; // the leading '…' takes up one visual char, keep highlighter aligned
    }
    return { snippet, leadTrim };
}

function escapeRegex(s: string): string {
    return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

// Minimal glob → RegExp for path excludes. Supports `**` (any depth), `*`
// (single segment, no slash), and `?` (one non-slash char). Anchored to the
// full path so `dist/**` matches `dist/file.txt` and `dist/sub/x.txt`.
function compileGlob(glob: string): RegExp {
    let i = 0;
    let out = '^';
    while (i < glob.length) {
        const c = glob[i];
        if (c === '*' && glob[i + 1] === '*') {
            // Trailing or middle `**` — collapse `/**/` into `(?:.*?/)?` so
            // it can match zero segments too.
            if (glob[i + 2] === '/') { out += '(?:.*/)?'; i += 3; }
            else { out += '.*'; i += 2; }
        } else if (c === '*') {
            out += '[^/]*'; i++;
        } else if (c === '?') {
            out += '[^/]'; i++;
        } else if ('.+^$(){}|[]\\'.includes(c)) {
            out += '\\' + c; i++;
        } else {
            out += c; i++;
        }
    }
    out += '$';
    return new RegExp(out);
}

function appendHighlighted(parent: HTMLElement, snippet: string, offset: number, length: number) {
    if (offset < 0 || offset > snippet.length || length <= 0) {
        parent.textContent = snippet;
        return;
    }
    const before = snippet.slice(0, offset);
    const mid = snippet.slice(offset, offset + length);
    const after = snippet.slice(offset + length);
    if (before) parent.appendChild(document.createTextNode(before));
    const mark = document.createElement('mark');
    mark.textContent = mid;
    parent.appendChild(mark);
    if (after) parent.appendChild(document.createTextNode(after));
}
