using System.Collections.Generic;
using FadeBasic.Virtual;

namespace FadeBasic.Launch
{
    /// <summary>
    /// An <see cref="ILaunchable"/> that also carries a discovered test
    /// manifest. The console-app launcher inspects this when handling
    /// <c>--fade-test=name</c> / <c>--fade-list-tests</c> arguments.
    /// Implementations include <see cref="Sdk.FadeRuntimeContext"/> and
    /// the generated launchable class baked into compiled console apps.
    /// </summary>
    public interface ITestLaunchable : ILaunchable
    {
        IReadOnlyList<TestManifestEntry> TestManifest { get; }
    }
}
