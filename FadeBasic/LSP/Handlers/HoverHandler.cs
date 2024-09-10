using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Ast;
using FadeBasic.Virtual;
using LSP.Services;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace LSP.Handlers;

public class HoverHandler : HoverHandlerBase
{
    private readonly ILogger<HoverHandler> _logger;
    private readonly DocumentService _docs;
    private readonly CompilerService _compiler;
    private readonly ProjectService _project;

    public HoverHandler(
        ILogger<HoverHandler> logger,
        DocumentService docs,
        CompilerService compiler,
        ProjectService project)
    {
        _logger = logger;
        _docs = docs;
        _compiler = compiler;
        _project = project;
    }
    
    protected override HoverRegistrationOptions CreateRegistrationOptions(HoverCapability capability, ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        };
    }


    StringBuilder GenerateMarkdown(CommandInfo command, DocumentUri uri)
    {
        var sb = new StringBuilder();
        if (!_compiler.TryGetDocsForSrc(uri, out var docs, out var docHost))
        {
            sb.Append("no docs loaded");
            return sb;
        }

        CommandDocs foundCommand = null;
        CommandGroupDocs foundGroup = null;
        foreach (var docGroup in docs.groups)
        {
            if (foundCommand != null)
            {
                break;
            }
            
            foreach (var docCommand in docGroup.commands)
            {
                if (command.name == docCommand.commandName)
                {
                    foundGroup = docGroup;
                    foundCommand = docCommand;
                    break;
                }
            }
        }
        
        // var foundCommand = docs.groups.SelectMany(x => x.commands)
            // .FirstOrDefault(x => x.commandName == command.name);

        if (foundCommand == null)
        {
            sb.Append("no docs available"); // no token found
            return sb;
        }

        sb.AppendLine($"[Full Documentation]({docHost.GetUrlForCommand(foundGroup.title, foundCommand.commandName)})");

        sb.AppendLine($"### {foundCommand.commandName}");
        if (!string.IsNullOrEmpty(foundCommand.methodDocs.summary))
        {
            sb.AppendLine(foundCommand.methodDocs.summary.Trim());
        }
        sb.Append(Environment.NewLine);
        
        if (command.args.Length > 0)
        {
            sb.AppendLine($"#### Parameters");
            if (foundCommand.methodDocs.parameters.Count > command.args.Length)
            {
                sb.Append("(invalid number of parameter docs)");
                return sb;
            }
            for (var i = 0; i < command.args.Length; i++)
            {
                var arg = command.args[i];
                var parameter = i < foundCommand.methodDocs.parameters.Count ? foundCommand.methodDocs.parameters[i] : default;
                sb.Append("##### ");
                if (VmUtil.TryGetVariableTypeDisplay(arg.typeCode, out var type))
                {
                    sb.Append($"`{type}` ");
                }
                else
                {
                    sb.Append("_unknown_ ");
                }
                
                if (arg.isOptional)
                {
                    sb.Append("_(optional)_ ");
                }
                if (arg.isRef)
                {
                    sb.Append("_(ref)_ ");
                }

                if (parameter != default)
                {
                    sb.Append(parameter.name);
                    sb.Append(Environment.NewLine);
                    sb.AppendLine(parameter.body.Trim());
                }
                else
                {
                    sb.AppendLine("_(doc missing)_");
                }
            }
        }
        
        if (command.returnType != TypeCodes.VOID)
        {
            sb.Append(Environment.NewLine);
            sb.Append("#### Returns");
            if (VmUtil.TryGetVariableTypeDisplay(command.returnType, out var type))
            {
                sb.Append($" `{type}`");
            }

            if (!string.IsNullOrEmpty(foundCommand.methodDocs.returns))
            {
                sb.Append(Environment.NewLine);
                sb.AppendLine(foundCommand.methodDocs.returns.Trim());
            }
        }

        return sb;
    }

    public override Task<Hover> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units))
        {
            _logger.LogError($"source document=[{request.TextDocument.Uri}] did not map to any compiled unit");
            return null;
        }

        if (units.Count == 0) return Task.FromResult(default(Hover?));
        
        var unit = units[0]; // TODO: how should a project be tokenized if it belongs to more than 1 project? 

        /*
         * given the position, we could find the token,
         * from the token, we could look up and see
         */
        // _logger.LogInformation("looking for def : " + request.Position);

        if (!unit.sourceMap.GetMappedPosition(request.TextDocument.Uri.GetFileSystemPath(), request.Position.Line,
                request.Position.Character, out var token))
        {
            return Task.FromResult(default(Hover?)); // no token found
        }

        var local = unit.sourceMap.GetOriginalLocation(token);
        var range = new Range(local.startLine, local.startChar, local.startLine, token.raw.Length);

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

        var markdown = $"test [this]({request.TextDocument.Uri.ToString()}#L2%2C4)";
        if (referencedNodes.Count == 0)
        {
            markdown = "no known node";
        }
        
        else
        {
            var smalledReferencedNode = referencedNodes.MinBy(a =>
                a.EndToken.charNumber + (a.EndToken.raw?.Length ?? 0) - a.StartToken.charNumber);

            var expr = smalledReferencedNode;//referencedNodes[0];
            var exprRange = unit.sourceMap.GetOriginalRange(new TokenRange
            {
                start = expr.StartToken,
                end = expr.EndToken
            });
            range = new Range(exprRange.startLine, exprRange.startChar, exprRange.endLine, exprRange.endChar);

            switch (expr)
            {
                case AstNode node when node.DeclaredFromSymbol?.source is IHasTriviaNode triviaSource:
                    markdown = triviaSource.Trivia;
                    break;
                case ExpressionStatement exprStatement when exprStatement.StartToken.flags.HasFlag(TokenFlags.FunctionCall) && exprStatement.expression is ArrayIndexReference exprIndexRef:
                    if (!unit.program.scope.functionTable.TryGetValue(exprIndexRef.variableName, out var function))
                    {
                        markdown = "_function does not exist_";
                        break;
                    }

                    markdown = function.Trivia;
                    break;
                case ArrayIndexReference indexRef when indexRef.StartToken.flags.HasFlag(TokenFlags.FunctionCall):
                    if (!unit.program.scope.functionTable.TryGetValue(indexRef.variableName, out function))
                    {
                        markdown = "_function does not exist_";
                        break;
                    }

                    markdown = function.Trivia;
                    break;
                case CommandExpression expression:
                    markdown = GenerateMarkdown(expression.command, request.TextDocument.Uri).ToString();
                    // a command that returns something is an expression!
                    break;
                case CommandStatement statement:
                    markdown = GenerateMarkdown(statement.command, request.TextDocument.Uri).ToString();
                    break;
            }
        }

        var hover = new Hover()
        {
            Range = range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = markdown
            })
        };
        return Task.FromResult(hover);

    }
}