// Per-document state held by the LSP. Each open file in the editor has one
// FadeDocument; the workspace owns the dictionary of them.

using System.Collections.Generic;
using FadeBasic;
using FadeBasic.Ast;
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
