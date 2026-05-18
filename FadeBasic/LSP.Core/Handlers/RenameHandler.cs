// Rename: find the declaration node behind the cursor, then emit a text edit
// for every reference site (the declaration's name token plus every node
// whose DeclaredFromSymbol.source resolves back to the declaration).

using System;
using System.Collections.Generic;
using FadeBasic;
using FadeBasic.Ast;

namespace FadeBasic.LSP.Core.Handlers
{
    public static class RenameHandler
    {
        private static readonly HashSet<Type> AllowedTypes = new HashSet<Type>
        {
            typeof(VariableRefNode),
            typeof(ArrayIndexReference),
            typeof(GoSubStatement),
            typeof(GotoStatement),
            typeof(RuntoStatement),
            typeof(DeclarationStatement),
            typeof(ParameterNode),
            typeof(FunctionStatement),
            typeof(LabelDeclarationNode),
        };

        public static LspWorkspaceEdit Compute(FadeDocument doc, int line, int character, string newName)
        {
            if (doc?.Program == null) return null;
            if (string.IsNullOrEmpty(newName)) return null;

            var token = ReferencesHandler.FindTokenAt(doc, line, character)
                        ?? ReferencesHandler.FindTokenAt(doc, line, character - 1);
            if (token == null) return null;

            IAstNode declaration = null;
            void Visit(IAstVisitable x)
            {
                if (declaration != null) return;
                if (!AllowedTypes.Contains(x.GetType())) return;

                bool match = false;
                if (x is FunctionStatement fs)
                    match = x.StartToken == token || fs.nameToken == token
                            || Token.AreLocationsEqual(token, x.StartToken)
                            || Token.AreLocationsEqual(token, fs.nameToken);
                else
                    match = Token.AreLocationsEqual(token, x.StartToken)
                            || Token.AreLocationsEqual(token, x.EndToken);

                if (match) declaration = x;
            }
            doc.Program.Visit(Visit);
            if (declaration == null) return null;

            // Walk up to the declaration if we matched a reference.
            if (declaration.DeclaredFromSymbol?.source is IAstNode resolved)
                declaration = resolved;

            var edits = new List<LspTextEdit>();
            AddEdit(declaration, newName, edits);

            doc.Program.Visit(x =>
            {
                if (ReferenceEquals(x, declaration)) return;
                if (x.DeclaredFromSymbol?.source is IAstNode src)
                {
                    if (ReferenceEquals(src, declaration))
                    {
                        AddEdit((IAstNode)x, newName, edits);
                    }
                    else if (src is AssignmentStatement asn
                             && ReferenceEquals(asn.variable, declaration))
                    {
                        AddEdit((IAstNode)x, newName, edits);
                    }
                }
            });

            if (edits.Count == 0) return null;

            return new LspWorkspaceEdit
            {
                Changes = new Dictionary<string, List<LspTextEdit>>
                {
                    [doc.Uri] = edits,
                },
            };
        }

        private static Token GetNameToken(IAstNode node)
        {
            switch (node)
            {
                case FunctionStatement fs: return fs.nameToken;
                // Variable name lives at EndToken; StartToken is the GLOBAL/LOCAL/DIM keyword.
                case DeclarationStatement d: return d.EndToken;
                case ParameterNode p: return p.StartToken;
                default: return node.StartToken;
            }
        }

        private static void AddEdit(IAstNode node, string newName, List<LspTextEdit> edits)
        {
            var t = GetNameToken(node);
            if (t == null) return;
            var len = t.raw?.Length ?? t.Length;
            edits.Add(new LspTextEdit
            {
                Range = new LspRange
                {
                    Start = new LspPosition { Line = t.lineNumber, Character = t.charNumber },
                    End = new LspPosition { Line = t.lineNumber, Character = t.charNumber + len },
                },
                NewText = newName,
            });
        }
    }
}
