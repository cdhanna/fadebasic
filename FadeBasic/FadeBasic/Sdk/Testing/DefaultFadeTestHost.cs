using System.Threading;
using System.Threading.Tasks;
using FadeBasic.Sdk;

namespace FadeBasic.Testing
{
    /// <summary>
    /// Stateless default host. Each test gets a fresh <c>VirtualMachine</c>
    /// via <see cref="FadeTestExecutor.RunTest"/> with no host-side reset.
    /// Used when the consumer hasn't tagged any class with
    /// <see cref="FadeTestHostAttribute"/>.
    /// </summary>
    public sealed class DefaultFadeTestHost : IFadeTestHost
    {
        public Task InitializeAsync(FadeTestSessionContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task BeforeAllTestsAsync(FadeTestSessionContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<FadeTestResult> RunTestAsync(FadeTestRunContext ctx, CancellationToken ct)
            => ctx.RunDefaultAsync(ct);

        public Task AfterAllTestsAsync(FadeTestSessionContext ctx, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => default;
    }
}
