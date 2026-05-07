using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic.Launch;
using FadeBasic.Sdk;
using FadeBasic.Virtual;
using Microsoft.Testing.Platform.Capabilities.TestFramework;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.TestFramework;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.Requests;
using Microsoft.Testing.Platform.TestHost;

namespace FadeBasic.Testing
{
    /// <summary>
    /// Microsoft.Testing.Platform <see cref="ITestFramework"/> that surfaces
    /// every concrete <c>TestManifestEntry</c> as an MTP <see cref="TestNode"/>.
    /// Discovery and execution both flow through the configured
    /// <see cref="IFadeTestHost"/>; the default host calls
    /// <see cref="FadeTestExecutor.RunTest"/> directly.
    /// </summary>
    internal sealed class FadeTestFramework : ITestFramework, IDataProducer
    {
        public const string FrameworkUid = "FadeBasic.Testing";

        private readonly ITestLaunchable _launchable;
        private readonly IFadeTestHost _host;
        private readonly IServiceProvider _services;
        private readonly HostMethodTable _hostMethods;
        private FadeTestSessionContext? _sessionContext;
        private bool _initialized;
        // Guards against double-firing AfterAllTestsAsync. We invoke it from
        // RunAsync's finally (so it pairs with BeforeAllTestsAsync and fires
        // even when the filter matches zero tests), and again defensively from
        // CloseTestSessionAsync — only the first call wins. Without the
        // RunAsync-side call, a "0 tests matched" run leaves the host blocked
        // because MTP, in some configurations, never calls CloseTestSession
        // after a run with no produced TestNode updates.
        private bool _afterAllInvoked;

        public FadeTestFramework(ITestLaunchable launchable, IFadeTestHost host, IServiceProvider services)
        {
            _launchable = launchable;
            _host = host;
            _services = services;
            _hostMethods = HostMethodTable.FromCommandCollection(launchable.CommandCollection);
        }

        public string Uid => FrameworkUid;
        public string Version => typeof(FadeTestFramework).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        public string DisplayName => "Fade";
        public string Description => "Surfaces FadeBasic `test ... endtest` blocks to dotnet test.";

        public Type[] DataTypesProduced => new[] { typeof(TestNodeUpdateMessage) };

        public Task<bool> IsEnabledAsync() => Task.FromResult(true);

        public async Task<CreateTestSessionResult> CreateTestSessionAsync(CreateTestSessionContext context)
        {
            _sessionContext = new FadeTestSessionContext(_launchable, _services);
            try
            {
                await _host.InitializeAsync(_sessionContext, context.CancellationToken).ConfigureAwait(false);
                _initialized = true;
                return new CreateTestSessionResult { IsSuccess = true };
            }
            catch (Exception ex)
            {
                return new CreateTestSessionResult { IsSuccess = false, ErrorMessage = "Fade test host init failed: " + ex.Message };
            }
        }

        public async Task<CloseTestSessionResult> CloseTestSessionAsync(CloseTestSessionContext context)
        {
            if (_initialized && _sessionContext != null)
            {
                await InvokeAfterAllOnceAsync(context.CancellationToken).ConfigureAwait(false);
                try
                {
                    await _host.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Suppress — the session should still close cleanly even
                    // if the host's DisposeAsync throws.
                }
            }
            return new CloseTestSessionResult { IsSuccess = true };
        }

        // Single-shot AfterAll invocation. Safe to call from both RunAsync's
        // finally and CloseTestSessionAsync without the host seeing the call
        // twice.
        private async Task InvokeAfterAllOnceAsync(CancellationToken ct)
        {
            if (_sessionContext == null) return;
            if (_afterAllInvoked) return;
            _afterAllInvoked = true;
            try
            {
                await _host.AfterAllTestsAsync(_sessionContext, ct).ConfigureAwait(false);
            }
            catch
            {
                // Suppress — the session should still close cleanly even if
                // the host's AfterAll throws. The error is surfaced in the
                // failed test that triggered it (if any).
            }
        }

