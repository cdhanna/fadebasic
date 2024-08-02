using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

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

    public override async Task<TextEditContainer?> Handle(DocumentFormattingParams request, CancellationToken cancellationToken)
    {
        var edits = new List<TextEdit>();
        // edits.Add(new TextEdit
        // {
        //     Range = new Range(0, 0, 0, 3),
        //     NewText = "tuna"
        // });
        
        // need to parse the program out to an AST
        
        
        return edits;
    }
}