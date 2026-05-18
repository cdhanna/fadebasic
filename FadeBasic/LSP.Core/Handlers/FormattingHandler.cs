// Formatting handlers (full document, range, on-type). All three delegate
// to TokenFormatter.Format on the lexed tokens, then translate the
// formatter's edits into LSP TextEdits. Range and on-type just filter
// the full set down.

using System.Collections.Generic;
using FadeBasic;

namespace FadeBasic.LSP.Core.Handlers
{
    public enum LspCasingSetting
    {
        Ignore = 0,
        ToUpper = 1,
        ToLower = 2,
    }

    public class LspFormattingOptions
    {
        public int TabSize = 4;
        public bool InsertSpaces = true;
        public LspCasingSetting Casing = LspCasingSetting.Ignore;
    }

    public static class FormattingHandler
    {
        public static List<LspTextEdit> Compute(FadeDocument doc, LspFormattingOptions options)
        {
            var edits = new List<LspTextEdit>();
            if (doc?.LexResults?.combinedTokens == null) return edits;

            options ??= new LspFormattingOptions();

            var casing = options.Casing switch
            {
                LspCasingSetting.ToUpper => TokenFormatSettings.CasingSetting.ToUpper,
                LspCasingSetting.ToLower => TokenFormatSettings.CasingSetting.ToLower,
                _ => TokenFormatSettings.CasingSetting.Ignore,
            };

            var settings = new TokenFormatSettings
            {
                TabSize = options.TabSize,
                UseTabs = !options.InsertSpaces,
                Casing = casing,
            };

            var tokenEdits = TokenFormatter.Format(doc.LexResults.combinedTokens, settings);

            // LSP wants the same set; Monaco applies edits in any order safely.
            foreach (var e in tokenEdits)
            {
                edits.Add(new LspTextEdit
                {
                    Range = new LspRange
                    {
                        Start = new LspPosition { Line = e.startLine, Character = e.startChar },
                        End = new LspPosition { Line = e.endLine, Character = e.endChar },
                    },
                    NewText = e.replacement ?? string.Empty,
                });
            }

            return edits;
        }

        public static List<LspTextEdit> ComputeRange(FadeDocument doc, LspFormattingOptions options, LspRange range)
        {
            var all = Compute(doc, options);
            if (range == null) return all;

            var filtered = new List<LspTextEdit>();
            foreach (var e in all)
            {
                if (RangeIntersects(e.Range, range)) filtered.Add(e);
            }
            return filtered;
        }

        public static List<LspTextEdit> ComputeOnType(FadeDocument doc, LspFormattingOptions options, LspPosition position)
        {
            var all = Compute(doc, options);
            if (position == null) return all;

            // Native handler keeps edits within 1 line of the caret.
            var filtered = new List<LspTextEdit>();
            foreach (var e in all)
            {
                var lineDist = System.Math.Abs(e.Range.Start.Line - position.Line);
                if (lineDist < 2) filtered.Add(e);
            }
            return filtered;
        }

        private static bool RangeIntersects(LspRange a, LspRange b)
        {
            // Treat ranges as inclusive of start, exclusive of end for "touch".
            // Returns true if a and b share any character or touch at their ends.
            if (Before(a.End, b.Start)) return false;
            if (Before(b.End, a.Start)) return false;
            return true;
        }

        private static bool Before(LspPosition p, LspPosition q)
        {
            if (p.Line < q.Line) return true;
            if (p.Line > q.Line) return false;
            return p.Character < q.Character;
        }
    }
}
