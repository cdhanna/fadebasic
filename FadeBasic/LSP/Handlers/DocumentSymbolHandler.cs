using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using FadeBasic;

namespace LSP.Handlers;

public class DocumentSymbolHandler : IDocumentSymbolHandler
{
    public static SymbolKind Convert(LexemType type)
    {
        switch (type)
        {
            case LexemType.OpEqual:
                return SymbolKind.Operator;

            case LexemType.VariableGeneral:
                return SymbolKind.Variable;
            
            case LexemType.LiteralString:
                return SymbolKind.String;

            case LexemType.LiteralReal:
            case LexemType.LiteralInt:
                return SymbolKind.Number;
            default:
                return SymbolKind.Key;
        }
    }
    
    
    public async Task<SymbolInformationOrDocumentSymbolContainer?> Handle(DocumentSymbolParams request, CancellationToken cancellationToken)
    {
        // TODO: get this data from the sync handler
        var content = await File.ReadAllTextAsync(DocumentUri.GetFileSystemPath(request), cancellationToken);

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(content);
        var symbols = new List<SymbolInformationOrDocumentSymbol>();

        foreach (var token in tokens)
        {
            if (token.raw == null) continue;

            switch (token.type)
            {
                case LexemType.OpEqual:
                case LexemType.LiteralInt:
                    continue;
                default:
                    break;
            }
            
            var symbol = new DocumentSymbol
            {
                Detail = token.raw,
                Kind = Convert(token.type),
                SelectionRange = new Range(token.lineNumber, token.charNumber, token.lineNumber, token.charNumber + token.Length),
                Range = new Range(token.lineNumber, token.charNumber, token.lineNumber, token.charNumber + token.Length),
                Name = token.raw
            };
            
            symbols.Add(symbol);
        }
        
        return symbols;
    }

    public DocumentSymbolRegistrationOptions GetRegistrationOptions(DocumentSymbolCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new DocumentSymbolRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),

        };
    }
}