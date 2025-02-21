using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using FadeBasic;

namespace LSP.Services;

public class ProjectService
{
    private ILogger<ProjectService> _logger;
    private readonly DocumentService _docs;
    private readonly ILanguageServerFacade _facade;

    private Dictionary<DocumentUri, (ProjectContext, ProjectCommandInfo)> _uriToProject =
        new Dictionary<DocumentUri, (ProjectContext, ProjectCommandInfo)>();

    public ProjectService(ILogger<ProjectService> logger, DocumentService docs, ILanguageServerFacade facade)
    {
        _logger = logger;
        _docs = docs;
        _facade = facade;
    }

    public bool TryGetProject(DocumentUri projectUri, out (ProjectContext, ProjectCommandInfo) context)
    {
        return _uriToProject.TryGetValue(projectUri, out context);
    }

    public void LoadProject(DocumentUri projectUri)
    {
        // Diagnostics are sent a document at a time, this example is for demonstration purposes only
        var diagnostics = ImmutableArray<Diagnostic>.Empty.ToBuilder();
        
        _logger.LogDebug($"loading project... uri=[{projectUri}]");
        if (!_docs.TryGetProjectDocument(projectUri, out var projectText))
        {
            throw new Exception("no project file exists");
        }

        try
        {
            var filePath = projectUri.GetFileSystemPath();
            var ctx = ProjectLoader.LoadCsProject(filePath);
            var commands = ProjectBuilder.LoadCommandMetadata(ctx);

            _uriToProject[projectUri] = (ctx, commands);     
            _logger.LogDebug($"loaded project... uri=[{projectUri}]");

        }
        catch (Exception ex)
        {   
            _logger.LogError($"failed to load project... uri=[{projectUri}] message=[{ex.Message}]");
            diagnostics.Add(new Diagnostic
            {
                Code = new DiagnosticCode(2),
                Severity = DiagnosticSeverity.Error,
                Message = "unable to load project - " + ex.Message,
                Range = new Range(
                    startLine: 0,
                    startCharacter: 0,
                    endLine: 0,
                    endCharacter: 1),
                Source = FadeBasicConstants.FadeBasicLanguage,
            });
        }
        finally
        {
            _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams()
            {
                Diagnostics = new Container<Diagnostic>(diagnostics.ToArray()),
                Uri = projectUri,
            });
        }
    }
}