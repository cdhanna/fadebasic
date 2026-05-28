using System;
using System.Collections.Generic;
using System.Diagnostics;
using FadeBasic.Json;
using FadeBasic.Sdk;
using FadeBasic.Virtual;
using FadeSdk = FadeBasic.Sdk.Fade;

namespace FadeBasic.Sdk
{
    // Host-agnostic cooperative scheduler for the Fade VM.
    //
    // Both runtime hosts (FadeBasic.Export.Web's iframe,
    // WebRuntime.MonoGame's Game1.Update loop, and future hosts)
    // share this state + these methods. The host is responsible for:
    //
    //   1. Setting `CommandsAccessor` so compile-from-source paths
    //      can fetch the host's active CommandCollection.
    //   2. Wiring `StandardCommands.WaitImpl` to call
    //      `OnCooperativeWait(ms)` — sets the wait deadline + suspends.
    //   3. Wiring `HostBridge.SuspendVm` to call
    //      `OnHostReplyWait()` — flags waiting + suspends.
    //   4. Calling `RunStartFromSource` / `RunStartFromBytecode` /
    //      `RunTestsStart` / `RunTick` / `StopRun` / `DepositResult*`
    //      from whatever JS-interop surface the host has.
    //   5. Driving `RunTick` repeatedly via the host's scheduler
    //      (setTimeout in the web template, requestAnimationFrame
    //      via Game1.Update in monogame, etc.).
    //
    // State is static — there's one VM running per host at a time.
    // The host is responsible for not nesting runs.
    public static class CooperativePump
    {
        // ─── Active run state ────────────────────────────────────────
        public static VirtualMachine RunVm { get; set; }
        private static string _runError;
        private static bool _waitingForHostReply;
        // Public so debug-session drivers can reset + read it the same
        // way RunTick does. WaitImpl writes via OnCooperativeWait;
        // the pump consumer clears at the start of each tick and
        // includes the post-tick value in its status so the JS pump
        // can schedule the next tick after the delay.
        public static int PendingWaitMs { get; set; }
        private static bool _runStopRequested;

        // ─── Cooperative test-runner state ───────────────────────────
        private static bool _testRunActive;
        private static FadeRuntimeContext _testCtx;
        private static List<TestManifestEntry> _testQueue;
        private static int _testIndex;
        private static List<FadeTestResult> _testResults;
        private static Stopwatch _testRunSw;
        private static Stopwatch _currentTestSw;

        // ─── Host wiring ─────────────────────────────────────────────
        public static Func<CommandCollection> CommandsAccessor { get; set; }
        private static CommandCollection GetCommands() =>
            CommandsAccessor?.Invoke() ?? throw new InvalidOperationException(
                "CooperativePump.CommandsAccessor not set — host must wire this before any compile-from-source op.");

        // Library commands call HostBridge.SuspendVm; the host wires
        // it to this method. Identical wiring on every host.
        public static void OnHostReplyWait()
        {
            _waitingForHostReply = true;
            RunVm?.Suspend();
        }

        // Library commands (StandardCommands.WaitImpl) call this when
        // `wait ms` fires during a cooperative run. Host wires its
        // WaitImpl to delegate here.
        public static void OnCooperativeWait(int ms)
        {
            PendingWaitMs = ms;
            _waitEndsAtTickMs = NowMs + ms;
            RunVm?.Suspend();
        }

        // Deadline (in ms, same epoch as NowMs) for the most recent
        // cooperative wait. Hosts that drive the VM per-frame (e.g.
        // Game1.Update under requestAnimationFrame) check IsBusyWaiting
        // before ticking — saves the setTimeout dance the Export.Web
        // pump uses, since rAF already provides per-frame cadence.
        private static long _waitEndsAtTickMs;
        private static long NowMs => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        // True when the pump shouldn't be advanced this frame: either
        // we're waiting on a host-reply (prompt$ etc.) or a wait-ms
        // deadline hasn't elapsed.
        public static bool IsBusyWaiting()
        {
            if (_waitingForHostReply) return true;
            if (NowMs < _waitEndsAtTickMs) return true;
            return false;
        }

