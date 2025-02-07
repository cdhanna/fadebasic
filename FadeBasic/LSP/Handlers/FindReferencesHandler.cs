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
        
        // use the token to resolve the variable

        var referencedNodes = new List<IAstNode>();
        // var x = unit.program.scope.functionTable;
        unit.program.Visit(x =>
        {
            bool isMatch = false;
            if (x is VariableRefNode or DeclarationStatement or ArrayIndexReference or LabelDeclarationNode or GoSubStatement or GotoStatement)
            {
                isMatch = x.StartToken == token || x.EndToken == token;
               
            } else if (x is FunctionStatement funcStatement)
            {
                isMatch = x.StartToken == token || funcStatement.nameToken == token;
            }
            
            if (isMatch)
            {
                referencedNodes.Add(x);
            }
        });

        if (referencedNodes.Count == 0)
        {
            return null;
            
        }
        var expr = referencedNodes[0];
        
        // if the user clicked on a reference to the root; this resolves it. 
        if (expr.DeclaredFromSymbol != null)
        {
            expr = expr.DeclaredFromSymbol.source;
        }

        var discoveredNodes = new List<IAstNode>
        {
            // the declaration counts as a reference
            expr
        };
        unit.program.Visit(x =>
        {
            if (x.DeclaredFromSymbol != null)
            {
                if (x.DeclaredFromSymbol.source == expr)
                {
                    discoveredNodes.Add(x);
                }
            }
        });
        
        
        locations = discoveredNodes.Select(x =>
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