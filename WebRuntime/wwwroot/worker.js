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

// ─── Synchronous prompt$ handshake ──────────────────────────────────────────
// `prompt$` is a JSImport that must return synchronously from C#'s
// perspective, even though the actual UI prompt happens on the main thread.
// We do this with a SharedArrayBuffer + Atomics.wait. The main thread sends
// a SharedArrayBuffer at boot time; on prompt(), we postMessage the request
// and Atomics.wait on the sync slot. The main thread fills bytes in the
// buffer and notifies us. We decode and return.
//
// Layout:  Int32Array[0]      = sync state (0 = waiting, 1 = ready)
//          Int32Array[1]      = response length in bytes
//          bytes[8..8+length] = UTF-8 response payload
let promptSab = null;
let promptSync = null;
let promptBytes = null;

// ─── Interruptible `wait ms` ───────────────────────────────────────────────
// Atomics.wait blocks the worker thread up to `ms` milliseconds. Pause /
// stop on the page side writes a non-zero "kind" into the SAB and calls
// Atomics.notify, which wakes the wait early and tells C# what to do
// next. Return value is the kind C# should react to:
//   0 = wait completed normally (timed out, no interrupt)
//   1 = page wants the VM to PAUSE
//   2 = page wants the VM to TERMINATE
let waitSab = null;
let waitView = null;
function waitMsInterruptible(ms) {
    if (!waitView) {
        // SAB not wired — fall back to busy polling so at least the call
        // doesn't crash. No interrupt capability in this mode.
        const end = performance.now() + ms;
        while (performance.now() < end) { /* spin */ }
        return 0;
    }
    Atomics.store(waitView, 0, 0);
    Atomics.wait(waitView, 0, 0, ms);
    // Read + clear in one shot so the next wait starts from a clean slot.
    const kind = Atomics.exchange(waitView, 0, 0);
    return kind | 0;
}

