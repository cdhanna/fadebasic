// Transport-agnostic DTOs used by all LSP handlers in Core. Different LSP
// frontends (OmniSharp-based native server, browser FadeBridge) translate
// between these and their wire-protocol types.

using System.Collections.Generic;
using FadeBasic.Json;

namespace FadeBasic.LSP.Core
{
    // The diagnostic DTOs implement IJsonable so the browser LSP (FadeBridge)
    // can serialize them with FadeBasic.Json — reflection-free, unlike
    // System.Text.Json, whose reflection path was ~8ms/reparse in WASM (and
    // trim-fragile). Field names are the lowercase wire keys the Playground
    // consumes (d.range.start.line, d.severity, …). Serialization is write-only
    // here; the read side of ProcessJson is unused but kept symmetric.
    public class LspPosition : IJsonable
    {
        public int Line;       // 0-based
        public int Character;  // 0-based
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField("line", ref Line);
            op.IncludeField("character", ref Character);
        }
    }

    public class LspRange : IJsonable
    {
        public LspPosition Start;
        public LspPosition End;
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField("start", ref Start);
            op.IncludeField("end", ref End);
        }
    }

    public enum LspDiagnosticSeverity
    {
        Error = 1,
        Warning = 2,
        Information = 3,
        Hint = 4,
    }

    public class LspDiagnostic : IJsonable
    {
        public LspRange Range;
        public LspDiagnosticSeverity Severity;
        public string Code;
        public string Source;
        public string Message;
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField("range", ref Range);
            var sev = (int)Severity;
            op.IncludeField("severity", ref sev);
            Severity = (LspDiagnosticSeverity)sev;
            op.IncludeField("code", ref Code);
            op.IncludeField("source", ref Source);
            op.IncludeField("message", ref Message);
        }
    }

    public class LspHoverResult
    {
        public string Contents;  // Markdown
        public LspRange Range;
    }

    public enum LspCompletionKind
    {
        Text = 0,
        Variable = 1,
        Function = 2,
        Interface = 3,
        Keyword = 4,
        Field = 5,
        Class = 6,
        Constant = 7,
        Reference = 8,
        Folder = 9,
        Method = 10,
        Snippet = 11,
    }

    public enum LspInsertTextFormat
    {
        PlainText = 1,
        Snippet = 2,
    }

    public class LspCompletionItem
    {
        public string Label;
        public string InsertText;
        public LspCompletionKind Kind;
        public string Detail;
        public string Documentation;
        public string SortText;
        public string FilterText;
        public LspInsertTextFormat InsertTextFormat = LspInsertTextFormat.PlainText;
        public bool TriggerParameterHints;
    }

    public class LspSemanticTokens
    {
        // LSP-encoded delta-format: groups of 5 ints.
        public List<int> Data;
    }

    public class LspTextEdit
    {
        public LspRange Range;
        public string NewText;
    }

    public class LspWorkspaceEdit
    {
        // Per-URI list of edits.
        public Dictionary<string, List<LspTextEdit>> Changes;
    }

    // Matches the LSP SymbolKind enum subset we use.
    public enum LspSymbolKind
    {
        File = 1, Module = 2, Namespace = 3, Package = 4, Class = 5,
        Method = 6, Property = 7, Field = 8, Constructor = 9, Enum = 10,
        Interface = 11, Function = 12, Variable = 13, Constant = 14,
        String = 15, Number = 16, Boolean = 17, Array = 18, Object = 19,
        Key = 20, Null = 21, EnumMember = 22, Struct = 23, Event = 24,
        Operator = 25, TypeParameter = 26,
    }

    public class LspDocumentSymbol
    {
        public string Name;
        public string Detail;
        public LspSymbolKind Kind;
        public LspRange Range;          // full extent (body included)
        public LspRange SelectionRange; // just the name token
        public List<LspDocumentSymbol> Children;
    }

    public enum LspFoldingRangeKind
    {
        Region = 0,
        Comment = 1,
        Imports = 2,
    }

    public class LspFoldingRange
    {
        public int StartLine;
        public int EndLine;
        public int? StartCharacter;
        public int? EndCharacter;
        public LspFoldingRangeKind Kind;
    }
}
