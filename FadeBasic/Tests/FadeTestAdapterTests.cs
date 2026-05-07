using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using FadeBasic.Launch;
using FadeBasic.TestAdapter;
using FadeBasic.Sdk;
using FadeBasic.Testing;
using FadeBasic.Virtual;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using NUnit.Framework;

namespace Tests;

/// <summary>
/// Unit tests for the VSTest adapter (Stage 11H — see TEST_ADAPTER.md).
/// We exercise the discoverer's internal entry-point and the executor's
/// helper methods directly, without invoking the VSTest pipeline. Integration
/// tests that drive vstest.console end-to-end live in a separate fixture
/// (deferred — see TEST_ADAPTER.md "Tests" section).
/// </summary>
[TestFixture]
public class FadeTestAdapterTests
{
    // ---- Discoverer ---------------------------------------------------

    [Test]
    public void Discoverer_FindsConcreteTests_SkipsAbstract()
    {
        var launchable = new ManifestLaunchable(new[]
        {
            new TestManifestEntry { name = "alpha",  entryPointAddress = 100, sourceLine = 5,  sourceFilePath = "/proj/main.fbasic" },
            new TestManifestEntry { name = "parent", entryPointAddress = 200, isAbstract = true },
            new TestManifestEntry { name = "beta",   entryPointAddress = 300, sourceLine = 12, sourceFilePath = "/proj/main.fbasic", fromParent = "parent" },
        });

        var cases = FadeTestDiscoverer.EnumerateTestCases("/proj/MyApp.dll", launchable).ToList();

        Assert.That(cases.Count, Is.EqualTo(2),
            "exactly the two concrete entries should surface; abstract entries are not run");
        var names = cases.Select(c => c.DisplayName).ToList();
        Assert.That(names, Is.EquivalentTo(new[] { "alpha", "beta" }));
    }

    [Test]
    public void Discoverer_PopulatesFadeFlavoredFields()
    {
        var launchable = new ManifestLaunchable(new[]
        {
            new TestManifestEntry { name = "wraps_at_right_edge", entryPointAddress = 42, sourceLine = 17, sourceFilePath = "/proj/fish.fbasic" },
        });

        var tc = FadeTestDiscoverer.EnumerateTestCases("/proj/MyApp.dll", launchable).Single();

        Assert.That(tc.DisplayName, Is.EqualTo("wraps_at_right_edge"));
        // FQN aligns with ManagedType.ManagedMethod — see Discoverer_ManagedType_BuildsFadeBasenamePath.
        Assert.That(tc.FullyQualifiedName, Is.EqualTo("Fade.fish.wraps_at_right_edge"));
        Assert.That(tc.ExecutorUri.ToString(), Is.EqualTo(FadeTestConstants.ExecutorUriString));
        Assert.That(tc.Source, Is.EqualTo("/proj/MyApp.dll"));
        Assert.That(tc.CodeFilePath, Is.EqualTo("/proj/fish.fbasic"),
            "double-clicking the test should jump to the .fbasic file, not the assembly");
        Assert.That(tc.LineNumber, Is.EqualTo(17));
    }

    [Test]
    public void Discoverer_TagsEveryCaseWithCategoryFade()
    {
        var launchable = new ManifestLaunchable(new[]
        {
            new TestManifestEntry { name = "a", entryPointAddress = 1, sourceFilePath = "/proj/x.fbasic" },
            new TestManifestEntry { name = "b", entryPointAddress = 2, sourceFilePath = "/proj/x.fbasic" },
        });

        var cases = FadeTestDiscoverer.EnumerateTestCases("/proj/MyApp.dll", launchable).ToList();

        foreach (var tc in cases)
        {
            Assert.That(tc.Traits.Any(t => t.Name == "Category" && t.Value == "Fade"),
                Is.True,
                $"case {tc.DisplayName} is missing the Category=Fade trait");
        }
    }

