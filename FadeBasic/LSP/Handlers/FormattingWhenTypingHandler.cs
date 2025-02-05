using System;
using System.Collections.Generic;
using System.Linq;
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

public class FormattingWhenTypingHandler : DocumentOnTypeFormattingHandlerBase
{
    
    protected override DocumentOnTypeFormattingRegistrationOptions CreateRegistrationOptions(DocumentOnTypeFormattingCapability capability,
        ClientCapabilities clientCapabilities)
    {
        var chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789()%.,";
        return new DocumentOnTypeFormattingRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
            FirstTriggerCharacter = chars[0].ToString(),
            MoreTriggerCharacter = chars.Select(x => x.ToString()).ToList()
        };
    }
    
    private readonly ILogger<SemanticTokenHandler> _logger;
    private readonly FormattingHandler _formatter;
    public FormattingWhenTypingHandler(
        FormattingHandler formatter,
        ILogger<SemanticTokenHandler> logger)
    {
        _formatter = formatter;
        _logger = logger;
    }
    
    public override async Task<TextEditContainer?> Handle(DocumentOnTypeFormattingParams request, CancellationToken cancellationToken)
    {
        var edits = await _formatter.Handle(new DocumentFormattingParams
        {
            Options = request.Options,
            TextDocument = request.TextDocument
        }, cancellationToken);


        var actualEdits = new List<TextEdit>();
        foreach (var edit in edits)
        {
            var lineDist = Math.Abs(edit.Range.Start.Line - request.Position.Line);
            if (lineDist < 2)
            {
                actualEdits.Add(edit);
            }
        }
        return actualEdits;
      
    }
}