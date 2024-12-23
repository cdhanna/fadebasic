using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FadeBasic.Json;

namespace FadeBasic
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
        CommandWord,
        
        Constant
    }

    public class LexerResults
    {
        public List<Token> tokens;
        public List<Token> comments;
        public List<Token> combinedTokens; // tokens and comments.
        public TokenStream stream;
        public List<LexerError> tokenErrors;

        public LexerResults()
        {
            
        }
    }

    [DebuggerDisplay("{Display}")]
    public class LexerError
    {
        public int lineNumber, charNumber;
        public ErrorCode error;
        public string text;

        public string Display => $"[{lineNumber}:{charNumber}:{text}] - {error}";
    }
    
    public class Lexer
    {

        public int helloFromRebuild;
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

        static Lexer()
        {
            foreach (var l in Lexems)
            {
                //l.priority = (int.MinValue + 10) + l.priority;
            }
        }

        private static Lexem LexemString = new Lexem(LexemType.LiteralString, new Regex("^\""));
        public static List<Lexem> Lexems = new List<Lexem>
        {
            new Lexem(LexemType.Constant, new Regex("^\\s*#constant\\s+([a-zA-Z][a-zA-Z0-9_]*)\\s+(.*)\\s*$")),
            new Lexem(LexemType.EndStatement, new Regex("^:")),
            new Lexem(LexemType.ArgSplitter, new Regex("^,")),
            new Lexem(LexemType.FieldSplitter, new Regex("^\\.")),
            
            new Lexem(-10,LexemType.WhiteSpace, new Regex("^(\\s|\\t|\\n)+")),
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

            new Lexem(LexemType.KeywordScope, new Regex("^(local|global)")),
            
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

            new Lexem(LexemType.KeywordRem, new Regex("^`(.*)$")),
            new Lexem(LexemType.KeywordRem, new Regex("^rem(.*)$")),
            // new Lexem(LexemType.WhiteSpace, new Regex("^remstart(.*)remend")),
            
            // new Lexem(LexemType.KeywordRem, new Regex("^(rem)(.*)$")),
            // new Lexem(LexemType.KeywordRem, new Regex("^`(.*)$")),
            new Lexem(-2, LexemType.KeywordRemStart, new Regex("^remstart(.)$")),
            new Lexem(-2, LexemType.KeywordRemEnd, new Regex("^remend")),
            //
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
            
            // special parsing will be needed for strings...
            LexemString,
            
            // new Lexem(LexemType.LiteralString, new Regex("^\"(.*?)\"")),
            // new Lexem(LexemType.LiteralString, new Regex(@"^(?<!\\)"".*?(?<!\\)""")),
            // new Lexem(LexemType.LiteralString, new Regex(@"^@?""(?:\""\""|[^""])*""")),
            
            new Lexem(-2, LexemType.VariableString, new Regex("^([a-zA-Z][a-zA-Z0-9_]*)\\$")),
            new Lexem(-2, LexemType.VariableReal, new Regex("^([a-zA-Z][a-zA-Z0-9_]*)#")),
            new Lexem( 2, LexemType.VariableGeneral, new Regex("^[a-zA-Z][a-zA-Z0-9_]*")),
            // new Lexem(-2, LexemType.Label, new Regex("^[a-zA-Z][a-zA-Z0-9_]*:")),
        };

        public List<Token> Tokenize(string input, CommandCollection commands = default)
        {
            var res = TokenizeWithErrors(input, commands?.Commands?.Select(c => c.name).ToList());
            return res.tokens;
        }

        public LexerResults TokenizeWithErrors(string input, CommandCollection commands) =>
            TokenizeWithErrors(input, commands?.Commands?.Select(c => c.name).ToList());
        public LexerResults TokenizeWithErrors(string input, List<string> commandNames=null)
        {
            var tokens = new List<Token>();
            var comments = new List<Token>();
            var combined = new List<Token>();

            void AddToken(Token t)
            {
                tokens.Add(t);
                combined.Add(t);
            }

            void AddComment(Token t)
            {
                comments.Add(t);
                combined.Add(t);
            }
            if (commandNames == null)
            {
                commandNames = new List<string>();
            }

            var errors = new List<LexerError>();

            var constantTable = new Dictionary<string, string>();

            var lexems = Lexems.ToList();
            // commands
            foreach (var commandName in commandNames)
            {
                // var pattern = "";
                var components = commandName.Select(x =>
                {
                    switch (x)
                    {
                        case ' ':
                            return "(\\s|\\t)+";
                        case '$':
                            return "\\$";
                        default:
                            return $"({char.ToLower(x)}|{char.ToUpper(x)})";
                    }
                });
                var pattern = "^" + string.Join("", components);
              
                // pattern += "(\\b|$)";
                var commandLexem = new Lexem(-((pattern.Length) * 100), LexemType.CommandWord, new Regex(pattern));
                lexems.Add(commandLexem);
            }

            lexems.Sort((a, b) =>
            {
                var prioCompare = a.priority.CompareTo(b.priority);
                return prioCompare;
                // if (prioCompare == 0)
                // {
                //     
                // }
            });

            var lines = input.Split(new string[]{"\n"}, StringSplitOptions.None);

            var eolLexem = new Lexem(LexemType.EndStatement, null);

            Token remBlockToken = null;
            var remBlockSb = new StringBuilder();
            var requestEoS = false;
            var requestEoSCharNumber = 0;
            for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
            {
                
                var line = lines[lineNumber];
                if (string.IsNullOrEmpty(line))
                {
                    if (remBlockToken != null)
                    {
                        AddComment(new Token
                        {
                            lineNumber = lineNumber,
                            charNumber = 0,
                            caseInsensitiveRaw = Environment.NewLine,
                            raw = Environment.NewLine,
                            lexem = remBlockToken.lexem
                        });
                    }
                }
                
                if (remBlockToken != null)
                {
                    remBlockToken = new Token
                    {
                        charNumber = 0,
                        lineNumber = lineNumber,
                        lexem = new Lexem(LexemType.KeywordRem, null),

                    };
                }

                for (var charNumber = 0; charNumber < line.Length; charNumber = charNumber)
                {
                    var foundMatch = false;
                    var sub = line.Substring(charNumber);
                    var subStr = sub.ToLowerInvariant();

                    
                    if (remBlockToken == null && subStr.StartsWith("remstart"))
                    {
                        // we are remmin'
                        remBlockToken = new Token
                        {
                            lexem = new Lexem(LexemType.KeywordRem, null),
                            charNumber = charNumber,
                            lineNumber = lineNumber,
                        };
                        continue;
                    } 
                    if (remBlockToken != null && subStr.StartsWith("remend"))
                    {
                        // we are done remmin' for now.
                        remBlockToken.raw = line.Substring(remBlockToken.charNumber,
                            (charNumber - remBlockToken.charNumber) + "remend".Length);
                        remBlockToken.caseInsensitiveRaw = remBlockToken.raw.ToLowerInvariant();
                        
                        AddComment(remBlockToken);
                        remBlockToken = null;
                        charNumber += "remend".Length;
                        continue;

                    } 
                    if (remBlockToken != null)
                    {
                        // we are still remmin'
                        charNumber++;
                        continue;
                    }

                    Token bestToken = null;
                    MatchCollection bestMatches = null;

                    var hadRemCandidate = false;
                    var isStringParse = subStr.Length > 0 && subStr[0] == '"';

                    if (isStringParse)
                    {
                        /*
                         * time to parse a string!
                         * - strings are one line
                         * - strings must end with a quote
                         * - strings can have backslashes, and those REQUIRE a second character to exist, which will be escaped.
                         */
                        var matchedEnd = false;
                        int strIndex = 0;
                        var charOffset = 0;
                        var strBuffer = new StringBuilder();
                        strBuffer.Append('"');

                        for (strIndex = charNumber + 1;
                             strIndex < line.Length; 
                             strIndex++) 
                        {
                            var strChar = line[strIndex];
                            switch (strChar)
                            {
                                case '"':
                                    // exit the loop, the string's bounds are found.
                                    strIndex++;
                                    strBuffer.Append('"');
                                    matchedEnd = true;
                                    break;
                                case '\\':
                                    // there must be a second character 
                                    if (strIndex == line.Length - 1)
                                    {
                                        // this is the last character in the line, but it cannot be.
                                        //throw new InvalidOperationException(); // TODO: replace with lexer error
                                    }

                                    // move forwards
                                    strIndex++;
                                    charOffset++;

                                    strBuffer.Append(line[strIndex]);

                                    break;
                                default:
                                    strBuffer.Append(strChar);
                                    break;
                            }

                            if (matchedEnd) break;
                        }

                        if (!matchedEnd)
                        {
                            errors.Add(new LexerError
                            {
                                charNumber = charNumber,
                                lineNumber = lineNumber,
                                error = ErrorCodes.LexerStringNeedsEnd,
                                text = line.Substring(charNumber)
                            });
                        }

                        var insensitiveRaw = line.Substring(charNumber, strIndex - charNumber);
                        var stringLiteralSubStr = strBuffer.ToString();
                        bestToken = new Token
                        {
                            caseInsensitiveRaw = insensitiveRaw.ToLowerInvariant(),
                            raw = stringLiteralSubStr,
                            lexem = LexemString,
                            lineNumber = lineNumber,
                            charNumber = charNumber
                        };
                        foundMatch = true;
                        charNumber += charOffset;
                    }
                    
                    for (var lexemId = 0; lexemId < lexems.Count && !isStringParse; lexemId++)
                    {
                        var lexem = lexems[lexemId];
                        var matches = lexem.regex.Matches(subStr);

                        if (matches.Count == 1)
                        {
                            foundMatch = true;
                            
                            var token = new Token
                            {
                                caseInsensitiveRaw = matches[0].Value,
                                raw = sub.Substring(matches[0].Index, matches[0].Length),
                                lexem = lexem,
                                lineNumber = lineNumber,
                                charNumber = charNumber
                            };
                           
                            if (bestToken == null || token.Length > bestToken.Length)
                            {
                                bestToken = token;
                                bestMatches = matches;
                            }

                            // break;
                        }
                        else if (matches.Count > 1)
                        {
                            throw new Exception("Token exception! Too many matches!");
                        }
                    }

                    if (bestToken != null)
                    {
                        switch (bestToken.type)
                        {
                            case LexemType.KeywordRem:
                                AddComment(bestToken);
                                break;
                            case LexemType.WhiteSpace:
                                // we ignore white space in token generation
                                // this could be a rem token...
                                break;
                            case LexemType.ArgSplitter:
                                requestEoS = false;
                                AddToken(bestToken);
                                // tokens.Add(bestToken);
                                
                                
                                
                                break;
                            case LexemType.Constant:
                                // replace all instances of string...
                                var toRemove = bestMatches[0].Groups[1].Value;
                                var toAdd = bestMatches[0].Groups[2].Value;

                                constantTable[toRemove] = toAdd;
                                // var prefix = line.Substring(0, charNumber);
                                // var suffix = line.Substring(charNumber + toRemove.Length);
                                //
                                // var replacementLine = prefix + toAdd + suffix;
                                break;
                            case LexemType.VariableGeneral
                                when constantTable.TryGetValue(bestToken.raw, out var replacement):
                                var prefix = line.Substring(0, charNumber);
                                var suffix = line.Substring(charNumber + bestToken.Length);

                                var replacementLine = prefix + replacement + suffix;
                                line = replacementLine;
                                continue;
                                break;
                            default:

                                if (requestEoS)
                                {
                                    requestEoS = false;
                                    AddToken(new Token
                                    {
                                        charNumber = requestEoSCharNumber,
                                        lexem = eolLexem,
                                        lineNumber = lineNumber,
                                        caseInsensitiveRaw = "\n"
                                    });
                                }

                                AddToken(bestToken);
                                break;
                        }


                        charNumber += bestToken.Length;
                    }

                    if (!foundMatch)
                    {
                        errors.Add(new LexerError
                        {
                            charNumber = charNumber,
                            lineNumber = lineNumber,
                            text = sub,
                            error = ErrorCodes.LexerUnmatchedText
                        });
                        charNumber += sub.Length;
                        // throw new Exception($"Token exception! No match for {subStr} at {lineNumber}:{charNumber}");
                    }
                }


                if (remBlockToken != null)
                {
                    // commit
                    remBlockToken.raw = line.Substring(remBlockToken.charNumber);
                    remBlockToken.caseInsensitiveRaw = remBlockToken.raw.ToLowerInvariant();
                    AddComment(remBlockToken);
                }
                
                var previousTokenWasNotEoS = tokens.Count > 0 ? tokens[tokens.Count - 1].type != LexemType.EndStatement : false;
                var previousTokenWasNotArgSplitter = tokens.Count > 0 ? tokens[tokens.Count - 1].type != LexemType.ArgSplitter : true;
                
                // if the next token is an arg splitter, than we don't want an EoS either...
                if (previousTokenWasNotEoS && previousTokenWasNotArgSplitter)
                {
                    requestEoS = true;
                    requestEoSCharNumber = line.Length;
                    // eosInsertIndex.Add();
                    // tokens.Add(new Token
                    // {
                    //     charNumber = line.Length, 
                    //     lexem = eolLexem,
                    //     lineNumber = lineNumber,
                    //     caseInsensitiveRaw = "\n"
                    // });
                }
                
            }
            
            if (requestEoS)
            {
                requestEoS = false;
                AddToken(new Token
                {
                    charNumber = requestEoSCharNumber, 
                    lexem = eolLexem,
                    lineNumber = lines.Length - 1,
                    caseInsensitiveRaw = "\n"
                });
            }

            return new LexerResults
            {
                tokens = tokens,
                comments = comments,
                combinedTokens = combined,
                stream = new TokenStream(tokens, errors),
                tokenErrors = errors
            };
        }


    }

    public class Lexem
    {
        public readonly Regex regex;
        public int priority;
        public readonly LexemType type;

        public Lexem()
        {
        }

        public Lexem(LexemType type)
        {
            this.type = type;
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

    [Flags]
    public enum TokenFlags
    {
        None   = 0,
        
        /// <summary>
        /// This flag indicates that the given token is an invocation to a function.
        /// This is helpful because function calls are represented as an array-index node in the AST
        /// </summary>
        FunctionCall  = 1 << 0,
        
        // Second = 1 << 1,
        // Third  = 1 << 2,
        // Fourth = 1 << 3
    }
    
    [Serializable]
    [DebuggerDisplay("{raw} ({type}:{lineNumber}:{charNumber})")]
    public class Token : IJsonable
    {
        public int lineNumber;
        public int charNumber;
        public string raw;
        public string caseInsensitiveRaw;
        
        public int Length => caseInsensitiveRaw?.Length ?? 0;
        public LexemType type => lexem?.type ?? LexemType.EOF;
        public string Location => $"{lineNumber}:{charNumber}";
        public TokenFlags flags = TokenFlags.None;
        public Lexem lexem;


        public static bool AreLocationsEqual(Token a, Token b)
        {
            if (a == null || b == null) return false;
            return a.lineNumber == b.lineNumber && a.charNumber == b.charNumber;
        }

        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(lineNumber), ref lineNumber);
            op.IncludeField(nameof(charNumber), ref charNumber);
            op.IncludeField(nameof(raw), ref raw);
            op.IncludeField(nameof(caseInsensitiveRaw), ref caseInsensitiveRaw);
            // op.IncludeField("lexemType", ref flags);
        }
    }

    public class TokenStream
    {
        public List<LexerError> Errors { get; }
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

        public TokenStream(List<Token> tokens) : this(tokens, new List<LexerError>())
        {
        }

        public TokenStream(List<Token> tokens, List<LexerError> errors)
        {
            Errors = errors;
            _tokens = tokens;
            Current = _tokens.Count > 0 ? _tokens[0] : null;
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

        public Token AdvanceUntil(LexemType type)
        {
            while (Current.type != type && Current.type != LexemType.EOF)
            {
                Current = _tokens[Index++];
            }

            return Current;
        }

        public void Patch(int index, List<Token> tokens)
        {
            _tokens.InsertRange(index , tokens);
        }

        public bool IsEof => Index >= _tokens.Count;

        public int Save()
        {
            return Index;
        }

        public void Restore(int index)
        {
            Index = index;
            Current = _tokens[index];
        }

        public List<Token> CreatePatchToken(LexemType type, string s, int offset=0)
        {
            var copyToken = _tokens[Math.Min(_tokens.Count - 1, Index + offset)];
            return new List<Token>
            {
                new Token
                {
                    charNumber = copyToken.charNumber,
                    lineNumber = copyToken.lineNumber,
                    caseInsensitiveRaw = s.ToLowerInvariant(),
                    raw = s,
                    lexem = new Lexem(type)
                }
            };
        }
    }

}