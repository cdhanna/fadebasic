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
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace LSP.Handlers;

public class SignatureHelpHandler : SignatureHelpHandlerBase
{
    private readonly ILogger<SignatureHelpHandler> _logger;
    private readonly CompilerService _compiler;
    private readonly ProjectService _project;

    public SignatureHelpHandler(ILogger<SignatureHelpHandler> logger, CompilerService compiler, ProjectService project)
    {
        _logger = logger;
        _compiler = compiler;
        _project = project;
    }

    protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(
        SignatureHelpCapability capability,
        ClientCapabilities clientCapabilities) => new SignatureHelpRegistrationOptions
    {
        DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        TriggerCharacters = new Container<string>("(", ","),
        RetriggerCharacters = new Container<string>(","),
    };

    public override Task<SignatureHelp?> Handle(SignatureHelpParams request, CancellationToken cancellationToken)
    {
        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units))
            return Task.FromResult(default(SignatureHelp?));

        var unit = units[0];

        if (!unit.sourceMap.TryGetMappedLocation(
                request.TextDocument.Uri.GetFileSystemPath(),
                request.Position.Line,
                request.Position.Character - 1,
                out _,
                out var mappedLine,
                out var mappedChar))
        {
            return Task.FromResult(default(SignatureHelp?));
        }

        var fakeToken = new Token { lineNumber = mappedLine, charNumber = mappedChar };

        bool Visit(IAstVisitable v) =>
            Token.IsLocationBeforeOrEqual(v.StartToken, fakeToken) &&
            Token.IsLocationBeforeOrEqual(fakeToken, v.EndToken);

        var group = unit.program.Where(Visit);
        var node = group.LastOrDefault();

        _logger.LogInformation("SignatureHelp node: " + node?.GetType().Name);

        // User-defined function call
        if (node is ArrayIndexReference arrRef &&
            arrRef.DeclaredFromSymbol?.source is FunctionStatement func)
        {
            return Task.FromResult(BuildFunctionSignature(func, arrRef.rankExpressions.Count));
        }

        // Built-in command — check innermost first, then walk up the group
        (CommandInfo command, List<IExpressionNode> args, List<int> argMap)? commandNode = node switch
        {
            CommandStatement cs => (cs.command, cs.args, cs.argMap),
            CommandExpression ce => (ce.command, ce.args, ce.argMap),
            _ => null
        };

        if (commandNode == null)
        {
            // cursor may be inside an arg expression; walk up to find the enclosing command
            for (var i = group.Count - 2; i >= 0; i--)
            {
                if (group[i] is CommandStatement cs2)
                {
                    commandNode = (cs2.command, cs2.args, cs2.argMap);
                    break;
                }
                if (group[i] is CommandExpression ce2)
                {
                    commandNode = (ce2.command, ce2.args, ce2.argMap);
                    break;
                }
            }
        }

        // Get project data once — needed for both AST and token-walk paths
        CommandDocs? commandDocs = null;
        ProjectCommandInfo? commandData = null;
        if (_compiler.TryGetProjectContexts(request.TextDocument.Uri, out var projects) &&
            _project.TryGetProject(projects[0], out var projectData))
        {
            commandData = projectData.Item2;
        }

        if (commandNode != null)
        {
            commandData?.docs.map.TryGetValue(commandNode.Value.command.sig, out commandDocs);
            return Task.FromResult(BuildCommandSignature(commandNode.Value.command, commandNode.Value.args, commandNode.Value.argMap, commandDocs));
        }

        // Fallback: AST is incomplete (e.g. user just typed `CommandName(`).
        // Walk tokens backward to find the enclosing `(` and the CommandWord before it.
        if (commandData != null)
        {
            var tokens = unit.lexerResults.allTokens;
            var activeParam = 0;
            var depth = 0;
            Token? openParen = null;

            for (var i = tokens.Count - 1; i >= 0; i--)
            {
                var t = tokens[i];
                if (t.lineNumber > mappedLine) continue;
                if (t.lineNumber == mappedLine && t.charNumber > mappedChar) continue;

                if (t.type == LexemType.ParenClose)        depth++;
                else if (t.type == LexemType.ParenOpen)
                {
                    if (depth > 0) depth--;
                    else { openParen = t; break; }
                }
                else if (t.type == LexemType.ArgSplitter && depth == 0)
                    activeParam++;
            }

            if (openParen != null)
            {
                // Find the token immediately before the `(`
                Token? nameToken = null;
                foreach (var t in tokens)
                {
                    if (t.lineNumber > openParen.lineNumber) break;
                    if (t.lineNumber == openParen.lineNumber && t.charNumber >= openParen.charNumber) break;
                    nameToken = t;
                }

                if (nameToken?.type == LexemType.CommandWord)
                {
                    var commandName = nameToken.caseInsensitiveRaw;
                    var command = unit.commands.Commands.FirstOrDefault(
                        c => string.Equals(c.name, commandName, System.StringComparison.OrdinalIgnoreCase));

                    if (command.name != null)
                    {
                        commandData.docs.map.TryGetValue(command.sig, out commandDocs);
                        return Task.FromResult(BuildCommandSignature(command, new List<IExpressionNode>(), new List<int>(), commandDocs, activeParam));
                    }
                }
            }
        }

        return Task.FromResult(default(SignatureHelp?));
    }

    // -------------------------------------------------------------------------
    // User-defined functions
    // -------------------------------------------------------------------------

    SignatureHelp? BuildFunctionSignature(FunctionStatement func, int activeParam)
    {
        var paramInfos = new List<ParameterInformation>();
        foreach (var param in func.parameters)
        {
            paramInfos.Add(new ParameterInformation
            {
                Label = new ParameterInformationLabel($"{param.variable.variableName} as {param.type.variableType}"),
            });
        }

        var labelParts = func.parameters.Select(p => $"{p.variable.variableName} as {p.type.variableType}");
        var signatureLabel = $"{func.name}({string.Join(", ", labelParts)})";

        var sigInfo = new SignatureInformation
        {
            Label = signatureLabel,
            Documentation = string.IsNullOrEmpty(func.Trivia)
                ? null
                : new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = func.Trivia }),
            Parameters = new Container<ParameterInformation>(paramInfos),
            ActiveParameter = activeParam,
        };

        return new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(sigInfo),
            ActiveSignature = 0,
            ActiveParameter = activeParam,
        };
    }

    // -------------------------------------------------------------------------
    // Built-in commands
    // -------------------------------------------------------------------------

    SignatureHelp? BuildCommandSignature(
        CommandInfo command,
        List<IExpressionNode> args,
        List<int> argMap,
        CommandDocs? docs,
        int tokenWalkActiveParam = -1)
    {
        // Visible params = skip VM-internal and raw args
        var visibleArgs = command.args
            .Select((a, i) => (arg: a, index: i))
            .Where(x => !x.arg.isVmArg && !x.arg.isRawArg)
            .ToList();

        if (visibleArgs.Count == 0)
            return null;

        // Compute which CommandArgInfo index the cursor is at.
        // tokenWalkActiveParam is used when the AST is incomplete (user just opened the paren).
        int activeCommandArgIndex;
        if (tokenWalkActiveParam >= 0)
        {
            activeCommandArgIndex = System.Math.Min(tokenWalkActiveParam, visibleArgs[^1].index);
        }
        else if (args.Count == 0 || argMap.Count == 0)
        {
            activeCommandArgIndex = 0;
        }
        else
        {
            var lastArgInfoIndex = argMap[args.Count - 1];
            activeCommandArgIndex = command.args[lastArgInfoIndex].isParams
                ? lastArgInfoIndex           // stay on the variadic param
                : lastArgInfoIndex + 1;
        }

        // Map CommandArgInfo index → visible param index
        var activeVisibleIndex = visibleArgs.FindIndex(x => x.index == activeCommandArgIndex);
        if (activeVisibleIndex < 0)
            activeVisibleIndex = visibleArgs.Count - 1; // clamp to last (e.g. past all optional params)

        // Build parameter information
        var paramLabels = new List<string>();
        var paramInfos = new List<ParameterInformation>();
        for (var vi = 0; vi < visibleArgs.Count; vi++)
        {
            var (arg, _) = visibleArgs[vi];
            var paramName = docs?.methodDocs.parameters.Count > vi
                ? docs.methodDocs.parameters[vi].name
                : $"arg{vi + 1}";
            var paramDoc = docs?.methodDocs.parameters.Count > vi
                ? docs.methodDocs.parameters[vi].body?.Trim()
                : null;

            var label = BuildArgLabel(arg, paramName);
            paramLabels.Add(label);
            paramInfos.Add(new ParameterInformation
            {
                Label = new ParameterInformationLabel(label),
                Documentation = string.IsNullOrEmpty(paramDoc)
                    ? null
                    : new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = paramDoc }),
            });
        }

        // Build the full signature label
        var signatureLabel = $"{command.name}({string.Join(", ", paramLabels)})";

        // Build documentation for the whole signature
        StringOrMarkupContent? sigDoc = null;
        if (!string.IsNullOrEmpty(docs?.methodDocs.summary))
            sigDoc = new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = docs!.methodDocs.summary });

        var sigInfo = new SignatureInformation
        {
            Label = signatureLabel,
            Documentation = sigDoc,
            Parameters = new Container<ParameterInformation>(paramInfos),
            ActiveParameter = activeVisibleIndex,
        };

        return new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(sigInfo),
            ActiveSignature = 0,
            ActiveParameter = activeVisibleIndex,
        };
    }

    static string BuildArgLabel(CommandArgInfo arg, string name)
    {
        VmUtil.TryGetVariableTypeDisplay(arg.typeCode, out var typeName);
        var sb = new StringBuilder();
        if (arg.isRef) sb.Append("ref ");
        sb.Append(typeName);
        if (arg.isParams) sb.Append("...");
        sb.Append(' ');
        sb.Append(name);
        if (arg.isOptional)
        {
            sb.Insert(0, '[');
            sb.Append(']');
        }
        return sb.ToString();
    }
}