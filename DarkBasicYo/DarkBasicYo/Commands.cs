using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DarkBasicYo.Ast;
using DarkBasicYo.Virtual;

namespace DarkBasicYo
{

    [AttributeUsage(AttributeTargets.Method)]
    public class CommandNameAttribute : Attribute
    {
        public string Name { get; set; }

        public CommandNameAttribute(string name)
        {
            Name = name;
        }
    }

    [Serializable]
    public struct CommandArgObject
    {
        public byte[] bytes;
        public byte typeCode;
    }
    
    public class CommandDescriptor
    {
        public string command;
        public List<ArgDescriptor> args = new List<ArgDescriptor>();
        public MethodInfo method;

        // public CommandDescriptor()
        // {
        // }

        public CommandDescriptor(MethodInfo methodInfo)
        {
            method = methodInfo;
            command = method.GetCustomAttribute<CommandNameAttribute>()?.Name;
            if (string.IsNullOrEmpty(command))
            {
                throw new Exception(
                    $"Custom command: name must be non null string. Use {nameof(CommandNameAttribute)} on method");
            }

            var parameters = method.GetParameters();
            args = new List<ArgDescriptor>();
            foreach (var parameter in parameters)
            {
                if (typeof(VirtualMachine).IsAssignableFrom(parameter.ParameterType ) )
                {
                    continue;
                }
                var arg = new ArgDescriptor
                {
                    // TODO: get name and other optional info
                    name = parameter.Name,
                    type = VariableType.Integer,
                    isRef = parameter.ParameterType.IsByRef,
                    isOptional = parameter.IsOptional,
                    // isVmArg = parameter.ParameterType == typeof(VirtualMachine)
                };
                args.Add(arg);
            }
        }

        // public CommandDescriptor(string command, List<ArgDescriptor> args)
        // {
        //     this.command = command;
        //     this.args = args;
        // }
        //
        // public CommandDescriptor(string command, params ArgDescriptor[] args) : this(command, args.ToList())
        // {
        //
        // }
    }

    public class CommandCollection
    {
        public readonly List<CommandInfo> Commands;
        public readonly List<IMethodSource> Sources;
        public readonly Dictionary<string, CommandInfo> Lookup;

        public CommandCollection()
        {
            Commands = new List<CommandInfo>();
            Lookup = new Dictionary<string, CommandInfo>();
            Sources = new List<IMethodSource>();
        }
       
        public CommandCollection(params IMethodSource[] sources)
        {
            Commands = new List<CommandInfo>();
            Sources = sources.ToList();
            foreach (var source in sources)
            {
                Commands.AddRange(source.Commands);
            }
            Lookup = Commands.ToDictionary(c => c.name.ToLowerInvariant());
        }
        
        // public static implicit operator CommandCollection(CommandInfo[] commands)
        // {
        //     return new CommandCollection(commands);
        // }
        
        // public CommandCollection(params Type[] commandClasses)
        // {
        //     Commands = new List<CommandDescriptor>();
        //     foreach (var commandClass in commandClasses)
        //     {
        //         var methods = commandClass.GetMethods(BindingFlags.Static | BindingFlags.Public);
        //         foreach (var method in methods)
        //         {
        //             var nameAttr = method.GetCustomAttribute<CommandNameAttribute>();
        //             if (nameAttr != null)
        //             {
        //                 Commands.Add(new CommandDescriptor(method));
        //             }
        //         }
        //     }
        //     Lookup = Commands.ToDictionary(c => c.command.ToLowerInvariant());
        //
        // }

        public bool TryGetCommandDescriptor(Token token, out CommandInfo commandDescriptor)
        {
            
            var lookup = Regex.Replace(token.raw, "(\\s)+", " ");
            // var lookup = Regex.Replace(token.raw, "(\\s||\\t)*", " ");
            return Lookup.TryGetValue(lookup, out commandDescriptor);
        }
    }

    public class ArgDescriptor
    {
        public string name;
        public VariableType type;
        public bool isRef;
        public bool isOptional;

        public bool isVmArg;
        // public int arity = 1; // TODO: ? 
        // public bool optional; // TODO: ?

        public ArgDescriptor()
        {

        }

        public ArgDescriptor(string name, VariableType type)
        {
            this.name = name;
            this.type = type;
        }
    }
}