using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using FadeBasic.Ast;
using LSP.Services;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace LSP.Handlers;

public class FindReferencesHandler : ReferencesHandlerBase
{
    private readonly ILogger<FindReferencesHandler> _logger;
    private readonly CompilerService _compiler;

    public FindReferencesHandler(
        ILogger<FindReferencesHandler> logger, 
        DocumentService docs, 
        CompilerService compiler)
    {
        _logger = logger;
        _compiler = compiler;
    }

    protected override ReferenceRegistrationOptions CreateRegistrationOptions(ReferenceCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new ReferenceRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        };
    }

    public override async Task<LocationContainer?> Handle(ReferenceParams request, CancellationToken cancellationToken)
    {
        var locations = new List<Location>();
        
        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units))
        {
            _logger.LogError($"source document=[{request.TextDocument.Uri}] did not map to any compiled unit");
            return null;
        }

        var unit = units[0]; // TODO: how should a project be tokenized if it belongs to more than 1 project? 

        /*
         * given the position, we could find the token,
         * from the token, we could look up and see
         */
        _logger.LogInformation("looking for def : " + request.Position);

        if (!unit.sourceMap.GetMappedPosition(request.TextDocument.Uri.GetFileSystemPath(), request.Position.Line,
                request.Position.Character, out var token))
        {
            return null; // no token found
        }

        var referencedNodes = new List<IAstNode>();
        // var x = unit.program.scope.functionTable;
        unit.program.Visit(x =>
        {
            var isMatch = x.StartToken == token || x.EndToken == token;
            if (isMatch)
            {
                referencedNodes.Add(x);
            }
        });

        locations = referencedNodes.Select(x =>
        {
            var source = unit.sourceMap.GetOriginalLocation(x.StartToken);
            return new Location
            {
                Uri = DocumentUri.File(source.fileName),
                Range = new Range(source.startLine, source.startChar, source.startLine,
                    x.EndToken.charNumber + x.EndToken.raw?.Length ?? 0)
            };
        }).ToList();

        if (locations.Count == 0)
        {
            return null;
        }
        return new LocationContainer(locations);
    }
}