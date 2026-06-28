// Folding ranges — thin adapter over FadeBasic.LSP.Core.Handlers.FoldingRangeHandler.
// The native LSP's previous implementation was a hardcoded stub (lines 2–4);
// Core's AST-driven version covers function bodies, if/for/while/do/repeat
// blocks, type/test blocks, and multi-line rem comments.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using LSP.Services;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using CoreFoldingHandler = FadeBasic.LSP.Core.Handlers.FoldingRangeHandler;

namespace LSP.Handlers;

public class FoldingRangeHandler : IFoldingRangeHandler
{
    private readonly CompilerService _compiler;

    public FoldingRangeHandler(CompilerService compiler)
    {
        _compiler = compiler;
    }

    public Task<Container<FoldingRange>?> Handle(FoldingRangeRequestParam request, CancellationToken cancellationToken)
    {
        var empty = new Container<FoldingRange>(new List<FoldingRange>());
        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units) || units.Count == 0)
            return Task.FromResult<Container<FoldingRange>?>(empty);

        var doc = CoreAdapter.ToDocument(units[0], request.TextDocument.Uri.ToString());
        var ranges = CoreFoldingHandler.Compute(doc);

        var omni = new List<FoldingRange>(ranges.Count);
        foreach (var r in ranges)
        {
            omni.Add(new FoldingRange
            {
                StartLine = r.StartLine,
                EndLine = r.EndLine,
                StartCharacter = r.StartCharacter,
                EndCharacter = r.EndCharacter,
                Kind = r.Kind switch
                {
                    FadeBasic.LSP.Core.LspFoldingRangeKind.Comment => FoldingRangeKind.Comment,
                    FadeBasic.LSP.Core.LspFoldingRangeKind.Imports => FoldingRangeKind.Imports,
                    _ => FoldingRangeKind.Region,
                },
            });
        }
        return Task.FromResult<Container<FoldingRange>?>(new Container<FoldingRange>(omni));
    }

    public FoldingRangeRegistrationOptions GetRegistrationOptions(FoldingRangeCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new FoldingRangeRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(TextDocumentFilter.ForLanguage(FadeBasicConstants.FadeBasicLanguage)),
        };
    }
}