// ─── Debug tick loop ────────────────────────────────────────────────────────
// While a debug session is active, we yield to the worker's message pump
// between batches of VM instructions so inbound messages (step/continue/
// breakpoint changes) can land. Tick budget is small enough that pause/step
// feels responsive; we use setTimeout(0) so messages get processed.
let debugTicking = false;
function startDebugTickLoop() {
    debugTicking = true;
    pumpDebugTick();
}
function pumpDebugTick() {
    if (!debugTicking) return;
    let result;
    try {
        const json = exports.WebRuntime.FadeBridge.DebugTick(500);
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
    // Schedule next batch — when paused, slow down so we're not burning CPU.
    const delay = result.paused ? 50 : 0;
    setTimeout(pumpDebugTick, delay);
}

function syncPromptFromMain(msg) {
    if (!promptSab) {
        // No shared buffer — main thread didn't isolate; return empty string.
        return '';
    }
    Atomics.store(promptSync, 0, 0);
    Atomics.store(promptSync, 1, 0);
    self.postMessage({ type: 'prompt-request', msg });
    // Block this worker thread until the main thread writes the response.
    Atomics.wait(promptSync, 0, 0);
    const len = Atomics.load(promptSync, 1);
    if (len <= 0) return '';
    // Firefox refuses to decode SharedArrayBuffer-backed views directly.
    // Copy into a plain ArrayBuffer first.
    const copy = new Uint8Array(len);
    copy.set(new Uint8Array(promptSab, 8, len));
    return new TextDecoder().decode(copy);
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
        prompt: (msg) => syncPromptFromMain(msg),
        waitMsInterruptible: (ms) => waitMsInterruptible(ms),
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
    'run', 'run-tests', 'prompt-sab',
    'debug-start', 'debug-start-test', 'debug-terminate',
    'debug-set-breakpoints', 'debug-step', 'debug-continue', 'debug-pause',
    'debug-stack-frames', 'debug-scopes', 'debug-variable-expansion',
    'debug-eval', 'debug-repl', 'debug-set-variable',
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
            result = exports.WebRuntime.FadeBridge.CompileAndRun(msg.source);
        } catch (e) {
            result = 'Worker error: ' + (e?.message ?? e);
        }
        self.postMessage({ type: 'result', id: msg.id, result });
    } else if (msg.type === 'lsp-set') {
        log('lsp-set: calling LspSetDocument');
        let diagnosticsJson = '[]';
        try {
            diagnosticsJson = exports.WebRuntime.FadeBridge.LspSetDocument(msg.uri, msg.text);
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
            tokensJson = exports.WebRuntime.FadeBridge.LspGetSemanticTokens(msg.uri);
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
            hoverJson = exports.WebRuntime.FadeBridge.LspHover(msg.uri, msg.line, msg.character);
        } catch (e) {
            log('lsp-hover failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-hover-result', id: msg.id, hover: hoverJson });
    } else if (msg.type === 'lsp-completion') {
        let json = '[]';
        try {
            json = exports.WebRuntime.FadeBridge.LspCompletion(msg.uri, msg.line, msg.character);
        } catch (e) {
            log('lsp-completion failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-completion-result', id: msg.id, items: json });
    } else if (msg.type === 'lsp-all-diagnostics') {
        let json = '{}';
        try {
            json = exports.WebRuntime.FadeBridge.LspGetAllDiagnostics();
        } catch (e) {
            log('lsp-all-diagnostics failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-all-diagnostics-result', id: msg.id, all: json });
    } else if (msg.type === 'lsp-signature-help') {
        let json = 'null';
        try {
            json = exports.WebRuntime.FadeBridge.LspSignatureHelp(msg.uri, msg.line, msg.character);
        } catch (e) {
            log('lsp-signature-help failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-signature-help-result', id: msg.id, sig: json });
    } else if (msg.type === 'lsp-references') {
        let json = '[]';
        try {
            json = exports.WebRuntime.FadeBridge.LspReferences(msg.uri, msg.line, msg.character);
        } catch (e) {
            log('lsp-references failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-references-result', id: msg.id, refs: json });
    } else if (msg.type === 'lsp-definition') {
        let json = 'null';
        try {
            json = exports.WebRuntime.FadeBridge.LspDefinition(msg.uri, msg.line, msg.character);
        } catch (e) {
            log('lsp-definition failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-definition-result', id: msg.id, def: json });
    } else if (msg.type === 'lsp-document-symbols') {
        let json = '[]';
        try {
            json = exports.WebRuntime.FadeBridge.LspDocumentSymbols(msg.uri);
        } catch (e) {
            log('lsp-document-symbols failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-document-symbols-result', id: msg.id, symbols: json });
    } else if (msg.type === 'lsp-folding-ranges') {
        let json = '[]';
        try {
            json = exports.WebRuntime.FadeBridge.LspFoldingRanges(msg.uri);
        } catch (e) {
            log('lsp-folding-ranges failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-folding-ranges-result', id: msg.id, ranges: json });
    } else if (msg.type === 'lsp-format') {
        let json = '[]';
        try {
            json = exports.WebRuntime.FadeBridge.LspFormat(msg.uri, msg.options || '');
        } catch (e) {
            log('lsp-format failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-format-result', id: msg.id, edits: json });
    } else if (msg.type === 'lsp-format-range') {
        let json = '[]';
        try {
            json = exports.WebRuntime.FadeBridge.LspFormatRange(
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
            json = exports.WebRuntime.FadeBridge.LspFormatOnType(msg.uri, msg.options || '', msg.line, msg.character);
        } catch (e) {
            log('lsp-format-on-type failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-format-on-type-result', id: msg.id, edits: json });
    } else if (msg.type === 'lsp-rename') {
        let json = 'null';
        try {
            json = exports.WebRuntime.FadeBridge.LspRename(msg.uri, msg.line, msg.character, msg.newName);
        } catch (e) {
            log('lsp-rename failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'lsp-rename-result', id: msg.id, edit: json });
    } else if (msg.type === 'set-project-type') {
        // Page sends this when the active fade.json switches between 'web'
        // and 'monogame' so the LSP swaps its CommandCollection. The page
        // should re-set every open document after this resolves so tokens
        // and diagnostics recompute against the new command set.
        let resolved = msg.projectType;
        try { resolved = exports.WebRuntime.FadeBridge.SetProjectType(msg.projectType); }
        catch (e) { log('set-project-type failed: ' + (e?.message ?? e)); }
        self.postMessage({ type: 'set-project-type-result', id: msg.id, projectType: resolved });
    } else if (msg.type === 'prompt-sab') {
        // Main thread is handing us the SharedArrayBuffer used by syncPromptFromMain.
        promptSab = msg.buffer;
        promptSync = new Int32Array(promptSab, 0, 2);
        promptBytes = new Uint8Array(promptSab, 8);
    } else if (msg.type === 'wait-interrupt-sab') {
        // SAB used by waitMsInterruptible() — main thread Atomics.notifies
        // it to wake an in-flight wait early when the user pauses/stops.
        waitSab = msg.buffer;
        waitView = new Int32Array(waitSab, 0, 1);
    } else if (msg.type === 'debug-start' || msg.type === 'debug-start-test') {
        let json = '{}';
        try {
            json = msg.type === 'debug-start-test'
                ? exports.WebRuntime.FadeBridge.DebugStartTest(msg.source, msg.testName || '')
                : exports.WebRuntime.FadeBridge.DebugStart(msg.source);
        } catch (e) {
            log(msg.type + ' failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'debug-start-result', id: msg.id, result: json });
        try {
            const parsed = JSON.parse(json);
            if (parsed?.ok && !debugTicking) startDebugTickLoop();
        } catch { /* ignore */ }
    } else if (msg.type === 'debug-terminate') {
        debugTicking = false;
        try { exports.WebRuntime.FadeBridge.DebugTerminate(); } catch (e) { log('terminate failed: ' + e); }
        self.postMessage({ type: 'debug-terminate-result', id: msg.id });
    } else if (msg.type === 'debug-set-breakpoints') {
        try { exports.WebRuntime.FadeBridge.DebugSetBreakpoints(msg.linesJson); }
        catch (e) { log('set-bp failed: ' + e); }
        self.postMessage({ type: 'debug-set-breakpoints-result', id: msg.id });
    } else if (msg.type === 'debug-step') {
        try { exports.WebRuntime.FadeBridge.DebugStep(msg.kind); }
        catch (e) { log('step failed: ' + e); }
        self.postMessage({ type: 'debug-step-result', id: msg.id });
    } else if (msg.type === 'debug-continue') {
        try { exports.WebRuntime.FadeBridge.DebugContinue(); }
        catch (e) { log('continue failed: ' + e); }
        self.postMessage({ type: 'debug-continue-result', id: msg.id });
    } else if (msg.type === 'debug-pause') {
        try { exports.WebRuntime.FadeBridge.DebugPause(); }
        catch (e) { log('pause failed: ' + e); }
        self.postMessage({ type: 'debug-pause-result', id: msg.id });
    } else if (msg.type === 'debug-stack-frames') {
        let json = '[]';
        try { json = exports.WebRuntime.FadeBridge.DebugStackFrames(); }
        catch (e) { log('stack-frames failed: ' + e); }
        self.postMessage({ type: 'debug-stack-frames-result', id: msg.id, frames: json });
    } else if (msg.type === 'debug-scopes') {
        let json = '{}';
        try { json = exports.WebRuntime.FadeBridge.DebugScopes(msg.frameId); }
        catch (e) { log('scopes failed: ' + e); }
        self.postMessage({ type: 'debug-scopes-result', id: msg.id, scopes: json });
    } else if (msg.type === 'debug-variable-expansion') {
        let json = '{}';
        try { json = exports.WebRuntime.FadeBridge.DebugVariableExpansion(msg.variableId); }
        catch (e) { log('var-expand failed: ' + e); }
        self.postMessage({ type: 'debug-variable-expansion-result', id: msg.id, scopes: json });
    } else if (msg.type === 'debug-eval') {
        let json = 'null';
        try { json = exports.WebRuntime.FadeBridge.DebugEval(msg.frameId, msg.expression); }
        catch (e) { log('eval failed: ' + e); }
        self.postMessage({ type: 'debug-eval-result', id: msg.id, result: json });
    } else if (msg.type === 'debug-repl') {
        let json = 'null';
        try { json = exports.WebRuntime.FadeBridge.DebugRepl(msg.frameId, msg.code); }
        catch (e) { log('repl failed: ' + e); }
        self.postMessage({ type: 'debug-repl-result', id: msg.id, result: json });
    } else if (msg.type === 'debug-set-variable') {
        let json = 'null';
        try { json = exports.WebRuntime.FadeBridge.DebugSetVariable(msg.frameId, msg.variableId, msg.rhs); }
        catch (e) { log('set-var failed: ' + e); }
        self.postMessage({ type: 'debug-set-variable-result', id: msg.id, result: json });
    } else if (msg.type === 'list-tests') {
        let json = '[]';
        try {
            json = exports.WebRuntime.FadeBridge.ListTests(msg.source);
        } catch (e) {
            log('list-tests failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'list-tests-result', id: msg.id, tests: json });
    } else if (msg.type === 'list-command-docs') {
        let json = '[]';
        try {
            json = exports.WebRuntime.FadeBridge.ListCommandDocs();
        } catch (e) {
            log('list-command-docs failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'list-command-docs-result', id: msg.id, docs: json });
    } else if (msg.type === 'get-version-info') {
        let json = '{}';
        try {
            json = exports.WebRuntime.FadeBridge.GetVersionInfo();
        } catch (e) {
            log('get-version-info failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'get-version-info-result', id: msg.id, info: json });
    } else if (msg.type === 'run-tests') {
        let json = '{}';
        try {
            json = exports.WebRuntime.FadeBridge.RunTests(msg.source, msg.testName || '');
        } catch (e) {
            log('run-tests failed: ' + (e?.message ?? e));
        }
        self.postMessage({ type: 'run-tests-result', id: msg.id, result: json });
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
