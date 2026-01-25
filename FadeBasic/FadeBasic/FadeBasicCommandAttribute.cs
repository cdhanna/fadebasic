using System;
using FadeBasic.Virtual;

namespace FadeBasic.SourceGenerators
{
    [Flags]
    public enum FadeBasicCommandUsage
    {
        // commands are able to execute during the runtime
        Runtime = 1 << 0, // 1
        
        // commands are able to execute during the compile time stage
        Macro = 1 << 1, // 2
        
        // both macro and runtime
        Both = Runtime | Macro
    }

    public static class FadeBasicCommandUsageUtil
    {
        public static FadeBasicCommandUsage Parse(string str)
        {
            switch (str)
            {
                case nameof(FadeBasicCommandUsage.Runtime):
                case "1":
                case "runtime":
                    return FadeBasicCommandUsage.Runtime;
                
                case nameof(FadeBasicCommandUsage.Macro):
                case "2":
                case "macro":
                    return FadeBasicCommandUsage.Macro;
                
                case nameof(FadeBasicCommandUsage.Both):
                case "3":
                case "both":
                    return FadeBasicCommandUsage.Both;
            }

            throw new NotImplementedException("unknown fade command usage:" + str);
        }
    }
    
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class FadeBasicCommandAttribute : Attribute
    {
        public string Name { get; }
        public FadeBasicCommandUsage Usage { get; }

        public FadeBasicCommandAttribute(string name)
        {
            Name = name;
            Usage = FadeBasicCommandUsage.Runtime;
        }

        public FadeBasicCommandAttribute(string name, FadeBasicCommandUsage usage)
        {
            Name = name;
            Usage = usage;
        }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public class FromVmAttribute : Attribute
    {
        
    }

    public struct RawArg<T>
    {
        public T value;
        public ulong address;
        public CommandArgRuntimeState state;
    } 
}