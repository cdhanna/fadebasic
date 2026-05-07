using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FadeBasic.Sdk;
using FadeBasic.Virtual;

namespace FadeBasic.Launch
{
    public class LaunchOptions
    {
        public const string ENV_ENABLE_DEBUG = "FADE_BASIC_DEBUG";
        public const string ENV_ENABLE_DEBUG_DONT_WAIT = "FADE_BASIC_DEBUG_DONT_WAIT";
        public const string ENV_DEBUG_PORT = "FADE_BASIC_DEBUG_PORT";
        public const string ENV_DEBUG_LOG_PATH = "FADE_BASIC_DEBUG_LOG_PATH";
        
        
        public bool debug;
        public int debugPort = 0;
        public bool debugWaitForConnection = true;
        public string debugLogPath;
        

        public static readonly LaunchOptions DefaultOptions;
        static LaunchOptions()
        {
            var debugEnv = Environment.GetEnvironmentVariable(ENV_ENABLE_DEBUG)?.ToLowerInvariant();
            var debugDontWait = Environment.GetEnvironmentVariable(ENV_ENABLE_DEBUG_DONT_WAIT)?.ToLowerInvariant();
            DefaultOptions = new LaunchOptions
            {
                debug = debugEnv == "true" || debugEnv == "1",
                debugPort = 0,
                debugWaitForConnection = !(debugDontWait == "true" || debugDontWait == "1"),
                debugLogPath = Environment.GetEnvironmentVariable(ENV_DEBUG_LOG_PATH)
            };

            if (!int.TryParse(Environment.GetEnvironmentVariable(ENV_DEBUG_PORT), out DefaultOptions.debugPort))
            {
                DefaultOptions.debugPort = LaunchUtil.FreeTcpPort();
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

            var vm = new VirtualMachine(instance.Bytecode)
            {
                hostMethods = HostMethodTable.FromCommandCollection(instance.CommandCollection)
            };

            if (!options.debug)
            {
                vm.Execute2(0); // 0 means run until suspend.
            }
            else
            {
                var session = new DebugSession(vm, instance.DebugData, instance.CommandCollection, options);
                machineToDebugTable.Add(vm, (instance, session));
                session.StartServer();
                session.DebugForever(); // needs infinite budget.
                session.ShutdownServer();

            }

        }

        // Args parsing: recognized command-line forms.
        public const string ArgFadeTest = "--fade-test";
        public const string ArgFadeListTests = "--fade-list-tests";
        public const string ArgFadeTestAll = "--fade-test-all";

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
            if (args != null && args.Length > 0)
            {
                if (TryDispatchTestArgs(instance, args, out var exitCode))
                {
                    return exitCode;
                }
            }
            Run(instance, options);
            return 0;
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