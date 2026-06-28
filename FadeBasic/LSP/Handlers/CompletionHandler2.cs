// Completion — thin adapter over FadeBasic.LSP.Core.Handlers.CompletionHandler.
//
// ─── Behavioral audit (pre-refactor native vs Core) ─────────────────────
//
// Pre-refactor native:
//   * Mapped position via `unit.sourceMap.TryGetMappedLocation`.
//   * Built a CompletionContext (macro vs non-macro program, leftToken,
//     scope's positionedVariables entry) and invoked `LSPUtil.GetCompletions`.
//   * Translated PortableCompletionItem → OmniSharp CompletionItem.
//   * On `unit.macroProgram == null` while leftToken is a macro token,
//     returned a single `<NO MACRO PROG>` placeholder item. Likewise for
//     missing `unit.program` → `<NO PROG>`.
//
// Core CompletionHandler:
//   * Does the SAME context building + LSPUtil.GetCompletions invocation,
//     just without the placeholder items (returns an empty list when the
//     program or macroProgram is unavailable). This is the only behavioral
//     diff vs the old native handler — debug strings no longer leak into
//     completions, which is what users want.
//
// Native still owns:
//   * Per-document URI → CodeUnit lookup (CompilerService).
//   * sourceMap position mapping (multi-file projects).
//   * Documentation field — `func.Trivia` for user-defined functions is
//     already populated by LSPUtil; built-in commands have no per-item
//     documentation in either implementation. (The hover handler is the
//     surface that shows command docs.)

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using LSP.Services;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using CoreCompletionHandler = FadeBasic.LSP.Core.Handlers.CompletionHandler;
using LspCompletionItem = FadeBasic.LSP.Core.LspCompletionItem;
using LspCompletionKind = FadeBasic.LSP.Core.LspCompletionKind;

namespace LSP.Handlers;

public class CompletionHandler2 : CompletionHandlerBase
{
    private readonly CompilerService _compiler;

    public CompletionHandler2(CompilerService compiler)
    {
        _compiler = compiler;
    }

    protected override CompletionRegistrationOptions CreateRegistrationOptions(CompletionCapability capability,
        ClientCapabilities clientCapabilities) => new CompletionRegistrationOptions
    {
        DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        TriggerCharacters = new Container<string>(" ", ".", "(", "=", "+", "*", "-", "/"),
        ResolveProvider = false,
    };

    public override Task<CompletionList?> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units) || units.Count == 0)
            return Task.FromResult(default(CompletionList?));

        var unit = units[0];

        if (!unit.sourceMap.TryGetMappedLocation(
                request.TextDocument.Uri.GetFileSystemPath(),
                request.Position.Line,
                request.Position.Character,
                out _, out var mappedLine, out var mappedChar))
        {
            return Task.FromResult(default(CompletionList?));
        }

        var doc = CoreAdapter.ToDocument(unit, request.TextDocument.Uri.ToString());
        var coreItems = CoreCompletionHandler.Compute(doc, mappedLine, mappedChar);

        var items = new List<CompletionItem>(coreItems.Count);
        foreach (var p in coreItems) items.Add(ToOmni(p));
        return Task.FromResult<CompletionList?>(new CompletionList(items, isIncomplete: false));
    }

    private static CompletionItem ToOmni(LspCompletionItem p)
    {
        return new CompletionItem
        {
            Label = p.Label ?? string.Empty,
            InsertText = p.InsertText ?? string.Empty,
            Kind = ToCompletionItemKind(p.Kind),
            Detail = p.Detail ?? string.Empty,
            SortText = p.SortText,
            FilterText = p.FilterText,
            InsertTextFormat = p.InsertTextFormat == FadeBasic.LSP.Core.LspInsertTextFormat.Snippet
                ? InsertTextFormat.Snippet
                : InsertTextFormat.PlainText,
            InsertTextMode = InsertTextMode.AdjustIndentation,
            Documentation = string.IsNullOrEmpty(p.Documentation)
                ? null
                : new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = p.Documentation!,
                },
            Command = p.TriggerParameterHints
                ? new OmniSharp.Extensions.LanguageServer.Protocol.Models.Command
                {
                    Name = "editor.action.triggerParameterHints",
                    Title = "Trigger Parameter Hints",
                }
                : null,
        };
    }

    private static CompletionItemKind ToCompletionItemKind(LspCompletionKind kind)
    {
        switch (kind)
        {
            case LspCompletionKind.Variable: return CompletionItemKind.Variable;
            case LspCompletionKind.Function: return CompletionItemKind.Function;
            case LspCompletionKind.Interface: return CompletionItemKind.Interface;
            case LspCompletionKind.Keyword: return CompletionItemKind.Keyword;
            case LspCompletionKind.Field: return CompletionItemKind.Field;
            case LspCompletionKind.Class: return CompletionItemKind.Class;
            case LspCompletionKind.Constant: return CompletionItemKind.Constant;
            case LspCompletionKind.Reference: return CompletionItemKind.Reference;
            case LspCompletionKind.Folder: return CompletionItemKind.Folder;
            default: return CompletionItemKind.Text;
        }
    }

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }
}
