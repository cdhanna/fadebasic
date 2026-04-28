using System;
using System.Collections.Generic;
using System.Linq;
using FadeBasic.Ast;
using FadeBasic.SourceGenerators;
using FadeBasic.Virtual;

namespace FadeBasic.Lsp
{
    public static class LSPUtil
    {
        public static SemanticTokenResult ClassifyToken(Token token, Token previousToken)
        {
            var result = new SemanticTokenResult();

            if (token.flags.HasFlag(TokenFlags.IsConstant))
            {
                result.Skip = true;
                return result;
            }

            result.TokenType = ClassifyLexemType(token);

            if (previousToken != null && token.type == LexemType.VariableGeneral)
            {
                if (previousToken.type == LexemType.KeywordAs)
                {
                    result.TokenType = PortableSemanticTokenType.Type;
                }
                else if (previousToken.type == LexemType.KeywordFunction)
                {
                    result.TokenType = PortableSemanticTokenType.Function;
                }
            }

            if (token.flags.HasFlag(TokenFlags.FunctionCall))
            {
                result.TokenType = PortableSemanticTokenType.Method;
            }

            return result;
        }

        static PortableSemanticTokenType ClassifyLexemType(Token token)
        {
            switch (token.type)
            {
                case LexemType.KeywordRem:
                case LexemType.KeywordRemStart:
                case LexemType.KeywordRemEnd:
                    return PortableSemanticTokenType.Comment;

                case LexemType.KeywordFunction:
                case LexemType.KeywordExitFunction:
                case LexemType.KeywordEndFunction:
                    return PortableSemanticTokenType.Function;

                case LexemType.ConstantBegin:
                case LexemType.ConstantEnd:
                case LexemType.ConstantTokenize:
                case LexemType.ConstantEndTokenize:
                case LexemType.ConstantBracketClose:
                case LexemType.ConstantBracketOpen:
                case LexemType.Constant:
                    return PortableSemanticTokenType.Macro;
                case LexemType.VariableReal when token.raw?.Length == 1:
                    return PortableSemanticTokenType.Macro;

                case LexemType.VariableString:
                case LexemType.VariableReal:
                case LexemType.VariableGeneral:
                    return PortableSemanticTokenType.Parameter;

                case LexemType.KeywordEnd:
                case LexemType.KeywordTo:
                case LexemType.KeywordNext:
                case LexemType.KeywordFor:
                case LexemType.KeywordSkip:
                case LexemType.KeywordStep:
                case LexemType.KeywordDo:
                case LexemType.KeywordLoop:
                case LexemType.KeywordWhile:
                case LexemType.KeywordEndWhile:
                case LexemType.KeywordRepeat:
                case LexemType.KeywordUntil:
                case LexemType.KeywordAnd:
                case LexemType.KeywordNot:
                case LexemType.KeywordXor:
                case LexemType.KeywordAs:
                case LexemType.OpMod:
                case LexemType.KeywordCase:
                case LexemType.KeywordElse:
                case LexemType.KeywordIf:
                case LexemType.KeywordEndIf:
                case LexemType.KeywordGoto:
                case LexemType.KeywordOr:
                case LexemType.KeywordScope:
                case LexemType.KeywordSelect:
                case LexemType.KeywordEndSelect:
                case LexemType.KeywordCaseDefault:
                case LexemType.KeywordThen:
                case LexemType.KeywordGoSub:
                case LexemType.ArgSplitter:
                case LexemType.FieldSplitter:
                case LexemType.KeywordDeclareArray:
                case LexemType.KeywordReDimArray:
                case LexemType.CommandWord:
                case LexemType.KeywordReturn:
                case LexemType.KeywordEndCase:
                case LexemType.KeywordExit:
                case LexemType.KeywordDefer:
                case LexemType.KeywordEndDefer:
                    return PortableSemanticTokenType.Keyword;

                case LexemType.KeywordType:
                case LexemType.KeywordEndType:
                    return PortableSemanticTokenType.Struct;

                case LexemType.KeywordTypeBoolean:
                case LexemType.KeywordTypeInteger:
                case LexemType.KeywordTypeFloat:
                case LexemType.KeywordTypeDoubleFloat:
                case LexemType.KeywordTypeDoubleInteger:
                case LexemType.KeywordTypeByte:
                case LexemType.KeywordTypeString:
                case LexemType.KeywordTypeWord:
                case LexemType.KeywordTypeDWord:
                    return PortableSemanticTokenType.Type;

                case LexemType.BracketClose:
                case LexemType.BracketOpen:
                case LexemType.ParenClose:
                case LexemType.ParenOpen:
                case LexemType.OpPlus:
                case LexemType.OpEqual:
                case LexemType.OpDivide:
                case LexemType.OpGt:
                case LexemType.OpGte:
                case LexemType.OpLt:
                case LexemType.OpLte:
                case LexemType.OpMinus:
                case LexemType.OpMultiply:
                case LexemType.OpPower:
                case LexemType.OpNotEqual:
                case LexemType.OpBitwiseAnd:
                case LexemType.OpBitwiseNot:
                case LexemType.OpBitwiseOr:
                case LexemType.OpBitwiseXor:
                case LexemType.OpBitwiseLeftShift:
                case LexemType.OpBitwiseRightShift:
                    return PortableSemanticTokenType.Operator;

                case LexemType.LiteralInt:
                case LexemType.LiteralBinary:
                case LexemType.LiteralOctal:
                case LexemType.LiteralHex:
                case LexemType.LiteralReal:
                    return PortableSemanticTokenType.Number;

                case LexemType.LiteralString:
                    return PortableSemanticTokenType.String;

                default:
                    return PortableSemanticTokenType.Comment;
            }
        }

