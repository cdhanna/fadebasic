// VM-crash UX. When a debug session emits REV_REQUEST_EXPLODE the main.ts
// handler resolves the failing `insIndex` to a source line and calls
// showCrashOverlay(...) here. The overlay owns:
//   - A red whole-line decoration on the failing line (sibling of the
//     yellow `.fade-current` paused-at-breakpoint style).
//   - A Monaco view zone rendered just below that line, containing the
//     error-kind chip + message + an Abort button.
//
// State is module-scoped so only one crash overlay can be active at a
// time — matches the runtime's "one error per program" reality. Callers
// must invoke hideCrashOverlay() before starting a new run so the
// previous decoration/zone don't bleed across executions.

import * as monaco from 'monaco-editor';

export type CrashErrorKind =
    | 'invalid-address'
    | 'divide-by-zero'
    | 'invalid-power'
    | 'invalid-memory-copy'
    | 'assert-failed'
    | 'system-error'
    | 'explode'
    | 'unknown';

export interface CrashOverlayArgs {
    editor: monaco.editor.IStandaloneCodeEditor;
    // 1-based Monaco line, in the file currently active on `editor`.
    line: number;
    // Sniffed from the inner-message prefix; controls the icon and
    // chip label. Falls back to 'explode' when no prefix matches.
    kind: CrashErrorKind;
    // Short, human-readable headline like "Array index out of bounds".
    title: string;
    // Optional secondary line with structured detail, e.g.
    // "Index 101 is outside the valid range 0…100." Null when there's
    // nothing useful to say beyond the title.
    detail: string | null;
    // True when the error originated from an unhandled .NET exception
    // in the VM host (as opposed to a structured Fade runtime error
    // like divide-by-zero). Drives a distinct chip + label so the user
    // can tell internal faults from "expected" Fade errors.
    isSystem?: boolean;
    // Click handler for the Abort button. Caller is responsible for
    // calling hideCrashOverlay() afterwards (or letting stopAll() do it).
    onAbort: () => void;
}

// Parse a REV_REQUEST_EXPLODE message — which arrives as the full JSON
// envelope, e.g. `{"id":-2,"type":6,"message":"invalid-address. ins=[240]
// index=[101] min=[0] max=[100]"}` — into a clean structured summary
// the UI can render. Falls back gracefully when the envelope shape
// changes or the inner message isn't one of the known kinds.
export function summarizeCrash(rawMessage: string): {
    kind: CrashErrorKind;
    title: string;
    detail: string | null;
    inner: string;
    isSystem: boolean;
} {
    let inner = rawMessage ?? '';
    let isSystem = false;
    try {
        const parsed = JSON.parse(inner);
        if (parsed && typeof parsed.message === 'string') {
            inner = parsed.message;
        }
        // The C# side sets isSystem=true on the ExplodedMessage envelope
        // when the error originated from an unhandled .NET exception (see
        // DebugSession.SendRuntimeErrorMessage). Surface that as the
        // authoritative flag — falls back to sniffing the kebab prefix
        // below if the envelope didn't carry it.
        if (parsed && typeof parsed.isSystem === 'boolean') {
            isSystem = parsed.isSystem;
        }
    } catch { /* not JSON, treat input as inner */ }

    const kind = detectCrashKind(inner);
    if (kind === 'system-error') isSystem = true;

    if (kind === 'system-error') {
        return {
            kind,
            title: 'Internal runtime error',
            detail: stripPrefix(inner, 'system-error'),
            inner,
            isSystem,
        };
    }
    if (kind === 'invalid-address') {
        // "invalid-address. ins=[240] index=[101] min=[0] max=[100]"
        const m = /index=\[(-?\d+)\][^\]]*?min=\[(-?\d+)\][^\]]*?max=\[(-?\d+)\]/.exec(inner);
        if (m) {
            return {
                kind,
                title: 'Array index out of bounds',
                detail: `Index ${m[1]} is outside the valid range ${m[2]}–${m[3]}.`,
                inner,
                isSystem,
            };
        }
        return { kind, title: 'Invalid memory access', detail: inner, inner, isSystem };
    }
    if (kind === 'divide-by-zero') {
        return { kind, title: 'Divide by zero', detail: null, inner, isSystem };
    }
    if (kind === 'invalid-power') {
        return { kind, title: 'Invalid exponent', detail: stripPrefix(inner, 'invalid-power'), inner, isSystem };
    }
    if (kind === 'invalid-memory-copy') {
        return { kind, title: 'Invalid memory copy', detail: stripPrefix(inner, 'invalid-memory-copy'), inner, isSystem };
    }
    if (kind === 'assert-failed') {
        const detail = stripPrefix(inner, /assert(ion)?-?failed/i);
        return { kind, title: 'Assertion failed', detail: detail || null, inner, isSystem };
    }
    // No kebab prefix matched. If the envelope flagged isSystem, treat it
    // as an internal fault even though the inner string didn't say so —
    // covers older messages or wrappers that strip the prefix.
    if (isSystem) {
        return { kind: 'system-error', title: 'Internal runtime error', detail: inner || null, inner, isSystem: true };
    }
    return { kind, title: 'Runtime error', detail: inner || null, inner, isSystem };
}

