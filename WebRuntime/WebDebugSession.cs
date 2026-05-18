// In-process DebugSession for the browser runtime. Skips the TCP listener
// that DebugSession.StartServer() spins up and instead exposes the
// inbound / outbound message queues directly so the worker can drive
// the session by method call.
//
// Lifecycle in WebRuntime:
//   1. Bridge compiles source → VirtualMachine + Compiler.DebugData.
//   2. Bridge constructs WebDebugSession(vm, dbg, …) — no StartServer().
//   3. Worker tick loop calls `session.StartDebugging(ops=200)` in batches
//      between message-pump calls so we yield control back to JS often
//      enough for inbound messages from the main thread.
//   4. Worker pushes inbound messages via `Enqueue(msg)` and drains
//      outbound via `DrainOutbound()`.
//
// We pre-set `didClientConnect = true`, `hasConnectedDebugger = 1`, and
// `debuggerSaidHello = 1` so the session doesn't sit waiting for a
// PROTO_HELLO handshake from a TCP client that doesn't exist.

using System.Collections.Generic;
using FadeBasic;
using FadeBasic.Launch;
using FadeBasic.Virtual;

namespace WebRuntime;

internal sealed class WebDebugSession : DebugSession
{
    public WebDebugSession(VirtualMachine vm, DebugData dbg, CommandCollection commands)
        : base(vm, dbg, commands, new LaunchOptions
        {
            // Critical: skip the wait-for-client handshake. We're the only
            // "client" and we're embedded in the same process.
            debugWaitForConnection = false,
            debugPort = 0,
            debug = true,
        }, label: "web")
    {
        // Tell StartDebugging we already have a connected debugger so the
        // "auto-resume on client disconnect" path doesn't kick in if we
        // hit a breakpoint and then pause briefly between ticks.
        didClientConnect = true;
        hasConnectedDebugger = 1;
        debuggerSaidHello = 1;
    }

    // Enqueue an inbound message (from the main thread) for the next tick
    // to dispatch via DebugSession.ReadMessage's switch.
    public void Enqueue(DebugMessage msg)
    {
        receivedMessages.Enqueue(msg);
    }

    // Drain everything the session has produced since the last call. The
    // worker re-posts each as a typed `debug-event` message to the page.
    public List<DebugMessage> DrainOutbound()
    {
        var result = new List<DebugMessage>();
        while (outboundMessages.TryDequeue(out var msg)) result.Add(msg);
        return result;
    }

    // True once the VM has run past the end of its program. Used by the
    // worker tick loop to know when to stop pumping and emit an
    // EXITED-equivalent message.
    public bool ProgramComplete => _vm == null || _vm.program == null
        || InstructionPointer >= _vm.program.Length;

    // Exposes whether any step request is currently in flight. The base
    // DebugSession only signals a step landing via PROTO_ACK on the original
    // step request — the bridge watches this to synthesize a "stopped"
    // event the page-side debug loop can hook.
    public bool HasStepInFlight =>
        stepNextMessage != null || stepIntoMessage != null || stepOutMessage != null;
}
