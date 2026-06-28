// Live-validation hook for .fx files. Runs the FX framing parser + HLSL
// translator on every model change (debounced) and surfaces whatever
// diagnostics surface as Monaco markers. This is the "as-you-type" half of
// shader editor feedback; the runtime half (KNI's GL compile errors after
// Reset) lives in shader-markers.ts.
//
// Caveats — what this can and can't catch:
//   ✓ FX framing errors: unterminated technique/pass/cbuffer/sampler_state
//     blocks, missing braces, etc. These come from parseFx and carry a
//     source offset we can map to line/column.
//   ✓ Translator diagnostics: missing entry function, ES 3.00 syntax
//     where ES 1.00 is expected, etc. These come from the HLSL translator.
//   ✗ GLSL syntax errors inside function bodies: e.g. a stray `a` token
//     without a semicolon, undeclared identifier, type mismatches. Our
//     translator passes the function body through verbatim, so syntax
//     issues only surface when KNI compiles the resulting GLSL at runtime
//     (after Reset). For those, see shader-markers.ts.

import * as monaco from 'monaco-editor';
import { parseFx } from './fx-parser';
import { translateHlslToGlsl } from './hlsl-translator';
import { validateGlsl } from './glsl-validator';

const MARKER_OWNER = 'shader-static';
const DEBOUNCE_MS = 300;

// Per-model debounce timers so each .fx file maintains its own throttle.
const _timers = new Map<string, number>();

// Track which models we've already hooked so multiple openFile calls on
// the same model don't stack listeners + dup validation runs.
const _hookedModels = new WeakSet<monaco.editor.ITextModel>();

export function attachShaderValidator(model: monaco.editor.ITextModel): void {
    if (model.getLanguageId() !== 'fadefx') {
        console.log('[shader-validator] skipping non-fadefx model:', model.uri.toString());
        return;
    }
    if (_hookedModels.has(model)) {
        // Already hooked — just kick off an immediate validation in case
        // the source has changed since the last keystroke triggered one.
        console.log('[shader-validator] already hooked, running immediate pass:', model.uri.toString());
        runValidation(model);
        return;
    }
    _hookedModels.add(model);
    console.log('[shader-validator] hooking onDidChangeContent for', model.uri.toString());
    // Initial pass — fire immediately rather than waiting for the first
    // keystroke, so opening a known-bad file shows squigglies right away.
    runValidation(model);
    const dispose = model.onDidChangeContent(() => {
        const key = model.uri.toString();
        const existing = _timers.get(key);
        if (existing !== undefined) clearTimeout(existing);
        _timers.set(key, window.setTimeout(() => {
            _timers.delete(key);
            runValidation(model);
        }, DEBOUNCE_MS));
    });
    // When the model is disposed, drop the listener + clear any pending timer.
    model.onWillDispose(() => {
        const t = _timers.get(model.uri.toString());
        if (t !== undefined) {
            clearTimeout(t);
            _timers.delete(model.uri.toString());
        }
        _hookedModels.delete(model);
        dispose.dispose();
    });
}

// Manual trigger for debugging from the dev tools console:
//   window.__debugShaderValidator()
// Runs validation against every .fx model that's currently open and logs
// everything it sees + every marker it sets. Useful when "I edit but
// nothing happens" needs to be narrowed to which step is broken.
(window as any).__debugShaderValidator = () => {
    const fxModels = monaco.editor.getModels().filter((m) => m.getLanguageId() === 'fadefx');
    console.log('[shader-validator] __debugShaderValidator: found', fxModels.length, 'fadefx model(s)');
    for (const m of fxModels) {
        console.log('[shader-validator] running validation for', m.uri.toString());
        runValidation(m);
    }
};

// Snapshot the per-model "what generation is currently in flight" so that
// an out-of-order glslang resolution (which is async) doesn't overwrite
// markers computed from a newer keystroke. Each runValidation bumps the
// counter and only writes markers when the resolved counter still matches.
const _genByModel = new WeakMap<monaco.editor.ITextModel, number>();

