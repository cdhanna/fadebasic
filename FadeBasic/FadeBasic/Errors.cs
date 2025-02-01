using System.Collections.Generic;
using System.Diagnostics;
using FadeBasic.Ast;

namespace FadeBasic
{
    [DebuggerDisplay("{start}-{end}")]
    public class TokenRange
    {
        public Token start;
        public Token end;

        public override string ToString()
        {
            if (Token.AreLocationsEqual(start, end))
            {
                return $"[{start.lineNumber}:{start.charNumber}]";
            }
            else
            {
                return $"[{start.lineNumber}:{start.charNumber},{end.lineNumber}:{end.charNumber}]";
            }
        }
    }

    public class ProgramRecovery
    {
        public int index;
        public ParseError error;
        public List<Token> correctiveTokens;

        public ProgramRecovery()
        {
            
        }
        
        public ProgramRecovery(ParseError error, int index, ProgramRecovery existing, List<Token> moreCorrectiveTokens)
        {
            this.index = index;
            this.error = error;
            correctiveTokens = new List<Token>();
            correctiveTokens.AddRange(existing.correctiveTokens);
            correctiveTokens.AddRange(moreCorrectiveTokens);
        }
        public ProgramRecovery(ParseError error, int index, List<Token> moreCorrectiveTokens)
        {
            this.index = index;
            this.error = error;
            correctiveTokens = new List<Token>();
            correctiveTokens.AddRange(moreCorrectiveTokens);
        }
    }

    public static class ParseErrorExtensions
    {
        private static Dictionary<VariableType, string> variableToDisplayMap = new Dictionary<VariableType, string>
        {
            [VariableType.Boolean] = "bool",
            [VariableType.String] = "string",
            [VariableType.Integer] = "int",
            [VariableType.DoubleInteger] = "long",
            [VariableType.Float] = "float",
            [VariableType.DoubleFloat] = "double",
            [VariableType.Struct] = "type",
            [VariableType.Void] = "void",
        };
        public static string ConvertTypeInfoToName(TypeInfo t)
        {
            if (t.type == VariableType.Struct)
            {
                return t.structName;
            }
            else
            {
                return variableToDisplayMap[t.type];
            }
        }
        public static void AddTypeError(this List<ParseError> errors, IAstNode node, TypeInfo from, TypeInfo toType)
        {
            

            var fromDisplay = ConvertTypeInfoToName(from);
            var toDisplay = ConvertTypeInfoToName(toType);
            
            errors.Add(new ParseError(node, ErrorCodes.InvalidCast, $"cannot convert {fromDisplay} to {toDisplay}"));
        }
    }
    
    [DebuggerDisplay("{Display}")]
    public class ParseError
    {
        public TokenRange location;
        public ErrorCode errorCode;
        public string message;

        public string Display => $"{location} - {CombinedMessage}";
        public string CombinedMessage => $"{errorCode}{(string.IsNullOrEmpty(message) ? "": $" | {message}")}";
        public ParseError()
        {
            
        }

        public ParseError(Token atToken, ErrorCode errorCode, string message="")
        {
            this.errorCode = errorCode;
            this.message = message;
            this.location = new TokenRange { start = atToken, end = atToken };
        }

        public ParseError(IAstNode node, ErrorCode errorCode, string message = "")
        {
            this.errorCode = errorCode;
            this.message = message;
            this.location = new TokenRange
            {
                start = node.StartToken,
                end = node.EndToken
            };
        }
    }

    public static class ErrorCodes
    {
        // 000 series represents lexer issues
        public static readonly ErrorCode LexerUnmatchedText = "[0001] Unknown text";
        public static readonly ErrorCode LexerStringNeedsEnd = "[0002] String must have closing quote";
        
