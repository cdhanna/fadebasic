// Document outline: lists top-level functions, type definitions, declarations,
// and labels. Each function expands into its local labels.

using System.Collections.Generic;
using FadeBasic;
using FadeBasic.Ast;

namespace FadeBasic.LSP.Core.Handlers
{
    public static class DocumentSymbolHandler
    {
        public static List<LspDocumentSymbol> Compute(FadeDocument doc)
        {
            var result = new List<LspDocumentSymbol>();
            if (doc?.Program == null) return result;

            var prog = doc.Program;

            foreach (var typeDef in prog.typeDefinitions)
            {
                if (typeDef?.name == null) continue;
                result.Add(new LspDocumentSymbol
                {
                    Name = typeDef.name.variableName ?? "<type>",
                    Detail = "type",
                    Kind = LspSymbolKind.Struct,
                    Range = NodeRange(typeDef),
                    SelectionRange = TokenRange(typeDef.name?.StartToken ?? typeDef.StartToken),
                });
            }

            foreach (var label in prog.labels)
            {
                if (label?.label == null) continue;
                result.Add(new LspDocumentSymbol
                {
                    Name = label.label,
                    Detail = "label",
                    Kind = LspSymbolKind.Key,
                    Range = NodeRange(label),
                    SelectionRange = TokenRange(label.StartToken),
                });
            }

            // Top-level declarations only (variables shown as outline entries).
            foreach (var stmt in prog.statements)
            {
                if (stmt is DeclarationStatement decl && decl.variableNode != null)
                {
                    result.Add(new LspDocumentSymbol
                    {
                        Name = decl.variableNode.variableName ?? "<var>",
                        Detail = decl.type?.variableType.ToString() ?? "variable",
                        Kind = LspSymbolKind.Variable,
                        Range = NodeRange(decl),
                        SelectionRange = TokenRange(decl.variableNode.StartToken),
                    });
                }
            }

            foreach (var func in prog.functions)
            {
                if (func?.nameToken == null) continue;
                var children = new List<LspDocumentSymbol>();
                if (func.labels != null)
                {
                    foreach (var label in func.labels)
                    {
                        if (label?.label == null) continue;
                        children.Add(new LspDocumentSymbol
                        {
                            Name = label.label,
                            Detail = "label",
                            Kind = LspSymbolKind.Key,
                            Range = NodeRange(label),
                            SelectionRange = TokenRange(label.StartToken),
                        });
                    }
                }
                result.Add(new LspDocumentSymbol
                {
                    Name = func.name ?? func.nameToken.raw ?? "<fn>",
                    Detail = "function",
                    Kind = LspSymbolKind.Function,
                    Range = NodeRange(func),
                    SelectionRange = TokenRange(func.nameToken),
                    Children = children.Count > 0 ? children : null,
                });
            }

            return result;
        }

        private static LspRange NodeRange(IAstNode node)
        {
            var s = node.StartToken;
            var e = node.EndToken ?? s;
            var endChar = e.charNumber + (e.raw?.Length ?? e.Length);
            return new LspRange
            {
                Start = new LspPosition { Line = s.lineNumber, Character = s.charNumber },
                End = new LspPosition { Line = e.lineNumber, Character = endChar },
            };
        }

        private static LspRange TokenRange(Token t)
        {
            if (t == null) return new LspRange
            {
                Start = new LspPosition(), End = new LspPosition(),
            };
            var len = t.raw?.Length ?? t.Length;
            return new LspRange
            {
                Start = new LspPosition { Line = t.lineNumber, Character = t.charNumber },
                End = new LspPosition { Line = t.lineNumber, Character = t.charNumber + len },
            };
        }
    }
}
