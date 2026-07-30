// Per-document state held by the LSP. Each open file in the editor has one
// FadeDocument; the workspace owns the dictionary of them.

using System.Collections.Generic;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Ast.Visitors;
using FadeBasic.Virtual;

namespace FadeBasic.LSP.Core
{
    public class FadeDocument
    {
        public string Uri;
        public string Text;
        public LexerResults LexResults;
        public ProgramNode Program;
        public CommandCollection Commands;
        // Optional doc-lookup hook. Hosts that have command documentation
        // (native LSP via ProjectDocs, WebRuntime via embedded JSON) install
        // a provider so hover/completion can surface rich markdown.
        public ICommandDocsProvider Docs;

        public bool IsValid => LexResults != null;

        private bool _triviaComputed;
        // Trivia (leading doc-comment strings on AST nodes) is consumed ONLY by
        // the on-demand hover / completion / signature-help handlers. Computing
        // it walks the whole AST and builds a token→index map over every token
        // in the file — ~38% of the WASM reparse. So we no longer compute it
        // eagerly on every keystroke (SetDocument); handlers call EnsureTrivia
        // the first time they actually need it, and it's memoized. A fresh edit
        // creates a new FadeDocument, so the flag resets naturally.
        public void EnsureTrivia()
        {
            if (_triviaComputed) return;
            _triviaComputed = true;
            if (Program == null || LexResults == null) return;
            try { Program.AddTrivia(LexResults); }
            catch { /* trivia is best-effort — never fail a hover/completion over it */ }
        }
    }

    // A minimal contract for command documentation. Returns null if the
    // command is unknown.
    public interface ICommandDocsProvider
    {
        ICommandDocs Lookup(CommandInfo command);
    }

    // The slice of a command's documentation we actually render. Hosts map
    // their own doc types into this.
    public interface ICommandDocs
    {
        string Summary { get; }
        string Returns { get; }
        string Remarks { get; }
        IReadOnlyList<ICommandParameterDoc> Parameters { get; }
        IReadOnlyList<string> Examples { get; }
        // Optional canonical web URL for this command (e.g. docs site).
        string Url { get; }
    }

    public interface ICommandParameterDoc
    {
        string Name { get; }
        string Body { get; }
    }
}
