using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FadeBasic.Launch;
using FadeBasic.Sdk;
using FadeBasic.Testing;
using FadeBasic.Virtual;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using VsTestResultMessage = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResultMessage;

namespace FadeBasic.TestAdapter
{
    /// <summary>
    /// VSTest TPv2 executor that runs Fade tests via the same
    /// <see cref="IFadeTestHost"/> the MTP framework uses. Both adapters
    /// share <see cref="FadeTestHostResolver"/> so a project-defined
    /// <c>[FadeTestHost]</c> class drives both <c>dotnet test</c> and IDE
    /// runs identically.
    /// </summary>
    [ExtensionUri(FadeTestConstants.ExecutorUriString)]
    public sealed class FadeTestExecutorAdapter : ITestExecutor
    {
        private CancellationTokenSource? _cts;

        /// <summary>
        /// Source-level run path. The IDE invokes this when the user hits
        /// "Run all tests in &lt;assembly&gt;." We rediscover then delegate
        /// to the <see cref="TestCase"/>-level overload so both code paths
        /// share execution logic.
        /// </summary>
        public void RunTests(
            IEnumerable<string>? sources,
            IRunContext? runContext,
            IFrameworkHandle? frameworkHandle)
        {
            if (sources == null || frameworkHandle == null) return;

            var collected = new List<TestCase>();
            var sink = new ListDiscoverySink(collected);
            new FadeTestDiscoverer().DiscoverTests(sources, runContext!, frameworkHandle, sink);
            RunTests(collected, runContext, frameworkHandle);
        }

        public void RunTests(
            IEnumerable<TestCase>? tests,
            IRunContext? runContext,
            IFrameworkHandle? frameworkHandle)
        {
            if (tests == null || frameworkHandle == null) return;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            // Group by source assembly so we initialize the host exactly once
            // per assembly, mirroring the MTP framework's session lifecycle.
            foreach (var group in tests.GroupBy(t => t.Source))
            {
                if (ct.IsCancellationRequested) break;

                if (!FadeTestLaunchableLoader.TryLoad(group.Key, frameworkHandle,
                        out var launchable))
                {
                    foreach (var skipped in group)
                    {
                        frameworkHandle.SendMessage(TestMessageLevel.Warning,
                            $"FadeBasic.TestAdapter: skipping {skipped.DisplayName} — could not load launchable from {Path.GetFileName(group.Key)}");
                    }
                    continue;
                }

                RunGroup(group, launchable, frameworkHandle, ct);
            }
        }

        public void Cancel() => _cts?.Cancel();

        // -- internals -----------------------------------------------------

