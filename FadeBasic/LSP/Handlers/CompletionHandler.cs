using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.SourceGenerators;
using LSP.Services;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace LSP.Handlers;

public class CompletionHandler : CompletionHandlerBase
{
    private ILogger<CompletionHandler> _logger;
    private CompilerService _compiler;
    private ProjectService _project;

    public CompletionHandler(
        ILogger<CompletionHandler> logger,
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
        TriggerCharacters = new Container<string>(" "),
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
                request.Position.Character - 1, out var error, out var mappedLineNumber, out var mappedCharNumber))
        {
            _logger.LogError($"source document=[{request.TextDocument.Uri}] did not map to any file");
            return null;
        }

        var list = new List<CompletionItem>();

        var fakeToken = new Token
        {
            lineNumber = mappedLineNumber, charNumber = mappedCharNumber
        };
        
        // need to find the nearest token to the left. 
        Token leftToken = null;
        for (var i = unit.lexerResults.allTokens.Count - 1; i >= 0; i --)
        {
            var token = unit.lexerResults.allTokens[i];
            if (token.lineNumber <= mappedLineNumber && token.charNumber <= mappedCharNumber)
            {
                leftToken = token;
                break;
            }
        }

        if (leftToken == null)
        {
            _logger.LogInformation("There is no found left token");
            return null;
        }
        
        bool Visit(IAstVisitable x)
        {
            return Token.IsLocationBeforeOrEqual(x.StartToken, fakeToken) && Token.IsLocationBeforeOrEqual(fakeToken, x.EndToken);
        }

        var programGroup = unit.program.Where(Visit);
        var programNode = programGroup.LastOrDefault();
        var macroNode = unit.macroProgram?.Where(Visit).LastOrDefault();
        

        var isMacro = macroNode != null;
        var node = isMacro ? macroNode : programNode;

        TypeInfo typeConstraint = default;
        string extraText = null;
        Command extraCommand = null;
        bool labelConstraint = false;
        typeConstraint.unset = true;
        switch (node)
        {
            case GotoStatement:
            case GoSubStatement:
                labelConstraint = true;
                break;
            case BinaryOperandExpression binOpExpr:
                typeConstraint = binOpExpr.lhs.ParsedType;
                break;
            case AssignmentStatement assignmentStatement:
                typeConstraint = assignmentStatement.variable.DeclaredFromSymbol?.typeInfo ?? assignmentStatement.variable.ParsedType;
                break;
            case ArrayIndexReference arrRef:
                if (arrRef.DeclaredFromSymbol.source is FunctionStatement func)
                {
                    if (func.parameters.Count > arrRef.rankExpressions.Count)
                    {
                        var requiredParam = func.parameters[(arrRef.rankExpressions.Count)];
                        
                        typeConstraint = TypeInfo.FromVariableType(requiredParam.type.variableType);

                        if (func.parameters.Count > arrRef.rankExpressions.Count + 1)
                        {
                            extraText = ", ";
                        }
                    }
                }
                else
                {
                    typeConstraint = TypeInfo.Int; // needs to be an array index. 
                }
                
                break;
        }
        
        _logger.LogInformation("Found expr " + node?.GetType().Name.ToString());

        bool PassesFilters(TypeInfo typeInfo)
        {
            if (typeConstraint.unset) return true;
            
            if (typeInfo.type != typeConstraint.type)
            {
                return false;
            }

            if (typeConstraint.type == VariableType.Struct)
            {
                if (!string.Equals(typeConstraint.structName, typeInfo.structName))
                {
                    return false;
                }
            }

            if (typeConstraint.IsArray)
            {
                return typeInfo.rank == typeConstraint.rank;
            }

            return true;
        }

        void HandleLabels(Scope scope, string functionName)
        {
            if (!labelConstraint) return;
            
            foreach (var (labelName, symbol) in scope.labelTable)
            {
                scope.labelDeclTable.TryGetValue(labelName, out var functionOwner);

                if (!string.Equals(functionOwner, functionName))
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
        }

        void HandleFunctionTable(Scope scope)
        {
            if (labelConstraint) return;
            var sb = new StringBuilder();
            foreach (var (name, symbol) in scope.functionSymbolTable)
            {
                var func = scope.functionTable[name];
                if (name == "_") continue; // skip invalid function name.
                if (!PassesFilters(func.ParsedType))
                {
                    continue;
                }

                // sb.Clear();
                // sb.Append('(');
                // for (var i = 0; i < func.parameters.Count; i ++)
                // {
                //     sb.Append('$');
                //     sb.Append((i+1).ToString());
                //     if (i < func.parameters.Count - 1)
                //     {
                //         sb.Append(',');
                //     }
                // }
                //
                // sb.Append(')');
                // sb.Append("$0");
                //
                list.Add(new CompletionItem
                {
                    InsertTextFormat = InsertTextFormat.Snippet,
                    InsertTextMode = InsertTextMode.AdjustIndentation,
                    Kind = CompletionItemKind.Function,
                    
                    InsertText = name + "($0)",
                    SortText = "b",
                    
                    Label = name, // label text. 
                    Detail = $"{func.ParsedType.type}",
                    Documentation = new MarkupContent()
                    {
                        Kind = MarkupKind.Markdown,
                        Value = func.Trivia
                    },
                    Command = new Command
                    {
                        Name = "editor.action.triggerParameterHints",
                        Title = "Trigger Parameter Hints"
                    } 
                });
            }
        }
        void HandleSymbolTable(SymbolTable table)
        {
            if (labelConstraint) return;
            foreach (var (key, symbol) in table)
            {
                if (Token.IsLocationBefore(fakeToken, symbol.source.StartToken))
                {
                    // the symbol is defined AFTER the cursor position, so it would be invalid to look at. 
                    continue; 
                }

                var symbolType = symbol.source?.ParsedType ?? symbol.typeInfo;
                if (symbolType.unset)
                {
                    symbolType = symbol.typeInfo; // TODO: this is kind of gross. Different AST nodes have their type info in different spots. An array is in source, but a reg ref is not. 
                }
                if (!PassesFilters(symbolType))
                {
                    continue;
                }

                var docMarkdown = string.Empty;
                switch (symbol.source)
                {
                    case AssignmentStatement assignmentStatement:
                        docMarkdown = assignmentStatement.Trivia;
                        break;
                    case DeclarationStatement declarationStatement:
                        docMarkdown = declarationStatement.Trivia;
                        break;
                }

                var insert = key;
                if (symbolType.IsArray && !typeConstraint.IsArray)
                {
                    insert += "($0)";
                }
                list.Add(new CompletionItem
                {
                    InsertTextFormat = InsertTextFormat.Snippet,
                    InsertTextMode = InsertTextMode.AdjustIndentation,
                    Kind = CompletionItemKind.Variable,
                    Label = key, // label text. 
                    InsertText = insert,
                    SortText = "a",
                    Detail = $"{symbolType.ToDisplay()}",
                    Documentation = new MarkupContent()
                    {
                        Kind = MarkupKind.Markdown,
                        Value = docMarkdown
                    }
                });
            }
        }

        if (isMacro)
        {
            if (unit.macroProgram.scope.positionedVariables.TryFindEntry(fakeToken, out var entry))
            {
                var table = entry.value;
                HandleSymbolTable(table.Item1);
            }
            HandleSymbolTable(unit.macroProgram.scope.allGlobalVariables);
            
        }
        else
        {
            // add local scoped variables
            if (unit.program.scope.positionedVariables.TryFindEntry(fakeToken, out var entry))
            {
                var table = entry.value;
                HandleSymbolTable(table.Item1);
                HandleLabels(unit.program.scope, table.Item2);
                
            }
            HandleSymbolTable(unit.program.scope.allGlobalVariables);

            HandleFunctionTable(unit.program.scope);
            
            
            // add available commands. 
            foreach (var command in unit.commands.Commands.Where(x => x.usage.HasFlag(FadeBasicCommandUsage.Runtime)))
            {
                if (labelConstraint) continue;
                
                TypeInfo.TryGetFromTypeCode(command.returnType, out var typeInfo);
                if (!PassesFilters(typeInfo))
                {
                    continue;
                }

                commandData.docs.map.TryGetValue(command.sig, out var commandDocs);
                
                // commandDocs.command.docString
                list.Add(new CompletionItem
                {
                    InsertTextFormat = InsertTextFormat.PlainText,
                    InsertTextMode = InsertTextMode.AdjustIndentation,
                    Kind = CompletionItemKind.Operator,
                    Label = command.name, // label text. 
                    SortText = "c",
                    
                    Detail = $"{typeInfo.type} - {commandDocs?.methodDocs.summary}",
                    Documentation = commandDocs?.command.docString
                });
            }
            
            // add keywords
            if (typeConstraint.unset)
            {
                list.Add(ifKeyword);
                
                // TODO: this can only appear outside of a function. 
                if (!programGroup.Any(x => x is FunctionStatement))
                {
                    list.Add(functionKeyword);
                }
            }
        }
        
        // add global scoped variables
        // TODO: how do we know to add macro/normal globals? 
        
        // TODO: add function completions
        //  TODO: add function parameter completions (by valid type?)
        
        // TODO: add command completions 
        //  TODO: add command parameter completions (by valid type?)
        
        
        
        // TODO: add label completions (if last token was a gosub / goto)
        
        
        // TODO: add struct field name completions (if last token was a .)
        var nextList = new List<CompletionItem>();
        foreach (var l in list)
        {
            extraText ??= "";
            var l2 = l with
            {
                InsertTextFormat = InsertTextFormat.Snippet,
                InsertTextMode = InsertTextMode.AdjustIndentation,
                InsertText = l.InsertText + extraText,
                Label = l.Label,
                Command = extraCommand == null ? l.Command : extraCommand
            };
            nextList.Add(l2);
        }
        
        return Task.FromResult(new CompletionList(nextList));
    }

    private readonly CompletionItem functionKeyword = new CompletionItem
    {
        InsertTextFormat = InsertTextFormat.Snippet,
        InsertTextMode = InsertTextMode.AdjustIndentation,
        Kind = CompletionItemKind.Keyword,
        InsertText = "FUNCTION $1\n\t$0\nENDFUNCTION",
        Label = "FUNCTION",
        SortText = "d",
    };
    private readonly CompletionItem ifKeyword = new CompletionItem
    {
        InsertTextFormat = InsertTextFormat.Snippet,
        InsertTextMode = InsertTextMode.AdjustIndentation,
        Kind = CompletionItemKind.Keyword,
        InsertText = "IF $1\n\t$0\nENDIF",
        Label = "IF",
        SortText = "d",
    };
    
    

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        // do the edit?
        _logger.LogInformation($"Handling a completion item... {request.TextEditText}");
        
        return Task.FromResult(request);
    }
}