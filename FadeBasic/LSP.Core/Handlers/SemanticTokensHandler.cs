// Walk the document's tokens and produce LSP-encoded delta semantic tokens.
// Token classification reuses FadeBasic.Lsp.LSPUtil.ClassifyToken.

using System.Collections.Generic;
using FadeBasic;
using FadeBasic.Lsp;

namespace FadeBasic.LSP.Core.Handlers
{
    public static class SemanticTokensHandler
    {
        // Index in this array becomes the token type integer emitted in the
        // encoded tokens stream. Frontends register a matching legend.
        public static readonly string[] Legend = new[]
        {
            "comment",   // 0
            "keyword",   // 1
            "function",  // 2
            "method",    // 3
            "macro",     // 4
            "parameter", // 5
            "struct",    // 6
            "type",      // 7
            "operator",  // 8
            "number",    // 9
            "string",    // 10
        };

        // Raw per-token classification. Frontends that need to filter or
        // remap tokens (e.g. the native LSP applying source-map per-token)
        // call this and build their own output; frontends that want the
        // canonical LSP delta-encoded stream call Compute() below.
        public readonly struct ClassifiedToken
        {
            public readonly Token Token;
            public readonly PortableSemanticTokenType Type;
            public ClassifiedToken(Token token, PortableSemanticTokenType type)
            {
                Token = token; Type = type;
            }
        }

        public static List<ClassifiedToken> Classify(FadeDocument doc)
        {
            var classified = new List<ClassifiedToken>();
            if (doc?.LexResults == null) return classified;
            var tokens = doc.LexResults.allTokens;
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.raw == null) continue;
                var prev = i > 0 ? tokens[i - 1] : null;
                var result = LSPUtil.ClassifyToken(token, prev);
                if (result.Skip) continue;
                classified.Add(new ClassifiedToken(token, result.TokenType));
            }
            return classified;
        }

        // Map our token-type enum into the legend index emitted on the wire.
        public static int LegendIndex(PortableSemanticTokenType t) => ToLegendIndex(t);

        public static List<int> Compute(FadeDocument doc)
            => Compute(doc, 0, int.MaxValue);

        // Range-scoped delta-encoded tokens: only tokens whose (0-based) line is
        // in [startLine, endLine) are emitted.
        //
        // Crucially, classification (LSPUtil.ClassifyToken does per-token command
        // lookups — the dominant cost) runs ONLY for in-range tokens, not the
        // whole document. On a large multi-file joined project a viewport request
        // therefore costs O(range) rather than O(all tokens); classifying every
        // token just to discard all but ~50 lines' worth was the real bottleneck
        // (~1.4s/keystroke on killcode), not serialization. Tokens are emitted in
        // source order, so we skip ahead to startLine and break past endLine.
        //
        // We still read the true immediately-preceding token as ClassifyToken's
        // `prev` context, so an in-range token classifies identically to the full
        // walk. The delta encoding re-bases naturally: prevLine starts at 0, so
        // the first in-range token's deltaLine is its absolute line and a decoder
        // accumulating from 0 lands on the correct line.
        public static List<int> Compute(FadeDocument doc, int startLine, int endLine)
        {
            var data = new List<int>();
            if (doc?.LexResults == null) return data;
            var tokens = doc.LexResults.allTokens;
            int prevLine = 0;
            int prevChar = 0;

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.raw == null) continue;
                int line = token.lineNumber;
                if (line < startLine) continue;   // not visible yet — don't classify
                if (line >= endLine) break;        // past the window (source-ordered)

                var prev = i > 0 ? tokens[i - 1] : null;
                var result = LSPUtil.ClassifyToken(token, prev);
                if (result.Skip) continue;

                int ch = token.charNumber;
                int deltaLine = line - prevLine;
                int deltaChar = deltaLine == 0 ? ch - prevChar : ch;

                data.Add(deltaLine);
                data.Add(deltaChar);
                data.Add(token.Length);
                data.Add(ToLegendIndex(result.TokenType));
                data.Add(0); // no modifiers

                prevLine = line;
                prevChar = ch;
            }
            return data;
        }

        private static int ToLegendIndex(PortableSemanticTokenType t)
        {
            switch (t)
            {
                case PortableSemanticTokenType.Comment: return 0;
                case PortableSemanticTokenType.Keyword: return 1;
                case PortableSemanticTokenType.Function: return 2;
                case PortableSemanticTokenType.Method: return 3;
                case PortableSemanticTokenType.Macro: return 4;
                case PortableSemanticTokenType.Parameter: return 5;
                case PortableSemanticTokenType.Struct: return 6;
                case PortableSemanticTokenType.Type: return 7;
                case PortableSemanticTokenType.Operator: return 8;
                case PortableSemanticTokenType.Number: return 9;
                case PortableSemanticTokenType.String: return 10;
                default: return 0;
            }
        }
    }
}
