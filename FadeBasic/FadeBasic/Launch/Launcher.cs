using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FadeBasic.Ast;
using FadeBasic.Sdk;
using FadeBasic.Virtual;
using FadeBasic.Virtual.HotReload;

namespace FadeBasic.Launch
{
    public class LaunchOptions
    {
        public const string ENV_ENABLE_DEBUG = "FADE_BASIC_DEBUG";
        public const string ENV_ENABLE_DEBUG_DONT_WAIT = "FADE_BASIC_DEBUG_DONT_WAIT";
        public const string ENV_DEBUG_PORT = "FADE_BASIC_DEBUG_PORT";
        public const string ENV_DEBUG_LOG_PATH = "FADE_BASIC_DEBUG_LOG_PATH";
        // Hot-reload watch: path to the .fbasic source to watch + recompile
        // in-process. Implies debug DATA (we recompile with it) but NOT the
        // debug SERVER — see Launcher.RunWithWatch.
        public const string ENV_WATCH = "FADE_BASIC_WATCH";
        // When set ("true"/"1"), the watch runs silently — no [fade-watch] info
        // logs (banner / reload verdicts). Genuine errors still print.
        public const string ENV_WATCH_QUIET = "FADE_BASIC_WATCH_QUIET";

        // GC tuning. Mirror the fade.json `settings.gc` block; also readable
        // from env for the desktop/CLI runner. sweepInterval: heap allocations
        // between garbage collections (higher = fewer collections, more memory;
        // 0/unset keeps the VM default). paranoid: poison freed memory and never
        // reuse it, so a use-after-free surfaces immediately — a diagnostic knob
        // for hunting GC-liveness bugs, not for shipping.
        public const string ENV_GC_SWEEP = "FADE_BASIC_GC_SWEEP";
        public const string ENV_GC_PARANOID = "FADE_BASIC_GC_PARANOID";


        public bool debug;
        public int debugPort = 0;
        public bool debugWaitForConnection = true;
        public string debugLogPath;

        /// <summary>Heap allocations between garbage collections. 0 = leave the
        /// VM default (<see cref="VirtualMachine.DEFAULT_SWEEP_INTERVAL"/>).</summary>
        public int gcSweepInterval = 0;
        /// <summary>Poison freed heap memory and never reuse it (diagnostic).</summary>
        public bool gcParanoid;

        /// <summary>Apply the GC knobs to a freshly-constructed VM. No-ops the
        /// sweep interval when unset (0) so callers keep the VM default.</summary>
        public void ApplyGc(VirtualMachine vm)
        {
            if (gcSweepInterval > 0) vm.sweepInterval = gcSweepInterval;
            vm.heap.paranoid = gcParanoid;
        }

        /// <summary>Hot-reload watch is enabled.</summary>
        public bool watch;
        /// <summary>
        /// What to watch: a single .fbasic file, a directory (all *.fbasic under
        /// it, joined), or null/empty → the current working directory.
        /// </summary>
        public string watchPath;
        /// <summary>Suppress [fade-watch] info logs; reload happens silently.</summary>
        public bool watchQuiet;

        public LaunchOptions Clone() => (LaunchOptions)MemberwiseClone();


