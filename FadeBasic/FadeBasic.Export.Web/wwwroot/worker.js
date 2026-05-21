// Dedicated module worker: hosts the .NET runtime + Fade compiler/VM.
// Bootstraps the runtime once, then handles run requests from the page
// via postMessage.

// Relative import (not '/_framework/...') so the runner is portable: hosts can
// mount this worker at any subpath (e.g. /runtime/worker.js) and the import
// still resolves correctly relative to the worker's own URL.
import { dotnet } from './_framework/dotnet.js';

let exports = null;
const queue = [];
// Each worker hosts a .NET runtime + bridge. The page boots TWO of these:
//
//   role='lsp' — handles LSP traffic (set-document, hover, completion,
//                semantic tokens, …) plus the lightweight `list-tests`
//                compile step. Stays responsive at all times.
//   role='vm'  — owns the live VM. Handles `run`, `run-tests`, and the
//                whole `debug-*` family. May get sync-blocked by user
//                code calling Thread.Sleep (e.g. `wait ms`) — that's
//                expected; the lsp worker keeps the page responsive.
//
// Both workers post heartbeats so the UI can distinguish "page is alive"
// from "VM is alive". The role flips behavior at message-dispatch time;
// the .NET runtime + JS module bindings are identical on both.
let role = 'lsp';

function log(message) {
    self.postMessage({ type: 'log', message, role });
}

// ─── Heartbeat ──────────────────────────────────────────────────────────────
// Posts a beat to the main thread every 500ms so the UI can show a
// "worker alive" indicator. A synchronous Thread.Sleep inside the VM
// (e.g. `wait ms`) blocks this worker thread entirely, which means the
// heartbeats stop until the sleep returns — that's exactly what we want
// to surface to the user as a busy state.
let heartbeatTick = 0;
setInterval(() => {
    heartbeatTick = (heartbeatTick + 1) | 0;
    self.postMessage({ type: 'heartbeat', tick: heartbeatTick, t: Date.now(), role });
}, 500);

// ─── Cooperative run-pump for the load-and-run flow ────────────────────────
// The export bundle runs user programs via FadeBridge.RunStart + repeated
// FadeBridge.RunTick calls instead of one synchronous LoadAndRun. Between
// ticks we yield to the worker's event loop (setTimeout 0) so postMessage
// from the page — prompt answers, future pause/stop, etc. — can land.
//
// Tick status drives scheduling:
//   complete=true              → run is over; emit result, stop pumping.
//   waitingForHostReply=true   → VM suspended waiting for a host-reply
//                                (prompt$ or any other cooperative cmd
//                                that called HostBridge.SuspendVm). Stop
//                                pumping entirely; a `host-reply` will
//                                deposit a value and resume the pump.
//   waitMs > 0                 → `wait ms` asked us to delay; setTimeout
//                                for that long, then tick again.
//   suspended/otherwise        → setTimeout(0): yield + continue.
let runPumpActive = false;
let runMsgId = null;
// Tracks the most recent setTimeout scheduled by pumpRunTick. Held so
// stop-run can clear it — otherwise a long wait-ms scheduled tick would
// have to wait out its delay before observing the stop flag.
let pumpTimerId = null;
// Which terminal message type the pump emits when it observes
// status.complete. Defaults to 'run-tick-result' (Run flow); the
// run-tests handler swaps it to 'run-tests-result' before kicking
// the pump so the same loop drives both flavors of execution.
let runPumpTerminalType = 'run-tick-result';

