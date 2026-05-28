// Three-way text merge. The classic problem: given a common ancestor
// (`base`), a local edit (`ours`), and a remote edit (`theirs`), produce a
// merged result that incorporates both sides' changes — emitting git-style
// conflict markers for regions where both sides changed the same lines
// differently.
//
// Algorithm (line-level, LCS-anchored):
//   1. Compute the LCS of (base, ours) and (base, theirs). Each gives us a
//      list of "matching pairs" — base indices that appear unchanged in the
//      respective other side.
//   2. Intersect by base index → candidate triple anchors. Filter to a
//      monotonically-increasing chain in (base, ours, theirs) so the
//      anchors form a valid alignment across all three.
//   3. Anchors partition each input into corresponding regions. For each
//      region:
//        - both sides unchanged → take base
//        - only `ours` changed   → take ours
//        - only `theirs` changed → take theirs
//        - both changed identically → take either
//        - both changed differently → conflict
//
// LCS table is O(N*M) memory; fine for playground-sized files (<<10k
// lines). Migration to Myers' O((N+M)*D) is a future optimization.

const MARK_OURS  = '<<<<<<<';
const MARK_MID   = '=======';
const MARK_THEIRS = '>>>>>>>';

export interface ConflictRegion {
    /** 1-based line in the merged output where the `<<<<<<<` marker sits. */
    line: number;
    ours: string[];
    theirs: string[];
    base: string[];
}

export interface Diff3Result {
    /** Merged text — newline-joined, with a trailing newline preserved iff
     *  the inputs had one. Contains `<<<<<<< ======= >>>>>>>` markers for
     *  unresolvable regions. */
    merged: string;
    hasConflicts: boolean;
    conflicts: ConflictRegion[];
}

export interface Diff3Options {
    /** Label written after `<<<<<<<` (default: "ours"). */
    oursLabel?: string;
    /** Label written after `>>>>>>>` (default: "theirs"). */
    theirsLabel?: string;
}

/**
 * Merge `ours` and `theirs` against their common ancestor `base`. Returns
 * the merged text (with conflict markers where automatic merge couldn't
 * resolve) and the list of conflict regions for UI reporting.
 */
export function diff3Merge(
    base: string,
    ours: string,
    theirs: string,
    opts: Diff3Options = {},
): Diff3Result {
    const oursLabel = opts.oursLabel ?? 'ours';
    const theirsLabel = opts.theirsLabel ?? 'theirs';

    const { lines: baseLines, hadTrailingNewline } = splitPreservingTrailingNewline(base);
    const oursLines = splitPreservingTrailingNewline(ours).lines;
    const theirsLines = splitPreservingTrailingNewline(theirs).lines;

    // Trivial shortcuts.
    if (arrayEq(oursLines, theirsLines)) {
        return { merged: assemble(oursLines, hadTrailingNewline), hasConflicts: false, conflicts: [] };
    }
    if (arrayEq(baseLines, oursLines)) {
        return { merged: assemble(theirsLines, hadTrailingNewline), hasConflicts: false, conflicts: [] };
    }
    if (arrayEq(baseLines, theirsLines)) {
        return { merged: assemble(oursLines, hadTrailingNewline), hasConflicts: false, conflicts: [] };
    }

    const lcsA = lcsPairs(baseLines, oursLines);
    const lcsB = lcsPairs(baseLines, theirsLines);

    // Intersect on base index; greedy-filter to monotonic-in-(a,b).
    const lcsBByBase = new Map<number, number>();
    for (const p of lcsB) lcsBByBase.set(p.base, p.other);
    const candidates: Array<{ base: number; a: number; b: number }> = [];
    for (const p of lcsA) {
        const bIdx = lcsBByBase.get(p.base);
        if (bIdx !== undefined) candidates.push({ base: p.base, a: p.other, b: bIdx });
    }
    candidates.sort((x, y) => x.base - y.base);
    const anchors: Array<{ base: number; a: number; b: number }> = [];
    let lastA = -1; let lastB = -1;
    for (const c of candidates) {
        if (c.a > lastA && c.b > lastB) {
            anchors.push(c);
            lastA = c.a;
            lastB = c.b;
        }
    }

    // Walk the input arrays in lockstep, partitioning by anchors. Each
    // anchor consumes ONE line from each side (the matched line itself).
    const out: string[] = [];
    const conflicts: ConflictRegion[] = [];
    let bi = 0; let ai = 0; let ti = 0;
    for (const anc of anchors) {
        emitRegion(
            baseLines.slice(bi, anc.base),
            oursLines.slice(ai, anc.a),
            theirsLines.slice(ti, anc.b),
            out, conflicts, oursLabel, theirsLabel,
        );
        out.push(baseLines[anc.base]);
        bi = anc.base + 1;
        ai = anc.a + 1;
        ti = anc.b + 1;
    }
    // Trailing region past the last anchor.
    emitRegion(
        baseLines.slice(bi),
        oursLines.slice(ai),
        theirsLines.slice(ti),
        out, conflicts, oursLabel, theirsLabel,
    );

    return {
        merged: assemble(out, hadTrailingNewline),
        hasConflicts: conflicts.length > 0,
        conflicts,
    };
}

/** True iff the text contains any of the three diff3 conflict marker lines.
 *  Used by the UI to gate "Mark resolved" — once removed by the user, the
 *  file can be re-committed. */
