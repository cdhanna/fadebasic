// Hover — thin adapter over FadeBasic.LSP.Core.Handlers.HoverHandler.
//
// ─── Behavioral audit (pre-refactor native vs Core) ─────────────────────
//
// Pre-refactor native:
//   * Walked the AST for the smallest matching node at the cursor.
//   * For a CommandStatement/CommandExpression, generated rich Markdown from
//     ProjectDocs (groups → commands → methodDocs).
//   * For a function call (FunctionCall flag on ExpressionStatement or
//     ArrayIndexReference), looked up `scope.functionTable` and returned
//     `function.Trivia` verbatim.
//   * For nodes whose DeclaredFromSymbol.source implements IHasTriviaNode,
//     returned the source's Trivia verbatim.
//   * Did NOT surface diagnostics on hover.
//   * Did NOT surface variable / parameter / label info beyond raw trivia.
//
// Core HoverHandler now provides:
//   * Error/lex-error markdown when a diagnostic encloses the cursor.
//   * Rich command markdown via ICommandDocsProvider (same ProjectDocs
//     pipeline behind a small interface). The native LSP installs a
//     `ProjectDocsCommandDocsProvider` here so the exact-same markdown
//     pipeline is used.
//   * Function-call hover via DeclaredFromSymbol.source on the AST.
//   * Symbol info for VariableRef / Declaration / Parameter / Label,
//     formatted as a fenced `fade` code block + trivia.
//
// Behavioral diffs vs old native:
//   * Hovering over a token that maps to a diagnostic now surfaces the
//     diagnostic instead of nothing (better).
//   * Hovering over a variable/parameter/label now shows a signature-shaped
//     header, not just trivia (more informative).
//   * The output range is derived from the matched token, not from
//     `unit.sourceMap.GetOriginalRange` on the AST node. For single-file
//     projects this is identical; for multi-file ones the new range may
//     be tighter (token-level instead of node-level).
//   * The old function-call path returned trivia raw (no header); Core now
//     prefixes a `function name(args)` code-block header before trivia.

using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using LSP.Services;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using CoreHoverHandler = FadeBasic.LSP.Core.Handlers.HoverHandler;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace LSP.Handlers;

public class HoverHandler : HoverHandlerBase
{
    private readonly CompilerService _compiler;

    public HoverHandler(CompilerService compiler)
    {
        _compiler = compiler;
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(HoverCapability capability, ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        };
    }

    public override Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units) || units.Count == 0)
            return Task.FromResult(default(Hover?));

        var unit = units[0];

        if (!unit.sourceMap.TryGetMappedLocation(
                request.TextDocument.Uri.GetFileSystemPath(),
                request.Position.Line,
                request.Position.Character,
                out _, out var mappedLine, out var mappedChar))
        {
            return Task.FromResult(default(Hover?));
        }

        // Install the project's docs so Core's command hover path renders
        // the same Markdown the pre-refactor native handler did.
        _compiler.TryGetDocsForSrc(request.TextDocument.Uri, out var projectDocs, out _);

        var doc = CoreAdapter.ToDocument(unit, request.TextDocument.Uri.ToString(), projectDocs);
        var hover = CoreHoverHandler.Compute(doc, mappedLine, mappedChar);
        if (hover == null) return Task.FromResult(default(Hover?));

        // Map the range back to the originating source file so multi-file
        // projects highlight the right region.
        var startTok = new Token { lineNumber = hover.Range.Start.Line, charNumber = hover.Range.Start.Character };
        var origin = unit.sourceMap.GetOriginalLocation(startTok);
        var len = System.Math.Max(1, hover.Range.End.Character - hover.Range.Start.Character);
        var range = new Range(origin.startLine, origin.startChar, origin.startLine, origin.startChar + len);

        return Task.FromResult<Hover?>(new Hover
        {
            Range = range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = hover.Contents ?? string.Empty,
            }),
        });
    }
}
