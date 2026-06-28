using System;

namespace FadeBasic.TestAdapter
{
    /// <summary>
    /// Shared identifiers between <see cref="FadeTestDiscoverer"/> and
    /// <see cref="FadeTestExecutorAdapter"/>. The <see cref="ExecutorUriString"/>
    /// is what binds a discovered <c>TestCase</c> to the executor that runs
    /// it; both classes' attributes reference this constant. The <c>/v1</c>
    /// suffix gives a graceful version-bump path if the adapter contract ever
    /// needs to break.
    /// </summary>
    internal static class FadeTestConstants
    {
        public const string ExecutorUriString = "executor://fadebasic/v1";

        public static readonly Uri ExecutorUri = new Uri(ExecutorUriString);
    }
}
