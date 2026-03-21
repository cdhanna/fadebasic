using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.SourceGenerators;
using FadeBasic.Virtual;
using LSP.Services;
using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace LSP.Handlers;

public class CompletionHandler2 : CompletionHandlerBase
{
    private ILogger<CompletionHandler2> _logger;
    private CompilerService _compiler;
    private ProjectService _project;

    public CompletionHandler2(
        ILogger<CompletionHandler2> logger,
        DocumentService docs, 
        CompilerService compiler,
        ProjectService project)
    {
        _project = project;
        _compiler = compiler;
        _logger = logger;
    }
    
    protected override CompletionRegistrationOptions CreateRegistrationOptions(CompletionCapability capability,
        ClientCapabilities clientCapabilities) => new CompletionRegistrationOptions
    {
        DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        TriggerCharacters = new Container<string>(" ", ".", "(", "=", "+", "*", "-", "/"),
        ResolveProvider = false
    };

    
    public override Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Handling a completion request... {request.TextDocument.Uri}");
        
        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units))
        {
            _logger.LogError($"source document=[{request.TextDocument.Uri}] did not map to any compiled unit");
            return null;
        }

        if (!_compiler.TryGetProjectContexts(request.TextDocument.Uri, out var projects))
        {
            _logger.LogError($"source document=[{request.TextDocument.Uri}] did not map to any compiled project");
            return null;
        }

        var unit = units[0]; // TODO: how should a project be tokenized if it belongs to more than 1 project? 
        _project.TryGetProject(projects[0], out var x);
        var commandData = x.Item2;
        
        
        // TODO: handle variable completion... 
        //  use unit.program.scope.localVariables
        //  need to capture scopes by lexical position. 

        if (!unit.sourceMap.TryGetMappedLocation(request.TextDocument.Uri.GetFileSystemPath(), request.Position.Line,
                request.Position.Character, out var error, out var mappedLineNumber, out var mappedCharNumber))
        {
            _logger.LogError($"source document=[{request.TextDocument.Uri}] did not map to any file");
            return null;
        }

        var fakeToken = new Token
        {
            lineNumber = mappedLineNumber, charNumber = mappedCharNumber
        };
        
        // need to find the nearest token to the left. 
        Token leftToken = null;
        for (var i = unit.lexerResults.allTokens.Count - 1; i >= 0; i --)
        {
            var token = unit.lexerResults.allTokens[i];
            if (token.lineNumber < mappedLineNumber)
            {
                leftToken = token;
                break;
            }
            if (token.lineNumber == mappedLineNumber && token.charNumber <= mappedCharNumber)
            {
                leftToken = token;
                break;
            }
        }

        var isMacro = false;

        if (leftToken == null)
        {
            _logger.LogInformation("There is no found left token");
            return null;
        }

        isMacro = leftToken.flags.HasFlag(TokenFlags.IsMacroToken);
        
        bool Visit(IAstVisitable x)
        {
            return x is ProgramNode || Token.IsLocationBeforeOrEqual(x.StartToken, fakeToken) && Token.IsLocationBeforeOrEqual(fakeToken, x.EndToken);
        }

        var programGroup = unit.program?.Where(Visit);
        var programNode = programGroup?.LastOrDefault();
        
        var macroGroup = unit.macroProgram?.Where(Visit);
        var macroNode = macroGroup?.LastOrDefault();
        
        // var isMacro = unit.macroProgram.statements.Count > 0 && macroNode != null;

        if (isMacro)
        {
            if (unit.macroProgram == null)
            {
                return Task.FromResult(new CompletionList(new List<CompletionItem>
                {
                    new CompletionItem
                    {
                        Kind = CompletionItemKind.Folder,
                        InsertText = "<NO MACRO PROG>"
                    }
                }));
            }
            if (!unit.macroProgram.scope.positionedVariables.TryFindEntry(fakeToken, out var entry))
            {
                entry = unit.macroProgram.scope.positionedVariables.entries[0];
            }

            var context = new CompletionContext
            {
                isMacro = true,
                fakeToken = fakeToken,
                leftToken = leftToken,
                program = unit.macroProgram,
                commands = unit.commands,
                functionName = entry.value.Item2,
                group = macroGroup,
                localScope = entry.value.Item1
            };
            var items = GetCompletions(context);
            return Task.FromResult(new CompletionList(items, isIncomplete: false));

        }
        else
        {
            if (unit.program == null)
            {
                return Task.FromResult(new CompletionList(new List<CompletionItem>
                {
                    new CompletionItem
                    {
                        Kind = CompletionItemKind.Folder,
                        InsertText = "<NO PROG>"
                    }
                }));
            }
            if (!unit.program.scope.positionedVariables.TryFindEntry(fakeToken, out var entry))
            {
                entry = unit.program.scope.positionedVariables.entries[0];
            }

            var context = new CompletionContext
            {
                fakeToken = fakeToken,
                leftToken = leftToken,
                program = unit.program,
                commands = unit.commands,
                functionName = entry.value.Item2,
                group = programGroup,
                localScope = entry.value.Item1
            };
            var items = GetCompletions(context);
            return Task.FromResult(new CompletionList(items, isIncomplete: false));

        }

    }

    public record CompletionContext
    {
        public CommandCollection commands;
        public ProgramNode program;
        public Token fakeToken;
        public Token leftToken;
        public Scope scope => program.scope;
        public List<IAstVisitable> group;
        public SymbolTable localScope;
        public string functionName;
        public bool isMacro;

    }

    public IEnumerable<(CompletionItem item, CommandInfo command)> GetCompletionsForCommandCalls(TypeInfo forType, CompletionContext context)
    {
        foreach (var command in context.commands.Commands)
        {
            if (context.isMacro && !command.usage.HasFlag(FadeBasicCommandUsage.Macro))
            {
                continue;
            }
            if (!context.isMacro && !command.usage.HasFlag(FadeBasicCommandUsage.Runtime))
            {
                continue;
            }
            if (!TypeInfo.TryGetFromTypeCode(command.returnType, out var commandType))
            {
                continue;
            }
            if (!commandType.IsAssignable(forType))
                continue;

            var hasReturn = command.returnType != TypeCodes.VOID;
            // TODO: add command documentation. 
            // TODO: show overload sigs? 
            
            yield return (new CompletionItem
            {
                InsertTextFormat = InsertTextFormat.Snippet,
                InsertTextMode = InsertTextMode.AdjustIndentation,
                Kind = CompletionItemKind.Interface,
                Label = command.name,
                InsertText = command.name + (hasReturn ? "($0)" : ""),
                SortText = "c",
                Detail = $"{commandType.ToDisplay()}",
                // Documentation = new MarkupContent()
                // {
                //     Kind = MarkupKind.Markdown,
                //     Value = func.Trivia
                // },
                Command = new Command
                {
                    Name = "editor.action.triggerParameterHints",
                    Title = "Trigger Parameter Hints"
                } 
            }, command);
        }
    }

    public IEnumerable<CompletionItem> GetKeywordCompletions(CompletionContext context)
    {
        // TODO: 
        yield break;
        // yield return new CompletionItem
        // {
        //     InsertTextFormat = InsertTextFormat.Snippet,
        //     InsertTextMode = InsertTextMode.AdjustIndentation,
        //     Kind = CompletionItemKind.Keyword,
        //     Label = "IF",
        //     InsertText = "IF $1\n\t$0\nENDIF",
        //     SortText = "b"
        // };
    }
    
    public IEnumerable<(CompletionItem item, Symbol func)> GetCompletionsForFunctionCalls(TypeInfo forType, Scope scope)
    {
        foreach (var (name, funcSymbol) in scope.functionSymbolTable)
        {
            var func = scope.functionTable[name];
            if (!func.ParsedType.IsAssignable(forType))
                continue;

            if (name == "_") continue; // skip invalid function name.
            
            yield return (new CompletionItem
            {
                InsertTextFormat = InsertTextFormat.Snippet,
                InsertTextMode = InsertTextMode.AdjustIndentation,
                Kind = CompletionItemKind.Function,
                Label = name,
                InsertText = name + "($0)",
                SortText = "b",
                Detail = $"{func.ParsedType.ToDisplay()}",
                Documentation = new MarkupContent()
                {
                    Kind = MarkupKind.Markdown,
                    Value = func.Trivia ?? string.Empty
                },
                Command = new Command
                {
                    Name = "editor.action.triggerParameterHints",
                    Title = "Trigger Parameter Hints"
                } 
            }, funcSymbol);
        }
    }
    
    public IEnumerable<(CompletionItem item, Symbol symbol)> GetCompletionsForSymbols(Token fakeToken, TypeInfo forType, SymbolTable symbolTable)
    {
        // var output = new List<CompletionItem>();
        foreach (var (name, symbol) in symbolTable)
        {
            if (Token.IsLocationBefore(fakeToken, symbol.source.StartToken))
            {
                // the symbol is defined AFTER the cursor position, so it would be invalid to look at. 
                continue; 
            }


            var insert = name;
            
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
                
                
            var docMarkdown = string.Empty;
            switch (symbol.source)
            {
                case AssignmentStatement assignmentStatement:
                    docMarkdown = assignmentStatement.Trivia ?? string.Empty;
                    break;
                case DeclarationStatement declarationStatement:
                    docMarkdown = declarationStatement.Trivia ?? string.Empty;
                    break;
            }
            
            yield return (new CompletionItem
            {
                InsertTextFormat = InsertTextFormat.Snippet,
                InsertTextMode = InsertTextMode.AdjustIndentation,
                Kind = CompletionItemKind.Variable,
                Label = name,
                FilterText = "",
                InsertText = insert,
                SortText = "a",
                Detail = $"{symbol.typeInfo.ToDisplay()}",
                Documentation = new MarkupContent()
                {
                    Kind = MarkupKind.Markdown,
                    Value = docMarkdown
                }
            }, symbol);
        }
    }

    List<CompletionItem> GetCompletions(CompletionContext context)
    {
        var node = context.group.LastOrDefault();
        if (node == null) return new List<CompletionItem>();

        switch (node)
        {
            case DefaultValueExpression def:
                return GetDefaultValueCompletions(def, context);
            case StructFieldReference when context.leftToken.type == LexemType.OpEqual && context.group.Count > 2 && context.group[^2] is AssignmentStatement assignmentRef:
                return GetAssignmentCompletions(assignmentRef, context);
            case StructFieldReference fieldRef:
                return GetStructCompletions(fieldRef, context);
            case CommandExpression commandExpression:
                return GetCommandParameterCompletions(commandExpression.command, commandExpression.argMap, commandExpression.args, context);
            case CommandStatement commandStatement:
                return GetCommandParameterCompletions(commandStatement.command, commandStatement.argMap, commandStatement.args, context);
            case ArrayIndexReference arrayIndexRefence when arrayIndexRefence?.DeclaredFromSymbol?.source is FunctionStatement func:
                return GetFunctionParameterCompletions(arrayIndexRefence, func, context);
            case ArrayIndexReference arrayIndexReference:
                return GetArrayIndexCompletions(arrayIndexReference, context);
            case GoSubStatement:
            case GotoStatement:
                return GetLabelCompletions(context);
            case BinaryOperandExpression binOp:
                return GetExpressionCompletions(binOp.ParsedType, context);
                
            case AssignmentStatement assignment:
                return GetAssignmentCompletions(assignment, context);
            case DeclarationStatement declaration when context.leftToken.type == LexemType.OpEqual:
                return GetDeclarationCompletion(declaration, context);
            case TypeReferenceNode trn when trn.startToken.flags.HasFlag(TokenFlags.IsPatchToken):
            case DeclarationStatement when context.leftToken.type == LexemType.KeywordAs:
                return GetTypeNameCompletions(context);
                
            case FunctionStatement when context.leftToken.type == LexemType.KeywordExitFunction:
            case ProgramNode when context.leftToken.type == LexemType.KeywordEndFunction || context.leftToken.type == LexemType.KeywordExitFunction:
                return GetExitFunctionCompletions(context);
            case ProgramNode when context.leftToken.type == LexemType.KeywordThen:
            case DeferStatement when context.leftToken.type == LexemType.KeywordDefer:
            case ProgramNode when context.leftToken.type == LexemType.KeywordDefer:
                return GetStatementCompletions(context, false);
            
            case MacroSubstitutionExpression:
            case MacroTokenizeStatement when context.leftToken.type == LexemType.ConstantBracketOpen:
                return GetExpressionCompletions(context);
            case RepeatUntilStatement when context.leftToken.type == LexemType.EndStatement || context.leftToken.type == LexemType.KeywordRem:
            case WhileStatement when context.leftToken.type == LexemType.EndStatement || context.leftToken.type == LexemType.KeywordRem:
            case DoLoopStatement when context.leftToken.type == LexemType.EndStatement || context.leftToken.type == LexemType.KeywordRem:
            case ForStatement when context.leftToken.type == LexemType.EndStatement || context.leftToken.type == LexemType.KeywordRem:
            case DeferStatement when context.leftToken.type == LexemType.EndStatement || context.leftToken.type == LexemType.KeywordRem:
            case IfStatement when context.leftToken.type == LexemType.EndStatement || context.leftToken.type == LexemType.KeywordRem:
            case ProgramNode when context.leftToken.type == LexemType.EndStatement || context.leftToken.type == LexemType.KeywordRem:
                // ah, at this point, we are on a top level statement!
                return GetStatementCompletions(context, true);
        }
        
        return new List<CompletionItem>();
    }

    List<CompletionItem> GetDefaultValueCompletions(DefaultValueExpression expression, CompletionContext context)
    {
        var list = new List<CompletionItem>();
        var type = expression.ParsedType;
        if (string.IsNullOrEmpty(type.structName))
        {
            // error case.
            return list;
        }
        var symTable = context.scope.typeNameToTypeMembers[type.structName];
        
        foreach (var (name, symbol) in symTable)
        {

            var t = symbol.source is IHasTriviaNode triviaNode
                ? triviaNode.Trivia
                : "";
            var item = new CompletionItem
            {
                InsertTextFormat = InsertTextFormat.Snippet,
                InsertTextMode = InsertTextMode.AdjustIndentation,
                Kind = CompletionItemKind.Field,
                Label = name,
                InsertText = name,
                SortText = "a",
                Detail = $"{symbol.typeInfo.ToDisplay()}",
                Documentation = new MarkupContent()
                {
                    Kind = MarkupKind.Markdown,
                    Value = t
                }
            };
            
            list.Add(item);
        }

        return list;
    }
    List<CompletionItem> GetStructCompletions(StructFieldReference reference, CompletionContext context)
    {
        var list = new List<CompletionItem>();
        
        // get the type of the left
        var type = reference.left.ParsedType;
        if (string.IsNullOrEmpty(type.structName))
        {
            // error case.
            return list;
        }
        var symTable = context.scope.typeNameToTypeMembers[type.structName];
        
        foreach (var (name, symbol) in symTable)
        {

            var t = symbol.source is IHasTriviaNode triviaNode
                ? triviaNode.Trivia
                : "";
            var item = new CompletionItem
            {
                InsertTextFormat = InsertTextFormat.Snippet,
                InsertTextMode = InsertTextMode.AdjustIndentation,
                Kind = CompletionItemKind.Field,
                Label = name,
                InsertText = name,
                SortText = "a",
                Detail = $"{symbol.typeInfo.ToDisplay()}",
                Documentation = new MarkupContent()
                {
                    Kind = MarkupKind.Markdown,
                    Value = t
                }
            };
            
            list.Add(item);
        }
        
        return list;
    }
    
    List<CompletionItem> GetCommandParameterCompletions(CommandInfo command,
        List<int> argMap, List<IExpressionNode> expressions,
        CompletionContext context)
    {
        var list = new List<CompletionItem>();

        // match the expressions through the args until we run out of expressions. 
        var exprIndex = 0;
        var argIndex = 0;

        // TODO: we need to handle command overloads here... 
        //  because it is very possible to type out one, and be wanting ot type the next one. 
        if (command.args == null || command.args.Length == 0)
        {
            // there are no args, so no completions required. 
            return list;
        }
        
        for (var i = 0; i < command.args.Length; i++)
        {
            var arg = command.args[i];
            if (arg.isVmArg)
            {
                continue; 
            }
            
            if (arg.isParams)
            {
                // as soon as we hit this, all following expressions are this type of arg.
                break;
            }
            
            // if we have an expression, move the arg along. 
            if (expressions.Count >= i)
            {
                argIndex = i;
            }
        }

        var theArg = command.args[argIndex];
        TypeInfo.TryGetFromTypeCode(theArg.typeCode, out var type);

        
        bool SymbolPredicate((CompletionItem item, Symbol symbol) x)
        {
            // if (statement == x.symbol.source)
            // {
            //     // cannot handle self reference. 
            //     return false;
            // }
            return true;
        }
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.localScope).Where(SymbolPredicate).Select(x => x.item));
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.scope.globalVariables).Where(SymbolPredicate).Select(x => x.item));
        list.AddRange(GetCompletionsForFunctionCalls(type, context.scope).Select(x => x.item));
        list.AddRange(GetCompletionsForCommandCalls(type, context).Select(x => x.item));


        return list;
    }

    List<CompletionItem> GetArrayIndexCompletions(ArrayIndexReference index, 
        CompletionContext context)
    {
        var list = new List<CompletionItem>();

        var type = TypeInfo.Int; // array index must be an int.
        bool SymbolPredicate((CompletionItem item, Symbol symbol) x)
        {
            // if (statement == x.symbol.source)
            // {
            //     // cannot handle self reference. 
            //     return false;
            // }
            return true;
        }
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.localScope).Where(SymbolPredicate).Select(x => x.item));
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.scope.globalVariables).Where(SymbolPredicate).Select(x => x.item));
        list.AddRange(GetCompletionsForFunctionCalls(type, context.scope).Select(x => x.item));
        list.AddRange(GetCompletionsForCommandCalls(type, context).Select(x => x.item));

        return list;
    }

    List<CompletionItem> GetFunctionParameterCompletions(ArrayIndexReference index, FunctionStatement func, CompletionContext context)
    {
        var list = new List<CompletionItem>();

        if (func.parameters.Count <= index.rankExpressions.Count)
        {
            return list;
        }
        
        var requiredParam = func.parameters[(index.rankExpressions.Count)];
        var type = TypeInfo.FromVariableType(requiredParam.type.variableType);

        bool SymbolPredicate((CompletionItem item, Symbol symbol) x)
        {
            // if (statement == x.symbol.source)
            // {
            //     // cannot handle self reference. 
            //     return false;
            // }
            return true;
        }
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.localScope).Where(SymbolPredicate).Select(x => x.item));
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.scope.globalVariables).Where(SymbolPredicate).Select(x => x.item));
        list.AddRange(GetCompletionsForFunctionCalls(type, context.scope).Select(x => x.item));
        list.AddRange(GetCompletionsForCommandCalls(type, context).Select(x => x.item));

        
        
        return list;
    }
    List<CompletionItem> GetExitFunctionCompletions(CompletionContext context)
    {
        var list = new List<CompletionItem>();

        // look up the function we are next to. 
        if (!context.scope.positionedVariables.TryFindEntry(context.leftToken, out var entry))
        {
            return list;
        }

        var (table, funcName) = entry.value;
        if (!context.scope.functionTable.TryGetValue(funcName, out var func))
        {
            return list;
        }

        if (!context.scope.functionReturnTypeTable.TryGetValue(funcName, out var funcTypes))
        {
            return list;
        }
        var type = func.ParsedType;
        
        // if the function just has one type,
        //  and it is VOID, then the user could just be typing out the normal flow. 
        //  but if there is already a non void type, then that MUST be the type. 
        if (funcTypes.Count == 1)
        {
            if (funcTypes[0].type == VariableType.Void)
            {
                type = TypeInfo.FromVariableType(VariableType.Any);
            }
            else
            {
                type = funcTypes[0];
            }
        }
        // if the function has more than one type, then they are in lexical order
        //  and we should pick the "last" one. 
        else if (funcTypes.Count > 1)
        {
            type = funcTypes.Last();
        }
        
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, table).Select(x => x.item));
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.scope.globalVariables).Select(x => x.item));
        list.AddRange(GetCompletionsForFunctionCalls(type, context.scope).Select(x => x.item));
        list.AddRange(GetCompletionsForCommandCalls(type, context).Select(x => x.item));

        // TODO: add things that return things that COULD match the type? 
        
        return list;
    }

    List<CompletionItem> GetStatementCompletions(CompletionContext context, bool includeKeywords)
    {
        var list = new List<CompletionItem>();
        // offer up keywords
        if (includeKeywords)
        {
            list.AddRange(GetKeywordCompletions(context));
        }

        // offer up function invocations that do not return anything. 
        list.AddRange(GetCompletionsForFunctionCalls(TypeInfo.Void, context.scope).Select(x => x.item));
        
        // offer up commands that do not return anything. 

        list.AddRange(GetCompletionsForCommandCalls(TypeInfo.Void, context).Select(x => x.item));
        return list;
    }

    List<CompletionItem> GetTypeNameCompletions(CompletionContext context)
    {
        var list = new List<CompletionItem>();
        foreach (var (name, _) in context.scope.typeNameToDecl)
        {
            list.Add(new CompletionItem
            {
                InsertTextFormat = InsertTextFormat.Snippet,
                InsertTextMode = InsertTextMode.AdjustIndentation,
                Kind = CompletionItemKind.Class,
                Label = name,
                InsertText = name,
                SortText = "a"
            });
        }

        var keywordTypes = new List<string>()
        {
            "int", "integer", "bool", "boolean", "string", "char", "word", "dword", "byte", "float", "double float", "double integer", "ushort", "uint", "long", "double"
        };
        foreach (var keyword in keywordTypes)
        {
            list.Add(new CompletionItem
            {
                InsertTextFormat = InsertTextFormat.Snippet,
                InsertTextMode = InsertTextMode.AdjustIndentation,
                Kind = CompletionItemKind.Keyword,
                Label = keyword.ToUpperInvariant(),
                InsertText = keyword.ToUpperInvariant(),
                SortText = "b"
            });
        }
       
        return list;
    }
    
    List<CompletionItem> GetDeclarationCompletion(DeclarationStatement statement, CompletionContext context)
    {
        var list = new List<CompletionItem>();
        
        // get the curren type
        var type = statement.ParsedType;
        
        // load up all the symbols in the scope, and all global functions.
        bool SymbolPredicate((CompletionItem item, Symbol symbol) x)
        {
            if (statement == x.symbol.source)
            {
                // cannot handle self reference. 
                return false;
            }
            return true;
        }
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.localScope).Where(SymbolPredicate).Select(x => x.item));
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.scope.globalVariables).Where(SymbolPredicate).Select(x => x.item));
        list.AddRange(GetCompletionsForFunctionCalls(type, context.scope).Select(x => x.item));
        list.AddRange(GetCompletionsForCommandCalls(type, context).Select(x => x.item));
        // list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.scope.allGlobalVariables));
        
        return list;
    }

    List<CompletionItem> GetExpressionCompletions(CompletionContext context)
    {
        return GetExpressionCompletions(TypeInfo.Unset, context);
    }
    List<CompletionItem> GetExpressionCompletions(TypeInfo type, CompletionContext context)
    {
        var list = new List<CompletionItem>();
        bool SymbolPredicate((CompletionItem item, Symbol symbol) x)
        {
            return true;
        }
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.localScope).Where(SymbolPredicate).Select(x => x.item));
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.scope.globalVariables).Where(SymbolPredicate).Select(x => x.item));
        list.AddRange(GetCompletionsForFunctionCalls(type, context.scope).Select(x => x.item));
        list.AddRange(GetCompletionsForCommandCalls(type, context).Select(x => x.item));

        return list;
    }
    List<CompletionItem> GetAssignmentCompletions(AssignmentStatement statement, CompletionContext context)
    {
        var list = new List<CompletionItem>();
        
        // left token must be the equal token, otherwise we are not actually on the assignment. 
        if (context.leftToken.type != LexemType.OpEqual)
        {
            return list; // early return
        }
        
        // get the curren type
        var type = statement.variable.ParsedType;
        
        // if the token is a patch token, then the type is not _real_...
        // var isFake = statement.expression.StartToken.flags.HasFlag(TokenFlags.IsPatchToken);
        // if (isFake)
        // {
        //     type = new TypeInfo
        //     {
        //         unset = true,
        //         type = VariableType.Any
        //     };
        // }

        if (statement.variable.DeclaredFromSymbol != null)
        {
            type = statement.variable.DeclaredFromSymbol.typeInfo;
            if (statement.variable is ArrayIndexReference)
            {
                type.rank = 0; // we are not assigning an array, we are assigning a field.
            }
        }
        
        // load up all the symbols in the scope, and all global functions.
        
        _logger.LogInformation($"handling assignment... {type}");

        bool SymbolPredicate((CompletionItem item, Symbol symbol) x)
        {
            if (statement == x.symbol.source)
            {
                // cannot handle self reference. 
                return false;
            }
            return true;
        }
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.localScope).Where(SymbolPredicate).Select(x => x.item));
        list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.scope.globalVariables).Where(SymbolPredicate).Select(x => x.item));
        list.AddRange(GetCompletionsForFunctionCalls(type, context.scope).Select(x => x.item));
        list.AddRange(GetCompletionsForCommandCalls(type, context).Select(x => x.item));
        // list.AddRange(GetCompletionsForSymbols(context.fakeToken, type, context.scope.allGlobalVariables));
        
        return list;
    }
    
    List<CompletionItem> GetLabelCompletions(CompletionContext context)
    {
        // add labels.
        var list = new List<CompletionItem>();
        foreach (var (labelName, symbol) in context.scope.labelTable)
        {
            context.scope.labelDeclTable.TryGetValue(labelName, out var functionOwner);

            if (!string.Equals(functionOwner, context.functionName))
            {
                // the label is in a different scope!
                continue;
            }

            var maybeTrivia = (symbol.source as LabelDeclarationNode)?.Trivia;
                
                
            list.Add(new CompletionItem
            {
                InsertTextFormat = InsertTextFormat.Snippet,
                InsertTextMode = InsertTextMode.AdjustIndentation,
                Kind = CompletionItemKind.Reference,
                Label = labelName,
                InsertText = labelName,
                SortText = "a",
                Documentation = new MarkupContent()
                {
                    Kind = MarkupKind.Markdown,
                    Value = maybeTrivia ?? ""
                }
            });
        }

        return list;
    }
    

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        // do the edit?
        _logger.LogInformation($"Handling a completion item... {request.TextEditText}");
        
        return Task.FromResult(request);
    }
}