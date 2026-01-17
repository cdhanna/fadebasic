using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FadeBasic.Ast;
using FadeBasic.Json;
using FadeBasic.Sdk;
using FadeBasic.Virtual;

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
        // KeywordUnDeclareArray,
        KeywordReDimArray,
        
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
        KeywordSkip,
        
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
        KeywordXor,

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
        OpBitwiseLeftShift,
        OpBitwiseRightShift,
        OpBitwiseAnd,
        OpBitwiseOr,
        OpBitwiseNot,
        OpBitwiseXor,
        ParenOpen,
        ParenClose,
        BracketOpen,
        BracketClose,
        LiteralReal,
        LiteralInt,
        LiteralString,
        VariableGeneral,
        VariableReal,
        VariableString,
        CommandWord,
        
        LiteralBinary,
        LiteralHex,
        LiteralOctal,
        
        Constant,
        ConstantBegin,
        ConstantEnd,
        ConstantTokenize,
        ConstantEndTokenize,
        ConstantBracketOpen,
        ConstantBracketClose
    }

    public class LexerResults
    {
        public List<Token> tokens;
        public List<Token> comments;
        public List<Token> combinedTokens; // tokens and comments.
        public List<Token> allTokens; // tokens and macros and comments.
        public TokenStream stream;
        public List<ParseError> tokenErrors;
        public List<Token> macroTokens = new List<Token>();

        public LexerResults()
        {
            
        }
    }

    public class Lexer
    {
        private static Lexem LexemString = new Lexem(LexemType.LiteralString, new Regex("^\""));
        private static Lexem LexemConstant = new Lexem(LexemType.Constant);
        // private static Lexem LexemConstantBegin = new Lexem(LexemType.Constant);
        // private static Lexem LexemConstant = new Lexem(LexemType.Constant);
        public static List<Lexem> Lexems = new List<Lexem>
        {
            new Lexem(LexemType.Constant, new Regex("^\\s*#constant\\s+([a-zA-Z][a-zA-Z0-9_]*)\\s+(.*)\\s*$"), LexemFlags.PreventEos),
            new Lexem(LexemType.ConstantBegin, new Regex("^#macro\\b"), LexemFlags.PreventEos),
            new Lexem(LexemType.ConstantEnd, new Regex("^#endmacro\\b"), LexemFlags.PreventEos),
            new Lexem(LexemType.ConstantTokenize, new Regex("^#tokenize\\b"), LexemFlags.PreventEos),
            new Lexem(LexemType.ConstantEndTokenize, new Regex("^#endtokenize\\b")),
            new Lexem(LexemType.ConstantBracketOpen, new Regex("^\\[")),
            new Lexem(LexemType.ConstantBracketClose, new Regex("^\\]")),
            
            
            new Lexem(LexemType.EndStatement, new Regex("^:")),
            new Lexem(LexemType.ArgSplitter, new Regex("^,"), LexemFlags.PreventEos),
            new Lexem(LexemType.FieldSplitter, new Regex("^\\.")),
            
            new Lexem(-10,LexemType.WhiteSpace, new Regex("^(\\s|\\t|\\n)+")),
            new Lexem(LexemType.ParenOpen, new Regex("^\\(")),
            new Lexem(LexemType.ParenClose, new Regex("^\\)")),
            new Lexem(LexemType.BracketOpen, new Regex("^\\{")),
            new Lexem(LexemType.BracketClose, new Regex("^\\}")),
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
            new Lexem(LexemType.OpBitwiseAnd, new Regex("^&&")),
            new Lexem(LexemType.OpBitwiseOr, new Regex("^\\|\\|")),
            new Lexem(LexemType.OpBitwiseNot, new Regex("^\\.\\.")),
            new Lexem(LexemType.OpBitwiseLeftShift, new Regex("^<<")),
            new Lexem(LexemType.OpBitwiseRightShift, new Regex("^>>")),
            new Lexem(LexemType.OpBitwiseXor, new Regex("^~~")),
            new Lexem(-3, LexemType.OpNotEqual, new Regex("^<>")),
            new Lexem(LexemType.KeywordAnd, new Regex("^and")),
            new Lexem(LexemType.KeywordXor, new Regex("^xor")),
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
            new Lexem(LexemType.KeywordSkip, new Regex("^skip")),
            
            new Lexem(LexemType.KeywordGoto, new Regex("^goto")),
            new Lexem(LexemType.KeywordGoSub, new Regex("^gosub")),
            new Lexem(LexemType.KeywordReturn, new Regex("^return")),
            
            new Lexem(LexemType.KeywordDeclareArray, new Regex("^dim")),
            // new Lexem(LexemType.KeywordUnDeclareArray, new Regex("^undim")),
            new Lexem(LexemType.KeywordReDimArray, new Regex("^redim")),

            new Lexem(LexemType.KeywordRem, new Regex("^`(.*)$"), LexemFlags.PreventEos),
            new Lexem(LexemType.KeywordRem, new Regex("^rem(.*)$"), LexemFlags.PreventEos),
            // new Lexem(LexemType.WhiteSpace, new Regex("^remstart(.*)remend")),
            
            // new Lexem(LexemType.KeywordRem, new Regex("^(rem)(.*)$")),
            // new Lexem(LexemType.KeywordRem, new Regex("^`(.*)$")),
            new Lexem(-2, LexemType.KeywordRemStart, new Regex("^remstart(.)$"), LexemFlags.PreventEos),
            new Lexem(-2, LexemType.KeywordRemEnd, new Regex("^remend"), LexemFlags.PreventEos),
            //
            new Lexem(LexemType.KeywordType, new Regex("^type")),
            new Lexem(LexemType.KeywordEndType, new Regex("^endtype")),
            
            new Lexem(LexemType.KeywordWhile, new Regex("^while")),
            new Lexem(LexemType.KeywordEndWhile, new Regex("^endwhile")),
            new Lexem(LexemType.KeywordAs, new Regex("^as")),
            new Lexem(LexemType.KeywordTypeBoolean, new Regex("(^boolean)|(^bool)")),
            new Lexem(LexemType.KeywordTypeByte, new Regex("^byte")),
            new Lexem(LexemType.KeywordTypeInteger, new Regex("(^integer)|(^int)")),
            new Lexem(LexemType.KeywordTypeWord, new Regex("(^word)|(^ushort)")),
            new Lexem(LexemType.KeywordTypeDWord, new Regex("(^dword)|(^uint)")),
            new Lexem(LexemType.KeywordTypeDoubleInteger, new Regex("(^double integer)|(^long)")),
            new Lexem(LexemType.KeywordTypeFloat, new Regex("^float")),
            new Lexem(LexemType.KeywordTypeDoubleFloat, new Regex("(^double float)|(^double)")),
            new Lexem(LexemType.KeywordTypeString, new Regex("^string")),

            new Lexem(-2, LexemType.LiteralReal, new Regex("^((\\d+\\.(\\d*))|(\\.\\d+))"), LexemFlags.MacroConcatable),
            new Lexem(LexemType.LiteralInt, new Regex("^\\d+"), LexemFlags.MacroConcatable),
            
            // literal symbols
            new Lexem(-3, LexemType.LiteralBinary, new Regex("^%(0|1)+"), LexemFlags.MacroConcatable),
            new Lexem(-3, LexemType.LiteralHex, new Regex("^0x([A-F]|[a-f]|[0-9])+"), LexemFlags.MacroConcatable),
            new Lexem(-3, LexemType.LiteralOctal, new Regex("^0c([0-7])+"), LexemFlags.MacroConcatable),
            
            // special parsing will be needed for strings...
            LexemString,
            
            // new Lexem(LexemType.LiteralString, new Regex("^\"(.*?)\"")),
            // new Lexem(LexemType.LiteralString, new Regex(@"^(?<!\\)"".*?(?<!\\)""")),
            // new Lexem(LexemType.LiteralString, new Regex(@"^@?""(?:\""\""|[^""])*""")),
            
            new Lexem(-2, LexemType.VariableString, new Regex("^([a-zA-Z]?[a-zA-Z0-9_]*)\\$"), LexemFlags.MacroConcatable),
            new Lexem(-2, LexemType.VariableReal, new Regex("^([a-zA-Z]?[a-zA-Z0-9_]*)#"), LexemFlags.MacroConcatable),
            new Lexem( 2, LexemType.VariableGeneral, new Regex("^[a-zA-Z][a-zA-Z0-9_]*"), LexemFlags.MacroConcatable),
            // new Lexem(-2, LexemType.Label, new Regex("^[a-zA-Z][a-zA-Z0-9_]*:")),
        };

        public List<Token> Tokenize(string input, CommandCollection commands = default)
        {
            var res = TokenizeWithErrors(input, commands?.Commands?.Select(c => c.name).ToList());
            return res.tokens;
        }

        public LexerResults TokenizeWithErrors(string input, CommandCollection commands, CommandCollection macroCommands=null) =>
            TokenizeWithErrors(input, commands?.Commands?.Select(c => c.name).ToList(), macroCommands);
        public LexerResults TokenizeWithErrors(string input, List<string> commandNames=null, CommandCollection macroCommands=null)
        {
            var tokens = new List<Token>();
            var comments = new List<Token>();
            var combined = new List<Token>();
            var all = new List<Token>();
            var macroTokens = new List<Token>();
            commandNames ??= new List<string>();
            macroCommands ??= new CommandCollection();
            
            var commandTree = CommandNameTree.Create(commandNames);
            var macroMacroTree = CommandNameTree.Create(macroCommands.Commands.Select(x => x.name).ToList());
            void AddToken(Token t)
            {
                tokens.Add(t);
                combined.Add(t);
                all.Add(t);
            
            }

            void AddComment(Token t)
            {
                comments.Add(t);
                combined.Add(t);
                all.Add(t);
            }

            var errors = new List<ParseError>();

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
                //lexems.Add(commandLexem);
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

            var lines = input.Split(new string[]{"\n"}, StringSplitOptions.None).ToList();

            var eolLexem = new Lexem(LexemType.EndStatement, null);

            Token remBlockToken = null;
            var remBlockSb = new StringBuilder();
            var requestEoS = false;
            var requestEoSCharNumber = 0;
            for (var lineNumber = 0; lineNumber < lines.Count; lineNumber++)
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
                        lexem = new Lexem(LexemType.KeywordRem, null, LexemFlags.PreventEos),

                    };
                }

                var charNumberMacroOffset = 0;
                var macroUntilCharNumber = -1;
                for (var charNumber = 0; charNumber < line.Length; charNumber = charNumber)
                {
                    var foundMatch = false;
                    var sub = line.Substring(charNumber);
                    var subStr = sub.ToLowerInvariant();

                    var isStillMacro = charNumber+charNumberMacroOffset < macroUntilCharNumber;
                    var flags = TokenFlags.None;
                    if (isStillMacro)
                    {
                        flags |= TokenFlags.IsConstant;
                    }
                    
                    
                    if (remBlockToken == null && subStr.StartsWith("remstart"))
                    {
                        // we are remmin'
                        remBlockToken = new Token
                        {
                            lexem = new Lexem(LexemType.KeywordRem, null, LexemFlags.PreventEos),
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
                            var text = line.Substring(charNumber);
                            errors.Add(new ParseError(new Token
                            {
                                raw = text,
                                caseInsensitiveRaw = text.ToLowerInvariant(),
                                lineNumber = lineNumber,
                                charNumber = charNumber,
                            }, ErrorCodes.LexerStringNeedsEnd, text));

                        }

                        var insensitiveRaw = line.Substring(charNumber, strIndex - charNumber);
                        var stringLiteralSubStr = strBuffer.ToString();
                        bestToken = new Token
                        {
                            caseInsensitiveRaw = insensitiveRaw.ToLowerInvariant(),
                            raw = stringLiteralSubStr,
                            lexem = LexemString,
                            lineNumber = lineNumber,
                            charNumber = charNumber + charNumberMacroOffset,
                            flags = flags

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
                                charNumber = charNumber + charNumberMacroOffset,
                                flags = flags

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

                                macroTokens.Add(bestToken);
                                all.Add(bestToken);
                                constantTable[toRemove.ToLowerInvariant()] = toAdd;
                                // var prefix = line.Substring(0, charNumber);
                                // var suffix = line.Substring(charNumber + toRemove.Length);
                                //
                                // var replacementLine = prefix + toAdd + suffix;
                                break;
                            case LexemType.VariableGeneral
                                when constantTable.TryGetValue(bestToken.caseInsensitiveRaw, out var replacement):
                                var prefix = line.Substring(0, charNumber);
                                var suffix = line.Substring(charNumber + bestToken.Length);

                                var replacementLine = prefix + replacement + suffix;
                                charNumberMacroOffset += line.Length - replacementLine.Length;
                                line = replacementLine;
                                macroUntilCharNumber = charNumber + bestToken.Length;

                                bestToken.lexem = new Lexem(LexemType.Constant);
                                macroTokens.Add(bestToken);
                                all.Add(bestToken);
                                continue;
                                break;
                            default:

                                
                                if (requestEoS)
                                {
                                    requestEoS = false;
                                    AddToken(new Token
                                    {
                                        charNumber = requestEoSCharNumber ,
                                        lexem = eolLexem,
                                        lineNumber = lineNumber,
                                        caseInsensitiveRaw = "\n",
                                        flags = flags

                                    });
                                }

                                AddToken(bestToken);
                                
                                break;
                        }


                        charNumber += bestToken.Length;
                    }

                    if (!foundMatch)
                    {
                        errors.Add(new ParseError(new Token
                        {
                            raw = sub,
                            caseInsensitiveRaw = sub.ToLowerInvariant(),
                            lineNumber = lineNumber,
                            charNumber = charNumber,
                        }, ErrorCodes.LexerUnmatchedText, sub));
                       
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
                
                var previousTokenWasNotEoS = all.Count > 0 ? all[all.Count - 1].type != LexemType.EndStatement : false;
                var previousTokenWasNotArgSplitter = all.Count > 0 ? all[all.Count - 1].type != LexemType.ArgSplitter : true;

                var previousTokenDoesNotPreventEos = true;
                if (all.Count > 0)
                {
                    // previousTokenDoesNotPreventEos = true;
                    if (all[all.Count - 1].lexem.flags.HasFlag(LexemFlags.PreventEos))
                    {
                        previousTokenDoesNotPreventEos = false;
                    }
                }
                
                // if the next token is an arg splitter, than we don't want an EoS either...
                if (previousTokenWasNotEoS && previousTokenWasNotArgSplitter && previousTokenDoesNotPreventEos)
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
                    lineNumber = lines.Count - 1,
                    caseInsensitiveRaw = "\n"
                });
            }
            
            var results = new LexerResults
            {
                tokens = tokens,
                comments = comments,
                allTokens = all,
                combinedTokens = combined,
                stream = new TokenStream(tokens, errors),
                tokenErrors = errors,
                macroTokens = macroTokens
            };

            HandleMacros2(results, commandNames, macroCommands, macroMacroTree);
            HandleCommandNames(results, commandTree);
            
            return results;
        }

        class MacroBlock
        {
            public int startTokenIndex, endTokenIndex;
            public List<TokenizeBlock> tokenizeBlocks = new List<TokenizeBlock>();
            public List<ParseError> errors = new List<ParseError>();

            public int TokenCount => endTokenIndex - startTokenIndex;

            
        }

        class TokenizeBlock
        {
            public int startTokenIndex, endTokenIndex;
            public List<ParseError> errors = new List<ParseError>();
        }


        void HandleCommandNames(LexerResults results, CommandNameTree tree)
        {
            HandleCommandNames(results.tokens, tree);
            HandleCommandNames(results.combinedTokens, tree);
            HandleCommandNames(results.allTokens, tree);
        }
        void HandleCommandNames(List<Token> tokens, CommandNameTree tree)
        {
            /*
             * The goal is to find token spans that match the command names,
             *  and then replace the token span with a single token representing
             *  the commandWord token.
             *
             * It would be helpful to have the commands in a tree-format
             */
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                var curr = tree;
                var j = i;
                while (tokens[j].caseInsensitiveRaw != null && curr.sub.TryGetValue(tokens[j].caseInsensitiveRaw, out var next))
                {
                    curr = next;
                    j++;
                }

                if (j != i && curr.isValidCommand)
                {
                    // re-write tokens from i to j as a single token.
                    var subset = tokens.Skip(i).Take(j - i);
                    var raw = string.Join(" ", subset.Select(x => x.raw));
                    tokens[i] = new Token
                    {
                        raw = raw,
                        caseInsensitiveRaw = raw.ToLowerInvariant(),
                        lexem = new Lexem(LexemType.CommandWord),
                        charNumber = tokens[i].charNumber,
                        lineNumber = tokens[i].lineNumber,
                        flags = tokens[i].flags,
                    };
                    tokens.RemoveRange(i + 1, (j - i) - 1);
                }
                
            }

            // var t = new Trie<string>(' ');
            // foreach (var command in commandNames)
            // {
            //     t.Insert(command.ToLowerInvariant(), command.ToLowerInvariant());
            // }
            //
            // for (var i = 0; i < tokens.Count; i++)
            // {
            //     switch (tokens[i].type)
            //     {
            //         case LexemType.VariableString:
            //         case LexemType.VariableGeneral:
            //             var matching = t.GetAll(tokens[i].caseInsensitiveRaw);
            //            
            //             break;
            //     }
            // }
        }
        
        void HandleMacros2(LexerResults current, List<string> commandNames, CommandCollection macroCommands, CommandNameTree macroCommandTree)
        {
            var stream = new TokenStream(current.tokens);

            TokenizeBlock ParseTokenizationBlock()
            {
                // assumption is that we are on a start tokenize
                var startIndex = stream.Index;
                var isShortcut = stream.Current.type == LexemType.VariableReal;
                var endIndex = stream.Index;
                stream.Advance();
                var searching = true;
                ParseError error = null;
                while (searching)
                {
                    switch (stream.Current.type)
                    {
                        case LexemType.EndStatement when isShortcut:
                            endIndex = stream.Index;
                            searching = false;
                            stream.Advance();
                            break;
                        case LexemType.ConstantTokenize:
                            error = new ParseError(stream.Current, ErrorCodes.LexerInvalidNestedTokenize);

                            stream.Advance();
                            break;
                        case LexemType.ConstantBegin:
                            error = new ParseError(stream.Current, ErrorCodes.LexerInvalidNestedMacro);
                            stream.Advance();
                            break;
                        case LexemType.ConstantEndTokenize:
                            endIndex = stream.Index;
                            searching = false;
                            stream.Advance();
                            break;
                        default:
                            stream.Advance();
                            break;
                    }
                    
                }
                
                
                
                var block = new TokenizeBlock
                {
                    startTokenIndex = startIndex - 1,
                    endTokenIndex = endIndex - 1,
                };
                if (error != null)
                {
                    block.errors.Add(error);
                }

                return block;
            }

            MacroBlock ParseMacroShortcut()
            {
                // assumption, we are on a # start token.
                var start = stream.Current;
                var startIndex = stream.Index;
                var endIndex = stream.Index;
                stream.Advance(); // move past #
                ParseError error = null;
                var searching = true;
                var tokenBlocks = new List<TokenizeBlock>();
                while (searching)
                {
                    switch (stream.Current.type)
                    {
                        case LexemType.ConstantBegin:
                            // error, we cannot have a nested macro block.
                            error = new ParseError(stream.Current, ErrorCodes.LexerInvalidNestedMacro);
                            stream.Advance();
                            break;
                        case LexemType.VariableReal:
                        case LexemType.ConstantTokenize:
                            var tokenBlock = ParseTokenizationBlock();
                            tokenBlocks.Add(tokenBlock);
                            
                            break;
                        case LexemType.EndStatement:
                            // hoozah!
                            searching = false;
                            endIndex = stream.Index;
                            current.tokens.Insert(endIndex, new Token
                            {
                              lexem = new Lexem(LexemType.EndStatement)
                            });
                            stream.Advance();
                            break;
                        case LexemType.ConstantEnd:
                            error = new ParseError(stream.Current, ErrorCodes.LexerInvalidEndMacro);
                            stream.Advance();
                            break;
                        default:
                            stream.Advance();
                            break;
                    }
                }

                var block = new MacroBlock
                {
                    startTokenIndex = startIndex - 1,
                    endTokenIndex = endIndex,
                    tokenizeBlocks = tokenBlocks
                };
                if (error != null)
                {
                    block.errors.Add(error);
                }
                return block;

            }
            
            MacroBlock ParseMacroBlock()
            {
                
                // assumption, we are on a #macro start token.
                var start = stream.Current;
                var startIndex = stream.Index;
                var endIndex = stream.Index;
                stream.Advance();
                ParseError error = null;
                var searching = true;
                var tokenBlocks = new List<TokenizeBlock>();
                while (searching)
                {
                    switch (stream.Current.type)
                    {
                        case LexemType.EOF:
                            error = new ParseError(start, ErrorCodes.LexerExpectedEndMacro);
                            searching = false;
                            break;
                        case LexemType.ConstantBegin:
                            // error, we cannot have a nested macro block.
                            error = new ParseError(stream.Current, ErrorCodes.LexerInvalidNestedMacro);
                            stream.Advance();
                            break;
                        case LexemType.VariableReal:
                        case LexemType.ConstantTokenize:
                            var tokenBlock = ParseTokenizationBlock();
                            tokenBlocks.Add(tokenBlock);
                            
                            break;
                        case LexemType.ConstantEnd:
                            // hoozah!
                            searching = false;
                            endIndex = stream.Index;
                            stream.Advance();
                            break;
                        default:
                            stream.Advance();
                            break;
                    }
                }

                var block = new MacroBlock
                {
                    startTokenIndex = startIndex - 1,
                    endTokenIndex = endIndex - 1,
                    tokenizeBlocks = tokenBlocks
                };
                if (error != null)
                {
                    block.errors.Add(error);
                }
                return block;

            }

            MacroBlock ParseReverseSubstitution()
            {
                // assumption is current token is [ 
                
                // search for closing ]. 
                // and adjust the token stream such that it maps out to a 
                //  #macro
                //    #tokenize
                //       ___
                //    #endtokenize
                //  #endmacro

                var startIndex = stream.Index;
                var endIndex = startIndex;
                var searching = true;
                stream.Advance();
                while (searching)
                {
                    switch (stream.Current.type)
                    {
                        // TODO: handle EOS and EOF
                        case LexemType.ConstantBracketClose:
                            // hoozah, this is the actual end!
                            endIndex = stream.Index;
                            stream.Advance();
                            searching = false;
                            break;
                        case LexemType.ConstantEndTokenize:
                        case LexemType.ConstantTokenize:
                        case LexemType.ConstantEnd:
                        case LexemType.ConstantBegin:
                        case LexemType.ConstantBracketOpen:
                            throw new NotImplementedException("need to create valid error case");
                            break;
                        default:
                            stream.Advance();
                            break;
                    }
                }
                
                // TODO: this is not right.
                //  need to RUN the program, and _THEN_ tokenize the _OUTPUT_
                
                // inject tokenize expression tokens...
                
                current.tokens.Insert(endIndex - 1, new Token
                {
                    lexem = new Lexem(LexemType.ConstantEndTokenize)
                });
                current.tokens.Insert(endIndex - 1, new Token
                {
                    lexem = new Lexem(LexemType.ConstantBracketClose)
                });
                
                current.tokens.Insert(startIndex, new Token
                {
                    lexem = new Lexem(LexemType.ConstantBracketOpen)
                });
                current.tokens.Insert(startIndex, new Token
                {
                    lexem = new Lexem(LexemType.ConstantTokenize)
                });
                stream.Advance();
                stream.Advance();
                stream.Advance(); // skip ahead of tokens that were just inserted.
                var mb =  new MacroBlock
                {
                    startTokenIndex = startIndex - 1,
                    endTokenIndex = endIndex + 3,
                    tokenizeBlocks = new List<TokenizeBlock>
                    {
                        new TokenizeBlock
                        {
                            startTokenIndex = startIndex,
                            endTokenIndex = endIndex + 2,

                        }
                    }
                };
                return mb;
            }

            var macroBlocks = new List<MacroBlock>();

            stream.Advance();
            while (!stream.IsEof)
            {
                // stream.Advance();
                switch (stream.Current.type)
                {
                    case LexemType.ConstantBracketOpen:
                        // parse until a close bracket. 
                        // stream.Advance();
                        macroBlocks.Add(ParseReverseSubstitution());
                        break;
                    case LexemType.VariableReal when stream.Current.Length == 1:
                        // stream.Advance();
                        macroBlocks.Add(ParseMacroShortcut());
                        break;
                    case LexemType.ConstantBegin:
                        // stream.Advance();
                        macroBlocks.Add(ParseMacroBlock());
                        break;
                    
                    // TODO all of these need to result in errors.
                    case LexemType.ConstantEndTokenize:
                    case LexemType.ConstantEnd:
                    case LexemType.ConstantTokenize:
                        throw new NotImplementedException("need to make better error");
                        break;
                    default:
                        stream.Advance();
                        break;
                }
            }

            // bundle up all the macro blocks next to each other. 
            var compileTokens = new List<Token>();
            
            var macroIndexToCompileTokenIndexStart = new List<int>();
            var errorCount = 0;
            foreach (var macro in macroBlocks)
            { 
                current.tokenErrors.AddRange(macro.errors);
                errorCount += macro.errors.Count;
                macroIndexToCompileTokenIndexStart.Add(compileTokens.Count);
                var tokenSlice = current.tokens
                    .Skip(macro.startTokenIndex + 1)
                    .Take((macro.endTokenIndex - macro.startTokenIndex) - 1)
                    .ToList();
                
                compileTokens.AddRange(tokenSlice);
            }
            if (errorCount > 0)
            {
                return;
            }
            // TODO: these endstatement tokens are the bane of my existence. 
            HandleCommandNames(compileTokens, macroCommandTree);
            var compileStream = new TokenStream(compileTokens);
            // TODO: need to re-handle this stream with macro-level commands. 
            var parser = new Parser(compileStream, macroCommands);
            
            var program = parser.ParseProgram();
            var macroErrors = program.GetAllErrors();
            
            // TODO: adjust the position of the errors so they match the original text. 
            // current.tokenErrors.AddRange(macroErrors);
            // if (macroErrors.Count > 0)
            // {
            //     // remove all macro blocks so errors are not double reported. 
            //     for (var i = macroBlocks.Count - 1; i >= 0; i--)
            //     {
            //         RemoveMacro(i);
            //     }
            //     return;
            // }
            
            
            var compiler = new Compiler(macroCommands);
            compiler.Compile(program);

            var vm = new VirtualMachine(compiler.Program)
            {
                tokenReplacements = new List<TokenReplacement>(),
                hostMethods = compiler.methodTable
            };
            vm.Execute2(0);

            // TODO: the issue is that the compileTokens do not
            //  share an index-space with the original tokens. 
            for (var i = 0; i < vm.tokenReplacements.Count; i++)
            {
                var repl = vm.tokenReplacements[i];
                for (var m = macroBlocks.Count - 1; m >= 0; m--)
                {
                    var index = macroIndexToCompileTokenIndexStart[m];
                    if (repl.tokenStartIndex >= index)
                    {
                        var diff = macroBlocks[m].startTokenIndex - index;
                        repl.tokenStartIndex += diff;
                        repl.tokenEndIndex += diff;
                        foreach (var sub in repl.substitutionReplacements)
                        {
                            sub.tokenStartIndex += diff;
                            sub.tokenEndIndex += diff;
                        }
                        break;
                    }
                }
            }
            // var replIndex = 0;
            // for (var i = 0; i < macroBlocks.Count; i++)
            // {
            //     var compileTokenIndex = macroIndexToCompileTokenIndexStart[i];
            //     
            // }
            
            /*
             * removing all of the macro blocks means the token coordinates in the vm will not match
             * 
             */

            // create a flat list of tokenization blocks, but map them back to their associated macro
            var tokenizationMap = new List<(int macroIndex, TokenizeBlock)>();
            for (var i = 0; i < macroBlocks.Count; i++)
            {
                for (var j = 0; j < macroBlocks[i].tokenizeBlocks.Count; j++)
                {
                    tokenizationMap.Add( (i, macroBlocks[i].tokenizeBlocks[j]));
                }
            }

            Token Concat(Token left, Token right)
            {
                var res = TokenizeWithErrors(left.raw + right.raw, commandNames);
                var token = res.tokens[0];
                token.charNumber = left.charNumber;
                token.lineNumber = left.lineNumber;
                return token;
            }

            void InsertToken(int index, Token t)
            {
                // three cases
                // 1. the token being added is a compiler-generated token
                // 2. the token being added is adjacent to a compiler-generated token
                // 3. neither. 

                var isCompilerToken = t.flags.HasFlag(TokenFlags.IsCompileTime);
                var hasNeighbor = current.tokens.Count > index;
                if (!hasNeighbor)
                {
                    current.tokens.Insert(index, t);
                    return;
                }
                var neighborToken = current.tokens[index];
                var isNextToCompilerToken = neighborToken.flags.HasFlag(TokenFlags.IsCompileTime);
                var bothConcatable = neighborToken.lexem.flags.HasFlag(LexemFlags.MacroConcatable) &&
                                       t.lexem.flags.HasFlag(LexemFlags.MacroConcatable);

                if (bothConcatable)
                {


                    // case 2. If the token is next to a compiler token, then 
                    //  IF the tokens are exactly bordering (no whitespace), they will be concat'd 
                    if (isNextToCompilerToken)
                    {
                        if (t.lineNumber == neighborToken.lineNumber && t.EndCharNumber == neighborToken.charNumber)
                        {
                            // concat!
                            current.tokens[index] = Concat(t, neighborToken);
                            return;
                        }
                    }

                    // case 1. If the token IS a compiler then, then
                    //  IF the tokens are exactly bordering (no whitespace0, they will be concat'd
                    if (isCompilerToken)
                    {
                        if (t.flags.HasFlag(TokenFlags.IsAdjacentToRightSib))
                        {
                            current.tokens[index] = Concat(t, neighborToken);
                            current.tokens[index].flags |= TokenFlags.IsCompileTime;
                            return;
                        }
                    }
                }

                // case 3. Neither. So just insert the token.
                current.tokens.Insert(index, t);
            }

            void RemoveMacro(int macroBlockIndex)
            {
                if (macroBlockIndex >= macroBlocks.Count) return;
                var mb = macroBlocks[macroBlockIndex];
                current.tokens.RemoveRange(mb.startTokenIndex, (mb.endTokenIndex - mb.startTokenIndex) +1 );
            }
            
            // now the assumption is that the tokenizationMap aligns with the vm replacements. 
            // which means it should be possible to move BACKWARDS, adding in tokens to the associated macro statements,
            // and then in one fell swoop, removing macros.
            var previousMacroBlockIndex = macroBlocks.Count - 1;
            for (var i = vm.tokenReplacements.Count - 1; i >= 0; i--)
            {
                var replacement = vm.tokenReplacements[i];
                var (macroIndex, tokenBlock) = tokenizationMap[replacement.tokenBlockIndex];
                var macroBlock = macroBlocks[macroIndex];

                if (previousMacroBlockIndex != macroIndex)
                {
                    RemoveMacro(previousMacroBlockIndex);
                }
                previousMacroBlockIndex = macroIndex;
                
                // walk backwards through all the tokens and handle the replacements. 
                // var substIndex = replacement.substitutionReplacements.Count - 1;
                var substIndex = 0;
                for (var x = replacement.tokenEndIndex; x >= replacement.tokenStartIndex; x--)
                {
                    var token = current.tokens[x];
                    if (substIndex >= replacement.substitutionReplacements.Count)
                    {
                        // there are no more replacements, which means we can just take all of these tokens.
                        //current.tokens.Insert(macroBlock.endTokenIndex + 1, token); // stick it at the end of the macro.
                        InsertToken(macroBlock.endTokenIndex + 1, token);
                        continue;
                    }

                    var subst = replacement.substitutionReplacements[substIndex];

                    var isTokenBeforeSubstEnd = x <= subst.tokenEndIndex + 1;
                    var isTokenBeforeSubstStart = x <= subst.tokenStartIndex + 1;

                    if (!isTokenBeforeSubstEnd)
                    {
                        // the token is not being substituted, so we can just add it. 
                        //current.tokens.Insert(macroBlock.endTokenIndex + 1, token); // stick it at the end of the macro.
                        InsertToken(macroBlock.endTokenIndex + 1, token);
                        
                        continue;
                    }

                    if (isTokenBeforeSubstEnd && !isTokenBeforeSubstStart)
                    {
                        // oh oh oh , this is the substitution itself! which means we are not inserting the raw token, we are using the final value. 
                        substIndex += 1;
                        var text = subst.raw.ToString();
                        var tokenResults = TokenizeWithErrors(text, commandNames, macroCommands);
                         
                       // for (var n = tokenResults.tokens.Count - 1; n >= 0; n--)
                        {
                            var fakeToken = tokenResults.tokens[0];
                            fakeToken.flags |= TokenFlags.IsCompileTime;
                            // this token needs to adopt the position of the substitution

                            fakeToken.lineNumber = current.tokens[subst.tokenStartIndex].lineNumber;
                            fakeToken.charNumber = current.tokens[subst.tokenStartIndex].charNumber;

                            if (current.tokens.Count > subst.tokenEndIndex + 2)
                            {
                                var closeBracketToken = current.tokens[subst.tokenEndIndex + 1];
                                var nextToken = current.tokens[subst.tokenEndIndex + 2];
                                if (closeBracketToken.lineNumber == nextToken.lineNumber &&
                                    closeBracketToken.EndCharNumber == nextToken.charNumber)
                                {
                                    fakeToken.flags |= TokenFlags.IsAdjacentToRightSib;
                                }
                            }

                            InsertToken(macroBlock.endTokenIndex + 1, fakeToken);

                        }

                        x = subst.tokenStartIndex;
                    }
                }
                
            }

            for (var i = previousMacroBlockIndex; i >= 0; i--)
            {
                RemoveMacro(i); // hard-code zero, because we should have processed all macro blocks EXCEPT we wouldn't have finished zero.
                
            }

        }
        
    }

    public class CommandNameTree
    {
        public bool isValidCommand;
        public Dictionary<string, CommandNameTree> sub = new Dictionary<string, CommandNameTree>();
        
        public CommandNameTree(){}

        public static CommandNameTree Create(List<string> commands)
        {
            var root = new CommandNameTree();
            foreach (var command in commands)
            {
                var parts = command.Split(' ');
                var curr = root;
                foreach (var part in parts)
                {
                    var caseInsensitivePart = part.ToLowerInvariant();
                    if (!curr.sub.TryGetValue(caseInsensitivePart, out var existing))
                    {
                        curr.sub[caseInsensitivePart] = existing = new CommandNameTree();
                    }
                    curr = existing;
                }

                curr.isValidCommand = true;
            }

            return root;
        }
        
      
    }
    
    [Flags]
    public enum LexemFlags
    {
        None = 0,
        MacroConcatable = 1 << 0, // 1
        PreventEos = 1 << 2 // 2
        // 1 << 3 // 4
    }
    
    public class Lexem
    {
        public LexemFlags flags;
        public readonly Regex regex;
        public int priority;
        public readonly LexemType type;
        

        public Lexem()
        {
        }

        public Lexem(LexemType type)
        {
            this.type = type;
            this.flags = LexemFlags.None;
        }
        
        public Lexem(LexemType type, Regex regex)
        {
            this.type = type;
            this.regex = regex;
            this.flags = LexemFlags.None;
        }
        
        public Lexem(LexemType type, Regex regex, LexemFlags flags)
        {
            this.type = type;
            this.regex = regex;
            this.flags = flags;
        }

        public Lexem(int priority, LexemType type, Regex regex)
        {
            this.priority = priority;
            this.type = type;
            this.regex = regex;
            this.flags = LexemFlags.None;
            
        }
        public Lexem(int priority, LexemType type, Regex regex, LexemFlags flags)
        {
            this.priority = priority;
            this.type = type;
            this.regex = regex;
            this.flags = flags;
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
        
        /// <summary>
        /// This flag indicates that the given token was expanded from a macro consant
        /// </summary>
        IsConstant = 1 << 1,
        
        /// <summary>
        /// should these tokens be running during the lexer phase?
        /// </summary>
        IsCompileTime = 1 << 2,
            
        /// <summary>
        /// used for macro handling, included when the substitution's end token was right next to the following token in the stream.
        /// this is used to decide if concat should happen
        /// </summary>
        IsAdjacentToRightSib = 1 << 3,
        
        // Fourth = 1 << 3
    }
    
    [Serializable]
    [DebuggerDisplay("{raw} ({type}:{lineNumber}:{charNumber})")]
    public class Token : IJsonable
    {
        public static readonly Token Blank = new Token();
        public static readonly Token Local = new Token{caseInsensitiveRaw = "local", raw = "local"};
        public static readonly Token Global = new Token{caseInsensitiveRaw = "global", raw = "global"};
        
        public int lineNumber;
        public int charNumber;
        public string raw;
        public string caseInsensitiveRaw;
        
        public int Length => caseInsensitiveRaw?.Length ?? 0;
        public int EndCharNumber => charNumber + Length;
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
        public List<ParseError> Errors { get; }
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

        public TokenStream(List<Token> tokens) : this(tokens, new List<ParseError>())
        {
        }

        public TokenStream(List<Token> tokens, List<ParseError> errors)
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
            if (Index >= _tokens.Count)
            {
                return Current = new Token
                {
                    lexem = new Lexem(LexemType.EOF, null)
                };
            }
            return Current = _tokens[Index++];
        }

        public Token AdvanceUntil(LexemType type)
        {
            while (Current.type != type && Current.type != LexemType.EOF)
            {
                if (Index >= _tokens.Count)
                {
                    // hit end of stream.
                    return new Token
                    {
                        lexem = new Lexem(LexemType.EndStatement)
                    };
                }
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