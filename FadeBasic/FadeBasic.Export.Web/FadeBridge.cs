using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using FadeBasic;
using FadeBasic.Json;
using FadeBasic.Launch;
using FadeBasic.Lib.Standard;
using FadeBasic.Sdk;
using FadeBasic.Virtual;
using FadeSdk = FadeBasic.Sdk.Fade;
using FadeBasic.LSP.Core;
using FadeBasic.LSP.Core.Handlers;

namespace FadeBasic.Export.Web;

// FadeBridge is the browser-side adapter between the worker's postMessage
// surface and the cross-platform LSP logic in FadeBasic.LSP.Core. The native
// LSP server in FadeBasic/LSP/ will get the same Core handlers behind its
// OmniSharp transport once it's refactored.
[SupportedOSPlatform("browser")]
public static partial class FadeBridge
{
    // Dynamically-registered command sources. Cleared by ClearCommandAssemblies
    // and rebuilt from fade.json's commandDlls on every project-type change.
    // MUST be declared before _workspace — static field initializers run in
    // declaration order and CreateWorkspace reads this list.
    private static readonly List<IMethodSource> _registeredSources = new();

    // Dynamically-loaded assemblies keyed by simple name. WASM's default
    // AssemblyLoadContext doesn't fall back to "scan loaded assemblies by
    // simple name" when binding type references the way desktop CLR does —
    // so when the entry assembly's static cctor does `new SomeLib.Foo()`,
    // resolution fails unless we hand the assembly back through Resolving.
    private static readonly Dictionary<string, Assembly> _dynamicAssemblies = new();
    private static bool _resolverHooked;

    private static void EnsureResolverHooked()
    {
        if (_resolverHooked) return;
        _resolverHooked = true;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
            _dynamicAssemblies.TryGetValue(name.Name ?? "", out var asm) ? asm : null;
    }

    // Load `bytes` into the default ALC and register the loaded assembly so
    // the Resolving handler can hand it back when the entry's type binder
    // looks it up by simple name. Duplicates with _framework/ are harmless:
    // default resolution finds those first, and Resolving only fires when
    // default resolution fails — so the byte-loaded copy is reachable only
    // for the assemblies that aren't already in _framework/.
    private static Assembly LoadAndRegister(byte[] bytes)
    {
        EnsureResolverHooked();
        var asm = Assembly.Load(bytes);
        var simpleName = asm.GetName().Name;
        if (!string.IsNullOrEmpty(simpleName))
            _dynamicAssemblies[simpleName] = asm;
        return asm;
    }

    // Active workspace — rebuilt by SetProjectType and RegisterCommandAssembly.
    private static FadeWorkspace _workspace = CreateWorkspace("web");
    private static string _activeProjectType = "web";

    private static FadeWorkspace CreateWorkspace(string projectType)
    {
        var sources = new List<IMethodSource>(_registeredSources) { new StandardCommands() };
        var commands = new CommandCollection(sources.ToArray());
        ICommandDocsProvider docs = StandardCommandDocs.BuildWeb();
        _ = projectType; // reserved for future type-specific doc providers

        var ws = new FadeWorkspace(commands);
        ws.Docs = docs;
        return ws;
    }

    // Called by the worker (main.ts → worker.js) when the active fade.json
    // type changes. Rebuilds the workspace with the right CommandCollection
    // so the LSP picks up the new command surface. Returns the new type so
    // the page can log/confirm. Idempotent.
    [JSExport]
    public static string SetProjectType(string projectType)
    {
        var t = (projectType ?? "web").ToLowerInvariant();
        if (t == _activeProjectType) return t;
        _activeProjectType = t;
        _workspace = CreateWorkspace(t);
        return t;
    }

