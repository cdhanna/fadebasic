using System;
using System.Diagnostics;
using System.Threading;
using FadeBasic.SourceGenerators;
using FadeBasic.Virtual;

namespace FadeBasic.Lib.Standard
{
    public partial class StandardCommands
    {
        private static Random _rand = new Random();
        private static DateTimeOffset _started = DateTimeOffset.Now;
        static StandardCommands()
        {
            
        }

        /// <summary>
        /// This command only exists to help attach a C# debugger to the program.
        /// This command will halt execution until a C# debugger is attached to the execution host. 
        /// </summary>
        /// <param name="vm"></param>
        [FadeBasicCommand("debug breakpoint")]
        public static void DebugBreakpoint([FromVm] VirtualMachine vm)
        {
            while (!Debugger.IsAttached)
            {
                Thread.Sleep(500);
            }
        }
        
        [FadeBasicCommand("test build", FadeBasicCommandUsage.Macro)]
        public static int BuildTest()
        {
            return 42;
        }
        
        [FadeBasicCommand("machine name$", FadeBasicCommandUsage.Both)]
        public static void GetMachineName(ref string machineName)
        {
            machineName = Environment.MachineName;
        }
        
        [FadeBasicCommand("randomize", FadeBasicCommandUsage.Both)]
        public static void RandomSeed(int seed)
        {
            _rand = new Random(seed);
        }
        
        [FadeBasicCommand("rnd", FadeBasicCommandUsage.Both)]
        public static int Random(int max)
        {
            return _rand.Next(max);
        }
        
        [FadeBasicCommand("timer", FadeBasicCommandUsage.Both)]
        public static long Timer()
        {
            var now = DateTimeOffset.Now;
            var delta = now - _started;
            return (long)delta.TotalMilliseconds;
        }
        
        [FadeBasicCommand("inc", FadeBasicCommandUsage.Both)]
        public static void Increment(ref int value, int amount = 1)
        {
            value += amount;
        }
                
        [FadeBasicCommand("dec", FadeBasicCommandUsage.Both)]
        public static void Decrement(ref int value, int amount = 1)
        {
            value -= amount;
        }
    }
}