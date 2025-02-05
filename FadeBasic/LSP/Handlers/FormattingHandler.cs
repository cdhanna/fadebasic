using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using LSP.Services;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace LSP.Handlers;

public class FormattingHandler : DocumentFormattingHandlerBase
{
    protected override DocumentFormattingRegistrationOptions CreateRegistrationOptions(DocumentFormattingCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new DocumentFormattingRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        };
    }

    
    private readonly ILogger<SemanticTokenHandler> _logger;
    private readonly DocumentService _docs;
    private readonly ILanguageServerConfiguration _lsp;
    private CompilerService _compiler;
    private readonly ProjectService _projects;

    public FormattingHandler(
        ILanguageServerConfiguration lsp,
        ILogger<SemanticTokenHandler> logger, 
        DocumentService docs, CompilerService compiler, ProjectService projects)
    {
        _lsp = lsp;
        _compiler = compiler;
        _projects = projects;
        _logger = logger;
        _docs = docs;
    }
    
    public override async Task<TextEditContainer?> Handle(DocumentFormattingParams request, CancellationToken cancellationToken)
    {
        var edits = new List<TextEdit>();

        var config = await _lsp.GetConfiguration(new ConfigurationItem
        {
            Section = "conf.language.fade"
        });
        var casingStr = config.GetSection("conf.language.fade")["formatCasing"];
        var casingOption = TokenFormatSettings.CasingSetting.Ignore;
        if (string.Equals("upper", casingStr, StringComparison.InvariantCultureIgnoreCase))
        {
            casingOption = TokenFormatSettings.CasingSetting.ToUpper;
        } else if (string.Equals("lower", casingStr, StringComparison.InvariantCultureIgnoreCase))
        {
            casingOption = TokenFormatSettings.CasingSetting.ToLower;
        }
        
        if (!_compiler.TryGetProjectContexts(request.TextDocument.Uri, out var contexts))
        {
            _logger.LogError($"source document=[{request.TextDocument.Uri}] did not map to any project contexts");
            return edits;
        }

        var projectUrl = contexts[0];
        if (!_projects.TryGetProject(projectUrl, out var project))
        {
            _logger.LogError($"source document=[{projectUrl}] did not map to any project");
            return edits;
        }

        if (!_docs.TryGetSourceDocument(request.TextDocument.Uri, out var doc))
        {
            _logger.LogError($"source document=[{request.TextDocument.Uri}] does not have a backing document");
            return edits;
        }

        var lexer = new Lexer();
        var lexerResults = lexer.TokenizeWithErrors(doc, project.Item2.collection);
        
        var tokenEdits = TokenFormatter.Format(lexerResults.combinedTokens, new TokenFormatSettings
        {
            TabSize = request.Options.TabSize,
            UseTabs = !request.Options.InsertSpaces,
            Casing = casingOption
        });

        for (var i = tokenEdits.Count - 1; i >= 0; i--)
        {
            var tokenEdit = tokenEdits[i];
            edits.Add(new TextEdit
            {
                Range = new Range(tokenEdit.startLine, tokenEdit.startChar, tokenEdit.endLine, tokenEdit.endChar),
                NewText = tokenEdit.replacement
            });
        }
        
        return edits;
    }
}