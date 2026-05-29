// FadeBasic runtime — hostable from either a Web Worker or an
// iframe's main thread.
//
// Two hosts use this file:
//
//   1. The Playground's lspWorker: spawned as a Web Worker.
//      worker.js (a thin shim) imports this module and wires
//      its self.postMessage / self.onmessage to onMessage /
//      dispatch.
//
//   2. The Export.Web preview iframe: index.html imports this
//      module directly on the main thread and wires its
//      window.parent.postMessage / window.message events to
//      the same surface.
//
// Wire protocol (what the host sees) is identical in both
// cases — same message type names, same payload shapes. The
// only differences between Worker-host and iframe-host are:
//
//   - Where `postMessage` lands (worker → page, vs. iframe → parent).
//   - Whether print/host-message events are consumed by an iframe-
//     local handler (iframe-host only).
//
// The runtime itself is host-agnostic. setRole only matters for
// diagnostic tagging on heartbeat / log events.

import { dotnet } from './_framework/dotnet.js';

// ─── Host I/O ──────────────────────────────────────────────────────────
// Subscriber set for outgoing events. The host calls onMessage(fn) to
// hook itself in; emit() fans out to all current subscribers. Set-based
// so multiple subscribers can coexist (the iframe sometimes wants to
// observe events alongside the relay-to-parent logic).
const _listeners = new Set();
function emit(msg) {
    for (const fn of _listeners) fn(msg);
}

export function onMessage(fn) {
    _listeners.add(fn);
    return () => _listeners.delete(fn);
}

let exports = null;
const _queue = [];

// Role tag carried on heartbeats / logs. Defaults to 'vm' so the
// iframe-host doesn't need to setRole before init. The Worker-host
// shim sets it to 'lsp' on the lspWorker side.
let role = 'vm';
export function setRole(r) { role = r === 'lsp' ? 'lsp' : 'vm'; }

function log(message) {
    emit({ type: 'log', message, role });
}

// ─── Heartbeat ─────────────────────────────────────────────────────────
let heartbeatTick = 0;
setInterval(() => {
    heartbeatTick = (heartbeatTick + 1) | 0;
    emit({ type: 'heartbeat', tick: heartbeatTick, t: Date.now(), role });
}, 500);

// ─── Cooperative run-pump ──────────────────────────────────────────────
// The export bundle runs user programs via FadeBridge.RunStart +
// repeated FadeBridge.RunTick calls instead of one synchronous
// LoadAndRun. Between ticks we yield to the host's event loop
// (setTimeout 0) so messages from the page — prompt answers, stop,
// etc. — can land between batches.
//
// Tick status drives scheduling:
//   complete=true              → run is over; emit result, stop pumping.
//   waitingForHostReply=true   → VM suspended waiting for a host-reply.
//                                Stop pumping; a host-reply restarts.
//   waitMs > 0                 → setTimeout(waitMs) before next tick.
//   suspended/otherwise        → setTimeout(0): yield + continue.
let runPumpActive = false;
let runMsgId = null;
let pumpTimerId = null;
let runPumpTerminalType = 'run-tick-result';

const RUN_TICK_BUDGET = 50000;

