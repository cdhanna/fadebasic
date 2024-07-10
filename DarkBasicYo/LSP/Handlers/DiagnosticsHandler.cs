using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DarkBasicYo;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using DocumentUri = OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri;

namespace LSP.Handlers;

public class DiagnosticsHandler : DocumentDiagnosticHandlerBase
{
    protected override DiagnosticsRegistrationOptions CreateRegistrationOptions(DiagnosticClientCapabilities capability,
        ClientCapabilities clientCapabilities)
    {
        return new DiagnosticsRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("basicScript"),
            InterFileDependencies = false,
            WorkspaceDiagnostics = false,
            Id = "basicScriptDiag"
        };
    }

    public override async Task<RelatedDocumentDiagnosticReport> Handle(DocumentDiagnosticParams request, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(DocumentUri.GetFileSystemPath(request), cancellationToken);

        
        // var lexer = new Lexer();
        // var stream = lexer.Tokenize(content);
        // var parser = new Parser(new TokenStream(stream), new CommandCollection());
        // var program = parser.ParseProgram();

        var diag = new Diagnostic
        {
            Range = new Range(1, 1, 1, 5),
            Message = "hot tuna",
            Severity = DiagnosticSeverity.Error
        };
        var report = new RelatedFullDocumentDiagnosticReport
        {
            Items = new List<Diagnostic>{diag}
            // RelatedDocuments = new ImmutableDictionary<DocumentUri, DocumentDiagnosticReport>(n)
        };

        // report.RelatedDocuments.Add(request.TextDocument.Uri, subReport);
        
        return report;
    }
}