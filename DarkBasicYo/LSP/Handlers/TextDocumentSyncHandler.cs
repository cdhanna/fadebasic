using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;


namespace LSP.Handlers;

public class TextDocumentSyncHandler : TextDocumentSyncHandlerBase
{
    private readonly ILogger<TextDocumentSyncHandler> _logger;
    private readonly ILanguageServerFacade _facade;

    private readonly TextDocumentSelector _textDocumentSelector = new TextDocumentSelector(
        new TextDocumentFilter {
            Pattern = "**/*.basic"
        }
    );
    public TextDocumentSyncKind Change { get; } = TextDocumentSyncKind.Full;

    public TextDocumentSyncHandler(ILogger<TextDocumentSyncHandler> logger, ILanguageServerFacade facade)
    {
        _logger = logger;
        _facade = facade;
    }
    
    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        return new TextDocumentAttributes(uri, "basicScript");
    }

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Opened " + request.TextDocument.Uri);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Changed " + request.TextDocument.Uri);
        // Diagnostics are sent a document at a time, this example is for demonstration purposes only
        var diagnostics = ImmutableArray<Diagnostic>.Empty.ToBuilder();

        diagnostics.Add(new Diagnostic()
        {
            Code = "ErrorCode_001",
            Severity = DiagnosticSeverity.Error,
            Message = "Something bad happened",
            Range = new Range(1,1,1,5),
            Source = "XXX",
            Tags = new Container<DiagnosticTag>(new DiagnosticTag[] { DiagnosticTag.Unnecessary })
        });
        

        _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams() 
        {
            Diagnostics = new Container<Diagnostic>(diagnostics.ToArray()),
            Uri = request.TextDocument.Uri,
            Version = request.TextDocument.Version
        });
        return Unit.Task;

    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Saved " + request.TextDocument.Uri);

        return Unit.Task;

    }

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Closed " + request.TextDocument.Uri);

        return Unit.Task;

    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        // _logger.LogInformation("getting config");
        return new TextDocumentSyncRegistrationOptions() {
            DocumentSelector = TextDocumentSelector.ForLanguage("basicScript"),
            Change = Change,
            Save = new SaveOptions() { IncludeText = true }
        };
    }
}