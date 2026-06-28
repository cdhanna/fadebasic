// Standalone diff-approval renderer. Extracted from ai-chat.ts so it
// can be unit-tested with jsdom and exercised in a standalone visual demo
// page (scripts/diff-demo.html) without booting the whole chat panel.
//
// Contract: pass in the container to mount into, the file + before/after
// content, and callbacks for approve/reject. The function appends the
// diff bubble + hint and returns a `dispose()` you can call to force-
// reject the dialog (used when the user clicks Stop or starts a new chat
// while the agent is waiting on confirmation).

// ─── Line diff (LCS) ────────────────────────────────────────────────────────

export type DiffLine = { type: 'same' | 'add' | 'remove'; text: string };

/** A single rendered diff row. `same|add|remove` are content lines; `gap`
 *  is a synthetic separator inserted by `compactDiff` to elide long runs of
 *  unchanged content. `hidden` is the number of lines collapsed into the gap. */
export type DiffRow =
    | { type: 'same' | 'add' | 'remove'; text: string }
    | { type: 'gap'; hidden: number };

export function lineDiff(oldText: string, newText: string): DiffLine[] {
    const a = oldText ? oldText.split('\n') : [];
    const b = newText ? newText.split('\n') : [];
    const m = a.length, n = b.length;
    const dp: number[][] = Array.from({ length: m + 1 }, () => new Array(n + 1).fill(0));
    for (let i = m - 1; i >= 0; i--)
        for (let j = n - 1; j >= 0; j--)
            dp[i][j] = a[i] === b[j] ? 1 + dp[i + 1][j + 1] : Math.max(dp[i + 1][j], dp[i][j + 1]);

    // Traceback. At a mismatch, advancing `i` corresponds to REMOVE; the
    // future LCS along that path is dp[i+1][j]. Advancing `j` corresponds
    // to ADD; the future LCS is dp[i][j+1]. Pick whichever preserves more
    // matches. The old code had this inverted, which produced wildly
    // inflated diffs (every line in a CRLF-edited file marked changed)
    // because suboptimal-trail choices cascade.
    const out: DiffLine[] = [];
    let i = 0, j = 0;
    while (i < m || j < n) {
        if (i < m && j < n && a[i] === b[j]) {
            out.push({ type: 'same', text: a[i] }); i++; j++;
        } else if (i < m && (j >= n || dp[i + 1][j] >= dp[i][j + 1])) {
            out.push({ type: 'remove', text: a[i] }); i++;
        } else {
            out.push({ type: 'add', text: b[j] }); j++;
        }
    }
    return out;
}

/** Collapse long runs of unchanged lines into `gap` rows, keeping
 *  `contextLines` of `same` on either side of every change. A single
 *  unchanged span shorter than `contextLines * 2 + 1` between two changes
 *  is kept intact (no gap created — looks weird to elide 2 lines and keep
 *  a 1-line marker instead). */
export function compactDiff(diff: DiffLine[], contextLines = 3): DiffRow[] {
    // Mark which indices contain a change (add/remove) — context windows
    // are computed against this set so adjacent changes share their
    // surrounding context.
    const changeIndices: number[] = [];
    for (let i = 0; i < diff.length; i++) {
        if (diff[i].type !== 'same') changeIndices.push(i);
    }
    // No changes → show everything (caller usually short-circuits with the
    // "no visible changes" placeholder before getting here, but be safe).
    if (changeIndices.length === 0) {
        return diff.map(l => ({ type: l.type, text: l.text }));
    }

    // Compute the keep set: every line within `contextLines` of any change.
    const keep = new Set<number>();
    for (const idx of changeIndices) {
        const lo = Math.max(0, idx - contextLines);
        const hi = Math.min(diff.length - 1, idx + contextLines);
        for (let k = lo; k <= hi; k++) keep.add(k);
    }

    const rows: DiffRow[] = [];
    let i = 0;
    while (i < diff.length) {
        if (keep.has(i)) {
            rows.push({ type: diff[i].type, text: diff[i].text });
            i++;
            continue;
        }
        // Run of hidden lines. Skip them and emit a gap marker.
        let j = i;
        while (j < diff.length && !keep.has(j)) j++;
        const hidden = j - i;
        // Don't bother eliding a 1-line run — the gap row itself takes a
        // line, so it doesn't actually save space and looks fussier.
        if (hidden <= 1) {
            for (let k = i; k < j; k++) rows.push({ type: diff[k].type, text: diff[k].text });
        } else {
            rows.push({ type: 'gap', hidden });
        }
        i = j;
    }
    return rows;
}

