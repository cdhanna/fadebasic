// Monaco gutter decorator — paints per-line markers on the editor margin
// indicating which lines have been added or modified relative to the last
// synced base. Hooked per Monaco model in main.ts.
//
// Refresh triggers:
//   - model content change (debounced; uses last-known base, no fetch)
//   - external refresh (sharing status changed → maybe new base, refetch)
//   - initial attach (one fetch)
//
// The base content is supplied by an injected `getBaseText` so this module
// stays free of GitHub-adapter coupling and is easy to test against a mock.

import type * as monaco from 'monaco-editor';
import { lineDiffTriState, type LineTriDiff } from './line-diff';

export interface AttachGutterOptions {
    model: monaco.editor.ITextModel;
    /** Text of this file at the user's latest local save. Null when the
     *  file isn't part of any save (added since publish). When null AND
     *  `getPublishedText` returns a value, the gutter falls back to
     *  treating that as the reference (current behaviour pre-saves). */
    getSavedText: () => Promise<string | null>;
    /** Text of this file at the last published commit. Null when the
     *  file isn't on the remote yet. */
    getPublishedText: () => Promise<string | null>;
    /** Subscribe to external refresh requests. Return value unsubscribes. */
    onShouldRefresh: (listener: () => void) => () => void;
    /** Debounce ms before refetching reference texts after model change.
     *  Default 400. */
    debounceMs?: number;
}

export interface GutterHandle {
    dispose(): void;
}

export function attachGutter(opts: AttachGutterOptions): GutterHandle {
    let decorationIds: string[] = [];
    let pendingTimer: number | undefined;
    /** Last-known reference texts. Both nullable. When non-null they're
     *  the inputs to `lineDiffTriState`. */
    let lastSavedText: string | null = null;
    let lastPublishedText: string | null = null;
    let disposed = false;

    function applyDecorations() {
        if (disposed) return;
        // No reference at all → no decorations to show. Common for a file
        // that's never been part of any save or publish.
        if (lastSavedText === null && lastPublishedText === null) {
            decorationIds = opts.model.deltaDecorations(decorationIds, []);
            return;
        }
        // Fall back to a single-text reference when one side is missing —
        // e.g. a file that exists in the latest save but not in the
        // remote, or vice versa.
        const savedRef = lastSavedText ?? lastPublishedText ?? '';
        const publishedRef = lastPublishedText ?? lastSavedText ?? '';
        const current = opts.model.getValue();
        const diff = lineDiffTriState(publishedRef, savedRef, current);
        decorationIds = opts.model.deltaDecorations(decorationIds, buildDecorations(diff));
    }

    async function refetchAndRender() {
        if (disposed) return;
        try {
            const [saved, published] = await Promise.all([
                opts.getSavedText(),
                opts.getPublishedText(),
            ]);
            if (disposed) return;
            lastSavedText = saved;
            lastPublishedText = published;
            applyDecorations();
        } catch {
            lastSavedText = null;
            lastPublishedText = null;
            applyDecorations();
        }
    }

    function scheduleRefetch() {
        if (pendingTimer != null) clearTimeout(pendingTimer);
        pendingTimer = window.setTimeout(() => {
            pendingTimer = undefined;
            void refetchAndRender();
        }, opts.debounceMs ?? 400);
    }

    // Model content changes → cheap rerender with the last-known base. Skip
    // a fetch; the post-save refresh path (onShouldRefresh) handles base
    // staleness independently.
    //
    // The line-diff is O(N*M) over file size, so for ~1000-line files this
    // is non-trivial per keystroke. Debounce to ~120 ms — well under one
    // editor tick so it still feels live, but coalesces typing bursts.
    let rerenderTimer: number | undefined;
    const RERENDER_DEBOUNCE_MS = 120;
    const contentSub = opts.model.onDidChangeContent(() => {
        if (rerenderTimer != null) clearTimeout(rerenderTimer);
        rerenderTimer = window.setTimeout(() => {
            rerenderTimer = undefined;
            applyDecorations();
        }, RERENDER_DEBOUNCE_MS);
    });

    const unsubRefresh = opts.onShouldRefresh(() => { scheduleRefetch(); });

    // Initial render.
    void refetchAndRender();

    return {
        dispose() {
            disposed = true;
            contentSub.dispose();
            unsubRefresh();
            if (pendingTimer != null) clearTimeout(pendingTimer);
            if (rerenderTimer != null) clearTimeout(rerenderTimer);
            opts.model.deltaDecorations(decorationIds, []);
            decorationIds = [];
        },
    };
}

function buildDecorations(diff: LineTriDiff): monaco.editor.IModelDeltaDecoration[] {
    const out: monaco.editor.IModelDeltaDecoration[] = [];
    // Unsaved edits — top priority colour (orange-ish). Live changes
    // since the last save.
    for (const line of diff.unsavedLines) {
        out.push({
            range: { startLineNumber: line, startColumn: 1, endLineNumber: line, endColumn: 1 },
            options: {
                isWholeLine: false,
                linesDecorationsClassName: 'sharing-gutter sharing-gutter-unsaved',
                overviewRuler: { color: '#ffb74d', position: 4 },
            },
        });
    }
    // Saved-but-not-yet-published edits — distinct colour (purple-ish)
    // so the user can see "this is captured in a save, waiting for
    // Publish."
    for (const line of diff.savedLines) {
        out.push({
            range: { startLineNumber: line, startColumn: 1, endLineNumber: line, endColumn: 1 },
            options: {
                isWholeLine: false,
                linesDecorationsClassName: 'sharing-gutter sharing-gutter-saved',
                overviewRuler: { color: '#b88aff', position: 4 },
            },
        });
    }
    for (const d of diff.unsavedDeletions) {
        const anchorLine = d.line === 0 ? 1 : d.line;
        const cls = d.line === 0
            ? 'sharing-gutter sharing-gutter-deletion-above sharing-gutter-deletion-unsaved'
            : 'sharing-gutter sharing-gutter-deletion-below sharing-gutter-deletion-unsaved';
        out.push({
            range: { startLineNumber: anchorLine, startColumn: 1, endLineNumber: anchorLine, endColumn: 1 },
            options: {
                linesDecorationsClassName: cls,
                overviewRuler: { color: '#f44336', position: 4 },
            },
        });
    }
    for (const d of diff.savedDeletions) {
        const anchorLine = d.line === 0 ? 1 : d.line;
        const cls = d.line === 0
            ? 'sharing-gutter sharing-gutter-deletion-above sharing-gutter-deletion-saved'
            : 'sharing-gutter sharing-gutter-deletion-below sharing-gutter-deletion-saved';
        out.push({
            range: { startLineNumber: anchorLine, startColumn: 1, endLineNumber: anchorLine, endColumn: 1 },
            options: {
                linesDecorationsClassName: cls,
                overviewRuler: { color: '#b88aff', position: 4 },
            },
        });
    }
    return out;
}