const RUN_TICK_BUDGET = 50000; // opcodes per batch — tune if heartbeat lags

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
        // Same terminal-type discipline as the success path so a
        // run-tests error surfaces in the test panel and a run-tick
        // error surfaces in the run flow.
        const envelope = runPumpTerminalType === 'run-tests-result'
            ? { passed: 0, failed: 0, duration: 0, results: [], error: err, printed: '' }
            : { ok: false, error: err };
        self.postMessage({
            type: runPumpTerminalType,
            id: runMsgId,
            result: JSON.stringify(envelope),
        });
        runMsgId = null;
        runPumpTerminalType = 'run-tick-result';
        return;
    }
    // Per-test progress: every tick that finalizes a test (in test
    // mode) carries a testProgress object — same shape as one entry
    // in the terminal testFinal.results array. Forward as a separate
    // event so the Playground's test panel can flip rows mid-run
    // instead of waiting for the terminal envelope. Happens for the
    // final test too, so the listener doesn't need a "last-test"
    // special-case.
    if (status.testProgress) {
        self.postMessage({ type: 'test-progress', result: status.testProgress });
    }
    // Per-test boundary: when a new test takes over the VM (mid-tick),
    // notify the iframe so it can clear its output area. Carries just
    // the new test's name — the iframe doesn't need anything else,
    // and the Playground side doesn't need to know about test
    // boundaries (it consumes finalized results via test-progress).
    if (status.testStarting) {
        self.postMessage({ type: 'test-starting', testName: status.testStarting.name });
    }
    if (status.complete) {
        runPumpActive = false;
        // Two envelope shapes based on which flavor of execution drove
        // this pump. Tests carry testFinal (aggregated per-test
        // results) and emit run-tests-result; Run carries just ok/error
        // and emits run-tick-result.
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
        self.postMessage({
            type: runPumpTerminalType,
            id: runMsgId,
            result: JSON.stringify(envelope),
        });
        runMsgId = null;
        runPumpTerminalType = 'run-tick-result';
        return;
    }
    if (status.waitingForHostReply) {
        // Halt the pump. A `host-reply` message will restart it via
        // pumpRunTick() after depositing the value (see the host-reply
        // dispatcher in the message handler below).
        return;
    }
    const delay = status.waitMs > 0 ? status.waitMs : 0;
    pumpTimerId = setTimeout(pumpRunTick, delay);
}

// ─── Debug tick loop ────────────────────────────────────────────────────────
// While a debug session is active, we yield to the worker's message pump
// between batches of VM instructions so inbound messages (step/continue/
// breakpoint changes/host-reply) can land. Tick budget is sized for two
// competing goals:
//   - Big enough that VM tick overhead doesn't dominate tight loops
//     (each tick is ~one JS→WASM→back round-trip).
//   - Small enough that user input (pause/step/breakpoint changes)
//     is honored quickly — at the worst case a click takes one whole
//     tick to land. 10K opcodes is ~0.2–2ms on modern hardware, well
//     under one frame.
// `wait ms` short-circuits the budget anyway via Suspend, so the budget
// is purely a control-responsiveness knob (not a wait knob).
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
        self.postMessage({ type: 'debug-event', event: { type: 'error', message: String(e?.message ?? e) } });
        return;
    }
    // Forward any outbound events back to the main thread.
    if (result.messages && result.messages.length) {
        for (const m of result.messages) {
            self.postMessage({ type: 'debug-event', event: m });
        }
    }
    // NOTE: do NOT re-emit `result.printed` here. The Print command in
    // WebCommands.cs streams every line live via the `web-commands.onPrint`
    // JSImport (handled below in setModuleImports), which means every
    // line already reached the page as a `print` message during execution.
    // Re-emitting the drained buffer would duplicate each line — and the
    // duplicate is especially visible at end-of-session when DebugTick
    // returns with `complete: true` and a fully-drained buffer.
    if (result.complete) {
        debugTicking = false;
        self.postMessage({ type: 'debug-event', event: { type: 'complete' } });
        return;
    }
    // Cooperative `wait ms`: when WaitImpl fired during this tick, it
    // stashed the duration in _pendingWaitMs and the C# side surfaces
    // it as result.waitMs. Use that as the delay before the next tick
    // so the program experiences a real wait — but the worker thread
    // stays free, heartbeats keep firing, and pause/step requests can
    // land during the wait. Paused state still uses 50ms cadence so we
    // don't burn CPU when the user is sitting on a breakpoint.
    let delay;
    if (result.waitMs && result.waitMs > 0) delay = result.waitMs;
    else if (result.paused) delay = 50;
    else delay = 0;
    setTimeout(pumpDebugTick, delay);
}