function pumpRunTick() {
    pumpTimerId = null;
    if (!runPumpActive) return;
    let status;
    try {
        const json = exports.FadeBasic.Export.Web.FadeBridge.RunTick(RUN_TICK_BUDGET);
        status = JSON.parse(json);
    } catch (e) {
        runPumpActive = false;
        const err = String(e?.message ?? e);
        const envelope = runPumpTerminalType === 'run-tests-result'
            ? { passed: 0, failed: 0, duration: 0, results: [], error: err, printed: '' }
            : { ok: false, error: err };
        emit({ type: runPumpTerminalType, id: runMsgId, result: JSON.stringify(envelope) });
        runMsgId = null;
        runPumpTerminalType = 'run-tick-result';
        return;
    }
    if (status.testProgress) {
        emit({ type: 'test-progress', result: status.testProgress });
    }
    if (status.testStarting) {
        emit({ type: 'test-starting', testName: status.testStarting.name });
    }
    if (status.complete) {
        runPumpActive = false;
        let envelope;
        if (runPumpTerminalType === 'run-tests-result' && status.testFinal) {
            envelope = {
                passed: status.testFinal.passed,
                failed: status.testFinal.failed,
                duration: status.testFinal.duration,
                results: status.testFinal.results,
                error: status.error ?? null,
                printed: '',
            };
        } else {
            envelope = { ok: !status.error, error: status.error ?? null };
        }
        emit({ type: runPumpTerminalType, id: runMsgId, result: JSON.stringify(envelope) });
        runMsgId = null;
        runPumpTerminalType = 'run-tick-result';
        return;
    }
    if (status.waitingForHostReply) return;
    const delay = status.waitMs > 0 ? status.waitMs : 0;
    pumpTimerId = setTimeout(pumpRunTick, delay);
}

// ─── Debug tick loop ───────────────────────────────────────────────────
const DEBUG_TICK_BUDGET = 10000;
let debugTicking = false;
function startDebugTickLoop() {
    debugTicking = true;
    pumpDebugTick();
}
function pumpDebugTick() {
    if (!debugTicking) return;
    let result;
    try {
        const json = exports.FadeBasic.Export.Web.FadeBridge.DebugTick(DEBUG_TICK_BUDGET);
        result = JSON.parse(json);
    } catch (e) {
        debugTicking = false;
        emit({ type: 'debug-event', event: { type: 'error', message: String(e?.message ?? e) } });
        return;
    }
    if (result.messages && result.messages.length) {
        for (const m of result.messages) {
            emit({ type: 'debug-event', event: m });
        }
    }
    if (result.complete) {
        debugTicking = false;
        emit({ type: 'debug-event', event: { type: 'complete' } });
        return;
    }
    let delay;
    if (result.waitMs && result.waitMs > 0) delay = result.waitMs;
    else if (result.paused) delay = 50;
    else delay = 0;
    setTimeout(pumpDebugTick, delay);
}

// ─── Boot ──────────────────────────────────────────────────────────────
export async function init() {
    log('creating .NET runtime...');
    const runtime = await dotnet.create();
    log('runtime created, registering JS imports...');

    runtime.setModuleImports('web-commands', {
        onPrint: (line) => emit({ type: 'print', line }),
        getLocation: () => '(unavailable in worker context)',
        getUserAgent: () => self.navigator?.userAgent ?? '(unavailable)',
        alert: (msg) => emit({ type: 'alert', msg }),
    });

    runtime.setModuleImports('fade-runtime', {
        postHostMessage: (channel, payload) =>
            emit({ type: 'host-message', channel, payload }),
    });

    log('registering assembly exports...');
    const config = runtime.getConfig();
    exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    log('exports loaded');

    while (_queue.length) handle(_queue.shift());
    emit({ type: 'ready', role });
}

// ─── Inbound dispatch ──────────────────────────────────────────────────
// `dispatch(msg)` is the host's entry point — every incoming message
// from the host's environment (worker.postMessage or window.message)
// goes through here. Same semantics as worker.js's handle() used to
// have, minus the role-based misroute checks (those existed only
// because the lspWorker / vmWorker were both processes with parallel
// op surfaces; now the LSP worker is the only Worker context, and
// the iframe is always the VM target).
export async function dispatch(msg) {
    if (!exports) { _queue.push(msg); return; }
    handle(msg);
}