function renderDiffPre(diff: DiffLine[]): HTMLElement {
    const pre = document.createElement('pre');
    pre.className = 'ai-diff';
    const rows = compactDiff(diff);
    for (const row of rows) {
        const span = document.createElement('span');
        if (row.type === 'gap') {
            span.className = 'ai-diff-line ai-diff-gap';
            span.textContent = `  ⋯ ${row.hidden} unchanged line${row.hidden === 1 ? '' : 's'} ⋯`;
        } else {
            span.className = `ai-diff-line ai-diff-${row.type}`;
            const prefix = row.type === 'add' ? '+ ' : row.type === 'remove' ? '- ' : '  ';
            span.textContent = prefix + row.text;
        }
        pre.appendChild(span);
        pre.appendChild(document.createTextNode('\n'));
    }
    return pre;
}

// ─── Diff approval UI ───────────────────────────────────────────────────────

export interface DiffApprovalOptions {
    container: HTMLElement;
    path: string;
    oldContent: string;
    newContent: string;
    onApprove(): void;
    onReject(): void;
}

export interface DiffApprovalHandle {
    /** Force-reject the dialog programmatically (e.g. on agent abort).
     *  Idempotent — safe to call multiple times. */
    forceReject(): void;
    /** The wrapper element, for tests/inspection. */
    wrapper: HTMLElement;
    /** The hint element appended after the wrapper. */
    hint: HTMLElement;
}

export function mountDiffApproval(opts: DiffApprovalOptions): DiffApprovalHandle {
    const { container, path, oldContent, newContent, onApprove, onReject } = opts;

    const isNew = oldContent.length === 0;
    const diff = lineDiff(oldContent, newContent);
    let added = 0, removed = 0;
    for (const d of diff) {
        if (d.type === 'add') added++;
        else if (d.type === 'remove') removed++;
    }

    // ── Wrapper ─────────────────────────────────────────────────────────
    const wrapper = document.createElement('div');
    wrapper.className = 'ai-diff-wrapper';

    // Layout safeguards. We've hit two distinct collapse modes in the
    // chat panel, both invisible in jsdom unit tests:
    //
    //   1. Wrapper taller than the scrollable parent → scrolling actions
    //      into view pushed the header off-screen, leaving the user
    //      looking at only the bottom of the wrapper (just buttons in a
    //      blue box).
    //   2. Wrapper as a flex item in `.ai-messages` collapsing to ~0
    //      height because `overflow: hidden` makes `min-height: auto`
    //      resolve to 0 and `flex-shrink` defaults to 1 — so sibling
    //      tool rows/plans above can squeeze it flat.
    //
    // CSS handles (2) via `flex-shrink: 0`. We also enforce it inline
    // (and bound max-height to the container) so the standalone visual
    // demo and any future host get the right behavior without depending
    // on the static stylesheet.
    wrapper.style.flexShrink = '0';
    const containerHeight = container.getBoundingClientRect().height;
    if (containerHeight > 180) {
        // Leave breathing room so the wrapper doesn't fully occlude
        // surrounding messages.
        wrapper.style.maxHeight = `${containerHeight - 24}px`;
    }

    // Header — click to collapse/expand the diff body (actions stay visible).
    const header = document.createElement('button');
    header.className = 'ai-diff-header';
    header.type = 'button';
    const chevron = document.createElement('span');
    chevron.className = 'ai-diff-chevron';
    chevron.textContent = '▼';
    const headerText = document.createElement('span');
    headerText.className = 'ai-diff-header-text';
    if (isNew) {
        const lineCount = newContent ? newContent.split('\n').length : 0;
        headerText.textContent = `Create file: ${path}  (${lineCount} line${lineCount === 1 ? '' : 's'})`;
    } else if (added === 0 && removed === 0) {
        headerText.textContent = `Edit: ${path}  (no visible changes)`;
    } else {
        const parts: string[] = [];
        if (added > 0) parts.push(`+${added}`);
        if (removed > 0) parts.push(`−${removed}`);
        headerText.textContent = `Edit: ${path}  (${parts.join(' ')})`;
    }
    header.append(chevron, headerText);
    wrapper.appendChild(header);

    const body = document.createElement('div');
    body.className = 'ai-diff-body';
    if (diff.length === 0 || (added === 0 && removed === 0 && !isNew)) {
        const empty = document.createElement('div');
        empty.className = 'ai-diff-empty';
        empty.textContent = isNew
            ? '(empty file — no content to display)'
            : '(model proposed the file but it matches the current contents exactly)';
        body.appendChild(empty);
    } else {
        body.appendChild(renderDiffPre(diff));
    }
    wrapper.appendChild(body);

    let collapsed = false;
    header.addEventListener('click', () => {
        collapsed = !collapsed;
        wrapper.classList.toggle('ai-diff-collapsed', collapsed);
        chevron.textContent = collapsed ? '▶' : '▼';
    });

    // Actions
    const actions = document.createElement('div');
    actions.className = 'ai-diff-actions';

    const applyBtn = document.createElement('button');
    applyBtn.className = 'ai-diff-apply';
    applyBtn.type = 'button';
    applyBtn.textContent = 'Apply';

    const rejectBtn = document.createElement('button');
    rejectBtn.className = 'ai-diff-reject';
    rejectBtn.type = 'button';
    rejectBtn.textContent = 'Reject';

    let resolved = false;
    const close = (verdict: 'approve' | 'reject') => {
        if (resolved) return;
        resolved = true;
        applyBtn.disabled = true;
        rejectBtn.disabled = true;
        wrapper.classList.add(verdict === 'approve' ? 'ai-diff-accepted' : 'ai-diff-rejected');
        (verdict === 'approve' ? onApprove : onReject)();
    };

    applyBtn.addEventListener('click', () => close('approve'));
    rejectBtn.addEventListener('click', () => close('reject'));

    actions.append(applyBtn, rejectBtn);
    wrapper.appendChild(actions);

    // ── Hint ────────────────────────────────────────────────────────────
    const hint = document.createElement('div');
    hint.className = 'ai-approval-hint';
    hint.textContent = isNew
        ? 'Review the proposed file above. Apply to create it, Reject to skip.'
        : 'Review the diff above, then Apply or Reject to continue.';

    container.appendChild(wrapper);
    container.appendChild(hint);

    // Belt-and-suspenders scroll into view. Important: scroll the
    // WRAPPER (header end) into view, not just the actions. Scrolling
    // only the actions can push the header above the visible area when
    // the wrapper is taller than the parent — which made the diff look
    // like a "blue box around just the buttons." The wrapper's own
    // max-height + internal body scroll keep actions reachable once the
    // header is visible.
    if (typeof requestAnimationFrame === 'function') {
        requestAnimationFrame(() => {
            try { wrapper.scrollIntoView({ block: 'nearest', behavior: 'auto' }); }
            catch { /* jsdom doesn't implement scrollIntoView */ }
        });
    }

    return {
        wrapper,
        hint,
        forceReject() { close('reject'); },
    };
}

