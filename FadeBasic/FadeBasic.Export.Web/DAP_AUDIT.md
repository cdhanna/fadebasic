# Web DAP Adapter — Architecture Notes

The Playground drives `FadeBasic.Launch.DebugSession` directly through the
WebRuntime bridge. The page acts as both DAP **client** and **adapter** —
there's no OmniSharp / VSCode layer in between.

## Why this matters

In a native VSCode run, traffic looks like:

```
VSCode (UI)  ↔  DAP adapter  ↔  DebugSession (in-proc or TCP)
```

The DAP adapter:
- forwards client requests (`next`, `continue`, `setBreakpoints`, …) to DebugSession,
- translates DebugSession's outputs back into DAP events VSCode expects
  (most importantly: `Stopped` events when a step lands or a breakpoint hits).

In the playground:

```
Page UI  ↔  worker bridge  ↔  WebDebugSession (in-proc, no TCP)
```

The page IS the adapter. So when DebugSession ACKs a step request with
`StepNextResponseMessage { status=1, reason="hit next" }`, the page has
to recognize that ACK as "stopped" — the same translation a native DAP
adapter does silently. Code in [main.ts](../Playground/src/main.ts)
`onDebugEvent` case `PROTO_ACK` parses the payload and refreshes the
call-stack + variables when it sees `status === 1`. We do **not** emit
synthetic protocol events on the bridge side — the protocol shape stays
identical to what a native client sees.

## Protocol-level behavior the bridge does **not** touch

- `REQUEST_BREAKPOINTS` — bridge forwards the `Breakpoint` payload as-is.
  `instructionMap.TryFindClosestTokenAtLocation` does the resolution.
- `REQUEST_STEP_OVER / IN / OUT` — forwarded with no modifications.
  Landings ACK with `StepNextResponseMessage` exactly as native sees.
- `REQUEST_PAUSE / PLAY` — forwarded as-is.
- `REQUEST_STACK_FRAMES`, `REQUEST_SCOPES`, `REQUEST_VARIABLE_EXPANSION`,
  `REQUEST_EVAL`, `REQUEST_REPL`, `REQUEST_SET_VAR` — bridge calls the
  matching DebugSession method (`GetFrames2`, `GetScopes`, `Expand`,
  `Eval`, `ReplExec`) and serializes the result. No DTO reshaping.
- `REV_REQUEST_BREAKPOINT` / `REV_REQUEST_EXITED` / `REV_REQUEST_EXPLODE`
  / `PROTO_ACK` — bridge forwards everything DebugSession enqueues to
  `outboundMessages` straight to the page.

## Where we deliberately diverge from native

| Concern | Native | Web | Why |
|---|---|---|---|
| **Transport** | TCP via `DebugSession.StartServer()` | Subclass `WebDebugSession` exposing `Enqueue` / `DrainOutbound` directly | No TCP in workers; no real client to connect. |
| **Pre-connect wait** | DebugSession blocks until `PROTO_HELLO` | Subclass pre-sets `didClientConnect = true`, `hasConnectedDebugger = 1`, `debuggerSaidHello = 1`. `debugWaitForConnection = false` on options. | We're embedded in the same process; the handshake is meaningless. |
| **Initial state** | Program runs immediately under `StartDebugging()` | Bridge enqueues `REQUEST_PAUSE` right after construction so the worker tick loop holds at instruction 0 | The page needs a window to install breakpoints before the VM begins. Without this, the worker's tick would race ahead of the page's `setBreakpoints` call. |
| **`Environment.Exit(0)` inside `REQUEST_TERMINATE`** | Hard-kills the process — appropriate for a standalone debug target | Bridge **never sends `REQUEST_TERMINATE`** — `FadeBridge.DebugTerminate` simply nulls the session reference; the worker tick loop sees `session == null`, stops, and emits a host-level `complete` event to the page. | `Environment.Exit(0)` in WASM would tear down the entire runtime including the LSP + WebCommands. The protocol mechanism (`requestedExit`) isn't needed because the bridge owns the lifetime. |
| **`LaunchOptions` static cctor** | Reads env vars + grabs a free TCP port | Patched to swallow exceptions and keep safe defaults | `LaunchUtil.FreeTcpPort()` throws in WASM (no sockets), and a throw in a static constructor produces `TypeInitializationException` on every later access to any field of `LaunchOptions`. Single try/catch keeps the type usable everywhere. |

## Bridge surface (FadeBridge.cs)

| Method | DebugSession route |
|---|---|
| `DebugStart(source)` | `Fade.TryCreateFromString → new WebDebugSession → Enqueue(REQUEST_PAUSE)` |
| `DebugTick(ops)` | `session.StartDebugging(ops); DrainOutbound()` |
| `DebugSetBreakpoints(json)` | `Enqueue(REQUEST_BREAKPOINTS)` |
| `DebugStep(kind)` | `Enqueue(REQUEST_STEP_{OVER,IN,OUT})` |
| `DebugContinue()` | `Enqueue(REQUEST_PLAY)` |
| `DebugPause()` | `Enqueue(REQUEST_PAUSE)` |
| `DebugTerminate()` | Drops `_debugSession` reference (no DAP message) |
| `DebugStackFrames()` | `session.GetFrames2()` |
| `DebugScopes(frameId)` | `session.GetScopes(new DebugScopeRequest { frameIndex = frameId })` |
| `DebugVariableExpansion(varId)` | `session.variableDb.Expand(varId)` |
| `DebugEval(frameId, expr)` | `session.Eval(...)` |
| `DebugRepl(frameId, code)` | `session.ReplExec(...)` |
| `DebugSetVariable(frameId, varId, rhs)` | `session.Eval(frameId, rhs, varId)` |

## Worker tick loop

`worker.js` `pumpDebugTick` runs `DebugTick(500)` repeatedly:
- 500-op budget per call so the worker yields to its postMessage pump
  often enough that step / pause / set-breakpoint messages from the page
  land between batches.
- 50ms delay when the session reports `paused`; 0ms when running, so the
  VM gets to execute at full speed until something stops it.
- Print buffer drained per tick and streamed through the existing `print`
  message channel so program output shows up in the Output panel live.

## Page-side "DAP adapter" behavior

`runner.onDebugEvent` cases:
- `REV_REQUEST_BREAKPOINT` → "paused at breakpoint" → refresh frames/vars.
- `PROTO_ACK` with `status === 1` → step landed → refresh frames/vars.
  Plain ACKs (for `setBreakpoints` / `continue` / etc.) treated as "resumed".
- `REV_REQUEST_EXITED` / `complete` → session done.
- `REV_REQUEST_EXPLODE` → runtime error.

This is exactly the translation a native DAP adapter does internally
before talking to VSCode.
