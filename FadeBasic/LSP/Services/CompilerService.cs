
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ApplicationSupport.Code;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
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
    private Dictionary<DocumentUri, ProgramNode> _docToAst = new Dictionary<DocumentUri, ProgramNode>();
    private Dictionary<DocumentUri, CodeUnit> _projectToUnit = new Dictionary<DocumentUri, CodeUnit>();
    
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

    public bool TryGetParserResults(DocumentUri srcUri, out ProgramNode program)
    {
        if (_docToAst.TryGetValue(srcUri, out program))
        {
            return true;
        }
        Update(srcUri);
        if (_docToAst.TryGetValue(srcUri, out program))
        {
            return true;
        }

        return false;
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

    public bool TryGetProjectUnit(DocumentUri projectUri, out CodeUnit unit)
    {
        if (_projectToUnit.TryGetValue(projectUri, out unit))
        {
            return true;
        }

        // TODO: we should be able to compile the project...
        return false;
    }


    public bool TryGetProjectsFromSource(DocumentUri sourceUri, out List<CodeUnit> units)
    {
        units = null;
        if (!TryGetProjectContexts(sourceUri, out var projectUris))
        {
            // do nothing. This src is not listed in a valid project.
            _logger.LogWarning("unknown source file edit does not belong to any project");
            return false;
        }

        units = new List<CodeUnit>(projectUris.Count);
        foreach (var projectUri in projectUris)
        {
            if (!TryGetProjectUnit(projectUri, out var unit))
            {
                _logger.LogError("no compiled unit... must compile");
                continue;
            }
            units.Add(unit);
        }

        return true;
    }
    
    
    public void Update(DocumentUri srcUri)
    {
        if (!_docs.TryGetSourceDocument(srcUri, out var fullText))
        {
            _logger.LogError("cannot find source file " + srcUri);
            throw new Exception("cannot find src file");
        }

        if (!TryGetProjectContexts(srcUri, out var projectUris))
        {
            // do nothing. This src is not listed in a valid project.
            _logger.LogWarning("unknown source file edit does not belong to any project");
            return;
        }
        
        // Diagnostics are sent a document at a time, this example is for demonstration purposes only
        // var diagnostics = ImmutableArray<Diagnostic>.Empty.ToBuilder();
        var fileToDiags = new Dictionary<string, List<Diagnostic>>();

        // resolve the project...
        try
        {
            foreach (var projectUri in projectUris)
            {
                if (!_projects.TryGetProject(projectUri, out var project))
                {
                    _logger.LogWarning("project uri not found");
                    continue;
                }

                var context = project.Item1;
                var commands = project.Item2;

                var sourceMap = context.CreateSourceMap(_docs.GetSourceLinesOrReadLines);
                
                var unit = sourceMap.Parse(commands);
                _projectToUnit[projectUri] = unit;
                _docToLexResults[srcUri] = unit.lexerResults;
                _docToAst[srcUri] = unit.program;
                // TODO: technically, this code unit is valid for the entire project at this point...
                
                var program = unit.program;

                foreach (var src in context.absoluteSourceFiles)
                {
                    fileToDiags[src] = new List<Diagnostic>();
                }
                
                foreach (var err in program.GetAllErrors())
                {

                    var location = sourceMap.GetOriginalRange(err.location);
                    if (!fileToDiags.TryGetValue(location.fileName, out var diags))
                    {
                        throw new InvalidOperationException("all files must already have empty diags");
                        // diags = fileToDiags[location.fileName] = new List<Diagnostic>();
                    }
                    
                    diags.Add(new Diagnostic()
                    {
                        Code = err.errorCode.ToString(),
                        Severity = DiagnosticSeverity.Error,
                        Message = err.Display,
                        Range = new Range(
                            startLine: location.startLine,
                            startCharacter: location.startChar,
                            endLine: location.endLine,
                            endCharacter: location.endChar),
                        Source = FadeBasicConstants.FadeBasicLanguage,
                        Tags = new Container<DiagnosticTag>()
                    });
                }
            }

            foreach (var kvp in fileToDiags)
            {
                var container = new Container<Diagnostic>(kvp.Value);
                var uri = DocumentUri.File(kvp.Key);
                _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
                {
                    Diagnostics = container,
                    Uri = uri
                });
            }
            // _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams()
            // {
            //     Diagnostics = new Container<Diagnostic>(diagnostics.ToArray()),
            //     Uri = srcUri,
            //     // Version = request.TextDocument.Version
            // });
        }
        catch (Exception ex)
        {
            // _logger.LogInformation("uh oh! " + ex?.Message);
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