    [Test]
    public void Discoverer_StampsEntryPointAddressOnTestCase()
    {
        // The executor uses this to look up the matching manifest entry
        // without re-walking the launchable. Verifying it round-trips.
        var launchable = new ManifestLaunchable(new[]
        {
            new TestManifestEntry { name = "go", entryPointAddress = 9999, sourceFilePath = "/proj/x.fbasic" },
        });

        var tc = FadeTestDiscoverer.EnumerateTestCases("/proj/MyApp.dll", launchable).Single();

        var addr = tc.GetPropertyValue<int>(FadeTestCaseProperties.EntryPointAddress, defaultValue: -1);
        Assert.That(addr, Is.EqualTo(9999));
    }

    [Test]
    public void Discoverer_FromParent_BecomesTrait()
    {
        var launchable = new ManifestLaunchable(new[]
        {
            new TestManifestEntry { name = "child", entryPointAddress = 1, fromParent = "fixture", sourceFilePath = "/proj/x.fbasic" },
        });

        var tc = FadeTestDiscoverer.EnumerateTestCases("/proj/MyApp.dll", launchable).Single();

        Assert.That(tc.Traits.Any(t => t.Name == "FromParent" && t.Value == "fixture"), Is.True);
        var fromParent = tc.GetPropertyValue<string>(FadeTestCaseProperties.FromParent, defaultValue: null!);
        Assert.That(fromParent, Is.EqualTo("fixture"));
    }

    [Test]
    public void Discoverer_OmitsCodeFilePath_WhenSourceUnknown()
    {
        // No source path on the manifest entry → don't guess; better to omit
        // than send a path the IDE will fail to open. (This is the case for
        // single-string SDK callers that didn't supply a SourceMap.)
        var launchable = new ManifestLaunchable(new[]
        {
            new TestManifestEntry { name = "x", entryPointAddress = 1, sourceLine = 3 },
        });

        var tc = FadeTestDiscoverer.EnumerateTestCases("/proj/MyApp.dll", launchable).Single();

        Assert.That(tc.CodeFilePath, Is.Null.Or.Empty);
        Assert.That(tc.LineNumber, Is.EqualTo(3),
            "LineNumber alone is still useful — Test Explorer shows it in the details pane");
    }

    [Test]
    public void Discoverer_ManagedType_BuildsFadeBasenamePath()
    {
        // ManagedType + ManagedMethod build the test tree in IDE Test
        // Explorers / `dotnet test` structured output. Format is
        // "Fade.<sanitized .fbasic basename>"; the test name becomes
        // ManagedMethod. Tooling that falls back to parsing FullyQualifiedName
        // gets the same grouping because FQN = ManagedType + "." + ManagedMethod.
        var launchable = new ManifestLaunchable(new[]
        {
            new TestManifestEntry { name = "wraps_at_right_edge", entryPointAddress = 1, sourceFilePath = "/proj/fish.fbasic", sourceLine = 5 },
        });

        var tc = FadeTestDiscoverer.EnumerateTestCases("/proj/MyApp.dll", launchable).Single();

        var managedType = tc.GetPropertyValue<string>(FadeTestCaseProperties.ManagedType, defaultValue: null!);
        var managedMethod = tc.GetPropertyValue<string>(FadeTestCaseProperties.ManagedMethod, defaultValue: null!);

        Assert.That(managedType, Is.EqualTo("Fade.fish"),
            "ManagedType groups tests by their .fbasic source file under a `Fade` namespace");
        Assert.That(managedMethod, Is.EqualTo("wraps_at_right_edge"),
            "ManagedMethod is the test name verbatim");
        Assert.That(tc.FullyQualifiedName, Is.EqualTo("Fade.fish.wraps_at_right_edge"));
    }