        // ─── Run entry points (compile-from-source / bytecode) ──────
        // RunStart (entry DLL bytes) is host-specific and stays in the
        // host class — assembly loading varies per runtime context.

        public static string RunStartFromSource(string source)
        {
            try
            {
                var commands = GetCommands();
                if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
                    return new PumpStartResult { ok = false, compileError = errors.ToDisplay() }.Jsonify();
                RunVm = ctx.Machine;
                ResetPerRunState();
                return new PumpStartResult { ok = true }.Jsonify();
            }
            catch (Exception ex)
            {
                return new PumpStartResult { ok = false, error = DescribeException(ex) }.Jsonify();
            }
        }

        public static byte[] CompileToBytecode(string source)
        {
            try
            {
                CommandCollection commands;
                try { commands = GetCommands(); } catch { return Array.Empty<byte>(); }
                if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out _))
                    return Array.Empty<byte>();
                return ctx.Machine.program;
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        public static string CompileToBytecodeStatus(string source)
        {
            try
            {
                CommandCollection commands;
                try { commands = GetCommands(); }
                catch (Exception ex) { return new PumpStartResult { ok = false, error = ex.Message }.Jsonify(); }
                if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
                    return new PumpStartResult { ok = false, compileError = errors.ToDisplay() }.Jsonify();
                return new PumpStartResult { ok = true, byteCount = ctx.Machine.program.Length }.Jsonify();
            }
            catch (Exception ex)
            {
                return new PumpStartResult { ok = false, error = DescribeException(ex) }.Jsonify();
            }
        }

        public static string RunStartFromBytecode(byte[] bytecode)
        {
            try
            {
                if (bytecode == null || bytecode.Length == 0)
                    throw new Exception("RunStartFromBytecode: empty bytecode");
                var commands = GetCommands();
                RunVm = new VirtualMachine(bytecode)
                {
                    hostMethods = HostMethodTable.FromCommandCollection(commands),
                };
                ResetPerRunState();
                return new PumpStartResult { ok = true }.Jsonify();
            }
            catch (Exception ex)
            {
                return new PumpStartResult { ok = false, error = DescribeException(ex) }.Jsonify();
            }
        }

        // Called by the host's RunStart (which knows how to load the
        // entry DLL) after it has built the VM. Host hands us the VM,
        // we install it as the run-pump's current VM.
        public static void RunStartWithVm(VirtualMachine vm)
        {
            RunVm = vm;
            ResetPerRunState();
        }

        private static void ResetPerRunState()
        {
            _runError = null;
            _waitingForHostReply = false;
            PendingWaitMs = 0;
            _waitEndsAtTickMs = 0;
            _runStopRequested = false;
            _testRunActive = false;
        }

