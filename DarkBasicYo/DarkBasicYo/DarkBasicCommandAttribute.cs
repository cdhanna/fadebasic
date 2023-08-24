using System;
using DarkBasicYo.Virtual;

namespace DarkBasicYo.SourceGenerators
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class DarkBasicCommandAttribute : Attribute
    {
        public string Name { get; }

        public DarkBasicCommandAttribute(string name)
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
        public int address;
        public CommandArgRuntimeState state;
    } 
}