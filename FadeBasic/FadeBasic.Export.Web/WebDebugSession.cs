// In-process DebugSession for the browser runtime. Skips the TCP listener
// that DebugSession.StartServer() spins up and instead exposes the
// inbound / outbound message queues directly so the worker can drive
// the session by method call.
//
// Lifecycle in FadeBasic.Export.Web:
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

namespace FadeBasic.Export.Web;

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

    // Synthesize a "stopped" outbound event. The base session's
    // SendStopMessage is protected and only fires when a breakpoint is
    // hit — manual REQUEST_PAUSE acks with a plain PROTO_ACK that the
    // page's adapter (see DAP_AUDIT.md) reads as "running". WaitImpl
    // calls this after enqueuing REQUEST_PAUSE so the page transitions
    // to its paused UI state.
    public void EmitStop()
    {
        outboundMessages.Enqueue(new DebugMessage
        {
            id = GetNextMessageId(),
            type = DebugMessageType.REV_REQUEST_BREAKPOINT,
        });
    }

    // True after a kind=3 (yield) interrupt has flipped requestedExit.
    // DebugTick reads this after StartDebugging returns and resets
    // requestedExit so the next tick can resume normally. This is the
    // hook that makes the worker yield between waits — see WaitImpl
    // in FadeBridge.CreateWorkspace.
    public bool WasYieldRequest { get; private set; }

    public void RequestYield()
    {
        WasYieldRequest = true;
        requestedExit = true;
        // VirtualMachine.Execute3 checks `!isSuspendRequested` per
        // instruction in its inner for-loop. Flipping it short-circuits
        // the current batch *immediately*, so the very next instruction
        // doesn't run. Without this, Execute3 keeps going until its
        // budget exhausts and requestedExit only takes effect at the
        // outer loop boundary — after at least one more instruction
        // (the one right after `wait ms`) has already executed.
        if (_vm != null) _vm.isSuspendRequested = true;
        // Enqueue a no-op too so the Execute3 lambda's `receivedMessages.Count > 0`
        // check is an *additional* yield path (some Execute paths take
        // the lambda route, some take the field-flag route).
        receivedMessages.Enqueue(new DebugMessage
        {
            id = GetNextMessageId(),
            type = DebugMessageType.NOOP,
        });
    }

    public void ClearYieldRequest()
    {
        if (!WasYieldRequest) return;
        WasYieldRequest = false;
        requestedExit = false;
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
