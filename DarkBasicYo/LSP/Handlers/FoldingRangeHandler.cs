using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace LSP.Handlers;

public class FoldingRangeHandler : IFoldingRangeHandler
{
    public Task<Container<FoldingRange>?> Handle(FoldingRangeRequestParam request, CancellationToken cancellationToken)
    {
        var ranges = new List<FoldingRange>();
        ranges.Add(new FoldingRange
        {
            StartLine = 2,
            EndLine = 4,
            Kind = FoldingRangeKind.Region,
            StartCharacter = 0,
            EndCharacter = 0
        });

        return Task.FromResult<Container<FoldingRange>?>(new Container<FoldingRange>(ranges));
    }

    public FoldingRangeRegistrationOptions GetRegistrationOptions(FoldingRangeCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new FoldingRangeRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(TextDocumentFilter.ForLanguage("basicScript"))
        };
    }
}