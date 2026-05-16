using System;
using System.Collections.Generic;
using System.Diagnostics;
using FadeBasic.Launch;
using FadeBasic.Virtual;

namespace FadeBasic.Sdk
{
    public static class FadeTestExecutor
    {
        // Run a single test entry against pre-built bytecode + host method table.
        // Used both by the SDK (FadeRuntimeContext.RunTest) and the console-app
        // launcher when handling `--fade-test=name`. Each call gets a fresh VM,
        // so test state is isolated.
        public static FadeTestResult RunTest(
            byte[] bytecode,
            HostMethodTable hostMethods,
            TestManifestEntry entry)
        {
            return RunTest(bytecode, hostMethods, entry, debugData: null);
        }

        // DebugData-aware overload: when supplied, the failure result includes
        // source-located stack frames built from the VM's methodStack snapshot
        // at the moment of failure. Call this overload from any caller that
        // has the program's DebugData (e.g., ILaunchable.DebugData).
        public static FadeTestResult RunTest(
            byte[] bytecode,
            HostMethodTable hostMethods,
            TestManifestEntry entry,
            DebugData debugData)
        {
            if (entry.isAbstract)
            {
                return new FadeTestResult
                {
                    testName = entry.name,
                    passed = false,
                    failureMessage = $"Test `{entry.name}` is abstract and cannot be run directly."
                };
            }

            var sw = Stopwatch.StartNew();
            var vm = new VirtualMachine(bytecode, entry.entryPointAddress)
            {
                hostMethods = hostMethods,
                // Test-mode: a failed assert (here or in main-program code reached
                // via `runto`) records a TestFailure instead of throwing.
                isTestExecution = true
            };
            try
            {
                vm.Execute3(0); // infinite budget!
            }
            catch (VirtualRuntimeException rex)
            {
                // The VM threw a structured runtime error. Resolve the
                // call-stack snapshot it carries into source-located frames
                // when DebugData is available, so the failure pane shows
                // where the crash actually happened (not just "VM threw").
                sw.Stop();
                return new FadeTestResult
                {
                    testName = entry.name,
                    passed = false,
                    failureMessage = "VM threw: " + rex.Message,
                    failureInstructionIndex = rex.Error.insIndex,
                    failureFrames = BuildFrames(rex.Error.insIndex, rex.Error.callStack, debugData),
                    duration = sw.Elapsed
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new FadeTestResult
                {
                    testName = entry.name,
                    passed = false,
                    failureMessage = "VM threw: " + ex.Message,
                    duration = sw.Elapsed
                };
            }
            sw.Stop();

            if (vm.assertionFailure != null)
            {
                var reason = vm.assertionFailure.reason;
                var hasReason = !string.IsNullOrEmpty(reason);
                var msg = hasReason
                    ? $"assert failed: {vm.assertionFailure.sourceText} — {reason}"
                    : $"assert failed: {vm.assertionFailure.sourceText}";
                return new FadeTestResult
                {
                    testName = entry.name,
                    passed = false,
                    failureMessage = msg,
                    failureSourceText = vm.assertionFailure.sourceText,
                    failureReason = reason,
                    failureInstructionIndex = vm.assertionFailure.instructionIndex,
                    failureFrames = BuildFrames(
                        vm.assertionFailure.instructionIndex,
                        vm.assertionFailure.callStack,
                        debugData),
                    duration = sw.Elapsed
                };
            }

            return new FadeTestResult
            {
                testName = entry.name,
                passed = true,
                duration = sw.Elapsed
            };
        }

        // Resolve a VM call-stack snapshot into source-located frames using
        // DebugData. Returns an empty list when DebugData is null (best-effort:
        // callers fall back to entry.sourceLine in that case).
        //
        // Generic over the error source: both TestFailure (assert in test mode)
        // and VirtualRuntimeError (any runtime crash) carry the same shape
        // (instructionIndex + callStack), so this helper takes the two raw
        // pieces rather than either struct.
        //
        // Walk strategy mirrors DebugSession.GetFrames2:
        //   1. The "innermost" frame's source location is the IP at failure.
        //   2. For each entry in methodStack (top-down), the function name
        //      comes from insToFunction[toIns], and the NEXT frame's source
        //      location comes from the call site (fromIns - 1).
        public static List<FadeStackFrame> BuildFrames(
            int instructionIndex,
            JumpHistoryData[] callStack,
            DebugData debugData)
        {
            var frames = new List<FadeStackFrame>();
            if (debugData == null) return frames;
            callStack = callStack ?? System.Array.Empty<JumpHistoryData>();

            var indexMap = new IndexCollection(debugData.statementTokens);

            // Start with the failure site itself.
            if (!indexMap.TryFindClosestTokenBeforeIndex(instructionIndex, out var currentToken))
            {
                return frames;
            }

            // Walk the snapshotted methodStack. callStack[0] is innermost.
            for (var i = 0; i < callStack.Length; i++)
            {
                var frame = callStack[i];
                var functionName = "<unknown>";
                if (debugData.insToFunction.TryGetValue(frame.toIns, out var fnToken))
                {
                    functionName = fnToken.token?.raw ?? functionName;
                }
                frames.Add(new FadeStackFrame
                {
                    functionName = functionName,
                    lineNumber = currentToken.token.lineNumber,
                    charNumber = currentToken.token.charNumber,
                    instructionIndex = instructionIndex
                });
                // Resolve the next frame's location to the call site of this
                // frame (fromIns - 1, matching DebugSession.GetFrames2).
                if (!indexMap.TryFindClosestTokenBeforeIndex(frame.fromIns - 1, out currentToken))
                {
                    return frames;
                }
            }

            // Outermost frame: code that wasn't inside any function call —
            // either the test body itself or main-program code reached via
            // runto. Function name is left empty; consumers can substitute
            // their own label (e.g., the test name).
            frames.Add(new FadeStackFrame
            {
                functionName = string.Empty,
                lineNumber = currentToken.token.lineNumber,
                charNumber = currentToken.token.charNumber,
                instructionIndex = callStack.Length > 0
                    ? callStack[callStack.Length - 1].fromIns - 1
                    : instructionIndex
            });
            return frames;
        }
    }