        // ─── Run tick ─────────────────────────────────────────────────
        public static string RunTick(int budget)
        {
            if (RunVm == null && _testRunActive)
                return BuildTestRunCompleteJson(stopped: false);

            if (RunVm == null)
                return new PumpTickResult { complete = true }.Jsonify();

            if (_runError != null && !_testRunActive)
                return new PumpTickResult { complete = true, error = _runError }.Jsonify();

            if (_runStopRequested)
            {
                _runStopRequested = false;
                RunVm = null;
                if (_testRunActive)
                    return BuildTestRunCompleteJson(stopped: true);
                return new PumpTickResult { complete = true, error = "stopped" }.Jsonify();
            }

            PendingWaitMs = 0;
            Exception testException = null;
            try
            {
                RunVm.Execute3(budget);
            }
            catch (Exception ex)
            {
                if (_testRunActive) testException = ex;
                else _runError = DescribeException(ex);
            }

            if (_testRunActive)
            {
                var vmFinished = RunVm.instructionIndex >= RunVm.program.Length
                    || testException != null;
                if (vmFinished && _testQueue != null && _testCtx != null
                    && _testIndex >= 0 && _testIndex < _testQueue.Count)
                {
                    _currentTestSw?.Stop();
                    var entry = _testQueue[_testIndex];
                    var result = FadeTestExecutor.BuildResultFromVm(
                        RunVm,
                        entry,
                        _currentTestSw?.Elapsed ?? TimeSpan.Zero,
                        _testCtx.Compiler.DebugData,
                        testException);
                    _testResults?.Add(result);
                    var progress = BuildTestResult(result);

                    if (!AdvanceTest())
                        return BuildTestRunCompleteJson(stopped: false, lastProgress: progress);

                    var nextName = _testQueue[_testIndex].name;
                    return new PumpTickResult
                    {
                        testProgress = progress,
                        testStarting = new PumpTestStarting { name = nextName },
                    }.Jsonify();
                }
            }

            var complete = _runError != null
                || RunVm.instructionIndex >= RunVm.program.Length;

            return new PumpTickResult
            {
                complete = complete,
                suspended = !complete && RunVm.isSuspendRequested,
                waitMs = PendingWaitMs,
                waitingForHostReply = _waitingForHostReply,
                error = _runError,
            }.Jsonify();
        }

        // ─── Stop ─────────────────────────────────────────────────────
        public static string StopRun()
        {
            _runStopRequested = true;
            _waitingForHostReply = false;
            PendingWaitMs = 0;
            RunVm?.Suspend();
            return "true";
        }

        // ─── Tests ────────────────────────────────────────────────────
        public static string RunTestsStart(string source, string testName)
        {
            _runStopRequested = false;
            _testRunActive = false;
            _testQueue = null;
            _testResults = null;
            _runError = null;

            try
            {
                var commands = GetCommands();
                if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
                    return new PumpStartResult { ok = false, compileError = errors.ToDisplay() }.Jsonify();

                var selectAll = string.IsNullOrWhiteSpace(testName);
                var queue = new List<TestManifestEntry>();
                foreach (var t in ctx.Compiler.TestManifest)
                {
                    if (t.isAbstract) continue;
                    if (!selectAll && !string.Equals(t.name, testName, StringComparison.OrdinalIgnoreCase)) continue;
                    queue.Add(t);
                    if (!selectAll) break;
                }

                _testCtx = ctx;
                _testQueue = queue;
                _testResults = new List<FadeTestResult>();
                _testIndex = -1;
                _testRunSw = Stopwatch.StartNew();
                _testRunActive = true;

                AdvanceTest();
                return new PumpStartResult { ok = true }.Jsonify();
            }
            catch (Exception ex)
            {
                return new PumpStartResult { ok = false, error = DescribeException(ex) }.Jsonify();
            }
        }

        private static bool AdvanceTest()
        {
            if (_testQueue == null || _testCtx == null) { RunVm = null; return false; }
            _testIndex++;
            if (_testIndex >= _testQueue.Count) { RunVm = null; return false; }
            var entry = _testQueue[_testIndex];
            var vm = new VirtualMachine(_testCtx.Machine.program, entry.entryPointAddress)
            {
                hostMethods = _testCtx.Compiler.methodTable,
                isTestExecution = true,
            };
            RunVm = vm;
            _runError = null;
            _waitingForHostReply = false;
            PendingWaitMs = 0;
            _currentTestSw = Stopwatch.StartNew();
            return true;
        }

