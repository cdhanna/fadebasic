using System;
using System.Collections.Generic;
using System.Linq;
using FadeBasic.Ast;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace FadeBasic
{
    public class FadeBasic
    {
        
        public FadeBasicTokens Tokenize(string src)
        {
            var lexer = new Lexer();
            var tokens = lexer.Tokenize(src);
            return new FadeBasicTokens
            {
                tokens = tokens.Select(x => new FadeBasicToken
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
    public class FadeBasicTokens
    {
        public List<FadeBasicToken> tokens;
    }

    [Serializable]
    public class FadeBasicToken
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public LexemType type;
        public int lineNumber;
        public int charPosition;
        public string rawText;
    }
}