// Recent History dockview panel. Subscribes to a CollaborationController
// and renders the commit log with per-commit file-level diffs + a Restore
// action. Lives in its own tab (next to Logs / Output / etc.) so it can
// expand horizontally — long commit messages and file lists no longer
// fight the narrow Collaboration panel's column.
//
// Per-commit expansion is local UI state — multiple history panels (in
// different windows) would each track their own expansions. Diff and tree
// data are cached centrally inside the collaboration panel and served
// through the controller, so opening the same commit in multiple panels
// only fetches once.

import type { CollaborationController, SharingCommitInfo } from './collaboration-panel';
import type { TreeDiff } from './git-types';
import type { LocalSave } from './local-saves';

const CSS_PREFIX = 'fade-hist';
const STYLE_ID = `${CSS_PREFIX}-styles`;

export interface HistoryPanelOptions {
    container: HTMLElement;
    controller: CollaborationController;
}

export interface HistoryPanelHandle {
    dispose(): void;
}

export function mountHistoryPanel(opts: HistoryPanelOptions): HistoryPanelHandle {
    injectStylesOnce();
    const root = opts.container;
    root.classList.add(`${CSS_PREFIX}-root`);
    root.replaceChildren();

    let commits: SharingCommitInfo[] = opts.controller.getRecentCommits();
    let saves: LocalSave[] = opts.controller.getPendingSaves();
    const expanded = new Set<string>();
    const diffs = new Map<string, TreeDiff>();
    const loading = new Set<string>();
    /** Same caches but keyed by save id rather than commit sha. Saves
     *  diffs are computed against the prior save or baseTree. */
    const saveDiffs = new Map<string, TreeDiff>();
    const saveLoading = new Set<string>();

    function render() {
        root.replaceChildren();
        const header = el('div', `${CSS_PREFIX}-header`);
        const title = el('div', `${CSS_PREFIX}-title`);
        title.textContent = 'History';
        const info = opts.controller.getRepoInfo();
        const meta = el('div', `${CSS_PREFIX}-meta`);
        meta.textContent = info ? `${info.owner}/${info.name} · ${info.branch}` : '(not connected)';
        header.append(title, meta);
        root.append(header);

        // Local saves section — rendered first, above the published divider.
        // These never touch the remote until the user clicks Publish.
        if (saves.length > 0) {
            const localH = el('h4', `${CSS_PREFIX}-section-h`);
            localH.textContent = 'Local saves';
            root.append(localH);
            root.append(p(
                `${saves.length} unpublished checkpoint${saves.length === 1 ? '' : 's'}. Publish to roll them up into one commit on the remote.`,
                'dim',
            ));
            const sList = el('ol', `${CSS_PREFIX}-list`);
            saves.forEach((s, i) => sList.append(renderSave(s, i)));
            root.append(sList);
            // Divider — visual + textual separation between local and published.
            const divider = el('div', `${CSS_PREFIX}-divider`);
            divider.textContent = '— published below —';
            root.append(divider);
        }

        // Remote commits section.
        const pubH = el('h4', `${CSS_PREFIX}-section-h`);
        pubH.textContent = 'Published commits';
        root.append(pubH);
        if (commits.length === 0) {
            root.append(p('No published history yet. Publish from the Source Control panel to populate.', 'dim'));
            return;
        }
        const list = el('ol', `${CSS_PREFIX}-list`);
        for (const c of commits) {
            list.append(renderCommit(c));
        }
        root.append(list);
    }

    function renderSave(s: LocalSave, idx: number): HTMLElement {
        const li = document.createElement('li');
        li.className = `${CSS_PREFIX}-li ${CSS_PREFIX}-li-save`;
        const isExpanded = expanded.has(`save:${s.id}`);

        const headerRow = el('div', `${CSS_PREFIX}-row`);
        headerRow.style.cursor = 'pointer';
        const caret = el('span', `${CSS_PREFIX}-caret`);
        caret.textContent = isExpanded ? '▼' : '▶';
        const badge = el('span', `${CSS_PREFIX}-savebadge`);
        badge.textContent = `save #${saves.length - idx}`;
        const msg = el('span', `${CSS_PREFIX}-msg`);
        msg.textContent = s.message;
        const metaSpan = el('span', `${CSS_PREFIX}-row-meta`);
        metaSpan.textContent = ` · ${s.time.slice(0, 16).replace('T', ' ')}`;
        headerRow.append(caret, text(' '), badge, text(' '), msg, metaSpan);
        headerRow.onclick = () => { void toggleSaveExpansion(s.id); };
        li.append(headerRow);

        if (isExpanded) {
            const detail = el('div', `${CSS_PREFIX}-detail`);
            const d = saveDiffs.get(s.id);
            if (saveLoading.has(s.id) && !d) {
                detail.append(p('Loading…', 'dim'));
            } else if (!d) {
                detail.append(p('(diff unavailable)', 'dim'));
            } else if (d.added.length === 0 && d.modified.length === 0 && d.deleted.length === 0) {
                detail.append(p('(no changes captured)', 'dim'));
            } else {
                const flist = el('ul', `${CSS_PREFIX}-diff`);
                const addRow = (path: string, kind: 'added' | 'modified' | 'deleted') => {
                    const r = document.createElement('li');
                    r.className = `${CSS_PREFIX}-diff-row ${CSS_PREFIX}-diff-${kind}`;
                    const g = el('span', `${CSS_PREFIX}-diff-glyph`);
                    g.textContent = kind === 'added' ? 'A' : kind === 'modified' ? 'M' : 'D';
                    const ps = el('span', `${CSS_PREFIX}-diff-path`);
                    ps.textContent = path;
                    r.append(g, ps);
                    const diffBtn = document.createElement('button');
                    diffBtn.className = `${CSS_PREFIX}-diff-btn`;
                    diffBtn.type = 'button';
                    diffBtn.textContent = 'diff';
                    diffBtn.title = `Open a read-only diff of ${path} (predecessor → this save).`;
                    diffBtn.onclick = (ev) => {
                        ev.stopPropagation();
                        void opts.controller.openDiffViewer({ kind: 'save', saveId: s.id, path });
                    };
                    r.append(diffBtn);
                    flist.append(r);
                };
                for (const path of d.added)    addRow(path, 'added');
                for (const path of d.modified) addRow(path, 'modified');
                for (const path of d.deleted)  addRow(path, 'deleted');
                detail.append(flist);
            }
            const actions = el('div', `${CSS_PREFIX}-actions`);
            const revertBtn = document.createElement('button');
            revertBtn.className = `${CSS_PREFIX}-btn`;
            revertBtn.textContent = 'Revert to this';
            revertBtn.type = 'button';
            revertBtn.onclick = () => { void opts.controller.revertToLocalSave(s.id); };
            const dropBtn = document.createElement('button');
            dropBtn.className = `${CSS_PREFIX}-btn`;
            dropBtn.textContent = 'Drop';
            dropBtn.type = 'button';
            dropBtn.onclick = () => {
                if (!confirm(`Drop save "${s.message}"? Only removes the snapshot — working tree is untouched.`)) return;
                opts.controller.dropLocalSave(s.id);
            };
            actions.append(revertBtn, dropBtn);
            detail.append(actions);
            li.append(detail);
        }
        return li;
    }

    async function toggleSaveExpansion(id: string) {
        const key = `save:${id}`;
        if (expanded.has(key)) {
            expanded.delete(key);
            render();
            return;
        }
        expanded.add(key);
        render();
        if (saveDiffs.has(id) || saveLoading.has(id)) return;
        saveLoading.add(id);
        try {
            const d = await opts.controller.getSaveDiff(id);
            if (d) saveDiffs.set(id, d);
        } catch { /* swallow — UI shows "(diff unavailable)" */ }
        finally {
            saveLoading.delete(id);
            render();
        }
    }

    function renderCommit(c: SharingCommitInfo): HTMLElement {
        const info = opts.controller.getRepoInfo();
        const li = document.createElement('li');
        const isExpanded = expanded.has(c.id);

        const headerRow = el('div', `${CSS_PREFIX}-row`);
        headerRow.style.cursor = 'pointer';
        const caret = el('span', `${CSS_PREFIX}-caret`);
        caret.textContent = isExpanded ? '▼' : '▶';
        // GitHub commit link (only when connected — otherwise plain text).
        let idEl: HTMLElement;
        if (info) {
            const a = document.createElement('a');
            a.className = `${CSS_PREFIX}-id ${CSS_PREFIX}-id-link`;
            a.href = `https://github.com/${info.owner}/${info.name}/commit/${c.id}`;
            a.target = '_blank';
            a.rel = 'noopener noreferrer';
            a.textContent = c.id.slice(0, 8);
            a.title = `Open commit ${c.id} on github.com`;
            a.addEventListener('click', (e) => e.stopPropagation());
            idEl = a;
        } else {
            idEl = el('span', `${CSS_PREFIX}-id`);
            idEl.textContent = c.id.slice(0, 8);
        }
        const msg = el('span', `${CSS_PREFIX}-msg`);
        msg.textContent = c.message.split('\n')[0];
        const metaSpan = el('span', `${CSS_PREFIX}-row-meta`);
        metaSpan.textContent = ` · ${c.author} · ${c.time.slice(0, 16).replace('T', ' ')}`;
        headerRow.append(caret, text(' '), idEl, text(' '), msg, metaSpan);
        headerRow.onclick = () => { void toggleExpansion(c.id); };
        li.append(headerRow);

        if (isExpanded) {
            li.append(renderDetail(c));
        }
        return li;
    }

    function renderDetail(c: SharingCommitInfo): HTMLElement {
        const detail = el('div', `${CSS_PREFIX}-detail`);
        const d = diffs.get(c.id);
        if (loading.has(c.id) && !d) {
            detail.append(p('Loading…', 'dim'));
        } else if (!d) {
            detail.append(p('(diff unavailable)', 'dim'));
        } else if (d.added.length === 0 && d.modified.length === 0 && d.deleted.length === 0) {
            detail.append(p('(no changes)', 'dim'));
        } else {
            const flist = el('ul', `${CSS_PREFIX}-diff`);
            const addRow = (path: string, kind: 'added' | 'modified' | 'deleted') => {
                const r = document.createElement('li');
                r.className = `${CSS_PREFIX}-diff-row ${CSS_PREFIX}-diff-${kind}`;
                const g = el('span', `${CSS_PREFIX}-diff-glyph`);
                g.textContent = kind === 'added' ? 'A' : kind === 'modified' ? 'M' : 'D';
                const ps = el('span', `${CSS_PREFIX}-diff-path`);
                ps.textContent = path;
                r.append(g, ps);
                const diffBtn = document.createElement('button');
                diffBtn.className = `${CSS_PREFIX}-diff-btn`;
                diffBtn.type = 'button';
                diffBtn.textContent = 'diff';
                diffBtn.title = `Open a read-only diff of ${path} (parent → this commit).`;
                diffBtn.onclick = (ev) => {
                    ev.stopPropagation();
                    void opts.controller.openDiffViewer({ kind: 'commit', commitSha: c.id, path });
                };
                r.append(diffBtn);
                flist.append(r);
            };
            for (const path of d.added)    addRow(path, 'added');
            for (const path of d.modified) addRow(path, 'modified');
            for (const path of d.deleted)  addRow(path, 'deleted');
            detail.append(flist);
        }
        // Restore action — except on the tip commit (no-op).
        const tip = commits[0];
        if (tip && c.id === tip.id) {
            detail.append(p('(current tip — already checked out)', 'dim'));
        } else {
            const restore = document.createElement('button');
            restore.className = `${CSS_PREFIX}-btn`;
            restore.textContent = 'Restore as new commit';
            restore.type = 'button';
            restore.onclick = () => { void opts.controller.restoreCommit(c.id); };
            const actions = el('div', `${CSS_PREFIX}-actions`);
            actions.append(restore);
            detail.append(actions);
        }
        return detail;
    }

    async function toggleExpansion(sha: string) {
        if (expanded.has(sha)) {
            expanded.delete(sha);
            render();
            return;
        }
        expanded.add(sha);
        render();
        if (diffs.has(sha) || loading.has(sha)) return;
        loading.add(sha);
        try {
            const d = await opts.controller.getCommitDiff(sha);
            if (d) diffs.set(sha, d);
        } catch {
            // controller already routes failures to the LogBus; we just
            // surface "(diff unavailable)" in the row.
        } finally {
            loading.delete(sha);
            render();
        }
    }

    const unsub = opts.controller.onHistoryChange((list) => {
        commits = list;
        render();
    });
    const unsubSaves = opts.controller.onSavesChange((list) => {
        saves = list;
        // Drop cached diffs for saves that no longer exist (dropped /
        // published-cleared) so we don't accumulate stale entries.
        const live = new Set(list.map((s) => s.id));
        for (const id of [...saveDiffs.keys()]) if (!live.has(id)) saveDiffs.delete(id);
        for (const id of [...expanded]) {
            if (id.startsWith('save:') && !live.has(id.slice('save:'.length))) {
                expanded.delete(id);
            }
        }
        render();
    });

    render();

    return {
        dispose() {
            unsub();
            unsubSaves();
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

function text(s: string): Text {
    return document.createTextNode(s);
}

function p(s: string, modifier?: 'dim'): HTMLElement {
    const n = el('p', `${CSS_PREFIX}-p` + (modifier === 'dim' ? ` ${CSS_PREFIX}-dim` : ''));
    n.textContent = s;
    return n;
}

function injectStylesOnce(): void {
    if (document.getElementById(STYLE_ID)) return;
    const style = document.createElement('style');
    style.id = STYLE_ID;
    style.textContent = `
.${CSS_PREFIX}-root {
    display: flex; flex-direction: column;
    height: 100%; box-sizing: border-box;
    padding: 8px 10px;
    overflow-y: auto;
    color: var(--vscode-foreground, #ddd);
    font: 13px/1.4 ui-sans-serif, system-ui, sans-serif;
}
.${CSS_PREFIX}-header {
    display: flex; align-items: baseline; gap: 12px;
    padding-bottom: 8px;
    border-bottom: 1px solid var(--vscode-panel-border, #333);
    margin-bottom: 8px;
}
.${CSS_PREFIX}-title { font-size: 13px; font-weight: 600; }
.${CSS_PREFIX}-meta { font-size: 12px; opacity: 0.6; font-family: ui-monospace, monospace; }
.${CSS_PREFIX}-p { margin: 0; opacity: 0.9; }
.${CSS_PREFIX}-dim { opacity: 0.55; font-size: 12px; }
.${CSS_PREFIX}-list {
    list-style: none; padding: 0; margin: 0;
    display: flex; flex-direction: column; gap: 2px;
}
.${CSS_PREFIX}-list li {
    padding: 2px 4px;
    border-left: 2px solid var(--vscode-panel-border, #444);
    padding-left: 8px;
}
.${CSS_PREFIX}-row {
    display: flex; align-items: baseline; gap: 4px;
    padding: 2px 0;
    user-select: none;
    font-size: 12px;
}
.${CSS_PREFIX}-row:hover {
    background: rgba(255,255,255,0.04);
    border-radius: 3px;
}
.${CSS_PREFIX}-caret {
    width: 12px; font-size: 9px;
    color: var(--vscode-icon-foreground, #888);
    flex-shrink: 0;
}
.${CSS_PREFIX}-id {
    font-family: ui-monospace, monospace;
    color: var(--vscode-textLink-foreground, #4da6ff);
}
.${CSS_PREFIX}-id-link {
    text-decoration: none;
    padding: 0 2px; border-radius: 2px;
}
.${CSS_PREFIX}-id-link:hover {
    text-decoration: underline;
    background: rgba(77,166,255,0.1);
}
.${CSS_PREFIX}-id-link::after {
    content: ' ↗'; font-size: 9px; opacity: 0.6;
}
.${CSS_PREFIX}-row-meta { opacity: 0.55; font-size: 11px; }
.${CSS_PREFIX}-detail {
    margin: 4px 0 8px 14px;
    padding: 6px 10px;
    background: rgba(255,255,255,0.03);
    border-radius: 4px;
    display: flex; flex-direction: column; gap: 6px;
}
.${CSS_PREFIX}-diff {
    list-style: none; padding: 0; margin: 0;
    display: flex; flex-direction: column; gap: 1px;
}
.${CSS_PREFIX}-diff-row {
    display: flex; align-items: center; gap: 8px;
    padding: 1px 4px; font-family: ui-monospace, monospace; font-size: 11px;
}
.${CSS_PREFIX}-diff-glyph { width: 14px; text-align: center; font-weight: 700; }
.${CSS_PREFIX}-diff-added    .${CSS_PREFIX}-diff-glyph { color: #6e6; }
.${CSS_PREFIX}-diff-modified .${CSS_PREFIX}-diff-glyph { color: #fc6; }
.${CSS_PREFIX}-diff-deleted  .${CSS_PREFIX}-diff-glyph { color: #f88; }
.${CSS_PREFIX}-diff-path { word-break: break-all; flex: 1 1 auto; min-width: 0; }
.${CSS_PREFIX}-diff-row { display: flex; align-items: center; gap: 8px; }
.${CSS_PREFIX}-diff-btn {
    appearance: none; border: 1px solid var(--vscode-panel-border, #555);
    background: transparent; color: inherit; cursor: pointer;
    padding: 0 6px; border-radius: 3px;
    font: inherit; font-size: 10px; line-height: 16px;
    opacity: 0.55; flex-shrink: 0;
}
.${CSS_PREFIX}-diff-row:hover .${CSS_PREFIX}-diff-btn { opacity: 1; }
.${CSS_PREFIX}-diff-btn:hover { background: rgba(77,166,255,0.1); border-color: rgba(77,166,255,0.4); }
.${CSS_PREFIX}-actions { display: flex; }
.${CSS_PREFIX}-btn {
    appearance: none; border: 1px solid var(--vscode-panel-border, #555);
    background: transparent; color: inherit; cursor: pointer;
    padding: 3px 10px; border-radius: 4px;
    font: inherit; font-size: 11px;
}
.${CSS_PREFIX}-btn:hover { filter: brightness(1.2); }
.${CSS_PREFIX}-section-h {
    margin: 6px 0 4px;
    font-size: 11px; font-weight: 600;
    text-transform: uppercase; letter-spacing: 0.04em;
    opacity: 0.7;
}
.${CSS_PREFIX}-divider {
    margin: 10px 0 6px;
    text-align: center;
    font-size: 10px; opacity: 0.45;
    text-transform: uppercase; letter-spacing: 0.08em;
    border-top: 1px dashed var(--vscode-panel-border, #444);
    padding-top: 4px;
}
.${CSS_PREFIX}-li-save {
    border-left-color: #b88aff !important;
}
.${CSS_PREFIX}-savebadge {
    font-family: ui-monospace, monospace;
    font-size: 10px;
    padding: 0 4px;
    border-radius: 3px;
    background: rgba(184,138,255,0.15);
    color: #b88aff;
}
`;
    document.head.appendChild(style);
}