        private static string BuildTestRunCompleteJson(bool stopped, PumpTestResult lastProgress = null)
        {
            _testRunActive = false;
            _testRunSw?.Stop();
            var results = _testResults ?? new List<FadeTestResult>();
            var passed = 0; var failed = 0;
            foreach (var r in results) { if (r.passed) passed++; else failed++; }
            return new PumpTickResult
            {
                complete = true,
                testProgress = lastProgress,
                testFinal = new PumpTestFinal
                {
                    passed = passed,
                    failed = failed,
                    duration = _testRunSw?.Elapsed.TotalMilliseconds ?? 0,
                    results = BuildTestResults(results),
                },
                error = stopped ? "Stopped" : null,
            }.Jsonify();
        }

        // ─── Deposit-result entry points ──────────────────────────────
        public static string DepositResultString(string value)
        {
            if (RunVm == null) return "false";
            if (!_waitingForHostReply) return "false";
            value ??= "";
            var vm = RunVm;
            if (vm.stack.ptr < 9) { _waitingForHostReply = false; return "false"; }
            _ = vm.stack.Pop();
            vm.stack.PopArraySpan(8, out var oldPtrSpan);
            var oldPtr = VmPtr.FromBytes(oldPtrSpan);
            vm.heap.TryDecrementRefCount(oldPtr);
            var size = value.Length * 4;
            var span = new byte[size];
            for (var i = 0; i < value.Length; i++)
            {
                var data = (uint)value[i];
                var b = BitConverter.GetBytes(data);
                span[i * 4 + 0] = b[0];
                span[i * 4 + 1] = b[1];
                span[i * 4 + 2] = b[2];
                span[i * 4 + 3] = b[3];
            }
            vm.heap.AllocateString(size, out var newPtr);
            vm.heap.WriteSpan(newPtr, size, span);
            var ptrBytes = VmPtr.GetBytes(ref newPtr);
            VmUtil.PushSpan(ref vm.stack, ptrBytes, TypeCodes.STRING);
            _waitingForHostReply = false;
            return "true";
        }

        private static bool BeginDeposit() => RunVm != null && _waitingForHostReply;
        private static string EndDeposit(bool ok)
        {
            _waitingForHostReply = false;
            return ok ? "true" : "false";
        }

        private static bool SwapPrimitiveTop(byte typeCode, byte[] newBytes)
        {
            if (RunVm == null) return false;
            if (newBytes == null) return false;
            var size = TypeCodes.GetByteSize(typeCode);
            if (newBytes.Length != size) return false;
            if (RunVm.stack.ptr < 1 + size) return false;
            RunVm.stack.ptr -= 1 + size;
            VmUtil.PushSpan(ref RunVm.stack, newBytes, typeCode);
            return true;
        }

        public static string DepositResultInt(int value) =>
            EndDeposit(BeginDeposit() && SwapPrimitiveTop(TypeCodes.INT, BitConverter.GetBytes(value)));
        public static string DepositResultReal(float value) =>
            EndDeposit(BeginDeposit() && SwapPrimitiveTop(TypeCodes.REAL, BitConverter.GetBytes(value)));
        public static string DepositResultBool(bool value) =>
            EndDeposit(BeginDeposit() && SwapPrimitiveTop(TypeCodes.BOOL, BitConverter.GetBytes(value)));
        public static string DepositResultByte(byte value) =>
            EndDeposit(BeginDeposit() && SwapPrimitiveTop(TypeCodes.BYTE, new[] { value }));
        public static string DepositResultWord(int value) =>
            EndDeposit(BeginDeposit() && SwapPrimitiveTop(TypeCodes.WORD, BitConverter.GetBytes((ushort)value)));
        public static string DepositResultDword(int value) =>
            EndDeposit(BeginDeposit() && SwapPrimitiveTop(TypeCodes.DWORD, BitConverter.GetBytes((uint)value)));
        public static string DepositResultDint(long value) =>
            EndDeposit(BeginDeposit() && SwapPrimitiveTop(TypeCodes.DINT, BitConverter.GetBytes(value)));
        public static string DepositResultDfloat(double value) =>
            EndDeposit(BeginDeposit() && SwapPrimitiveTop(TypeCodes.DFLOAT, BitConverter.GetBytes(value)));
        public static string DepositResultVoid() => EndDeposit(BeginDeposit());

