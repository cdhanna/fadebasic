using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DarkBasicYo
{

    public class CommandDescriptor
    {
        public string command;
        public List<ArgDescriptor> args = new List<ArgDescriptor>();

        public CommandDescriptor()
        {
        }

        public CommandDescriptor(string command, List<ArgDescriptor> args)
        {
            this.command = command;
            this.args = args;
        }

        public CommandDescriptor(string command, params ArgDescriptor[] args) : this(command, args.ToList())
        {

        }
    }

    public class CommandCollection
    {
        public readonly List<CommandDescriptor> Commands;
        public readonly Dictionary<string, CommandDescriptor> Lookup;

        public CommandCollection(List<CommandDescriptor> commands)
        {
            Commands = commands;
            Lookup = commands.ToDictionary(c => c.command);
        }

        public CommandCollection(params CommandDescriptor[] commands) : this(commands.ToList())
        {

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
        public LiteralType type;

        // public int arity = 1; // TODO: ? 
        // public bool optional; // TODO: ?

        public ArgDescriptor()
        {

        }

        public ArgDescriptor(string name, LiteralType type)
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