// Trim a kebab-case error prefix + trailing punctuation/whitespace
// from the start of a formatted message. Used to extract the
// human-readable tail without re-implementing the kind detector.
function stripPrefix(s: string, prefix: string | RegExp): string | null {
    const re = typeof prefix === 'string'
        ? new RegExp('^' + prefix.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '[.:\\s]*', 'i')
        : new RegExp('^(?:' + prefix.source + ')[.:\\s]*', prefix.flags.includes('i') ? 'i' : 'i');
    const tail = s.replace(re, '').trim();
    return tail.length > 0 ? tail : null;
}

interface ActiveOverlay {
    editor: monaco.editor.IStandaloneCodeEditor;
    model: monaco.editor.ITextModel;
    decorationIds: string[];
    widget: monaco.editor.IContentWidget;
    domNode: HTMLDivElement;
    // Listener disposal — when the user types past the crash line, monaco
    // edit events shift the line number on its own (decoration tracks),
    // but the content widget's anchor stays at the original line. We
    // dispose on hide rather than chase edits — the next stopAll/run-
    // start clears the overlay anyway.
    modelDisposeListener: monaco.IDisposable | null;
}

let active: ActiveOverlay | null = null;

const KIND_LABELS: Record<CrashErrorKind, string> = {
    'invalid-address': 'Invalid address',
    'divide-by-zero': 'Divide by zero',
    'invalid-power': 'Invalid power',
    'invalid-memory-copy': 'Invalid memory copy',
    'assert-failed': 'Assertion failed',
    'system-error': 'Internal error',
    'explode': 'Runtime error',
    'unknown': 'Runtime error',
};

// Sniff the kebab-case prefix at the start of the inner message
// (matches the format produced by VirtualMachine.cs error sites:
//  "invalid-address. ins=[115] …", "divide-by-zero. …", etc.). Returns
// 'unknown' when nothing matches so the caller doesn't have to special-
// case missing data.
export function detectCrashKind(message: string): CrashErrorKind {
    const head = message.trim().toLowerCase();
    if (head.startsWith('invalid-address')) return 'invalid-address';
    if (head.startsWith('divide-by-zero')) return 'divide-by-zero';
    if (head.startsWith('invalid-power')) return 'invalid-power';
    if (head.startsWith('invalid-memory-copy')) return 'invalid-memory-copy';
    if (head.startsWith('assert-failed') || head.startsWith('assertion')) return 'assert-failed';
    // Emitted by DebugSession's generic catch blocks for unhandled .NET
    // exceptions — we surface these to the user as "Internal error" so
    // it's clear the fault wasn't a normal Fade runtime condition.
    if (head.startsWith('system-error')) return 'system-error';
    if (head.startsWith('explode')) return 'explode';
    return 'unknown';
}

// Extract the failing instruction index from the formatted message
// (`ins=[N]`). The format is stable across all VirtualMachine.cs error
// sites — it's the one token we anchor on rather than re-parse the
// whole sentence. Returns null when absent so callers can fall back to
// the static error text without a line jump.
export function extractInsIndex(message: string): number | null {
    const m = /\bins=\[(\d+)\]/.exec(message);
    if (!m) return null;
    const n = Number(m[1]);
    return Number.isFinite(n) ? n : null;
}

