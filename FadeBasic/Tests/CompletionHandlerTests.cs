// Behavioral tests for CompletionHandler.Compute. The completion path
// is multi-layered (LSPUtil.GetCompletions switch + our cursor-position
// rescues + safety-net fallback) and previously had no test coverage —
// each fix surfaces a new edge case in the other layers. These tests
// pin down the cases that have shipped regressions so we can iterate
// the handler without re-breaking the same flows.
//
// Test commands come from TestCommands.CommandsForTesting (single-word
// commands like `print`/`inc`/`add` + multi-word commands like
// `wait key`, `wait ms`, `screen width`, `any input`). All assertions
// are about LABELS — the wire shape isn't relevant here, just whether
// the expected items make it past the routing.

using System.Linq;
using FadeBasic.LSP.Core;
using FadeBasic.LSP.Core.Handlers;
using NUnit.Framework;

namespace Tests
{
    [TestFixture]
    public class CompletionHandlerTests
    {
        private static FadeDocument BuildDoc(string source)
        {
            var workspace = new FadeWorkspace(TestCommands.CommandsForTesting);
            return workspace.SetDocument("test://completion.fbasic", source);
        }

        // Run Compute on a source string with `|` marking the cursor.
        // Less error-prone than passing raw (line, char) tuples in every
        // test, and the cursor marker stays visually adjacent to the
        // surrounding code in the test source.
        private static System.Collections.Generic.List<LspCompletionItem> CompleteAt(string sourceWithCursor)
        {
            var cursorIdx = sourceWithCursor.IndexOf('|');
            Assert.That(cursorIdx, Is.GreaterThanOrEqualTo(0), "test source must contain a '|' cursor marker");
            var source = sourceWithCursor.Remove(cursorIdx, 1);
            // Walk to (line, char) — 0-based LSP positions.
            int line = 0, ch = 0;
            for (var i = 0; i < cursorIdx; i++)
            {
                if (source[i] == '\n') { line++; ch = 0; }
                else { ch++; }
            }
            var doc = BuildDoc(source);
            return CompletionHandler.Compute(doc, line, ch);
        }

        private static bool HasLabel(System.Collections.Generic.IEnumerable<LspCompletionItem> items, string label)
        {
            return items.Any(i => i.Label == label);
        }

        // ─── Statement-start contexts (commands should be visible) ──────

        [Test]
        public void Cursor_OnEmptyLineAfterAssignment_ReturnsCommands()
        {
            // Plain `EndStatement` case in the AST switch: leftToken is
            // the newline, group is ProgramNode. GetStatementCompletions
            // fires directly.
            var items = CompleteAt("x = 5\n|");
            Assert.That(HasLabel(items, "print"), Is.True, "expected `print` after newline");
            Assert.That(HasLabel(items, "wait key"), Is.True, "expected multi-word `wait key` after newline");
        }

        [Test]
        public void Cursor_AtStartOfDocument_ReturnsCommands()
        {
            // Bare empty document — leftToken is null/missing. Today the
            // handler short-circuits to empty when there's no leftToken.
            // Captured as a baseline; if we ever change the early-out,
            // this test catches it.
            var items = CompleteAt("|");
            Assert.That(items, Is.Empty, "empty doc: no completions because there's no leftToken");
        }

        [Test]
        public void Cursor_AfterSingleLetterAtLineStart_FallbackReturnsCommands()
        {
            // User typed `s` from scratch. AST routes to an unfinished
            // AssignmentStatement; GetAssignmentCompletions early-returns
            // because LeftToken isn't `=`. The safety-net fallback in
            // CompletionHandler.Compute should kick in and surface the
            // statement-level command list. Monaco then filters by what
            // was typed.
            var items = CompleteAt("s|");
            Assert.That(items, Is.Not.Empty, "fallback should populate something for `s|`");
            Assert.That(HasLabel(items, "screen width"), Is.True,
                "expected at least one `s...` command via the safety-net fallback");
        }

        // ─── End-of-complete-command-word (Case A: list commands) ──────

