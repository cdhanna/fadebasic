using System;
using System.Collections;
using System.Collections.Generic;
using FadeBasic.Launch;
using FadeBasic.Virtual;

namespace FadeBasic.Compiled
{
    /// <summary>
    /// Base class for a Fade program compiled to C# by <see cref="FadeBasic.CSharp.CSharpTranspiler"/>.
    ///
    /// Fade code can suspend (the `sync` command calls <see cref="VirtualMachine.Suspend"/>) at any
    /// call depth. The transpiler turns every function that can reach a suspending command into a
    /// C# iterator; a call to one is `yield return Callee(...)`, and a suspension is
    /// `yield return null`. <see cref="Run"/> drives that tree of iterators with an explicit stack,
    /// so resuming costs the same no matter how deep the suspended call is.
    ///
    /// Tests: <see cref="StartTest"/> starts a test's launcher instead of the main program. A
    /// `runto` yields <see cref="RuntoRequest"/>; the driver then resumes the main program (kept
    /// across runtos, like the VM's programResumeIP) until it yields <see cref="RuntoHit"/> at the
    /// target label, and parks it again.
    /// </summary>
    public abstract class CompiledFadeProgram : IFadeRunner
    {
        public static readonly object RuntoRequest = new object();
        public static readonly object RuntoHit = new object();

        /// <summary>
        /// A stand-in VM handed to commands that take a <c>[FromVm] VirtualMachine</c>. It runs no
        /// bytecode. Commands may call <see cref="VirtualMachine.Suspend"/> on it, and the generated
        /// code sets <see cref="VirtualMachine.instructionIndex"/> to a unique call-site id before each
        /// such call, so commands that key UI state off the instruction index still get a stable id.
        /// </summary>
        public readonly VirtualMachine __vm;

        readonly Stack<IEnumerator> _stack = new Stack<IEnumerator>();

        // The main program while a test is not running it: its whole call chain (main, and any
        // routine or function it is inside), bottom first. Null until the first runto.
        List<IEnumerator> _parkedMain;
        int _runtoBase;
        bool _started;
        bool _testMode;
        bool _unwinding;
        FadeIntStack _runto = new FadeIntStack(4);

        /// <summary>The global scope's defer stack (shared by the main program and test bodies).</summary>
        protected FadeIntStack __gdefers = new FadeIntStack(4);

        /// <summary>Installed mocks: command id to mock body index. Null until a test mocks something.</summary>
        protected Dictionary<int, int> __mocks;

        Dictionary<int, int> _callCounts;

        public bool IsComplete { get; private set; }

        /// <summary>The first failed assert (or forbidden mock call) of a test run.</summary>
        public VirtualMachine.TestFailure AssertionFailure { get; private set; }

        public VirtualMachine HostVm => __vm;

        public string BackendName => "csharp";

        protected CompiledFadeProgram()
        {
            __vm = VirtualMachine.CreateHostContext();
        }

        protected abstract IEnumerator __Main();

        /// <summary>The launcher for a test (its `from` chain, then the global defers), or null.</summary>
        protected virtual IEnumerator __CreateTest(string name) => null;

        protected virtual void __DrainGlobalDefers()
        {
        }

        /// <summary>
        /// Starts the named test instead of the main program. Returns false when the program has
        /// no such test. Call before the first <see cref="Run"/>, on a fresh instance per test.
        /// </summary>
        public bool StartTest(string name)
        {
            if (_started) throw new InvalidOperationException("the program has already started");
            var launcher = __CreateTest(name);
            if (launcher == null) return false;
            _started = true;
            _testMode = true;
            __vm.isTestExecution = true;
            _stack.Push(launcher);
            return true;
        }

