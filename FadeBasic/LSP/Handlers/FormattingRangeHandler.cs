using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace LSP.Handlers;

public class FormattingRangeHandler : DocumentRangeFormattingHandlerBase
{
    protected override DocumentRangeFormattingRegistrationOptions CreateRegistrationOptions(DocumentRangeFormattingCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new DocumentRangeFormattingRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        };
    }
    
    private readonly ILogger<SemanticTokenHandler> _logger;
    private readonly FormattingHandler _formatter;
    public FormattingRangeHandler(
        FormattingHandler formatter,
        ILogger<SemanticTokenHandler> logger)
    {
        _formatter = formatter;
        _logger = logger;
    }


    public override async Task<TextEditContainer> Handle(DocumentRangeFormattingParams request, CancellationToken cancellationToken)
    {
        var edits = await _formatter.Handle(new DocumentFormattingParams
        {
            Options = request.Options,
            TextDocument = request.TextDocument
        }, cancellationToken);


        var actualEdits = new List<TextEdit>();
        foreach (var edit in edits)
        {
            if (!edit.Range.IntersectsOrTouches(request.Range)) continue;
            actualEdits.Add(edit);
            
        }
        return actualEdits;
    }
}