    /// <summary>
    /// A single source-located frame in an assertion-failure stack trace.
    /// Built from the VM's methodStack snapshot + DebugData by the test runner.
    /// </summary>
    public class FadeStackFrame
    {
        // Name of the function the frame is inside, or "" for the outermost
        // (test body / main-program) frame.
        public string functionName;
        // Source line in the same coordinate space the rest of the compiler
        // uses (0-based, as emitted by the lexer). Consumers that need to
        // display 1-based line numbers should add 1. Source-map resolution
        // for multi-file projects happens upstream of the runner.
        public int lineNumber;
        public int charNumber;
        public int instructionIndex;
    }

    public class FadeTestResult
    {
        public string testName;
        public bool passed;
        // Null when passed.
        public string failureMessage;
        // Captured assertion text from the failing `assert` (when an assert tripped).
        public string failureSourceText;
        // Optional reason string supplied via `assert <cond>, "<reason>"`. Null
        // or empty when the user didn't provide one.
        public string failureReason;
        // IP at the moment of failure; useful for source-mapping when DebugData
        // is available. -1 if not applicable.
        public int failureInstructionIndex = -1;
        // Source-located stack frames at the moment of failure (innermost first,
        // outermost last). Empty when DebugData wasn't available at run time;
        // callers should fall back to entry.sourceLine in that case.
        public List<FadeStackFrame> failureFrames = new List<FadeStackFrame>();
        public TimeSpan duration;
    }

    public class FadeTestRunResult
    {
        public List<FadeTestResult> tests = new List<FadeTestResult>();
        public int passedCount;
        public int failedCount;
        public bool AllPassed => failedCount == 0 && tests.Count > 0;
        public TimeSpan duration;
    }

    public partial class FadeRuntimeContext
    {
        // Concrete tests (skips abstract fixtures).
        public IEnumerable<TestManifestEntry> Tests
        {
            get
            {
                foreach (var t in Compiler.TestManifest)
                {
                    if (!t.isAbstract) yield return t;
                }
            }
        }

        public FadeTestResult RunTest(string testName)
        {
            foreach (var t in Compiler.TestManifest)
            {
                if (string.Equals(t.name, testName, StringComparison.OrdinalIgnoreCase))
                {
                    if (t.isAbstract)
                    {
                        return new FadeTestResult
                        {
                            testName = testName,
                            passed = false,
                            failureMessage = $"Test `{testName}` is abstract and cannot be run directly."
                        };
                    }
                    return RunTest(t);
                }
            }
            return new FadeTestResult
            {
                testName = testName,
                passed = false,
                failureMessage = $"No test named `{testName}` was found in the program."
            };
        }

        public FadeTestResult RunTest(TestManifestEntry entry)
        {
            return FadeTestExecutor.RunTest(
                Machine.program,
                Compiler.methodTable,
                entry,
                Compiler.DebugData);
        }

        public FadeTestRunResult RunAllTests()
        {
            var run = new FadeTestRunResult();
            var sw = Stopwatch.StartNew();
            foreach (var t in Compiler.TestManifest)
            {
                if (t.isAbstract) continue;
                var r = RunTest(t);
                run.tests.Add(r);
                if (r.passed) run.passedCount++;
                else run.failedCount++;
            }
            sw.Stop();
            run.duration = sw.Elapsed;
            return run;
        }
    }
}