        /// <summary>
        /// Runs the program until it suspends (returns true) or finishes (returns false).
        /// Mirrors the host loop `while (!vm.isSuspendRequested &amp;&amp; !done) vm.Execute2()`.
        /// Runtime errors propagate, as they do from the VM.
        /// </summary>
        public bool Run()
        {
            if (IsComplete) return false;

            __vm.isSuspendRequested = false;
            try
            {
                if (!_started)
                {
                    _started = true;
                    _stack.Push(__Main());
                }

                while (_stack.Count > 0)
                {
                    var top = _stack.Peek();
                    if (!top.MoveNext())
                    {
                        _stack.Pop();
                        continue;
                    }

                    var current = top.Current;
                    if (current is IEnumerator child)
                    {
                        _stack.Push(child);
                        continue;
                    }
                    if (current == RuntoRequest)
                    {
                        // resume the main program where it was parked (or start it)
                        _runtoBase = _stack.Count;
                        if (_parkedMain == null)
                        {
                            _stack.Push(__Main());
                        }
                        else
                        {
                            foreach (var frame in _parkedMain) _stack.Push(frame);
                            _parkedMain = null;
                        }
                        continue;
                    }
                    if (current == RuntoHit)
                    {
                        // park the main program, including the routine the label is in, until the next runto
                        var parked = new List<IEnumerator>();
                        while (_stack.Count > _runtoBase) parked.Add(_stack.Pop());
                        parked.Reverse();
                        _parkedMain = parked;
                        continue;
                    }

                    return true; // suspended
                }
            }
            catch (FadeEndProgramException)
            {
                _stack.Clear();
            }
            catch (FadeAssertException)
            {
                Unwind();
            }
            catch (VirtualRuntimeException)
            {
                IsComplete = true;
                throw;
            }
            catch (Exception ex)
            {
                // the same contract as the VM: anything that escapes a statement is a runtime error
                IsComplete = true;
                throw new CompiledFadeRuntimeException(VirtualRuntimeErrorType.INTERNAL_ERROR, ex.Message, -1);
            }

            IsComplete = true;
            return false;
        }

        /// <summary>Runs the program to completion, ignoring suspensions.</summary>
        public void RunToCompletion()
        {
            while (Run())
            {
            }
        }

        /// <summary>
        /// The VM's assert trampoline: drain the defers of every live scope, innermost first, then
        /// the global scope's. Suspended iterators run their pending defers when disposed (their
        /// bodies sit in try/finally); synchronous frames already did on the way out. A second
        /// failure while draining halts on the spot, keeping the first.
        /// </summary>
        void Unwind()
        {
            try
            {
                while (_stack.Count > 0)
                {
                    (_stack.Pop() as IDisposable)?.Dispose();
                }
                __DrainGlobalDefers();
            }
            catch (FadeAssertException)
            {
            }
            catch (FadeEndProgramException)
            {
            }
            _stack.Clear();
        }

        // ---- used by generated code ---------------------------------------------------------

        protected bool __IsUnwinding => _unwinding;

        protected void __AssertFailed(string reason, string sourceText, int sourceLine)
        {
            if (!_testMode)
            {
                var message = string.IsNullOrEmpty(reason)
                    ? $"assert failed: {sourceText}"
                    : $"assert failed: {sourceText} — {reason}";
                throw new CompiledFadeRuntimeException(VirtualRuntimeErrorType.ASSERT_FAILED, message, sourceLine);
            }

            if (AssertionFailure == null)
            {
                AssertionFailure = new VirtualMachine.TestFailure
                {
                    sourceText = sourceText,
                    reason = reason ?? "",
                    instructionIndex = -1,
                    sourceLine = sourceLine
                };
            }
            _unwinding = true;
            throw FadeAssertException.Instance;
        }

        protected void __RuntoPush(int target) => _runto.Push(target);

        protected bool __RuntoReached(int target)
        {
            if (_runto.Count == 0 || _runto.Peek() != target) return false;
            _runto.Pop();
            return true;
        }

        protected void __InstallMock(int commandId, int mockIndex)
        {
            if (__mocks == null) __mocks = new Dictionary<int, int>();
            __mocks[commandId] = mockIndex;
        }

        protected void __ClearMock(int commandId) => __mocks?.Remove(commandId);

        protected void __ClearMocks() => __mocks?.Clear();

        protected void __CountCall(int commandId)
        {
            if (!_testMode) return;
            if (_callCounts == null) _callCounts = new Dictionary<int, int>();
            _callCounts.TryGetValue(commandId, out var count);
            _callCounts[commandId] = count + 1;
        }

        protected int __CallCount(int commandId)
        {
            if (_callCounts == null) return 0;
            _callCounts.TryGetValue(commandId, out var count);
            return count;
        }
    }

    /// <summary>A failed assert inside a test: unwinds to the driver, which drains the defers.</summary>
    public sealed class FadeAssertException : Exception
    {
        public static readonly FadeAssertException Instance = new FadeAssertException();
        FadeAssertException() : base("fade test assertion failed") { }
    }
}
