using System;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using FadeBasic.Launch;
using FadeBasic.Sdk;
using FadeBasic.Testing;
using FadeBasic.Virtual;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class FadeTestingAdapterTests
{
    [Test]
    public void IsTestInvocation_RecognizesCommonMtpFlags()
    {
        Assert.That(FadeTestApplicationBuilder.IsTestInvocation(new[] { "--list-tests" }), Is.True);
        Assert.That(FadeTestApplicationBuilder.IsTestInvocation(new[] { "--server" }), Is.True);
        Assert.That(FadeTestApplicationBuilder.IsTestInvocation(new[] { "--filter", "DisplayName=foo" }), Is.True);
        Assert.That(FadeTestApplicationBuilder.IsTestInvocation(new[] { "--results-directory=/tmp" }), Is.True);
        Assert.That(FadeTestApplicationBuilder.IsTestInvocation(new[] { "--diagnostic" }), Is.True);
    }

    [Test]
    public void IsTestInvocation_DoesNotMatchProgramArgs()
    {
        Assert.That(FadeTestApplicationBuilder.IsTestInvocation(System.Array.Empty<string>()), Is.False);
        Assert.That(FadeTestApplicationBuilder.IsTestInvocation(new[] { "hello", "world" }), Is.False);
        // --fade-test=name is the legacy CLI shape; it should NOT route through
        // MTP — Launcher.Main<T> handles it.
        Assert.That(FadeTestApplicationBuilder.IsTestInvocation(new[] { "--fade-test=foo" }), Is.False);
        Assert.That(FadeTestApplicationBuilder.IsTestInvocation(new[] { "--fade-test", "foo" }), Is.False);
        Assert.That(FadeTestApplicationBuilder.IsTestInvocation(new[] { "--fade-list-tests" }), Is.False);
        Assert.That(FadeTestApplicationBuilder.IsTestInvocation(new[] { "--fade-test-all" }), Is.False);
    }

    [Test]
    public void Resolver_FallsBackToDefaultHost_WhenNoAttribute()
    {
        // Tests assembly has no [FadeTestHost] class attribute; the resolver
        // should hand back DefaultFadeTestHost when nothing else is provided.
        var host = FadeTestHostResolver.Resolve(null);
        Assert.That(host, Is.InstanceOf<DefaultFadeTestHost>());
    }

    [Test]
    public void Resolver_PrefersExplicitHost()
    {
        var explicitHost = new StubHost();
        var resolved = FadeTestHostResolver.Resolve(explicitHost);
        Assert.That(resolved, Is.SameAs(explicitHost));
    }

    [Test]
    public async Task DefaultHost_PassesThroughCustomImplementation()
    {
        // Verify the contract: a custom IFadeTestHost gets the right fields
        // on FadeTestRunContext and can return a result that the framework
        // would surface back through MTP.
        var stub = new RecordingHost();
        var stubLaunchable = new EmptyLaunchable();
        var entry = new TestManifestEntry { name = "noop" };
        var hostMethods = HostMethodTable.FromCommandCollection(stubLaunchable.CommandCollection);
        var ctx = new FadeTestRunContext(stubLaunchable, entry, hostMethods);

        var result = await stub.RunTestAsync(ctx, CancellationToken.None);
        Assert.That(stub.LastEntry, Is.SameAs(entry));
        Assert.That(stub.LastLaunchable, Is.SameAs(stubLaunchable));
        Assert.That(result.passed, Is.True);
    }

    [Test]
    public void DefaultHost_AbstractEntry_IsRejected()
    {
        var stubLaunchable = new EmptyLaunchable();
        var entry = new TestManifestEntry { name = "abstract_one", isAbstract = true };
        var hostMethods = HostMethodTable.FromCommandCollection(stubLaunchable.CommandCollection);

        var result = FadeTestExecutor.RunTest(stubLaunchable.Bytecode, hostMethods, entry);
        Assert.That(result.passed, Is.False);
        Assert.That(result.failureMessage, Does.Contain("abstract"));
    }

    private sealed class StubHost : IFadeTestHost
    {
        public Task InitializeAsync(FadeTestSessionContext c, CancellationToken ct) => Task.CompletedTask;
        public Task BeforeAllTestsAsync(FadeTestSessionContext c, CancellationToken ct) => Task.CompletedTask;
        public Task<FadeTestResult> RunTestAsync(FadeTestRunContext c, CancellationToken ct)
            => Task.FromResult(new FadeTestResult { testName = c.Entry.name, passed = true });
        public Task AfterAllTestsAsync(FadeTestSessionContext c, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;
    }

    private sealed class RecordingHost : IFadeTestHost
    {
        public TestManifestEntry? LastEntry;
        public ITestLaunchable? LastLaunchable;

        public Task InitializeAsync(FadeTestSessionContext c, CancellationToken ct) => Task.CompletedTask;
        public Task BeforeAllTestsAsync(FadeTestSessionContext c, CancellationToken ct) => Task.CompletedTask;
        public Task<FadeTestResult> RunTestAsync(FadeTestRunContext c, CancellationToken ct)
        {
            LastEntry = c.Entry;
            LastLaunchable = c.Launchable;
            return Task.FromResult(new FadeTestResult { testName = c.Entry.name, passed = true });
        }
        public Task AfterAllTestsAsync(FadeTestSessionContext c, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;
    }

    private sealed class EmptyLaunchable : ITestLaunchable
    {
        // A single HALT byte (opcode value mirrors what the compiler emits at
        // the end of every program). The VM is expected to halt immediately
        // when its IP starts here, which is good enough for "does the host
        // round-trip a result" coverage.
        private static readonly byte[] _bytes = new byte[] { (byte)OpCodes.RETURN };
        private static readonly CommandCollection _commands = new CommandCollection();

        public byte[] Bytecode => _bytes;
        public CommandCollection CommandCollection => _commands;
        public DebugData DebugData => new DebugData();
        public IReadOnlyList<TestManifestEntry> TestManifest => new System.Collections.Generic.List<TestManifestEntry>();
    }
}