        // ---------------------------------------------------------------
        // Completion methods
        // ---------------------------------------------------------------

        public static List<PortableCompletionItem> GetCompletions(CompletionContext context)
        {
            var node = context.Group.LastOrDefault();
            if (node == null) return new List<PortableCompletionItem>();

            switch (node)
            {
                case DefaultValueExpression def:
                    return GetDefaultValueCompletions(def, context);
                case StructFieldReference _ when context.LeftToken.type == LexemType.OpEqual && context.Group.Count > 2 && context.Group[context.Group.Count - 2] is AssignmentStatement assignmentRef:
                    return GetAssignmentCompletions(assignmentRef, context);
                case StructFieldReference fieldRef:
                    return GetStructCompletions(fieldRef, context);
                case CommandExpression commandExpression:
                    return GetCommandParameterCompletions(commandExpression.command, commandExpression.argMap, commandExpression.args, context);
                case CommandStatement commandStatement:
                    return GetCommandParameterCompletions(commandStatement.command, commandStatement.argMap, commandStatement.args, context);
                case ArrayIndexReference arrayIndexRef when arrayIndexRef?.DeclaredFromSymbol?.source is FunctionStatement func:
                    return GetFunctionParameterCompletions(arrayIndexRef, func, context);
                case ArrayIndexReference arrayIndexReference:
                    return GetArrayIndexCompletions(arrayIndexReference, context);
                case GoSubStatement _:
                case GotoStatement _:
                    return GetLabelCompletions(context);
                case BinaryOperandExpression binOp:
                    return GetExpressionCompletions(binOp.ParsedType, context);

                case AssignmentStatement assignment:
                    return GetAssignmentCompletions(assignment, context);
                case DeclarationStatement declaration when context.LeftToken.type == LexemType.OpEqual:
                    return GetDeclarationCompletions(declaration, context);
                case TypeReferenceNode trn when trn.startToken.flags.HasFlag(TokenFlags.IsPatchToken):
                case DeclarationStatement _ when context.LeftToken.type == LexemType.KeywordAs:
                    return GetTypeNameCompletions(context);

                case FunctionStatement _ when context.LeftToken.type == LexemType.KeywordExitFunction:
                case ProgramNode _ when context.LeftToken.type == LexemType.KeywordEndFunction || context.LeftToken.type == LexemType.KeywordExitFunction:
                    return GetExitFunctionCompletions(context);
                case ProgramNode _ when context.LeftToken.type == LexemType.KeywordThen:
                case DeferStatement _ when context.LeftToken.type == LexemType.KeywordDefer:
                case ProgramNode _ when context.LeftToken.type == LexemType.KeywordDefer:
                    return GetStatementCompletions(context, false);

                case MacroSubstitutionExpression _:
                case MacroTokenizeStatement _ when context.LeftToken.type == LexemType.ConstantBracketOpen:
                    return GetExpressionCompletions(context);
                case RepeatUntilStatement _ when context.LeftToken.type == LexemType.EndStatement || context.LeftToken.type == LexemType.KeywordRem:
                case WhileStatement _ when context.LeftToken.type == LexemType.EndStatement || context.LeftToken.type == LexemType.KeywordRem:
                case DoLoopStatement _ when context.LeftToken.type == LexemType.EndStatement || context.LeftToken.type == LexemType.KeywordRem:
                case ForStatement _ when context.LeftToken.type == LexemType.EndStatement || context.LeftToken.type == LexemType.KeywordRem:
                case DeferStatement _ when context.LeftToken.type == LexemType.EndStatement || context.LeftToken.type == LexemType.KeywordRem:
                case IfStatement _ when context.LeftToken.type == LexemType.EndStatement || context.LeftToken.type == LexemType.KeywordRem:
                case ProgramNode _ when context.LeftToken.type == LexemType.EndStatement || context.LeftToken.type == LexemType.KeywordRem:
                    return GetStatementCompletions(context, true);
            }

            return new List<PortableCompletionItem>();
        }

