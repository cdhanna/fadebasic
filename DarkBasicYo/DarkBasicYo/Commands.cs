using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DarkBasicYo.Virtual;

namespace DarkBasicYo
{

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
        
        public bool TryGetCommandDescriptor(Token token, out CommandInfo commandDescriptor)
        {
            
            var lookup = Regex.Replace(token.raw, "(\\s)+", " ");
            // var lookup = Regex.Replace(token.raw, "(\\s||\\t)*", " ");
            return Lookup.TryGetValue(lookup, out commandDescriptor);
        }
    }

}