// Formatting — thin adapter over FadeBasic.LSP.Core.Handlers.FormattingHandler.
//
// ─── Behavioral audit (pre-refactor native vs Core) ─────────────────────
//
// Both run `TokenFormatter.Format(unit.lexerResults.combinedTokens, settings)`
// and translate the resulting edits to LSP TextEdits.
//
// Native passed casing from the language-server configuration setting
// `conf.language.fade.formatCasing` ("upper" | "lower" | other). Core
// takes a TabSize/InsertSpaces/Casing options object. We adapt by reading
// the same setting before invoking Core.
//
// Native re-lexed the source from disk on every request. Now we lex once
// per document change via CompilerService and reuse those tokens through
// Core. This avoids reading the file system on every format and keeps the
// formatter output in sync with everything else the LSP has parsed.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using LSP.Services;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using CoreFormatHandler = FadeBasic.LSP.Core.Handlers.FormattingHandler;
using LspCasingSetting = FadeBasic.LSP.Core.Handlers.LspCasingSetting;
using LspFormattingOptions = FadeBasic.LSP.Core.Handlers.LspFormattingOptions;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace LSP.Handlers;

public class FormattingHandler : DocumentFormattingHandlerBase
{
    protected override DocumentFormattingRegistrationOptions CreateRegistrationOptions(DocumentFormattingCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new DocumentFormattingRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        };
    }

    private readonly ILogger<FormattingHandler> _logger;
    private readonly ILanguageServerConfiguration _lsp;
    private readonly CompilerService _compiler;

    public FormattingHandler(
        ILanguageServerConfiguration lsp,
        ILogger<FormattingHandler> logger,
        CompilerService compiler)
    {
        _lsp = lsp;
        _logger = logger;
        _compiler = compiler;
    }

    public override async Task<TextEditContainer?> Handle(DocumentFormattingParams request, CancellationToken cancellationToken)
    {
        var edits = new List<TextEdit>();

        // Honor the existing language-server config setting that controls
        // identifier casing — same behavior as the pre-refactor handler.
        var config = await _lsp.GetConfiguration(new ConfigurationItem
        {
            Section = "conf.language.fade",
        });
        var casingStr = config.GetSection("conf.language.fade")["formatCasing"];
        var casing = LspCasingSetting.Ignore;
        if (string.Equals("upper", casingStr, StringComparison.InvariantCultureIgnoreCase))
            casing = LspCasingSetting.ToUpper;
        else if (string.Equals("lower", casingStr, StringComparison.InvariantCultureIgnoreCase))
            casing = LspCasingSetting.ToLower;

        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units) || units.Count == 0)
            return edits;

        var doc = CoreAdapter.ToDocument(units[0], request.TextDocument.Uri.ToString());
        var coreEdits = CoreFormatHandler.Compute(doc, new LspFormattingOptions
        {
            TabSize = request.Options.TabSize,
            InsertSpaces = request.Options.InsertSpaces,
            Casing = casing,
        });

        foreach (var e in coreEdits)
        {
            edits.Add(new TextEdit
            {
                Range = new Range(
                    e.Range.Start.Line, e.Range.Start.Character,
                    e.Range.End.Line, e.Range.End.Character),
                NewText = e.NewText ?? string.Empty,
            });
        }

        return edits;
    }
}
