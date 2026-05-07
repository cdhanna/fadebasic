using System;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic.Launch;
using FadeBasic.Sdk;
using FadeBasic.Virtual;

namespace FadeBasic.Testing
{
    /// <summary>
    /// Extensibility point for downstream consumers that want to control the
    /// "around" of every Fade test — typically: spin up a Game/graphics-device
    /// once, reset host-side state between tests, decide how the VM is driven
    /// (synchronous, frame-stepped, debugger-attached). The default implementation
    /// (<see cref="DefaultFadeTestHost"/>) just delegates to
    /// <see cref="FadeTestExecutor.RunTest"/>.
    /// </summary>
    /// <remarks>
    /// Lifecycle, in order:
    /// <list type="number">
    /// <item><see cref="InitializeAsync"/> — once per process, expensive setup.</item>
    /// <item><see cref="BeforeAllTestsAsync"/> — once per run.</item>
    /// <item>For each test: <see cref="RunTestAsync"/>.</item>
    /// <item><see cref="AfterAllTestsAsync"/>.</item>
    /// <item><see cref="DisposeAsync"/>.</item>
    /// </list>
    /// </remarks>
    public interface IFadeTestHost
    {
        Task InitializeAsync(FadeTestSessionContext ctx, CancellationToken ct);

        Task BeforeAllTestsAsync(FadeTestSessionContext ctx, CancellationToken ct);

        /// <summary>
        /// Run a single test. Implementations typically:
        /// (1) reset host-side state, (2) build/reuse a VM at
        /// <c>ctx.Entry.entryPointAddress</c>, (3) drive the VM, (4) translate
        /// the outcome to a <see cref="FadeTestResult"/>. Hosts that only want
        /// to wrap default behavior should call <c>ctx.RunDefaultAsync(ct)</c>.
        /// </summary>
        Task<FadeTestResult> RunTestAsync(FadeTestRunContext ctx, CancellationToken ct);

        Task AfterAllTestsAsync(FadeTestSessionContext ctx, CancellationToken ct);

        ValueTask DisposeAsync();
    }

    /// <summary>
    /// Information passed to per-session host hooks. The launchable carries the
    /// bytecode + commands; <see cref="Services"/> is MTP's service provider
    /// (logger, output device, etc.), null when running outside MTP (unit
    /// tests of the host).
    /// </summary>
    public sealed class FadeTestSessionContext
    {
        public ITestLaunchable Launchable { get; }
        public IServiceProvider? Services { get; }

        public FadeTestSessionContext(ITestLaunchable launchable, IServiceProvider? services)
        {
            Launchable = launchable;
            Services = services;
        }
    }

    /// <summary>
    /// Information passed to per-test host hooks.
    /// </summary>
    public sealed class FadeTestRunContext
    {
        public ITestLaunchable Launchable { get; }
        public TestManifestEntry Entry { get; }
        public HostMethodTable HostMethods { get; }

        public FadeTestRunContext(ITestLaunchable launchable, TestManifestEntry entry, HostMethodTable hostMethods)
        {
            Launchable = launchable;
            Entry = entry;
            HostMethods = hostMethods;
        }

        /// <summary>
        /// Convenience for hosts that only need to wrap default execution.
        /// Returns the same result the <see cref="DefaultFadeTestHost"/> would
        /// produce for this test.
        /// </summary>
        public Task<FadeTestResult> RunDefaultAsync(CancellationToken ct)
        {
            // FadeTestExecutor.RunTest is synchronous today. Wrap it; once
            // cooperative cancellation lands inside the VM, this becomes an
            // actual async call.
            ct.ThrowIfCancellationRequested();
            var result = FadeTestExecutor.RunTest(Launchable.Bytecode, HostMethods, Entry);
            return Task.FromResult(result);
        }
    }
}
