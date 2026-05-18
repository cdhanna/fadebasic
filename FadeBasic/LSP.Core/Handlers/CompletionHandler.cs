// Compute completion items at a position. Builds a CompletionContext for the
// existing FadeBasic.Lsp.LSPUtil.GetCompletions which does the real work.

using System.Collections.Generic;
using System.Linq;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Lsp;
using LspCompletionContext = FadeBasic.Lsp.CompletionContext;

namespace FadeBasic.LSP.Core.Handlers
{
    public static class CompletionHandler
    {
        public static List<LspCompletionItem> Compute(FadeDocument doc, int line, int character)
        {
            if (doc?.LexResults == null || doc.Program == null) return new List<LspCompletionItem>();

            var fakeToken = new Token { lineNumber = line, charNumber = character };

            // Find the nearest token to the left.
            Token leftToken = null;
            for (int i = doc.LexResults.allTokens.Count - 1; i >= 0; i--)
            {
                var token = doc.LexResults.allTokens[i];
                if (token.lineNumber < line)
                {
                    leftToken = token;
                    break;
                }
                if (token.lineNumber == line && token.charNumber <= character)
                {
                    leftToken = token;
                    break;
                }
            }

            if (leftToken == null) return new List<LspCompletionItem>();

            bool isMacro = leftToken.flags.HasFlag(TokenFlags.IsMacroToken);

            bool Visit(IAstVisitable v)
            {
                return v is ProgramNode
                    || (Token.IsLocationBeforeOrEqual(v.StartToken, fakeToken)
                        && Token.IsLocationBeforeOrEqual(fakeToken, v.EndToken));
            }

            ProgramNode programNode;
            IEnumerable<IAstVisitable> group;
            if (isMacro && doc.LexResults.macroProgram != null)
            {
                programNode = doc.LexResults.macroProgram;
                group = programNode?.Where(Visit);
            }
            else
            {
                programNode = doc.Program;
                group = programNode?.Where(Visit);
            }

            if (programNode == null) return new List<LspCompletionItem>();

            // Locate the function/scope context the position is inside.
            if (!programNode.scope.positionedVariables.TryFindEntry(fakeToken, out var entry))
            {
                if (programNode.scope.positionedVariables.entries.Count == 0)
                    return new List<LspCompletionItem>();
                entry = programNode.scope.positionedVariables.entries[0];
            }

            var context = new LspCompletionContext
            {
                IsMacro = isMacro,
                FakeToken = fakeToken,
                LeftToken = leftToken,
                Program = programNode,
                Commands = doc.Commands,
                FunctionName = entry.value.Item2,
                Group = group?.ToList(),
                ConstantTable = doc.LexResults.constantTable,
                LocalScope = entry.value.Item1,
            };

            var portable = LSPUtil.GetCompletions(context);
            return portable.Select(ToLspCompletionItem).ToList();
        }

        private static LspCompletionItem ToLspCompletionItem(PortableCompletionItem p)
        {
            return new LspCompletionItem
            {
                Label = p.Label,
                InsertText = p.InsertText,
                Kind = ToKind(p.Kind),
                Detail = p.Detail,
                Documentation = p.Documentation,
                SortText = p.SortText,
                FilterText = p.FilterText,
                InsertTextFormat = p.InsertTextFormat == PortableInsertTextFormat.Snippet
                    ? LspInsertTextFormat.Snippet
                    : LspInsertTextFormat.PlainText,
                TriggerParameterHints = p.TriggerParameterHints,
            };
        }

        private static LspCompletionKind ToKind(PortableCompletionKind kind)
        {
            switch (kind)
            {
                case PortableCompletionKind.Variable: return LspCompletionKind.Variable;
                case PortableCompletionKind.Function: return LspCompletionKind.Function;
                case PortableCompletionKind.Interface: return LspCompletionKind.Interface;
                case PortableCompletionKind.Keyword: return LspCompletionKind.Keyword;
                case PortableCompletionKind.Field: return LspCompletionKind.Field;
                case PortableCompletionKind.Class: return LspCompletionKind.Class;
                case PortableCompletionKind.Constant: return LspCompletionKind.Constant;
                case PortableCompletionKind.Reference: return LspCompletionKind.Reference;
                case PortableCompletionKind.Folder: return LspCompletionKind.Folder;
                default: return LspCompletionKind.Text;
            }
        }
    }
}
