// Dedicated module worker: hosts the .NET runtime + Fade compiler/VM.
// Bootstraps the runtime once, then handles run requests from the page
// via postMessage.

// Relative import (not '/_framework/...') so the runner is portable: hosts can
// mount this worker at any subpath (e.g. /runtime/worker.js) and the import
// still resolves correctly relative to the worker's own URL.
import { dotnet } from './_framework/dotnet.js';

let exports = null;
const queue = [];

function log(message) {
    self.postMessage({ type: 'log', message });
}

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
    if (result.printed) {
        // Stream prints from the running program through the same `print`
        // event channel as normal Run so the output panel updates live.
        const lines = result.printed.split('\n');
        for (const line of lines) {
            if (line.length === 0) continue;
            self.postMessage({ type: 'print', line });
        }
    }
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
    });

    log('registering assembly exports...');
    const config = runtime.getConfig();
    exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    log('exports loaded');

    while (queue.length) handle(queue.shift());
    self.postMessage({ type: 'ready' });
}

function handle(msg) {
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
    } else if (msg.type === 'prompt-sab') {
        // Main thread is handing us the SharedArrayBuffer used by syncPromptFromMain.
        promptSab = msg.buffer;
        promptSync = new Int32Array(promptSab, 0, 2);
        promptBytes = new Uint8Array(promptSab, 8);
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
