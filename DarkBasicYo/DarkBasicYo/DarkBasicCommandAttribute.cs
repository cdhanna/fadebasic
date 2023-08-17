using System;

namespace DarkBasicYo.SourceGenerators
{
    [AttributeUsage(AttributeTargets.Method)]
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
}