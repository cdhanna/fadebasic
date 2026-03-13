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

        unit.program.scope.positionedVariables.TryFindEntry(fakeToken, out var entry);

        var items = GetCompletions(new CompletionContext
        {
            program = unit.program, 
            functionName = entry.value.Item2, 
            group = programGroup,
            localScope = entry.value.Item1
        });
        return Task.FromResult(new CompletionList(items));
    }

    record CompletionContext
    {
        public ProgramNode program;
        public Scope scope => program.scope;
        public List<IAstVisitable> group;
        public SymbolTable localScope;
        public string functionName;
    }

    List<CompletionItem> GetCompletions(CompletionContext context)
    {
        var node = context.group.LastOrDefault();
        if (node == null) return new List<CompletionItem>();

        switch (node)
        {
            case GoSubStatement:
            case GotoStatement:
                return GetLabelCompletions(context);
            case AssignmentStatement assignment:
                return GetAssignmentCompletions(assignment, context);
        }
        
        return new List<CompletionItem>();
    }

    List<CompletionItem> GetAssignmentCompletions(AssignmentStatement statement, CompletionContext context)
    {
        var list = new List<CompletionItem>();
        
        
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