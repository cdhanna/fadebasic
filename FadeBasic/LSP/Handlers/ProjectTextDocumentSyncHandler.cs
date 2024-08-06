
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LSP.Services;
using MediatR;

namespace LSP.Handlers;
using FadeBasic;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;


public class ProjectTextDocumentSyncHandler: TextDocumentSyncHandlerBase
{
    private readonly ILogger<ProjectTextDocumentSyncHandler> _logger;
    private readonly ILanguageServerFacade _facade;
    private readonly DocumentService _docs;
    private ProjectService _projects;

    public ProjectTextDocumentSyncHandler(
        ILogger<ProjectTextDocumentSyncHandler> logger, 
        ILanguageServerFacade facade,
        DocumentService docs,
        ProjectService projects
    )
    {
        _projects = projects;
        _logger = logger;
        _facade = facade;
        _docs = docs;
    }
    
    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        return new TextDocumentAttributes(uri, "xml");
    }


    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("opened the project file!!!");
        // var diagnostics = ImmutableArray<Diagnostic>.Empty.ToBuilder();
        // diagnostics.Add(new Diagnostic()
        // {
        //     Code = "doop",
        //     Severity = DiagnosticSeverity.Error,
        //     Message = "hello",
        //     Range = new Range(1, 2, 1, 4),
        //     Source = "basicScriptProject",
        //     Tags = new Container<DiagnosticTag>()
        // });
        //
        // _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams()
        // {
        //     Diagnostics = new Container<Diagnostic>(diagnostics.ToArray()),
        //     Uri = request.TextDocument.Uri,
        //     Version = request.TextDocument.Version
        // });
        return Task.FromResult(Unit.Value);
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("changing text in yaml file");

        var fullText = request.ContentChanges.FirstOrDefault().Text;
        _docs.SetProjectDocument(request.TextDocument.Uri, fullText);
        
        return Task.FromResult(Unit.Value);

    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        _projects.LoadProject(request.TextDocument.Uri);
        return Task.FromResult(Unit.Value);
    }

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        return Task.FromResult(Unit.Value);

    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(TextSynchronizationCapability capability,
        ClientCapabilities clientCapabilities)
    {
        _logger.LogInformation("registered");

        return new TextDocumentSyncRegistrationOptions() {
            DocumentSelector = TextDocumentSelector.ForPattern($"**/*{FadeBasic.FadeBasicConstants.CSharpProjectExt}"),
            Change = TextDocumentSyncKind.Full,
            Save = new SaveOptions() { IncludeText = true }
        };
    }
}