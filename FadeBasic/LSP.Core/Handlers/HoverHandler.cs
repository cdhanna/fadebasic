// Surface hover info at a position. Precedence:
//   1. Any diagnostic that covers the position → error markdown.
//   2. A built-in command call → rich markdown from ICommandDocsProvider.
//   3. A function call (user-defined) → trivia from the function decl.
//   4. A symbol reference / declaration → name + type + trivia (as markdown).
//   5. Generic token info as a fallback.

using System.Collections.Generic;
using System.Text;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace FadeBasic.LSP.Core.Handlers
{
    public static class HoverHandler
    {
        public static LspHoverResult Compute(FadeDocument doc, int line, int character)
        {
            if (doc == null) return null;
            doc.EnsureTrivia(); // lazily materialize doc-comment trivia we render below

            // First: any diagnostic at this position
            if (doc.Program != null)
            {
                foreach (var err in doc.Program.GetAllErrors())
                {
                    if (PositionInsideRange(err.location, line, character))
                        return new LspHoverResult
                        {
                            Contents = "**Error " + err.errorCode.code + "**\n\n" + err.CombinedMessage,
                            Range = RangeOf(err.location),
                        };
                }
            }
            if (doc.LexResults?.tokenErrors != null)
            {
                foreach (var err in doc.LexResults.tokenErrors)
                {
                    if (PositionInsideRange(err.location, line, character))
                        return new LspHoverResult
                        {
                            Contents = "**Lex error " + err.errorCode.code + "**\n\n" + err.CombinedMessage,
                            Range = RangeOf(err.location),
                        };
                }
            }

            // Symbol-aware hover: when the token under the cursor belongs to a
            // VariableRef / ArrayIndexReference / FunctionStatement /
            // DeclarationStatement, surface name + type + doc-comment.
            // Built-in commands take priority so we can show rich docs.
            if (doc.Program != null)
            {
                var hover = TryComputeCommandHover(doc, line, character)
                            ?? TryComputeSymbolHover(doc, line, character);
                if (hover != null) return hover;
            }

            // Otherwise: basic info on the token at this position
            if (doc.LexResults != null)
            {
                foreach (var token in doc.LexResults.allTokens)
                {
                    if (token.raw == null) continue;
                    if (token.lineNumber != line) continue;
                    if (character < token.charNumber) continue;
                    if (character > token.charNumber + token.Length) continue;

                    return new LspHoverResult
                    {
                        Contents = "`" + token.raw + "` — " + token.type.ToString(),
                        Range = new LspRange
                        {
                            Start = new LspPosition { Line = token.lineNumber, Character = token.charNumber },
                            End = new LspPosition { Line = token.lineNumber, Character = token.charNumber + token.Length },
                        },
                    };
                }
            }

            return null;
        }

        // Builds a markdown hover for a built-in command at the position.
        // Walks the AST for the smallest CommandStatement / CommandExpression
        // whose token range encloses the cursor. If we have a docs provider
        // we surface summary + parameters + returns + remarks + examples,
        // mirroring the native LSP's behavior; otherwise we return a basic
        // signature header so the user at least sees the command name.
        private static LspHoverResult TryComputeCommandHover(FadeDocument doc, int line, int character)
        {
            var fakeToken = new Token { lineNumber = line, charNumber = character };
            CommandInfo? command = null;
            IAstNode owner = null;

            doc.Program.Visit(node =>
            {
                if (node is ProgramNode) return;
                if (node.StartToken == null || node.EndToken == null) return;
                if (!Token.IsLocationBeforeOrEqual(node.StartToken, fakeToken)) return;
                if (!Token.IsLocationBeforeOrEqual(fakeToken, node.EndToken)) return;
                switch (node)
                {
                    case CommandStatement cs:
                        // Prefer the innermost enclosing node — keep updating.
                        command = cs.command; owner = cs;
                        break;
                    case CommandExpression ce:
                        command = ce.command; owner = ce;
                        break;
                }
            });
            if (command == null || owner == null) return null;

            var md = BuildCommandMarkdown(command.Value, doc.Docs);
            return new LspHoverResult
            {
                Contents = md,
                Range = new LspRange
                {
                    Start = new LspPosition { Line = owner.StartToken.lineNumber, Character = owner.StartToken.charNumber },
                    End = new LspPosition { Line = owner.EndToken.lineNumber, Character = owner.EndToken.charNumber + (owner.EndToken.raw?.Length ?? owner.EndToken.Length) },
                },
            };
        }

        // Public so hosts that want to surface command docs in their own
        // UI (e.g. WebRuntime's Help tab) can reuse the same markdown
        // renderer the hover uses, keeping both surfaces in sync.
        public static string BuildCommandMarkdown(CommandInfo command, ICommandDocsProvider docsProvider)
        {
            var docs = docsProvider?.Lookup(command);
            var sb = new StringBuilder();

            if (docs != null && !string.IsNullOrEmpty(docs.Url))
                sb.AppendLine($"[Full Documentation]({docs.Url})\n");

            sb.AppendLine("### " + command.name);
            AppendCommandBody(sb, command, docs);
            return sb.ToString();
        }

        // The full markdown for a command NAME that may carry several overloads:
        // the name once, then each overload's signature followed by ITS OWN doc
        // decoration (summary/params/returns/remarks/examples). A single-overload
        // command reads the same as BuildCommandMarkdown, minus the redundant
        // signature line. Used by the help browser so overloaded commands show
        // every variant instead of collapsing to the first.
        public static string BuildOverloadedCommandMarkdown(string name, IReadOnlyList<CommandInfo> overloads, ICommandDocsProvider docsProvider)
        {
            var sb = new StringBuilder();
            sb.AppendLine("### " + name);
            if (overloads == null || overloads.Count == 0) return sb.ToString();

            var showSignatures = overloads.Count > 1;
            for (var i = 0; i < overloads.Count; i++)
            {
                var command = overloads[i];
                var docs = docsProvider?.Lookup(command);

                // With multiple overloads, head each one with its signature so
                // the reader can tell which variant the following docs describe.
                if (showSignatures)
                {
                    sb.AppendLine();
                    sb.AppendLine("#### `" + BuildCommandSignatureString(command) + "`");
                }

                if (docs != null && !string.IsNullOrEmpty(docs.Url))
                    sb.AppendLine($"[Full Documentation]({docs.Url})\n");

                AppendCommandBody(sb, command, docs);
            }
            return sb.ToString();
        }

        // A one-line, human-readable signature, e.g.
        // `inc(ref Integer arg1, [Integer arg2]) -> Integer`.
        private static string BuildCommandSignatureString(CommandInfo command)
        {
            var parts = new List<string>();
            var args = command.args ?? new CommandArgInfo[0];
            var visibleIdx = 0;
            foreach (var a in args)
            {
                if (a.isVmArg || a.isRawArg) continue;
                var part = new StringBuilder();
                if (a.isRef) part.Append("ref ");
                part.Append(VmUtil.TryGetVariableTypeDisplay(a.typeCode, out var tn) ? tn : "?");
                if (a.isParams) part.Append("...");
                part.Append(" arg").Append(++visibleIdx);
                if (a.isOptional) { part.Insert(0, "["); part.Append("]"); }
                parts.Add(part.ToString());
            }
            var sig = command.name + "(" + string.Join(", ", parts) + ")";
            if (command.returnType != TypeCodes.VOID && VmUtil.TryGetVariableTypeDisplay(command.returnType, out var rt))
                sig += " -> " + rt;
            return sig;
        }

        // Renders everything below the `### name` header: summary, parameters,
        // returns, remarks, examples. Shared by the single- and multi-overload
        // markdown builders so each overload gets the same decoration.
        private static void AppendCommandBody(StringBuilder sb, CommandInfo command, ICommandDocs docs)
        {
            if (!string.IsNullOrEmpty(docs?.Summary))
                sb.AppendLine(docs.Summary.Trim() + "\n");

            // Parameters
            var visibleArgs = command.args ?? new CommandArgInfo[0];
            int visibleCount = 0;
            foreach (var a in visibleArgs) if (!a.isVmArg && !a.isRawArg) visibleCount++;
            if (visibleCount > 0)
            {
                sb.AppendLine("#### Parameters");
                int paramIdx = 0;
                for (var i = 0; i < visibleArgs.Length; i++)
                {
                    var arg = visibleArgs[i];
                    if (arg.isVmArg || arg.isRawArg) continue;
                    sb.Append("##### ");
                    if (VmUtil.TryGetVariableTypeDisplay(arg.typeCode, out var typeName))
                        sb.Append("`").Append(typeName).Append("` ");
                    else
                        sb.Append("_unknown_ ");
                    if (arg.isOptional) sb.Append("_(optional)_ ");
                    if (arg.isRef) sb.Append("_(ref)_ ");
                    if (arg.isParams) sb.Append("_(params)_ ");

                    var pdoc = (docs?.Parameters != null && paramIdx < docs.Parameters.Count) ? docs.Parameters[paramIdx] : null;
                    if (pdoc != null)
                    {
                        sb.Append(pdoc.Name);
                        sb.Append('\n');
                        if (!string.IsNullOrEmpty(pdoc.Body)) sb.AppendLine(pdoc.Body.Trim());
                    }
                    else
                    {
                        sb.AppendLine("arg" + (paramIdx + 1));
                    }
                    paramIdx++;
                }
            }

            if (command.returnType != TypeCodes.VOID)
            {
                sb.AppendLine();
                sb.Append("#### Returns");
                if (VmUtil.TryGetVariableTypeDisplay(command.returnType, out var typeName))
                    sb.Append(" `").Append(typeName).Append('`');
                if (!string.IsNullOrEmpty(docs?.Returns))
                {
                    sb.Append('\n');
                    sb.AppendLine(docs.Returns.Trim());
                }
                else
                {
                    sb.AppendLine();
                }
            }

            if (!string.IsNullOrEmpty(docs?.Remarks))
            {
                sb.AppendLine();
                sb.AppendLine("#### Remarks");
                sb.AppendLine(docs.Remarks.Trim());
            }

            if (docs?.Examples != null && docs.Examples.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("#### Examples");
                foreach (var ex in docs.Examples) sb.AppendLine(ex.Trim());
            }
        }

        // Builds a markdown hover for symbol-bearing nodes. Returns null when
        // the position isn't on a known symbol.
        private static LspHoverResult TryComputeSymbolHover(FadeDocument doc, int line, int character)
        {
            var token = ReferencesHandler.FindTokenAt(doc, line, character);
            if (token == null) return null;

            IAstNode hit = null;
            doc.Program.Visit(x =>
            {
                if (hit != null) return;
                bool match = false;
                switch (x)
                {
                    case VariableRefNode _:
                    case ArrayIndexReference _:
                    case DeclarationStatement _:
                    case ParameterNode _:
                    case LabelDeclarationNode _:
                    case GoSubStatement _:
                    case GotoStatement _:
                    case RuntoStatement _:
                        match = Token.AreLocationsEqual(token, x.StartToken)
                                || Token.AreLocationsEqual(token, x.EndToken);
                        break;
                    case FunctionStatement fs:
                        match = x.StartToken == token || fs.nameToken == token
                                || Token.AreLocationsEqual(token, x.StartToken)
                                || Token.AreLocationsEqual(token, fs.nameToken);
                        break;
                }
                if (match) hit = x;
            });
            if (hit == null) return null;

            // Resolve a reference to its declaration so we can read trivia.
            var decl = hit;
            if (decl.DeclaredFromSymbol?.source is IAstNode resolved) decl = resolved;

            var (header, trivia) = DescribeDeclaration(decl, hit);
            if (header == null) return null;

            var md = header;
            if (!string.IsNullOrEmpty(trivia))
                md += "\n\n---\n\n" + NormalizeTrivia(trivia);

            return new LspHoverResult
            {
                Contents = md,
                Range = new LspRange
                {
                    Start = new LspPosition { Line = token.lineNumber, Character = token.charNumber },
                    End = new LspPosition { Line = token.lineNumber, Character = token.charNumber + (token.raw?.Length ?? token.Length) },
                },
            };
        }

        // Returns (markdown header, raw trivia). header includes a fenced
        // code block; trivia is added separately so we can normalize it.
        private static (string header, string trivia) DescribeDeclaration(IAstNode decl, IAstNode hitNode)
        {
            string trivia = null;
            if (decl is IHasTriviaNode th) trivia = th.Trivia;
            else if (hitNode is IHasTriviaNode th2) trivia = th2.Trivia;

            switch (decl)
            {
                case FunctionStatement func:
                {
                    var parts = new System.Text.StringBuilder();
                    parts.Append("function ").Append(func.name ?? func.nameToken?.raw ?? "<fn>").Append('(');
                    if (func.parameters != null)
                    {
                        for (var i = 0; i < func.parameters.Count; i++)
                        {
                            if (i > 0) parts.Append(", ");
                            var p = func.parameters[i];
                            parts.Append(p.variable?.variableName ?? "?")
                                 .Append(" as ")
                                 .Append(p.type?.variableType.ToString() ?? "?");
                        }
                    }
                    parts.Append(')');
                    return ("```fade\n" + parts + "\n```", trivia);
                }
                case DeclarationStatement d:
                {
                    var typeName = d.type?.variableType.ToString() ?? "?";
                    var name = d.variableNode?.variableName ?? d.EndToken?.raw ?? "?";
                    return ("```fade\n" + name + " as " + typeName + "\n```", trivia);
                }
                case ParameterNode p:
                {
                    var typeName = p.type?.variableType.ToString() ?? "?";
                    var name = p.variable?.variableName ?? "?";
                    return ("```fade\n" + name + " as " + typeName + "  (parameter)\n```", trivia);
                }
                case LabelDeclarationNode lbl:
                {
                    return ("```fade\n:" + lbl.label + "\n```", trivia);
                }
                case VariableRefNode v:
                {
                    return ("```fade\n" + (v.variableName ?? "?") + "\n```", trivia);
                }
                case ArrayIndexReference a:
                {
                    var name = a.variableName ?? "?";
                    return ("```fade\n" + name + "(...)\n```", trivia);
                }
            }
            return (null, null);
        }

        private static string NormalizeTrivia(string raw)
        {
            // Strip leading comment markers (`'`, `rem`, ``` ` ```) and trim each line.
            var lines = raw.Replace("\r\n", "\n").Split('\n');
            var sb = new System.Text.StringBuilder();
            foreach (var line in lines)
            {
                var l = line.TrimStart();
                if (l.StartsWith("`")) l = l.Substring(1).TrimStart();
                else if (l.StartsWith("'")) l = l.Substring(1).TrimStart();
                else if (l.StartsWith("rem ", System.StringComparison.OrdinalIgnoreCase)) l = l.Substring(4).TrimStart();
                else if (l.Equals("rem", System.StringComparison.OrdinalIgnoreCase)) l = string.Empty;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(l);
            }
            return sb.ToString().TrimEnd();
        }

        private static bool PositionInsideRange(TokenRange range, int line, int character)
        {
            if (range == null) return false;
            var s = range.start; var e = range.end;
            if (s == null || e == null) return false;
            if (line < s.lineNumber || line > e.lineNumber) return false;
            if (line == s.lineNumber && character < s.charNumber) return false;
            if (line == e.lineNumber && character > e.charNumber + e.Length) return false;
            return true;
        }

        private static LspRange RangeOf(TokenRange range)
        {
            var s = range.start; var e = range.end ?? s;
            return new LspRange
            {
                Start = new LspPosition { Line = s?.lineNumber ?? 0, Character = s?.charNumber ?? 0 },
                End = new LspPosition
                {
                    Line = e?.lineNumber ?? 0,
                    Character = (e?.charNumber ?? 0) + (e?.Length ?? 1),
                },
            };
        }
    }
}
