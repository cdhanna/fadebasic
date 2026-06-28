// References — thin adapter over FadeBasic.LSP.Core.Handlers.ReferencesHandler.
//
// ─── Behavioral audit (pre-refactor native vs Core) ─────────────────────
//
// Common:
//   * Both find the token at the cursor (with a one-character left-drift
//     hail-mary for cursors sitting in whitespace).
//   * Both gather AST nodes whose StartToken/EndToken locations match the
//     clicked token, then chase DeclaredFromSymbol.source.
//
// Core ADDS:
//   * Clicking the declaration site (e.g. the LHS of an implicit `x = 1`)
//     now also returns the use sites. The old native handler returned only
//     the LHS node here because it followed a single chain — Core unions
//     every interpretation (node itself + DeclaredFromSymbol.source +
//     nodes whose DeclaredFromSymbol.source has a token at the clicked
//     position), so def-site clicks behave like use-site clicks.
//   * `or RuntoStatement` is in the allowed-types match list (old native
//     code had it too, parity here).
//
// Core MISSES (vs native):
//   * The old native walked `unit.macroProgram` in addition to
//     `unit.program`. Tokens inside macro-expanded regions don't currently
//     resolve through Core, which only inspects `doc.Program`.
//   * Multi-file source-map mapping happens HERE (not in Core), so ranges
//     come back as project-buffer coordinates and we translate them to
//     originating-file coordinates before returning.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using LSP.Services;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using CoreRefsHandler = FadeBasic.LSP.Core.Handlers.ReferencesHandler;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace LSP.Handlers;

public class FindReferencesHandler : ReferencesHandlerBase
{
    private readonly ILogger<FindReferencesHandler> _logger;
    private readonly CompilerService _compiler;

    public FindReferencesHandler(
        ILogger<FindReferencesHandler> logger,
        DocumentService docs,
        CompilerService compiler)
    {
        _logger = logger;
        _compiler = compiler;
    }

    protected override ReferenceRegistrationOptions CreateRegistrationOptions(ReferenceCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new ReferenceRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        };
    }

    public override Task<LocationContainer?> Handle(ReferenceParams request, CancellationToken cancellationToken)
    {
        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units) || units.Count == 0)
            return Task.FromResult(default(LocationContainer?));

        var unit = units[0];
        if (!unit.sourceMap.TryGetMappedLocation(
                request.TextDocument.Uri.GetFileSystemPath(),
                request.Position.Line,
                request.Position.Character,
                out _,
                out var mappedLine,
                out var mappedChar))
        {
            return Task.FromResult(default(LocationContainer?));
        }

        var doc = CoreAdapter.ToDocument(unit, request.TextDocument.Uri.ToString());
        var coreLocs = CoreRefsHandler.Compute(doc, mappedLine, mappedChar);
        if (coreLocs == null || coreLocs.Count == 0)
            return Task.FromResult(default(LocationContainer?));

        var locations = new List<Location>(coreLocs.Count);
        foreach (var l in coreLocs)
        {
            var startTok = new Token { lineNumber = l.Range.Start.Line, charNumber = l.Range.Start.Character };
            var origin = unit.sourceMap.GetOriginalLocation(startTok);
            var len = System.Math.Max(1, l.Range.End.Character - l.Range.Start.Character);
            locations.Add(new Location
            {
                Uri = DocumentUri.File(origin.fileName),
                Range = new Range(origin.startLine, origin.startChar, origin.startLine, origin.startChar + len),
            });
        }
        return Task.FromResult<LocationContainer?>(new LocationContainer(locations));
    }
}
