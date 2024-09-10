using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FadeBasic.Ast.Visitors
{
    public static partial class TriviaVistor
    {
        public static void AddTrivia(this ProgramNode program, LexerResults lexerResults)
        {
            // types, variables, and functions can have "trivia", which is a term taken from C# source generator land
            //  all it really means is leading doc strings.

            Dictionary<Token, int> tokenToIndexTable = new Dictionary<Token, int>();
            for (var i = 0 ; i < lexerResults.combinedTokens.Count; i ++)
            {
                var token = lexerResults.combinedTokens[i];
                tokenToIndexTable[token] = i;
            }

            program.Visit(node =>
            {
                if (node is IHasTriviaNode triviaNode)
                {
                    triviaNode.AddTrivia(tokenToIndexTable, lexerResults);
                }
            });
            
            // foreach (var function in program.functions)
            // {
            //     function.AddTrivia(tokenToIndexTable, lexerResults);
            // }
        }

        public static void AddTrivia(this IHasTriviaNode node, Dictionary<Token, int> tokenToIndexTable,
            LexerResults lexerResults)
        {
            var startToken = node.StartToken;

            // find the token's position in the stream
            if (!tokenToIndexTable.TryGetValue(startToken, out var startIndex))
            {
                // maybe this was an injected token, because of error parsing or some-such.
                return;
            }

            // work backwards finding all valid comment-tokens
            var foundOtherTokens = false;
            var triviaBuilder = new StringBuilder();
            var remGroup = false;
            for (var i = startIndex - 1; i >= 0; i--)
            {
                var token = lexerResults.combinedTokens[i];
                switch (token.type)
                {
                    // these are comment tokens, and should be included in the trivia
                    case LexemType.KeywordRem:

                        // process the comment, and remove the leading ` symbol and white-space
                        var input = token.raw;
                        var loweredInput = input.ToLowerInvariant();
                        if (loweredInput.StartsWith("remstart"))
                        {
                            remGroup = false;
                            input = input.Substring("remstart".Length);
                        }
                        else if (loweredInput.StartsWith("`"))
                        {
                            input = input.Substring(1);
                        }
                        else if (loweredInput.EndsWith("remend"))
                        {
                            remGroup = true;
                            input = input.Substring(0, input.Length - "remend".Length);
                        }

                        var comment = Regex.Replace(input, "^\\s*", "");
                        comment = Regex.Replace(comment, "\\s*$", "");

                        // if there is already content, add a single newline character
                        if (triviaBuilder.Length > 0)
                        {
                            if (remGroup)
                            {
                                if (!string.IsNullOrEmpty(input))
                                {
                                    triviaBuilder.Insert(0, Environment.NewLine);
                                }
                            }
                            else
                            {
                                triviaBuilder.Insert(0, Environment.NewLine);
                            }
                        }

                        // because we are going backwards, insert the comment at the top of the list, as it would read top-to-bottom. 
                        triviaBuilder.Insert(0, comment);
                        break;

                    // these tokens are essentially white-space, and can be ignored.
                    case LexemType.EndStatement:
                        break;

                    // this token breaks the stream of comment tokens
                    default:
                        foundOtherTokens = true;
                        break;
                }

                if (foundOtherTokens)
                {
                    break;
                }
            }

            var trivia = triviaBuilder.ToString();
            node.Trivia = trivia;

        }
    }
}