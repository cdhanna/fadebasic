// Semantic tokens — thin adapter over FadeBasic.LSP.Core.Handlers.SemanticTokensHandler.
//
// ─── Behavioral audit (pre-refactor native vs Core) ─────────────────────
//
// Pre-refactor native and Core both run `LSPUtil.ClassifyToken` against
// every token in the lex stream. The only project-aware step is filtering
// tokens to the requesting URI and remapping each token's position
// through `unit.sourceMap.GetOriginalLocation` (multi-file projects feed
// many source files into one concatenated lex buffer).
//
// Refactor: Core now exposes `Classify(doc)` returning the raw
// (token, type) list. The native adapter calls Classify, then for each
// token does the per-token source-map filter+remap before pushing onto
// the OmniSharp builder. Core's `Compute(doc)` still returns the
// canonical LSP delta-encoded ints (used unchanged by WebRuntime which
// is single-file).

using System;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using FadeBasic.Lsp;
using LSP.Services;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using CoreSemTokensHandler = FadeBasic.LSP.Core.Handlers.SemanticTokensHandler;

namespace LSP.Handlers;

public class SemanticTokenHandler : SemanticTokensHandlerBase
{
    private readonly ILogger<SemanticTokenHandler> _logger;
    private readonly CompilerService _compiler;

    public SemanticTokenHandler(ILogger<SemanticTokenHandler> logger, CompilerService compiler)
    {
        _logger = logger;
        _compiler = compiler;
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
            // Delta disabled: the incremental diff was mis-coloring block-closing
            // keywords (endif/loop) when the client's cached previous token set
            // lagged an edit. A full token set per request is always correct and
            // cheap for source-sized files. (The lexer classification itself is
            // correct — see LexClassifyProbeTests.)
            Full = new SemanticTokensCapabilityRequestFull { Delta = false },
            Range = true,
        };
    }

    protected override Task Tokenize(SemanticTokensBuilder builder, ITextDocumentIdentifierParams identifier,
        CancellationToken cancellationToken)
    {
        try
        {
            // Parse the live buffer right now (cheap) so highlighting tracks
            // every keystroke instead of waiting on the debounced diagnostics
            // pass. Fall back to the last cached unit if a fresh parse isn't
            // available (e.g. project not yet resolved).
            if (!_compiler.TryParseFresh(identifier.TextDocument.Uri, out var unit) || unit == null)
            {
                if (!_compiler.TryGetProjectsFromSource(identifier.TextDocument.Uri, out var units) || units.Count == 0)
                    return Task.CompletedTask;
                unit = units[0];
            }

            var doc = CoreAdapter.ToDocument(unit, identifier.TextDocument.Uri.ToString());
            var classified = CoreSemTokensHandler.Classify(doc);
            var emptyMods = Array.Empty<SemanticTokenModifier>();
            var thisFilePath = identifier.TextDocument.Uri.GetFileSystemPath();

            foreach (var ct in classified)
            {
                var location = unit.sourceMap.GetOriginalLocation(ct.Token.lineNumber, ct.Token.charNumber);
                if (location.fileName != thisFilePath) continue;
                builder.Push(location.startLine, location.startChar, ct.Token.Length,
                    ToSemanticTokenType(ct.Type), emptyMods);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"TOKEN ERR type=[{ex.GetType().Name}] message=[{ex.Message}]");
        }
        finally
        {
            builder.Commit();
        }
        return Task.CompletedTask;
    }

    private static SemanticTokenType ToSemanticTokenType(PortableSemanticTokenType type)
    {
        switch (type)
        {
            case PortableSemanticTokenType.Comment:   return SemanticTokenType.Comment;
            case PortableSemanticTokenType.Function:  return SemanticTokenType.Function;
            case PortableSemanticTokenType.Macro:     return SemanticTokenType.Macro;
            case PortableSemanticTokenType.Parameter: return SemanticTokenType.Parameter;
            case PortableSemanticTokenType.Keyword:   return SemanticTokenType.Keyword;
            case PortableSemanticTokenType.Struct:    return SemanticTokenType.Struct;
            case PortableSemanticTokenType.Type:      return SemanticTokenType.Type;
            case PortableSemanticTokenType.Operator:  return SemanticTokenType.Operator;
            case PortableSemanticTokenType.Number:    return SemanticTokenType.Number;
            case PortableSemanticTokenType.String:    return SemanticTokenType.String;
            case PortableSemanticTokenType.Method:    return SemanticTokenType.Method;
            default: return SemanticTokenType.Comment;
        }
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(ITextDocumentIdentifierParams args, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));
    }
}