    [Test]
    public void Discoverer_ManagedType_SanitizesNonIdentifierCharacters()
    {
        // .fbasic basenames can include dashes, dots in names, etc. — invalid
        // in dotted-identifier paths. We sanitize to [A-Za-z0-9_].
        var launchable = new ManifestLaunchable(new[]
        {
            new TestManifestEntry { name = "t", entryPointAddress = 1, sourceFilePath = "/proj/my-game.fbasic" },
        });

        var tc = FadeTestDiscoverer.EnumerateTestCases("/proj/MyApp.dll", launchable).Single();

        var managedType = tc.GetPropertyValue<string>(FadeTestCaseProperties.ManagedType, defaultValue: null!);
        Assert.That(managedType, Is.EqualTo("Fade.my_game"));
    }

    [Test]
    public void Discoverer_ManagedType_FallsBackToAssemblyName_WhenNoSourceFile()
    {
        // Single-string SDK callers won't have sourceFilePath populated.
        // Fall back to the assembly basename so the tree still groups
        // sensibly.
        var launchable = new ManifestLaunchable(new[]
        {
            new TestManifestEntry { name = "t", entryPointAddress = 1 },
        });

        var tc = FadeTestDiscoverer.EnumerateTestCases("/proj/CoolApp.dll", launchable).Single();

        var managedType = tc.GetPropertyValue<string>(FadeTestCaseProperties.ManagedType, defaultValue: null!);
        Assert.That(managedType, Is.EqualTo("Fade.CoolApp"));
    }

    [Test]
    public void Discoverer_ToManagedIdentifier_CoercesEmptyToTests()
    {
        Assert.That(FadeTestDiscoverer.ToManagedIdentifier(""), Is.EqualTo("Tests"));
        Assert.That(FadeTestDiscoverer.ToManagedIdentifier(null!), Is.EqualTo("Tests"));
        Assert.That(FadeTestDiscoverer.ToManagedIdentifier("9digit"), Does.StartWith("_"),
            "C# identifiers cannot start with a digit");
    }

    [Test]
    public void Discoverer_MultipleFiles_EachTestKeepsItsOwnPath()
    {
        // Multi-`.fbasic` projects: each entry's sourceFilePath drives its
        // CodeFilePath. This is the whole point of the per-entry plumbing.
        var launchable = new ManifestLaunchable(new[]
        {
            new TestManifestEntry { name = "a", entryPointAddress = 1, sourceFilePath = "/proj/foo.fbasic", sourceLine = 5 },
            new TestManifestEntry { name = "b", entryPointAddress = 2, sourceFilePath = "/proj/bar.fbasic", sourceLine = 10 },
        });

        var cases = FadeTestDiscoverer.EnumerateTestCases("/proj/MyApp.dll", launchable).ToList();

        var a = cases.First(c => c.DisplayName == "a");
        var b = cases.First(c => c.DisplayName == "b");
        Assert.That(a.CodeFilePath, Is.EqualTo("/proj/foo.fbasic"));
        Assert.That(b.CodeFilePath, Is.EqualTo("/proj/bar.fbasic"));
    }

    // ---- Executor: ResolveEntry --------------------------------------

    [Test]
    public void Executor_ResolveEntry_PrefersAddressOverDisplayName()
    {
        var launchable = new ManifestLaunchable(new[]
        {
            new TestManifestEntry { name = "duplicate_name", entryPointAddress = 100, isAbstract = true },
            new TestManifestEntry { name = "duplicate_name", entryPointAddress = 200 },
        });

        var tc = new TestCase("Fade.x.duplicate_name", FadeTestConstants.ExecutorUri, "/proj/MyApp.dll");
        tc.SetPropertyValue(FadeTestCaseProperties.EntryPointAddress, 200);

        var resolved = FadeTestExecutorAdapter.ResolveEntry(tc, launchable);
        Assert.That(resolved, Is.Not.Null);
        Assert.That(resolved!.entryPointAddress, Is.EqualTo(200),
            "name collisions are resolved by entry-point address, not by name");
    }