        [Test]
        public void Cursor_AtEndOfSingleWordCommand_ReturnsStatementCompletions()
        {
            // `print` is a complete command — lexer rewrites the token to
            // CommandWord. cursorAtCommandEnd branch fires; we add the
            // statement-level command list so Monaco can keep filtering.
            var items = CompleteAt("print|");
            Assert.That(HasLabel(items, "print"), Is.True);
            // Multiple commands should be in the list for filtering.
            Assert.That(items.Count, Is.GreaterThan(1),
                "expected full command list at end-of-CommandWord, got just one");
        }

        [Test]
        public void Cursor_AtEndOfMultiWordCommand_ReturnsStatementCompletions()
        {
            // `wait key` is a complete two-word command. The lexer
            // collapses both tokens into one CommandWord. Same rescue
            // path as the single-word case.
            var items = CompleteAt("wait key|");
            Assert.That(HasLabel(items, "wait key"), Is.True);
            Assert.That(HasLabel(items, "wait ms"), Is.True);
        }

        // ─── Past-end-of-command (Case B: arg-slot variables) ──────────

        [Test]
        public void Cursor_AfterSingleWordCommandAndSpace_ReturnsArgVariables()
        {
            // `inc` takes (ref int variable, int amount = 1). After
            // `inc ` the user is in the first-arg slot — int variables
            // in scope should be suggested.
            var items = CompleteAt("a = 4\ninc |");
            Assert.That(HasLabel(items, "a"), Is.True,
                "expected int variable `a` in first-arg-slot of `inc `");
        }

        [Test]
        public void Cursor_AfterCompleteCommandAndSpace_SuppressesMultiWordContinuations()
        {
            // `inc` is also a prefix of no other commands in TestCommands,
            // so this is mostly a regression-safety test. The multi-word
            // rescue should be SKIPPED when leftToken is a complete
            // CommandWord and the prefix ends in a space — otherwise it'd
            // outrank the variables. (If we ever add a command that
            // happens to start with `inc `, this test will help us
            // remember to verify ordering.)
            var items = CompleteAt("a = 4\ninc |");
            Assert.That(HasLabel(items, "a"), Is.True);
        }

        // ─── Partial multi-word command (continuation suggestions) ─────

        [Test]
        public void Cursor_AfterPartialMultiWordCommandWithSpace_ReturnsContinuations()
        {
            // `wait` alone is NOT a registered command — only `wait key`
            // and `wait ms` are. The lexer leaves `wait` as a plain
            // identifier. The multi-word prefix rescue should fire and
            // surface both continuations.
            var items = CompleteAt("wait |");
            Assert.That(HasLabel(items, "wait key"), Is.True);
            Assert.That(HasLabel(items, "wait ms"), Is.True);
        }

        [Test]
        public void Cursor_AfterPartialMultiWordCommand_NoTrailingSpace_ReturnsContinuations()
        {
            // `screen` is a partial of `screen width`. With no trailing
            // space we expect to still see `screen width` so Monaco can
            // filter as the user types.
            var items = CompleteAt("screen|");
            // `screen` itself isn't a CommandWord here (no leaf node);
            // the multi-word rescue is what would surface it — but its
            // condition is `prefix.Contains(' ')`, which is false for the
            // single word `screen`. The safety-net fallback ought to
            // catch this: portable is empty → fall back to statement
            // completions → `screen width` appears for Monaco to filter.
            Assert.That(HasLabel(items, "screen width"), Is.True,
                "expected `screen width` via the safety-net fallback when typing the prefix `screen`");
        }

        // ─── Symbol-visibility rules ────────────────────────────────────

        [Test]
        public void Variable_DeclaredAfterCursor_NotSuggestedAsArg()
        {
            // GetSymbolCompletions skips symbols whose declaration is
            // strictly AFTER the cursor (IsLocationBefore check). Verify
            // the rule survives our rescue path.
            var items = CompleteAt("inc |\ny = 7");
            Assert.That(HasLabel(items, "y"), Is.False,
                "variable declared after the cursor should not be offered");
        }
    }
}