        public static readonly LaunchOptions DefaultOptions;
        static LaunchOptions()
        {
            // Best-effort: in WASM there are no env vars / TCP sockets, and
            // any throw here gets wrapped in a TypeInitializationException
            // for every later access to ANY LaunchOptions field. Swallow
            // failures so the type stays usable.
            DefaultOptions = new LaunchOptions
            {
                debug = false,
                debugPort = 0,
                debugWaitForConnection = true,
                debugLogPath = null,
            };
            try
            {
                var debugEnv = Environment.GetEnvironmentVariable(ENV_ENABLE_DEBUG)?.ToLowerInvariant();
                var debugDontWait = Environment.GetEnvironmentVariable(ENV_ENABLE_DEBUG_DONT_WAIT)?.ToLowerInvariant();
                DefaultOptions.debug = debugEnv == "true" || debugEnv == "1";
                DefaultOptions.debugWaitForConnection = !(debugDontWait == "true" || debugDontWait == "1");
                DefaultOptions.debugLogPath = Environment.GetEnvironmentVariable(ENV_DEBUG_LOG_PATH);
                // FADE_BASIC_WATCH: "true"/"1" → watch the cwd; any other value →
                // a file or directory path; unset → no watch.
                var watchEnv = Environment.GetEnvironmentVariable(ENV_WATCH);
                if (!string.IsNullOrEmpty(watchEnv))
                {
                    DefaultOptions.watch = true;
                    var lower = watchEnv.ToLowerInvariant();
                    DefaultOptions.watchPath = (lower == "true" || lower == "1") ? null : watchEnv;
                }
                var quietEnv = Environment.GetEnvironmentVariable(ENV_WATCH_QUIET)?.ToLowerInvariant();
                DefaultOptions.watchQuiet = quietEnv == "true" || quietEnv == "1";

                int.TryParse(Environment.GetEnvironmentVariable(ENV_GC_SWEEP), out DefaultOptions.gcSweepInterval);
                var paranoidEnv = Environment.GetEnvironmentVariable(ENV_GC_PARANOID)?.ToLowerInvariant();
                DefaultOptions.gcParanoid = paranoidEnv == "true" || paranoidEnv == "1";

                if (!int.TryParse(Environment.GetEnvironmentVariable(ENV_DEBUG_PORT), out DefaultOptions.debugPort))
                {
                    DefaultOptions.debugPort = LaunchUtil.FreeTcpPort();
                }
            }
            catch
            {
                // Browser / sandboxed environment — DefaultOptions retains
                // the safe defaults set above.
            }
        }

    }
    
    public static class Launcher
    {
        // public static bool IsDebugMode => Environment.GetEnvironmentVariable("FADE_BASIC_DEBUG")
        //

        public static Dictionary<VirtualMachine, (ILaunchable, DebugSession)> machineToDebugTable =
            new Dictionary<VirtualMachine, (ILaunchable, DebugSession)>();
        
        public static void Run<T>(LaunchOptions options=null) 
            where T : ILaunchable, new()
        {
            Run<T>(new T(), options);
        }
        
        public static void Run<T>(T instance, LaunchOptions options=null)
            where T : ILaunchable
        {
            options ??= LaunchOptions.DefaultOptions;

            // Headless watch (no debugger): the standalone hot-reload loop.
            if (options.watch && !options.debug)
            {
                RunWithWatch(instance, options);
                return;
            }

            // Plain run (no debugger, no watch): just execute the baked bytecode.
            if (!options.debug)
            {
                var runVm = new VirtualMachine(instance.Bytecode)
                {
                    hostMethods = HostMethodTable.FromCommandCollection(instance.CommandCollection)
                };
                options.ApplyGc(runVm);
                runVm.Execute2(0); // 0 means run until suspend.
                return;
            }

            // Debug path. When --fade-watch is ALSO set, run an in-process-compiled
            // program (so the reload facts share a statement map with the bytecode
            // we actually run) and hand the armed HotReloadSession to the debug
            // session — DebugForever applies edits at a safepoint and rebinds via
            // RestartAfterReload, keeping the debugger attached (see DebugSession).
            VirtualMachine vm;
            DebugData debugData;
            CommandCollection commands;
            ReloadWatch reloadWatch = options.watch ? BuildReloadWatch(instance, options) : null;
            if (reloadWatch != null)
            {
                vm = reloadWatch.Vm;
                debugData = reloadWatch.Compiler.DebugData;
                commands = reloadWatch.Collection;
            }
            else
            {
                vm = new VirtualMachine(instance.Bytecode)
                {
                    hostMethods = HostMethodTable.FromCommandCollection(instance.CommandCollection)
                };
                debugData = instance.DebugData;
                commands = instance.CommandCollection;
            }
            options.ApplyGc(vm);

            var session = new DebugSession(vm, debugData, commands, options);
            session.HotReload = reloadWatch?.Session; // null → debug without reload
            machineToDebugTable.Add(vm, (instance, session));
            session.StartServer();
            try
            {
                session.DebugForever(); // needs infinite budget.
            }
            finally
            {
                session.ShutdownServer();
                if (reloadWatch != null)
                    foreach (var fw in reloadWatch.Watchers) fw.Dispose();
            }
        }

