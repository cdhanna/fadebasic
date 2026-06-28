// Helpers that turn the native LSP's per-request CodeUnit into a Core
// FadeDocument so handlers can delegate to FadeBasic.LSP.Core.Handlers.
//
// The native LSP is project-aware — its CodeUnit may span multiple source
// files concatenated via SourceMap, with macros expanded into a parallel
// `macroProgram`. When all of that lives in a single source file (the
// common case), the source map is identity and the conversion is direct.
// Multi-file projects are handled by the existing native logic that maps
// positions through `unit.sourceMap` before / after calling Core.

using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.LSP.Core;
using FadeBasic.Virtual;
using ApplicationSupport.Code;

namespace LSP.Handlers;

internal static class CoreAdapter
{
    // Wrap a CodeUnit + URI as a FadeDocument suitable for Core handlers.
    // Docs (when available) are surfaced so command hover renders rich
    // markdown — same source the native HoverHandler uses, just behind
    // the Core ICommandDocsProvider interface.
    public static FadeDocument ToDocument(CodeUnit unit, string uri, ProjectDocs? docs = null)
    {
        return new FadeDocument
        {
            Uri = uri,
            // We don't bother re-reconstructing the source text here; Core
            // handlers operate on the parsed AST + lex results, not raw text.
            Text = string.Empty,
            LexResults = unit.lexerResults,
            Program = unit.program,
            Commands = unit.commands,
            Docs = docs == null ? null : new ProjectDocsCommandDocsProvider(docs),
        };
    }
}