async function init() {
    log('creating .NET runtime...');
    // Do NOT call runMain() — Program.cs ends with host.RunAsync() which never
    // returns, hanging the worker forever. Skip Main; bootstrap manually.
    const runtime = await dotnet.create();
    log('runtime created, registering JS imports...');

    // Worker-side implementation of the "web-commands" module. The C# side
    // declares [JSImport(..., "web-commands")] for each of these; main-thread
    // mode satisfies them by loading web-commands.js, worker mode satisfies
    // them here so we never hit "module not registered" errors.
    runtime.setModuleImports('web-commands', {
        onPrint: (line) => self.postMessage({ type: 'print', line }),
        getLocation: () => '(unavailable in worker context)',
        getUserAgent: () => self.navigator?.userAgent ?? '(unavailable)',
        alert: (msg) => self.postMessage({ type: 'alert', msg }),
    });

    // Runtime-level "fade-runtime" module. FadeBridge.PostHostMessage
    // forwards any library's HostBridge.PostMessage call to here, which
    // fans out as a generic `host-message` to the page. The page's
    // hostHandlers map routes by channel name and posts back a typed
    // `host-reply`. This is the extension point for plugin authors:
    // their library uses HostBridge.PostMessage("their-channel", payload),
    // their page-side handler is registered under that channel name, and
    // neither this worker.js nor FadeBridge ever needs to know about it.
    runtime.setModuleImports('fade-runtime', {
        postHostMessage: (channel, payload) =>
            self.postMessage({ type: 'host-message', channel, payload }),
    });

    log('registering assembly exports...');
    const config = runtime.getConfig();
    exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    log('exports loaded');

    while (queue.length) handle(queue.shift());
    self.postMessage({ type: 'ready', role });
}

// Op → required role. Anything not listed is treated as either-side.
const VM_OPS = new Set([
    'run', 'run-tests',
    'run-start', 'run-start-source', 'run-start-bytecode',
    'stop-run',
    'compile-to-bytecode',
    'host-reply',
    'debug-start', 'debug-start-test', 'debug-terminate',
    'debug-set-breakpoints', 'debug-step', 'debug-continue', 'debug-pause',
    'debug-stack-frames', 'debug-scopes', 'debug-variable-expansion',
    'debug-eval', 'debug-repl', 'debug-set-variable',
    'get-debug-test-result',
]);

