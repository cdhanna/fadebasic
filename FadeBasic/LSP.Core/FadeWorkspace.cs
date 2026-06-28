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
            var program = parser.ParseProgram();

            // Resolves names, populates DeclaredFromSymbol on AST refs, and
            // fills program.scope.positionedVariables — all of which the
            // completion, references, and goto-def handlers depend on.
            // Without this the only errors we report are syntax-level.
            try
            {
                program.AddScopeRelatedErrors(ParseOptions.Default);
            }
            catch { /* visitor is best-effort; never fail SetDocument */ }

            // Attach trivia (doc-comment) strings to functions/declarations/
            // labels so the hover handler can render them as markdown.
            try
            {
                program.AddTrivia(lex);
            }
            catch { /* trivia is best-effort */ }

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
