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

public class RenameHandler : RenameHandlerBase
{
    private readonly ILogger<RenameHandler> _logger;
    private readonly CompilerService _compiler;

    public RenameHandler(ILogger<RenameHandler> logger, CompilerService compiler)
    {
        
        _logger = logger;
        _compiler = compiler;
    }

    protected override RenameRegistrationOptions CreateRegistrationOptions(RenameCapability capability,
        ClientCapabilities clientCapabilities) => new RenameRegistrationOptions
    {
        DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        PrepareProvider = false,
    };

    public override Task<WorkspaceEdit?> Handle(RenameParams request, CancellationToken cancellationToken)
    {
        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units))
            return Task.FromResult(default(WorkspaceEdit?));

        var unit = units[0];

        if (!unit.sourceMap.GetMappedPosition(request.TextDocument.Uri.GetFileSystemPath(),
                request.Position.Line, request.Position.Character, out var token))
        {
            if (!unit.sourceMap.GetMappedPosition(request.TextDocument.Uri.GetFileSystemPath(),
                    request.Position.Line, request.Position.Character - 1, out token))
                return Task.FromResult(default(WorkspaceEdit?));
        }

        // var declarationNode = ResolveDeclaration(unit, token);
        
        var allowedTypes = new HashSet<Type>
        {
            typeof(VariableRefNode),
            typeof(ArrayIndexReference),
            typeof(GoSubStatement),
            typeof(GotoStatement),
            typeof(DeclarationStatement),
            typeof(ParameterNode),
        };

        bool Visit(IAstVisitable x)
        {
            if (!allowedTypes.Contains(x.GetType())) return false;
            return x.StartToken == token || x.EndToken == token;
        }
        IAstNode? declarationNode = unit.program.FindFirst(Visit) 
                                    ?? unit.macroProgram?.FindFirst(Visit);
        
        declarationNode = declarationNode?.DeclaredFromSymbol?.source ?? declarationNode;
        if (declarationNode == null)
            return Task.FromResult(default(WorkspaceEdit?));

        var edits = new List<TextEdit>();
        CollectEdits(unit, declarationNode, request.NewName, edits);

        _logger.LogInformation($"Rename: found {edits.Count} edits for '{request.NewName}' in {request.TextDocument.Uri}");

        if (edits.Count == 0)
            return Task.FromResult(default(WorkspaceEdit?));

        return Task.FromResult<WorkspaceEdit?>(new WorkspaceEdit
        {
            Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
            {
                [request.TextDocument.Uri] = edits
            }
        });
    }

    // public override Task<RangeOrPlaceholderRange?> Handle(PrepareRenameParams request,
    //     CancellationToken cancellationToken)
    // {
    //     if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units))
    //         return Task.FromResult(default(RangeOrPlaceholderRange?));
    //
    //     var unit = units[0];
    //
    //     if (!unit.sourceMap.GetMappedPosition(request.TextDocument.Uri.GetFileSystemPath(),
    //             request.Position.Line, request.Position.Character, out var token))
    //     {
    //         if (!unit.sourceMap.GetMappedPosition(request.TextDocument.Uri.GetFileSystemPath(),
    //                 request.Position.Line, request.Position.Character - 1, out token))
    //             return Task.FromResult(default(RangeOrPlaceholderRange?));
    //     }
    //
    //     var declarationNode = ResolveDeclaration(unit, token);
    //     if (declarationNode == null)
    //         return Task.FromResult(default(RangeOrPlaceholderRange?));
    //
    //     var nameToken = GetNameToken(declarationNode);
    //     var loc = unit.sourceMap.GetOriginalLocation(nameToken);
    //     var range = new Range(loc.startLine, loc.startChar, loc.startLine,
    //         loc.startChar + (nameToken.raw?.Length ?? 0));
    //
    //     return Task.FromResult<RangeOrPlaceholderRange?>(
    //         new RangeOrPlaceholderRange(new PlaceholderRange
    //         {
    //             Range = range,
    //             Placeholder = nameToken.raw ?? string.Empty
    //         }));
    // }

    // -------------------------------------------------------------------------

    /// <summary>
    /// Given the token under the cursor, walks up to the declaration node
    /// (the node that owns the symbol — not a reference to it).
    /// </summary>
    IAstNode? ResolveDeclaration(CodeUnit unit, Token token)
    {
        IAstNode? found = null;

        void Visit(IAstVisitable x)
        {
            bool isMatch = x switch
            {
                VariableRefNode => Token.AreLocationsEqual(token, x.StartToken),
                DeclarationStatement => Token.AreLocationsEqual(token, x.StartToken) || Token.AreLocationsEqual(token, x.EndToken),
                ArrayIndexReference => Token.AreLocationsEqual(token, x.StartToken),
                LabelDeclarationNode => Token.AreLocationsEqual(token, x.StartToken),
                GoSubStatement => Token.AreLocationsEqual(token, x.StartToken) || Token.AreLocationsEqual(token, x.EndToken),
                GotoStatement => Token.AreLocationsEqual(token, x.StartToken) || Token.AreLocationsEqual(token, x.EndToken),
                FunctionStatement fs => Token.AreLocationsEqual(token, x.StartToken) || Token.AreLocationsEqual(token, fs.nameToken),
                _ => false
            };

            if (isMatch) found = x;
        }

        unit.program.Visit(Visit);
        unit.macroProgram?.Visit(Visit);

        if (found == null) return null;

        // Walk up to declaration if this is a reference
        if (found.DeclaredFromSymbol != null)
            found = found.DeclaredFromSymbol.source;

        return found;
    }

    /// <summary>
    /// Returns the token that represents just the name portion of a declaration node.
    /// </summary>
    static Token GetNameToken(IAstNode node) => node switch
    {
        FunctionStatement fs => fs.nameToken,
        // Variable name is at EndToken; StartToken is the scope keyword (GLOBAL/LOCAL/DIM)
        DeclarationStatement decl => decl.EndToken,
        // ParameterNode.StartToken == parameter.variable.startToken (the name token)
        ParameterNode param => param.StartToken,
        _ => node.StartToken
    };

    void CollectEdits(CodeUnit unit, IAstNode declarationNode, string newName, List<TextEdit> edits)
    {
        // Include the declaration itself
        AddEdit(unit, declarationNode, newName, edits);

        // Include all references that point back to this declaration
        unit.program.Visit(x =>
        {
            if (x == declarationNode) return;
            if (x.DeclaredFromSymbol?.source == declarationNode)
                AddEdit(unit, x, newName, edits);
            if (x.DeclaredFromSymbol?.source is AssignmentStatement assignment &&
                assignment.variable == declarationNode)
            {
                AddEdit(unit, x, newName, edits);
            }
        });

        unit.macroProgram?.Visit(x =>
        {
            if (x == declarationNode) return;
            if (x.DeclaredFromSymbol?.source == declarationNode)
                AddEdit(unit, x, newName, edits);
        });
    }

    void AddEdit(CodeUnit unit, IAstNode node, string newName, List<TextEdit> edits)
    {
        var nameToken = GetNameToken(node);
        var loc = unit.sourceMap.GetOriginalLocation(nameToken);
        edits.Add(new TextEdit
        {
            NewText = newName,
            Range = new Range(loc.startLine, loc.startChar, loc.startLine,
                loc.startChar + (nameToken.raw?.Length ?? 0))
        });
    }
}
