// Line-level diff used by the gutter decorator.
//
// LCS-based (O(N*M) time + memory). Good enough for playground-sized files
// — Myers' O((N+M)*D) refinement would be cheaper for large edits but isn't
// worth the complexity at this scale. The output is shaped for direct
// translation into Monaco decoration ranges.
//
// Output convention:
//   - `marks[i].line` is a 1-based line in the *current* text. Only changed
//     lines appear; unchanged lines are absent.
//   - `deletions[i].line` is "lines were deleted just after this current
//     line." 0 = before line 1.
//
// "Modified" is a paired delete+add at the same position; we surface it as
// a single mark on the added line rather than separate add+delete markers,
// matching how VS Code's gutter renders inline diffs.

export interface LineDiffMark {
    /** 1-based line in the current (live) text. */
    line: number;
    /** What happened to this line. */
    kind: 'added' | 'modified';
}

export interface LineDiffDeletion {
    /** 1-based line in current AFTER which the deletion sits. 0 = before line 1. */
    line: number;
    /** How many lines were removed. */
    count: number;
}

export interface LineDiffResult {
    marks: LineDiffMark[];
    deletions: LineDiffDeletion[];
}

export function lineDiff(baseText: string, currentText: string): LineDiffResult {
    const a = splitLines(baseText);
    const b = splitLines(currentText);
    const m = a.length;
    const n = b.length;

    if (m === 0 && n === 0) return { marks: [], deletions: [] };
    if (m === 0) {
        return { marks: b.map((_, i) => ({ line: i + 1, kind: 'added' })), deletions: [] };
    }
    if (n === 0) {
        return { marks: [], deletions: [{ line: 0, count: m }] };
    }

    // LCS length DP — lcs[i][j] = LCS length of a[i..] and b[j..].
    // Stored row-major in a flat Int32Array for cache friendliness.
    const cols = n + 1;
    const lcs = new Int32Array((m + 1) * cols);
    for (let i = m - 1; i >= 0; i--) {
        for (let j = n - 1; j >= 0; j--) {
            if (a[i] === b[j]) {
                lcs[i * cols + j] = lcs[(i + 1) * cols + (j + 1)] + 1;
            } else {
                lcs[i * cols + j] = Math.max(lcs[(i + 1) * cols + j], lcs[i * cols + (j + 1)]);
            }
        }
    }

    // Walk the table top-down to emit the shortest edit script.
    type Op = 'eq' | 'add' | 'del';
    const ops: Op[] = [];
    let i = 0; let j = 0;
    while (i < m && j < n) {
        if (a[i] === b[j]) { ops.push('eq'); i++; j++; }
        else if (lcs[(i + 1) * cols + j] >= lcs[i * cols + (j + 1)]) { ops.push('del'); i++; }
        else { ops.push('add'); j++; }
    }
    while (i < m) { ops.push('del'); i++; }
    while (j < n) { ops.push('add'); j++; }

    // Group consecutive non-eq ops into a single "change region" and decide
    // per region whether to surface as add/modified/delete. A region with
    // both `del` and `add` is a modification; pure-add is addition; pure-del
    // is a deletion marker. (A region that's ALL del + ALL add could also be
    // "N-for-M replace" — we mark the adds as 'modified'.)
    const marks: LineDiffMark[] = [];
    const deletions: LineDiffDeletion[] = [];
    let curLine = 0;
    let regionDels = 0;
    let regionAdds: number[] = [];

    function flushRegion() {
        if (regionDels === 0 && regionAdds.length === 0) return;
        const kind: 'added' | 'modified' = regionDels > 0 ? 'modified' : 'added';
        for (const ln of regionAdds) marks.push({ line: ln, kind });
        if (regionDels > 0 && regionAdds.length === 0) {
            // Pure deletion — anchor it after the last unchanged current line.
            deletions.push({ line: curLine, count: regionDels });
        }
        regionDels = 0;
        regionAdds = [];
    }

    for (const op of ops) {
        if (op === 'eq') {
            flushRegion();
            curLine++;
        } else if (op === 'add') {
            curLine++;
            regionAdds.push(curLine);
        } else { // del
            regionDels++;
        }
    }
    flushRegion();

    return { marks, deletions };
}

function splitLines(s: string): string[] {
    if (s === '') return [];
    const arr = s.split('\n');
    // Drop a phantom trailing empty entry produced by a final newline, so we
    // don't attribute changes to a line that doesn't visually exist.
    if (arr.length > 0 && arr[arr.length - 1] === '') arr.pop();
    return arr;
}

// ─── tri-state diff ────────────────────────────────────────────────────────

export interface LineTriDiff {
    /** Lines (1-based, in `current`) that differ from `savedRef` — the
     *  "unsaved changes" the user has typed since their last local save. */
    unsavedLines: number[];
    /** Lines that match `savedRef` but differ from `publishedRef` — i.e.,
     *  the saved-but-not-yet-published baseline. These show as a distinct
     *  colour in the gutter so the user can see "this line is in a save
     *  that hasn't been pushed yet." */
    savedLines: number[];
    /** Deletion anchors (lines in current after which deletions sit)
     *  relative to `savedRef`. Always counted as "unsaved" deletions
     *  because that's the most-recent reference. */
    unsavedDeletions: LineDiffDeletion[];
    /** Deletion anchors relative to `publishedRef` that didn't also
     *  appear in `unsavedDeletions` — i.e., deletions captured in a save
     *  but not yet published. */
    savedDeletions: LineDiffDeletion[];
}

/**
 * Three-way diff of `current` against TWO reference texts. Used by the
 * gutter to render distinct decorations for unsaved edits vs
 * saved-but-unpublished edits.
 *
 * Conventions:
 *   - `savedRef`     = the latest local save's content (or `publishedRef`
 *                      when there's no save yet — pass identical strings).
 *   - `publishedRef` = the last published commit's content for this path.
 *
 * Algorithm: run `lineDiff` twice and partition the result. A line that
 * differs from `savedRef` is "unsaved"; a line that's unchanged vs
 * `savedRef` but differs from `publishedRef` is "saved." Same for
 * deletions, using set-difference on the anchor line numbers.
 */
export function lineDiffTriState(
    publishedRef: string,
    savedRef: string,
    current: string,
): LineTriDiff {
    const unsaved = lineDiff(savedRef, current);
    const unsavedLineSet = new Set(unsaved.marks.map((m) => m.line));
    const unsavedDelAnchors = new Set(unsaved.deletions.map((d) => d.line));

    const savedLines: number[] = [];
    const savedDeletions: LineDiffDeletion[] = [];

    // Only compute the published-side diff when it's actually different
    // from savedRef — saves the LCS work when there are no local saves
    // (i.e., savedRef === publishedRef).
    if (publishedRef !== savedRef) {
        const published = lineDiff(publishedRef, current);
        for (const m of published.marks) {
            if (!unsavedLineSet.has(m.line)) savedLines.push(m.line);
        }
        for (const d of published.deletions) {
            if (!unsavedDelAnchors.has(d.line)) savedDeletions.push(d);
        }
    }

    return {
        unsavedLines: unsaved.marks.map((m) => m.line),
        savedLines,
        unsavedDeletions: unsaved.deletions,
        savedDeletions,
    };
}