        // 100 series represents parse issues
        public static readonly ErrorCode ExpressionMissingAfterOpenParen = "[0100] No expression after open paren";
        public static readonly ErrorCode ExpressionMissingCloseParen = "[0101] Missing closing paren";
        public static readonly ErrorCode ExpressionMissing = "[0102] No valid expression found";
        public static readonly ErrorCode VariableIndexMissingCloseParen = "[0103] Missing closing paren for array index";
        public static readonly ErrorCode VariableReferenceMissing = "[0104] Missing variable reference";
        public static readonly ErrorCode DeclarationMissingTypeRef = "[0105] Variable declaration missing type reference";
        public static readonly ErrorCode DeclarationInvalidTypeRef = "[0106] Variable declaration must have a valid type reference";
        public static readonly ErrorCode AmbiguousDeclarationOrAssignment = "[0107] Statement is ambiguous between a declaration or assignment";
        public static readonly ErrorCode IfStatementMissingEndIf = "[0108] If statement is missing a closing EndIf clause";
        public static readonly ErrorCode WhileStatementMissingEndWhile = "[0109] While statement is missing a closing EndWhile clause";
        public static readonly ErrorCode RepeatStatementMissingUntil = "[0110] Repeat statement is missing a closing Until clause";
        public static readonly ErrorCode DoStatementMissingLoop = "[0111] Do statement is missing a closing Loop clause";
        public static readonly ErrorCode ForStatementMissingOpening = "[0112] For statement is missing opening equals operation";
        public static readonly ErrorCode ForStatementMissingTo = "[0113] For statement is missing end range expression";
        public static readonly ErrorCode ForStatementMissingNext = "[0114] For statement is missing closing Next clause";
        public static readonly ErrorCode SelectStatementMissingEndSelect = "[0115] Select statement is missing closing EndSelect clause";
        public static readonly ErrorCode CaseStatementMissingEndCase = "[0116] Case statement is missing closing EndCase clause";
        public static readonly ErrorCode ExpectedLiteralInt = "[0117] Expected a literal integer";
        public static readonly ErrorCode MultipleDefaultCasesFound = "[0118] There can only be one default case per Select statement";
        public static readonly ErrorCode SelectStatementUnknownCase = "[0119] Expected to find a case statement";
        public static readonly ErrorCode GotoMissingLabel = "[0120] Goto statement missing label";
        public static readonly ErrorCode GoSubMissingLabel = "[0121] GoSub statement missing label";
        public static readonly ErrorCode FunctionMissingName = "[0122] Function missing name";
        public static readonly ErrorCode FunctionMissingOpenParen = "[0122] Function missing open paren";
        public static readonly ErrorCode FunctionMissingCloseParen = "[0123] Function missing close paren";
        public static readonly ErrorCode FunctionMissingEndFunction = "[0124] Function missing EndFunction clause";
        public static readonly ErrorCode ExpectedParameter = "[0125] Expected parameter declaration";
        public static readonly ErrorCode FunctionDefinedInsideFunction = "[0126] Functions cannot be defined inside functions";
        public static readonly ErrorCode ScopedDeclarationExpectedAs = "[0127] Scoped declaration must define explicit type";
        public static readonly ErrorCode ScopedDeclarationInvalid = "[0128] Scoped declaration must define a variable or an array";
        public static readonly ErrorCode ArrayDeclarationInvalid = "[0129] array declaration must define a variable";
        public static readonly ErrorCode ArrayDeclarationMissingOpenParen = "[0130] array declaration missing open paren";
        public static readonly ErrorCode ArrayDeclarationMissingCloseParen = "[0131] array declaration missing close paren";
        public static readonly ErrorCode ArrayDeclarationRequiresSize = "[0132] array declaration requires size";
        public static readonly ErrorCode ArrayDeclarationInvalidSizeExpression = "[0133] array declaration requires valid size expression";
        public static readonly ErrorCode ArrayDeclarationSizeLimit = "[0143] array declaration can only declare up to 5 dimensions";
        public static readonly ErrorCode TypeDefMissingEndType = "[0144] Type definition missing closing EndType clause";
        public static readonly ErrorCode TypeDefMissingName = "[0145] Type definition missing name";
        public static readonly ErrorCode UnknownStatement = "[0146] Invalid statement";
        public static readonly ErrorCode CommandNoOverloadFound = "[0147] No overload for command";
        public static readonly ErrorCode FunctionCannotUseCommandName = "[0148] function name is already a reserved command";
        public static readonly ErrorCode FunctionParameterCardinalityMismatch = "[0149] function has incorrect number of parameters";
        public static readonly ErrorCode TraverseLabelBetweenScopes = "[0150] Cannot go to a label in a different scope";
        public static readonly ErrorCode TypeMustBeTopLevel = "[0151] Type declaration cannot be nested";
        public static readonly ErrorCode MultiLineDeclareCannotInferType = "[0152] Multi-line declaration cannot infer type of declaration";
        public static readonly ErrorCode MultiLineDeclareInvalidVariable = "[0153] Multi-line declaration must have a valid left hand side";
        public static readonly ErrorCode MultiLineAssignmentCannotInferType = "[0153] Multi-line assignment has invalid statement";

        // 200 series represents post-parse issues
        public static readonly ErrorCode InvalidReference = "[0200] Invalid reference";
        public static readonly ErrorCode UnknownLabel = "[0201] Unknown label";
        public static readonly ErrorCode ExpressionIsNotAStruct = "[0202] Expression is not a type, and cannot be indexed";
        public static readonly ErrorCode UnknownStructRef = "[0203] Expression references an unknown Type";
        public static readonly ErrorCode StructFieldDoesNotExist = "[0204] Member is not declared in Type";
        public static readonly ErrorCode StructFieldReferencesUnknownStruct = "[0205] Member is not a declared Type";
        public static readonly ErrorCode StructFieldsRecursive = "[0206] A recursive declaration has been detected";
        public static readonly ErrorCode CannotIndexIntoNonArray = "[0207] Cannot index into non array variable";
        public static readonly ErrorCode ArrayCardinalityMismatch = "[0208] Incorrect number of index expressions";
        public static readonly ErrorCode FunctionAlreadyDeclared = "[0209] Functions do not support overloads";
        public static readonly ErrorCode UnknownType = "[0210] Type is not defined";
        public static readonly ErrorCode ArrayRankMustBeInteger = "[0211] Array rank expression must be an integer";
        public static readonly ErrorCode ImplicitArrayDeclaration = "[0212] Implicit array declarations are not allowed";
    
        // 300 series represents type issues
        public static readonly ErrorCode SymbolAlreadyDeclared = "[0300] Symbol already declared";
        public static readonly ErrorCode InvalidCast = "[0301] Expression cannot be cast to given type";
        public static readonly ErrorCode AmbiguousFunctionReturnType = "[0302] Function has ambiguous return type";
        public static readonly ErrorCode UnknowableFunctionReturnType = "[0303] Function has no inferable return type";
    }

    public struct ErrorCode
    {
        public int code;
        public string message;
        
        public static implicit operator ErrorCode (string input)
        {
            return new ErrorCode
            {
                code = int.Parse(input.Substring(1, 4)),
                message = input.Substring(7)
            };
        }

        public override string ToString()
        {
            return $"[{code:0000}] {message}";
        }
        // public static implicit operator string(ErrorCode code)
        // {
        //     
        // }
    }
    
    
    /*
     * Some errors to catch... 
     * - You cannot assign something to a function with no return type.
     * - type safety?
     * - argument counts
     */
    
    
    // public class 
}