using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DarkBasicYo;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace LSP.Handlers;

public class SemanticTokenHandler : SemanticTokensHandlerBase
{
    private readonly ILogger<SemanticTokenHandler> _logger;

    public SemanticTokenHandler(ILogger<SemanticTokenHandler> logger)
    {
        _logger = logger;
    }
    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(SemanticTokensCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new SemanticTokensRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("basicScript"),
            Legend = new SemanticTokensLegend
            {
                TokenModifiers = capability.TokenModifiers,
                TokenTypes = capability.TokenTypes
            },
            Full = new SemanticTokensCapabilityRequestFull
            {
                Delta = true
            },
            Range = true
        };
    }

    protected override async Task Tokenize(SemanticTokensBuilder builder, ITextDocumentIdentifierParams identifier,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(DocumentUri.GetFileSystemPath(identifier.TextDocument), cancellationToken);

        try
        {
            var lexer = new Lexer();
            var tokens = lexer.Tokenize(content);

            foreach (var token in tokens)
            {
                if (token.raw == null) continue;
                builder.Push(token.lineNumber, token.charNumber, token.raw.Length, ConvertSymbol(token.type),
                    new SemanticTokenModifier[] { });
            }

            builder.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogError("TOKEN ERR " + ex.Message);
        }
    }

    static SemanticTokenType ConvertSymbol(LexemType lexem)
    {
        switch (lexem)
        {
            case LexemType.KeywordRem:
                return SemanticTokenType.Comment;
            
            case LexemType.KeywordFunction:
                return SemanticTokenType.Function;
            case LexemType.KeywordEndFunction:
                return SemanticTokenType.Function;
            
            
            
            case LexemType.VariableGeneral:
                return SemanticTokenType.Parameter;
            
            case LexemType.KeywordTo:
            case LexemType.KeywordNext:
            case LexemType.KeywordFor:
                return SemanticTokenType.Keyword;
                
            case LexemType.ParenClose:
            case LexemType.ParenOpen:
            case LexemType.OpEqual:
                return SemanticTokenType.Operator;
            case LexemType.LiteralInt:
                return SemanticTokenType.Number;
            default:
                return SemanticTokenType.Comment;
        }
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(ITextDocumentIdentifierParams args, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));
    }
}