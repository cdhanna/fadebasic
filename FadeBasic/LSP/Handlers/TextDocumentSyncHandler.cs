using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using FadeBasic.Ast;
using LSP.Services;
using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;


namespace LSP.Handlers;

public class TextDocumentSyncHandler : TextDocumentSyncHandlerBase
{
    private readonly ILogger<TextDocumentSyncHandler> _logger;
    private readonly ILanguageServerFacade _facade;
    private readonly DocumentService _docs;


    private CompilerService _compiler;
    public TextDocumentSyncKind Change { get; } = TextDocumentSyncKind.Full;

    public TextDocumentSyncHandler(
        ILogger<TextDocumentSyncHandler> logger, 
        ILanguageServerFacade facade,
        DocumentService docs,
        CompilerService compiler
        )
    {
        _compiler = compiler;
        _logger = logger;
        _facade = facade;
        _docs = docs;
    }
    
    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        return new TextDocumentAttributes(uri, FadeBasicConstants.FadeBasicLanguage);
    }

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Opened " + request.TextDocument.Uri);
        _docs.SetSourceDocument(request.TextDocument.Uri, request.TextDocument.Text);
        _compiler.Update(request.TextDocument.Uri);

        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        var fullText = request.ContentChanges.FirstOrDefault().Text;
        _docs.SetSourceDocument(request.TextDocument.Uri, fullText);
        // Range = new Range(

        _compiler.Update(request.TextDocument.Uri);
        // try
        // {
        //     var tokenizer = new Lexer();
        //     var tokenData = tokenizer.TokenizeWithErrors(fullText);
        //
        //     var parser = new Parser(tokenData.stream, new CommandCollection());
        //     var program = parser.ParseProgram();
        //
        //     // Diagnostics are sent a document at a time, this example is for demonstration purposes only
        //     var diagnostics = ImmutableArray<Diagnostic>.Empty.ToBuilder();
        //
        //     foreach (var err in program.GetAllErrors())
        //     {
        //         diagnostics.Add(new Diagnostic()
        //         {
        //             Code = err.errorCode.ToString(),
        //             Severity = DiagnosticSeverity.Error,
        //             Message = err.Display,
        // Range = new Range(
        //                 startLine: err.location.start.lineNumber,
        //                 startCharacter: err.location.start.charNumber,
        //                 endLine: err.location.end.lineNumber,
        //                 endCharacter: err.location.end.charNumber),
        //             Source = "basicScript",
        //             Tags = new Container<DiagnosticTag>()
        //         });
        //     }
        //
        //     _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams()
        //     {
        //         Diagnostics = new Container<Diagnostic>(diagnostics.ToArray()),
        //         Uri = request.TextDocument.Uri,
        //         Version = request.TextDocument.Version
        //     });
        // }
        // catch (Exception ex)
        // {
        //     _logger.LogError(ex.GetType().Name + " -- " + ex.Message + " \n " + ex.StackTrace);
        // }

        return Unit.Task;

    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        return Unit.Task;

    }

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        // _docs.ClearSourceDocument(request.TextDocument.Uri);
        return Unit.Task;

    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        // _logger.LogInformation("getting config");
        return new TextDocumentSyncRegistrationOptions() {
            DocumentSelector = TextDocumentSelector.ForPattern($"**/*{FadeBasicConstants.FadeBasicScriptExt}"),
            Change = Change,
            Save = new SaveOptions() { IncludeText = true }
        };
    }
}