        public static IEnumerable<(PortableCompletionItem item, CommandInfo command)> GetCommandCallCompletions(TypeInfo forType, CompletionContext context)
        {
            foreach (var command in context.Commands.Commands)
            {
                if (context.IsMacro && !command.usage.HasFlag(FadeBasicCommandUsage.Macro))
                    continue;
                if (!context.IsMacro && !command.usage.HasFlag(FadeBasicCommandUsage.Runtime))
                    continue;
                if (!TypeInfo.TryGetFromTypeCode(command.returnType, out var commandType))
                    continue;
                if (!commandType.IsAssignable(forType))
                    continue;

                var hasReturn = command.returnType != TypeCodes.VOID;

                yield return (new PortableCompletionItem
                {
                    InsertTextFormat = PortableInsertTextFormat.Snippet,
                    Kind = PortableCompletionKind.Interface,
                    Label = command.name,
                    InsertText = command.name + (hasReturn ? "($0)" : ""),
                    SortText = "c",
                    Detail = commandType.ToDisplay(),
                    TriggerParameterHints = true
                }, command);
            }
        }

        public static IEnumerable<PortableCompletionItem> GetKeywordCompletions(CompletionContext context)
        {
            yield break;
        }

        public static IEnumerable<(PortableCompletionItem item, Symbol func)> GetFunctionCallCompletions(TypeInfo forType, Scope scope)
        {
            foreach (var kvp in scope.functionSymbolTable)
            {
                var name = kvp.Key;
                var funcSymbol = kvp.Value;
                var func = scope.functionTable[name];
                if (!func.ParsedType.IsAssignable(forType))
                    continue;

                if (name == "_") continue;

                var displayName = func.nameToken.raw;
                yield return (new PortableCompletionItem
                {
                    InsertTextFormat = PortableInsertTextFormat.Snippet,
                    Kind = PortableCompletionKind.Function,
                    Label = displayName,
                    InsertText = displayName + "($0)",
                    SortText = "b",
                    Detail = func.ParsedType.ToDisplay(),
                    Documentation = func.Trivia ?? string.Empty,
                    TriggerParameterHints = true
                }, funcSymbol);
            }
        }

        public static IEnumerable<PortableCompletionItem> GetConstantCompletions(Token fakeToken, TypeInfo forType, CompletionContext context)
        {
            foreach (var kvp in context.ConstantTable)
            {
                var constantName = kvp.Key;
                var val = kvp.Value;
                yield return new PortableCompletionItem
                {
                    InsertTextFormat = PortableInsertTextFormat.Snippet,
                    Kind = PortableCompletionKind.Constant,
                    Label = constantName,
                    FilterText = "",
                    InsertText = constantName,
                    SortText = "d",
                    Detail = val,
                    Documentation = val
                };
            }
        }

