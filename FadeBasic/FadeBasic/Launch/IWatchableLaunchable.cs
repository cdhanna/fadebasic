using System.Collections.Generic;

namespace FadeBasic.Launch
{
    /// <summary>
    /// An <see cref="ILaunchable"/> that knows the ordered set of .fbasic source
    /// files it was built from. Hot-reload watch (<c>--fade-watch</c>) uses this
    /// to recompile the exact same sources — via <c>SourceMap.CreateSourceMap</c>,
    /// the same join the build uses — instead of a user-supplied path that could
    /// be wrong or miss files in a multi-file project.
    ///
    /// The generated launchable class implements this (paths are baked in at
    /// build time from the project's source map).
    /// </summary>
    public interface IWatchableLaunchable : ILaunchable
    {
        /// <summary>Absolute .fbasic source paths, in the order the build joined them.</summary>
        IReadOnlyList<string> SourceFiles { get; }
    }
}
