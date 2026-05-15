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
            return FadeTestExecutor.RunTest(Machine.program, Compiler.methodTable, entry);
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