        public static IEnumerable<(PortableCompletionItem item, Symbol symbol)> GetSymbolCompletions(Token fakeToken, TypeInfo forType, SymbolTable symbolTable)
        {
            foreach (var kvp in symbolTable)
            {
                var name = kvp.Key;
                var symbol = kvp.Value;
                if (Token.IsLocationBefore(fakeToken, symbol.source.StartToken))
                    continue;

                var displayName = name;
                var docMarkdown = string.Empty;
                switch (symbol.source)
                {
                    case ParameterNode parameterStatement:
                        displayName = parameterStatement.variable.VariableNameCaseSensitive;
                        break;
                    case AssignmentStatement assignmentStatement:
                        docMarkdown = assignmentStatement.Trivia ?? string.Empty;
                        if (assignmentStatement.variable is VariableRefNode v)
                        {
                            displayName = v.VariableNameCaseSensitive;
                        }
                        break;
                    case DeclarationStatement declarationStatement:
                        docMarkdown = declarationStatement.Trivia ?? string.Empty;
                        displayName = declarationStatement.variableNode.VariableNameCaseSensitive;
                        break;
                }

                var insert = displayName;

                if (!symbol.typeInfo.IsAssignable(forType, out var badParity))
                {
                    if (symbol.typeInfo.IsArray && badParity)
                    {
                        insert += "($0)";
                    }
                    else
                    {
                        continue;
                    }
                }

                yield return (new PortableCompletionItem
                {
                    InsertTextFormat = PortableInsertTextFormat.Snippet,
                    Kind = PortableCompletionKind.Variable,
                    Label = displayName,
                    FilterText = "",
                    InsertText = insert,
                    SortText = "a",
                    Detail = symbol.typeInfo.ToDisplay(),
                    Documentation = docMarkdown
                }, symbol);
            }
        }

        public static List<PortableCompletionItem> GetDefaultValueCompletions(DefaultValueExpression expression, CompletionContext context)
        {
            var list = new List<PortableCompletionItem>();
            var type = expression.ParsedType;
            if (string.IsNullOrEmpty(type.structName))
                return list;

            var symTable = context.Scope.typeNameToTypeMembers[type.structName];
            foreach (var kvp in symTable)
            {
                var name = kvp.Key;
                var symbol = kvp.Value;
                var t = symbol.source is IHasTriviaNode triviaNode ? triviaNode.Trivia : "";
                list.Add(new PortableCompletionItem
                {
                    InsertTextFormat = PortableInsertTextFormat.Snippet,
                    Kind = PortableCompletionKind.Field,
                    Label = name,
                    InsertText = name,
                    SortText = "a",
                    Detail = symbol.typeInfo.ToDisplay(),
                    Documentation = t
                });
            }
            return list;
        }

        public static List<PortableCompletionItem> GetStructCompletions(StructFieldReference reference, CompletionContext context)
        {
            var list = new List<PortableCompletionItem>();
            var type = reference.left.ParsedType;
            if (string.IsNullOrEmpty(type.structName))
                return list;

            var symTable = context.Scope.typeNameToTypeMembers[type.structName];
            foreach (var kvp in symTable)
            {
                var name = kvp.Key;
                var symbol = kvp.Value;
                var t = symbol.source is IHasTriviaNode triviaNode ? triviaNode.Trivia : "";
                list.Add(new PortableCompletionItem
                {
                    InsertTextFormat = PortableInsertTextFormat.Snippet,
                    Kind = PortableCompletionKind.Field,
                    Label = name,
                    InsertText = name,
                    SortText = "a",
                    Detail = symbol.typeInfo.ToDisplay(),
                    Documentation = t
                });
            }
            return list;
        }

        public static List<PortableCompletionItem> GetCommandParameterCompletions(CommandInfo command, List<int> argMap, List<IExpressionNode> expressions, CompletionContext context)
        {
            var list = new List<PortableCompletionItem>();
            if (command.args == null || command.args.Length == 0)
                return list;

            var argIndex = 0;
            for (var i = 0; i < command.args.Length; i++)
            {
                var arg = command.args[i];
                if (arg.isVmArg) continue;
                if (arg.isParams) break;
                if (expressions.Count >= i)
                    argIndex = i;
            }

            var theArg = command.args[argIndex];
            TypeInfo.TryGetFromTypeCode(theArg.typeCode, out var type);

            list.AddRange(GetSymbolCompletions(context.FakeToken, type, context.LocalScope).Select(x => x.item));
            list.AddRange(GetSymbolCompletions(context.FakeToken, type, context.Scope.globalVariables).Select(x => x.item));
            list.AddRange(GetFunctionCallCompletions(type, context.Scope).Select(x => x.item));
            list.AddRange(GetCommandCallCompletions(type, context).Select(x => x.item));

            return list;
        }

