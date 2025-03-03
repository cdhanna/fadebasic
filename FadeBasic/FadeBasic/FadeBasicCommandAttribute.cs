using System;
using FadeBasic.Virtual;

namespace FadeBasic.SourceGenerators
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class FadeBasicCommandAttribute : Attribute
    {
        public string Name { get; }

        public FadeBasicCommandAttribute(string name)
        {
            Name = name;
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