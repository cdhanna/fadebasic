using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FadeBasic.Testing
{
    /// <summary>
    /// Locates an <see cref="IFadeTestHost"/> implementation for the test app
    /// to use. Resolution order:
    /// <list type="number">
    /// <item>The instance passed into <see cref="FadeTestApplicationBuilder.RunAsync"/> (if non-null).</item>
    /// <item>A class in the entry assembly tagged <see cref="FadeTestHostAttribute"/>
    ///   that implements <see cref="IFadeTestHost"/>.</item>
    /// <item>Fallback to <see cref="DefaultFadeTestHost"/>.</item>
    /// </list>
    /// </summary>
    public static class FadeTestHostResolver
    {
        // Test-only seam. Set via OverrideForTests; checked first by Resolve so
        // unit tests of the VSTest executor can inject a fake host without
        // dragging in the [FadeTestHost] attribute discovery path.
        private static IFadeTestHost? _testOverride;

        public static IFadeTestHost Resolve(IFadeTestHost? explicitHost)
        {
            if (explicitHost != null) return explicitHost;
            if (_testOverride != null) return _testOverride;

            var discovered = DiscoverFromAttributes();
            if (discovered != null) return discovered;

            return new DefaultFadeTestHost();
        }

        /// <summary>
        /// Test seam — install a fake host that <see cref="Resolve"/> returns
        /// when no explicit host is passed. Returns an <see cref="IDisposable"/>
        /// that clears the override on disposal so each test owns its own
        /// scope. NOT for production code.
        /// </summary>
        public static IDisposable OverrideForTests(IFadeTestHost host)
        {
            _testOverride = host;
            return new TestOverrideScope();
        }

        private sealed class TestOverrideScope : IDisposable
        {
            public void Dispose() => _testOverride = null;
        }

        public static IFadeTestHost? DiscoverFromAttributes()
        {
            var entry = Assembly.GetEntryAssembly();
            var candidates = new List<Type>();

            // Try the entry assembly first. Under `dotnet run` this is the
            // user's app and usually carries the host, so we keep authoring-
            // intent priority over anything transitively referenced.
            if (entry != null) CollectHostCandidates(entry, candidates);

            // Fall through to all loaded assemblies when the entry didn't
            // contain a [FadeTestHost]. Two cases this catches:
            //   1. EntryAssembly is null (some test-host scenarios).
            //   2. Under `dotnet test`, EntryAssembly is vstest's testhost.dll;
            //      the launchable carrying the host is loaded into a separate
            //      collectible ALC by FadeTestLaunchableLoader. AppDomain
            //      enumerates assemblies across all ALCs.
            if (candidates.Count == 0)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm == entry) continue;
                    CollectHostCandidates(asm, candidates);
                }
            }

            if (candidates.Count == 0) return null;
            if (candidates.Count > 1)
            {
                var names = string.Join(", ", candidates.Select(c => c.FullName));
                throw new InvalidOperationException(
                    $"Multiple [FadeTestHost] classes found: {names}. Exactly one is permitted per test app.");
            }

            var hostType = candidates[0];
            try
            {
                return (IFadeTestHost)Activator.CreateInstance(hostType)!;
            }
            catch (MissingMethodException)
            {
                throw new InvalidOperationException(
                    $"[FadeTestHost] class `{hostType.FullName}` must have a public parameterless constructor.");
            }
        }

        private static void CollectHostCandidates(Assembly asm, List<Type> sink)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

            foreach (var t in types)
            {
                if (t == null) continue;
                if (t.IsAbstract || t.IsInterface) continue;
                if (!typeof(IFadeTestHost).IsAssignableFrom(t)) continue;
                if (t.GetCustomAttribute<FadeTestHostAttribute>() == null) continue;
                sink.Add(t);
            }
        }
    }
}
