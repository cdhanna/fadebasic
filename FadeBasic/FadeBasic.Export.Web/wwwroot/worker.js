// Worker-context shim. Imports the host-agnostic runtime module and
// wires the Web Worker's self.postMessage / self.onmessage to the
// runtime's onMessage / dispatch surface. All actual logic lives in
// runtime.js — this file just hooks the I/O.
//
// Used by the Playground's lspWorker. Export.Web's iframe loads
// runtime.js directly on its main thread (no Worker indirection) —
// see wwwroot/index.html.

import { init, dispatch, onMessage, setRole } from './runtime.js';

onMessage((m) => self.postMessage(m));

self.onmessage = (e) => {
    // First-message contract: parent sends `{type: 'configure', role}`
    // immediately after construction. Default role is 'vm'; the LSP
    // worker sets 'lsp' so heartbeat / log events carry the right tag.
    if (e.data?.type === 'configure') {
        setRole(e.data.role);
        return;
    }
    dispatch(e.data);
};

init();
