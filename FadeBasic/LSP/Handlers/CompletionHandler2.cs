using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Lsp;
using LspCompletionContext = FadeBasic.Lsp.CompletionContext;
using FadeBasic.Virtual;
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

        bool Visit(IAstVisitable v)
        {
            return v is ProgramNode || Token.IsLocationBeforeOrEqual(v.StartToken, fakeToken) && Token.IsLocationBeforeOrEqual(fakeToken, v.EndToken);
        }

        var programGroup = unit.program?.Where(Visit);
        var programNode = programGroup?.LastOrDefault();

        var macroGroup = unit.macroProgram?.Where(Visit);
        var macroNode = macroGroup?.LastOrDefault();

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

            var context = new LspCompletionContext
            {
                IsMacro = true,
                FakeToken = fakeToken,
                LeftToken = leftToken,
                Program = unit.macroProgram,
                Commands = unit.commands,
                FunctionName = entry.value.Item2,
                Group = macroGroup,
                ConstantTable = unit.lexerResults.constantTable,
                LocalScope = entry.value.Item1
            };

            var items = LSPUtil.GetCompletions(context);
            return Task.FromResult(new CompletionList(items.Select(ToCompletionItem).ToList(), isIncomplete: false));

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

            var context = new LspCompletionContext
            {
                FakeToken = fakeToken,
                LeftToken = leftToken,
                Program = unit.program,
                Commands = unit.commands,
                FunctionName = entry.value.Item2,
                Group = programGroup,
                ConstantTable = unit.lexerResults.constantTable,
                LocalScope = entry.value.Item1
            };
            var items = LSPUtil.GetCompletions(context);
            return Task.FromResult(new CompletionList(items.Select(ToCompletionItem).ToList(), isIncomplete: false));
        }
    }

    static CompletionItem ToCompletionItem(PortableCompletionItem p)
    {
        return new CompletionItem
        {
            Label = p.Label,
            InsertText = p.InsertText,
            Kind = ToCompletionItemKind(p.Kind),
            Detail = p.Detail,
            SortText = p.SortText,
            FilterText = p.FilterText,
            InsertTextFormat = p.InsertTextFormat == PortableInsertTextFormat.Snippet
                ? InsertTextFormat.Snippet
                : InsertTextFormat.PlainText,
            InsertTextMode = InsertTextMode.AdjustIndentation,
            Documentation = string.IsNullOrEmpty(p.Documentation)
                ? null
                : new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = p.Documentation
                },
            Command = p.TriggerParameterHints
                ? new Command
                {
                    Name = "editor.action.triggerParameterHints",
                    Title = "Trigger Parameter Hints"
                }
                : null,
        };
    }

    static CompletionItemKind ToCompletionItemKind(PortableCompletionKind kind)
    {
        switch (kind)
        {
            case PortableCompletionKind.Variable: return CompletionItemKind.Variable;
            case PortableCompletionKind.Function: return CompletionItemKind.Function;
            case PortableCompletionKind.Interface: return CompletionItemKind.Interface;
            case PortableCompletionKind.Keyword: return CompletionItemKind.Keyword;
            case PortableCompletionKind.Field: return CompletionItemKind.Field;
            case PortableCompletionKind.Class: return CompletionItemKind.Class;
            case PortableCompletionKind.Constant: return CompletionItemKind.Constant;
            case PortableCompletionKind.Reference: return CompletionItemKind.Reference;
            case PortableCompletionKind.Folder: return CompletionItemKind.Folder;
            default: return CompletionItemKind.Text;
        }
    }

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Handling a completion item... {request.TextEditText}");
        return Task.FromResult(request);
    }
}