        public async Task ExecuteRequestAsync(ExecuteRequestContext context)
        {
            try
            {
                switch (context.Request)
                {
                    case DiscoverTestExecutionRequest discoverRequest:
                        await DiscoverAsync(context, discoverRequest).ConfigureAwait(false);
                        break;
                    case RunTestExecutionRequest runRequest:
                        await RunAsync(context, runRequest).ConfigureAwait(false);
                        break;
                    default:
                        // Unknown request type — complete and let MTP move on.
                        break;
                }
            }
            finally
            {
                context.Complete();
            }
        }

        private async Task DiscoverAsync(ExecuteRequestContext context, DiscoverTestExecutionRequest request)
        {
            foreach (var entry in EnumerateConcrete(_launchable.TestManifest))
            {
                var node = BuildTestNode(entry);
                node.Properties.Add(DiscoveredTestNodeStateProperty.CachedInstance);
                await PublishAsync(context, request.Session.SessionUid, node).ConfigureAwait(false);
            }
        }

        private async Task RunAsync(ExecuteRequestContext context, RunTestExecutionRequest request)
        {
            if (_sessionContext == null) return;

            var ct = context.CancellationToken;

            await _host.BeforeAllTestsAsync(_sessionContext, ct).ConfigureAwait(false);

            try
            {
                foreach (var entry in EnumerateConcrete(_launchable.TestManifest))
                {
                    ct.ThrowIfCancellationRequested();

                    var node = BuildTestNode(entry);
                    if (!ShouldRun(entry, node, request.Filter)) continue;

                    node.Properties.Add(InProgressTestNodeStateProperty.CachedInstance);
                    await PublishAsync(context, request.Session.SessionUid, node).ConfigureAwait(false);

                    FadeTestResult result;
                    try
                    {
                        var runCtx = new FadeTestRunContext(_launchable, entry, _hostMethods);
                        result = await _host.RunTestAsync(runCtx, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // The runner itself cancelled — treat as a failure attached
                        // to this test so MTP doesn't drop the in-progress state.
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

                    var finalNode = BuildTestNode(entry);
                    if (result.passed)
                    {
                        finalNode.Properties.Add(PassedTestNodeStateProperty.CachedInstance);
                    }
                    else
                    {
                        var ex = new FadeTestException(result);
                        finalNode.Properties.Add(new FailedTestNodeStateProperty(ex, result.failureMessage));
                    }
                    await PublishAsync(context, request.Session.SessionUid, finalNode).ConfigureAwait(false);
                }
            }
            finally
            {
                // Pair AfterAll with the BeforeAll above. If the filter matched
                // zero tests (foreach never enters the body), or if MTP doesn't
                // call CloseTestSessionAsync after a no-op run, the host still
                // gets the "all tests done" signal it uses to wind down — e.g.,
                // a hosted Game shutting down its window.
                await InvokeAfterAllOnceAsync(ct).ConfigureAwait(false);
            }
        }

        private TestNode BuildTestNode(TestManifestEntry entry)
        {
            var node = new TestNode
            {
                Uid = "fade::" + entry.name,
                DisplayName = entry.name
            };

            var (asmName, nsName, typeName, methodName) = SplitIdentity(entry);

            // TestMethodIdentifierProperty lets MTP's TreeNodeFilter resolve
            // path-style filters like `dotnet test --filter "*singleFrame*"`
            // or `/*/*/<file>/*`. Without it the filter has no structured
            // properties to match against and silently selects zero tests.
            // Positional args here because the record's parameter names in
            // the shipping NuGet metadata don't match the property names —
            // named arguments fail to bind.
            node.Properties.Add(new TestMethodIdentifierProperty(
                asmName,
                nsName,
                typeName,
                methodName,
                /*arity:*/ 0,
                Array.Empty<string>(),
                "void"));

            // File-location property gives Test Explorer the gutter source
            // link. The compile-time post-pass (LaunchUtil) populates
            // sourceFilePath via the project's SourceMap; older launchables
            // may leave it empty, in which case the IDE will fall back to
            // the test name instead of a clickable file link.
            if (entry.sourceLine > 0)
            {
                node.Properties.Add(new TestFileLocationProperty(
                    entry.sourceFilePath ?? string.Empty,
                    new LinePositionSpan(
                        new LinePosition(entry.sourceLine, entry.sourceChar),
                        new LinePosition(entry.sourceLine, entry.sourceChar))));
            }
            return node;
        }

        // MTP tree-node paths are `/Asm/Namespace/Type/Method`. Fade tests
        // don't have a true CLR class hierarchy, so we synthesize:
        //   asm  → the launchable's owning assembly
        //   ns   → constant "Fade" (avoid an empty segment — MTP's path parser
        //          collapses consecutive slashes, which would silently turn
        //          our 4-segment path into 3 and break `/*/*/*/<method>`-
        //          style filters)
        //   type → the .fbasic file's basename (so all tests in fish.fbasic
        //          share `/.../fish/...`, which makes per-file filters natural)
        //   method → the test name
        private (string asm, string ns, string type, string method) SplitIdentity(TestManifestEntry entry)
        {
            var asm = _launchable.GetType().Assembly.GetName().Name ?? "Fade";
            var ns = "Fade";
            var type = "Tests";
            if (!string.IsNullOrEmpty(entry.sourceFilePath))
            {
                type = System.IO.Path.GetFileNameWithoutExtension(entry.sourceFilePath);
            }
            return (asm, ns, type, entry.name);
        }

        private string BuildNodePath(TestManifestEntry entry)
        {
            var (asm, ns, type, method) = SplitIdentity(entry);
            return $"/{asm}/{ns}/{type}/{method}";
        }

        private static IEnumerable<TestManifestEntry> EnumerateConcrete(IReadOnlyList<TestManifestEntry> manifest)
        {
            foreach (var entry in manifest)
            {
                if (entry.isAbstract) continue;
                yield return entry;
            }
        }

        // MTP exposes three filter shapes:
        //   NopFilter            — always match (the default).
        //   TestNodeUidListFilter — exact UID matches; produced by selections
        //                           coming from --filter-uid or IDE test-panel
        //                           "run selected" actions.
        //   TreeNodeFilter        — path/glob expression on `/Asm/Ns/Type/Method`;
        //                           produced by `dotnet test --filter "..."`.
        // Anything else: be permissive (run the test) so a future MTP filter
        // type doesn't silently drop tests.
        private bool ShouldRun(TestManifestEntry entry, TestNode node, ITestExecutionFilter? filter)
        {
            if (filter == null || filter is NopFilter) return true;

            if (filter is TestNodeUidListFilter uidList && uidList.TestNodeUids != null)
            {
                foreach (var u in uidList.TestNodeUids)
                {
                    if (u.Value == node.Uid) return true;
                }
                return false;
            }

            // TreeNodeFilter is currently flagged TPEXP ("evaluation only")
            // by MTP. Suppressed here because path-style `dotnet test --filter`
            // is the de-facto way users select tests; the API has been stable
            // across recent MTP versions and the diagnostic just signals that
            // the type may move to a non-preview namespace in the future.
#pragma warning disable TPEXP
            if (filter is TreeNodeFilter tree)
            {
                return tree.MatchesFilter(BuildNodePath(entry), node.Properties);
            }
#pragma warning restore TPEXP

            return true;
        }

        private async Task PublishAsync(ExecuteRequestContext context, SessionUid sessionUid, TestNode node)
        {
            await context.MessageBus
                .PublishAsync(this, new TestNodeUpdateMessage(sessionUid, node))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Surfaces a Fade-specific failure to MTP. The framework reports failure
    /// messages with their original `.fbasic` source text and the offending
    /// instruction index; the IDE renders the stack from <see cref="StackTrace"/>.
    /// </summary>
    internal sealed class FadeTestException : Exception
    {
        public FadeTestException(FadeTestResult result)
            : base(BuildMessage(result))
        {
        }

        private static string BuildMessage(FadeTestResult r)
        {
            var msg = string.IsNullOrEmpty(r.failureMessage) ? "test failed" : r.failureMessage;
            if (!string.IsNullOrEmpty(r.failureSourceText))
            {
                msg += $"\n  source: {r.failureSourceText}";
            }
            if (r.failureInstructionIndex >= 0)
            {
                msg += $"\n  ip: {r.failureInstructionIndex}";
            }
            return msg;
        }
    }

    internal sealed class FadeTestFrameworkCapabilities : ITestFrameworkCapabilities
    {
        public IReadOnlyCollection<ITestFrameworkCapability> Capabilities { get; }
            = Array.Empty<ITestFrameworkCapability>();
    }
}
