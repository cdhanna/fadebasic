using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DarkBasicYo.Ast;

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
                var arg = new ArgDescriptor
                {
                    // TODO: get name and other optional info
                    name = parameter.Name,
                    type = VariableType.Integer,
                    isRef = parameter.ParameterType.IsByRef,
                    isOptional = parameter.IsOptional
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
        public readonly List<CommandDescriptor> Commands;
        public readonly Dictionary<string, CommandDescriptor> Lookup;

        // public CommandCollection(List<CommandDescriptor> commands)
        // {
        //     Commands = commands;
        //     Lookup = commands.ToDictionary(c => c.command);
        // }
        //
        // public CommandCollection(params CommandDescriptor[] commands) : this(commands.ToList())
        // {
        //
        // }

        public CommandCollection(params Type[] commandClasses)
        {
            Commands = new List<CommandDescriptor>();
            foreach (var commandClass in commandClasses)
            {
                var methods = commandClass.GetMethods(BindingFlags.Static | BindingFlags.Public);
                foreach (var method in methods)
                {
                    var nameAttr = method.GetCustomAttribute<CommandNameAttribute>();
                    if (nameAttr != null)
                    {
                        Commands.Add(new CommandDescriptor(method));
                    }
                }
            }
            Lookup = Commands.ToDictionary(c => c.command.ToLowerInvariant());

        }

        public bool TryGetCommandDescriptor(Token token, out CommandDescriptor commandDescriptor)
        {
            var lookup = Regex.Replace(token.raw, "(\\s||\\t)*", "");
            return Lookup.TryGetValue(lookup, out commandDescriptor);
        }
    }

    public class ArgDescriptor
    {
        public string name;
        public VariableType type;
        public bool isRef;
        public bool isOptional;

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

    [Flags]
    public enum LiteralType
    {
        Integer = 1, // 00001
        Real = 2, // 00010
        String = 4, // 00100
        Any = 7, // 00111
        Numeric = 3 // 00011
    }
}