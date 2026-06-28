// Rename — thin adapter over FadeBasic.LSP.Core.Handlers.RenameHandler.
//
// ─── Behavioral audit (pre-refactor native vs Core) ─────────────────────
//
// Common:
//   * Both find the AST node behind the cursor, walk to the declaration via
//     DeclaredFromSymbol.source, then emit one TextEdit per reference site
//     (the declaration's name token + every node whose DeclaredFromSymbol
//     points back to it).
//   * Both use the same `GetNameToken` rules for which token actually gets
//     the replacement string (e.g., DeclarationStatement.EndToken to skip
//     the GLOBAL/LOCAL/DIM keyword).
//
// Diff:
//   * The old native handler walked both `unit.program` and
//     `unit.macroProgram`. Core walks only `doc.Program`. Tokens inside
//     macro-expanded regions don't yet rename through Core. (Aligns with
//     References / GotoDef — TODO if macro renames become a requirement.)
//   * The old handler returned ranges keyed by the request's DocumentUri.
//     Core returns ranges in unit (project-buffer) coordinates; we map
//     them back to originating files via `unit.sourceMap` here, so the
//     resulting WorkspaceEdit's URIs match the originating source files.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using LSP.Services;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using CoreRenameHandler = FadeBasic.LSP.Core.Handlers.RenameHandler;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace LSP.Handlers;

public class RenameHandler : RenameHandlerBase
{
    private readonly CompilerService _compiler;

    public RenameHandler(CompilerService compiler)
    {
        _compiler = compiler;
    }

    protected override RenameRegistrationOptions CreateRegistrationOptions(RenameCapability capability,
        ClientCapabilities clientCapabilities) => new RenameRegistrationOptions
    {
        DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        PrepareProvider = false,
    };

    public override Task<WorkspaceEdit?> Handle(RenameParams request, CancellationToken cancellationToken)
    {
        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units) || units.Count == 0)
            return Task.FromResult(default(WorkspaceEdit?));

        var unit = units[0];
        if (!unit.sourceMap.TryGetMappedLocation(
                request.TextDocument.Uri.GetFileSystemPath(),
                request.Position.Line,
                request.Position.Character,
                out _, out var mappedLine, out var mappedChar))
        {
            return Task.FromResult(default(WorkspaceEdit?));
        }

        var doc = CoreAdapter.ToDocument(unit, request.TextDocument.Uri.ToString());
        var ws = CoreRenameHandler.Compute(doc, mappedLine, mappedChar, request.NewName);
        if (ws == null || ws.Changes == null || ws.Changes.Count == 0)
            return Task.FromResult(default(WorkspaceEdit?));

        // Translate each edit back into the originating file's coordinate
        // space via `unit.sourceMap`. Edits from a single concatenated
        // project buffer may resolve to different source files.
        var changes = new Dictionary<DocumentUri, List<TextEdit>>();
        foreach (var kv in ws.Changes)
        {
            foreach (var edit in kv.Value)
            {
                var startTok = new Token { lineNumber = edit.Range.Start.Line, charNumber = edit.Range.Start.Character };
                var origin = unit.sourceMap.GetOriginalLocation(startTok);
                var len = System.Math.Max(1, edit.Range.End.Character - edit.Range.Start.Character);
                var key = DocumentUri.File(origin.fileName);
                if (!changes.TryGetValue(key, out var list))
                    changes[key] = list = new List<TextEdit>();
                list.Add(new TextEdit
                {
                    NewText = edit.NewText ?? string.Empty,
                    Range = new Range(origin.startLine, origin.startChar, origin.startLine, origin.startChar + len),
                });
            }
        }

        return Task.FromResult<WorkspaceEdit?>(new WorkspaceEdit
        {
            Changes = changes.ToDictionary(k => k.Key, v => (IEnumerable<TextEdit>)v.Value),
        });
    }
}
