// Document outline — thin adapter over FadeBasic.LSP.Core.Handlers.DocumentSymbolHandler.
//
// ─── Behavioral audit (pre-refactor native vs Core) ─────────────────────
//
// Pre-refactor native: enumerated *every* lexer token whose type wasn't
//   OpEqual / LiteralInt and dumped one DocumentSymbol per token, with the
//   token kind inferred from LexemType (Variable/String/Number/Key). This
//   produced an extremely noisy outline — every identifier, keyword, and
//   string literal in the file showed up as a separate symbol. It also
//   re-read the file from disk on every request (TODO comment acknowledged
//   this as a hack).
//
// Core: walks the parsed AST and emits structured outline entries —
//   FunctionStatement (with nested LabelDeclarationNode children),
//   top-level DeclarationStatement, TypeDefinitionStatement, LabelDeclarationNode.
//   Each entry has a full-extent Range (covers the body) and a
//   SelectionRange (just the name token), which is what VSCode's
//   breadcrumbs and outline view expect.
//
// Behavioral diff (intentional):
//   * No more per-token noise — only meaningful symbols.
//   * Range now covers the full body of a function/type/label rather than
//     a single token.
//   * No file IO — Core reads from the in-memory parse tree.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using LSP.Services;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using CoreDocSymbolHandler = FadeBasic.LSP.Core.Handlers.DocumentSymbolHandler;
using CoreDocSymbol = FadeBasic.LSP.Core.LspDocumentSymbol;
using LspSymbolKind = FadeBasic.LSP.Core.LspSymbolKind;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace LSP.Handlers;

public class DocumentSymbolHandler : IDocumentSymbolHandler
{
    private readonly CompilerService _compiler;

    public DocumentSymbolHandler(CompilerService compiler)
    {
        _compiler = compiler;
    }

    public Task<SymbolInformationOrDocumentSymbolContainer?> Handle(DocumentSymbolParams request, CancellationToken cancellationToken)
    {
        var empty = new SymbolInformationOrDocumentSymbolContainer(new List<SymbolInformationOrDocumentSymbol>());
        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units) || units.Count == 0)
            return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(empty);

        var doc = CoreAdapter.ToDocument(units[0], request.TextDocument.Uri.ToString());
        var coreSyms = CoreDocSymbolHandler.Compute(doc);

        var result = new List<SymbolInformationOrDocumentSymbol>(coreSyms.Count);
        foreach (var s in coreSyms) result.Add(new SymbolInformationOrDocumentSymbol(Convert(s)));
        return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(
            new SymbolInformationOrDocumentSymbolContainer(result));
    }

    private static DocumentSymbol Convert(CoreDocSymbol s)
    {
        var children = new List<DocumentSymbol>();
        if (s.Children != null)
            foreach (var c in s.Children) children.Add(Convert(c));

        return new DocumentSymbol
        {
            Name = s.Name ?? string.Empty,
            Detail = s.Detail ?? string.Empty,
            Kind = ToSymbolKind(s.Kind),
            Range = new Range(
                s.Range.Start.Line, s.Range.Start.Character,
                s.Range.End.Line, s.Range.End.Character),
            SelectionRange = new Range(
                s.SelectionRange.Start.Line, s.SelectionRange.Start.Character,
                s.SelectionRange.End.Line, s.SelectionRange.End.Character),
            Children = new Container<DocumentSymbol>(children),
        };
    }

    private static SymbolKind ToSymbolKind(LspSymbolKind kind)
    {
        return kind switch
        {
            LspSymbolKind.Function => SymbolKind.Function,
            LspSymbolKind.Variable => SymbolKind.Variable,
            LspSymbolKind.Constant => SymbolKind.Constant,
            LspSymbolKind.Struct   => SymbolKind.Struct,
            LspSymbolKind.Method   => SymbolKind.Method,
            LspSymbolKind.Interface => SymbolKind.Interface,
            LspSymbolKind.Key      => SymbolKind.Key,
            LspSymbolKind.Class    => SymbolKind.Class,
            LspSymbolKind.String   => SymbolKind.String,
            LspSymbolKind.Number   => SymbolKind.Number,
            _ => SymbolKind.Variable,
        };
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
