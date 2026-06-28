// Bridge KNI's effect-load errors to Monaco markers on the corresponding
// .fx file, so shader compile failures show up as red squigglies in the
// editor instead of just plain-text lines in the Output panel.
//
// The errors arrive as multi-line stderr text from the iframe, shaped like:
//
//   [fade] effect load failed: 'test': Shader Compilation Failed.
//   ERROR: 0:26: 'input' : Illegal use of reserved word
//   ERROR: 0:26: 'input' : syntax error
//
// We collect every line of one error sequence (header + each `ERROR:` line)
// into one marker per .fx file. The line number in the GLSL doesn't map
// exactly to the .fx source (the translator inserts varying decls /
// trampoline lines), so we put the marker at line 1 with the full text;
// the user reads the message + jumps to the right line manually. Tracking
// real source positions through the translator is a future upgrade.
//
// Lifecycle:
//   - clearShaderMarkers() runs at the start of each Run, wiping stale
//     markers so a passing recompile shows no squigglies.
//   - captureShaderErrorLine() consumes a stderr line — returns true if
//     the line was a shader-error line (the caller still emits it to the
//     Output panel either way, this just side-effects markers).
//   - flushPending() emits the accumulated marker; called when a non-
//     shader-error stderr line arrives, or explicitly at end-of-stream.

import * as monaco from 'monaco-editor';

interface PendingError {
    assetName: string;     // e.g. 'test' (no .fx extension)
    headerLine: string;    // the `effect load failed: …` line
    errorLines: string[];  // each `ERROR: …` line
}

let _pending: PendingError | null = null;

// Match the header of an effect-compile-failure stderr block. Anchored
// loosely so it survives any leading log-level prefix the iframe adds.
const HEADER_RE = /effect load failed:\s*'([^']+)'/;
// `ERROR: <a>:<b>: …` — GLSL drivers vary on whether <a>:<b> is
// `column:line` or `shader_index:line`. We don't care about the number
// for marker placement; we render the whole line into the marker message.
const ERROR_LINE_RE = /^\s*ERROR:/;

export function captureShaderErrorLine(line: string): boolean {
    const startMatch = line.match(HEADER_RE);
    if (startMatch) {
        // New shader-error sequence starting; flush any previous one.
        if (_pending) flushPending();
        _pending = {
            assetName: startMatch[1],
            headerLine: line,
            errorLines: [],
        };
        // Diagnostic log — surfaces in dev-tools console so we can verify
        // the capture pipeline is firing on real stderr. Remove once this
        // path has been confirmed working in production usage.
        console.log('[shader-markers] header captured for', startMatch[1]);
        return true;
    }
    if (_pending && ERROR_LINE_RE.test(line)) {
        _pending.errorLines.push(line);
        console.log('[shader-markers] error line:', line);
        return true;
    }
    // A non-ERROR line breaks the sequence — flush whatever we accumulated
    // so the marker lands even if there's more stderr to come from other
    // sources.
    if (_pending) flushPending();
    return false;
}

export function flushPending(): void {
    const p = _pending;
    _pending = null;
    if (!p) return;
    const fxPath = `${p.assetName}.fx`;
    const uri = monaco.Uri.file(`/workspace/${fxPath}`);
    const model = monaco.editor.getModel(uri);
    if (!model) {
        console.warn(
            '[shader-markers] flushPending: no model at', uri.toString(),
            '— marker dropped. Existing models:',
            monaco.editor.getModels().map((m) => m.uri.toString()),
        );
        return;
    }
    console.log('[shader-markers] flushing marker to', uri.toString(), 'with', p.errorLines.length, 'error lines');

    // Compose a readable error message. The header gets a friendly prefix
    // (the raw `[fade] effect load failed: …` is technical); each ERROR
    // line stays verbatim so users can correlate to the GLSL output the
    // translator produced.
    const headerMsg = p.headerLine.replace(/^.*effect load failed:\s*'[^']*':\s*/, '').trim();
    const body = p.errorLines.length > 0
        ? '\n' + p.errorLines.join('\n')
        : '\n(KNI did not provide per-line error details — see the Output panel ' +
          'for what stderr printed. Line numbers if any refer to the *generated* ' +
          'GLSL, not your .fx source, since the translator inserts varying decls + ' +
          'a main() trampoline.)';

    monaco.editor.setModelMarkers(model, MARKER_OWNER, [{
        startLineNumber: 1,
        startColumn: 1,
        endLineNumber: 1,
        endColumn: model.getLineMaxColumn(1),
        severity: monaco.MarkerSeverity.Error,
        message: `Shader compile failed: ${headerMsg}${body}`,
        source: 'shader-runtime',
    }]);
}

// Wipe shader markers on every .fx model. Called at the start of each Run
// so a fresh compile gets a clean slate; if errors re-occur we set new
// markers, and if not the user sees no squigglies.
export function clearShaderMarkers(): void {
    _pending = null;
    for (const m of monaco.editor.getModels()) {
        const u = m.uri.toString();
        if (u.endsWith('.fx')) {
            monaco.editor.setModelMarkers(m, MARKER_OWNER, []);
        }
    }
}

// Marker owner string — Monaco namespaces markers by owner so we can clear
// only our own without disturbing the fade-language compile markers.
const MARKER_OWNER = 'shader-runtime';