    [Test]
    public void Executor_ResolveEntry_FallsBackToDisplayName_WhenNoAddress()
    {
        var launchable = new ManifestLaunchable(new[]
        {
            new TestManifestEntry { name = "named", entryPointAddress = 50 },
        });
        var tc = new TestCase("Fade.x.named", FadeTestConstants.ExecutorUri, "/proj/MyApp.dll")
        {
            DisplayName = "named"
        };
        // Deliberately do NOT set EntryPointAddress — exercise the fallback.

        var resolved = FadeTestExecutorAdapter.ResolveEntry(tc, launchable);
        Assert.That(resolved, Is.Not.Null);
        Assert.That(resolved!.name, Is.EqualTo("named"));
    }

    [Test]
    public void Executor_ResolveEntry_ReturnsNull_WhenUnresolvable()
    {
        var launchable = new ManifestLaunchable(new[]
        {
            new TestManifestEntry { name = "exists", entryPointAddress = 1 },
        });
        var tc = new TestCase("Fade.x.missing", FadeTestConstants.ExecutorUri, "/proj/MyApp.dll")
        {
            DisplayName = "missing"
        };

        var resolved = FadeTestExecutorAdapter.ResolveEntry(tc, launchable);
        Assert.That(resolved, Is.Null);
    }

    // ---- Executor: failure formatting --------------------------------

    [Test]
    public void Executor_BuildErrorMessage_IncludesFbasicSourceAndLine()
    {
        // Failure pane should read as a Fade error, not a generic dump.
        var entry = new TestManifestEntry { name = "wraps", sourceLine = 42 };
        var result = new FadeTestResult
        {
            passed = false,
            failureMessage = "x is 0",
            failureSourceText = "assert x = 1",
        };
        var msg = FadeTestExecutorAdapter.BuildErrorMessage(result, "/proj/fish.fbasic", entry);

        Assert.That(msg, Does.Contain("x is 0"));
        Assert.That(msg, Does.Contain("source: assert x = 1"));
        Assert.That(msg, Does.Contain("at fish.fbasic:42"));
    }

    [Test]
    public void Executor_BuildErrorMessage_GracefulWhenNoSourceText()
    {
        var entry = new TestManifestEntry { name = "x", sourceLine = 7 };
        var result = new FadeTestResult { passed = false, failureMessage = "vm boom" };
        var msg = FadeTestExecutorAdapter.BuildErrorMessage(result, "/p/main.fbasic", entry);

        Assert.That(msg, Does.Contain("vm boom"));
        Assert.That(msg, Does.Not.Contain("source:"),
            "the source: line should be omitted when failureSourceText is empty");
    }

    [Test]
    public void Executor_BuildErrorStackTrace_ProducesClickableFormat()
    {
        // The exact format ("at NAME in FILE:line N") is the contract that
        // both Rider and VS Code parse to make stack lines clickable.
        var entry = new TestManifestEntry { name = "wraps_at_right_edge", sourceLine = 42 };
        var stack = FadeTestExecutorAdapter.BuildErrorStackTrace("/proj/fish.fbasic", entry);

        Assert.That(stack, Does.Match(@"\s+at wraps_at_right_edge in /proj/fish\.fbasic:line 42"));
    }

    [Test]
    public void Executor_BuildErrorStackTrace_EmptyWhenSourceUnknown()
    {
        // Without source info we can't synthesize a useful frame; emit
        // empty rather than a half-frame the IDE will mis-parse.
        var entry = new TestManifestEntry { name = "x", sourceLine = 0 };
        var stack = FadeTestExecutorAdapter.BuildErrorStackTrace("/p/main.fbasic", entry);
        Assert.That(stack, Is.Empty);

        var entry2 = new TestManifestEntry { name = "x", sourceLine = 5 };
        var stack2 = FadeTestExecutorAdapter.BuildErrorStackTrace(string.Empty, entry2);
        Assert.That(stack2, Is.Empty);
    }

    // ---- Executor: stdout/stderr capture ------------------------------

