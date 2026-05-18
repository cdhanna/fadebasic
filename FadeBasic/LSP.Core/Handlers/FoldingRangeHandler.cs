// Folding ranges: AST-driven. Visits the program and emits a fold for every
// compound statement (function, if/then, for/next, while/endwhile,
// do/loop, repeat/until, type definitions, tests) that spans multiple
// lines.

using System.Collections.Generic;
using FadeBasic;
using FadeBasic.Ast;

namespace FadeBasic.LSP.Core.Handlers
{
    public static class FoldingRangeHandler
    {
        public static List<LspFoldingRange> Compute(FadeDocument doc)
        {
            var ranges = new List<LspFoldingRange>();
            if (doc?.Program == null) return ranges;

            doc.Program.Visit(node =>
            {
                if (node.StartToken == null || node.EndToken == null) return;
                if (node is ProgramNode) return;

                bool isFoldable = node is FunctionStatement
                    || node is IfStatement
                    || node is ForStatement
                    || node is WhileStatement
                    || node is DoLoopStatement
                    || node is RepeatUntilStatement
                    || node is TypeDefinitionStatement
                    || node is TestNode;
                if (!isFoldable) return;

                var startLine = node.StartToken.lineNumber;
                var endLine = node.EndToken.lineNumber;
                if (endLine <= startLine) return; // single-line; no fold

                ranges.Add(new LspFoldingRange
                {
                    StartLine = startLine,
                    EndLine = endLine,
                    StartCharacter = node.StartToken.charNumber,
                    EndCharacter = node.EndToken.charNumber + (node.EndToken.raw?.Length ?? node.EndToken.Length),
                    Kind = LspFoldingRangeKind.Region,
                });
            });

            // Multi-line comments fold too. The Lexer tags rem-block tokens
            // with LexemType.RemStart so we can detect them here.
            if (doc.LexResults?.combinedTokens != null)
            {
                foreach (var t in doc.LexResults.combinedTokens)
                {
                    if (t?.raw == null) continue;
                    if (t.type != LexemType.KeywordRemStart && t.type != LexemType.KeywordRem) continue;
                    var startLine = t.lineNumber;
                    // raw may span multiple lines; count them.
                    int endLine = startLine;
                    foreach (var c in t.raw)
                        if (c == '\n') endLine++;
                    if (endLine > startLine)
                    {
                        ranges.Add(new LspFoldingRange
                        {
                            StartLine = startLine,
                            EndLine = endLine,
                            Kind = LspFoldingRangeKind.Comment,
                        });
                    }
                }
            }

            return ranges;
        }
    }
}
