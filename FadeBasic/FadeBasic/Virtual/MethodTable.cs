using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FadeBasic.Virtual
{
    public interface IMethodSource
    {
        int Count { get; }

        CommandInfo[] Commands { get; }
        
        string CommandGroupName { get; }
        
        // bool TryRun(VirtualMachine vm, int methodIndex);
    }

    public delegate void CommandExecution(VirtualMachine vm);

    public enum CommandArgRuntimeState
    {
        Value,
        RegisterRef,
        HeapRef
    }
    
    [DebuggerDisplay("{name} ret[{returnType}]")]
    public struct CommandInfo
    {
        public string name;
        public string sig;
        public int methodIndex; // how to run this command.
        public CommandArgInfo[] args;
        public CommandExecution executor;
        public byte returnType;
        
        public string UniqueName => name + sig;
    }

    public struct CommandArgInfo
    {
        public byte typeCode;
        public bool isRef;
        public bool isOptional;
        public bool isVmArg;
        public bool isParams;
        public bool isRawArg;
    }
    
    
    public class MethodTableGroup
    {
        public List<IMethodSource> methodSources;

        // public void Run(VirtualMachine vm, int methodIndex)
        // {
        //     var offset = 0;
        //     for (var i = 0; i < methodSources.Count; i++)
        //     {
        //         var source = methodSources[i];
        //         if (source.TryRun(vm, methodIndex - offset))
        //         {
        //             return;
        //         }
        //
        //         offset += source.Count;
        //     }
        //     
        //     // if we get here, we didn't find the method!
        //     throw new Exception("Ah, we couldn't find a method for " + methodIndex);
        // }
    }
}