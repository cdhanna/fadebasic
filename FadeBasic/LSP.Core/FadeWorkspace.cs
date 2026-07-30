// Collection of FadeDocuments. Owns the lexer/parser path. Frontends call
// SetDocument when a file is opened or changes, then ask handlers for results.

using System.Collections.Generic;
using FadeBasic;
using FadeBasic.Ast.Visitors;

namespace FadeBasic.LSP.Core
{
    public class FadeWorkspace
    {
        private readonly Dictionary<string, FadeDocument> _docs = new();
        private readonly Lexer _lexer = new();

        public CommandCollection Commands { get; set; }
        // Optional docs provider — when set, every SetDocument call attaches
        // it to the resulting FadeDocument so handlers can render rich
        // command markdown.
        public ICommandDocsProvider Docs { get; set; }

        public FadeWorkspace(CommandCollection commands = null)
        {
            Commands = commands ?? new CommandCollection();
        }

        public FadeDocument SetDocument(string uri, string text)
        {
            var lex = _lexer.TokenizeWithErrors(text, Commands);
            var parser = new Parser(lex.stream, Commands);
            // ParseProgram already runs AddScopeRelatedErrors (Parser.cs, via the
            // default ParseOptions) — it resolves names, populates
            // DeclaredFromSymbol on AST refs, and fills
            // program.scope.positionedVariables, which the completion,
            // references, and goto-def handlers depend on. We used to call
            // AddScopeRelatedErrors AGAIN here with the same options, running the
            // whole scope-resolution visitor a second time per reparse (~13% of
            // the reparse and duplicate diagnostics). ParseProgram covers it.
            var program = parser.ParseProgram();

            // Trivia (doc-comment strings for hover/completion/signature-help) is
            // computed LAZILY now — see FadeDocument.EnsureTrivia. Attaching it
            // here walked the whole AST on every keystroke (~38% of the WASM
            // reparse) for data only the on-demand handlers ever read.

            var doc = new FadeDocument
            {
                Uri = uri,
                Text = text,
                LexResults = lex,
                Program = program,
                Commands = Commands,
                Docs = Docs,
            };
            _docs[uri] = doc;
            return doc;
        }

        public FadeDocument Get(string uri)
        {
            _docs.TryGetValue(uri, out var d);
            return d;
        }

        public bool Remove(string uri) => _docs.Remove(uri);

        public IEnumerable<FadeDocument> AllDocuments => _docs.Values;
    }
}
