using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FadeBasic.SourceGenerators;
using FadeBasic.Virtual;

namespace FadeBasic
{
    
    
    public class CommandCollection
    {
        public readonly List<CommandInfo> Commands;
        public readonly List<IMethodSource> Sources;
        public readonly Dictionary<string, List<CommandInfo>> Lookup;

        public CommandCollection()
        {
            Commands = new List<CommandInfo>();
            Lookup = new Dictionary<string, List<CommandInfo>>();
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

            Lookup = new Dictionary<string, List<CommandInfo>>();
            foreach (var command in Commands)
            {
                var key = command.name.ToLowerInvariant();
                if (!Lookup.TryGetValue(key, out var entries))
                {
                    Lookup[key] = entries = new List<CommandInfo>();
                }
                entries.Add(command);
            }
        }
        
        public bool TryGetCommandDescriptor(FadeBasicCommandUsage usage, Token token, out List<CommandInfo> commandDescriptor)
        {
            
            var lookup = Regex.Replace(token.caseInsensitiveRaw.Trim(), "(\\s)+", " ");
            // var lookup = Regex.Replace(token.raw, "(\\s||\\t)*", " ");
            commandDescriptor = new List<CommandInfo>();
            if (Lookup.TryGetValue(lookup, out var matchName))
            {
                commandDescriptor = matchName.Where(x => x.usage.HasFlag(usage)).ToList();
                return commandDescriptor.Count > 0;
            }

            return false;
        }
    }

}