function handle(msg) {
    // Configuration is always accepted — it's what makes us either role.
    if (msg.type === 'configure') {
        role = msg.role === 'vm' ? 'vm' : 'lsp';
        return;
    }
    // Cheap roundtrip for the heartbeat probes.
    if (msg.type === 'ping') {
        self.postMessage({ type: 'pong', id: msg.id, t: Date.now() });
        return;
    }
    // Sanity guard: if the page accidentally sends a VM op to the LSP
    // worker (or vice-versa), surface a clear error instead of silently
    // dropping the message.
    const isVmOp = VM_OPS.has(msg.type);
    if (isVmOp && role !== 'vm') {
        self.postMessage({
            type: 'worker-misroute',
            requested: msg.type,
            actualRole: role,
            id: msg.id,
        });
        return;
    }
    if (!isVmOp && role === 'vm' && /^(lsp-|list-tests|list-command-docs)/.test(msg.type)) {
        self.postMessage({
            type: 'worker-misroute',
            requested: msg.type,
            actualRole: role,
            id: msg.id,
        });
        return;
    }

    if (msg.type === 'run') {
        let result;
        try {
            result = exports.FadeBasic.Export.Web.FadeBridge.CompileAndRun(msg.source);
        } catch (e) {
            result = 'Worker error: ' + (e?.message ?? e);
        }
        self.postMessage({ type: 'result', id: msg.id, result });
    } else if (msg.type === 'lsp-set') {
        log('lsp-set: calling LspSetDocument');
        let diagnosticsJson = '[]';
        try {
            diagnosticsJson = exports.FadeBasic.Export.Web.FadeBridge.LspSetDocument(msg.uri, msg.text);
            log('lsp-set: returned, length=' + diagnosticsJson.length);
        } catch (e) {
            log('lsp-set failed: ' + (e?.message ?? e));
        }
        self.postMessage({
            type: 'lsp-diagnostics',
            uri: msg.uri,
            version: msg.version,
            diagnostics: diagnosticsJson,
        });
    } else if (msg.type === 'lsp-tokens') {
        let tokensJson = '[]';
        try {
            tokensJson = exports.FadeBasic.Export.Web.FadeBridge.LspGetSemanticTokens(msg.uri);
        } catch (e) {
            log('lsp-tokens failed: ' + (e?.message ?? e));
        }
        self.postMessage({
            type: 'lsp-tokens-result',
            id: msg.id,
            uri: msg.uri,
            tokens: tokensJson,
        });
    } else if (msg.type === 'lsp-hover') {
        let hoverJson = 'null';
        try {
            hoverJson = exports.FadeBasic.Export.Web.FadeBridge.LspHover(msg.uri, msg.line, msg.character);
        } catch (e) {
            log('lsp-hover failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-hover-result', id: msg.id, hover: hoverJson });
    } else if (msg.type === 'lsp-completion') {
        let json = '[]';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.LspCompletion(msg.uri, msg.line, msg.character);
        } catch (e) {
            log('lsp-completion failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-completion-result', id: msg.id, items: json });
    } else if (msg.type === 'lsp-all-diagnostics') {
        let json = '{}';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.LspGetAllDiagnostics();
        } catch (e) {
            log('lsp-all-diagnostics failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-all-diagnostics-result', id: msg.id, all: json });
    } else if (msg.type === 'lsp-signature-help') {
        let json = 'null';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.LspSignatureHelp(msg.uri, msg.line, msg.character);
        } catch (e) {
            log('lsp-signature-help failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-signature-help-result', id: msg.id, sig: json });
    } else if (msg.type === 'lsp-references') {
        let json = '[]';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.LspReferences(msg.uri, msg.line, msg.character);
        } catch (e) {
            log('lsp-references failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-references-result', id: msg.id, refs: json });
    } else if (msg.type === 'lsp-definition') {
        let json = 'null';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.LspDefinition(msg.uri, msg.line, msg.character);
        } catch (e) {
            log('lsp-definition failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-definition-result', id: msg.id, def: json });
    } else if (msg.type === 'lsp-document-symbols') {
        let json = '[]';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.LspDocumentSymbols(msg.uri);
        } catch (e) {
            log('lsp-document-symbols failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-document-symbols-result', id: msg.id, symbols: json });
    } else if (msg.type === 'lsp-folding-ranges') {
        let json = '[]';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.LspFoldingRanges(msg.uri);
        } catch (e) {
            log('lsp-folding-ranges failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-folding-ranges-result', id: msg.id, ranges: json });
    } else if (msg.type === 'lsp-format') {
        let json = '[]';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.LspFormat(msg.uri, msg.options || '');
        } catch (e) {
            log('lsp-format failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-format-result', id: msg.id, edits: json });
    } else if (msg.type === 'lsp-format-range') {
        let json = '[]';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.LspFormatRange(
                msg.uri, msg.options || '',
                msg.startLine, msg.startCh, msg.endLine, msg.endCh,
            );
        } catch (e) {
            log('lsp-format-range failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-format-range-result', id: msg.id, edits: json });
    } else if (msg.type === 'lsp-format-on-type') {
        let json = '[]';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.LspFormatOnType(msg.uri, msg.options || '', msg.line, msg.character);
        } catch (e) {
            log('lsp-format-on-type failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-format-on-type-result', id: msg.id, edits: json });
    } else if (msg.type === 'lsp-rename') {
        let json = 'null';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.LspRename(msg.uri, msg.line, msg.character, msg.newName);
        } catch (e) {
            log('lsp-rename failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-rename-result', id: msg.id, edit: json });
    } else if (msg.type === 'load-assembly') {
        let json = '{"ok":false,"error":"unknown"}';
        try {
            const bytes = msg.dllBytes instanceof Uint8Array ? msg.dllBytes : new Uint8Array(msg.dllBytes);
            json = exports.FadeBasic.Export.Web.FadeBridge.LoadAssembly(bytes);
        } catch (e) {
            json = JSON.stringify({ ok: false, error: String(e?.message ?? e) });
            log('load-assembly failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'load-assembly-result', id: msg.id, result: json });
    } else if (msg.type === 'run-start'
            || msg.type === 'run-start-source'
            || msg.type === 'run-start-bytecode') {
        // Cooperative-pump entry points. Three flavors, same pump:
        //   run-start          : DLL bytes carrying an ILaunchable
        //                        (the original export-bundle path).
        //   run-start-source   : raw Fade source (Playground path).
        //   run-start-bytecode : pre-compiled bytecode bytes (preview
        //                        iframe path — Playground compiles via
        //                        CompileToBytecode, posts the bytes in).
        // Pump emits exactly one terminal 'run-tick-result' per call.
        const FB = exports.FadeBasic.Export.Web.FadeBridge;
        let startJson = '{"ok":false,"error":"unknown"}';
        try {
            if (msg.type === 'run-start') {
                const bytes = msg.dllBytes instanceof Uint8Array
                    ? msg.dllBytes : new Uint8Array(msg.dllBytes);
                startJson = FB.RunStart(bytes);
            } else if (msg.type === 'run-start-source') {
                startJson = FB.RunStartFromSource(msg.source || '');
            } else {
                const bytes = msg.bytecode instanceof Uint8Array
                    ? msg.bytecode : new Uint8Array(msg.bytecode);
                startJson = FB.RunStartFromBytecode(bytes);
            }
        } catch (e) {
            startJson = JSON.stringify({ ok: false, error: String(e?.message ?? e) });
            log(msg.type + ' failed: ' + (e?.message ?? e));
        }
        // If setup failed (compile error, no ILaunchable, etc.), the
        // pump won't get anywhere. Surface the error immediately as
        // the terminal result and don't start ticking. Forward both
        // `compileError` and `error` so callers can distinguish them.
        try {
            const parsed = JSON.parse(startJson);
            if (!parsed.ok) {
                self.postMessage({
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
        // Tear down an in-flight run. Cancel any pending pump tick so a
        // long wait-ms doesn't delay observation of the stop flag, then
        // call StopRun on the C# side. We drive one final tick now so
        // the terminal run-tick-result fires immediately rather than
        // waiting for whatever was already scheduled.
        if (pumpTimerId != null) { clearTimeout(pumpTimerId); pumpTimerId = null; }
        try {
            exports.FadeBasic.Export.Web.FadeBridge.StopRun();
        } catch (e) {
            log('stop-run failed: ' + (e?.message ?? e));
        }
        // Surface the terminal result immediately when the pump was active.
        if (runPumpActive) pumpTimerId = setTimeout(pumpRunTick, 0);
    } else if (msg.type === 'compile-to-bytecode') {
        // Compile source to a raw bytecode blob, returning the bytes +
        // status separately. Used by the Playground for the export
        // download and for handing bytes off to the preview iframe.
        const FB = exports.FadeBasic.Export.Web.FadeBridge;
        let bytecode = null, statusJson = '{"ok":false}';
        try {
            statusJson = FB.CompileToBytecodeStatus(msg.source || '');
            const status = JSON.parse(statusJson);
            if (status.ok) bytecode = FB.CompileToBytecode(msg.source || '');
        } catch (e) {
            statusJson = JSON.stringify({ ok: false, error: String(e?.message ?? e) });
            log('compile-to-bytecode failed: ' + (e?.message ?? e));
        }
        // Transfer the ArrayBuffer if we got bytes (avoids a copy on
        // larger programs). Bytecode itself is small for typical Fade
        // programs (kilobytes), but transfer ownership is correct.
        const bytecodeBuf = bytecode ? bytecode.buffer : null;
        self.postMessage(
            { type: 'compile-to-bytecode-result', id: msg.id, status: statusJson, bytecode: bytecodeBuf },
            bytecodeBuf ? [bytecodeBuf] : []
        );
    } else if (msg.type === 'host-reply') {
        // Page is replying to a HostBridge.PostMessage. Dispatch by result
        // type to the matching Deposit* JSExport (which swaps whatever
        // placeholder the source-generated executor pushed for the real
        // value), then resume the pump. resultType values match the
        // FadeBasic VM primitive type names — see TypeCodes in
        // FadeBasic/Virtual/OpCodes.cs.
        //
        // New channels do NOT need changes here. Only adding a brand-new
        // FadeBasic VM primitive (which is rare) would require touching
        // this switch.
        const FB = exports.FadeBasic.Export.Web.FadeBridge;
        try {
            switch (msg.resultType) {
                case 'string': FB.DepositResultString(msg.value ?? '');                  break;
                case 'int':    FB.DepositResultInt((msg.value | 0));                     break;
                case 'real':   FB.DepositResultReal(+msg.value);                         break;
                case 'bool':   FB.DepositResultBool(!!msg.value);                        break;
                case 'byte':   FB.DepositResultByte((msg.value | 0) & 0xff);             break;
                case 'word':   FB.DepositResultWord((msg.value | 0) & 0xffff);           break;
                // uint32: JS bitwise ops are signed 32-bit, so use >>> to
                // get the unsigned interpretation before narrowing in C#.
                case 'dword':  FB.DepositResultDword((msg.value >>> 0) | 0);             break;
                // int64 — needs BigInt to preserve values past 2^53. Accept
                // BigInt, number, or numeric string from the page.
                case 'dint':   FB.DepositResultDint(BigInt(msg.value ?? 0));             break;
                case 'dfloat': FB.DepositResultDfloat(+msg.value);                       break;
                case 'void':   FB.DepositResultVoid();                                   break;
                default:
                    log('host-reply: unknown resultType=' + msg.resultType);
                    // Fall through with void so the pump doesn't hang
                    // waiting for a reply that won't come. The placeholder
                    // pushed by the executor (whatever its default value)
                    // becomes the command's answer.
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
            json = exports.FadeBasic.Export.Web.FadeBridge.RegisterCommandAssembly(bytes, msg.className);
        } catch (e) {
            json = JSON.stringify({ ok: false, error: String(e?.message ?? e) });
            log('register-command-assembly failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'register-command-assembly-result', id: msg.id, result: json });
    } else if (msg.type === 'clear-command-assemblies') {
        try { exports.FadeBasic.Export.Web.FadeBridge.ClearCommandAssemblies(); }
        catch (e) { log('clear-command-assemblies failed: ' + (e?.message ?? e)); }
        self.postMessage({ type: 'clear-command-assemblies-result', id: msg.id });
    } else if (msg.type === 'set-project-type') {
        // Page sends this when the active fade.json switches between 'web'
        // and 'monogame' so the LSP swaps its CommandCollection. The page
        // should re-set every open document after this resolves so tokens
        // and diagnostics recompute against the new command set.
        let resolved = msg.projectType;
        try { resolved = exports.FadeBasic.Export.Web.FadeBridge.SetProjectType(msg.projectType); }
        catch (e) { log('set-project-type failed: ' + (e?.message ?? e)); }
        self.postMessage({ type: 'set-project-type-result', id: msg.id, projectType: resolved });
    } else if (msg.type === 'debug-start' || msg.type === 'debug-start-test') {
        let json = '{}';
        try {
            json = msg.type === 'debug-start-test'
                ? exports.FadeBasic.Export.Web.FadeBridge.DebugStartTest(msg.source, msg.testName || '')
                : exports.FadeBasic.Export.Web.FadeBridge.DebugStart(msg.source);
        } catch (e) {
            log(msg.type + ' failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'debug-start-result', id: msg.id, result: json });
        try {
            const parsed = JSON.parse(json);
            if (parsed?.ok && !debugTicking) startDebugTickLoop();
        } catch { /* ignore */ }
    } else if (msg.type === 'get-debug-test-result') {
        let json = 'null';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.GetDebugTestResult();
        } catch (e) {
            log('get-debug-test-result failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'get-debug-test-result-result', id: msg.id, result: json });
    } else if (msg.type === 'debug-terminate') {
        debugTicking = false;
        try { exports.FadeBasic.Export.Web.FadeBridge.DebugTerminate(); } catch (e) { log('terminate failed: ' + e); }
        self.postMessage({ type: 'debug-terminate-result', id: msg.id });
    } else if (msg.type === 'debug-set-breakpoints') {
        try { exports.FadeBasic.Export.Web.FadeBridge.DebugSetBreakpoints(msg.linesJson); }
        catch (e) { log('set-bp failed: ' + e); }
        self.postMessage({ type: 'debug-set-breakpoints-result', id: msg.id });
    } else if (msg.type === 'debug-step') {
        try { exports.FadeBasic.Export.Web.FadeBridge.DebugStep(msg.kind); }
        catch (e) { log('step failed: ' + e); }
        self.postMessage({ type: 'debug-step-result', id: msg.id });
    } else if (msg.type === 'debug-continue') {
        try { exports.FadeBasic.Export.Web.FadeBridge.DebugContinue(); }
        catch (e) { log('continue failed: ' + e); }
        self.postMessage({ type: 'debug-continue-result', id: msg.id });
    } else if (msg.type === 'debug-pause') {
        try { exports.FadeBasic.Export.Web.FadeBridge.DebugPause(); }
        catch (e) { log('pause failed: ' + e); }
        self.postMessage({ type: 'debug-pause-result', id: msg.id });
    } else if (msg.type === 'debug-stack-frames') {
        let json = '[]';
        try { json = exports.FadeBasic.Export.Web.FadeBridge.DebugStackFrames(); }
        catch (e) { log('stack-frames failed: ' + e); }
        self.postMessage({ type: 'debug-stack-frames-result', id: msg.id, frames: json });
    } else if (msg.type === 'debug-scopes') {
        let json = '{}';
        try { json = exports.FadeBasic.Export.Web.FadeBridge.DebugScopes(msg.frameId); }
        catch (e) { log('scopes failed: ' + e); }
        self.postMessage({ type: 'debug-scopes-result', id: msg.id, scopes: json });
    } else if (msg.type === 'debug-variable-expansion') {
        let json = '{}';
        try { json = exports.FadeBasic.Export.Web.FadeBridge.DebugVariableExpansion(msg.variableId); }
        catch (e) { log('var-expand failed: ' + e); }
        self.postMessage({ type: 'debug-variable-expansion-result', id: msg.id, scopes: json });
    } else if (msg.type === 'debug-eval') {
        let json = 'null';
        try { json = exports.FadeBasic.Export.Web.FadeBridge.DebugEval(msg.frameId, msg.expression); }
        catch (e) { log('eval failed: ' + e); }
        self.postMessage({ type: 'debug-eval-result', id: msg.id, result: json });
    } else if (msg.type === 'debug-repl') {
        let json = 'null';
        try { json = exports.FadeBasic.Export.Web.FadeBridge.DebugRepl(msg.frameId, msg.code); }
        catch (e) { log('repl failed: ' + e); }
        self.postMessage({ type: 'debug-repl-result', id: msg.id, result: json });
    } else if (msg.type === 'debug-set-variable') {
        let json = 'null';
        try { json = exports.FadeBasic.Export.Web.FadeBridge.DebugSetVariable(msg.frameId, msg.variableId, msg.rhs); }
        catch (e) { log('set-var failed: ' + e); }
        self.postMessage({ type: 'debug-set-variable-result', id: msg.id, result: json });
    } else if (msg.type === 'list-tests') {
        let json = '[]';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.ListTests(msg.source);
        } catch (e) {
            log('list-tests failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'list-tests-result', id: msg.id, tests: json });
    } else if (msg.type === 'list-command-docs') {
        let json = '[]';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.ListCommandDocs();
        } catch (e) {
            log('list-command-docs failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'list-command-docs-result', id: msg.id, docs: json });
    } else if (msg.type === 'get-version-info') {
        let json = '{}';
        try {
            json = exports.FadeBasic.Export.Web.FadeBridge.GetVersionInfo();
        } catch (e) {
            log('get-version-info failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'get-version-info-result', id: msg.id, info: json });
    } else if (msg.type === 'run-tests') {
        // Cooperative test run — same pump as Run. Compile + start the
        // first test in C#, then pumpRunTick drives forward, advancing
        // the test queue inside RunTick. Terminal envelope is shaped
        // as run-tests-result so the Playground's test panel consumer
        // sees the same payload it did under the old synchronous path.
        let startJson = '{"ok":false}';
        try {
            startJson = exports.FadeBasic.Export.Web.FadeBridge.RunTestsStart(
                msg.source || '', msg.testName || '');
        } catch (e) {
            startJson = JSON.stringify({ ok: false, error: String(e?.message ?? e) });
            log('run-tests-start failed: ' + (e?.message ?? e));
        }
        try {
            const parsed = JSON.parse(startJson);
            if (!parsed.ok) {
                // Compile failure — emit terminal immediately with the
                // shape the test panel expects (compileError surfaces
                // separately from error).
                self.postMessage({
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

self.onmessage = (e) => {
    if (exports) {
        handle(e.data);
    } else {
        queue.push(e.data);
    }
};

init().catch((e) => {
    self.postMessage({ type: 'boot-error', message: String(e?.stack ?? e) });
});
