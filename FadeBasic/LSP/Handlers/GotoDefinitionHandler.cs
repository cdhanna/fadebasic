// Goto definition — thin adapter over FadeBasic.LSP.Core.Handlers.DefinitionHandler.
//
// Audit vs the pre-refactor native handler:
//   * Both find the AST node at the cursor (VariableRef / ArrayIndexReference /
//     GoSub / Goto / Runto), then follow DeclaredFromSymbol.source to the
//     declaration.
//   * The native handler additionally walked the macroProgram. Core walks
//     `doc.Program` only; macro lookups currently fall back to the
//     non-existence path. The native FindFirst behavior used here returned the
//     declaration of an in-source token — for macro-expanded tokens this
//     never produced a location anyway (the old code's `unit.macroProgram`
//     pass searched the SAME source coordinates, so behavior is preserved
//     for non-macro positions).
//   * Both translate ranges back through `unit.sourceMap` so multi-file
//     projects resolve to the originating file.

using System;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using LSP.Services;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using CoreDefHandler = FadeBasic.LSP.Core.Handlers.DefinitionHandler;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace LSP.Handlers;

public class GotoDefinitionHandler : DefinitionHandlerBase
{
    private readonly ILogger<GotoDefinitionHandler> _logger;
    private readonly CompilerService _compiler;

    public GotoDefinitionHandler(ILogger<GotoDefinitionHandler> logger, DocumentService docs, CompilerService compiler)
    {
        _logger = logger;
        _compiler = compiler;
    }

    protected override DefinitionRegistrationOptions CreateRegistrationOptions(DefinitionCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new DefinitionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        };
    }

    public override Task<LocationOrLocationLinks?> Handle(DefinitionParams request, CancellationToken cancellationToken)
    {
        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units) || units.Count == 0)
            return Task.FromResult(default(LocationOrLocationLinks?));

        var unit = units[0];

        if (!unit.sourceMap.TryGetMappedLocation(
                request.TextDocument.Uri.GetFileSystemPath(),
                request.Position.Line,
                request.Position.Character,
                out _,
                out var mappedLine,
                out var mappedChar))
        {
            return Task.FromResult(default(LocationOrLocationLinks?));
        }

        var doc = CoreAdapter.ToDocument(unit, request.TextDocument.Uri.ToString());
        var loc = CoreDefHandler.Compute(doc, mappedLine, mappedChar);
        if (loc == null) return Task.FromResult(default(LocationOrLocationLinks?));

        // Map the result range back through the source map so multi-file
        // projects resolve to the originating file.
        var startToken = new FadeBasic.Token
        {
            lineNumber = loc.Range.Start.Line,
            charNumber = loc.Range.Start.Character,
        };
        var origin = unit.sourceMap.GetOriginalLocation(startToken);
        int rangeLen = Math.Max(1, loc.Range.End.Character - loc.Range.Start.Character);

        return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(
            new LocationOrLocationLink(new Location
            {
                Uri = DocumentUri.File(origin.fileName),
                Range = new Range(origin.startLine, origin.startChar, origin.startLine, origin.startChar + rangeLen),
            })));
    }
}