        // ─── Result shaping ──────────────────────────────────────────
        public static string SerializeTestResult(FadeTestResult r) => BuildTestResult(r).Jsonify();

        private static PumpTestResult BuildTestResult(FadeTestResult r)
        {
            var frames = new List<PumpTestFrame>();
            if (r.failureFrames != null)
            {
                foreach (var f in r.failureFrames)
                {
                    frames.Add(new PumpTestFrame
                    {
                        functionName = f.functionName,
                        lineNumber = f.lineNumber,
                        charNumber = f.charNumber,
                        instructionIndex = f.instructionIndex,
                    });
                }
            }
            return new PumpTestResult
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

        private static List<PumpTestResult> BuildTestResults(List<FadeTestResult> results)
        {
            var list = new List<PumpTestResult>(results.Count);
            foreach (var r in results) list.Add(BuildTestResult(r));
            return list;
        }

        // ─── Diagnostics ─────────────────────────────────────────────
        private static string DescribeException(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
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
    }

    // ─── JSON result types ────────────────────────────────────────────────
    // Used only for pump → JS serialization via IJsonable.

    internal class PumpStartResult : IJsonable
    {
        public bool ok;
        public string error;
        public string compileError;
        public int byteCount;

        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField("ok", ref ok);
            op.IncludeField("error", ref error);
            op.IncludeField("compileError", ref compileError);
            op.IncludeField("byteCount", ref byteCount);
        }
    }

    internal class PumpTickResult : IJsonable
    {
        public bool complete;
        public bool suspended;
        public int waitMs;
        public bool waitingForHostReply;
        public string error;
        public PumpTestResult testProgress;
        public PumpTestStarting testStarting;
        public PumpTestFinal testFinal;

        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField("complete", ref complete);
            op.IncludeField("suspended", ref suspended);
            op.IncludeField("waitMs", ref waitMs);
            op.IncludeField("waitingForHostReply", ref waitingForHostReply);
            op.IncludeField("error", ref error);
            op.IncludeField("testProgress", ref testProgress);
            op.IncludeField("testStarting", ref testStarting);
            op.IncludeField("testFinal", ref testFinal);
        }
    }

    internal class PumpTestStarting : IJsonable
    {
        public string name;

        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField("name", ref name);
        }
    }

    internal class PumpTestResult : IJsonable
    {
        public string name;
        public bool passed;
        public double duration;
        public string failureMessage;
        public string failureReason;
        public string failureSourceText;
        public int failureInstructionIndex;
        public List<PumpTestFrame> failureFrames = new List<PumpTestFrame>();

        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField("name", ref name);
            op.IncludeField("passed", ref passed);
            op.IncludeField("duration", ref duration);
            op.IncludeField("failureMessage", ref failureMessage);
            op.IncludeField("failureReason", ref failureReason);
            op.IncludeField("failureSourceText", ref failureSourceText);
            op.IncludeField("failureInstructionIndex", ref failureInstructionIndex);
            op.IncludeField("failureFrames", ref failureFrames);
        }
    }

    internal class PumpTestFrame : IJsonable
    {
        public string functionName;
        public int lineNumber;
        public int charNumber;
        public int instructionIndex;

        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField("functionName", ref functionName);
            op.IncludeField("lineNumber", ref lineNumber);
            op.IncludeField("charNumber", ref charNumber);
            op.IncludeField("instructionIndex", ref instructionIndex);
        }
    }

    internal class PumpTestFinal : IJsonable
    {
        public int passed;
        public int failed;
        public double duration;
        public List<PumpTestResult> results = new List<PumpTestResult>();

        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField("passed", ref passed);
            op.IncludeField("failed", ref failed);
            op.IncludeField("duration", ref duration);
            op.IncludeField("results", ref results);
        }
    }
}
