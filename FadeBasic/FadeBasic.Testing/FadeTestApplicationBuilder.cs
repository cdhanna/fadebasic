using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using FadeBasic.Launch;
using FadeBasic.Sdk;
using FadeBasic.Virtual;
using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Extensions.Messages;

namespace FadeBasic.Testing
{
    /// <summary>
    /// Single entry point used by the generated <c>Main</c> when a Fade
    /// project opts in to <c>dotnet test</c>. Detects MTP-flavored args,
    /// resolves an <see cref="IFadeTestHost"/>, and delegates to the
    /// Microsoft.Testing.Platform builder.
    /// </summary>
    public static class FadeTestApplicationBuilder
    {
        // Anything starting with one of these prefixes (or matching one of the
        // bare flags) is a MTP / dotnet-test invocation. We intentionally
        // dispatch into MTP for *unknown* `--`-args too so future MTP
        // additions don't fall through to Launcher.Main and break with
        // "unrecognized argument" errors.
        private static readonly string[] _mtpExactArgs = new[]
        {
            "--list-tests",
            "--server",
            "--diagnostic",
            "--no-banner",
            "--info",
            "--help",
            "--retry-failed-tests"
        };

        private static readonly string[] _mtpPrefixArgs = new[]
        {
            "--filter", "--filter-uid", "--filter-trait",
            "--results-directory", "--report-trx", "--report-trx-filename",
            "--minimum-expected-tests", "--timeout", "--treenode-filter"
        };

        // Environment variables MTP / vstest set when launching a test app.
        // Their presence is a strong signal that we should route through MTP
        // even when no recognized flag is on the command line.
        private static readonly string[] _mtpEnvVarPrefixes = new[]
        {
            "TESTINGPLATFORM_",
            "DOTNET_TEST_"
        };