function handle(msg) {
    if (!msg) return;
    // Cheap roundtrip for heartbeat probes.
    if (msg.type === 'ping') {
        emit({ type: 'pong', id: msg.id, t: Date.now() });
        return;
    }

    const FB = exports.FadeBasic.Export.Web.FadeBridge;

    if (msg.type === 'run') {
        let result;
        try {
            result = FB.CompileAndRun(msg.source);
        } catch (e) {
            result = 'Worker error: ' + (e?.message ?? e);
        }
        emit({ type: 'result', id: msg.id, result });
    } else if (msg.type === 'lsp-set') {
        log('lsp-set: calling LspSetDocument');
        let diagnosticsJson = '[]';
        try {
            diagnosticsJson = FB.LspSetDocument(msg.uri, msg.text);
            log('lsp-set: returned, length=' + diagnosticsJson.length);
        } catch (e) {
            log('lsp-set failed: ' + (e?.message ?? e));
        }
        emit({
            type: 'lsp-diagnostics',
            uri: msg.uri,
            version: msg.version,
            diagnostics: diagnosticsJson,
        });
    } else if (msg.type === 'lsp-tokens') {
        let tokensJson = '[]';
        try { tokensJson = FB.LspGetSemanticTokens(msg.uri); }
        catch (e) { log('lsp-tokens failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-tokens-result', id: msg.id, uri: msg.uri, tokens: tokensJson });
    } else if (msg.type === 'lsp-hover') {
        let hoverJson = 'null';
        try { hoverJson = FB.LspHover(msg.uri, msg.line, msg.character); }
        catch (e) { log('lsp-hover failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-hover-result', id: msg.id, hover: hoverJson });
    } else if (msg.type === 'lsp-completion') {
        let json = '[]';
        try { json = FB.LspCompletion(msg.uri, msg.line, msg.character); }
        catch (e) { log('lsp-completion failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-completion-result', id: msg.id, items: json });
    } else if (msg.type === 'lsp-all-diagnostics') {
        let json = '{}';
        try { json = FB.LspGetAllDiagnostics(); }
        catch (e) { log('lsp-all-diagnostics failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-all-diagnostics-result', id: msg.id, all: json });
    } else if (msg.type === 'lsp-signature-help') {
        let json = 'null';
        try { json = FB.LspSignatureHelp(msg.uri, msg.line, msg.character); }
        catch (e) { log('lsp-signature-help failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-signature-help-result', id: msg.id, sig: json });
    } else if (msg.type === 'lsp-references') {
        let json = '[]';
        try { json = FB.LspReferences(msg.uri, msg.line, msg.character); }
        catch (e) { log('lsp-references failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-references-result', id: msg.id, refs: json });
    } else if (msg.type === 'lsp-definition') {
        let json = 'null';
        try { json = FB.LspDefinition(msg.uri, msg.line, msg.character); }
        catch (e) { log('lsp-definition failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-definition-result', id: msg.id, def: json });
    } else if (msg.type === 'lsp-document-symbols') {
        let json = '[]';
        try { json = FB.LspDocumentSymbols(msg.uri); }
        catch (e) { log('lsp-document-symbols failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-document-symbols-result', id: msg.id, symbols: json });
    } else if (msg.type === 'lsp-folding-ranges') {
        let json = '[]';
        try { json = FB.LspFoldingRanges(msg.uri); }
        catch (e) { log('lsp-folding-ranges failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-folding-ranges-result', id: msg.id, ranges: json });
    } else if (msg.type === 'lsp-format') {
        let json = '[]';
        try { json = FB.LspFormat(msg.uri, msg.options || ''); }
        catch (e) { log('lsp-format failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-format-result', id: msg.id, edits: json });
    } else if (msg.type === 'lsp-format-range') {
        let json = '[]';
        try {
            json = FB.LspFormatRange(
                msg.uri, msg.options || '',
                msg.startLine, msg.startCh, msg.endLine, msg.endCh);
        } catch (e) { log('lsp-format-range failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-format-range-result', id: msg.id, edits: json });
    } else if (msg.type === 'lsp-format-on-type') {
        let json = '[]';
        try { json = FB.LspFormatOnType(msg.uri, msg.options || '', msg.line, msg.character); }
        catch (e) { log('lsp-format-on-type failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-format-on-type-result', id: msg.id, edits: json });
    } else if (msg.type === 'lsp-rename') {
        let json = 'null';
        try { json = FB.LspRename(msg.uri, msg.line, msg.character, msg.newName); }
        catch (e) { log('lsp-rename failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-rename-result', id: msg.id, edit: json });
    } else if (msg.type === 'load-assembly') {
        let json = '{"ok":false,"error":"unknown"}';
        try {
            const bytes = msg.dllBytes instanceof Uint8Array ? msg.dllBytes : new Uint8Array(msg.dllBytes);
            json = FB.LoadAssembly(bytes);
        } catch (e) {
            json = JSON.stringify({ ok: false, error: String(e?.message ?? e) });
            log('load-assembly failed: ' + (e?.message ?? e));
        }
        emit({ type: 'load-assembly-result', id: msg.id, result: json });
    } else if (msg.type === 'run-start'
            || msg.type === 'run-start-source'
            || msg.type === 'run-start-bytecode') {
        let startJson = '{"ok":false,"error":"unknown"}';
        try {
            if (msg.type === 'run-start') {
                const bytes = msg.dllBytes instanceof Uint8Array ? msg.dllBytes : new Uint8Array(msg.dllBytes);
                startJson = FB.RunStart(bytes);
            } else if (msg.type === 'run-start-source') {
                startJson = FB.RunStartFromSource(msg.source || '');
            } else {
                const bytes = msg.bytecode instanceof Uint8Array ? msg.bytecode : new Uint8Array(msg.bytecode);
                startJson = FB.RunStartFromBytecode(bytes);
            }
        } catch (e) {
            startJson = JSON.stringify({ ok: false, error: String(e?.message ?? e) });
            log(msg.type + ' failed: ' + (e?.message ?? e));
        }
        try {
            const parsed = JSON.parse(startJson);
            if (!parsed.ok) {
                emit({
                    type: 'run-tick-result', id: msg.id,
                    result: JSON.stringify({
                        ok: false,
                        error: parsed.error ?? null,
                        compileError: parsed.compileError ?? null,
                    }),
                });
                return;
            }
        } catch { /* keep going; pump will report */ }
        runPumpActive = true;
        runMsgId = msg.id;
        runPumpTerminalType = 'run-tick-result';
        pumpTimerId = setTimeout(pumpRunTick, 0);
    } else if (msg.type === 'stop-run') {
        if (pumpTimerId != null) { clearTimeout(pumpTimerId); pumpTimerId = null; }
        try { FB.StopRun(); }
        catch (e) { log('stop-run failed: ' + (e?.message ?? e)); }
        if (runPumpActive) pumpTimerId = setTimeout(pumpRunTick, 0);
    } else if (msg.type === 'compile-to-bytecode') {
        let bytecode = null, statusJson = '{"ok":false}';
        try {
            statusJson = FB.CompileToBytecodeStatus(msg.source || '');
            const status = JSON.parse(statusJson);
            if (status.ok) bytecode = FB.CompileToBytecode(msg.source || '');
        } catch (e) {
            statusJson = JSON.stringify({ ok: false, error: String(e?.message ?? e) });
            log('compile-to-bytecode failed: ' + (e?.message ?? e));
        }
        const bytecodeBuf = bytecode ? bytecode.buffer : null;
        emit({ type: 'compile-to-bytecode-result', id: msg.id, status: statusJson, bytecode: bytecodeBuf });
    } else if (msg.type === 'host-reply') {
        try {
            switch (msg.resultType) {
                case 'string': FB.DepositResultString(msg.value ?? ''); break;
                case 'int':    FB.DepositResultInt((msg.value | 0)); break;
                case 'real':   FB.DepositResultReal(+msg.value); break;
                case 'bool':   FB.DepositResultBool(!!msg.value); break;
                case 'byte':   FB.DepositResultByte((msg.value | 0) & 0xff); break;
                case 'word':   FB.DepositResultWord((msg.value | 0) & 0xffff); break;
                case 'dword':  FB.DepositResultDword((msg.value >>> 0) | 0); break;
                case 'dint':   FB.DepositResultDint(BigInt(msg.value ?? 0)); break;
                case 'dfloat': FB.DepositResultDfloat(+msg.value); break;
                case 'void':   FB.DepositResultVoid(); break;
                default:
                    log('host-reply: unknown resultType=' + msg.resultType);
                    FB.DepositResultVoid();
                    break;
            }
        } catch (e) {
            log('host-reply failed: ' + (e?.message ?? e));
        }
        if (runPumpActive) pumpTimerId = setTimeout(pumpRunTick, 0);
    } else if (msg.type === 'register-command-assembly') {
        let json = '{"ok":false,"error":"unknown"}';
        try {
            const bytes = msg.dllBytes instanceof Uint8Array ? msg.dllBytes : new Uint8Array(msg.dllBytes);
            json = FB.RegisterCommandAssembly(bytes, msg.className);
        } catch (e) {
            json = JSON.stringify({ ok: false, error: String(e?.message ?? e) });
            log('register-command-assembly failed: ' + (e?.message ?? e));
        }
        emit({ type: 'register-command-assembly-result', id: msg.id, result: json });
    } else if (msg.type === 'clear-command-assemblies') {
        try { FB.ClearCommandAssemblies(); }
        catch (e) { log('clear-command-assemblies failed: ' + (e?.message ?? e)); }
        emit({ type: 'clear-command-assemblies-result', id: msg.id });
    } else if (msg.type === 'set-project-type') {
        let resolved = msg.projectType;
        try { resolved = FB.SetProjectType(msg.projectType); }
        catch (e) { log('set-project-type failed: ' + (e?.message ?? e)); }
        emit({ type: 'set-project-type-result', id: msg.id, projectType: resolved });
    } else if (msg.type === 'debug-start' || msg.type === 'debug-start-test') {
        let json = '{}';
        try {
            json = msg.type === 'debug-start-test'
                ? FB.DebugStartTest(msg.source, msg.testName || '')
                : FB.DebugStart(msg.source);
        } catch (e) {
            log(msg.type + ' failed: ' + (e?.message ?? e));
        }
        emit({ type: 'debug-start-result', id: msg.id, result: json });
        try {
            const parsed = JSON.parse(json);
            if (parsed?.ok && !debugTicking) startDebugTickLoop();
        } catch { /* ignore */ }
    } else if (msg.type === 'get-debug-test-result') {
        let json = 'null';
        try { json = FB.GetDebugTestResult(); }
        catch (e) { log('get-debug-test-result failed: ' + (e?.message ?? e)); }
        emit({ type: 'get-debug-test-result-result', id: msg.id, result: json });
    } else if (msg.type === 'debug-terminate') {
        debugTicking = false;
        try { FB.DebugTerminate(); } catch (e) { log('terminate failed: ' + e); }
        emit({ type: 'debug-terminate-result', id: msg.id });
    } else if (msg.type === 'debug-set-breakpoints') {
        try { FB.DebugSetBreakpoints(msg.linesJson); }
        catch (e) { log('set-bp failed: ' + e); }
        emit({ type: 'debug-set-breakpoints-result', id: msg.id });
    } else if (msg.type === 'debug-step') {
        try { FB.DebugStep(msg.kind); }
        catch (e) { log('step failed: ' + e); }
        emit({ type: 'debug-step-result', id: msg.id });
    } else if (msg.type === 'debug-continue') {
        try { FB.DebugContinue(); }
        catch (e) { log('continue failed: ' + e); }
        emit({ type: 'debug-continue-result', id: msg.id });
    } else if (msg.type === 'debug-pause') {
        try { FB.DebugPause(); }
        catch (e) { log('pause failed: ' + e); }
        emit({ type: 'debug-pause-result', id: msg.id });
    } else if (msg.type === 'debug-stack-frames') {
        let json = '[]';
        try { json = FB.DebugStackFrames(); }
        catch (e) { log('stack-frames failed: ' + e); }
        emit({ type: 'debug-stack-frames-result', id: msg.id, frames: json });
    } else if (msg.type === 'debug-scopes') {
        let json = '{}';
        try { json = FB.DebugScopes(msg.frameId); }
        catch (e) { log('scopes failed: ' + e); }
        emit({ type: 'debug-scopes-result', id: msg.id, scopes: json });
    } else if (msg.type === 'debug-variable-expansion') {
        let json = '{}';
        try { json = FB.DebugVariableExpansion(msg.variableId); }
        catch (e) { log('var-expand failed: ' + e); }
        emit({ type: 'debug-variable-expansion-result', id: msg.id, scopes: json });
    } else if (msg.type === 'debug-eval') {
        let json = 'null';
        try { json = FB.DebugEval(msg.frameId, msg.expression); }
        catch (e) { log('eval failed: ' + e); }
        emit({ type: 'debug-eval-result', id: msg.id, result: json });
    } else if (msg.type === 'debug-repl') {
        let json = 'null';
        try { json = FB.DebugRepl(msg.frameId, msg.code); }
        catch (e) { log('repl failed: ' + e); }
        emit({ type: 'debug-repl-result', id: msg.id, result: json });
    } else if (msg.type === 'debug-set-variable') {
        let json = 'null';
        try { json = FB.DebugSetVariable(msg.frameId, msg.variableId, msg.rhs); }
        catch (e) { log('set-var failed: ' + e); }
        emit({ type: 'debug-set-variable-result', id: msg.id, result: json });
    } else if (msg.type === 'list-tests') {
        let json = '[]';
        try { json = FB.ListTests(msg.source); }
        catch (e) { log('list-tests failed: ' + (e?.message ?? e)); }
        emit({ type: 'list-tests-result', id: msg.id, tests: json });
    } else if (msg.type === 'list-command-docs') {
        let json = '[]';
        try { json = FB.ListCommandDocs(); }
        catch (e) { log('list-command-docs failed: ' + (e?.message ?? e)); }
        emit({ type: 'list-command-docs-result', id: msg.id, docs: json });
    } else if (msg.type === 'lsp-tokenize-snippet') {
        let json = '[]';
        try { json = FB.LspTokenizeSnippet(msg.source ?? ''); }
        catch (e) { log('lsp-tokenize-snippet failed: ' + (e?.message ?? e)); }
        emit({ type: 'lsp-tokenize-snippet-result', id: msg.id, tokens: json });
    } else if (msg.type === 'get-version-info') {
        let json = '{}';
        try { json = FB.GetVersionInfo(); }
        catch (e) { log('get-version-info failed: ' + (e?.message ?? e)); }
        emit({ type: 'get-version-info-result', id: msg.id, info: json });
    } else if (msg.type === 'run-tests') {
        let startJson = '{"ok":false}';
        try {
            startJson = FB.RunTestsStart(msg.source || '', msg.testName || '');
        } catch (e) {
            startJson = JSON.stringify({ ok: false, error: String(e?.message ?? e) });
            log('run-tests-start failed: ' + (e?.message ?? e));
        }
        try {
            const parsed = JSON.parse(startJson);
            if (!parsed.ok) {
                emit({
                    type: 'run-tests-result', id: msg.id,
                    result: JSON.stringify({
                        passed: 0, failed: 0, duration: 0, results: [],
                        error: parsed.compileError ?? parsed.error ?? 'unknown',
                        printed: '',
                    }),
                });
                return;
            }
        } catch { /* fall through to pump */ }
        runPumpActive = true;
        runMsgId = msg.id;
        runPumpTerminalType = 'run-tests-result';
        pumpTimerId = setTimeout(pumpRunTick, 0);
    }
}
