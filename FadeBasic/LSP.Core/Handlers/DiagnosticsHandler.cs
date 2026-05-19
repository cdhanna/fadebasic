// Collect lex + parse errors from a FadeDocument as portable diagnostics.

using System.Collections.Generic;
using FadeBasic;
using FadeBasic.Ast;

namespace FadeBasic.LSP.Core.Handlers
{
    public static class DiagnosticsHandler
    {
        public static List<LspDiagnostic> Compute(FadeDocument doc)
        {
            var diagnostics = new List<LspDiagnostic>();
            if (doc == null) return diagnostics;

            // Lex errors and parse errors can describe the same root cause —
            // e.g. an unclosed string literal surfaces in both passes with
            // identical code/message/range. De-dup by signature so the UI
            // shows a single problem per (range, code, message) tuple.
            var seen = new HashSet<string>();
            string SigOf(LspDiagnostic d) =>
                $"{d.Code}|{d.Message}|{d.Range.Start.Line}:{d.Range.Start.Character}-{d.Range.End.Line}:{d.Range.End.Character}";

            void AddUnique(LspDiagnostic d)
            {
                if (seen.Add(SigOf(d))) diagnostics.Add(d);
            }

            if (doc.LexResults?.tokenErrors != null)
            {
                foreach (var err in doc.LexResults.tokenErrors)
                    AddUnique(MakeDiag(err));
            }

            if (doc.Program != null)
            {
                foreach (var err in doc.Program.GetAllErrors())
                    AddUnique(MakeDiag(err));
            }

            return diagnostics;
        }

        private static LspDiagnostic MakeDiag(ParseError err)
        {
            var startTok = err.location?.start;
            var endTok = err.location?.end ?? startTok;
            int startLine = startTok?.lineNumber ?? 0;
            int startChar = startTok?.charNumber ?? 0;
            int endLine = endTok?.lineNumber ?? startLine;
            int endChar = endTok != null
                ? endTok.charNumber + System.Math.Max(1, endTok.Length)
                : startChar + 1;
            return new LspDiagnostic
            {
                Severity = LspDiagnosticSeverity.Error,
                Range = new LspRange
                {
                    Start = new LspPosition { Line = startLine, Character = startChar },
                    End = new LspPosition { Line = endLine, Character = endChar },
                },
                Message = err.CombinedMessage,
                Code = err.errorCode.code.ToString(),
                Source = "fade",
            };
        }
    }
}