export function showCrashOverlay(args: CrashOverlayArgs): void {
    // Tear down any previous overlay before painting the new one — guards
    // against rapid consecutive crashes (rare, but the wire allows it).
    hideCrashOverlay();

    const { editor, line, kind, title, detail, onAbort } = args;
    // Treat the kind 'system-error' as system as well — covers callers
    // that pass kind alone without the explicit boolean.
    const isSystem = args.isSystem === true || kind === 'system-error';
    const model = editor.getModel();
    if (!model) return;

    // Whole-line decoration: red background + red gutter glyph. Mirrors
    // the structure of setCurrentLine's .fade-current decoration so the
    // two can't visually collide (we clear .fade-current before painting).
    // System errors get a "(internal)" qualifier in the hover tooltip so
    // the user can tell at a glance that this isn't a normal Fade error.
    const kindLabel = isSystem ? `${KIND_LABELS[kind]} (internal)` : KIND_LABELS[kind];
    const hoverMd = detail
        ? `**${kindLabel}** — ${title}\n\n${detail}`
        : `**${kindLabel}** — ${title}`;
    const decorationIds = model.deltaDecorations([], [{
        range: new monaco.Range(line, 1, line, 1),
        options: {
            isWholeLine: true,
            className: isSystem ? 'fade-crashed fade-crashed-system' : 'fade-crashed',
            glyphMarginClassName: isSystem
                ? 'codicon codicon-bug fade-crashed fade-crashed-system'
                : 'codicon codicon-error fade-crashed',
            glyphMarginHoverMessage: { value: hoverMd },
        },
    }]);

    // Content widget — the same primitive Monaco uses for hover popups
    // and IntelliSense suggestions. Lives in the editor's
    // contentWidgets overlay layer, which is fully interactive (unlike
    // view zones, which are intended as display-only spacers and
    // intercept mousedown for cursor placement). We get clickable
    // buttons "for free" and Monaco handles repositioning on scroll.
    const domNode = document.createElement('div');
    domNode.className = isSystem ? 'fade-crash-zone fade-crash-zone-system' : 'fade-crash-zone';

    const inner = document.createElement('div');
    inner.className = 'fade-crash-zone-inner';

    const icon = document.createElement('span');
    // Bug glyph for system errors visually separates internal faults
    // from the standard red ! used for runtime errors the user caused.
    icon.className = isSystem
        ? 'fade-crash-icon codicon codicon-bug'
        : 'fade-crash-icon codicon codicon-error';

    const textCol = document.createElement('div');
    textCol.className = 'fade-crash-text';
    if (isSystem) {
        // A small chip above the title makes the "this is an internal
        // fault, not your code's fault" message unmistakable.
        const chip = document.createElement('span');
        chip.className = 'fade-crash-system-chip';
        chip.textContent = 'Internal error';
        textCol.appendChild(chip);
    }
    const titleEl = document.createElement('div');
    titleEl.className = 'fade-crash-title';
    titleEl.textContent = title;
    textCol.appendChild(titleEl);
    if (detail) {
        const detailEl = document.createElement('div');
        detailEl.className = 'fade-crash-detail';
        detailEl.textContent = detail;
        textCol.appendChild(detailEl);
    }

    const abortBtn = document.createElement('button');
    abortBtn.type = 'button';
    abortBtn.className = 'fade-crash-abort';
    abortBtn.textContent = 'Abort';
    abortBtn.title = 'Stop the program and clear this error';
    abortBtn.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();
        onAbort();
    });

    inner.append(icon, textCol, abortBtn);
    domNode.appendChild(inner);

    const widget: monaco.editor.IContentWidget = {
        getId: () => 'fade.crashOverlay',
        getDomNode: () => domNode,
        getPosition: () => ({
            position: { lineNumber: line, column: 1 },
            preference: [
                monaco.editor.ContentWidgetPositionPreference.BELOW,
                monaco.editor.ContentWidgetPositionPreference.ABOVE,
            ],
        }),
    };
    editor.addContentWidget(widget);

    // If the model is disposed (user closes the file), tear down the
    // overlay so the next show() doesn't try to deltaDecorations a
    // freed model.
    const modelDisposeListener = model.onWillDispose(() => hideCrashOverlay());

    active = { editor, model, decorationIds, widget, domNode, modelDisposeListener };

    // Reveal the failing line so the user actually sees the widget.
    try {
        editor.revealLineInCenterIfOutsideViewport(line, monaco.editor.ScrollType.Smooth);
    } catch { /* editor may not be fully ready */ }
}

export function hideCrashOverlay(): void {
    if (!active) return;
    const { editor, model, decorationIds, widget, modelDisposeListener } = active;
    active = null;

    try { modelDisposeListener?.dispose(); } catch { /* ignore */ }
    try { model.deltaDecorations(decorationIds, []); } catch { /* model may be disposed */ }
    try { editor.removeContentWidget(widget); } catch { /* editor may be torn down */ }
}

export function hasActiveCrashOverlay(): boolean {
    return active !== null;
}
