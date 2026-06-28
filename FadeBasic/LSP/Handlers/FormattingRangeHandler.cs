// Range formatting — runs the full FormattingHandler then filters the
// result by intersection with the requested range. Pre-refactor native
// did the same thing.
//
// Core also has a `ComputeRange` method that does the equivalent filter;
// we keep the "format full then filter" composition here so we re-use the
// already-wired config / source-map handling in FormattingHandler.

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

    private readonly FormattingHandler _formatter;

    public FormattingRangeHandler(FormattingHandler formatter)
    {
        _formatter = formatter;
    }

    public override async Task<TextEditContainer> Handle(DocumentRangeFormattingParams request, CancellationToken cancellationToken)
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
            if (!edit.Range.IntersectsOrTouches(request.Range)) continue;
            actualEdits.Add(edit);
        }
        return actualEdits;
    }
}