        public static List<PortableCompletionItem> GetArrayIndexCompletions(ArrayIndexReference index, CompletionContext context)
        {
            var list = new List<PortableCompletionItem>();
            var type = TypeInfo.Int;

            list.AddRange(GetSymbolCompletions(context.FakeToken, type, context.LocalScope).Select(x => x.item));
            list.AddRange(GetSymbolCompletions(context.FakeToken, type, context.Scope.globalVariables).Select(x => x.item));
            list.AddRange(GetFunctionCallCompletions(type, context.Scope).Select(x => x.item));
            list.AddRange(GetCommandCallCompletions(type, context).Select(x => x.item));

            return list;
        }

        public static List<PortableCompletionItem> GetFunctionParameterCompletions(ArrayIndexReference index, FunctionStatement func, CompletionContext context)
        {
            var list = new List<PortableCompletionItem>();
            if (func.parameters.Count <= index.rankExpressions.Count)
                return list;

            var requiredParam = func.parameters[index.rankExpressions.Count];
            var type = TypeInfo.FromVariableType(requiredParam.type.variableType);

            list.AddRange(GetSymbolCompletions(context.FakeToken, type, context.LocalScope).Select(x => x.item));
            list.AddRange(GetSymbolCompletions(context.FakeToken, type, context.Scope.globalVariables).Select(x => x.item));
            list.AddRange(GetFunctionCallCompletions(type, context.Scope).Select(x => x.item));
            list.AddRange(GetCommandCallCompletions(type, context).Select(x => x.item));

            return list;
        }

        public static List<PortableCompletionItem> GetExitFunctionCompletions(CompletionContext context)
        {
            var list = new List<PortableCompletionItem>();
            if (!context.Scope.positionedVariables.TryFindEntry(context.LeftToken, out var entry))
                return list;

            var table = entry.value.Item1;
            var funcName = entry.value.Item2;
            if (!context.Scope.functionTable.TryGetValue(funcName, out var func))
                return list;
            if (!context.Scope.functionReturnTypeTable.TryGetValue(funcName, out var funcTypes))
                return list;

            var type = func.ParsedType;
            if (funcTypes.Count == 1)
            {
                type = funcTypes[0].type == VariableType.Void
                    ? TypeInfo.FromVariableType(VariableType.Any)
                    : funcTypes[0];
            }
            else if (funcTypes.Count > 1)
            {
                type = funcTypes[funcTypes.Count - 1];
            }

            list.AddRange(GetSymbolCompletions(context.FakeToken, type, table).Select(x => x.item));
            list.AddRange(GetSymbolCompletions(context.FakeToken, type, context.Scope.globalVariables).Select(x => x.item));
            list.AddRange(GetFunctionCallCompletions(type, context.Scope).Select(x => x.item));
            list.AddRange(GetCommandCallCompletions(type, context).Select(x => x.item));

            return list;
        }

        public static List<PortableCompletionItem> GetStatementCompletions(CompletionContext context, bool includeKeywords)
        {
            var list = new List<PortableCompletionItem>();
            if (includeKeywords)
                list.AddRange(GetKeywordCompletions(context));
            list.AddRange(GetFunctionCallCompletions(TypeInfo.Void, context.Scope).Select(x => x.item));
            list.AddRange(GetCommandCallCompletions(TypeInfo.Void, context).Select(x => x.item));
            return list;
        }

        public static List<PortableCompletionItem> GetTypeNameCompletions(CompletionContext context)
        {
            var list = new List<PortableCompletionItem>();
            foreach (var kvp in context.Scope.typeNameToDecl)
            {
                list.Add(new PortableCompletionItem
                {
                    InsertTextFormat = PortableInsertTextFormat.Snippet,
                    Kind = PortableCompletionKind.Class,
                    Label = kvp.Key,
                    InsertText = kvp.Key,
                    SortText = "a"
                });
            }

            var keywordTypes = new string[]
            {
                "int", "integer", "bool", "boolean", "string", "char", "word", "dword", "byte",
                "float", "double float", "double integer", "ushort", "uint", "long", "double"
            };
            foreach (var keyword in keywordTypes)
            {
                list.Add(new PortableCompletionItem
                {
                    InsertTextFormat = PortableInsertTextFormat.Snippet,
                    Kind = PortableCompletionKind.Keyword,
                    Label = keyword.ToUpperInvariant(),
                    InsertText = keyword.ToUpperInvariant(),
                    SortText = "b"
                });
            }
            return list;
        }