        /// <summary>
        /// Headless hot-reload watch. Recompiles the watched source in-process
        /// WITH debug data (so the reload machinery has a statement map, no matter
        /// how the assembly was built), runs it in a safepoint-aware loop, and
        /// applies edits live via <see cref="HotReloadSession"/>. No debug server
        /// and no connection wait — this is the "debug DATA, not debug SERVER" path.
        ///
        /// v1: single-file watch. Multi-file projects are a follow-up.
        /// </summary>
        /// <summary>
        /// A configured hot-reload watch: an in-process-compiled VM plus the armed
        /// <see cref="HotReloadSession"/> and the file watchers feeding it. Shared
        /// by the headless watch (<see cref="RunWithWatch"/>) and the debug+watch
        /// path (<see cref="Run{T}"/>). <c>null</c> is returned on initial compile
        /// error.
        /// </summary>
        internal sealed class ReloadWatch
        {
            public VirtualMachine Vm;
            public Compiler Compiler;
            public CommandCollection Collection;
            public HotReloadSession Session;
            public List<FileSystemWatcher> Watchers = new List<FileSystemWatcher>();
            public string Label;
        }

        /// <summary>
        /// Resolve the watched source set, compile it in-process WITH debug data
        /// (so the reload machinery — and any attached debug session — share a
        /// statement map that matches the bytecode we actually run), build the VM +
        /// <see cref="HotReloadSession"/>, and install FileSystemWatchers that arm
        /// the session on edits. Returns <c>null</c> and logs on compile error.
        /// </summary>
        static ReloadWatch BuildReloadWatch<T>(T instance, LaunchOptions options)
            where T : ILaunchable
        {
            var collection = instance.CommandCollection;

            // Prefer the exact source set the program was built from (paths baked
            // into the launchable), recomposed via the SAME join the build uses
            // (SourceMap.CreateSourceMap). This gives multi-file parity with a
            // normal `dotnet run` and removes the wrong-path footgun. Falls back
            // to an explicit path / cwd only when the launchable can't tell us.
            var inferred = (instance as IWatchableLaunchable)?.SourceFiles?
                .Where(File.Exists).ToList();

            Func<string> compose;
            var watcherSpecs = new List<(string dir, string filter, bool recursive)>();
            string label;

            if (inferred != null && inferred.Count > 0)
            {
                var files = inferred;
                compose = () => SourceMap.CreateSourceMap(files).fullSource;
                foreach (var d in files.Select(f => Path.GetDirectoryName(Path.GetFullPath(f)))
                             .Where(d => !string.IsNullOrEmpty(d)).Distinct())
                    watcherSpecs.Add((d, "*.fbasic", false));
                label = $"{files.Count} built source file(s)";
            }
            else if (!string.IsNullOrEmpty(options.watchPath) && Directory.Exists(Path.GetFullPath(options.watchPath))
                     || string.IsNullOrEmpty(options.watchPath))
            {
                var dir = string.IsNullOrEmpty(options.watchPath)
                    ? Directory.GetCurrentDirectory() : Path.GetFullPath(options.watchPath);
                compose = () => ComposeDirectory(dir);
                watcherSpecs.Add((dir, "*.fbasic", true));
                label = $"*.fbasic under {dir}";
            }
            else
            {
                var file = Path.GetFullPath(options.watchPath);
                if (!File.Exists(file))
                {
                    Console.Error.WriteLine($"[fade-watch] path not found: {file}");
                    return null;
                }
                compose = () => SafeRead(file);
                watcherSpecs.Add((Path.GetDirectoryName(file), Path.GetFileName(file), false));
                label = file;
            }

            var initialSource = compose();
            if (!TryCompileSource(initialSource, collection, out var compiler, out var compileError))
            {
                Console.Error.WriteLine($"[fade-watch] initial compile failed:\n{compileError}");
                return null;
            }

            var vm = new VirtualMachine(compiler.Program)
            {
                hostMethods = HostMethodTable.FromCommandCollection(collection)
            };
            var facts = ProgramFacts.FromCompiler(compiler);
            var session = new HotReloadSession(vm, facts, src =>
            {
                if (!TryCompileSource(src, collection, out var c, out var err))
                    throw new Exception(err);
                return c;
            });

            var result = new ReloadWatch
            {
                Vm = vm, Compiler = compiler, Collection = collection, Session = session, Label = label,
            };

            // Dedupe: only arm when the composed source actually differs from what
            // we last armed. Holding ctrl+s (or the editor firing several events
            // per save) then produces no reload churn.
            var armLock = new object();
            var lastArmed = initialSource;
            void OnChanged(object _, FileSystemEventArgs __)
            {
                string next;
                try { next = compose(); }
                catch { return; /* transient IO while the editor writes; next event catches it */ }
                lock (armLock)
                {
                    if (string.Equals(next, lastArmed)) return; // no real change
                    lastArmed = next;
                }
                session.Arm(next);
            }
            foreach (var (dir, filter, recursive) in watcherSpecs)
            {
                var fw = new FileSystemWatcher(dir, filter)
                {
                    IncludeSubdirectories = recursive,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                fw.Changed += OnChanged;
                fw.Created += OnChanged;
                fw.Deleted += OnChanged;
                fw.Renamed += (o, e) => OnChanged(o, e);
                result.Watchers.Add(fw);
            }

            return result;
        }

        /// <summary>
        /// Headless hot-reload watch. Recompiles the watched source in-process WITH
        /// debug data, runs it in a safepoint-aware loop, and applies edits live via
        /// <see cref="HotReloadSession"/>. No debug server and no connection wait —
        /// this is the "debug DATA, not debug SERVER" path. For debug + watch (a
        /// debugger attached), see the debug branch of <see cref="Run{T}"/>.
        ///
        /// v1: single-file / inferred-source watch. Multi-file projects are covered
        /// via IWatchableLaunchable.SourceFiles.
        /// </summary>
        static void RunWithWatch<T>(T instance, LaunchOptions options)
            where T : ILaunchable
        {
            var setup = BuildReloadWatch(instance, options);
            if (setup == null) return;

            var vm = setup.Vm;
            var session = setup.Session;
            bool quiet = options.watchQuiet;
            void Info(string msg) { if (!quiet) Console.WriteLine(msg); }

            try
            {
                Info($"[fade-watch] watching {setup.Label} — save to hot-reload.");

                // Run the program; suspend at a STATEMENT boundary whenever a
                // reload is armed so the control gate evaluates at a clean
                // safepoint (offset 0).
                while (vm.instructionIndex < vm.program.Length
                       && vm.error.type == VirtualRuntimeErrorType.NONE)
                {
                    vm.Execute2(256, ins =>
                        session.HasPending
                        && HotReloadUtil.StatementStartForInstruction(session.CurrentFacts, ins) == ins);

                    if (!session.HasPending) continue;

                    try
                    {
                        var plan = session.Tick();
                        Info($"[fade-watch] {DescribePlan(plan, session)}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[fade-watch] reload rejected: {ex.Message}");
                        session.Cancel();
                    }
                }

                if (vm.error.type != VirtualRuntimeErrorType.NONE)
                    Console.Error.WriteLine($"[fade-watch] runtime error: {vm.error.message}");
                Info("[fade-watch] program finished.");
            }
            finally
            {
                foreach (var fw in setup.Watchers) fw.Dispose();
            }
        }

        static string SafeRead(string path)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return File.ReadAllText(path); }
                catch (IOException) when (attempt < 5) { Thread.Sleep(20); }
            }
        }

        // Join every *.fbasic under a directory into one program. Deterministic
        // order: any file named main.fbasic first, then the rest by full path.
        // (v1 heuristic — a real multi-file project's fade.json `sources` order
        // would be more precise; noted in the design doc.)
        public static string ComposeDirectory(string dir)
        {
            var files = Directory.GetFiles(dir, "*.fbasic", SearchOption.AllDirectories);
            Array.Sort(files, (a, b) =>
            {
                bool am = string.Equals(Path.GetFileName(a), "main.fbasic", StringComparison.OrdinalIgnoreCase);
                bool bm = string.Equals(Path.GetFileName(b), "main.fbasic", StringComparison.OrdinalIgnoreCase);
                if (am != bm) return am ? -1 : 1;
                return string.CompareOrdinal(a, b);
            });

            var sb = new System.Text.StringBuilder();
            foreach (var f in files)
            {
                sb.Append(SafeRead(f));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        static string DescribePlan(ReconcilePlan plan, HotReloadSession session)
        {
            switch (plan.Verdict)
            {
                case Verdict.ApplicableNow: return "reloaded";
                case Verdict.NoChange: return "no change";
                case Verdict.PendingTransient:
                    var lines = plan.BlockingStatements
                        .Select(s => LineOf(session.CurrentFacts, s))
                        .Where(l => l >= 0).Distinct().OrderBy(l => l).ToList();
                    var where = lines.Count > 0 ? $" (active near source line {string.Join(",", lines.Select(l => l + 1))})" : "";
                    return $"pending — waiting for active code to finish{where}";
                case Verdict.PermanentlyRude: return $"cannot hot-reload: {plan.RudeReason} — restart required";
                default: return plan.Verdict.ToString();
            }
        }

        static int LineOf(ProgramFacts facts, int stmtStart)
        {
            if (facts?.Debug == null) return -1;
            foreach (var t in facts.Debug.statementTokens)
                if (t.insIndex == stmtStart && t.token != null) return t.token.lineNumber;
            return -1;
        }

        /// <summary>
        /// Lex/parse/compile source in-process WITH debug data. Returns false and
        /// a human-readable error string on parse errors (never throws for those).
        /// </summary>
        public static bool TryCompileSource(string src, CommandCollection collection, out Compiler compiler, out string error)
        {
            compiler = null;
            error = null;
            var lexer = new Lexer();
            var tokens = lexer.Tokenize(src, collection);
            var parser = new Parser(new TokenStream(tokens), collection);
            var ast = parser.ParseProgram();
            var errors = ast.GetAllErrors();
            if (errors.Count > 0)
            {
                error = string.Join("\n", errors.Select(e => e.Display));
                return false;
            }
            compiler = new Compiler(collection, new CompilerOptions { GenerateDebugData = true });
            compiler.Compile(ast);
            return true;
        }

        // Args parsing: recognized command-line forms.
        public const string ArgFadeTest = "--fade-test";
        public const string ArgFadeListTests = "--fade-list-tests";
        public const string ArgFadeTestAll = "--fade-test-all";
        public const string ArgFadeWatch = "--fade-watch";

        /// <summary>
        /// Console-app entry point that dispatches between normal program
        /// execution and the test runner based on <paramref name="args"/>.
        /// Returns the process exit code (0 = success, 1 = failure or no tests
        /// found, 2 = unsupported launchable).
        /// Recognized flags:
        /// <list type="bullet">
        /// <item><c>--fade-test=name</c> — run a single test, exit 0/1 on pass/fail.</item>
        /// <item><c>--fade-test-all</c> — run all tests, exit 0/1 on all-pass / any-fail.</item>
        /// <item><c>--fade-list-tests</c> — print test names (one per line) and exit.</item>
        /// </list>
        /// With no recognized flag, falls through to normal program execution.
        /// </summary>
        public static int Main<T>(string[] args, LaunchOptions options=null)
            where T : ILaunchable, new()
        {
            return Main(new T(), args, options);
        }

        public static int Main<T>(T instance, string[] args, LaunchOptions options=null)
            where T : ILaunchable
        {
            options ??= LaunchOptions.DefaultOptions;
            if (args != null && args.Length > 0)
            {
                if (TryDispatchTestArgs(instance, args, out var exitCode))
                {
                    return exitCode;
                }
                if (TryGetWatchArg(args, out var watchPath))
                {
                    options = options.Clone();
                    options.watch = true;
                    options.watchPath = watchPath; // may be null → cwd
                }
            }
            Run(instance, options);
            return 0;
        }

        // Recognizes `--fade-watch`, `--fade-watch <path>`, and `--fade-watch=<path>`.
        // A bare `--fade-watch` (no path, or followed by another flag) enables
        // watch with a null path, which RunWithWatch resolves to the cwd.
        static bool TryGetWatchArg(string[] args, out string path)
        {
            path = null;
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == ArgFadeWatch)
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-")) path = args[i + 1];
                    return true;
                }
                if (a.StartsWith(ArgFadeWatch + "=")) { path = a.Substring(ArgFadeWatch.Length + 1); return true; }
            }
            return false;
        }

        // Returns true if args contain a recognized test-runner flag, in which
        // case `exitCode` is set. Returns false if no test flag matched (caller
        // should fall through to normal program execution).
        public static bool TryDispatchTestArgs(ILaunchable instance, string[] args, out int exitCode)
        {
            exitCode = 0;
            string testName = null;
            var testAll = false;
            var listTests = false;

            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == ArgFadeListTests) { listTests = true; continue; }
                if (a == ArgFadeTestAll) { testAll = true; continue; }
                if (a == ArgFadeTest && i + 1 < args.Length)
                {
                    testName = args[++i];
                    continue;
                }
                if (a.StartsWith(ArgFadeTest + "="))
                {
                    testName = a.Substring(ArgFadeTest.Length + 1);
                    continue;
                }
            }

            if (!listTests && !testAll && testName == null) return false;

            if (!(instance is ITestLaunchable testInstance))
            {
                Console.Error.WriteLine(
                    "fade: this program does not expose a test manifest "
                    + "(implement ITestLaunchable to enable --fade-test).");
                exitCode = 2;
                return true;
            }

            var hostMethods = HostMethodTable.FromCommandCollection(testInstance.CommandCollection);

            if (listTests)
            {
                foreach (var t in testInstance.TestManifest)
                {
                    if (t.isAbstract) continue;
                    Console.WriteLine(t.name);
                }
                exitCode = 0;
                return true;
            }

            if (testAll)
            {
                exitCode = RunManyAndReport(testInstance.TestManifest, testInstance.Bytecode, hostMethods);
                return true;
            }

            // Single test by name.
            var match = testInstance.TestManifest
                .FirstOrDefault(t => string.Equals(t.name, testName, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                Console.Error.WriteLine($"fade: no test named `{testName}` was found.");
                exitCode = 1;
                return true;
            }
            var result = FadeTestExecutor.RunTest(testInstance.Bytecode, hostMethods, match);
            ReportResult(result);
            exitCode = result.passed ? 0 : 1;
            return true;
        }

        static int RunManyAndReport(IReadOnlyList<TestManifestEntry> manifest, byte[] bytecode, HostMethodTable hostMethods)
        {
            var passed = 0;
            var failed = 0;
            foreach (var entry in manifest)
            {
                if (entry.isAbstract) continue;
                var r = FadeTestExecutor.RunTest(bytecode, hostMethods, entry);
                ReportResult(r);
                if (r.passed) passed++; else failed++;
            }
            Console.WriteLine($"fade: {passed} passed, {failed} failed.");
            return failed == 0 && (passed + failed) > 0 ? 0 : 1;
        }

        static void ReportResult(FadeTestResult r)
        {
            if (r.passed)
            {
                Console.WriteLine($"  PASS  {r.testName}  ({r.duration.TotalMilliseconds:F1} ms)");
            }
            else
            {
                Console.WriteLine($"  FAIL  {r.testName}  ({r.duration.TotalMilliseconds:F1} ms)");
                if (!string.IsNullOrEmpty(r.failureMessage))
                {
                    Console.WriteLine("        " + r.failureMessage);
                }
            }
        }
    }
}