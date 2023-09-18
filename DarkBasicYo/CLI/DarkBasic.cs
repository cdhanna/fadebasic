using System;
using System.Collections.Generic;
using System.Linq;
using DarkBasicYo.Ast;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DarkBasicYo
{
    public class DarkBasic
    {
        
        public DarkBasicTokens Tokenize(string src)
        {
            var lexer = new Lexer();
            var tokens = lexer.Tokenize(src);
            return new DarkBasicTokens
            {
                tokens = tokens.Select(x => new DarkBasicToken
                {
                    rawText = x.raw,
                    lineNumber = x.lineNumber,
                    charPosition = x.charNumber,
                    type = x.type
                }).ToList()
            };
        }
        
        public ProgramNode Parse(string src)
        {
            var lexer = new Lexer();
            var tokens = lexer.Tokenize(src);
            var parser = new Parser(new TokenStream(tokens), new CommandCollection());

            var program = parser.ParseProgram();
            return program;
        }
    }
    
    [Serializable]
    public class DarkBasicTokens
    {
        public List<DarkBasicToken> tokens;
    }

    [Serializable]
    public class DarkBasicToken
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public LexemType type;
        public int lineNumber;
        public int charPosition;
        public string rawText;
    }
}