export function hasConflictMarkers(text: string): boolean {
    return /^<{7} |^={7}$|^>{7} /m.test(text);
}

export interface ParsedConflictRegion {
    /** 1-based line number of the `<<<<<<<` marker. */
    startLine: number;
    /** 1-based line number of the `=======` separator. */
    midLine: number;
    /** 1-based line number of the `>>>>>>>` marker. */
    endLine: number;
    ours: string[];
    theirs: string[];
    /** Raw text on the start marker after `<<<<<<<` (e.g. label). */
    oursLabel: string;
    theirsLabel: string;
}

/**
 * Scan a file's text for diff3 conflict regions. Returns regions in order
 * (top to bottom). Unterminated markers (a `<<<<<<<` without a matching
 * `>>>>>>>`) are skipped — the parser only emits well-formed triplets.
 *
 * Used by the conflict editor to mark live regions and by the source-
 * control panel to detect "still has markers" state.
 */
export function parseConflictRegions(text: string): ParsedConflictRegion[] {
    const lines = text.split('\n');
    const out: ParsedConflictRegion[] = [];
    let i = 0;
    while (i < lines.length) {
        const line = lines[i];
        const startMatch = /^<{7}\s?(.*)$/.exec(line);
        if (!startMatch) { i++; continue; }
        const oursLabel = startMatch[1] ?? '';
        const startLine = i + 1;
        // Scan forward for the matching ======= and >>>>>>>. If we hit
        // another <<<<<<< before either, the region is malformed and we
        // skip past this start marker.
        let mid = -1;
        let end = -1;
        let endLabel = '';
        for (let j = i + 1; j < lines.length; j++) {
            if (/^<{7}\s?/.test(lines[j])) break;
            if (mid < 0 && /^={7}$/.test(lines[j])) { mid = j; continue; }
            const endMatch = /^>{7}\s?(.*)$/.exec(lines[j]);
            if (endMatch && mid > 0) {
                end = j;
                endLabel = endMatch[1] ?? '';
                break;
            }
        }
        if (mid > 0 && end > mid) {
            out.push({
                startLine,
                midLine: mid + 1,
                endLine: end + 1,
                ours: lines.slice(i + 1, mid),
                theirs: lines.slice(mid + 1, end),
                oursLabel,
                theirsLabel: endLabel,
            });
            i = end + 1;
        } else {
            i++;
        }
    }
    return out;
}

// ─── classification of a single anchor-bounded region ──────────────────────

function emitRegion(
    base: string[],
    ours: string[],
    theirs: string[],
    out: string[],
    conflicts: ConflictRegion[],
    oursLabel: string,
    theirsLabel: string,
): void {
    const oursChanged = !arrayEq(base, ours);
    const theirsChanged = !arrayEq(base, theirs);

    if (!oursChanged && !theirsChanged) {
        for (const line of base) out.push(line);
        return;
    }
    if (oursChanged && !theirsChanged) {
        for (const line of ours) out.push(line);
        return;
    }
    if (!oursChanged && theirsChanged) {
        for (const line of theirs) out.push(line);
        return;
    }
    // Both changed.
    if (arrayEq(ours, theirs)) {
        for (const line of ours) out.push(line);
        return;
    }
    // True conflict — embed git-style markers.
    const markerLine = out.length + 1;       // 1-based line of the `<<<<<<<`
    out.push(`${MARK_OURS} ${oursLabel}`);
    for (const line of ours) out.push(line);
    out.push(MARK_MID);
    for (const line of theirs) out.push(line);
    out.push(`${MARK_THEIRS} ${theirsLabel}`);
    conflicts.push({ line: markerLine, ours, theirs, base });
}

// ─── line-pair LCS ─────────────────────────────────────────────────────────

interface MatchPair { base: number; other: number; }

function lcsPairs(base: string[], other: string[]): MatchPair[] {
    const m = base.length; const n = other.length;
    if (m === 0 || n === 0) return [];
    const cols = n + 1;
    const dp = new Int32Array((m + 1) * cols);
    for (let i = m - 1; i >= 0; i--) {
        for (let j = n - 1; j >= 0; j--) {
            if (base[i] === other[j]) dp[i * cols + j] = dp[(i + 1) * cols + (j + 1)] + 1;
            else dp[i * cols + j] = Math.max(dp[(i + 1) * cols + j], dp[i * cols + (j + 1)]);
        }
    }
    const out: MatchPair[] = [];
    let i = 0; let j = 0;
    while (i < m && j < n) {
        if (base[i] === other[j]) { out.push({ base: i, other: j }); i++; j++; }
        else if (dp[(i + 1) * cols + j] >= dp[i * cols + (j + 1)]) i++;
        else j++;
    }
    return out;
}

// ─── helpers ───────────────────────────────────────────────────────────────

function arrayEq(a: string[], b: string[]): boolean {
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
    return true;
}

interface SplitResult { lines: string[]; hadTrailingNewline: boolean; }

function splitPreservingTrailingNewline(s: string): SplitResult {
    if (s === '') return { lines: [], hadTrailingNewline: false };
    const hadTrailingNewline = s.endsWith('\n');
    let body = s;
    if (hadTrailingNewline) body = body.slice(0, -1);
    return { lines: body.split('\n'), hadTrailingNewline };
}

function assemble(lines: string[], trailingNewline: boolean): string {
    if (lines.length === 0) return trailingNewline ? '\n' : '';
    return lines.join('\n') + (trailingNewline ? '\n' : '');
}