        public static List<PortableCompletionItem> GetDeclarationCompletions(DeclarationStatement statement, CompletionContext context)
        {
            var list = new List<PortableCompletionItem>();
            var type = statement.ParsedType;

            bool SymbolPredicate((PortableCompletionItem item, Symbol symbol) x)
            {
                return statement != x.symbol.source;
            }

            list.AddRange(GetSymbolCompletions(context.FakeToken, type, context.LocalScope).Where(SymbolPredicate).Select(x => x.item));
            list.AddRange(GetSymbolCompletions(context.FakeToken, type, context.Scope.globalVariables).Where(SymbolPredicate).Select(x => x.item));
            list.AddRange(GetFunctionCallCompletions(type, context.Scope).Select(x => x.item));
            list.AddRange(GetCommandCallCompletions(type, context).Select(x => x.item));

            return list;
        }

        public static List<PortableCompletionItem> GetExpressionCompletions(CompletionContext context)
        {
            return GetExpressionCompletions(TypeInfo.Unset, context);
        }

        public static List<PortableCompletionItem> GetExpressionCompletions(TypeInfo type, CompletionContext context)
        {
            var list = new List<PortableCompletionItem>();
            list.AddRange(GetSymbolCompletions(context.FakeToken, type, context.LocalScope).Select(x => x.item));
            list.AddRange(GetSymbolCompletions(context.FakeToken, type, context.Scope.globalVariables).Select(x => x.item));
            list.AddRange(GetFunctionCallCompletions(type, context.Scope).Select(x => x.item));
            list.AddRange(GetCommandCallCompletions(type, context).Select(x => x.item));
            return list;
        }

        public static List<PortableCompletionItem> GetAssignmentCompletions(AssignmentStatement statement, CompletionContext context)
        {
            var list = new List<PortableCompletionItem>();

            if (context.LeftToken.type != LexemType.OpEqual)
                return list;

            var type = statement.variable.ParsedType;
            if (statement.variable.DeclaredFromSymbol != null)
            {
                type = statement.variable.DeclaredFromSymbol.typeInfo;
                if (statement.variable is ArrayIndexReference)
                {
                    type.rank = 0;
                }
            }

            bool SymbolPredicate((PortableCompletionItem item, Symbol symbol) x)
            {
                return statement != x.symbol.source;
            }

            list.AddRange(GetConstantCompletions(context.FakeToken, type, context));
            list.AddRange(GetSymbolCompletions(context.FakeToken, type, context.LocalScope).Where(SymbolPredicate).Select(x => x.item));
            list.AddRange(GetSymbolCompletions(context.FakeToken, type, context.Scope.globalVariables).Where(SymbolPredicate).Select(x => x.item));
            list.AddRange(GetFunctionCallCompletions(type, context.Scope).Select(x => x.item));
            list.AddRange(GetCommandCallCompletions(type, context).Select(x => x.item));

            return list;
        }

        public static List<PortableCompletionItem> GetLabelCompletions(CompletionContext context)
        {
            var list = new List<PortableCompletionItem>();
            foreach (var kvp in context.Scope.labelTable)
            {
                var labelName = kvp.Key;
                var symbol = kvp.Value;
                context.Scope.labelDeclTable.TryGetValue(labelName, out var functionOwner);

                if (!string.Equals(functionOwner, context.FunctionName))
                    continue;

                var maybeTrivia = (symbol.source as LabelDeclarationNode)?.Trivia;

                list.Add(new PortableCompletionItem
                {
                    InsertTextFormat = PortableInsertTextFormat.Snippet,
                    Kind = PortableCompletionKind.Reference,
                    Label = labelName,
                    InsertText = labelName,
                    SortText = "a",
                    Documentation = maybeTrivia ?? ""
                });
            }
            return list;
        }
    }
}
