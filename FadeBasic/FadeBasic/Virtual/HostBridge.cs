using System;

namespace FadeBasic.Virtual
{
    // Generic primitives that runtime hosts (FadeBasic.Export.Web for the
    // WASM bundle, a native CLI, the MonoGame runtime, etc.) install at
    // startup. Library commands (any [FadeBasicCommand] in any assembly)
    // call these primitives — they never know who the host is.
    //
    // The design goal here is plugin extensibility WITHOUT modifying core.
    // A new library that needs cooperative behavior reuses the same two
    // primitives plus an arbitrary channel name; the corresponding page-
    // side handler is registered by the consumer in their own index.html.
    // Core, the runtime, and worker.js all stay untouched.
    //
    // Hooks default to null. A library command that calls a null hook
    // gracefully degrades (the VM doesn't suspend, the placeholder pushed
    // by the executor stays on the stack as the command's "answer").
    public static class HostBridge
    {
        // Fire-and-forget signal from a library command to whatever runtime
        // is hosting it. The host implementation typically forwards the
        // payload across some boundary — for the WASM runtime it becomes a
        // postMessage from the worker to the page.
        //
        // `channel` is an opaque string that identifies the operation. The
        // page (or whatever endpoint the host forwards to) routes on this.
        // Library authors should namespace channels with their library name
        // to avoid collisions (e.g. "fade-web/prompt", "my-plugin/file-pick").
        //
        // Payload is a string for transport simplicity. Structured payloads
        // should be JSON-encoded by the library; the page decodes them.
        //
        // This call does NOT block, does NOT suspend the VM, and does NOT
        // return a value. A library that needs a reply pairs this with
        // SuspendVm and waits for the host to deposit a result later.
        public static Action<string, string> PostMessage;

        // Asks the host to pause the current VM. The host knows which VM
        // is currently being pumped (it owns the scheduler). After this
        // returns, the calling command should also return — the next tick
        // will see the suspend flag and exit cleanly, yielding control
        // back to the host's event loop.
        //
        // Pairing pattern for a "request → reply" command:
        //   1. Push a placeholder return value (the source-generated
        //      executor does this automatically based on the command's
        //      C# return type — "" for string commands, 0 for int, etc).
        //   2. Call PostMessage to ask the host to do something async.
        //   3. Call SuspendVm.
        //   4. Return the placeholder.
        // The host receives the reply later, swaps the placeholder for the
        // real value via a Deposit* JSExport, and resumes the pump.
        public static Action SuspendVm;
    }
}