    // Load a command DLL from raw bytes, instantiate the named class, and
    // merge it into the workspace. Both workers (LSP + VM) receive this call
    // so hover/completion and execution see the same command surface.
    // dllBytes is a Uint8Array on the JS side; className is fully-qualified.
    [JSExport]
    public static string RegisterCommandAssembly(byte[] dllBytes, string className)
    {
        try
        {
            var asm = LoadAndRegister(dllBytes);
            var type = asm.GetType(className)
                ?? throw new Exception($"Type '{className}' not found in assembly");
            var instance = Activator.CreateInstance(type) as IMethodSource
                ?? throw new Exception($"'{className}' does not implement IMethodSource");
            _registeredSources.Add(instance);
            _workspace = CreateWorkspace(_activeProjectType);
            return JsonSerializer.Serialize(new { ok = true }, _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = DescribeException(ex) }, _jsonOpts);
        }
    }

    // Remove all dynamically-registered command sources and rebuild the workspace.
    // Called by the page before re-registering whenever fade.json's commandDlls changes.
    [JSExport]
    public static string ClearCommandAssemblies()
    {
        _registeredSources.Clear();
        _workspace = CreateWorkspace(_activeProjectType);
        return "true";
    }

    // Load a side-by-side dependency DLL into the AppDomain without
    // registering it as a command source. Used by the export loader to pull
    // in the game's transitive deps (e.g. FadeBasic.Lib.Web.dll) BEFORE the
    // entry assembly is loaded — otherwise resolving the entry's ILaunchable
    // type would fail because referenced assemblies aren't yet present.
    [JSExport]
    public static string LoadAssembly(byte[] dllBytes)
    {
        try
        {
            LoadAndRegister(dllBytes);
            return JsonSerializer.Serialize(new { ok = true }, _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = DescribeException(ex) }, _jsonOpts);
        }
    }

    // ─── Cooperative pump model ───────────────────────────────────────
    // The web runtime can't run the VM to completion in one synchronous
    // call: while C# is executing, the worker's JS event loop is blocked,
    // so any postMessage from the page (prompt answers, pause/stop) sits
    // undelivered in the queue. Instead, we hold the VM as static state
    // and the worker pumps it in small budgeted batches, yielding to its
    // event loop between batches. `prompt$` and `wait ms` cooperate by
    // calling vm.Suspend() and stashing wake-up state in WebRuntimeBridge;
    // the JS pump reads that state to decide how (and when) to schedule
    // the next tick. See worker.js for the pump driver, WebCommands.cs
    // for the prompt$/wait ms command implementations.

    private static VirtualMachine? _runVm;
    private static string? _runError;
    // The single owner of suspend/resume state for this runtime. Library
    // commands only see HostBridge — they don't reach into these fields
    // directly. Plugins (other libraries / pages) extend behavior by
    // adding new HostBridge.PostMessage channels, not by adding new
    // fields here.
    private static bool _waitingForHostReply;
    private static int _pendingWaitMs;
    // Set by StopRun to terminate the cooperative pump regardless of
    // what state the VM is in (mid-batch, waiting on a host reply,
    // sleeping between wait-ms timeouts). RunTick observes it on the
    // next call and emits a synthetic complete=true result with an
    // error of "stopped" so callers can distinguish from a normal end.
    private static bool _runStopRequested;

    // ─── Cooperative test-runner state ───────────────────────────────
    // When _testRunActive is true, RunTick treats _runVm as "the
    // current test's VM" instead of a single run-to-end VM. As each
    // test's VM completes, RunTick finalizes that test's result and
    // advances _runVm to the next test in _testQueue. When the queue
    // is empty, RunTick emits complete=true with the aggregated
    // testFinal envelope. wait ms / prompt$ / stop all reuse the
    // existing Run-pump infrastructure unchanged — tests are just a
    // sequence of cooperative runs sharing the same pump.
    private static bool _testRunActive;
    private static FadeRuntimeContext? _testCtx;
    private static List<TestManifestEntry>? _testQueue;
    private static int _testIndex;
    private static List<FadeBasic.Sdk.FadeTestResult>? _testResults;
    private static System.Diagnostics.Stopwatch? _testRunSw;
    private static System.Diagnostics.Stopwatch? _currentTestSw;
    private static Exception? _currentTestException;

    // Routes C# → JS for HostBridge.PostMessage. Worker.js binds the
    // 'fade-runtime' module to a fan-out that posts `host-message` to
    // the page; the page dispatches by `channel` and replies with a
    // typed `host-reply` that flows back into DepositResultString etc.
    [JSImport("postHostMessage", "fade-runtime")]
    internal static partial void PostHostMessage(string channel, string payload);

    // Static wire-up: install our cooperative-scheduling primitives.
    // Library commands call these via HostBridge; we own the VM and
    // the suspend/resume bookkeeping. Each runtime host does the same
    // dance — MonoGame swaps WaitImpl in its own startup, a native CLI
    // would do similar but with synchronous semantics. Runs once on
    // first touch of FadeBridge (which is at worker boot, when the
    // worker resolves the assembly's JS exports).
    //
    // Adding a new cooperative command in a future library does NOT
    // require any changes here — the library invokes PostMessage with
    // its own channel name and SuspendVm to pause; the page-side
    // handler registers in hostHandlers. The runtime is channel-agnostic.
    static FadeBridge()
    {
        StandardCommands.WaitImpl = ms =>
        {
            // Two cooperative paths plus a defensive fallback:
            //
            //  - Cooperative pump (Run flow OR test flow): _runVm is
            //    non-null. Both share the same pump infrastructure —
            //    tests just point _runVm at one test's VM at a time,
            //    advancing between tests inside RunTick. Record the
            //    wait, suspend, let the JS pump schedule the next tick
            //    after `ms`. Worker thread stays responsive.
            //
            //  - Debug session: _debugSession is non-null. Mirror of
            //    the above for DebugTick / pumpDebugTick.
            //
            //  - Fallback: a Fade program ran outside any host driver
            //    we know about (shouldn't happen in normal use). Block
            //    via Thread.Sleep so behavior matches a desktop host.
            if (_runVm != null)
            {
                _pendingWaitMs = ms;
                _runVm.Suspend();
            }
            else if (_debugSession != null)
            {
                _pendingWaitMs = ms;
                _debugSession._vm?.Suspend();
            }
            else
            {
                System.Threading.Thread.Sleep(ms);
            }
        };
        HostBridge.PostMessage = (channel, payload) =>
            PostHostMessage(channel, payload);
        HostBridge.SuspendVm = () =>
        {
            _waitingForHostReply = true;
            _runVm?.Suspend();
        };
    }

    // Begin a run. Resolves the ILaunchable from the entry assembly and
    // builds the VM, but does NOT execute. The worker then drives the VM
    // forward via RunTick until it reports complete=true. Mirrors the
    // setup half of the old synchronous LoadAndRun.
    [JSExport]
    public static string RunStart(byte[] entryDllBytes)
    {
        try
        {
            var asm = LoadAndRegister(entryDllBytes);
            Type launchableType = null;
            foreach (var t in asm.GetTypes())
            {
                if (!t.IsClass || t.IsAbstract) continue;
                if (typeof(ILaunchable).IsAssignableFrom(t)) { launchableType = t; break; }
            }
            if (launchableType == null)
                throw new Exception("No ILaunchable implementation found in entry assembly");
            var instance = (ILaunchable)Activator.CreateInstance(launchableType);
            _runVm = new VirtualMachine(instance.Bytecode)
            {
                hostMethods = HostMethodTable.FromCommandCollection(instance.CommandCollection),
            };
            _runError = null;
            _waitingForHostReply = false;
            _pendingWaitMs = 0;
            _runStopRequested = false;
            _testRunActive = false;
            return JsonSerializer.Serialize(new { ok = true }, _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = DescribeException(ex) }, _jsonOpts);
        }
    }

    // Begin a run from raw Fade source. The Playground and any other
    // host that compiles-on-the-fly uses this instead of the DLL-based
    // RunStart. Commands come from _workspace.Commands (which already
    // includes the StandardCommands + every RegisterCommandAssembly).
    // Returns { ok, compileError? }; compile failures surface here
    // rather than at the first tick.
    [JSExport]
    public static string RunStartFromSource(string source)
    {
        try
        {
            var commands = _workspace.Commands;
            if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    compileError = errors.ToDisplay(),
                }, _jsonOpts);
            }
            _runVm = ctx.Machine;
            _runError = null;
            _waitingForHostReply = false;
            _pendingWaitMs = 0;
            _runStopRequested = false;
            _testRunActive = false;
            return JsonSerializer.Serialize(new { ok = true }, _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = DescribeException(ex) }, _jsonOpts);
        }
    }

    // Compile Fade source to a raw bytecode blob. Used by the export
    // download path and by the Playground's preview iframe — both want
    // the compiled program as bytes they can hand to RunStartFromBytecode
    // (in another process / iframe / future runtime). Returns the
    // bytecode directly; callers should check for empty (compile fail)
    // via the companion CompileToBytecodeStatus method.
    //
    // We compile against the host's current _workspace.Commands so the
    // bytecode's host-method indices match what a re-loaded runtime
    // will resolve them against (assuming it loads the same DLLs).
    [JSExport]
    public static byte[] CompileToBytecode(string source)
    {
        try
        {
            var commands = _workspace.Commands;
            if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out _))
                return Array.Empty<byte>();
            return ctx.Machine.program;
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    // Companion to CompileToBytecode — returns compile diagnostics as
    // JSON so callers can surface errors when CompileToBytecode returns
    // an empty buffer. Split into two calls because JSExport doesn't
    // give us a clean way to return both bytes AND a status struct.
    [JSExport]
    public static string CompileToBytecodeStatus(string source)
    {
        try
        {
            var commands = _workspace.Commands;
            if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    compileError = errors.ToDisplay(),
                }, _jsonOpts);
            }
            return JsonSerializer.Serialize(new { ok = true, byteCount = ctx.Machine.program.Length }, _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = DescribeException(ex) }, _jsonOpts);
        }
    }

    // Boot the cooperative pump from pre-compiled bytecode (the output
    // of CompileToBytecode, possibly produced by a different runtime
    // instance / process / build step). Commands resolve against the
    // current workspace — callers must have already RegisterCommandAssembly'd
    // every DLL the bytecode references, otherwise CALL_HOST opcodes
    // will hit a null method during the first tick.
    [JSExport]
    public static string RunStartFromBytecode(byte[] bytecode)
    {
        try
        {
            if (bytecode == null || bytecode.Length == 0)
                throw new Exception("RunStartFromBytecode: empty bytecode");
            var commands = _workspace.Commands;
            _runVm = new VirtualMachine(bytecode)
            {
                hostMethods = HostMethodTable.FromCommandCollection(commands),
            };
            _runError = null;
            _waitingForHostReply = false;
            _pendingWaitMs = 0;
            _runStopRequested = false;
            _testRunActive = false;
            return JsonSerializer.Serialize(new { ok = true }, _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = DescribeException(ex) }, _jsonOpts);
        }
    }

    // Run one cooperative batch of the program. `budget` caps the number
    // of opcodes dispatched (0 = unlimited; not recommended — see below).
    // Returns the run status so the JS pump can choose how to schedule
    // the next tick:
    //   complete=true              → run is finished; stop pumping
    //   waitingForHostReply=true   → halt pump entirely; resume only after
    //                                a host-reply deposits a value
    //   waitMs > 0                 → setTimeout(tick, waitMs) before next
    //   suspended=true otherwise   → setTimeout(tick, 0)  (yield + continue)
    //   none of the above          → setTimeout(tick, 0)  (also yield)
    // Budget 0 would defeat the whole point of the pump model (no yield
    // between opcodes), so the worker passes a finite number — high enough
    // that VM overhead per batch is small, low enough to keep heartbeats
    // alive.
    [JSExport]
    public static string RunTick(int budget)
    {
        // Queue exhausted in test mode (AdvanceTest set _runVm = null
        // because we ran the last test on the previous tick). Emit the
        // aggregated testFinal envelope.
        if (_runVm == null && _testRunActive)
            return BuildTestRunCompleteJson(stopped: false);

        if (_runVm == null)
            return JsonSerializer.Serialize(new { complete = true }, _jsonOpts);

        if (_runError != null && !_testRunActive)
            return JsonSerializer.Serialize(new { complete = true, error = _runError }, _jsonOpts);

        // Honor a pending stop request before doing any more work. The
        // pump treats this terminal result the same as a normal complete
        // and stops scheduling new ticks. Tear down the VM reference too
        // so a stale Suspend doesn't leak across runs.
        if (_runStopRequested)
        {
            _runStopRequested = false;
            _runVm = null;
            if (_testRunActive)
                return BuildTestRunCompleteJson(stopped: true);
            return JsonSerializer.Serialize(new
            {
                complete = true,
                error = "stopped",
            }, _jsonOpts);
        }

        // Per-tick reset for the wake-up hint. The host-reply flag is NOT
        // reset here — it persists across the suspend until DepositResultXxx
        // clears it (since the whole point is that the VM stays paused
        // across multiple JS event-loop ticks while we wait for the page).
        _pendingWaitMs = 0;
        Exception? testException = null;
        try
        {
            _runVm.Execute3(budget);
        }
        catch (Exception ex)
        {
            if (_testRunActive)
            {
                // Test runs don't bail on a single test's exception — the
                // test fails, we move on. The exception goes into the
                // current test's result via BuildResultFromVm below.
                testException = ex;
            }
            else
            {
                _runError = DescribeException(ex);
            }
        }

        // Test-mode transition: if the current test's VM is finished
        // (either ran past the program end or threw), finalize that
        // test's result and start the next one. Don't surface complete
        // to the pump yet — keep ticking until the queue is exhausted.
        if (_testRunActive)
        {
            var vmFinished = _runVm.instructionIndex >= _runVm.program.Length
                || testException != null;
            if (vmFinished && _testQueue != null && _testCtx != null
                && _testIndex >= 0 && _testIndex < _testQueue.Count)
            {
                _currentTestSw?.Stop();
                var entry = _testQueue[_testIndex];
                var result = FadeBasic.Sdk.FadeTestExecutor.BuildResultFromVm(
                    _runVm,
                    entry,
                    _currentTestSw?.Elapsed ?? System.TimeSpan.Zero,
                    _testCtx.Compiler.DebugData,
                    testException);
                _testResults?.Add(result);
                var progress = TestResultToObject(result);

                if (!AdvanceTest())
                {
                    // Last test in the queue — emit testProgress for
                    // this final test alongside the terminal envelope,
                    // so the Playground's per-test stream is uniform
                    // (no special-case for "the run that just ended").
                    return BuildTestRunCompleteJson(stopped: false, lastProgress: progress);
                }

                // Started next test. Tell the pump to schedule another
                // tick immediately (waitMs=0, not complete, not suspended)
                // and surface this test's result so the UI flips its
                // row from "running" to pass/fail before the next test
                // visibly starts. `testStarting` names the test that
                // just became active — the iframe uses it to clear its
                // output area so each test's prints start on a clean
                // slate.
                var nextName = _testQueue![_testIndex].name;
                return JsonSerializer.Serialize(new
                {
                    complete = false,
                    suspended = false,
                    waitMs = 0,
                    waitingForHostReply = false,
                    testProgress = progress,
                    testStarting = new { name = nextName },
                }, _jsonOpts);
            }
            // Test's VM is still mid-flight — suspended on a wait or
            // host-reply, or just used up its budget. Surface like a
            // normal Run tick so the pump schedules appropriately.
        }

        var complete = _runError != null
            || _runVm.instructionIndex >= _runVm.program.Length;

        return JsonSerializer.Serialize(new
        {
            complete,
            suspended = !complete && _runVm.isSuspendRequested,
            waitMs = _pendingWaitMs,
            waitingForHostReply = _waitingForHostReply,
            error = _runError,
        }, _jsonOpts);
    }

    // Terminate an in-flight run. Sets a flag the next RunTick honors,
    // clears the prompt-wait so the pump can resume even from a halted
    // state, and asks the VM to break out of the current Execute3 batch.
    // Safe to call when no run is active — it's a no-op then.
    //
    // The pump driver (worker.js) is expected to follow this by either
    // calling RunTick once (to flush the synthetic stopped result) or
    // by simply letting an in-flight setTimeout fire — the next tick
    // observes the stop flag and exits.
    [JSExport]
    public static string StopRun()
    {
        _runStopRequested = true;
        _waitingForHostReply = false;
        _pendingWaitMs = 0;
        // _runVm is also the test pump's current VM, so a single
        // Suspend handles both cases. The next RunTick sees the stop
        // flag, finalizes (with stopped error), and emits the terminal
        // event.
        _runVm?.Suspend();
        return "true";
    }

    // ─── Deposit-result entry points ──────────────────────────────────
    // The worker calls into one of these in response to a `host-reply`
    // message from the page. The library command earlier pushed a
    // type-shaped placeholder onto the operand stack (source-generated
    // executors push the default-value bytes + the type code based on
    // the command's C# return type) and suspended the VM via
    // HostBridge.SuspendVm. The matching Deposit* call swaps that
    // placeholder for the real value; the next RunTick resumes and
    // the consuming opcode pops the real value.
    //
    // The set of supported result types matches the FadeBasic VM's
    // primitive type table (see Virtual/OpCodes.cs:TypeCodes). String
    // is heap-allocated; scalars overwrite the placeholder bytes in
    // place. Void is a no-op stack-wise (the command pushed nothing).

    // Helper: gate every Deposit* on "we're actually paused waiting".
    // Returns true if the deposit should proceed; flips the flag off
    // either way so the pump can resume on the next tick.
    private static bool BeginDeposit()
    {
        if (_runVm == null || !_waitingForHostReply) return false;
        return true;
    }

    private static string EndDeposit(bool ok)
    {
        _waitingForHostReply = false;
        return ok ? "true" : "false";
    }

    [JSExport]
    public static string DepositResultString(string value) =>
        EndDeposit(BeginDeposit() && HostStackOps.SwapTopString(_runVm, value));

    // ─── Scalar deposits ──────────────────────────────────────────────
    // JS Number is double-precision, so each of these accepts whatever
    // JS-native type maps cleanly to its C# parameter; the worker.js
    // dispatcher coerces JS values into the right shape before calling.
    // BitConverter.GetBytes handles endianness consistently with what
    // the source-generated executors push.

    [JSExport]
    public static string DepositResultInt(int value) =>
        EndDeposit(BeginDeposit() && HostStackOps.SwapTopPrimitive(_runVm,
            TypeCodes.INT, BitConverter.GetBytes(value)));

    [JSExport]
    public static string DepositResultReal(float value) =>
        EndDeposit(BeginDeposit() && HostStackOps.SwapTopPrimitive(_runVm,
            TypeCodes.REAL, BitConverter.GetBytes(value)));

    [JSExport]
    public static string DepositResultBool(bool value) =>
        EndDeposit(BeginDeposit() && HostStackOps.SwapTopPrimitive(_runVm,
            TypeCodes.BOOL, BitConverter.GetBytes(value)));

    [JSExport]
    public static string DepositResultByte(byte value) =>
        EndDeposit(BeginDeposit() && HostStackOps.SwapTopPrimitive(_runVm,
            TypeCodes.BYTE, new[] { value }));

    // ushort isn't a clean JS-interop type, and the page-side number is
    // already a double anyway; the worker mask/coerces and we narrow
    // here. Same shape for dword below.
    [JSExport]
    public static string DepositResultWord(int value) =>
        EndDeposit(BeginDeposit() && HostStackOps.SwapTopPrimitive(_runVm,
            TypeCodes.WORD, BitConverter.GetBytes((ushort)value)));

    [JSExport]
    public static string DepositResultDword(int value) =>
        EndDeposit(BeginDeposit() && HostStackOps.SwapTopPrimitive(_runVm,
            TypeCodes.DWORD, BitConverter.GetBytes((uint)value)));

    // int64. The JSMarshalAs annotation tells the JS generator to use
    // BigInt on the JS side — without it the generator refuses to
    // marshal `long` (SYSLIB1072). The page handler should return
    // `{ resultType: 'dint', value: BigInt(...) }` to preserve values
    // past 2^53.
    [JSExport]
    public static string DepositResultDint(
        [JSMarshalAs<JSType.BigInt>] long value) =>
        EndDeposit(BeginDeposit() && HostStackOps.SwapTopPrimitive(_runVm,
            TypeCodes.DINT, BitConverter.GetBytes(value)));

    [JSExport]
    public static string DepositResultDfloat(double value) =>
        EndDeposit(BeginDeposit() && HostStackOps.SwapTopPrimitive(_runVm,
            TypeCodes.DFLOAT, BitConverter.GetBytes(value)));

    // Void-returning command: the executor pushed nothing, so there's
    // no placeholder to swap. Just clear the wait flag so the pump can
    // resume on the next tick.
    [JSExport]
    public static string DepositResultVoid() =>
        EndDeposit(BeginDeposit());

    // Unwrap TargetInvocationException / TypeInitializationException so the
    // page sees the real cause instead of "Arg_TargetInvocationException".
    // Resource-key messages are common under WASM trimming — strings like
    // ArgumentNull_Generic land in the report; the chain plus type name
    // usually narrows things down.
    private static string DescribeException(Exception ex)
    {
        var sb = new StringBuilder();
        var current = ex;
        var depth = 0;
        while (current != null && depth < 6)
        {
            if (sb.Length > 0) sb.Append("\n  → ");
            sb.Append(current.GetType().FullName).Append(": ").Append(current.Message);
            if (!string.IsNullOrEmpty(current.StackTrace) && depth == 0)
                sb.Append('\n').Append(current.StackTrace);
            current = current.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    // camelCase JSON to match LSP wire-protocol convention; TS interfaces in
    // Playground use lowercase field names. IncludeFields is critical — Core
    // DTOs use public FIELDS (not properties), which System.Text.Json ignores
    // by default. Without this every diagnostic serializes as {} and the
    // Playground throws "d.range is undefined".
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    // ─── Run ──────────────────────────────────────────────────────────────
    [JSInvokable]
    [JSExport]
    // Returns a JSON envelope so the page can format different kinds of
    // output (compile errors / runtime errors / printed stdout) with their
    // own styling. Shape: { compileError, runtimeError, printed }. Any
    // field may be null/empty. Print output also streams through `onPrint`
    // during execution; we drain anything that wasn't flushed yet.
    public static string CompileAndRun(string source)
    {
        var commands = _workspace.Commands;
        if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
        {
            return JsonSerializer.Serialize(new
            {
                compileError = errors.ToDisplay(),
                runtimeError = (string)null,
                printed = "",
            }, _jsonOpts);
        }

        string runtimeError = null;
        try { ctx.Run(); }
        catch (Exception ex) { runtimeError = ex.GetType().Name + ": " + ex.Message; }

        return JsonSerializer.Serialize(new
        {
            compileError = (string)null,
            runtimeError,
            printed = "",
        }, _jsonOpts);
    }

    // ─── LSP entry points — thin adapters over Core ───────────────────────

    [JSExport]
    public static string LspSetDocument(string uri, string text)
    {
        try
        {
            var doc = _workspace.SetDocument(uri, text);
            return JsonSerializer.Serialize(DiagnosticsHandler.Compute(doc), _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new LspDiagnostic[]
            {
                new LspDiagnostic
                {
                    Severity = LspDiagnosticSeverity.Error,
                    Range = new LspRange
                    {
                        Start = new LspPosition { Line = 0, Character = 0 },
                        End = new LspPosition { Line = 0, Character = 1 },
                    },
                    Message = $"LSP internal error: {ex.GetType().Name}: {ex.Message}",
                    Code = "INT-001",
                    Source = "fade",
                },
            }, _jsonOpts);
        }
    }

    [JSExport]
    public static string LspGetSemanticTokens(string uri)
    {
        var doc = _workspace.Get(uri);
        return JsonSerializer.Serialize(SemanticTokensHandler.Compute(doc), _jsonOpts);
    }

    [JSExport]
    public static string LspHover(string uri, int line, int character)
    {
        var doc = _workspace.Get(uri);
        var hover = HoverHandler.Compute(doc, line, character);
        return hover == null ? "null" : JsonSerializer.Serialize(hover, _jsonOpts);
    }

    [JSExport]
    public static string LspCompletion(string uri, int line, int character)
    {
        var doc = _workspace.Get(uri);
        var items = CompletionHandler.Compute(doc, line, character);
        return JsonSerializer.Serialize(items, _jsonOpts);
    }

    [JSExport]
    public static string LspGetAllDiagnostics()
    {
        var all = new Dictionary<string, List<LspDiagnostic>>();
        foreach (var doc in _workspace.AllDocuments)
            all[doc.Uri] = DiagnosticsHandler.Compute(doc);
        return JsonSerializer.Serialize(all, _jsonOpts);
    }

    [JSExport]
    public static string LspSignatureHelp(string uri, int line, int character)
    {
        var doc = _workspace.Get(uri);
        var sig = SignatureHelpHandler.Compute(doc, line, character);
        return sig == null ? "null" : JsonSerializer.Serialize(sig, _jsonOpts);
    }

    [JSExport]
    public static string LspReferences(string uri, int line, int character)
    {
        var doc = _workspace.Get(uri);
        var refs = ReferencesHandler.Compute(doc, line, character);
        return JsonSerializer.Serialize(refs ?? new List<LspLocation>(), _jsonOpts);
    }

    [JSExport]
    public static string LspDefinition(string uri, int line, int character)
    {
        var doc = _workspace.Get(uri);
        var def = DefinitionHandler.Compute(doc, line, character);
        return def == null ? "null" : JsonSerializer.Serialize(def, _jsonOpts);
    }

    [JSExport]
    public static string LspDocumentSymbols(string uri)
    {
        var doc = _workspace.Get(uri);
        var syms = DocumentSymbolHandler.Compute(doc);
        return JsonSerializer.Serialize(syms ?? new List<LspDocumentSymbol>(), _jsonOpts);
    }

    [JSExport]
    public static string LspFoldingRanges(string uri)
    {
        var doc = _workspace.Get(uri);
        var ranges = FoldingRangeHandler.Compute(doc);
        return JsonSerializer.Serialize(ranges ?? new List<LspFoldingRange>(), _jsonOpts);
    }

    // optionsJson is an LspFormattingOptions in camelCase JSON.
    [JSExport]
    public static string LspFormat(string uri, string optionsJson)
    {
        var doc = _workspace.Get(uri);
        var opts = string.IsNullOrEmpty(optionsJson)
            ? new LspFormattingOptions()
            : JsonSerializer.Deserialize<LspFormattingOptions>(optionsJson, _jsonOpts) ?? new LspFormattingOptions();
        var edits = FormattingHandler.Compute(doc, opts);
        return JsonSerializer.Serialize(edits, _jsonOpts);
    }

    [JSExport]
    public static string LspFormatRange(string uri, string optionsJson, int startLine, int startCh, int endLine, int endCh)
    {
        var doc = _workspace.Get(uri);
        var opts = string.IsNullOrEmpty(optionsJson)
            ? new LspFormattingOptions()
            : JsonSerializer.Deserialize<LspFormattingOptions>(optionsJson, _jsonOpts) ?? new LspFormattingOptions();
        var range = new LspRange
        {
            Start = new LspPosition { Line = startLine, Character = startCh },
            End = new LspPosition { Line = endLine, Character = endCh },
        };
        var edits = FormattingHandler.ComputeRange(doc, opts, range);
        return JsonSerializer.Serialize(edits, _jsonOpts);
    }

    [JSExport]
    public static string LspFormatOnType(string uri, string optionsJson, int line, int character)
    {
        var doc = _workspace.Get(uri);
        var opts = string.IsNullOrEmpty(optionsJson)
            ? new LspFormattingOptions()
            : JsonSerializer.Deserialize<LspFormattingOptions>(optionsJson, _jsonOpts) ?? new LspFormattingOptions();
        var edits = FormattingHandler.ComputeOnType(doc, opts, new LspPosition { Line = line, Character = character });
        return JsonSerializer.Serialize(edits, _jsonOpts);
    }

    [JSExport]
    public static string LspRename(string uri, int line, int character, string newName)
    {
        var doc = _workspace.Get(uri);
        var edit = RenameHandler.Compute(doc, line, character, newName);
        return edit == null ? "null" : JsonSerializer.Serialize(edit, _jsonOpts);
    }

    // ─── Help / command docs ──────────────────────────────────────────────
    // Returns a JSON array of every command currently loaded in the
    // workspace's CommandCollection, with the same markdown the hover
    // provider renders. Used by the page's Help tab to build a TOC +
    // per-command reader. One row per UNIQUE command name (overloads
    // collapse — the first signature wins). Sorted alphabetically.
    [JSExport]
    public static string ListCommandDocs()
    {
        try
        {
            var commands = _workspace.Commands?.Commands;
            if (commands == null)
            {
                return "[]";
            }
            // Dedupe by command.name. Overloads (e.g. `rgb` with 3 vs 4
            // args) share a name; we surface one row per name and use the
            // first CommandInfo we find — BuildCommandMarkdown already
            // describes all parameter slots from that signature.
            var seen = new HashSet<string>();
            var rows = new List<object>();
            foreach (var c in commands)
            {
                if (string.IsNullOrEmpty(c.name)) continue;
                if (!seen.Add(c.name)) continue;
                string markdown;
                try
                {
                    markdown = FadeBasic.LSP.Core.Handlers.HoverHandler.BuildCommandMarkdown(
                        c, _workspace.Docs);
                }
                catch (Exception ex)
                {
                    markdown = $"### {c.name}\n\n_Failed to render docs: {ex.Message}_";
                }
                rows.Add(new
                {
                    name = c.name,
                    signature = c.sig,
                    // Best-effort: classify into a "group" based on the
                    // command name's first word for the TOC. The native
                    // command-doc generator keeps a category in metadata
                    // we don't propagate here yet; this is a useful
                    // approximation until that's wired through.
                    group = GuessGroup(c.name),
                    markdown,
                });
            }
            // Stable alphabetical order so the TOC is deterministic.
            rows.Sort((a, b) =>
                string.Compare(
                    (string)a.GetType().GetProperty("name").GetValue(a),
                    (string)b.GetType().GetProperty("name").GetValue(b),
                    StringComparison.OrdinalIgnoreCase));
            return JsonSerializer.Serialize(rows, _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = "Failed to enumerate command docs: " + ex.Message,
            }, _jsonOpts);
        }
    }

    // Cheap heuristic: cluster commands by their first word so the TOC
    // gets meaningful section headings (e.g. "print", "string", "wait").
    // For multi-word commands ("wait ms", "wait key") this also yields a
    // shared bucket. Single-word commands get their own bucket named
    // after themselves only when no peers share the prefix — to avoid
    // a 200-bucket TOC, single-words fall back to a generic "Core" group.
    private static string GuessGroup(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Core";
        var idx = name.IndexOf(' ');
        return idx > 0 ? name.Substring(0, idx) : "Core";
    }

    // ─── Tests ────────────────────────────────────────────────────────────
    // Compile the source and list the test entry points. Returns a JSON
    // array of { name, isAbstract, fromParent, sourceLine }. On compile
    // failure returns an empty array (errors surface via LspSetDocument).
    [JSExport]
    public static string ListTests(string source)
    {
        try
        {
            var commands = _workspace.Commands;
            if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out _))
                return "[]";
            var tests = new List<object>();
            foreach (var t in ctx.Compiler.TestManifest)
            {
                tests.Add(new
                {
                    name = t.name,
                    isAbstract = t.isAbstract,
                    fromParent = t.fromParent,
                    sourceLine = t.sourceLine,
                    sourceChar = t.sourceChar,
                });
            }
            return JsonSerializer.Serialize(tests, _jsonOpts);
        }
        catch
        {
            return "[]";
        }
    }

    // Compile + run either all tests (testName empty / null) or a single
    // named test. Returns JSON with { passed, failed, duration, results[],
    // printed, error? }. `printed` is the captured stdout from any
    // `print` statements run during testing.
    // Begin a cooperative test run. Compiles `source`, builds the
    // queue of tests to run (`testName` empty/null = all non-abstract
    // tests; otherwise just the named one), and starts the first
    // test's VM by setting _runVm. The JS-side pump (worker.js) then
    // drives RunTick repeatedly, exactly the same way it does for a
    // regular Run — RunTick observes _testRunActive and handles the
    // per-test transitions internally. Final result envelope shows
    // up via RunTick's complete=true return, carrying the testFinal
    // payload aggregated across all tests.
    [JSExport]
    public static string RunTestsStart(string source, string testName)
    {
        _runStopRequested = false;
        _testRunActive = false;
        _testQueue = null;
        _testResults = null;
        _runError = null;

        var commands = _workspace.Commands;
        if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                compileError = errors.ToDisplay(),
            }, _jsonOpts);
        }

        var selectAll = string.IsNullOrWhiteSpace(testName);
        var queue = new List<TestManifestEntry>();
        foreach (var t in ctx.Compiler.TestManifest)
        {
            if (t.isAbstract) continue;
            if (!selectAll && !string.Equals(t.name, testName, System.StringComparison.OrdinalIgnoreCase)) continue;
            queue.Add(t);
            if (!selectAll) break;
        }

        _testCtx = ctx;
        _testQueue = queue;
        _testResults = new List<FadeBasic.Sdk.FadeTestResult>();
        _testIndex = -1;
        _testRunSw = System.Diagnostics.Stopwatch.StartNew();
        _testRunActive = true;

        // Boot the first test's VM. If the queue is empty (no matching
        // tests), AdvanceTest returns false and _runVm stays null;
        // the next RunTick reports complete with an empty testFinal.
        AdvanceTest();
        return JsonSerializer.Serialize(new { ok = true }, _jsonOpts);
    }

    // Advance to the next test in the queue. Returns true if a new
    // test was started (so the pump should keep ticking), false if
    // the queue is exhausted. On failure to start (queue empty),
    // _runVm is set to null so the next RunTick observes the
    // not-running state and emits the testFinal envelope.
    private static bool AdvanceTest()
    {
        if (_testQueue == null || _testCtx == null) { _runVm = null; return false; }
        _testIndex++;
        if (_testIndex >= _testQueue.Count) { _runVm = null; return false; }
        var entry = _testQueue[_testIndex];
        var vm = new VirtualMachine(_testCtx.Machine.program, entry.entryPointAddress)
        {
            hostMethods = _testCtx.Compiler.methodTable,
            isTestExecution = true,
        };
        _runVm = vm;
        _runError = null;
        _waitingForHostReply = false;
        _pendingWaitMs = 0;
        _currentTestSw = System.Diagnostics.Stopwatch.StartNew();
        _currentTestException = null;
        return true;
    }

    // Build the terminal envelope a test run emits via RunTick when
    // either the queue is exhausted or StopRun was honored. `stopped`
    // tags the surface error so callers can distinguish "ran to end"
    // from "user cancelled" — partial results in either case.
    //
    // `lastProgress` carries the per-test event for the test that
    // finalized in THIS same tick (when called from the
    // "AdvanceTest returned false" path). The Playground's progress
    // listener gets the last test the same way it gets every other —
    // no special-case for "the run that just ended."
    private static string BuildTestRunCompleteJson(bool stopped, object? lastProgress = null)
    {
        _testRunActive = false;
        _testRunSw?.Stop();
        var results = _testResults ?? new List<FadeBasic.Sdk.FadeTestResult>();
        var passed = 0; var failed = 0;
        foreach (var r in results) { if (r.passed) passed++; else failed++; }
        var duration = _testRunSw?.Elapsed.TotalMilliseconds ?? 0;
        var payload = new
        {
            complete = true,
            testProgress = lastProgress,
            testFinal = new
            {
                passed,
                failed,
                duration,
                results = ResultsToObjects(results),
            },
            error = stopped ? "Stopped" : (string?)null,
        };
        return JsonSerializer.Serialize(payload, _jsonOpts);
    }

    // Shape a single test result for JSON wire transport. Extracted from
    // ResultsToObjects so the test-progress stream (one event per
    // finalized test) can reuse the same payload shape the terminal
    // testFinal envelope uses — the Playground's tests panel handles
    // both with one code path.
    private static object TestResultToObject(FadeBasic.Sdk.FadeTestResult r)
    {
        var frames = new List<object>();
        if (r.failureFrames != null)
        {
            foreach (var f in r.failureFrames)
            {
                frames.Add(new
                {
                    functionName = f.functionName,
                    lineNumber = f.lineNumber,
                    charNumber = f.charNumber,
                    instructionIndex = f.instructionIndex,
                });
            }
        }
        return new
        {
            name = r.testName,
            passed = r.passed,
            duration = r.duration.TotalMilliseconds,
            failureMessage = r.failureMessage,
            failureReason = r.failureReason,
            failureSourceText = r.failureSourceText,
            failureInstructionIndex = r.failureInstructionIndex,
            failureFrames = frames,
        };
    }

    private static List<object> ResultsToObjects(List<FadeBasic.Sdk.FadeTestResult> results)
    {
        var list = new List<object>(results.Count);
        foreach (var r in results)
        {
            var frames = new List<object>();
            if (r.failureFrames != null)
            {
                foreach (var f in r.failureFrames)
                {
                    frames.Add(new
                    {
                        functionName = f.functionName,
                        lineNumber = f.lineNumber,
                        charNumber = f.charNumber,
                        instructionIndex = f.instructionIndex,
                    });
                }
            }
            list.Add(new
            {
                name = r.testName,
                passed = r.passed,
                duration = r.duration.TotalMilliseconds,
                failureMessage = r.failureMessage,
                failureReason = r.failureReason,
                failureSourceText = r.failureSourceText,
                failureInstructionIndex = r.failureInstructionIndex,
                failureFrames = frames,
            });
        }
        return list;
    }

    // ─── Debug session (DAP) ────────────────────────────────────────────
    // One active session at a time. The worker calls DebugStart() to
    // compile + boot a session, then DebugTick() in a loop to make
    // forward progress, draining outbound messages between ticks.

    private static FadeRuntimeContext _debugContext;
    private static WebDebugSession _debugSession;
    private static int _debugMessageIdCounter;
    // Tracks the pause state across ticks so we can emit a synthetic stop
    // event on running→paused transitions (e.g. step landings, which the
    // base DebugSession only signals via a PROTO_ACK on the step request).
    private static bool _debugWasPaused;
    // Set when DebugStartTest boots a session targeting a specific test.
    // GetDebugTestResult uses this to know which test name to report, and
    // (via _debugSession._vm.assertionFailure) whether it passed. Cleared
    // by DebugStart (non-test) and DebugTerminate so subsequent debug
    // queries return null instead of stale data.
    private static FadeBasic.Virtual.TestManifestEntry _debugTestEntry;

    private static int NextDebugId() => ++_debugMessageIdCounter;

    // Compile + boot a debug session that targets a specific test entry
    // point. Mirrors FadeTestExecutor.RunTest's setup — a fresh VM at the
    // test's entry address with isTestExecution=true — but wraps it in a
    // WebDebugSession so we can pause, step, and inspect normally.
    [JSExport]
    public static string DebugStartTest(string source, string testName)
    {
        try
        {
            DebugTerminate();
            var commands = _workspace.Commands;
            if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "Compile failed:\n" + errors.ToDisplay(),
                    statementLines = Array.Empty<int>(),
                }, _jsonOpts);
            }
            FadeBasic.Virtual.TestManifestEntry foundEntry = null;
            foreach (var t in ctx.Compiler.TestManifest)
            {
                if (string.Equals(t.name, testName, StringComparison.OrdinalIgnoreCase))
                {
                    foundEntry = t;
                    break;
                }
            }
            if (foundEntry == null || foundEntry.isAbstract)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = foundEntry == null
                        ? $"No test named '{testName}' found"
                        : $"Test '{testName}' is abstract and cannot be debugged",
                }, _jsonOpts);
            }

            // Fresh VM at the test's entry address (matches
            // FadeTestExecutor.RunTest's bootstrap so the test runs the same
            // way it would in normal test execution).
            var vm = new FadeBasic.Virtual.VirtualMachine(ctx.Machine.program, foundEntry.entryPointAddress)
            {
                hostMethods = ctx.Compiler.methodTable,
                isTestExecution = true,
            };
            _debugContext = ctx;
            _debugSession = new WebDebugSession(vm, ctx.Compiler.DebugData, commands);
            _debugTestEntry = foundEntry;
            _debugWasPaused = true;
            EnqueueBasic(DebugMessageType.REQUEST_PAUSE);

            var lines = new SortedSet<int>();
            foreach (var t in ctx.Compiler.DebugData.statementTokens)
                if (t?.token != null) lines.Add(t.token.lineNumber);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                statementLines = lines,
                testName = foundEntry.name,
                testLine = foundEntry.sourceLine,
            }, _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Debug-test start failed: " + ex.Message,
            }, _jsonOpts);
        }
    }

    // Compile + boot a debug session. Returns JSON with { ok, error?,
    // statementTokens[] } so the page can render gutter glyphs at valid
    // breakpoint lines.
    [JSExport]
    public static string DebugStart(string source)
    {
        try
        {
            DebugTerminate(); // reset any prior session.
            var commands = _workspace.Commands;
            if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "Compile failed:\n" + errors.ToDisplay(),
                    statementLines = Array.Empty<int>(),
                }, _jsonOpts);
            }
            _debugContext = ctx;
            _debugSession = new WebDebugSession(ctx.Machine, ctx.Compiler.DebugData, commands);
            _debugTestEntry = null; // non-test debug; clear any prior test marker
            // Pre-mark as paused so the first tick's running→paused detection
            // doesn't fire a synthetic stop event for our internal start-
            // pause. Real pauses (breakpoints, steps) flip from false→true
            // and emit normally.
            _debugWasPaused = true;

            // Start the session in a paused state. The page must set its
            // breakpoints and then call DebugContinue() to begin running.
            // Without this, the tick loop in worker.js would race the page
            // and execute past any breakpoints before they're installed.
            EnqueueBasic(DebugMessageType.REQUEST_PAUSE);

            // Surface valid statement lines so the editor can show breakpoint
            // hints. statementTokens have 1-based lineNumber.
            var lines = new SortedSet<int>();
            foreach (var t in ctx.Compiler.DebugData.statementTokens)
                if (t?.token != null) lines.Add(t.token.lineNumber);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                statementLines = lines,
            }, _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Debug start failed: " + ex.Message,
            }, _jsonOpts);
        }
    }

    // Run a budget of VM instructions. Returns drained outbound messages
    // (as a JSON array of DebugMessage) + a small status object. The
    // worker loops over this until either a stop message arrives, a
    // terminate request comes in, or the program completes.
    [JSExport]
    public static string DebugTick(int ops)
    {
        if (_debugSession == null)
            return JsonSerializer.Serialize(new { running = false, complete = true, messages = Array.Empty<object>() }, _jsonOpts);

        // Per-tick reset for the cooperative-wait hint. WaitImpl writes
        // this when `wait ms` fires inside the debug session; the pump
        // (worker.js pumpDebugTick) reads it from the response and uses
        // it as the next setTimeout delay. Without the reset, a stale
        // value from a previous tick would re-trigger the wait.
        _pendingWaitMs = 0;
        try { _debugSession.StartDebugging(ops); }
        catch (Exception ex) { /* never fail the worker — surface as a message */
            _debugSession.Enqueue(new DebugMessage { id = NextDebugId(), type = DebugMessageType.NOOP });
            return JsonSerializer.Serialize(new
            {
                running = false,
                complete = true,
                error = "Runtime exception: " + ex.Message,
                messages = Array.Empty<object>(),
            }, _jsonOpts);
        }
        // If WaitImpl flipped requestedExit to unwind early (kind=3 yield
        // for breakpoint updates etc., or kind=2 terminate before the
        // page's debug-terminate has landed), clear the flag now so the
        // NEXT tick can resume normally. For genuine kind=2 terminate
        // the debug-terminate message will null _debugSession on the
        // next worker tick anyway, so the reset is harmless there.
        _debugSession.ClearYieldRequest();

        var drained = _debugSession.DrainOutbound();
        var msgs = new List<object>(drained.Count);
        foreach (var m in drained)
        {
            msgs.Add(new
            {
                id = m.id,
                type = m.type.ToString(),
                json = m.RawJson ?? m.Jsonify(),
            });
        }

        // No synthetic events. The page acts as its own DAP adapter — it
        // listens for PROTO_ACK with status=1 on its own step requests and
        // treats those as "stopped after step", same way a real DAP adapter
        // translates the ACK into a DAP Stopped event for VSCode.

        var printed = "";
        return JsonSerializer.Serialize(new
        {
            running = !_debugSession.IsPaused,
            paused = _debugSession.IsPaused,
            complete = _debugSession.ProgramComplete,
            instructionPointer = _debugSession.InstructionPointer,
            messages = msgs,
            // Cooperative wait: when `wait ms` fired during this tick
            // the JS pump should setTimeout for that duration before
            // the next tick. Zero means "no wait pending" — pump uses
            // its normal small interval.
            waitMs = _pendingWaitMs,
            printed,
        }, _jsonOpts);
    }

    // Replace the active breakpoint set. linesJson is a JSON array of
    // { lineNumber, colNumber? } pairs in the source's coordinate space.
    [JSExport]
    public static string DebugSetBreakpoints(string linesJson)
    {
        if (_debugSession == null) return "false";
        var input = JsonSerializer.Deserialize<List<BreakpointRequestDto>>(linesJson, _jsonOpts)
                    ?? new List<BreakpointRequestDto>();
        var msg = new RequestBreakpointMessage
        {
            id = NextDebugId(),
            type = DebugMessageType.REQUEST_BREAKPOINTS,
            breakpoints = input.Select(b => new Breakpoint
            {
                lineNumber = b.Line,
                colNumber = b.Column,
            }).ToList(),
        };
        // RawJson is what the session uses when re-parsing typed payloads.
        msg.RawJson = msg.Jsonify();
        _debugSession.Enqueue(msg);
        return "true";
    }

    [JSExport]
    public static string DebugStep(string kind)
    {
        if (_debugSession == null) return "false";
        var type = kind switch
        {
            "over" => DebugMessageType.REQUEST_STEP_OVER,
            "in"   => DebugMessageType.REQUEST_STEP_IN,
            "out"  => DebugMessageType.REQUEST_STEP_OUT,
            _ => DebugMessageType.NOOP,
        };
        if (type == DebugMessageType.NOOP) return "false";
        EnqueueBasic(type);
        return "true";
    }

    [JSExport]
    public static string DebugContinue()
    {
        if (_debugSession == null) return "false";
        EnqueueBasic(DebugMessageType.REQUEST_PLAY);
        return "true";
    }

    [JSExport]
    public static string DebugPause()
    {
        if (_debugSession == null) return "false";
        EnqueueBasic(DebugMessageType.REQUEST_PAUSE);
        return "true";
    }

    [JSExport]
    public static string DebugTerminate()
    {
        // Do NOT enqueue REQUEST_TERMINATE — DebugSession's handler calls
        // Environment.Exit(0) which would kill the entire WASM runtime.
        // Just drop our references; the session is GC'd naturally and the
        // tick loop sees `session == null` on its next call.
        _debugSession = null;
        _debugContext = null;
        _debugTestEntry = null;
        return "true";
    }

    // Extract a FadeTestResult from the currently-debugging test's VM.
    // Returns "null" (JSON) when the session isn't a test debug or when
    // there's no live session to inspect.
    //
    // Callable at any point during a debug-test session — the Playground
    // typically calls it once the session emits 'complete' so it can
    // flip the test row from 'running' to 'pass'/'fail'. Calling
    // mid-execution returns a partial snapshot (assertionFailure may
    // not be set yet); the result is most meaningful when the VM has
    // run past program.Length, which is exactly the 'complete' signal
    // the Playground listens for.
    [JSExport]
    public static string GetDebugTestResult()
    {
        if (_debugSession == null || _debugTestEntry == null) return "null";
        var vm = _debugSession._vm;
        if (vm == null) return "null";
        var elapsed = System.TimeSpan.Zero; // debug sessions don't time tests
        var result = FadeBasic.Sdk.FadeTestExecutor.BuildResultFromVm(
            vm,
            _debugTestEntry,
            elapsed,
            _debugContext?.Compiler.DebugData,
            runtimeException: null);
        return JsonSerializer.Serialize(TestResultToObject(result), _jsonOpts);
    }

    [JSExport]
    public static string DebugStackFrames()
    {
        if (_debugSession == null) return "[]";
        var frames = _debugSession.GetFrames2();
        return JsonSerializer.Serialize(frames, _jsonOpts);
    }

    [JSExport]
    public static string DebugScopes(int frameId)
    {
        if (_debugSession == null) return "{\"scopes\":[]}";
        var resp = _debugSession.GetScopes(new DebugScopeRequest { frameIndex = frameId });
        StripRuntimeRefs(resp);
        return JsonSerializer.Serialize(resp, _jsonOpts);
    }

    [JSExport]
    public static string DebugVariableExpansion(int variableId)
    {
        if (_debugSession == null) return "{\"scopes\":[]}";
        var sub = _debugSession.variableDb.Expand(variableId);
        var msg = new ScopesMessage { scopes = new List<DebugScope> { sub } };
        StripRuntimeRefs(msg);
        return JsonSerializer.Serialize(msg, _jsonOpts);
    }

    // DebugVariable carries a `runtimeVariable` field that holds live VM
    // internals (delegates, byref data) — System.Text.Json can't serialize
    // them. The native LSP/DAP serializer skips this via IJsonable's
    // ProcessJson, but our STJ-based path here doesn't honor that. Null
    // the field before serializing so the response is clean.
    private static void StripRuntimeRefs(ScopesMessage msg)
    {
        if (msg?.scopes == null) return;
        foreach (var scope in msg.scopes)
        {
            if (scope?.variables == null) continue;
            foreach (var v in scope.variables) v.runtimeVariable = null;
        }
    }

    [JSExport]
    public static string DebugEval(int frameId, string expression)
    {
        if (_debugSession == null) return "null";
        var result = _debugSession.Eval(frameId, expression);
        return JsonSerializer.Serialize(result, _jsonOpts);
    }

    [JSExport]
    public static string DebugRepl(int frameId, string code)
    {
        if (_debugSession == null) return "null";
        var result = _debugSession.ReplExec(frameId, code);
        return JsonSerializer.Serialize(result, _jsonOpts);
    }

    [JSExport]
    public static string DebugSetVariable(int frameId, int variableId, string rhs)
    {
        if (_debugSession == null) return "null";
        var result = _debugSession.Eval(frameId, rhs, variableId);
        // DebugVariableDatabase caches the local/global scope on first read
        // and returns the cached object on subsequent calls. After a
        // successful set the underlying VM memory is updated but the cached
        // DebugVariable.value strings still show the old display value.
        // Bust the cache so the next GetScopes call rebuilds with fresh
        // values. (ClearLifetime resets variable IDs too — the page must
        // re-request scopes; expandedVars by-id state on the client
        // intentionally resets per pause anyway.)
        if (result != null && result.id != -1)
        {
            try { _debugSession.variableDb.ClearLifetime(); } catch { /* best effort */ }
        }
        return JsonSerializer.Serialize(result, _jsonOpts);
    }

    private static void EnqueueBasic(DebugMessageType type)
    {
        if (_debugSession == null) return;
        var msg = new DebugMessage { id = NextDebugId(), type = type };
        msg.RawJson = msg.Jsonify();
        _debugSession.Enqueue(msg);
    }

    private sealed class BreakpointRequestDto
    {
        public int Line { get; set; }
        public int Column { get; set; }
    }

    // Returns a JSON object with FadeBasic + .NET runtime version strings
    // for display in the browser's Diagnostics panel.
    [JSExport]
    public static string GetVersionInfo()
    {
        var asm = typeof(FadeBasic.Virtual.VirtualMachine).Assembly;
        var attrs = (System.Reflection.AssemblyInformationalVersionAttribute[])
            asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
        var fadeVersion = attrs.Length > 0 ? attrs[0].InformationalVersion : asm.GetName().Version?.ToString() ?? "unknown";
        var dotnetVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        return JsonSerializer.Serialize(new { fadeBasic = fadeVersion, dotnet = dotnetVersion });
    }
}

