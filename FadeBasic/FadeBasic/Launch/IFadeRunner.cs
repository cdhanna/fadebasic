using System;
using FadeBasic.Virtual;

namespace FadeBasic.Launch
{
    /// <summary>
    /// A running Fade program, whichever backend runs it. Hosts drive it one frame at a time:
    /// <see cref="Run"/> executes until the program suspends (e.g. `sync`) or ends.
    /// </summary>
    public interface IFadeRunner
    {
        /// <summary>Runs until the program suspends (true) or finishes (false).</summary>
        bool Run();

        bool IsComplete { get; }

        /// <summary>
        /// The VM commands see. For the VM backend it is the machine running the program; for the
        /// C# backend it is a host-context VM that executes nothing.
        /// </summary>
        VirtualMachine HostVm { get; }

        /// <summary>The first failed assert of a test run, or null.</summary>
        VirtualMachine.TestFailure AssertionFailure { get; }

        /// <summary>"vm" or "csharp".</summary>
        string BackendName { get; }
    }

    /// <summary>Runs bytecode on the <see cref="VirtualMachine"/>.</summary>
    public sealed class VmRunner : IFadeRunner
    {
        public VirtualMachine Machine { get; }

        public VmRunner(VirtualMachine machine)
        {
            Machine = machine;
        }

        public bool Run()
        {
            Machine.isSuspendRequested = false;
            while (!Machine.isSuspendRequested && Machine.instructionIndex < Machine.program.Length)
            {
                Machine.Execute2();
            }
            return !IsComplete;
        }

        public bool IsComplete => Machine.instructionIndex >= Machine.program.Length;
        public VirtualMachine HostVm => Machine;
        public VirtualMachine.TestFailure AssertionFailure => Machine.assertionFailure;
        public string BackendName => "vm";
    }

    /// <summary>A launchable built with <c>FadeBackend=CSharp</c>: it carries the compiled program too.</summary>
    public interface ICompiledLaunchable : ILaunchable
    {
        Compiled.CompiledFadeProgram CreateCompiledProgram();
    }

    public static class FadeRunners
    {
        /// <summary>Environment variable that picks the backend at runtime: "vm" or "csharp".</summary>
        public const string ENV_BACKEND = "FADE_BACKEND";

        /// <summary>
        /// Whether to run the compiled program: the launchable must carry one, and FADE_BACKEND
        /// must not say "vm".
        /// </summary>
        public static bool UseCompiled(ILaunchable launchable)
        {
            if (!(launchable is ICompiledLaunchable)) return false;
            if (!HasBytecode(launchable)) return true; // a FadeBackend=CSharp build has nothing else
            var env = Environment.GetEnvironmentVariable(ENV_BACKEND);
            return !string.Equals(env, "vm", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>False for a launchable built with FadeBackend=CSharp, which carries no bytecode.</summary>
        public static bool HasBytecode(ILaunchable launchable) =>
            launchable.Bytecode != null && launchable.Bytecode.Length > 0;

        /// <summary>A runner for the main program, on the backend <see cref="UseCompiled"/> picks.</summary>
        public static IFadeRunner CreateRunner(ILaunchable launchable, Action<VirtualMachine> configureVm = null)
        {
            if (UseCompiled(launchable))
            {
                return ((ICompiledLaunchable)launchable).CreateCompiledProgram();
            }
            var vm = new VirtualMachine(launchable.Bytecode)
            {
                hostMethods = HostMethodTable.FromCommandCollection(launchable.CommandCollection)
            };
            configureVm?.Invoke(vm);
            return new VmRunner(vm);
        }

        /// <summary>A runner positioned at a test's entry, in test mode. Null for an unknown test.</summary>
        public static IFadeRunner CreateTestRunner(ILaunchable launchable, TestManifestEntry entry,
            Action<VirtualMachine> configureVm = null)
        {
            if (UseCompiled(launchable))
            {
                var program = ((ICompiledLaunchable)launchable).CreateCompiledProgram();
                return program.StartTest(entry.name) ? program : null;
            }
            var vm = new VirtualMachine(launchable.Bytecode, entry.entryPointAddress)
            {
                hostMethods = HostMethodTable.FromCommandCollection(launchable.CommandCollection),
                isTestExecution = true
            };
            configureVm?.Invoke(vm);
            return new VmRunner(vm);
        }
    }
}
