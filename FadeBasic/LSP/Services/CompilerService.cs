
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using ApplicationSupport.Code;
using ApplicationSupport.Docs;
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

    private Dictionary<DocumentUri, DocHost> _projectToDocHost = new Dictionary<DocumentUri, DocHost>();
    private Dictionary<DocumentUri, DocHost> _srcToDocHost = new Dictionary<DocumentUri, DocHost>();
    private Dictionary<DocumentUri, LexerResults> _docToLexResults = new Dictionary<DocumentUri, LexerResults>();
    private Dictionary<DocumentUri, ProgramNode> _docToAst = new Dictionary<DocumentUri, ProgramNode>();
    private Dictionary<DocumentUri, CodeUnit> _projectToUnit = new Dictionary<DocumentUri, CodeUnit>();
    private Dictionary<DocumentUri, ProjectDocs> _srcToDocs = new Dictionary<DocumentUri, ProjectDocs>();

    // Debounce state for UpdateDebounced. Typing cancels the previous
    // pending update so diagnostics are only computed (and published)
    // once the user pauses — mid-word and mid-statement states never
    // produce visible errors.
    public const int DebounceMs = 300;
    private readonly object _debounceLock = new object();
    private Dictionary<DocumentUri, System.Threading.CancellationTokenSource> _pendingUpdates
        = new Dictionary<DocumentUri, System.Threading.CancellationTokenSource>();

    /// <summary>
    /// Schedule an <see cref="Update"/> for this document after a short idle
    /// period. Each call resets the timer for that document, so a typing
    /// burst results in a single parse + diagnostics publish at the end.
    /// Use this from didChange; didOpen/didSave should call Update directly
    /// for immediate feedback.
    /// </summary>
    public void UpdateDebounced(DocumentUri srcUri)
    {
        System.Threading.CancellationTokenSource cts;
        lock (_debounceLock)
        {
            if (_pendingUpdates.TryGetValue(srcUri, out var pending))
            {
                pending.Cancel();
                pending.Dispose();
            }
            cts = _pendingUpdates[srcUri] = new System.Threading.CancellationTokenSource();
        }

        var token = cts.Token;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMs, token);
                Update(srcUri);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer keystroke — drop silently
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "debounced update failed for " + srcUri);
            }
            finally
            {
                lock (_debounceLock)
                {
                    if (_pendingUpdates.TryGetValue(srcUri, out var current) && current == cts)
                    {
                        _pendingUpdates.Remove(srcUri);
                    }
                }
            }
        });
    }

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
    
    public bool TryGetDocsForSrc(DocumentUri srcUri, out ProjectDocs unit, out DocHost docHost)
    {
        docHost = default;
        if (_srcToDocs.TryGetValue(srcUri, out unit) && _srcToDocHost.TryGetValue(srcUri, out docHost))
        {
            return true;
        }

        return false;
    }


    /// <summary>
    /// Parse the project owning <paramref name="sourceUri"/> from the CURRENT
    /// editor buffer, right now — no debounce, no diagnostics, no publishing,
    /// no caching side effects. Used by fast, read-only features (semantic
    /// highlighting) that must reflect the live buffer on every keystroke;
    /// lexing/parsing is cheap, so we don't make them wait on the debounced
    /// diagnostics pass (see <see cref="UpdateDebounced"/>).
    /// </summary>
    public bool TryParseFresh(DocumentUri sourceUri, out CodeUnit unit)
    {
        unit = null;
        if (!TryGetProjectContexts(sourceUri, out var projectUris)) return false;
        foreach (var projectUri in projectUris)
        {
            if (!_projects.TryGetProject(projectUri, out var project)) continue;
            var context = project.Item1;
            var commands = project.Item2;
            var sourceMap = context.CreateSourceMap(_docs.GetSourceLinesOrReadLines);
            unit = sourceMap.Parse(commands.collection);
            return true;
        }
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
    
    
    // serializes Update bodies — debounced updates run on a background task
    // and must not interleave with request-thread updates.
    private readonly object _updateGate = new object();

    public void Update(DocumentUri srcUri)
    {
        lock (_updateGate)
        {
            UpdateUnsafe(srcUri);
        }
    }

    void UpdateUnsafe(DocumentUri srcUri)
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
                
                
                if (!_projectToDocHost.TryGetValue(projectUri, out var docHost))
                {
                    docHost = _projectToDocHost[projectUri] = new DocHost(commands.docs, new DocHostOptions());
                    var _ = docHost.Start();
                }
                else
                {
                    docHost.ChangeData(commands.docs);
                }

                // ProjectDocMethods.LoadDocs<MarkdownDocParser>(project.Item1.);
                var sourceMap = context.CreateSourceMap(_docs.GetSourceLinesOrReadLines);
                
                var unit = sourceMap.Parse(commands.collection);
                _projectToUnit[projectUri] = unit;
                _docToLexResults[srcUri] = unit.lexerResults;
                _docToAst[srcUri] = unit.program;
                _srcToDocs[srcUri] = commands.docs;
                _srcToDocHost[srcUri] = docHost;
                
                // TODO: technically, this code unit is valid for the entire project at this point...
                
                var program = unit.program;

                foreach (var src in context.absoluteSourceFiles)
                {
                    fileToDiags[src] = new List<Diagnostic>();
                }

                foreach (var err in unit.lexerResults.tokenErrors)
                {
                    var location = sourceMap.GetOriginalRange(err.location);
                    if (!fileToDiags.TryGetValue(location.fileName, out var diags))
                    {
                        throw new InvalidOperationException("all files must already have empty diags");
                        // diags = fileToDiags[location.fileName] = new List<Diagnostic>();
                    }
                    diags.Add(new Diagnostic()
                    {
                        Code = err.errorCode.code,
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

                if (unit.lexerResults.tokenErrors.Count == 0)
                {
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