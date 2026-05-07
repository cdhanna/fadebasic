using System.Collections.Generic;
using System.IO;
using FadeBasic.Launch;
using FadeBasic.Testing;
using FadeBasic.Virtual;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

namespace FadeBasic.TestAdapter
{
    /// <summary>
    /// VSTest TPv2 discoverer that surfaces every concrete
    /// <see cref="TestManifestEntry"/> in a built test assembly as a
    /// <see cref="TestCase"/>. The <c>[FileExtension]</c> attributes tell
    /// VSTest "scan these file types"; the <c>[DefaultExecutorUri]</c>
    /// binds emitted cases to <see cref="FadeTestExecutorAdapter"/>.
    /// </summary>
    [FileExtension(".dll")]
    [FileExtension(".exe")]
    [DefaultExecutorUri(FadeTestConstants.ExecutorUriString)]
    public sealed class FadeTestDiscoverer : ITestDiscoverer
    {
        public void DiscoverTests(
            IEnumerable<string> sources,
            IDiscoveryContext discoveryContext,
            IMessageLogger logger,
            ITestCaseDiscoverySink discoverySink)
        {
            foreach (var source in sources)
            {
                if (!FadeTestLaunchableLoader.TryLoad(source, logger, out var launchable))
                    continue; // not a Fade test project, or load failed (logged)

                foreach (var testCase in EnumerateTestCases(source, launchable))
                {
                    discoverySink.SendTestCase(testCase);
                }
            }
        }

        /// <summary>
        /// Build the <see cref="TestCase"/> objects without touching the sink.
        /// Exposed internally so unit tests can verify discovery output without
        /// stubbing the VSTest infrastructure. The originating <c>.fbasic</c>
        /// file path comes from each <see cref="TestManifestEntry.sourceFilePath"/>
        /// — the compile-time pipeline stamps it via <see cref="LaunchUtil.ApplySourceMap"/>.
        /// </summary>
        internal static IEnumerable<TestCase> EnumerateTestCases(
            string assemblyPath,
            ITestLaunchable launchable)
        {
            var asmName = Path.GetFileNameWithoutExtension(assemblyPath);
            foreach (var entry in launchable.TestManifest)
            {
                if (entry.isAbstract) continue;
                yield return BuildTestCase(entry, assemblyPath, asmName);
            }
        }

        private static TestCase BuildTestCase(
            TestManifestEntry entry,
            string assemblyPath,
            string assemblyName)
        {
            var fbasicFilePath = entry.sourceFilePath ?? string.Empty;

            // ManagedType + ManagedMethod are how modern IDE Test Explorers
            // (VS Code C# Dev Kit, Visual Studio, the `dotnet test` CLI's
            // structured output) build their test tree. Without these,
            // tooling falls back to parsing FullyQualifiedName which often
            // yields the "test appears under a dot" symptom.
            //
            // Format: "Fade.<sanitized .fbasic basename>" with the test
            // name as ManagedMethod. Identifiers are sanitized to
            // [A-Za-z0-9_] so consumers see syntactically-valid C# names.
            var typeSegment = FadeManagedIdentifier.ToManagedIdentifier(
                !string.IsNullOrEmpty(fbasicFilePath)
                    ? Path.GetFileNameWithoutExtension(fbasicFilePath)
                    : assemblyName);
            var managedType = "Fade." + typeSegment;
            var managedMethod = entry.name;

            // Keep FQN aligned with ManagedType.ManagedMethod — IDEs that fall
            // back to FQN-parsing then produce the same grouping as IDEs that
            // read ManagedType/ManagedMethod directly.
            var fqn = managedType + "." + managedMethod;

            var tc = new TestCase(fqn, FadeTestConstants.ExecutorUri, assemblyPath)
            {
                DisplayName = entry.name
            };
            // ManagedType / ManagedMethod tell IDE Test Explorers how to
            // split this case into a tree. The framework's own registrations
            // for these properties are private; we register our own via the
            // canonical IDs (TestProperty.Register is idempotent by id, so
            // we get back the framework's instance).
            tc.SetPropertyValue(FadeTestCaseProperties.ManagedType, managedType);
            tc.SetPropertyValue(FadeTestCaseProperties.ManagedMethod, managedMethod);

            // CodeFilePath + LineNumber drive the Test Explorer "double-click
            // jumps to source" behavior. Only set when we actually have the
            // source path — guessing a wrong file is worse than omitting.
            if (!string.IsNullOrEmpty(fbasicFilePath))
            {
                tc.CodeFilePath = fbasicFilePath;
            }
            if (entry.sourceLine > 0)
            {
                tc.LineNumber = entry.sourceLine;
            }

            // Filterable category. Both Rider and VS Code surface this as a
            // trait/tag the user can group/filter by ("show only Fade tests").
            tc.Traits.Add(new Trait("Category", "Fade"));
            if (!string.IsNullOrEmpty(entry.fromParent))
            {
                tc.Traits.Add(new Trait("FromParent", entry.fromParent));
                tc.SetPropertyValue(FadeTestCaseProperties.FromParent, entry.fromParent);
            }

            // Carry the entry-point address forward; the executor uses this
            // to look up the matching manifest entry, since DisplayName can
            // collide between abstract parents and concrete children.
            tc.SetPropertyValue(FadeTestCaseProperties.EntryPointAddress, entry.entryPointAddress);
            if (!string.IsNullOrEmpty(fbasicFilePath))
            {
                tc.SetPropertyValue(FadeTestCaseProperties.FbasicSourceFile, fbasicFilePath);
            }

            return tc;
        }

        /// <summary>
        /// Coerce an arbitrary string (file basename, assembly name) into a
        /// C#-shaped identifier so IDEs that parse <see cref="TestCase.ManagedType"/>
        /// as a dotted-identifier path (Rider, in particular) accept it.
        /// Delegates to <see cref="FadeManagedIdentifier.ToManagedIdentifier"/>
        /// so the LSP-based discovery path produces the same tree shape.
        /// </summary>
        internal static string ToManagedIdentifier(string raw)
            => FadeManagedIdentifier.ToManagedIdentifier(raw);
    }
}