    [Test]
    public void Executor_CapturesStdout_FromTestRun_AsTestResultMessage()
    {
        // The Fade standard library's `print` lands on Console.WriteLine
        // (FadeBasicCommands.cs); the adapter redirects Console.Out around
        // the run so the IDE's test details pane gets the output. Verifying
        // the pipe end-to-end with a fake host that prints during its run.
        var entry = new TestManifestEntry { name = "prints", entryPointAddress = 1, sourceFilePath = "/p/x.fbasic", sourceLine = 5 };
        var launchable = new ManifestLaunchable(new[] { entry });
        var host = new PrintingHost(stdoutText: "hello from test", stderrText: "warning text");

        var captured = RunOneAndCapture(launchable, entry, host);

        var stdout = captured.Messages.SingleOrDefault(m => m.Category == TestResultMessage.StandardOutCategory);
        Assert.That(stdout, Is.Not.Null, "stdout message should be attached when the test prints");
        Assert.That(stdout!.Text, Does.Contain("hello from test"));

        var stderr = captured.Messages.SingleOrDefault(m => m.Category == TestResultMessage.StandardErrorCategory);
        Assert.That(stderr, Is.Not.Null);
        Assert.That(stderr!.Text, Does.Contain("warning text"));
    }

    [Test]
    public void Executor_OmitsMessages_WhenTestPrintsNothing()
    {
        // Defensive: don't pollute the result with empty StandardOut entries.
        // Some IDEs render a blank "Output" tab for any non-null message,
        // so emitting nothing is the right behavior.
        var entry = new TestManifestEntry { name = "silent", entryPointAddress = 1, sourceFilePath = "/p/x.fbasic", sourceLine = 1 };
        var launchable = new ManifestLaunchable(new[] { entry });
        var host = new PrintingHost(stdoutText: "", stderrText: "");

        var captured = RunOneAndCapture(launchable, entry, host);

        Assert.That(captured.Messages, Is.Empty,
            "no stdout/stderr captured → no message entries on the result");
    }