// ─── Generic confirm (no diff) ───────────────────────────────────────────────

export interface ConfirmOptions {
    container: HTMLElement;
    title: string;
    /** Optional secondary lines (e.g. license, size). */
    detail?: string;
    approveLabel?: string;
    rejectLabel?: string;
    onApprove(): void;
    onReject(): void;
}

/** A lightweight yes/no approval bubble, styled like the diff approval but
 *  without a diff body. Used for actions that aren't file edits — e.g.
 *  confirming a catalog asset import before it downloads into the project.
 *  Returns a handle whose `forceReject()` settles it on abort/new-chat. */
export function mountConfirm(opts: ConfirmOptions): DiffApprovalHandle {
    const { container, title, detail, onApprove, onReject } = opts;

    const wrapper = document.createElement('div');
    wrapper.className = 'ai-diff-wrapper ai-confirm-wrapper';
    wrapper.style.flexShrink = '0';

    const header = document.createElement('div');
    header.className = 'ai-diff-header ai-confirm-header';
    const headerText = document.createElement('span');
    headerText.className = 'ai-diff-header-text';
    headerText.textContent = title;
    header.appendChild(headerText);
    wrapper.appendChild(header);

    if (detail) {
        const body = document.createElement('div');
        body.className = 'ai-diff-body ai-confirm-body';
        body.textContent = detail;
        wrapper.appendChild(body);
    }

    const actions = document.createElement('div');
    actions.className = 'ai-diff-actions';
    const applyBtn = document.createElement('button');
    applyBtn.className = 'ai-diff-apply';
    applyBtn.type = 'button';
    applyBtn.textContent = opts.approveLabel ?? 'Import';
    const rejectBtn = document.createElement('button');
    rejectBtn.className = 'ai-diff-reject';
    rejectBtn.type = 'button';
    rejectBtn.textContent = opts.rejectLabel ?? 'Skip';

    let resolved = false;
    const close = (verdict: 'approve' | 'reject') => {
        if (resolved) return;
        resolved = true;
        applyBtn.disabled = true;
        rejectBtn.disabled = true;
        wrapper.classList.add(verdict === 'approve' ? 'ai-diff-accepted' : 'ai-diff-rejected');
        (verdict === 'approve' ? onApprove : onReject)();
    };
    applyBtn.addEventListener('click', () => close('approve'));
    rejectBtn.addEventListener('click', () => close('reject'));
    actions.append(applyBtn, rejectBtn);
    wrapper.appendChild(actions);

    const hint = document.createElement('div');
    hint.className = 'ai-approval-hint';
    hint.textContent = 'Approve to add this to your project, or Skip.';

    container.appendChild(wrapper);
    container.appendChild(hint);
    if (typeof requestAnimationFrame === 'function') {
        requestAnimationFrame(() => {
            try { wrapper.scrollIntoView({ block: 'nearest', behavior: 'auto' }); }
            catch { /* jsdom */ }
        });
    }

    return { wrapper, hint, forceReject() { close('reject'); } };
}
