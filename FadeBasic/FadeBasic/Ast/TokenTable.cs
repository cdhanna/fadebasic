using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Ast
{
    
    public class TokenTable<T>
    {
        public List<Entry> entries = new List<Entry>();
        public void Add(Entry entry)
        {
            entries.Add(entry);
        }

        public bool TryFindEntry(int linePosition, int charPosition, out Entry found)
        {
            var fakeToken = new Token
            {
                lineNumber = linePosition, charNumber = charPosition
            };
            return TryFindEntry(fakeToken, out found);
        }
        public bool TryFindEntry(Token token, out Entry found)
        {
            var foundEntries = new List<Entry>();
            
            // TODO: replace this with a tree lookup, or something.
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (Token.IsLocationBefore(token, entry.start))
                {
                    // the token is before the start of this entry. 
                    continue;
                }

                if (Token.IsLocationBefore(entry.end, token))
                {
                    // the token is after the end of this entry
                    continue;
                }

                foundEntries.Add(entry);
            }

            foundEntries.Sort((a, b) =>
            {
                var aDist = Token.GetTokenDistance(a.start, a.end);
                var bDist = Token.GetTokenDistance(b.start, b.end);
                return aDist.CompareTo(bDist);
            });

            found = foundEntries.FirstOrDefault();
            var didFind = found != null;
            if (!didFind)
            {
                found = entries[0];
            }
            return didFind;
        }
        
        public class Entry
        {
            public Token start, end;
            public T value;
            public Entry(){}

            public Entry(IAstVisitable node, T data)
            {
                start = node.StartToken;
                end = node.EndToken;
                value = data;
            }
        }
    }
    
}