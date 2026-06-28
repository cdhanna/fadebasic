// "What's new" popup for Playground version bumps.
//
// On every boot, maybeShowChangelogPopup() compares the active
// PLAYGROUND_VERSION against the version this browser saw last (stored
// in localStorage under LAST_SEEN_KEY). If they differ, the modal
// renders every CHANGELOG entry NEWER than the last-seen version and
// commits the new pointer on dismiss. First-ever loads (no stored
// pointer) are silent — we set the pointer to the active version so
// the popup only fires for real upgrades, not for new users.
//
// showChangelogModal() also powers the Diagnostics-panel version row
// click, where the user has explicitly asked to see the full history.

import { marked } from 'marked';
import {
    CHANGELOG,
    CHANGELOG_CATEGORIES,
    PLAYGROUND_VERSION,
    type ChangelogEntry,
} from './changelog';

// Same defensive scrub pattern used by markdown-preview.ts: input is
// authored by the Playground maintainer (not the end user), but a stray
// <script> in a changelog bullet shouldn't get a chance to run.
function scrubInlineHtml(html: string): string {
    return html
        .replace(/<\s*(script|style|iframe|object|embed)[^>]*>[\s\S]*?<\s*\/\s*\1\s*>/gi, '')
        .replace(/\son[a-z]+\s*=\s*"[^"]*"/gi, '')
        .replace(/\son[a-z]+\s*=\s*'[^']*'/gi, '')
        .replace(/javascript:/gi, '');
}

// Parse one bullet as inline markdown. parseInline skips the
// paragraph wrapping `parse` adds, so `**bold** word` becomes
// `<strong>bold</strong> word` directly — exactly what we want
// inside an <li>.
function renderBulletMarkdown(source: string): string {
    try {
        const html = marked.parseInline(source, { async: false, gfm: true, breaks: false }) as string;
        return scrubInlineHtml(html);
    } catch {
        // Fall back to plain text if marked throws — better a dull
        // bullet than a broken popup.
        const div = document.createElement('div');
        div.textContent = source;
        return div.innerHTML;
    }
}

const LAST_SEEN_KEY = 'fade.playground.lastSeenVersion';

function readLastSeen(): string | null {
    try { return localStorage.getItem(LAST_SEEN_KEY); } catch { return null; }
}

function writeLastSeen(version: string): void {
    try { localStorage.setItem(LAST_SEEN_KEY, version); } catch { /* ignore */ }
}

// Entries strictly newer than `lastSeen`, matched by version-equality
// against the CHANGELOG array (which is ordered newest-first). If
// `lastSeen` isn't in the array — e.g. a stale localStorage value
// pointing at a long-trimmed version — we show everything; better to
// over-tell than to silently skip a real bump.
function entriesNewerThan(lastSeen: string): ChangelogEntry[] {
    const idx = CHANGELOG.findIndex(e => e.version === lastSeen);
    if (idx === -1) return CHANGELOG.slice();
    return CHANGELOG.slice(0, idx);
}

export function maybeShowChangelogPopup(): void {
    const lastSeen = readLastSeen();
    if (lastSeen === null) {
        // First boot on this browser — no upgrade story to tell. Stamp
        // the pointer so the next bump fires the modal naturally.
        writeLastSeen(PLAYGROUND_VERSION);
        return;
    }
    if (lastSeen === PLAYGROUND_VERSION) return;

    const entries = entriesNewerThan(lastSeen);
    if (entries.length === 0) {
        // lastSeen is ahead of PLAYGROUND_VERSION (downgrade), or no
        // diff to show. Re-sync the pointer rather than nag.
        writeLastSeen(PLAYGROUND_VERSION);
        return;
    }
    showChangelogModal(entries, lastSeen);
}

// Render `entries` into the static #changelog-overlay markup and wire
// dismiss handlers. `previous` is the version the user was last on, or
// null when the user manually opened the modal (in which case the
// subtitle just shows the active version).
export function showChangelogModal(entries: ChangelogEntry[], previous: string | null): void {
    const overlay = document.getElementById('changelog-overlay');
    const body = document.getElementById('changelog-body');
    const subtitle = document.getElementById('changelog-subtitle');
    const dismissBtn = document.getElementById('changelog-dismiss') as HTMLButtonElement | null;
    if (!overlay || !body || !subtitle || !dismissBtn) return;

    subtitle.textContent = previous
        ? `Playground updated to ${PLAYGROUND_VERSION} (from ${previous})`
        : `Playground ${PLAYGROUND_VERSION}`;

    body.replaceChildren();
    for (const entry of entries) {
        const section = document.createElement('section');
        section.className = 'changelog-entry';

        const titleRow = document.createElement('div');
        titleRow.className = 'changelog-entry-title';
        const versionEl = document.createElement('span');
        versionEl.className = 'changelog-entry-version';
        versionEl.textContent = entry.version;
        const dateEl = document.createElement('span');
        dateEl.className = 'changelog-entry-date';
        dateEl.textContent = entry.date;
        titleRow.append(versionEl, dateEl);
        section.appendChild(titleRow);

        // Iterate categories in CHANGELOG_CATEGORIES order so the
        // rendered layout always reads Added → Changed → Fixed →
        // Removed → Notes regardless of object-literal key order.
        for (const cat of CHANGELOG_CATEGORIES) {
            const items = entry[cat.key];
            if (!items || items.length === 0) continue;

            const catBlock = document.createElement('div');
            catBlock.className = 'changelog-cat';

            const catTitle = document.createElement('div');
            catTitle.className = 'changelog-cat-title';
            catTitle.textContent = cat.label;
            catBlock.appendChild(catTitle);

            const list = document.createElement('ul');
            for (const item of items) {
                const li = document.createElement('li');
                // innerHTML is fed marked-parsed-then-scrubbed
                // output, never raw user input. See scrubInlineHtml.
                li.innerHTML = renderBulletMarkdown(item);
                list.appendChild(li);
            }
            catBlock.appendChild(list);
            section.appendChild(catBlock);
        }

        body.appendChild(section);
    }

    overlay.hidden = false;

    const dismiss = () => {
        overlay.hidden = true;
        writeLastSeen(PLAYGROUND_VERSION);
        document.removeEventListener('keydown', onKeyDown);
        overlay.removeEventListener('click', onBackdropClick);
        dismissBtn.removeEventListener('click', dismiss);
    };
    const onKeyDown = (e: KeyboardEvent) => { if (e.key === 'Escape') dismiss(); };
    const onBackdropClick = (e: MouseEvent) => { if (e.target === overlay) dismiss(); };

    dismissBtn.addEventListener('click', dismiss);
    overlay.addEventListener('click', onBackdropClick);
    document.addEventListener('keydown', onKeyDown);
    dismissBtn.focus();
}

// Diagnostics-panel "view full changelog" entry point — shows every
// known entry regardless of last-seen.
export function showFullChangelog(): void {
    showChangelogModal(CHANGELOG, null);
}
