// Go-to-definition: given a cursor on a reference, return the location of
// the AST node that declared the symbol the reference resolves to.
//
// Ported from FadeBasic/LSP/Handlers/GotoDefinitionHandler.cs.

using System;
using System.Collections.Generic;
using FadeBasic;
using FadeBasic.Ast;

namespace FadeBasic.LSP.Core.Handlers
{
    public static class DefinitionHandler
    {
        private static readonly HashSet<Type> AllowedTypes = new HashSet<Type>
        {
            typeof(VariableRefNode),
            typeof(ArrayIndexReference),
            typeof(GoSubStatement),
            typeof(GotoStatement),
            typeof(RuntoStatement),
        };

        public static LspLocation Compute(FadeDocument doc, int line, int character)
        {
            if (doc?.Program == null || doc.LexResults == null) return null;

            var token = ReferencesHandler.FindTokenAt(doc, line, character)
                        ?? ReferencesHandler.FindTokenAt(doc, line, character - 1);
            if (token == null) return null;

            bool Visit(IAstVisitable x)
            {
                if (!AllowedTypes.Contains(x.GetType())) return false;
                return x.StartToken == token || x.EndToken == token;
            }

            var node = doc.Program.FindFirst(Visit) as IAstNode;
            if (node == null) return null;

            IAstNode target = node;
            switch (node)
            {
                case ExpressionStatement exprStatement:
                    target = exprStatement.expression as IAstNode ?? node;
                    break;
            }

            if (target.DeclaredFromSymbol == null) return null;
            var origin = target.DeclaredFromSymbol.source;
            if (origin == null) return null;

            return new LspLocation
            {
                Uri = doc.Uri,
                Range = ReferencesHandler.TokenRangeOf(origin),
            };
        }
    }
}
