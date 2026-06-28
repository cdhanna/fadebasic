// On-type formatting — runs the full FormattingHandler then keeps edits
// within one line of the caret. Same composition the pre-refactor handler
// used; just delegates the actual formatting to the shared adapter.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

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
            MoreTriggerCharacter = chars.Select(x => x.ToString()).ToList(),
        };
    }

    private readonly FormattingHandler _formatter;

    public FormattingWhenTypingHandler(FormattingHandler formatter)
    {
        _formatter = formatter;
    }

    public override async Task<TextEditContainer?> Handle(DocumentOnTypeFormattingParams request, CancellationToken cancellationToken)
    {
        var edits = await _formatter.Handle(new DocumentFormattingParams
        {
            Options = request.Options,
            TextDocument = request.TextDocument,
        }, cancellationToken);

        var actualEdits = new List<TextEdit>();
        if (edits == null) return actualEdits;
        foreach (var edit in edits)
        {
            var lineDist = Math.Abs(edit.Range.Start.Line - request.Position.Line);
            if (lineDist < 2) actualEdits.Add(edit);
        }
        return actualEdits;
    }
}