        private static void RunGroup(
            IEnumerable<TestCase> tests,
            ITestLaunchable launchable,
            IFrameworkHandle handle,
            CancellationToken ct)
        {
            var host = FadeTestHostResolver.Resolve(explicitHost: null);
            var sessionContext = new FadeTestSessionContext(launchable, services: null);
            var hostMethods = HostMethodTable.FromCommandCollection(launchable.CommandCollection);

            // VSTest's executor contract is sync; the host APIs are async.
            // .GetAwaiter().GetResult() is safe here because:
            //  - We're on a vstest.console worker thread, never the IDE UI thread.
            //  - The tasks we await don't post back to a SynchronizationContext.
            try
            {
                host.InitializeAsync(sessionContext, ct).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                handle.SendMessage(TestMessageLevel.Error,
                    $"FadeBasic.TestAdapter: host.InitializeAsync threw: {ex.Message}");
                return;
            }

            try
            {
                try
                {
                    host.BeforeAllTestsAsync(sessionContext, ct).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    handle.SendMessage(TestMessageLevel.Warning,
                        $"FadeBasic.TestAdapter: host.BeforeAllTestsAsync threw: {ex.Message}");
                }

                foreach (var tc in tests)
                {
                    if (ct.IsCancellationRequested) break;
                    RunOne(tc, launchable, host, hostMethods, handle, ct);
                }

                try
                {
                    host.AfterAllTestsAsync(sessionContext, ct).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    handle.SendMessage(TestMessageLevel.Warning,
                        $"FadeBasic.TestAdapter: host.AfterAllTestsAsync threw: {ex.Message}");
                }
            }
            finally
            {
                try { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* swallow — disposal failure shouldn't fail the run */ }
            }
        }

        private static void RunOne(
            TestCase tc,
            ITestLaunchable launchable,
            IFadeTestHost host,
            HostMethodTable hostMethods,
            IFrameworkHandle handle,
            CancellationToken ct)
        {
            handle.RecordStart(tc);
            var entry = ResolveEntry(tc, launchable);
            if (entry == null)
            {
                var notFound = new TestResult(tc)
                {
                    Outcome = TestOutcome.NotFound,
                    ErrorMessage = "FadeBasic.TestAdapter: no matching test entry in launchable manifest"
                };
                handle.RecordResult(notFound);
                handle.RecordEnd(tc, TestOutcome.NotFound);
                return;
            }

            var runCtx = new FadeTestRunContext(launchable, entry, hostMethods);

            FadeTestResult result;
            var sw = Stopwatch.StartNew();
            // Redirect Console.Out/Error around the run so anything the test
            // prints (the standard library's `print` lands on Console.WriteLine,
            // see FadeBasicCommands.cs / FadeBasic.Lib.Standard.Console) ends up
            // as TestResultMessage.StandardOut/Error on the VSTest result —
            // which is what Rider's Unit Tests window renders in its Output pane.
            // Tests run sequentially in this adapter (per RunGroup), so a process-
            // wide redirect is safe; we still save/restore in case the test host
            // injects its own writers.
            var capturedOut = new StringWriter();
            var capturedErr = new StringWriter();
            var prevOut = Console.Out;
            var prevErr = Console.Error;
            Console.SetOut(capturedOut);
            Console.SetError(capturedErr);
            try
            {
                try
                {
                    result = host.RunTestAsync(runCtx, ct).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    result = new FadeTestResult
                    {
                        testName = entry.name,
                        passed = false,
                        failureMessage = "test cancelled"
                    };
                }
                catch (Exception ex)
                {
                    result = new FadeTestResult
                    {
                        testName = entry.name,
                        passed = false,
                        failureMessage = "test host threw: " + ex.Message
                    };
                }
            }
            finally
            {
                Console.SetOut(prevOut);
                Console.SetError(prevErr);
            }
            sw.Stop();

            var sourceFile = ResolveSourceFile(tc, entry);

            var vsResult = new TestResult(tc)
            {
                Outcome = result.passed ? TestOutcome.Passed : TestOutcome.Failed,
                Duration = sw.Elapsed,
            };

            var stdout = capturedOut.ToString();
            var stderr = capturedErr.ToString();
            if (stdout.Length > 0)
                vsResult.Messages.Add(new VsTestResultMessage(VsTestResultMessage.StandardOutCategory, stdout));
            if (stderr.Length > 0)
                vsResult.Messages.Add(new VsTestResultMessage(VsTestResultMessage.StandardErrorCategory, stderr));

            if (!result.passed)
            {
                vsResult.ErrorMessage = BuildErrorMessage(result, sourceFile, entry);
                vsResult.ErrorStackTrace = BuildErrorStackTrace(sourceFile, entry);
            }

            handle.RecordResult(vsResult);
            handle.RecordEnd(tc, vsResult.Outcome);
        }

        /// <summary>
        /// Look up the originating <see cref="TestManifestEntry"/> for a
        /// <see cref="TestCase"/>. Prefers the entry-point address (stable
        /// across abstract/concrete name collisions) and falls back to
        /// display-name match.
        /// </summary>
        internal static TestManifestEntry? ResolveEntry(TestCase tc, ITestLaunchable launchable)
        {
            var addr = tc.GetPropertyValue<int>(FadeTestCaseProperties.EntryPointAddress, defaultValue: -1);
            if (addr >= 0)
            {
                foreach (var e in launchable.TestManifest)
                {
                    if (e.entryPointAddress == addr && !e.isAbstract) return e;
                }
            }
            // Fallback by display name (last-resort; the address path should
            // always succeed for cases produced by our discoverer).
            foreach (var e in launchable.TestManifest)
            {
                if (!e.isAbstract && string.Equals(e.name, tc.DisplayName, StringComparison.Ordinal))
                    return e;
            }
            return null;
        }

        /// <summary>
        /// Pick the best <c>.fbasic</c> path to surface in failure messages
        /// and stack frames. Preference order:
        /// (1) the property the discoverer stamped on the <see cref="TestCase"/>,
        /// (2) <see cref="TestCase.CodeFilePath"/> (set by the discoverer when
        /// the path is known),
        /// (3) the manifest entry's <see cref="TestManifestEntry.sourceFilePath"/>
        /// (when the executor was reached without going through our discoverer
        /// — e.g., a synthetic <c>TestCase</c> filtered by a runsettings query).
        /// </summary>
        private static string ResolveSourceFile(TestCase tc, TestManifestEntry entry)
        {
            var stamped = tc.GetPropertyValue<string>(FadeTestCaseProperties.FbasicSourceFile, defaultValue: null!);
            if (!string.IsNullOrEmpty(stamped)) return stamped;
            if (!string.IsNullOrEmpty(tc.CodeFilePath)) return tc.CodeFilePath;
            return entry.sourceFilePath ?? string.Empty;
        }

        /// <summary>
        /// Format the failure message in a Fade-flavored shape. Surfaces the
        /// captured assertion source text and the originating <c>.fbasic</c>
        /// line so the Test Explorer "failure" pane reads as a Fade error,
        /// not a generic .NET exception dump.
        /// </summary>
        internal static string BuildErrorMessage(FadeTestResult r, string fbasicPath, TestManifestEntry entry)
        {
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(r.failureMessage) ? "test failed" : r.failureMessage);
            if (!string.IsNullOrEmpty(r.failureSourceText))
            {
                sb.Append("\n  source: ").Append(r.failureSourceText);
            }
            if (entry.sourceLine > 0 && !string.IsNullOrEmpty(fbasicPath))
            {
                sb.Append("\n  at ")
                  .Append(Path.GetFileName(fbasicPath))
                  .Append(':')
                  .Append(entry.sourceLine);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Synthesize a single stack-trace frame in the canonical <c>at &lt;name&gt;
        /// in &lt;file&gt;:line N</c> format. Both VS Code and Rider parse this
        /// regex and turn it into a clickable source link in the failure pane.
        /// </summary>
        internal static string BuildErrorStackTrace(string fbasicPath, TestManifestEntry entry)
        {
            if (entry.sourceLine <= 0 || string.IsNullOrEmpty(fbasicPath)) return string.Empty;
            return $"   at {entry.name} in {fbasicPath}:line {entry.sourceLine}";
        }

        // Tiny sink that captures discovered cases into a list for the
        // sources-overload of RunTests. Defined here (not as a separate file)
        // because it's purely an implementation detail of this executor.
        private sealed class ListDiscoverySink : ITestCaseDiscoverySink
        {
            private readonly List<TestCase> _list;
            public ListDiscoverySink(List<TestCase> list) { _list = list; }
            public void SendTestCase(TestCase discoveredTest) => _list.Add(discoveredTest);
        }
    }
}
