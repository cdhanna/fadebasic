
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using FadeBasic;
using FadeBasic.Ast;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;


namespace LSP.Services;

public class CompilerService
{
    private ILogger<CompilerService> _logger;
    private DocumentService _docs;
    private ProjectService _projects;
    private readonly ILanguageServerFacade _facade;

    private Dictionary<DocumentUri, LexerResults> _docToLexResults = new Dictionary<DocumentUri, LexerResults>();

    public CompilerService(ILogger<CompilerService> logger, 
        DocumentService docs, 
        ProjectService projects,
        ILanguageServerFacade facade)
    {
        _projects = projects;
        _facade = facade;
        _docs = docs;
        _logger = logger;
    }

    public bool TryGetLexerResults(DocumentUri srcUri, out LexerResults lexerResults)
    {
        if (_docToLexResults.TryGetValue(srcUri, out lexerResults))
        {
            return true;
        }
        
        Update(srcUri);
        if (_docToLexResults.TryGetValue(srcUri, out lexerResults))
        {
            return true;
        }

        return false;
    }
    
    public void Update(DocumentUri srcUri)
    {
        if (!_docs.TryGetSourceDocument(srcUri, out var fullText))
        {
            throw new Exception("cannot find src file");
        }

        if (!TryGetProjectContexts(srcUri, out var projectUris))
        {
            // do nothing. This src is not listed in a valid project.
            return;
        }
        
        // Diagnostics are sent a document at a time, this example is for demonstration purposes only
        var diagnostics = ImmutableArray<Diagnostic>.Empty.ToBuilder();

        // resolve the project...
        try
        {
            foreach (var projectUri in projectUris)
            {
                if (!_projects.TryGetProject(projectUri, out var project))
                {
                    continue;
                }
                
                var tokenizer = new Lexer();
                var tokenData = tokenizer.TokenizeWithErrors(fullText, project.Item2);
                _docToLexResults[srcUri] = tokenData; // TODO: what if there is more than one project?
                
                var parser = new Parser(tokenData.stream, project.Item2);
                var program = parser.ParseProgram();

                foreach (var err in program.GetAllErrors())
                {
                    diagnostics.Add(new Diagnostic()
                    {
                        Code = err.errorCode.ToString(),
                        Severity = DiagnosticSeverity.Error,
                        Message = err.Display,
                        Range = new Range(
                            startLine: err.location.start.lineNumber,
                            startCharacter: err.location.start.charNumber,
                            endLine: err.location.end.lineNumber,
                            endCharacter: err.location.end.charNumber),
                        Source = "basicScript",
                        Tags = new Container<DiagnosticTag>()
                    });
                }
            }

            _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams()
            {
                Diagnostics = new Container<Diagnostic>(diagnostics.ToArray()),
                Uri = srcUri,
                // Version = request.TextDocument.Version
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex.GetType().Name + " -- " + ex.Message + " \n " + ex.StackTrace);
        }
    }

    public bool TryGetProjectContexts(DocumentUri src, out List<DocumentUri> projects)
    {
        // a src file can be a part of multiple projects...
        
        // context = null;
        projects = new List<DocumentUri>();

        var srcPath = src.GetFileSystemPath();
        foreach (var (projectUri, projectFullText) in _docs.AllProjects())
        {
            if (!_projects.TryGetProject(projectUri, out var x))
            {
                continue;
            }

            var (context, commands) = x;
            if (context.absoluteSourceFiles.Contains(srcPath))
            {
                projects.Add(projectUri);
            }
        }
        
        return projects.Count > 0;
    }
    
    
}