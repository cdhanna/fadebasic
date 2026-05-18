// Signature help: given a cursor position inside a command/function call,
// surface the call's parameter list and which parameter the cursor is on.
//
// Ported from FadeBasic/LSP/Handlers/SignatureHelpHandler.cs but stripped
// of the project/source-map indirection — the Core variant operates on a
// single FadeDocument. Project-wide command docs aren't available here yet
// (the native handler resolves them via ProjectService); when they are
// surfaced into Core, plumb them through FadeDocument and lift the same
// docs-map lookup into BuildCommandSignature.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace FadeBasic.LSP.Core.Handlers
{
    public class LspSignatureParameter
    {
        public string Label;
        public string Documentation;
    }

    public class LspSignatureInformation
    {
        public string Label;
        public string Documentation;
        public List<LspSignatureParameter> Parameters;
        public int ActiveParameter;
    }

    public class LspSignatureHelp
    {
        public List<LspSignatureInformation> Signatures;
        public int ActiveSignature;
        public int ActiveParameter;
    }

    public static class SignatureHelpHandler
    {
        public static LspSignatureHelp Compute(FadeDocument doc, int line, int character)
        {
            if (doc?.Program == null || doc.LexResults == null) return null;

            // Use char-1 — the cursor sits between characters; the token that
            // "encloses" the cursor is one to the left.
            var probeChar = character > 0 ? character - 1 : 0;
            var fakeToken = new Token { lineNumber = line, charNumber = probeChar };

            bool Visit(IAstVisitable v) =>
                Token.IsLocationBeforeOrEqual(v.StartToken, fakeToken) &&
                Token.IsLocationBeforeOrEqual(fakeToken, v.EndToken);

            var group = doc.Program.Where(Visit) ?? new List<IAstVisitable>();
            var node = group.LastOrDefault();

            // User-defined function call
            if (node is ArrayIndexReference arrRef &&
                arrRef.DeclaredFromSymbol?.source is FunctionStatement func)
            {
                return BuildFunctionSignature(func, arrRef.rankExpressions.Count);
            }

            // Built-in command — check innermost first, then walk up the group
            (CommandInfo command, List<IExpressionNode> args, List<int> argMap)? commandNode = node switch
            {
                CommandStatement cs => (cs.command, cs.args, cs.argMap),
                CommandExpression ce => (ce.command, ce.args, ce.argMap),
                _ => null,
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

            if (commandNode != null)
            {
                return BuildCommandSignature(
                    commandNode.Value.command,
                    commandNode.Value.args,
                    commandNode.Value.argMap);
            }

            // Fallback: AST is incomplete (e.g. user just typed `CommandName(`).
            // Walk tokens backward to find the enclosing `(` and the CommandWord before it.
            var tokens = doc.LexResults.allTokens;
            var activeParam = 0;
            var depth = 0;
            Token openParen = null;

            for (var i = tokens.Count - 1; i >= 0; i--)
            {
                var t = tokens[i];
                if (t.lineNumber > line) continue;
                if (t.lineNumber == line && t.charNumber > probeChar) continue;

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
                Token nameToken = null;
                foreach (var t in tokens)
                {
                    if (t.lineNumber > openParen.lineNumber) break;
                    if (t.lineNumber == openParen.lineNumber && t.charNumber >= openParen.charNumber) break;
                    nameToken = t;
                }

                if (nameToken?.type == LexemType.CommandWord)
                {
                    var commandName = nameToken.caseInsensitiveRaw;
                    var command = doc.Commands.Commands.FirstOrDefault(
                        c => string.Equals(c.name, commandName, System.StringComparison.OrdinalIgnoreCase));

                    if (command.name != null)
                    {
                        return BuildCommandSignature(
                            command,
                            new List<IExpressionNode>(),
                            new List<int>(),
                            activeParam);
                    }
                }
            }

            return null;
        }

        // --- User-defined functions ----------------------------------------

        private static LspSignatureHelp BuildFunctionSignature(FunctionStatement func, int activeParam)
        {
            var paramInfos = new List<LspSignatureParameter>();
            foreach (var param in func.parameters)
            {
                paramInfos.Add(new LspSignatureParameter
                {
                    Label = $"{param.variable.variableName} as {param.type.variableType}",
                });
            }

            var labelParts = func.parameters.Select(p => $"{p.variable.variableName} as {p.type.variableType}");
            var signatureLabel = $"{func.name}({string.Join(", ", labelParts)})";

            return new LspSignatureHelp
            {
                Signatures = new List<LspSignatureInformation>
                {
                    new LspSignatureInformation
                    {
                        Label = signatureLabel,
                        Documentation = string.IsNullOrEmpty(func.Trivia) ? null : func.Trivia,
                        Parameters = paramInfos,
                        ActiveParameter = activeParam,
                    },
                },
                ActiveSignature = 0,
                ActiveParameter = activeParam,
            };
        }

        // --- Built-in commands ---------------------------------------------

        private static LspSignatureHelp BuildCommandSignature(
            CommandInfo command,
            List<IExpressionNode> args,
            List<int> argMap,
            int tokenWalkActiveParam = -1)
        {
            // Visible params = skip VM-internal and raw args
            var visibleArgs = command.args
                .Select((a, i) => (arg: a, index: i))
                .Where(x => !x.arg.isVmArg && !x.arg.isRawArg)
                .ToList();

            if (visibleArgs.Count == 0) return null;

            int activeCommandArgIndex;
            if (tokenWalkActiveParam >= 0)
            {
                activeCommandArgIndex = System.Math.Min(tokenWalkActiveParam, visibleArgs[visibleArgs.Count - 1].index);
            }
            else if (args.Count == 0 || argMap.Count == 0)
            {
                activeCommandArgIndex = 0;
            }
            else
            {
                var lastArgInfoIndex = argMap[args.Count - 1];
                activeCommandArgIndex = command.args[lastArgInfoIndex].isParams
                    ? lastArgInfoIndex
                    : lastArgInfoIndex + 1;
            }

            var activeVisibleIndex = visibleArgs.FindIndex(x => x.index == activeCommandArgIndex);
            if (activeVisibleIndex < 0)
                activeVisibleIndex = visibleArgs.Count - 1;

            var paramLabels = new List<string>();
            var paramInfos = new List<LspSignatureParameter>();
            for (var vi = 0; vi < visibleArgs.Count; vi++)
            {
                var arg = visibleArgs[vi].arg;
                var paramName = $"arg{vi + 1}";
                var label = BuildArgLabel(arg, paramName);
                paramLabels.Add(label);
                paramInfos.Add(new LspSignatureParameter { Label = label });
            }

            var signatureLabel = $"{command.name}({string.Join(", ", paramLabels)})";

            return new LspSignatureHelp
            {
                Signatures = new List<LspSignatureInformation>
                {
                    new LspSignatureInformation
                    {
                        Label = signatureLabel,
                        Parameters = paramInfos,
                        ActiveParameter = activeVisibleIndex,
                    },
                },
                ActiveSignature = 0,
                ActiveParameter = activeVisibleIndex,
            };
        }

        private static string BuildArgLabel(CommandArgInfo arg, string name)
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
}