function runValidation(model: monaco.editor.ITextModel): void {
    const source = model.getValue();
    const markers: monaco.editor.IMarkerData[] = [];
    const myGen = (_genByModel.get(model) ?? 0) + 1;
    _genByModel.set(model, myGen);
    console.log('[shader-validator] runValidation gen=' + myGen, 'uri=', model.uri.toString(), 'sourceLen=', source.length);

    try {
        const fx = parseFx(source);

        // FX-parser warnings: each one has a sourceOffset we can map to
        // (line, column) directly.
        for (const w of fx.warnings) {
            const pos = offsetToPosition(source, w.sourceOffset);
            markers.push({
                severity: monaco.MarkerSeverity.Warning,
                message: w.message,
                startLineNumber: pos.line,
                startColumn: pos.column,
                endLineNumber: pos.line,
                endColumn: pos.column + 1,
                source: 'fx',
            });
        }

        // For each (stage, entrypoint) the FX framing references, run the
        // translator and collect its diagnostics, then feed the resulting
        // GLSL to glslang (WASM, lazy-loaded) for real syntax + type
        // checking. glslang errors are mapped back to .fx source lines via
        // the translator's preambleLineCount.
        const seen = new Set<string>();
        const glslangTasks: Promise<void>[] = [];
        for (const t of fx.techniques) {
            for (const p of t.passes) {
                for (const a of p.assigns) {
                    if (a.kind !== 'shader' || !a.entrypoint) continue;
                    const stage =
                        a.name === 'PixelShader' || a.name === 'pixelshader' ? 'pixel'
                      : a.name === 'VertexShader' || a.name === 'vertexshader' ? 'vertex'
                      : null;
                    if (!stage) continue;
                    const key = `${stage}:${a.entrypoint}`;
                    if (seen.has(key)) continue;
                    seen.add(key);
                    const result = translateHlslToGlsl({
                        source: fx.hlslStripped,
                        entrypoint: a.entrypoint,
                        stage,
                        cbuffers: fx.cbuffers,
                        samplerStateLiterals: fx.samplerStateLiterals,
                    });
                    for (const d of result.diagnostics) {
                        // Translator diagnostics don't currently carry column
                        // info — only an optional line. Point at the start
                        // of the reported line; for unmapped diagnostics
                        // (no line at all) fall back to line 1.
                        const line = d.line ?? 1;
                        markers.push({
                            severity: d.severity === 'error'
                                ? monaco.MarkerSeverity.Error
                                : monaco.MarkerSeverity.Warning,
                            message: `[${stage} ${a.entrypoint}] ${d.message}`,
                            startLineNumber: line,
                            startColumn: 1,
                            endLineNumber: line,
                            endColumn: 999,
                            source: 'fx',
                        });
                    }
                    // Schedule the glslang validation pass. We feed it a
                    // copy of the translated GLSL with an explicit
                    // `#version 100` directive — required for glslang to
                    // parse as ES 1.00; KNI accepts version-less GLSL but
                    // glslang doesn't.
                    glslangTasks.push((async () => {
                        // We hand the translator's raw GLSL to validateGlsl;
                        // it internally upgrades ES 1.00 → ES 3.10 for the
                        // SPIR-V compile path, and returns how many lines
                        // it had to prepend so we can subtract them when
                        // mapping errors back.
                        console.log('[shader-validator] calling validateGlsl for stage=', stage, 'entry=', a.entrypoint, 'glslLen=', result.glsl.length);
                        const validation = await validateGlsl(result.glsl, stage);
                        console.log('[shader-validator] validateGlsl returned',
                            'diagnostics=', validation.diagnostics.length,
                            'unavailable=', validation.glslangUnavailable ?? false,
                            'addedHeaderLines=', validation.addedHeaderLines,
                        );
                        // Bail if a newer keystroke superseded us.
                        if (_genByModel.get(model) !== myGen) return;
                        // Two preamble offsets to subtract from glslang's
                        // line numbers to get back to the .fx source:
                        //   - addedHeaderLines: lines the validator prepended
                        //     to satisfy ES 3.10's #version + precision +
                        //     out-fragColor requirements
                        //   - preambleLineCount: lines the translator
                        //     prepended (precision, cbuffers, varyings)
                        //     before the user's body
                        const offset = validation.addedHeaderLines + result.preambleLineCount;
                        // Temporary diagnostic — surface the line-mapping math
                        // so we can verify the offset is what we expect when
                        // markers land on the wrong line.
                        for (const d of validation.diagnostics) {
                            console.log(
                                '[shader-validator] mapping diagnostic:',
                                'glslLine=', d.line,
                                'addedHeader=', validation.addedHeaderLines,
                                'translatorPreamble=', result.preambleLineCount,
                                'offset=', offset,
                                'fxLine=', Math.max(1, d.line - offset),
                                'message=', d.message,
                            );
                        }
                        for (const d of validation.diagnostics) {
                            const fxLine = Math.max(1, d.line - offset);
                            markers.push({
                                severity: d.severity === 'error'
                                    ? monaco.MarkerSeverity.Error
                                    : monaco.MarkerSeverity.Warning,
                                message: d.token
                                    ? `[${stage}] '${d.token}': ${d.message}`
                                    : `[${stage}] ${d.message}`,
                                startLineNumber: fxLine,
                                startColumn: 1,
                                endLineNumber: fxLine,
                                endColumn: 999,
                                source: 'glsl',
                            });
                        }
                        if (validation.glslangUnavailable && validation.glslangUnavailableReason) {
                            // Warning level (visible squiggle) rather than Info
                            // (invisible) so the user actually notices that
                            // their live syntax checking isn't running. This
                            // means a marker WILL appear on .fx files when
                            // glslang fails to load — and the message text
                            // names the failure cause so they can act.
                            markers.push({
                                severity: monaco.MarkerSeverity.Warning,
                                message:
                                    'Live shader validation is unavailable. ' +
                                    'Runtime errors after Reset still surface as markers.\n\n' +
                                    'Reason: ' + validation.glslangUnavailableReason,
                                startLineNumber: 1, startColumn: 1,
                                endLineNumber: 1, endColumn: 999,
                                source: 'glslang',
                            });
                        }
                        // Re-write markers atomically — apply the latest set
                        // (synchronous markers above + async glslang
                        // additions) so the user doesn't see partial state.
                        if (_genByModel.get(model) === myGen) {
                            console.log('[shader-validator] setModelMarkers (post-glslang)',
                                'count=', markers.length, 'uri=', model.uri.toString());
                            monaco.editor.setModelMarkers(model, MARKER_OWNER, markers);
                        } else {
                            console.log('[shader-validator] skipping setModelMarkers — newer gen has superseded',
                                'mine=', myGen, 'current=', _genByModel.get(model));
                        }
                    })());
                }
            }
        }

        // No technique with a shader assignment at all? Surface a hint —
        // the user wrote HLSL but never declared a pass binding it.
        let foundEntry = false;
        for (const t of fx.techniques) {
            for (const p of t.passes) {
                for (const a of p.assigns) {
                    if (a.kind === 'shader' && a.entrypoint) { foundEntry = true; break; }
                }
            }
        }
        if (!foundEntry && source.trim().length > 0) {
            markers.push({
                severity: monaco.MarkerSeverity.Warning,
                message: 'No `technique { pass { PixelShader = compile … }; };` block found — ' +
                         'the shader will compile to an XNB but no pass binds an entry point.',
                startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 999,
                source: 'fx',
            });
        }
    } catch (e: any) {
        // Validator throwing is a translator-level bug, not a user error.
        // Still surface a marker so the user knows something's wrong rather
        // than wondering why their squigglies disappeared.
        markers.push({
            severity: monaco.MarkerSeverity.Error,
            message: `Shader validator crashed: ${e?.message ?? e}. This is a translator bug — please report.`,
            startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 999,
            source: 'fx',
        });
    }

    // Set the synchronous markers (FX parser + translator diagnostics)
    // immediately so the user sees feedback within ~300ms of typing even
    // if glslang is still loading or slow. The async glslang tasks above
    // re-call setModelMarkers with the merged set when they resolve.
    console.log('[shader-validator] setModelMarkers (sync)',
        'count=', markers.length, 'uri=', model.uri.toString());
    monaco.editor.setModelMarkers(model, MARKER_OWNER, markers);
}

// Map a byte/char offset into the source to a 1-based (line, column).
// Used to translate parseFx's `sourceOffset` into Monaco's coordinate space.
function offsetToPosition(source: string, offset: number): { line: number; column: number } {
    if (offset <= 0) return { line: 1, column: 1 };
    let line = 1;
    let col = 1;
    for (let i = 0; i < offset && i < source.length; i++) {
        if (source[i] === '\n') {
            line++;
            col = 1;
        } else {
            col++;
        }
    }
    return { line, column: col };
}
