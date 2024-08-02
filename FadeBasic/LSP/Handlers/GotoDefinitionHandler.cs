using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ApplicationSupport.Code;
using FadeBasic;
using FadeBasic.Ast;
using LSP.Services;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace LSP.Handlers;

public class GotoDefinitionHandler : DefinitionHandlerBase
{
    private readonly ILogger<GotoDefinitionHandler> _logger;
    private readonly DocumentService _docs;
    private readonly CompilerService _compiler;

    public GotoDefinitionHandler(
        ILogger<GotoDefinitionHandler> logger, 
        DocumentService docs, 
        CompilerService compiler)
    {
        _logger = logger;
        _docs = docs;
        _compiler = compiler;
    }
    
    protected override DefinitionRegistrationOptions CreateRegistrationOptions(DefinitionCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new DefinitionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        };
    }

    public override async Task<LocationOrLocationLinks?> Handle(DefinitionParams request, CancellationToken cancellationToken)
    {
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

        // at this point, we know the token, but we need the part of the AST it represents. 

        var allowedTypes = new HashSet<Type>
        {
            typeof(VariableRefNode),
            typeof(ArrayIndexReference),
            typeof(GoSubStatement),
            typeof(GotoStatement),
        };
        var node = unit.program.FindFirst(x =>
        {
            if (!allowedTypes.Contains(x.GetType())) return false;
            return x.StartToken == token || x.EndToken == token;
        }); 
        _logger.LogInformation($"looking for {node}");

        LocationOrLocationLink location = null;
        switch (node)
        { 
            case ExpressionStatement exprStatement:
                location = GetLink(exprStatement.expression, unit);
                break;
            
            case GoSubStatement _:
            case GotoStatement _:
            case ArrayIndexReference _:
            case VariableRefNode _:
                location = GetLink(node, unit);
                break;
        }
        
        // once we know the AST node, we can look for its "declaration" AST node

        if (location == null)
        {
            return null;
        }

        return new LocationOrLocationLinks(location);
        // return null;
        // var links = new LocationOrLocationLink[]
        // {
        //     new LocationOrLocationLink(new Location
        //     {
        //         Uri = request.TextDocument.Uri,
        //         Range = new Range(20, 0, 20, 5)
        //     })
        // };
        return null;
        // return new LocationOrLocationLinks(links);
    }

    LocationOrLocationLink GetLink(IAstNode node, CodeUnit unit)
    {
        if (node.DeclaredFromSymbol == null) return null;
        
        var origin = node.DeclaredFromSymbol.source.StartToken;
        var definition = unit.sourceMap.GetOriginalLocation(origin);

        return
            new LocationOrLocationLink(new Location
            {
                Uri = DocumentUri.File(definition.fileName),
                Range = new Range(definition.startLine, definition.startChar, definition.startLine,
                    definition.startChar + origin.raw.Length)
            });

    }
}