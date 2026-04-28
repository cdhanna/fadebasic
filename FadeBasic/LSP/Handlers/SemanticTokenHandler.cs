using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Json;
using FadeBasic.Lsp;
using FadeBasic.Sdk;
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
        SourceMap sourceMap = null;
        try
        {
            if (!_compiler.TryGetProjectsFromSource(identifier.TextDocument.Uri, out var units))
            {
                _logger.LogError($"source document=[{identifier.TextDocument.Uri}] did not map to any compiled unit");
                return;
            }

            var unit = units[0]; // TODO: how should a project be tokenized if it belongs to more than 1 project? 
            sourceMap = unit.sourceMap;
            var emptyMods = Array.Empty<SemanticTokenModifier>();

            for (var i = 0; i < unit.lexerResults.allTokens.Count; i++)
            {
                var token = unit.lexerResults.allTokens[i];
                if (token.raw == null) continue;

                var location = unit.sourceMap.GetOriginalLocation(token.lineNumber, token.charNumber);
                if (location.fileName != identifier.TextDocument.Uri.GetFileSystemPath())
                    continue;

                var prevToken = i > 0 ? unit.lexerResults.allTokens[i - 1] : null;
                var result = LSPUtil.ClassifyToken(token, prevToken);
                if (result.Skip) continue;

                builder.Push(location.startLine, location.startChar, token.Length, ToSemanticTokenType(result.TokenType), emptyMods);
            }


        }
        catch (Exception ex)
        {
            _logger.LogError($"TOKEN ERR type=[{ex.GetType().Name}] message=[{ex.Message}] stack=[{ex.StackTrace}]" );
            if (sourceMap == null)
            {
                _logger.LogError(" No source map exists");
            }
            else
            {
                _logger.LogError(sourceMap.fullSource);
                _logger.LogError("File Ranges");
                _logger.LogError(string.Join(",", sourceMap.fileRanges.Select(kvp => $"[{kvp.Item1}] -> {kvp.Item2.Start} to {kvp.Item2.End}")));
                
                _logger.LogError("File To Ranges");
                _logger.LogError(string.Join(",", sourceMap._fileToRange.Select(kvp => $"[{kvp.Key}] -> {kvp.Value.Start} to {kvp.Value.End}")));
                
                _logger.LogError("Line To TOkens");
                _logger.LogError(string.Join(",", sourceMap._lineToTokens.Select(kvp => $"[{kvp.Key}] -> {string.Join("|", kvp.Value.Select(t => t.Jsonify()))}")));
            }
        }
        finally
        {
            builder.Commit();
        }
    }

    static SemanticTokenType ToSemanticTokenType(PortableSemanticTokenType type)
    {
        switch (type)
        {
            case PortableSemanticTokenType.Comment: return SemanticTokenType.Comment;
            case PortableSemanticTokenType.Function: return SemanticTokenType.Function;
            case PortableSemanticTokenType.Macro: return SemanticTokenType.Macro;
            case PortableSemanticTokenType.Parameter: return SemanticTokenType.Parameter;
            case PortableSemanticTokenType.Keyword: return SemanticTokenType.Keyword;
            case PortableSemanticTokenType.Struct: return SemanticTokenType.Struct;
            case PortableSemanticTokenType.Type: return SemanticTokenType.Type;
            case PortableSemanticTokenType.Operator: return SemanticTokenType.Operator;
            case PortableSemanticTokenType.Number: return SemanticTokenType.Number;
            case PortableSemanticTokenType.String: return SemanticTokenType.String;
            case PortableSemanticTokenType.Method: return SemanticTokenType.Method;
            default: return SemanticTokenType.Comment;
        }
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(ITextDocumentIdentifierParams args, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));
    }
}