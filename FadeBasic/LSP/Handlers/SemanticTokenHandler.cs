using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using LSP.Services;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace LSP.Handlers;

public class SemanticTokenHandler : SemanticTokensHandlerBase
{
    private readonly ILogger<SemanticTokenHandler> _logger;
    private readonly DocumentService _docs;
    private CompilerService _compiler;
    private readonly ProjectService _projects;

    public SemanticTokenHandler(
        ILogger<SemanticTokenHandler> logger, 
        DocumentService docs, CompilerService compiler, ProjectService projects)
    {
        _compiler = compiler;
        _projects = projects;
        _logger = logger;
        _docs = docs;
    }
    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(SemanticTokensCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new SemanticTokensRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
            Legend = new SemanticTokensLegend
            {
                TokenModifiers = capability.TokenModifiers,
                TokenTypes = capability.TokenTypes,
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
        try
        {
            if (!_compiler.TryGetProjectsFromSource(identifier.TextDocument.Uri, out var units))
            {
                _logger.LogError($"source document=[{identifier.TextDocument.Uri}] did not map to any compiled unit");
                return;
            }

            var unit = units[0]; // TODO: how should a project be tokenized if it belongs to more than 1 project? 
            
            var emptyMods = Array.Empty<SemanticTokenModifier>();

            
            // it is important that macro tokens GO FIRST; the LSP takes the first value for each spot. 
            // TODO: this is still a little flakey
            foreach (var token in unit.lexerResults.macroTokens)
            {
                if (token.raw == null) continue;
                var location = unit.sourceMap.GetOriginalLocation(token.lineNumber, token.charNumber);
                if (location.fileName != identifier.TextDocument.Uri.GetFileSystemPath())
                {
                    continue;
                }
                builder.Push(location.startLine, location.startChar, token.Length, SemanticTokenType.Macro, emptyMods);
            }
            
            foreach (var token in unit.lexerResults.combinedTokens)
            {
                if (token.raw == null) continue;
                
                
                // if the token is not part of this file; we can skip it.
                // TODO: it would be better if this was cached...
                var location = unit.sourceMap.GetOriginalLocation(token.lineNumber, token.charNumber);
                if (location.fileName != identifier.TextDocument.Uri.GetFileSystemPath())
                {
                    continue;
                }

                var tokenType = ConvertSymbol(token.type);
                if (token.flags.HasFlag(TokenFlags.FunctionCall))
                {
                    tokenType = SemanticTokenType.Method;
                    
                }
                builder.Push(location.startLine, location.startChar, token.Length, tokenType, emptyMods);
            }


        }
        catch (Exception ex)
        {
            _logger.LogError("TOKEN ERR " + ex.Message);
        }
        finally
        {
            builder.Commit();
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
            
            
            case LexemType.Constant:
                return SemanticTokenType.Macro;
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
            case LexemType.LiteralBinary:
            case LexemType.LiteralOctal:
            case LexemType.LiteralHex:
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