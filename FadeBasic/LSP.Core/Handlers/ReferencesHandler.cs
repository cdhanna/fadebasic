// References: given a cursor position, find all AST nodes that resolve to
// the same Symbol (i.e. all uses of the variable, function, label, etc.).
//
// Ported from FadeBasic/LSP/Handlers/FindReferencesHandler.cs but stripped
// of the source-map indirection — Core operates on a single FadeDocument.

using System.Collections.Generic;
using System.Linq;
using FadeBasic;
using FadeBasic.Ast;

namespace FadeBasic.LSP.Core.Handlers
{
    public class LspLocation
    {
        public string Uri;
        public LspRange Range;
    }

    public static class ReferencesHandler
    {
        public static List<LspLocation> Compute(FadeDocument doc, int line, int character)
        {
            if (doc?.Program == null || doc.LexResults == null) return null;

            // Find the token at this position. Try the cursor's own column,
            // then drift one to the left (hail-mary for cursors sitting in
            // immediate whitespace next to a token).
            var token = FindTokenAt(doc, line, character)
                        ?? FindTokenAt(doc, line, character - 1);
            if (token == null) return null;

            // Pass 1: collect every AST node that "starts/ends" at the
            // clicked token (using location-equality so declaration sites and
            // use sites both match) plus every node x where
            // x.DeclaredFromSymbol.source has a token at this position.
            var atToken = new List<IAstNode>();
            var sourceCandidates = new HashSet<IAstNode>();

            void Pass1(IAstVisitable x)
            {
                bool isMatch = false;
                if (x is VariableRefNode
                    or DeclarationStatement
                    or ArrayIndexReference
                    or LabelDeclarationNode
                    or GoSubStatement
                    or GotoStatement
                    or RuntoStatement)
                {
                    isMatch = Token.AreLocationsEqual(token, x.StartToken)
                              || Token.AreLocationsEqual(token, x.EndToken);
                }
                else if (x is FunctionStatement funcStatement)
                {
                    isMatch = x.StartToken == token || funcStatement.nameToken == token;
                }
                if (isMatch) atToken.Add(x);
            }
            doc.Program.Visit(Pass1);

            if (atToken.Count == 0) return new List<LspLocation>();

            // For each match, the "source" node is either the resolved
            // DeclaredFromSymbol.source (clicked on a use) or the node itself
            // (clicked on the declaration). Both get added to the candidate
            // set so we union the uses of every possible interpretation.
            foreach (var node in atToken)
            {
                sourceCandidates.Add(node);
                if (node.DeclaredFromSymbol?.source is IAstNode src)
                    sourceCandidates.Add(src);
            }

            // Also collect every distinct "source" node referenced anywhere
            // in the program whose StartToken sits at the same location as
            // our clicked token. This catches the case where the user clicks
            // on the declaration site (e.g. the LHS of `x = 1`) but the
            // implicit symbol's source is a different AST node (the
            // surrounding AssignmentStatement). Matching by token location
            // unions the two interpretations.
            doc.Program.Visit(x =>
            {
                if (x.DeclaredFromSymbol?.source is IAstNode src
                    && src.StartToken != null
                    && Token.AreLocationsEqual(src.StartToken, token))
                {
                    sourceCandidates.Add(src);
                }
            });

            // Pass 2: every node whose DeclaredFromSymbol.source is in the
            // candidate set is a reference. Source nodes themselves count.
            var discovered = new HashSet<IAstNode>(sourceCandidates);
            doc.Program.Visit(x =>
            {
                if (x.DeclaredFromSymbol?.source is IAstNode src && sourceCandidates.Contains(src))
                    discovered.Add(x);
            });

            return discovered.Select(n => new LspLocation
            {
                Uri = doc.Uri,
                Range = TokenRangeOf(n),
            }).ToList();
        }

        internal static Token FindTokenAt(FadeDocument doc, int line, int character)
        {
            if (character < 0) return null;
            foreach (var t in doc.LexResults.allTokens)
            {
                if (t.raw == null && t.caseInsensitiveRaw == null) continue;
                if (t.lineNumber != line) continue;
                if (character < t.charNumber) continue;
                if (character > t.charNumber + t.Length) continue;
                return t;
            }
            return null;
        }

        internal static LspRange TokenRangeOf(IAstNode node)
        {
            var s = node.StartToken;
            var e = node.EndToken ?? s;
            var endChar = e.charNumber + (e.raw?.Length ?? e.Length);
            return new LspRange
            {
                Start = new LspPosition { Line = s.lineNumber, Character = s.charNumber },
                End = new LspPosition { Line = s.lineNumber, Character = endChar },
            };
        }
    }
}
