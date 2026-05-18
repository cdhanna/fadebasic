// Dedicated module worker: hosts the .NET runtime + Fade compiler/VM.
// Bootstraps the runtime once, then handles run requests from the page
// via postMessage.

import { dotnet } from '/_framework/dotnet.js';

let exports = null;
const queue = [];

function log(message) {
    self.postMessage({ type: 'log', message });
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
