using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DarkBasicYo;
using LSP.Services;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace LSP.Handlers;

public class SemanticTokenHandler : SemanticTokensHandlerBase
{
    private readonly ILogger<SemanticTokenHandler> _logger;
    private readonly DocumentService _docs;
    private CompilerService _compiler;

    public SemanticTokenHandler(
        ILogger<SemanticTokenHandler> logger, 
        DocumentService docs, CompilerService compiler)
    {
        _compiler = compiler;
        _logger = logger;
        _docs = docs;
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
        // builder.
        
        // var content = await File.ReadAllTextAsync(DocumentUri.GetFileSystemPath(identifier.TextDocument), cancellationToken);
        // if (!_docs.TryGetSourceDocument(identifier.TextDocument.Uri, out var content))
        // {
        //     throw new Exception("No content available");
        // }
        try
        {
            if (!_compiler.TryGetLexerResults(identifier.TextDocument.Uri, out var results))
            {
                return;
            }

            var tokens = results.tokens;
            
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
            case LexemType.KeywordRemStart:
            case LexemType.KeywordRemEnd:
                return SemanticTokenType.Comment;
            
            case LexemType.KeywordFunction:
                return SemanticTokenType.Function;
            case LexemType.KeywordExitFunction:
            case LexemType.KeywordEndFunction:
                return SemanticTokenType.Function;
            
            
            
            case LexemType.VariableString:
            case LexemType.VariableReal:
            case LexemType.VariableGeneral:
                return SemanticTokenType.Parameter;
            
            case LexemType.KeywordEnd:
            case LexemType.KeywordTo:
            case LexemType.KeywordNext:
            case LexemType.KeywordFor:
            case LexemType.KeywordStep:
            case LexemType.KeywordDo:
            case LexemType.KeywordLoop:
            case LexemType.KeywordWhile:
            case LexemType.KeywordEndWhile:
            case LexemType.KeywordRepeat:
            case LexemType.KeywordUntil:
            case LexemType.KeywordAnd:
            case LexemType.KeywordAs:
            case LexemType.KeywordCase:
            case LexemType.KeywordElse:
            case LexemType.KeywordIf:
            case LexemType.KeywordEndIf:
            case LexemType.KeywordGoto:
            case LexemType.KeywordOr:
            case LexemType.KeywordScope:
            case LexemType.KeywordSelect:
            case LexemType.KeywordEndSelect:
            case LexemType.KeywordCaseDefault:
            case LexemType.KeywordThen:
            case LexemType.KeywordGoSub:
            case LexemType.ArgSplitter:
            case LexemType.FieldSplitter:
            case LexemType.KeywordDeclareArray:
            case LexemType.KeywordUnDeclareArray:
            case LexemType.CommandWord:
            case LexemType.KeywordEndCase:
                return SemanticTokenType.Keyword;
                
            case LexemType.KeywordType:
            case LexemType.KeywordEndType:
                return SemanticTokenType.Struct;
            
            case LexemType.KeywordTypeBoolean:
            case LexemType.KeywordTypeInteger:
            case LexemType.KeywordTypeDoubleFloat:
            case LexemType.KeywordTypeDoubleInteger:
            case LexemType.KeywordTypeByte:
            case LexemType.KeywordTypeString:
            case LexemType.KeywordTypeWord:
            case LexemType.KeywordTypeDWord:
                return SemanticTokenType.Type;

            case LexemType.ParenClose:
            case LexemType.ParenOpen:
            case LexemType.OpPlus:
            case LexemType.OpEqual:
            case LexemType.OpDivide:
            case LexemType.OpGt:
            case LexemType.OpGte:
            case LexemType.OpLt:
            case LexemType.OpLte:
            case LexemType.OpMinus:
            case LexemType.OpMod:
            case LexemType.OpMultiply:
            case LexemType.OpPower:
            case LexemType.OpNotEqual:
                return SemanticTokenType.Operator;
            case LexemType.LiteralInt:
            case LexemType.LiteralReal:
                return SemanticTokenType.Number;
            case LexemType.LiteralString:
                return SemanticTokenType.String;
            default:
                return SemanticTokenType.Comment;
        }
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(ITextDocumentIdentifierParams args, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));
    }
}