using System;
using System.Collections.Generic;
using System.IO;
using FadeBasic;
using System.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace LSP.Services;


public class DocumentService
{
    private Dictionary<DocumentUri, string> _uriToSource = new Dictionary<DocumentUri, string>();
    private Dictionary<DocumentUri, string> _uriToProject = new Dictionary<DocumentUri, string>();
    
    
    public IEnumerable<(DocumentUri, string)> AllProjects() 
    {
        foreach (var kvp in _uriToProject)
        {
            yield return (kvp.Key, kvp.Value);
        }
    }
    
    public void SetProjectDocument(DocumentUri uri, string fullText)
    {
        _uriToProject[uri] = fullText;
    }

    public void ClearProjectDocument(DocumentUri uri)
    {
        _uriToProject.Remove(uri);
    }

    public bool TryGetProjectDocument(DocumentUri uri, out string content)
    {
        return _uriToProject.TryGetValue(uri, out content);
    }
    
    public void SetSourceDocument(DocumentUri uri, string fullText)
    {
        _uriToSource[uri] = fullText;
    }

    public void ClearSourceDocument(DocumentUri uri)
    {
        _uriToSource.Remove(uri);
    }

    public bool TryGetSourceDocument(DocumentUri uri, out string content)
    {
        return _uriToSource.TryGetValue(uri, out content);
    }

    public void Populate(DocumentUri rootUri)
    {
        var filePath = rootUri.GetFileSystemPath();
        
        // find all .basic files and .basicProject.yaml files
        var basicFiles = Directory.GetFiles(filePath, $"*{FadeBasicConstants.FadeBasicScriptExt}", SearchOption.AllDirectories);
        var projectFiles = Directory.GetFiles(filePath, $"*{FadeBasicConstants.FadeBasicProjectExt}", SearchOption.AllDirectories);

        foreach (var projectFile in projectFiles)
        {
            var uri = DocumentUri.FromFileSystemPath(projectFile);
            var fullText = File.ReadAllText(projectFile);
            SetProjectDocument(uri, fullText);
            
            // start loading project in the background...
        }

        foreach (var sourceFile in basicFiles)
        {
            var uri = DocumentUri.FromFileSystemPath(sourceFile);
            var fullText = File.ReadAllText(sourceFile);
            SetSourceDocument(uri, fullText);
        }
    }
}