    private static TestResult RunOneAndCapture(
        ManifestLaunchable launchable,
        TestManifestEntry entry,
        IFadeTestHost host)
    {
        // Use a path that exists so the loader's GetFullPath/cache lookup
        // resolves stably. The actual file content is never read because
        // we pre-register the in-memory launchable on the loader cache.
        var assemblyPath = System.IO.Path.GetTempFileName();
        var tc = FadeTestDiscoverer.EnumerateTestCases(assemblyPath, launchable).Single();
        var handle = new CapturingFrameworkHandle();
        var executor = new FadeTestExecutorAdapter();

        // Inject the host AND pre-load the launchable. The executor's
        // RunGroup re-loads launchables from disk via FadeTestLaunchableLoader;
        // the test seam shortcircuits that to our in-memory instance.
        using (FadeTestHostResolver.OverrideForTests(host))
        using (FadeTestLaunchableLoader.RegisterForTests(assemblyPath, launchable))
        {
            executor.RunTests(new[] { tc }, runContext: null, frameworkHandle: handle);
        }

        try
        {
            if (handle.Results.Count != 1)
            {
                Assert.Fail($"Expected 1 TestResult, got {handle.Results.Count}. Handle messages: " +
                    string.Join("; ", handle.Messages));
            }
            return handle.Results[0];
        }
        finally
        {
            try { System.IO.File.Delete(assemblyPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Test host that writes to Console.Out / Console.Error during its
    /// RunTestAsync, mimicking what a real Fade test would do via `print`.
    /// </summary>
    private sealed class PrintingHost : IFadeTestHost
    {
        private readonly string _stdout;
        private readonly string _stderr;
        public PrintingHost(string stdoutText, string stderrText) { _stdout = stdoutText; _stderr = stderrText; }
        public Task InitializeAsync(FadeTestSessionContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task BeforeAllTestsAsync(FadeTestSessionContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task<FadeTestResult> RunTestAsync(FadeTestRunContext ctx, CancellationToken ct)
        {
            if (_stdout.Length > 0) Console.Write(_stdout);
            if (_stderr.Length > 0) Console.Error.Write(_stderr);
            return Task.FromResult(new FadeTestResult { testName = ctx.Entry.name, passed = true });
        }
        public Task AfterAllTestsAsync(FadeTestSessionContext ctx, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;
    }

    private sealed class CapturingFrameworkHandle : IFrameworkHandle
    {
        public List<TestResult> Results { get; } = new List<TestResult>();
        public List<string> Messages { get; } = new List<string>();
        public void RecordStart(TestCase testCase) { }
        public void RecordResult(TestResult testResult) => Results.Add(testResult);
        public void RecordEnd(TestCase testCase, TestOutcome outcome) { }
        public void RecordAttachments(IList<AttachmentSet> attachmentSets) { }
        public bool EnableShutdownAfterTestRun { get; set; }
        public int LaunchProcessWithDebuggerAttached(string filePath, string workingDirectory, string arguments,
            IDictionary<string, string> environmentVariables) => 0;
        public void SendMessage(TestMessageLevel testMessageLevel, string message) => Messages.Add(message);
    }

    // ---- Loader: mtime-based cache invalidation ----------------------

    [Test]
    public void Loader_ReinspectsAfterAssemblyMtimeChange()
    {
        // The whole point of the new loader is to pick up `dotnet build`
        // output without restarting vstest.console. The mtime sentinel is
        // what drives that — first call caches the inspection result with
        // a timestamp, repeat calls hit the cache, but a fresher mtime
        // forces a re-inspection (and an unload of the previous ALC).
        //
        // We verify by feeding the loader a non-Fade file (random bytes),
        // which logs a warning each time it's actually inspected. Cache
        // hits do NOT log; mtime-driven reloads DO. So warning count is
        // the observable witness.
        var tmpPath = Path.Combine(
            Path.GetTempPath(),
            "FadeAdapterMtimeTest_" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(tmpPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var logger = new CountingLogger();

        try
        {
            FadeTestLaunchableLoader.ResetCacheForTests();

            // First call inspects, fails (not a real PE), caches the
            // negative result with the current mtime.
            FadeTestLaunchableLoader.TryLoad(tmpPath, logger, out _);
            var warningsAfterFirst = logger.WarningCount;
            Assert.That(warningsAfterFirst, Is.GreaterThan(0),
                "an invalid DLL should log a warning on inspection");

            // Same mtime → cache hit, no new warning.
            FadeTestLaunchableLoader.TryLoad(tmpPath, logger, out _);
            Assert.That(logger.WarningCount, Is.EqualTo(warningsAfterFirst),
                "second TryLoad with unchanged mtime should hit the cache without re-inspecting");

            // Touch — newer mtime forces a fresh inspection on the next call.
            File.SetLastWriteTimeUtc(tmpPath, DateTime.UtcNow.AddMinutes(1));
            FadeTestLaunchableLoader.TryLoad(tmpPath, logger, out _);
            Assert.That(logger.WarningCount, Is.GreaterThan(warningsAfterFirst),
                "after the file's mtime advances, the loader must re-inspect");
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { /* best-effort */ }
            FadeTestLaunchableLoader.ResetCacheForTests();
        }
    }

    private sealed class CountingLogger : IMessageLogger
    {
        public int WarningCount { get; private set; }
        public int ErrorCount { get; private set; }
        public void SendMessage(TestMessageLevel level, string message)
        {
            if (level == TestMessageLevel.Warning) WarningCount++;
            else if (level == TestMessageLevel.Error) ErrorCount++;
        }
    }

    // ---- Helpers -----------------------------------------------------

    private sealed class ManifestLaunchable : ITestLaunchable
    {
        private static readonly byte[] _bytes = new byte[] { (byte)OpCodes.RETURN };
        private static readonly CommandCollection _commands = new CommandCollection();
        private readonly IReadOnlyList<TestManifestEntry> _entries;
        public ManifestLaunchable(IEnumerable<TestManifestEntry> entries)
        {
            _entries = entries.ToList();
        }
        public byte[] Bytecode => _bytes;
        public CommandCollection CommandCollection => _commands;
        public DebugData DebugData => new DebugData();
        public IReadOnlyList<TestManifestEntry> TestManifest => _entries;
    }
}