        /// <summary>
        /// True when the args (or surrounding environment) indicate a
        /// <c>dotnet test</c> / IDE Test Explorer invocation. Used by the
        /// generated <c>Main</c> to decide between MTP and the existing
        /// <see cref="Launcher.Main{T}(string[], LaunchOptions)"/> path.
        /// </summary>
        public static bool IsTestInvocation(string[] args)
        {
            if (args != null)
            {
                foreach (var raw in args)
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    foreach (var exact in _mtpExactArgs)
                    {
                        if (string.Equals(raw, exact, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    foreach (var prefix in _mtpPrefixArgs)
                    {
                        if (raw.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                            raw.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase) ||
                            raw.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }

            foreach (var envKey in System.Environment.GetEnvironmentVariables().Keys)
            {
                if (envKey is string s)
                {
                    foreach (var prefix in _mtpEnvVarPrefixes)
                    {
                        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            return false;
        }

        // MTP args that print info and exit without ever running a test session.
        // Hosts that do extra setup before MTP takes over (e.g., spinning up a
        // game window) can use IsInfoOnlyInvocation to skip that work, since
        // the framework's RunAsync session callback is never invoked for these.
        private static readonly string[] _mtpInfoOnlyArgs = new[]
        {
            "--help", "-h", "-?",
            "--info",
            "--list-tests",
            "--version"
        };

        /// <summary>
        /// True when the args indicate MTP will just print information and
        /// exit (help, version, --list-tests, etc.) without running any
        /// tests. Use this in your <c>Main</c> to short-circuit any
        /// expensive host-side setup (graphics device, content loading,
        /// game-loop spin-up) and just <c>await RunAsync</c> directly.
        /// </summary>
        public static bool IsInfoOnlyInvocation(string[] args)
        {
            if (args == null) return false;
            foreach (var raw in args)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                foreach (var info in _mtpInfoOnlyArgs)
                {
                    if (string.Equals(raw, info, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Boot the MTP test application against <paramref name="launchable"/>.
        /// Pass <paramref name="host"/> to inject a custom <see cref="IFadeTestHost"/>;
        /// otherwise the resolver discovers a <see cref="FadeTestHostAttribute"/>-tagged
        /// class or falls back to <see cref="DefaultFadeTestHost"/>.
        /// </summary>
        public static async Task<int> RunAsync(ITestLaunchable launchable, string[] args, IFadeTestHost? host = null)
        {
            var resolvedHost = FadeTestHostResolver.Resolve(host);

            var builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);
            builder.RegisterTestFramework(
                _ => new FadeTestFrameworkCapabilities(),
                (_, services) => new FadeTestFramework(launchable, resolvedHost, services));

            using var app = await builder.BuildAsync().ConfigureAwait(false);
            return await app.RunAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// All concrete (non-abstract) tests in the launchable's manifest.
        /// This is just a filtered view of <see cref="ITestLaunchable.TestManifest"/>;
        /// abstract entries are fixtures that exist for inheritance and aren't
        /// runnable on their own.
        /// </summary>
        public static IEnumerable<TestManifestEntry> GetConcreteTests(ITestLaunchable launchable)
        {
            if (launchable == null) throw new ArgumentNullException(nameof(launchable));
            foreach (var entry in launchable.TestManifest)
            {
                if (entry.isAbstract) continue;
                yield return entry;
            }
        }

        /// <summary>
        /// The subset of <see cref="GetConcreteTests"/> that would actually
        /// be executed under the supplied <paramref name="args"/>. Honors the
        /// same filter shapes <see cref="FadeTestFramework"/> does at run time:
        /// <c>--filter-uid &lt;fade::name&gt;</c>, <c>--filter &lt;path-glob&gt;</c>,
        /// and the no-filter case (returns all concrete tests).
        ///
        /// Intended for hosts that want to skip expensive setup (e.g., booting
        /// a graphics-device-backed game) when a run will execute zero tests.
        /// </summary>
        public static List<TestManifestEntry> SelectTests(ITestLaunchable launchable, string[] args)
        {
            if (launchable == null) throw new ArgumentNullException(nameof(launchable));

            var filter = ParseFilterArgs(args);
            var asmName = launchable.GetType().Assembly.GetName().Name ?? "Fade";
            var result = new List<TestManifestEntry>();
            foreach (var entry in launchable.TestManifest)
            {
                if (entry.isAbstract) continue;
                if (filter.Matches(asmName, entry)) result.Add(entry);
            }
            return result;
        }

        // Mirrors FadeTestFramework.BuildNodePath. Kept here so consumers can
        // pre-compute the same path the framework would emit for a given
        // entry, which is what TreeNodeFilter matches against.
        internal static string BuildNodePath(string asmName, TestManifestEntry entry)
        {
            var typeName = "Tests";
            if (!string.IsNullOrEmpty(entry.sourceFilePath))
            {
                typeName = System.IO.Path.GetFileNameWithoutExtension(entry.sourceFilePath);
            }
            return "/" + asmName + "/Fade/" + typeName + "/" + entry.name;
        }

        private readonly struct ParsedFilter
        {
            public readonly HashSet<string>? RequestedUids;
            public readonly string? PathGlob;

            public ParsedFilter(HashSet<string>? uids, string? path)
            {
                RequestedUids = uids;
                PathGlob = path;
            }

            public bool IsEmpty => RequestedUids == null && PathGlob == null;

            public bool Matches(string asmName, TestManifestEntry entry)
            {
                if (IsEmpty) return true;
                if (RequestedUids != null && RequestedUids.Contains("fade::" + entry.name)) return true;
                if (PathGlob != null)
                {
                    var path = BuildNodePath(asmName, entry);
                    if (TreeNodeFilterMatches(PathGlob, path)) return true;
                }
                return false;
            }
        }

        private static ParsedFilter ParseFilterArgs(string[] args)
        {
            HashSet<string>? uids = null;
            string? pathGlob = null;
            if (args == null) return new ParsedFilter(uids, pathGlob);

            for (var i = 0; i < args.Length; i++)
            {
                var raw = args[i];
                if (string.IsNullOrEmpty(raw)) continue;

                if (TryReadValue(args, ref i, "--filter-uid", out var uid))
                {
                    uids ??= new HashSet<string>(StringComparer.Ordinal);
                    uids.Add(uid!);
                }
                // dotnet test sometimes forwards the user's `--filter <glob>`
                // unchanged, but on .NET 10 it can also rewrite to the
                // explicit `--treenode-filter <glob>`. Accept both spellings.
                else if (TryReadValue(args, ref i, "--filter", out var glob)
                      || TryReadValue(args, ref i, "--treenode-filter", out glob))
                {
                    pathGlob = glob;
                }
            }
            return new ParsedFilter(uids, pathGlob);
        }

        private static bool TryReadValue(string[] args, ref int i, string flag, out string? value)
        {
            var raw = args[i];
            if (string.Equals(raw, flag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    value = args[++i];
                    return true;
                }
                value = null;
                return false;
            }
            var prefix = flag + "=";
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = raw.Substring(prefix.Length);
                return true;
            }
            // Some MTP variants accept `--filter:value`.
            prefix = flag + ":";
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = raw.Substring(prefix.Length);
                return true;
            }
            value = null;
            return false;
        }

        // MTP's TreeNodeFilter is internal-ctor and marked TPEXP, so we use
        // reflection rather than `new TreeNodeFilter(...)`. If the API moves
        // we degrade to "match anything" so a host doesn't accidentally skip
        // booting and miss real tests.
        private static MethodInfo? _treeNodeMatchMethod;
        private static ConstructorInfo? _treeNodeCtor;
        private static bool _treeNodeReflectionFailed;

        private static bool TreeNodeFilterMatches(string glob, string path)
        {
            if (_treeNodeReflectionFailed) return true;

            try
            {
                if (_treeNodeCtor == null)
                {
                    var t = Type.GetType("Microsoft.Testing.Platform.Requests.TreeNodeFilter, Microsoft.Testing.Platform");
                    if (t == null) { _treeNodeReflectionFailed = true; return true; }
                    _treeNodeCtor = t.GetConstructor(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        binder: null, types: new[] { typeof(string) }, modifiers: null);
                    _treeNodeMatchMethod = t.GetMethod("MatchesFilter");
                    if (_treeNodeCtor == null || _treeNodeMatchMethod == null)
                    {
                        _treeNodeReflectionFailed = true;
                        return true;
                    }
                }

                var instance = _treeNodeCtor!.Invoke(new object[] { glob });
                var bag = new PropertyBag();
                return (bool)_treeNodeMatchMethod!.Invoke(instance, new object[] { path, bag })!;
            }
            catch
            {
                // Invalid filter expression (e.g., `**/x` — `**` not in final
                // segment) is treated as a non-match. Same outcome as MTP at
                // runtime, just without crashing the host.
                return false;
            }
        }
    }
}
