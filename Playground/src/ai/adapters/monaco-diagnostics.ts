// Monaco-backed implementation of DiagnosticsProvider.
//
// Diagnostics flow LSP worker → main.ts handler → monaco markers (owner:'fade').
// Querying Monaco gives us the same data the Problems panel renders, kept
// in sync automatically. We just need to translate marker shape → our
// agent-facing shape (workspace-relative paths, severity enum names).
//
// This file imports `monaco` directly; tests should use a hand-rolled
// in-memory DiagnosticsProvider instead of stubbing Monaco.

import * as monaco from 'monaco-editor';
import type { DiagnosticEntry, DiagnosticSeverity, DiagnosticsProvider } from '../tools';

const FADE_MARKER_OWNER = 'fade';

/** Strip the `/workspace/` prefix Monaco's URIs carry. */
function uriToWorkspacePath(uri: monaco.Uri): string {
    const p = uri.path;
    const m = /\/workspace\/(.+)$/.exec(p);
    return m ? m[1] : p.replace(/^\/+/, '');
}

function severityFromMarker(s: monaco.MarkerSeverity): DiagnosticSeverity {
    if (s === monaco.MarkerSeverity.Error) return 'error';
    if (s === monaco.MarkerSeverity.Warning) return 'warning';
    if (s === monaco.MarkerSeverity.Info) return 'info';
    return 'hint';
}

function markerToEntry(m: monaco.editor.IMarker): DiagnosticEntry {
    return {
        path: uriToWorkspacePath(m.resource),
        severity: severityFromMarker(m.severity),
        line: m.startLineNumber,
        column: m.startColumn,
        endLine: m.endLineNumber,
        endColumn: m.endColumn,
        message: m.message,
        code: typeof m.code === 'string' ? m.code : m.code?.value,
        source: m.source ?? FADE_MARKER_OWNER,
    };
}

/** The production DiagnosticsProvider used by the playground. */
export const monacoDiagnosticsProvider: DiagnosticsProvider = {
    async getAll() {
        const markers = monaco.editor.getModelMarkers({ owner: FADE_MARKER_OWNER });
        return markers.map(markerToEntry);
    },

    async forFile(path) {
        const uri = monaco.Uri.file(`/workspace/${path}`);
        const markers = monaco.editor.getModelMarkers({ owner: FADE_MARKER_OWNER, resource: uri });
        return markers.map(markerToEntry);
    },
};
