using System;
using System.Collections.Generic;

namespace DarkBasicYo.Virtual
{
    public interface IMethodSource
    {
        int Count { get; }

        CommandInfo[] Commands { get; }
        
        // bool TryRun(VirtualMachine vm, int methodIndex);
    }

    public delegate void CommandExecution(VirtualMachine vm);

    public enum CommandArgRuntimeState
    {
        Value,
        RegisterRef,
        HeapRef
    }
    
    public struct CommandInfo
    {
        public string name;
        public int methodIndex; // how to run this command.
        public CommandArgInfo[] args;
        public CommandExecution executor;
    }

    public struct CommandArgInfo
    {
        public byte typeCode;
        public bool isRef;
        public bool isOptional;
        public bool isVmArg;
        public int xyz;
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