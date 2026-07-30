using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FadeBasic.Ast;
using FadeBasic.Json;
using FadeBasic.Sdk;
using FadeBasic.SourceGenerators;
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
        
        KeywordDefer,
        KeywordEndDefer,

        KeywordTest,
        KeywordEndTest,
        KeywordAbstract,
        KeywordFrom,
        KeywordRunto,
        KeywordEndRunto,
        KeywordMaxCycles,
        KeywordAssert,
        KeywordMock,
        KeywordEndMock,
        KeywordExitMock,
        KeywordForbid,
        KeywordClear,
        KeywordMocks,
        KeywordCallCount,
        KeywordLen,
        KeywordDims,
        KeywordBytes,

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
        public Dictionary<string, string> constantTable;
        public List<ParseError> tokenErrors;
        public List<Token> macroTokens = new List<Token>();
        public ProgramNode macroProgram;
        public LexerResults()
        {
            
        }
    }

    public class Lexer
    {
        private static Lexem LexemString = new Lexem(LexemType.LiteralString, new Regex("^\""), LexemFlags.MacroConcatable);
        private static Lexem LexemConstant = new Lexem(LexemType.Constant);
        private static readonly List<Lexem> _sortedLexems;
        private static readonly Dictionary<LexemType, Lexem> _lexemForType;
        private static readonly Dictionary<string, LexemType> _keywords;
        private static readonly Regex _rxConstant;

        private readonly StringBuilder _strBuffer = new StringBuilder();

        static Lexer()
        {
            _sortedLexems = Lexems
                .Select(l => l.regex == null
                    ? l
                    : new Lexem(l.priority, l.type,
                        new Regex(l.regex.ToString(), l.regex.Options | RegexOptions.IgnoreCase),
                        l.flags))
                .OrderBy(l => l.priority)
                .ToList();

            _lexemForType = new Dictionary<LexemType, Lexem>();
            foreach (var l in Lexems)
            {
                if (!_lexemForType.ContainsKey(l.type))
                    _lexemForType[l.type] = l;
            }
            foreach (LexemType lt in Enum.GetValues(typeof(LexemType)))
            {
                if (!_lexemForType.ContainsKey(lt))
                    _lexemForType[lt] = new Lexem(lt);
            }

            _rxConstant = _sortedLexems.Find(l => l.type == LexemType.Constant).regex;

            _keywords = new Dictionary<string, LexemType>(StringComparer.OrdinalIgnoreCase)
            {
                ["for"]          = LexemType.KeywordFor,
                ["to"]           = LexemType.KeywordTo,
                ["step"]         = LexemType.KeywordStep,
                ["next"]         = LexemType.KeywordNext,
                ["endfunction"]  = LexemType.KeywordEndFunction,
                ["function"]     = LexemType.KeywordFunction,
                ["exitfunction"] = LexemType.KeywordExitFunction,
                ["do"]           = LexemType.KeywordDo,
                ["loop"]         = LexemType.KeywordLoop,
                ["select"]       = LexemType.KeywordSelect,
                ["endselect"]    = LexemType.KeywordEndSelect,
                ["case"]         = LexemType.KeywordCase,
                ["endcase"]      = LexemType.KeywordEndCase,
                ["default"]      = LexemType.KeywordCaseDefault,
                ["repeat"]       = LexemType.KeywordRepeat,
                ["until"]        = LexemType.KeywordUntil,
                ["local"]        = LexemType.KeywordScope,
                ["global"]       = LexemType.KeywordScope,
                ["if"]           = LexemType.KeywordIf,
                ["endif"]        = LexemType.KeywordEndIf,
                ["else"]         = LexemType.KeywordElse,
                ["then"]         = LexemType.KeywordThen,
                ["end"]          = LexemType.KeywordEnd,
                ["exit"]         = LexemType.KeywordExit,
                ["skip"]         = LexemType.KeywordSkip,
                ["enddefer"]     = LexemType.KeywordEndDefer,
                ["defer"]        = LexemType.KeywordDefer,
                ["endtest"]      = LexemType.KeywordEndTest,
                ["test"]         = LexemType.KeywordTest,
                ["abstract"]     = LexemType.KeywordAbstract,
                ["from"]         = LexemType.KeywordFrom,
                ["endrunto"]     = LexemType.KeywordEndRunto,
                ["runto"]        = LexemType.KeywordRunto,
                ["assert"]       = LexemType.KeywordAssert,
                ["endmock"]      = LexemType.KeywordEndMock,
                ["mocks"]        = LexemType.KeywordMocks,
                ["mock"]         = LexemType.KeywordMock,
                ["exitmock"]     = LexemType.KeywordExitMock,
                ["forbid"]       = LexemType.KeywordForbid,
                ["clear"]        = LexemType.KeywordClear,
                ["goto"]         = LexemType.KeywordGoto,
                ["gosub"]        = LexemType.KeywordGoSub,
                ["return"]       = LexemType.KeywordReturn,
                ["dim"]          = LexemType.KeywordDeclareArray,
                ["redim"]        = LexemType.KeywordReDimArray,
                ["remstart"]     = LexemType.KeywordRemStart,
                ["remend"]       = LexemType.KeywordRemEnd,
                ["rem"]          = LexemType.KeywordRem,
                ["type"]         = LexemType.KeywordType,
                ["endtype"]      = LexemType.KeywordEndType,
                ["while"]        = LexemType.KeywordWhile,
                ["endwhile"]     = LexemType.KeywordEndWhile,
                ["as"]           = LexemType.KeywordAs,
                ["boolean"]      = LexemType.KeywordTypeBoolean,
                ["bool"]         = LexemType.KeywordTypeBoolean,
                ["byte"]         = LexemType.KeywordTypeByte,
                ["integer"]      = LexemType.KeywordTypeInteger,
                ["int"]          = LexemType.KeywordTypeInteger,
                ["word"]         = LexemType.KeywordTypeWord,
                ["ushort"]       = LexemType.KeywordTypeWord,
                ["dword"]        = LexemType.KeywordTypeDWord,
                ["uint"]         = LexemType.KeywordTypeDWord,
                ["long"]         = LexemType.KeywordTypeDoubleInteger,
                ["float"]        = LexemType.KeywordTypeFloat,
                ["double"]       = LexemType.KeywordTypeDoubleFloat,
                ["string"]       = LexemType.KeywordTypeString,
                ["not"]          = LexemType.KeywordNot,
                ["and"]          = LexemType.KeywordAnd,
                ["or"]           = LexemType.KeywordOr,
                ["xor"]          = LexemType.KeywordXor,
                ["mod"]          = LexemType.OpMod,
                ["len"]          = LexemType.KeywordLen,
                ["dims"]         = LexemType.KeywordDims,
                ["bytes"]        = LexemType.KeywordBytes,
            };
        }
        // private static Lexem LexemConstantBegin = new Lexem(LexemType.Constant);
        // private static Lexem LexemConstant = new Lexem(LexemType.Constant);
        public static List<Lexem> Lexems = new List<Lexem>
        {
            new Lexem(LexemType.Constant, new Regex("^\\s*#constant\\s+([a-zA-Z][a-zA-Z0-9_]*)\\s+(.*)\\s*$")),
            new Lexem(LexemType.ConstantBegin, new Regex("^#macro\\b")),
            new Lexem(LexemType.ConstantEnd, new Regex("^#endmacro\\b")),
            new Lexem(LexemType.ConstantTokenize, new Regex("^#tokenize\\b")),
            new Lexem(LexemType.ConstantEndTokenize, new Regex("^#endtokenize\\b")),
            new Lexem(LexemType.ConstantBracketOpen, new Regex("^\\[")),
            new Lexem(LexemType.ConstantBracketClose, new Regex("^\\]")),
            
            
            new Lexem(LexemType.EndStatement, new Regex("^:")),
            new Lexem(LexemType.ArgSplitter, new Regex("^,")),
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
            
            new Lexem(LexemType.KeywordEndDefer, new Regex("^enddefer")),
            new Lexem(LexemType.KeywordDefer, new Regex("^defer")),

            new Lexem(LexemType.KeywordEndTest, new Regex("^endtest\\b")),
            new Lexem(LexemType.KeywordTest, new Regex("^test\\b")),
            new Lexem(LexemType.KeywordAbstract, new Regex("^abstract\\b")),
            new Lexem(LexemType.KeywordFrom, new Regex("^from\\b")),
            new Lexem(LexemType.KeywordEndRunto, new Regex("^endrunto\\b")),
            new Lexem(LexemType.KeywordRunto, new Regex("^runto\\b")),
            // Multi-word keyword: `max cycles`. Matches one or more spaces/tabs
            // between the two words; ranks higher (more specific) than VariableGeneral.
            new Lexem(-2, LexemType.KeywordMaxCycles, new Regex("^max[ \\t]+cycles\\b")),
            new Lexem(LexemType.KeywordAssert, new Regex("^assert\\b")),

            new Lexem(LexemType.KeywordEndMock, new Regex("^endmock\\b")),
            new Lexem(LexemType.KeywordMocks, new Regex("^mocks\\b")),
            new Lexem(LexemType.KeywordMock, new Regex("^mock\\b")),
            new Lexem(LexemType.KeywordExitMock, new Regex("^exitmock\\b")),
            new Lexem(LexemType.KeywordForbid, new Regex("^forbid\\b")),
            new Lexem(LexemType.KeywordClear, new Regex("^clear\\b")),
            // Multi-word keyword: `call count`. Higher priority (-2) so it
            // matches before VariableGeneral; users who write `call` alone
            // (or `call somethingElse`) still get a VariableGeneral token.
            new Lexem(-2, LexemType.KeywordCallCount, new Regex("^call[ \\t]+count\\b")),
            new Lexem(-2, LexemType.KeywordLen, new Regex("^len\\b")),
            new Lexem(-2, LexemType.KeywordDims, new Regex("^dims\\b")),
            new Lexem(-2, LexemType.KeywordBytes, new Regex("^bytes\\b")),

            new Lexem(LexemType.KeywordGoto, new Regex("^goto")),
            new Lexem(LexemType.KeywordGoSub, new Regex("^gosub")),
            new Lexem(LexemType.KeywordReturn, new Regex("^return")),
            
            new Lexem(LexemType.KeywordDeclareArray, new Regex("^dim")),
            // new Lexem(LexemType.KeywordUnDeclareArray, new Regex("^undim")),
            new Lexem(LexemType.KeywordReDimArray, new Regex("^redim")),

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
            
            new Lexem(-2, LexemType.VariableString, new Regex("^([a-zA-Z_]?[a-zA-Z0-9_]*)\\$"), LexemFlags.MacroConcatable),
            new Lexem(-2, LexemType.VariableReal, new Regex("^([a-zA-Z_]?[a-zA-Z0-9_]*)#"), LexemFlags.MacroConcatable),
            new Lexem( 2, LexemType.VariableGeneral, new Regex("^[a-zA-Z_][a-zA-Z0-9_]*"), LexemFlags.MacroConcatable),
            // new Lexem(-2, LexemType.Label, new Regex("^[a-zA-Z][a-zA-Z0-9_]*:")),
        };

        public List<Token> Tokenize(string input, CommandCollection commands = default)
        {
            var res = TokenizeWithErrors(input, commands);
            return res.tokens;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
        private static bool IsHexDigit(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private static bool LineIsKeyword(string src, int absStart, int lineEnd, string kw)
        {
            int end = absStart + kw.Length;
            if (end > lineEnd) return false;
            if (string.Compare(src, absStart, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            return end >= lineEnd || !IsWordChar(src[end]);
        }

        private static bool CharPositionStartsWith(string src, int absStart, int lineEnd, string keyword) =>
            absStart + keyword.Length <= lineEnd &&
            string.Compare(src, absStart, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0;

        private static bool TryMatchManual(
            string src, int absStart, int lineEnd,
            out Lexem lexemOut, out int lengthOut, out Match matchOut)
        {
            matchOut = null;
            lexemOut = null;
            lengthOut = 0;

            if (absStart >= lineEnd) return false;
            char c = src[absStart];
            int start = absStart;

            switch (c)
            {
                case ':': lexemOut = _lexemForType[LexemType.EndStatement];         lengthOut = 1; return true;
                case ',': lexemOut = _lexemForType[LexemType.ArgSplitter];          lengthOut = 1; return true;
                case '(': lexemOut = _lexemForType[LexemType.ParenOpen];            lengthOut = 1; return true;
                case ')': lexemOut = _lexemForType[LexemType.ParenClose];           lengthOut = 1; return true;
                case '{': lexemOut = _lexemForType[LexemType.BracketOpen];          lengthOut = 1; return true;
                case '}': lexemOut = _lexemForType[LexemType.BracketClose];         lengthOut = 1; return true;
                case '+': lexemOut = _lexemForType[LexemType.OpPlus];               lengthOut = 1; return true;
                case '-': lexemOut = _lexemForType[LexemType.OpMinus];              lengthOut = 1; return true;
                case '*': lexemOut = _lexemForType[LexemType.OpMultiply];           lengthOut = 1; return true;
                case '/': lexemOut = _lexemForType[LexemType.OpDivide];             lengthOut = 1; return true;
                case '^': lexemOut = _lexemForType[LexemType.OpPower];              lengthOut = 1; return true;
                case '=': lexemOut = _lexemForType[LexemType.OpEqual];              lengthOut = 1; return true;
                case '[': lexemOut = _lexemForType[LexemType.ConstantBracketOpen];  lengthOut = 1; return true;
                case ']': lexemOut = _lexemForType[LexemType.ConstantBracketClose]; lengthOut = 1; return true;

                case '`':
                    lexemOut = _lexemForType[LexemType.KeywordRem];
                    lengthOut = lineEnd - start;
                    return true;

                case '&':
                    if (start + 1 < lineEnd && src[start + 1] == '&')
                    { lexemOut = _lexemForType[LexemType.OpBitwiseAnd]; lengthOut = 2; return true; }
                    return false;
                case '|':
                    if (start + 1 < lineEnd && src[start + 1] == '|')
                    { lexemOut = _lexemForType[LexemType.OpBitwiseOr]; lengthOut = 2; return true; }
                    return false;
                case '~':
                    if (start + 1 < lineEnd && src[start + 1] == '~')
                    { lexemOut = _lexemForType[LexemType.OpBitwiseXor]; lengthOut = 2; return true; }
                    return false;

                case '>':
                {
                    char next = start + 1 < lineEnd ? src[start + 1] : '\0';
                    if (next == '=') { lexemOut = _lexemForType[LexemType.OpGte];               lengthOut = 2; return true; }
                    if (next == '>') { lexemOut = _lexemForType[LexemType.OpBitwiseRightShift]; lengthOut = 2; return true; }
                    lexemOut = _lexemForType[LexemType.OpGt]; lengthOut = 1; return true;
                }
                case '<':
                {
                    char next = start + 1 < lineEnd ? src[start + 1] : '\0';
                    if (next == '=') { lexemOut = _lexemForType[LexemType.OpLte];              lengthOut = 2; return true; }
                    if (next == '<') { lexemOut = _lexemForType[LexemType.OpBitwiseLeftShift]; lengthOut = 2; return true; }
                    if (next == '>') { lexemOut = _lexemForType[LexemType.OpNotEqual];         lengthOut = 2; return true; }
                    lexemOut = _lexemForType[LexemType.OpLt]; lengthOut = 1; return true;
                }

                case '.':
                {
                    char next = start + 1 < lineEnd ? src[start + 1] : '\0';
                    if (next == '.') { lexemOut = _lexemForType[LexemType.OpBitwiseNot]; lengthOut = 2; return true; }
                    if (next >= '0' && next <= '9')
                    {
                        int end = start + 2;
                        while (end < lineEnd && char.IsDigit(src[end])) end++;
                        lexemOut = _lexemForType[LexemType.LiteralReal];
                        lengthOut = end - start;
                        return true;
                    }
                    lexemOut = _lexemForType[LexemType.FieldSplitter]; lengthOut = 1; return true;
                }

                case '%':
                {
                    int end = start + 1;
                    while (end < lineEnd && (src[end] == '0' || src[end] == '1')) end++;
                    if (end > start + 1) { lexemOut = _lexemForType[LexemType.LiteralBinary]; lengthOut = end - start; return true; }
                    return false;
                }

                case '#':
                {
                    if (LineIsKeyword(src, start + 1, lineEnd, "endtokenize")) { lexemOut = _lexemForType[LexemType.ConstantEndTokenize]; lengthOut = 12; return true; }
                    if (LineIsKeyword(src, start + 1, lineEnd, "endmacro"))    { lexemOut = _lexemForType[LexemType.ConstantEnd];         lengthOut = 9;  return true; }
                    if (LineIsKeyword(src, start + 1, lineEnd, "tokenize"))    { lexemOut = _lexemForType[LexemType.ConstantTokenize];    lengthOut = 9;  return true; }
                    if (LineIsKeyword(src, start + 1, lineEnd, "macro"))       { lexemOut = _lexemForType[LexemType.ConstantBegin];       lengthOut = 6;  return true; }
                    var constSub = src.Substring(start, lineEnd - start);
                    var constMatch = _rxConstant.Match(constSub);
                    if (constMatch.Success)
                    {
                        matchOut = constMatch;
                        lexemOut = _lexemForType[LexemType.Constant];
                        lengthOut = constMatch.Length;
                        return true;
                    }
                    // Bare '#' — VariableReal with empty identifier prefix (matches original regex behaviour)
                    lexemOut = _lexemForType[LexemType.VariableReal]; lengthOut = 1; return true;
                }

                case ' ':
                case '\t':
                case '\r':
                case '\n':
                {
                    int end = start + 1;
                    while (end < lineEnd && (src[end] == ' ' || src[end] == '\t' || src[end] == '\r' || src[end] == '\n')) end++;
                    lexemOut = _lexemForType[LexemType.WhiteSpace];
                    lengthOut = end - start;
                    return true;
                }

                default:
                {
                    if (c == '$')
                    { lexemOut = _lexemForType[LexemType.VariableString]; lengthOut = 1; return true; }

                    if (c >= '0' && c <= '9')
                    {
                        char next = start + 1 < lineEnd ? src[start + 1] : '\0';
                        if (c == '0' && (next == 'x' || next == 'X'))
                        {
                            int end = start + 2;
                            while (end < lineEnd && IsHexDigit(src[end])) end++;
                            if (end > start + 2) { lexemOut = _lexemForType[LexemType.LiteralHex]; lengthOut = end - start; return true; }
                            return false;
                        }
                        if (c == '0' && (next == 'c' || next == 'C'))
                        {
                            int end = start + 2;
                            while (end < lineEnd && src[end] >= '0' && src[end] <= '7') end++;
                            if (end > start + 2) { lexemOut = _lexemForType[LexemType.LiteralOctal]; lengthOut = end - start; return true; }
                            return false;
                        }
                        int iEnd = start + 1;
                        while (iEnd < lineEnd && char.IsDigit(src[iEnd])) iEnd++;
                        if (iEnd < lineEnd && src[iEnd] == '.')
                        {
                            int rEnd = iEnd + 1;
                            while (rEnd < lineEnd && char.IsDigit(src[rEnd])) rEnd++;
                            lexemOut = _lexemForType[LexemType.LiteralReal]; lengthOut = rEnd - start; return true;
                        }
                        if (iEnd < lineEnd && src[iEnd] == '$')
                        { lexemOut = _lexemForType[LexemType.VariableString]; lengthOut = iEnd - start + 1; return true; }
                        if (iEnd < lineEnd && src[iEnd] == '#')
                        { lexemOut = _lexemForType[LexemType.VariableReal]; lengthOut = iEnd - start + 1; return true; }
                        lexemOut = _lexemForType[LexemType.LiteralInt]; lengthOut = iEnd - start; return true;
                    }

                    if (char.IsLetter(c) || c == '_')
                    {
                        int idEnd = start + 1;
                        while (idEnd < lineEnd && IsWordChar(src[idEnd])) idEnd++;

                        if (idEnd < lineEnd && src[idEnd] == '$')
                        { lexemOut = _lexemForType[LexemType.VariableString]; lengthOut = idEnd - start + 1; return true; }
                        if (idEnd < lineEnd && src[idEnd] == '#')
                        { lexemOut = _lexemForType[LexemType.VariableReal]; lengthOut = idEnd - start + 1; return true; }

                        var ident = src.Substring(start, idEnd - start);
                        if (!_keywords.TryGetValue(ident, out var kwType))
                        {
                            int identLen = idEnd - start;
                            if (identLen == 3 && string.Compare(src, start, "max", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
                            {
                                int p = idEnd;
                                while (p < lineEnd && (src[p] == ' ' || src[p] == '\t')) p++;
                                if (p > idEnd && LineIsKeyword(src, p, lineEnd, "cycles"))
                                { lexemOut = _lexemForType[LexemType.KeywordMaxCycles]; lengthOut = p + 6 - start; return true; }
                            }
                            else if (identLen == 4 && string.Compare(src, start, "call", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                            {
                                int p = idEnd;
                                while (p < lineEnd && (src[p] == ' ' || src[p] == '\t')) p++;
                                if (p > idEnd && LineIsKeyword(src, p, lineEnd, "count"))
                                { lexemOut = _lexemForType[LexemType.KeywordCallCount]; lengthOut = p + 5 - start; return true; }
                            }
                            lexemOut = _lexemForType[LexemType.VariableGeneral]; lengthOut = idEnd - start; return true;
                        }

                        if (kwType == LexemType.KeywordRem)
                        { lexemOut = _lexemForType[LexemType.KeywordRem]; lengthOut = lineEnd - start; return true; }

                        if (kwType == LexemType.KeywordTypeDoubleFloat)
                        {
                            if (idEnd < lineEnd && src[idEnd] == ' ')
                            {
                                if (LineIsKeyword(src, idEnd + 1, lineEnd, "float"))
                                { lexemOut = _lexemForType[LexemType.KeywordTypeDoubleFloat]; lengthOut = idEnd + 6 - start; return true; }
                                if (LineIsKeyword(src, idEnd + 1, lineEnd, "integer"))
                                { lexemOut = _lexemForType[LexemType.KeywordTypeDoubleInteger]; lengthOut = idEnd + 8 - start; return true; }
                            }
                        }

                        lexemOut = _lexemForType[kwType]; lengthOut = idEnd - start; return true;
                    }

                    return false;
                }
            }
        }

        public LexerResults TokenizeWithErrors(string input, CommandCollection commands=default)
        {
            var tokens = new List<Token>();
            var comments = new List<Token>();
            var combined = new List<Token>();
            var all = new List<Token>();
            var macroTokens = new List<Token>();
            commands ??= new CommandCollection();
            // var runtimeCommandNames = commands.Commands?.Where(x => x.usage.HasFlag(FadeBasicCommandUsage.Runtime)).Select(c => c.name).ToList() ?? new List<string>();
            // Cached on the collection — building this trie is constant across
            // reparses (see CommandCollection.CommandTree).
            var runtimeCommandTree = commands.CommandTree;
            void AddToken(Token t)
            {
                tokens.Add(t);
                combined.Add(t);
                all.Add(t);
            }

            void FlushEos(ref bool requestEoS, int requestEoSCharNumber, int[] lineEndsArr, int[] lineStartsArr, int lineNumber, Lexem eolLexem, TokenFlags flags)
            {
                if (requestEoS)
                {
                    requestEoS = false;

                    var previousToken = all.LastOrDefault();
                    var cn = previousToken == null
                        ? requestEoSCharNumber
                        : lineEndsArr[previousToken.lineNumber] - lineStartsArr[previousToken.lineNumber] + 1; // synthetic index
                    var ln = previousToken == null
                        ? lineNumber
                        : previousToken.lineNumber;
                    AddToken(new Token
                    {
                        charNumber = cn,
                        lexem = eolLexem,
                        lineNumber = ln,
                        caseInsensitiveRaw = "\n",
                        flags = flags
                    });
                }
            }

            void AddComment(Token t)
            {
                comments.Add(t);
                combined.Add(t);
                all.Add(t);
            }

            var errors = new List<ParseError>();

            var constantTable = new Dictionary<string, string>(comparer: StringComparer.InvariantCultureIgnoreCase);

            // Build line boundary index without allocating per-line strings.
            int lineCount = 1;
            for (int ci = 0; ci < input.Length; ci++) if (input[ci] == '\n') lineCount++;
            var lineStarts = new int[lineCount];
            var lineEnds   = new int[lineCount];
            {
                int li = 0;
                lineStarts[0] = 0;
                for (int ci = 0; ci < input.Length; ci++)
                {
                    if (input[ci] == '\n')
                    {
                        lineEnds[li] = ci;   // end of this line (exclusive, points at \n)
                        lineStarts[++li] = ci + 1;
                    }
                }
                lineEnds[lineCount - 1] = input.Length;
            }

            var eolLexem = new Lexem(LexemType.EndStatement, null);

            Token remBlockToken = null;
            var requestEoS = false;
            var requestEoSCharNumber = 0;
            for (var lineNumber = 0; lineNumber < lineCount; lineNumber++)
            {
                // src/lineStart allow the mutation path (constant substitution) to swap in a
                // local string without touching the original input or the line-index arrays.
                string src = input;
                int lineStart = lineStarts[lineNumber];
                int lineLen   = lineEnds[lineNumber] - lineStart;
                int lineEnd   = lineStart + lineLen;   // absolute end of line content in src

                if (lineLen == 0)
                {
                    if (remBlockToken != null)
                    {
                        AddComment(new Token
                        {
                            lineNumber = lineNumber,
                            charNumber = 0,
                            caseInsensitiveRaw = Environment.NewLine,
                            raw = Environment.NewLine,
                            type = remBlockToken.type,
                            lexemFlags = remBlockToken.lexemFlags
                            // lexem = remBlockToken.lexem
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

                var charNumberMacroOffset = 0;
                var macroUntilCharNumber = -1;
                for (var charNumber = 0; charNumber < lineLen; charNumber = charNumber)
                {
                    var foundMatch = false;

                    var isStillMacro = charNumber + charNumberMacroOffset < macroUntilCharNumber;
                    var flags = TokenFlags.None;
                    if (isStillMacro)
                    {
                        flags |= TokenFlags.IsConstant;
                    }

                    if (remBlockToken == null && CharPositionStartsWith(src, lineStart + charNumber, lineEnd, "remstart"))
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
                    if (remBlockToken != null && CharPositionStartsWith(src, lineStart + charNumber, lineEnd, "remend"))
                    {
                        // we are done remmin' for now.
                        remBlockToken.raw = src.Substring(lineStart + remBlockToken.charNumber,
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
                    Match bestMatch = null;

                    var isStringParse = charNumber < lineLen && src[lineStart + charNumber] == '"';

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
                        _strBuffer.Clear();
                        _strBuffer.Append('"');

                        for (strIndex = charNumber + 1;
                             strIndex < lineLen;
                             strIndex++)
                        {
                            var strChar = src[lineStart + strIndex];
                            switch (strChar)
                            {
                                case '"':
                                    strIndex++;
                                    _strBuffer.Append('"');
                                    matchedEnd = true;
                                    break;
                                case '\\':
                                    if (strIndex == lineLen - 1)
                                    {
                                        // this is the last character in the line, but it cannot be.
                                        //throw new InvalidOperationException(); // TODO: replace with lexer error
                                    }

                                    strIndex++;
                                    charOffset++;

                                    _strBuffer.Append(src[lineStart + strIndex]);

                                    break;
                                default:
                                    _strBuffer.Append(strChar);
                                    break;
                            }

                            if (matchedEnd) break;
                        }

                        if (!matchedEnd)
                        {
                            var text = src.Substring(lineStart + charNumber, lineLen - charNumber);
                            errors.Add(new ParseError(new Token
                            {
                                raw = text,
                                caseInsensitiveRaw = text.ToLowerInvariant(),
                                lineNumber = lineNumber,
                                charNumber = charNumber,
                            }, ErrorCodes.LexerStringNeedsEnd, text));
                        }

                        var insensitiveRaw = src.Substring(lineStart + charNumber, strIndex - charNumber);
                        var stringLiteralSubStr = _strBuffer.ToString();
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

                    if (!isStringParse && TryMatchManual(src, lineStart + charNumber, lineEnd, out var bestLexem, out var bestLength, out var manualMatch))
                    {
                        bestMatch = manualMatch;
                        foundMatch = true;
                        var rawStr = src.Substring(lineStart + charNumber, bestLength);
                        bestToken = new Token
                        {
                            caseInsensitiveRaw = rawStr.ToLowerInvariant(),
                            raw = rawStr,
                            lexem = bestLexem,
                            lineNumber = lineNumber,
                            charNumber = charNumber + charNumberMacroOffset,
                            flags = flags
                        };
                    }

                    if (bestToken != null)
                    {
                        switch (bestToken.type)
                        {
                            case LexemType.KeywordRem:
                                FlushEos(ref requestEoS, requestEoSCharNumber, lineEnds, lineStarts, lineNumber, eolLexem, flags);
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
                                var toRemoveMatch = bestMatch.Groups[1];
                                var toRemove = bestToken.raw.Substring(toRemoveMatch.Index, toRemoveMatch.Length);
                                var toAdd = bestMatch.Groups[2].Value;

                                macroTokens.Add(bestToken);
                                all.Add(bestToken);
                                constantTable[toRemove] = toAdd;
                                // var prefix = line.Substring(0, charNumber);
                                // var suffix = line.Substring(charNumber + toRemove.Length);
                                //
                                // var replacementLine = prefix + toAdd + suffix;
                                break;
                            case LexemType.VariableGeneral
                                when constantTable.TryGetValue(bestToken.caseInsensitiveRaw, out var replacement):
                                var prefix = src.Substring(lineStart, charNumber);
                                var suffix = src.Substring(lineStart + charNumber + bestToken.Length, lineLen - charNumber - bestToken.Length);

                                var replacementLine = prefix + replacement + suffix;
                                charNumberMacroOffset += lineLen - replacementLine.Length;
                                // Switch src to the mutated line string; reset lineStart to 0 within it.
                                src = replacementLine;
                                lineStart = 0;
                                lineLen = replacementLine.Length;
                                lineEnd = lineLen;
                                macroUntilCharNumber = charNumber + bestToken.Length;

                                bestToken.lexem = new Lexem(LexemType.Constant);
                                macroTokens.Add(bestToken);
                                all.Add(bestToken);
                                continue;
                                break;
                            default:

                                FlushEos(ref requestEoS, requestEoSCharNumber, lineEnds, lineStarts, lineNumber, eolLexem, flags);
                                // if (requestEoS)
                                // {
                                //     requestEoS = false;
                                //
                                //     var previousToken = all.LastOrDefault();
                                //     var cn = previousToken == null
                                //         ? requestEoSCharNumber
                                //         : lines[previousToken.lineNumber].Length + 1; // synthetic index. 
                                //     var ln = previousToken == null
                                //         ? lineNumber
                                //         : previousToken.lineNumber;
                                //     AddToken(new Token
                                //     {
                                //         charNumber = cn ,
                                //         lexem = eolLexem,
                                //         lineNumber = ln,
                                //         caseInsensitiveRaw = "\n",
                                //         flags = flags
                                //
                                //     });
                                // }

                                AddToken(bestToken);
                                
                                break;
                        }


                        charNumber += bestToken.Length;
                    }

                    if (!foundMatch)
                    {
                        var errText = src.Substring(lineStart + charNumber, lineLen - charNumber);
                        errors.Add(new ParseError(new Token
                        {
                            raw = errText,
                            caseInsensitiveRaw = errText.ToLowerInvariant(),
                            lineNumber = lineNumber,
                            charNumber = charNumber,
                        }, ErrorCodes.LexerUnmatchedText, errText));

                        charNumber = lineLen;
                    }
                }

                if (remBlockToken != null)
                {
                    // commit
                    remBlockToken.raw = src.Substring(lineStart + remBlockToken.charNumber, lineLen - remBlockToken.charNumber);
                    remBlockToken.caseInsensitiveRaw = remBlockToken.raw.ToLowerInvariant();
                    AddComment(remBlockToken);
                }

                var previousTokenWasNotEoS = tokens.Count > 0
                    ? tokens[tokens.Count - 1].type != LexemType.EndStatement
                    : false;
                var previousTokenWasNotArgSplitter = tokens.Count > 0
                    ? tokens[tokens.Count - 1].type != LexemType.ArgSplitter
                    : true;
                var previousTokenWasNotTokenize = tokens.Count > 0
                    ? tokens[tokens.Count - 1].type != LexemType.ConstantTokenize
                    : true;

                if (previousTokenWasNotEoS && previousTokenWasNotArgSplitter && previousTokenWasNotTokenize)
                {
                    requestEoS = true;
                    requestEoSCharNumber = lineLen;
                }
            }

            if (requestEoS)
            {
                requestEoS = false;
                var previousToken = all.LastOrDefault();
                var cn = previousToken == null
                    ? requestEoSCharNumber
                    : lineEnds[previousToken.lineNumber] - lineStarts[previousToken.lineNumber] + 1; // synthetic index
                var ln = previousToken == null
                    ? lineCount - 1
                    : previousToken.lineNumber;
                AddToken(new Token
                {
                    charNumber = cn, 
                    lexem = eolLexem,
                    lineNumber = ln,
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
                macroTokens = macroTokens,
                constantTable = constantTable
            };

            // add the runtime commands in.
            HandleCommandNames(input, lineStarts, results, runtimeCommandTree);

            HandleMacros2(results, commands);
            
            return results;
        }

        class MacroBlock
        {
            public int startTokenIndex, endTokenIndex;
            public int removeExtra;
            public int startPadding;
            public List<TokenizeBlock> tokenizeBlocks = new List<TokenizeBlock>();
            public List<ParseError> errors = new List<ParseError>();

            public int TokenCount => endTokenIndex - startTokenIndex;
            public int OutsideEndTokenIndex => endTokenIndex + 1 + startPadding;
        }

        class TokenizeBlock
        {
            public int startTokenIndex, endTokenIndex;
            public bool isShortcut;
            public List<ParseError> errors = new List<ParseError>();
        }


        void HandleCommandNames(string input, int[] lineStarts, LexerResults results, CommandNameTree tree)
        {
            HandleCommandNames(input, lineStarts, results.tokens, tree);
            HandleCommandNames(input, lineStarts, results.combinedTokens, tree);
            HandleCommandNames(input, lineStarts, results.allTokens, tree);
        }
        void HandleCommandNames(string input, int[] lineStarts, List<Token> tokens, CommandNameTree tree)
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
                // Don't rewrite a token that's already been tagged as a
                // language keyword. Words like `len` collide with legacy
                // host commands but the keyword wins.
                if (token.type == LexemType.KeywordLen
                    || token.type == LexemType.KeywordDims
                    || token.type == LexemType.KeywordBytes) continue;
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
                    var subset = tokens.Skip(i).Take(j - i).ToArray();

                    var first = subset[0];
                    var last = subset[subset.Length - 1];

                    if (first.lineNumber != last.lineNumber)
                    {
                        throw new NotImplementedException("line number cannot span");
                    }

                    var firstChar = first.charNumber;
                    var lastChar = last.EndCharNumber;
                    var raw = input.Substring(lineStarts[first.lineNumber] + firstChar, lastChar - firstChar);
                    
                   // var raw = string.Join(" ", subset.Select(x => x.raw));
                    tokens[i] = new Token
                    {
                        raw = raw,
                        caseInsensitiveRaw = raw.ToLowerInvariant(),
                        lexem = new Lexem(LexemType.CommandWord),
                        charNumber = tokens[i].charNumber,
                        lineNumber = tokens[i].lineNumber,
                        flags = tokens[i].flags,
                    };
                    
                    if (curr.usage.HasFlag(FadeBasicCommandUsage.Macro))
                    {
                        tokens[i].flags |= TokenFlags.IsMacroCommand;
                    }
                    if (curr.usage.HasFlag(FadeBasicCommandUsage.Runtime))
                    {
                        tokens[i].flags |= TokenFlags.IsRuntimeCommand;
                    }
                    tokens.RemoveRange(i + 1, (j - i) - 1);
                }
                
            }
        }
        
        void HandleMacros2(LexerResults current, CommandCollection commands)
        {
            var stream = new TokenStream(current.tokens);
            // var macroCommandNames = commands.Commands.Where(c => c.usage.HasFlag(FadeBasicCommandUsage.Macro)).Select(c => c.name).ToList();
            // var macroCommandTree = CommandNameTree.Create(macroCommandNames);
            //
            // modify the entire token stream to use macro command names... 
            //  if any command names appear outside of a macro block, that is invalid. 
            // HandleCommandNames(lines, current, macroCommandTree, FadeBasicCommandUsage.Macro);
            
            TokenizeBlock ParseTokenizationBlock()
            {
                // assumption is that we are on a start tokenize
                var startIndex = stream.Index;
                var start = stream.Current;
                var isShortcut = stream.Current.type == LexemType.VariableReal;
                var endIndex = stream.Index;
                stream.Advance();
                var searching = true;
                // ParseError error = null;
                var errors = new List<ParseError>();
                while (searching)
                {
                    switch (stream.Current.type)
                    {
                        case LexemType.EOF:
                            errors.Add(new ParseError(start, ErrorCodes.LexerExpectedEndTokenize));
                            searching = false;
                            break;
                        case LexemType.EndStatement when isShortcut:
                            endIndex = stream.Index;
                            searching = false;
                            stream.Advance();
                            break;
                        case LexemType.ConstantTokenize:
                            errors.Add(new ParseError(stream.Current, ErrorCodes.LexerInvalidNestedTokenize));

                            stream.Advance();
                            break;
                        case LexemType.ConstantBegin:
                            errors.Add(new ParseError(stream.Current, ErrorCodes.LexerInvalidNestedMacro));
                            stream.Advance();
                            break;
                        case LexemType.ConstantEnd:
                            errors.Add(new ParseError(start, ErrorCodes.LexerExpectedEndTokenize));
                            searching = false;
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
                    isShortcut = isShortcut
                };
                if (errors.Count > 0)
                {
                    block.errors.AddRange(errors);
                }

                return block;
            }

            MacroBlock ParseMacroShortcut()
            {
                // assumption, we are on a # start token.
                var start = stream.Current;
                start.lexem = new Lexem(LexemType.ConstantBegin);
                var startIndex = stream.Index;
                var endIndex = stream.Index;
                stream.Advance(); // move past #
                var first = stream.Current;
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
                    endTokenIndex = endIndex ,
                    tokenizeBlocks = tokenBlocks,
                    removeExtra = 0,
                    startPadding = 0
                };

                // idk how to fix this, 
                //  but if you have a blank-line single line macro, 
                //  ```
                //  #
                //  ```
                //  then this is the only way I could get it to map to ZERO tokesn without breaking everything else. 
                if (block.endTokenIndex == block.startTokenIndex + 2 && first.type == LexemType.EndStatement)
                {
                    block.startPadding = 1;
                }
                
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
                var errors = new List<ParseError>();
                var searching = true;
                var tokenBlocks = new List<TokenizeBlock>();
                while (searching)
                {
                    switch (stream.Current.type)
                    {
                        case LexemType.EOF:
                            errors.Add(new ParseError(start, ErrorCodes.LexerExpectedEndMacro));
                            searching = false;
                            break;
                        case LexemType.ConstantBegin:
                            errors.Add(new ParseError(stream.Current, ErrorCodes.LexerInvalidNestedMacro));
                            var _ = ParseMacroBlock();
                            
                            // error, we cannot have a nested macro block.
                            // stream.Advance();
                            break;
                        case LexemType.VariableReal:
                        case LexemType.ConstantTokenize:
                            var tokenBlock = ParseTokenizationBlock();
                            tokenBlocks.Add(tokenBlock);
                            errors.AddRange(tokenBlock.errors);
                            break;
                        case LexemType.ConstantEndTokenize:
                            errors.Add(new ParseError(stream.Current, ErrorCodes.LexerInvalidEndTokenize));
                            stream.Advance();
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
                    startPadding = 1,
                    endTokenIndex = endIndex - 1,
                    tokenizeBlocks = tokenBlocks,
                    removeExtra = 1
                };
                block.errors.AddRange(errors);
                
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
                var errors = new List<ParseError>();
                var startIndex = stream.Index;
                var startToken = stream.Current;
                var endIndex = startIndex;
                Token endToken = startToken;
                var searching = true;
                stream.Advance();
                while (searching)
                {
                    switch (stream.Current.type)
                    {
                        // TODO: handle EOS and EOF
                        case LexemType.EOF:
                        case LexemType.EndStatement:
                            // searching = false;
                            errors.Add(new ParseError(startToken, ErrorCodes.SubstitutionMissingCloseBracket));
                            // endIndex = stream.Index;
                            // insert a fake close bracket, and add an error.
                            endToken = new Token
                            {
                                lexem = new Lexem(LexemType.ConstantBracketClose),
                                lineNumber = stream.Current.lineNumber,
                                charNumber = stream.Current.charNumber,
                            };
                            current.tokens.Insert(stream.Index -1 , endToken);
                            // stream.Restore(stream.Index - 1);
                            endIndex = stream.Index;
                            
                            stream.Advance();
                            searching = false;
                            break;
                        case LexemType.ConstantBracketClose:
                            // hoozah, this is the actual end!
                            endIndex = stream.Index;
                            endToken = stream.Current;
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
                    lexem = new Lexem(LexemType.ConstantBracketClose),
                    lineNumber = endToken.lineNumber,
                    charNumber = endToken.charNumber
                });
                
                current.tokens.Insert(startIndex, new Token
                {
                    lexem = new Lexem(LexemType.ConstantBracketOpen), 
                    lineNumber = startToken.lineNumber,
                    charNumber = startToken.charNumber
                });
                current.tokens.Insert(startIndex, new Token
                {
                    lexem = new Lexem(LexemType.ConstantTokenize)
                });
                stream.Advance();
                stream.Advance();
                stream.Advance(); // skip ahead of tokens that were just inserted.
                stream.Advance(); // skip ahead of tokens that were just inserted.
                var mb =  new MacroBlock
                {
                    errors = errors,
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
            var macroParseErrors = new List<ParseError>();
            while (!stream.IsEof || (stream.Count == 1 && macroParseErrors.Count == 0) ) 
            {
                // stream.Advance();
                switch (stream.Current.type)
                {
                    case LexemType.ConstantBracketOpen:
                        // parse until a close bracket. 
                        // stream.Advance();
                        macroBlocks.Add(ParseReverseSubstitution());
                        break;
                    case LexemType.ConstantBracketClose:
                        macroParseErrors.Add(new ParseError(stream.Current, ErrorCodes.SubstitutionMissingOpenBracket));
                        stream.Advance();
                        break;
                    case LexemType.VariableReal when stream.Current.Length == 1:
                        // stream.Advance();
                        macroBlocks.Add(ParseMacroShortcut());
                        break;
                    case LexemType.ConstantBegin:
                        // stream.Advance();
                        macroBlocks.Add(ParseMacroBlock());
                        break;
                    
                    case LexemType.ConstantEnd:
                        // the macro block should handle its own end expression. So in this case, we have an unexpected end. 
                        macroParseErrors.Add(new ParseError(stream.Current, ErrorCodes.LexerInvalidEndMacro));
                        stream.Advance();
                        
                        break;
                    
                    case LexemType.ConstantTokenize:
                        macroParseErrors.Add(new ParseError(stream.Current, ErrorCodes.LexerTokenizeMustAppearInMacro));
                        var badBlock = ParseTokenizationBlock();
                        macroParseErrors.AddRange(badBlock.errors);
                        break;
                    
                    case LexemType.ConstantEndTokenize:
                        macroParseErrors.Add(new ParseError(stream.Current, ErrorCodes.LexerTokenizeMustAppearInMacro));
                        macroParseErrors.Add(new ParseError(stream.Current, ErrorCodes.LexerInvalidEndTokenize));
                        stream.Advance();
                        //throw new NotImplementedException("need to make better error");
                        break;
                    default:
                        stream.Advance();
                        break;
                }
            }

            // bundle up all the macro blocks next to each other. 
            var compileTokens = new List<Token>();
            
            var macroIndexToCompileTokenIndexStart = new List<int>();
            var errorCount = macroParseErrors.Count;
            current.tokenErrors.AddRange(macroParseErrors);
            foreach (var macro in macroBlocks)
            { 
                current.tokenErrors.AddRange(macro.errors);
                errorCount += macro.errors.Count;
                macroIndexToCompileTokenIndexStart.Add(compileTokens.Count);
                var tokenSlice = current.tokens
                    .Skip(macro.startTokenIndex + 1 + macro.startPadding)
                    .Take((macro.endTokenIndex - macro.startTokenIndex) - (1 + macro.startPadding))
                    .ToList();
                
                compileTokens.AddRange(tokenSlice);
            }

            for (var i = 0; i < compileTokens.Count; i++)
            {
                compileTokens[i].flags |= TokenFlags.IsMacroToken;
            }
            
            var compileStream = new TokenStream(compileTokens);
            // TODO: need to re-handle this stream with macro-level commands. 
            var parser = new Parser(compileStream, commands, FadeBasicCommandUsage.Macro);
            
            var macroProgram = parser.ParseProgram();
            current.macroProgram = macroProgram;
            var macroErrors = macroProgram.GetAllErrors();
            
            // TODO: adjust the position of the errors so they match the original text. 
            current.tokenErrors.AddRange(macroErrors);
            if (errorCount > 0 || macroErrors.Count > 0)
            {
                // remove all macro blocks so errors are not double reported. 
                for (var i = macroBlocks.Count - 1; i >= 0; i--)
                {
                    RemoveMacro(i);
                }
                return;
            }

            if (macroErrors.Count > 0)
            {
                return;
            }
            var compiler = new Compiler(commands);
            compiler.Compile(macroProgram);

            var vm = new VirtualMachine(compiler.Program)
            {
                tokenReplacements = new List<TokenReplacement>(),
                hostMethods = compiler.methodTable
            };
            vm.Execute2(0);

            
            {
                // the compileTokens do not
                //  share an index-space with the original tokens. 
                for (var i = 0; i < vm.tokenReplacements.Count; i++)
                {
                    var repl = vm.tokenReplacements[i];
                    for (var m = macroBlocks.Count - 1; m >= 0; m--)
                    {
                        var index = macroIndexToCompileTokenIndexStart[m];
                        if (repl.tokenStartIndex >= index)
                        {
                            var diff = (macroBlocks[m].startPadding + 1) + macroBlocks[m].startTokenIndex - index;
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
            }
            
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
                
                var combined = left.raw + right.raw;
                
                var res = TokenizeWithErrors(combined, commands);
                
                var token = res.tokens[0];
                token.flags |= left.flags;
                token.flags |= right.flags;
                token.charNumber = left.charNumber;
                token.lineNumber = left.lineNumber;
                return token;
            }

            void InsertToken(int macroBlockIndex, Token t)
            {
                var index = macroBlocks[macroBlockIndex].OutsideEndTokenIndex;
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
                var bothConcatable = neighborToken.lexemFlags.HasFlag(LexemFlags.MacroConcatable) &&
                                       t.lexemFlags.HasFlag(LexemFlags.MacroConcatable);

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
                current.tokens.RemoveRange(mb.startTokenIndex, (mb.endTokenIndex - mb.startTokenIndex) +1+mb.removeExtra );
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

                var tokenEndPadding = tokenBlock.isShortcut ? 0 : 2;
                var tokenStartPadding = 1;
                // walk backwards through all the tokens and handle the replacements. 
                // var substIndex = replacement.substitutionReplacements.Count - 1;
                var substIndex = 0;
                for (var x = replacement.tokenEndIndex - tokenEndPadding; x >= replacement.tokenStartIndex + tokenStartPadding; x--)
                {
                    var token = current.tokens[x];
                    if (substIndex >= replacement.substitutionReplacements.Count)
                    {
                        // there are no more replacements, which means we can just take all of these tokens.
                        //current.tokens.Insert(macroBlock.endTokenIndex + 1, token); // stick it at the end of the macro.
                        InsertToken(macroIndex, token);
                        continue;
                    }

                    var subst = replacement.substitutionReplacements[substIndex];

                    var isTokenBeforeSubstEnd = x <= subst.tokenEndIndex ;
                    var isTokenBeforeSubstStart = x <= subst.tokenStartIndex + 1;

                    if (!isTokenBeforeSubstEnd)
                    {
                        // the token is not being substituted, so we can just add it. 
                        //current.tokens.Insert(macroBlock.endTokenIndex + 1, token); // stick it at the end of the macro.
                        InsertToken(macroIndex, token);
                        
                        continue;
                    }

                    if (isTokenBeforeSubstEnd && !isTokenBeforeSubstStart)
                    {
                        // oh oh oh , this is the substitution itself! which means we are not inserting the raw token, we are using the final value. 
                        substIndex += 1;
                        string text = null;
                        if (subst.isStringify)
                        {
                            text = "\"" + subst.raw.ToString() + "\"";
                        }
                        else
                        {
                            text = subst.raw.ToString();
                        }
                        
                        var tokenResults = TokenizeWithErrors(text, commands);
                         
                       // for (var n = tokenResults.tokens.Count - 1; n >= 0; n--)
                        {
                            var fakeToken = tokenResults.tokens[0];
                            if (subst.transitiveTypeFlags.HasFlag(TransitiveTypeFlags.Haunted))
                            {
                                fakeToken.flags |= TokenFlags.IsHauntedGenerated;
                            }
                            
                            fakeToken.flags |= TokenFlags.IsCompileTime;
                            // this token needs to adopt the position of the substitution

                            fakeToken.lineNumber = current.tokens[subst.tokenStartIndex].lineNumber;
                            fakeToken.charNumber = current.tokens[subst.tokenStartIndex].charNumber;

                            if (current.tokens.Count > subst.tokenEndIndex + 2)
                            {
                                var closeBracketToken = current.tokens[subst.tokenEndIndex ];
                                var nextToken = current.tokens[subst.tokenEndIndex + 1];
                                if (closeBracketToken.lineNumber == nextToken.lineNumber &&
                                    closeBracketToken.EndCharNumber == nextToken.charNumber)
                                {
                                    fakeToken.flags |= TokenFlags.IsAdjacentToRightSib;
                                }
                            }

                            InsertToken(macroIndex, fakeToken);

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
        public FadeBasicCommandUsage usage;
        public Dictionary<string, CommandNameTree> sub = new Dictionary<string, CommandNameTree>();
        
        public CommandNameTree(){}

        public static CommandNameTree Create(List<CommandInfo> commands)
        {
            var root = new CommandNameTree();
            foreach (var command in commands)
            {
                var parts = command.name.Split(' ');
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
                curr.usage |= command.usage;
            }

            return root;
        }
        
      
    }
    
    [Flags]
    public enum LexemFlags
    {
        None = 0,
        MacroConcatable = 1 << 0, // 1
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
        
        // TODO: flag the tokens with their usage, so the correct error message can be parsed. 
        
        /// <summary>
        /// true when the token was inserted due to a command that had the <see cref="FadeBasicCommandUsage.Macro"/>
        /// </summary>
        IsMacroCommand = 1 << 4,
        
        /// <summary>
        /// true when the token was inserted due to a command that had the <see cref="FadeBasicCommandUsage.Runtime"/>
        /// </summary>
        IsRuntimeCommand = 1 << 5,
        
        /// <summary>
        /// true when the token was generated from a substitution, where the substitution included haunted variables. 
        /// </summary>
        IsHauntedGenerated = 1 << 6,
        
        /// <summary>
        /// true when this token was generated by the parser just to keep the program happy
        /// </summary>
        IsPatchToken = 1 << 7,
        
        /// <summary>
        /// true when the token is part of a macro block
        /// </summary>
        IsMacroToken = 1 << 8
    }
    
    [Serializable]
    [DebuggerDisplay("{raw} ({type}:{lineNumber}:{charNumber})")]
    public class Token : IJsonable
    {
        public override string ToString()
        {
            return $"{raw} ({type}:{lineNumber}:{charNumber})";
        }

        public static readonly Token Blank = new Token();
        public static readonly Token Local = new Token{caseInsensitiveRaw = "local", raw = "local"};
        public static readonly Token Global = new Token{caseInsensitiveRaw = "global", raw = "global"};
        
        public int lineNumber;
        public int charNumber;
        public string raw;
        public string caseInsensitiveRaw;
        public LexemType type = LexemType.EOF;
        public LexemFlags lexemFlags;
        public TokenFlags flags = TokenFlags.None;
        
        public int Length => caseInsensitiveRaw?.Length ?? 0;
        public int EndCharNumber => charNumber + Length;
  
        public string Location => $"{lineNumber}:{charNumber}";


        public Lexem lexem
        {
            set
            {
                type = value.type;
                lexemFlags = value.flags;
            }
        }

        public static long GetTokenDistance(Token a, Token b)
        {
            // TODO: handle character distance as well? 
            return 1000 * Math.Abs(b.lineNumber - a.lineNumber) + (Math.Abs(b.charNumber - a.charNumber));
        }
        
        /// <summary>
        /// is a before b?
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static bool IsLocationBefore(Token a, Token b)
        {
            if (a == null || b == null) return false;

            return a.lineNumber < b.lineNumber || (a.lineNumber == b.lineNumber && a.charNumber < b.charNumber);
        }
        public static bool IsLocationBeforeOrEqual(Token a, Token b)
        {
            if (a == null || b == null) return false;

            return AreLocationsEqual(a, b) || a.lineNumber < b.lineNumber || (a.lineNumber == b.lineNumber && a.charNumber < b.charNumber);
        }
        
        public static bool AreLocationsEqual(Token a, Token b)
        {
            if (a == null || b == null) return false;
            return a.lineNumber == b.lineNumber && a.charNumber == b.charNumber;
        }

        public void ProcessJson<T>(ref T op) where T : IJsonOperation
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
        public int Count => _tokens.Count;

        public Token Current { get; private set; }
        public Token Previous => _tokens.Count > 0 ? _tokens[Index - 1] : null;
        private int _maxIndex;
        

        public Token Peek => IsEof
            ? new Token
            {
                lexem = new Lexem(LexemType.EOF, null)
            }
            : _tokens[Index];

        public Token Peek2 => Index + 1 >= _maxIndex
            ? new Token
            {
                lexem = new Lexem(LexemType.EOF, null)
            }
            : _tokens[Index + 1];

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

        public void SkipEos()
        {
            while (Peek.type == LexemType.EndStatement)
            {
                Advance();
            }
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

        /// <summary>
        /// Returns a single-line source-text reconstruction of tokens in the range
        /// [startInclusive, endExclusive). Tokens are joined by single spaces, which
        /// loses exact original whitespace but produces readable output for things
        /// like assertion failure messages.
        /// </summary>
        public string GetSourceText(int startInclusive, int endExclusive)
        {
            if (startInclusive >= endExclusive) return "";
            if (startInclusive < 0) startInclusive = 0;
            if (endExclusive > _tokens.Count) endExclusive = _tokens.Count;
            var sb = new StringBuilder();
            for (var i = startInclusive; i < endExclusive; i++)
            {
                if (i > startInclusive) sb.Append(' ');
                var raw = _tokens[i].raw ?? _tokens[i].caseInsensitiveRaw ?? "";
                sb.Append(raw);
            }
            return sb.ToString();
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
                    lexem = new Lexem(type),
                    flags = TokenFlags.IsPatchToken
                }
            };
        }
    }

}