using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DarkBasicYo
{

    public enum LexemType
    {
        EOF,
        EndStatement,
        KeywordRem,
        KeywordRemStart,
        KeywordRemEnd,
        
        KeywordDeclareArray,
        KeywordUnDeclareArray,
        
        KeywordFor, 
        KeywordTo, 
        KeywordStep, 
        KeywordNext,
        
        KeywordFunction,
        KeywordEndFunction,
        KeywordExitFunction,
        
        KeywordIf,
        KeywordThen,
        KeywordEndIf,
        KeywordElse,
        
        KeywordSelect,
        KeywordEndSelect,
        KeywordCase,
        KeywordEndCase,
        KeywordCaseDefault,

        KeywordGoto,
        KeywordGoSub,
        KeywordReturn,
        KeywordEnd,

        KeywordType,
        KeywordEndType,
        
        KeywordRepeat,
        KeywordUntil,
        KeywordDo,
        KeywordLoop,
        KeywordWhile,
        KeywordEndWhile,
        
        KeywordExit,
        
        KeywordAs,
        KeywordTypeInteger,
        KeywordTypeByte,
        KeywordTypeWord,
        KeywordTypeDWord,
        KeywordTypeDoubleInteger,
        KeywordTypeFloat, // real
        KeywordTypeDoubleFloat,
        KeywordTypeString,
        KeywordTypeBoolean,

        KeywordScope,
        
        KeywordAnd,
        KeywordOr,
        KeywordNot,

        // Colon,

        WhiteSpace,
        ArgSplitter,
        FieldSplitter,
        OpPlus,
        OpMultiply,
        OpDivide,
        OpMinus,
        OpPower,
        OpMod,
        OpGt,
        OpLt,
        OpGte,
        OpLte,
        OpEqual,
        OpNotEqual,
        ParenOpen,
        ParenClose,
        LiteralReal,
        LiteralInt,
        LiteralString,
        VariableGeneral,
        VariableReal,
        VariableString,
        CommandWord
    }

    public class Lexer
    {

        // TODOs
        /*
         * 3. comments REM REMSTART REMEND, `
         * 5. if endif, else
         * 6. for loop stuff
         * 7. repeat until
         * 1. functions
         * 2. types
         * 3. arrays
         * 4. negative numbers
         * AND, OR, XOR and NOT
         */

        public static List<Lexem> Lexems = new List<Lexem>
        {
            new Lexem(LexemType.EndStatement, new Regex("^:")),
            new Lexem(LexemType.ArgSplitter, new Regex("^,")),
            new Lexem(LexemType.FieldSplitter, new Regex("^\\.")),
            
            new Lexem(LexemType.WhiteSpace, new Regex("^(\\s|\\t|\\n)+")),
            new Lexem(LexemType.ParenOpen, new Regex("^\\(")),
            new Lexem(LexemType.ParenClose, new Regex("^\\)")),
            new Lexem(LexemType.OpPlus, new Regex("^\\+")),
            new Lexem(LexemType.OpMinus, new Regex("^\\-")),
            new Lexem(LexemType.OpMultiply, new Regex("^\\*")),
            new Lexem(LexemType.OpDivide, new Regex("^\\/")),
            new Lexem(LexemType.OpGt, new Regex("^>")),
            new Lexem(LexemType.OpLt, new Regex("^<")),
            new Lexem(-2, LexemType.OpLte, new Regex("^<=")),
            new Lexem(-2, LexemType.OpGte, new Regex("^>=")),
            new Lexem(LexemType.OpMod, new Regex("^mod")),
            new Lexem(LexemType.OpPower, new Regex("^\\^")),
            new Lexem(LexemType.OpEqual, new Regex("^=")),
            new Lexem(-3, LexemType.OpNotEqual, new Regex("^<>")),
            new Lexem(LexemType.KeywordAnd, new Regex("^and")),
            new Lexem(LexemType.KeywordOr, new Regex("^or")),
            new Lexem(LexemType.KeywordNot, new Regex("^not")),
            new Lexem(LexemType.KeywordFor, new Regex("^for")),
            new Lexem(LexemType.KeywordTo, new Regex("^to")),
            new Lexem(LexemType.KeywordStep, new Regex("^step")),
            new Lexem(LexemType.KeywordNext, new Regex("^next")),
            
            new Lexem(LexemType.KeywordEndFunction, new Regex("^endfunction\\b")), 
            new Lexem(LexemType.KeywordFunction, new Regex("^function\\b")),
            new Lexem(-1, LexemType.KeywordExitFunction, new Regex("^exitfunction\\b")),

            new Lexem(LexemType.KeywordDo, new Regex("^do\\b")), // TODO: add word boundary to everything...
            new Lexem(LexemType.KeywordLoop, new Regex("^loop")),
            
            new Lexem(LexemType.KeywordSelect, new Regex("^select")),
            new Lexem(LexemType.KeywordEndSelect, new Regex("^endselect")),
            new Lexem(LexemType.KeywordCase, new Regex("^case")),
            new Lexem(LexemType.KeywordEndCase, new Regex("^endcase")),
            new Lexem(LexemType.KeywordCaseDefault, new Regex("^default")),
            
            new Lexem(LexemType.KeywordRepeat, new Regex("^repeat")),
            new Lexem(LexemType.KeywordUntil, new Regex("^until")),
            // new Lexem(LexemType.Colon, new Regex("^:")),

            new Lexem(LexemType.KeywordScope, new Regex("^(local)|(global)")),
            
            new Lexem(LexemType.KeywordIf, new Regex("^if")),
            new Lexem(LexemType.KeywordEndIf, new Regex("^endif")),
            new Lexem(LexemType.KeywordElse, new Regex("^else")),
            new Lexem(LexemType.KeywordThen, new Regex("^then")),
            new Lexem(1,LexemType.KeywordEnd, new Regex("^end")),
            new Lexem(LexemType.KeywordExit, new Regex("^exit")),
            
            new Lexem(LexemType.KeywordGoto, new Regex("^goto")),
            new Lexem(LexemType.KeywordGoSub, new Regex("^gosub")),
            new Lexem(LexemType.KeywordReturn, new Regex("^return")),
            
            new Lexem(LexemType.KeywordDeclareArray, new Regex("^dim")),
            new Lexem(LexemType.KeywordUnDeclareArray, new Regex("^undim")),

            new Lexem(LexemType.KeywordRem, new Regex("^(rem)(.*)$")),
            new Lexem(LexemType.KeywordRem, new Regex("^`(.*)$")),
            new Lexem(-2, LexemType.KeywordRemStart, new Regex("^remstart(.*)$")),
            new Lexem(-2, LexemType.KeywordRemEnd, new Regex("^remend")),
            
            new Lexem(LexemType.KeywordType, new Regex("^type")),
            new Lexem(LexemType.KeywordEndType, new Regex("^endtype")),
            
            new Lexem(LexemType.KeywordWhile, new Regex("^while")),
            new Lexem(LexemType.KeywordEndWhile, new Regex("^endwhile")),
            new Lexem(LexemType.KeywordAs, new Regex("^as")),
            new Lexem(LexemType.KeywordTypeBoolean, new Regex("^boolean")),
            new Lexem(LexemType.KeywordTypeByte, new Regex("^byte")),
            new Lexem(LexemType.KeywordTypeInteger, new Regex("^integer")),
            new Lexem(LexemType.KeywordTypeWord, new Regex("^word")),
            new Lexem(LexemType.KeywordTypeDWord, new Regex("^dword")),
            new Lexem(LexemType.KeywordTypeDoubleInteger, new Regex("^double integer")),
            new Lexem(LexemType.KeywordTypeFloat, new Regex("^float")),
            new Lexem(LexemType.KeywordTypeDoubleFloat, new Regex("^double float")),
            new Lexem(LexemType.KeywordTypeString, new Regex("^string")),

            new Lexem(-2, LexemType.LiteralReal, new Regex("^((\\d+\\.(\\d*))|(\\.\\d+))")),
            new Lexem(LexemType.LiteralInt, new Regex("^\\d+")),
            // new Lexem(LexemType.LiteralString, new Regex("^\"(.*?)\"")),
            new Lexem(LexemType.LiteralString, new Regex(@"^(?<!\\)"".*?(?<!\\)""")),
            
            new Lexem(-2, LexemType.VariableString, new Regex("^([a-zA-Z][a-zA-Z0-9_]*)\\$")),
            new Lexem(-2, LexemType.VariableReal, new Regex("^([a-zA-Z][a-zA-Z0-9_]*)#")),
            new Lexem(2, LexemType.VariableGeneral, new Regex("^[a-zA-Z][a-zA-Z0-9_]*")),
            // new Lexem(-2, LexemType.Label, new Regex("^[a-zA-Z][a-zA-Z0-9_]*:")),
        };


        public List<Token> Tokenize(string input, CommandCollection commands = default)
        {
            var tokens = new List<Token>();
            if (commands == default)
            {
                commands = new CommandCollection();
            }

            var lexems = Lexems.ToList();
            foreach (var command in commands.Commands)
            {
                // var pattern = "";
                var components = command.name.Select(x =>
                {
                    switch (x)
                    {
                        case ' ':
                            return "(\\s|\\t)+";
                        default:
                            return $"({char.ToLower(x)}|{char.ToUpper(x)})";
                    }
                });
                var pattern = "^" + string.Join("", components);
                var commandLexem = new Lexem(-1, LexemType.CommandWord, new Regex(pattern));
                lexems.Add(commandLexem);
            }

            lexems.Sort((a, b) => a.priority.CompareTo(b.priority));

            var lines = input.Split(new string[]{"\n"}, StringSplitOptions.RemoveEmptyEntries);

            var eolLexem = new Lexem(LexemType.EndStatement, null);

            Token remBlockToken = null;
            var remBlockSb = new StringBuilder();
            for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
            {
                var line = lines[lineNumber];

                if (remBlockToken == null && line.ToLowerInvariant().StartsWith("remstart"))
                {
                    remBlockToken = new Token
                    {
                        lexem = new Lexem(LexemType.KeywordRemStart, null),
                        charNumber = 0,
                        lineNumber = lineNumber,
                    };
                    remBlockSb.Clear();
                    remBlockSb.AppendLine(line.Substring("remstart".Length));
                    continue;

                } else if (remBlockToken != null && (line.ToLowerInvariant().StartsWith("remend") || lineNumber == lines.Length - 1))
                {
                    remBlockToken.raw = remBlockSb.ToString();
                    tokens.Add(remBlockToken);
                    tokens.Add(new Token
                    {
                        lexem = new Lexem(LexemType.KeywordRemEnd, null),
                        charNumber = 0,
                        lineNumber = lineNumber,
                        raw = line
                    });
                    remBlockToken = null;
                    continue;
                } else if (remBlockToken != null)
                {
                    remBlockSb.AppendLine(line);
                    continue;
                }

                for (var charNumber = 0; charNumber < line.Length; charNumber = charNumber)
                {
                    var foundMatch = false;
                    var subStr = line.Substring(charNumber).ToLowerInvariant();

                    for (var lexemId = 0; lexemId < lexems.Count; lexemId++)
                    {
                        var lexem = lexems[lexemId];
                        var matches = lexem.regex.Matches(subStr);
                        if (matches.Count == 1)
                        {
                            foundMatch = true;
                            var token = new Token
                            {
                                raw = matches[0].Value,
                                lexem = lexem,
                                lineNumber = lineNumber,
                                charNumber = charNumber
                            };

                            switch (lexem.type)
                            {
                                case LexemType.WhiteSpace:
                                    // we ignore white space in token generation
                                    break;
                                default:
                                    tokens.Add(token);
                                    break;
                            }
                            
                            charNumber += token.raw.Length;
                            break;
                        }
                        else if (matches.Count > 1)
                        {
                            throw new Exception("Token exception! Too many matches!");
                        }
                    }

                    if (!foundMatch)
                    {
                        throw new Exception($"Token exception! No match for {subStr} at {lineNumber}:{charNumber}");
                    }
                }

                if (tokens[tokens.Count - 1].type != LexemType.EndStatement)
                {
                    tokens.Add(new Token
                    {
                        charNumber = line.Length, 
                        lexem = eolLexem,
                        lineNumber = lineNumber,
                        raw = "\n"
                    });
                }
                
            }

            return tokens;
        }


    }

    public class Lexem
    {
        public readonly Regex regex;
        public readonly int priority;
        public readonly LexemType type;

        public Lexem()
        {
        }

        public Lexem(LexemType type, Regex regex)
        {
            this.type = type;
            this.regex = regex;
        }

        public Lexem(int priority, LexemType type, Regex regex)
        {
            this.priority = priority;
            this.type = type;
            this.regex = regex;
        }
    }

    [Serializable]
    [DebuggerDisplay("{raw} ({type}:{lineNumber}:{charNumber})")]
    public class Token
    {
        public int lineNumber;
        public int charNumber;
        public string raw;
        public LexemType type => lexem.type;
        public string Location => $"{lineNumber}:{charNumber}";

        public Lexem lexem;
    }

    public class TokenStream
    {
        private readonly List<Token> _tokens;

        public int Index { get; private set; }

        public Token Current { get; private set; }
        private int _maxIndex;
        

        public Token Peek => IsEof
            ? new Token
            {
                lexem = new Lexem(LexemType.EOF, null)
            }
            : _tokens[Index];

        public List<Token> PeekUntilEoS => PeekUntil(LexemType.EndStatement);
        public List<Token> PeekUntil(LexemType type)
        {
            var res = _tokens.Skip(Index).TakeWhile(x => x.type != type).ToList();
            return res;
        }

        public TokenStream(List<Token> tokens)
        {
            _tokens = tokens;
            Current = _tokens[0];
            _maxIndex = tokens.Count;
        }

        public TokenStream(List<Token> tokens, int startIndex, int maxIndex)
        {
            _tokens = tokens;
            Index = startIndex;
            Current = _tokens[startIndex];
            _maxIndex = maxIndex;
        }

        public Token Advance()
        {
            return Current = _tokens[Index++];
        }


        public bool IsEof => Index >= _tokens.Count;
       
    }

}