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
using FadeBasic.Ast;
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
        public void TypeNameCompletions_PreserveDeclaredCasing()
        {
            // `type fTest` is stored under the lowercased key `ftest`, but the
            // `as <type>` completion must show the declared casing `fTest`.
            var items = CompleteAt("type fTest\nendtype\nff as |");
            Assert.That(HasLabel(items, "fTest"), Is.True, "type completion should preserve declared casing");
            Assert.That(HasLabel(items, "ftest"), Is.False, "should not offer the lowercased key");
        }

        [Test]
        public void CommandCompletions_OverloadedCommand_ListedOnce()
        {
            // `ovrbump` has two overloads (ref-int and ref-float). They all
            // insert the same text, so the completion list must show the name
            // once, not once per overload.
            var items = CompleteAt("x = 5\n|");
            var count = items.Count(i => i.Label == "ovrbump");
            Assert.That(count, Is.EqualTo(1), "an overloaded command should appear once in completions");
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

        // ─── Variable visibility across scopes ──────────────────────────
        // fbasic functions have isolated scopes — top-level assignments
        // (`x = 5` at file scope) are top-level locals and are NOT visible
        // inside function bodies. Only explicit `global x` declarations
        // cross the boundary. These tests pin the visibility rules that
        // already work correctly so future LSP refactors can't regress
        // them silently.

        [Test]
        public void Variable_FunctionParameter_VisibleInFunctionBody()
        {
            // Parameter symbols should appear in completions inside their
            // function body — the most basic visibility rule.
            var src =
                "function add(amount as integer)\n" +
                "  inc |\n" +
                "endfunction";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "amount"), Is.True,
                "function parameter `amount` should be visible in its body");
        }

        [Test]
        public void Variable_DeclaredInsideIfBlock_VisibleAfterIf()
        {
            // fbasic doesn't have block-scoped locals — a variable
            // assigned inside an if/while/for is visible after the block
            // ends in the same scope. Make sure GetSymbolCompletions
            // sees variables introduced inside nested control-flow.
            var src =
                "if 1 = 1\n" +
                "  flag = 7\n" +
                "endif\n" +
                "inc |";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "flag"), Is.True,
                "variable assigned inside an if-block should remain visible after the endif");
        }

        [Test]
        public void Variable_DeclaredInsideForLoop_VisibleAfter()
        {
            var src =
                "for i = 1 to 10\n" +
                "  total = total + i\n" +
                "next i\n" +
                "inc |";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "i"), Is.True,
                "loop variable `i` should be visible after the loop");
            Assert.That(HasLabel(items, "total"), Is.True,
                "loop-body assignment `total` should be visible after the loop");
        }

        [Test]
        public void Variable_DeclaredInsideWhileLoop_Visible()
        {
            var src =
                "while count < 10\n" +
                "  count = count + 1\n" +
                "  step_amount = 5\n" +
                "  inc |\n" +
                "endwhile";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "count"), Is.True,
                "loop counter assignable in body should be visible mid-body");
            Assert.That(HasLabel(items, "step_amount"), Is.True,
                "any body-local should be visible mid-body");
        }

        // ─── Comments (REM lines + ` ticks) shouldn't surface anything ───

        [Test]
        public void Cursor_InsideRemComment_ReturnsNoCompletions()
        {
            // User typing inside a `rem` line is in a comment. The lexer
            // collapses the whole `rem ...` tail into a single KeywordRem
            // token, so leftToken.type == KeywordRem at any cursor position
            // past `rem `. We should suppress completions there entirely.
            var items = CompleteAt("rem this is a comment|");
            Assert.That(items, Is.Empty,
                "no completions should appear inside a REM comment");
        }

        [Test]
        public void Cursor_InsideRemComment_AfterStatement_ReturnsNoCompletions()
        {
            // Same rule applies when the REM follows other content on a
            // previous line. The cursor is still inside the comment;
            // the previous statement context shouldn't leak.
            var items = CompleteAt("x = 5\nrem note about x|");
            Assert.That(items, Is.Empty,
                "REM-line completions stay suppressed after a prior statement");
        }

        // ─── Case-insensitive symbol lookup (fbasic is case-insensitive) ──

        [Test]
        public void Variable_DeclaredByAssignment_LabelPreservesCase()
        {
            // fbasic is case-insensitive at the lexer (variables are
            // canonicalized to lowercase internally) but the user's
            // spelling is what should appear in the dropdown.
            var items = CompleteAt("BallPos = 5\ninc |");
            var ball = items.FirstOrDefault(i => string.Equals(i.Label, "BallPos", System.StringComparison.OrdinalIgnoreCase));
            Assert.That(ball, Is.Not.Null, "BallPos variable should be offered");
            Assert.That(ball.Label, Is.EqualTo("BallPos"),
                "assignment-declared label should preserve user case");
        }

        [Test]
        public void Variable_DeclaredByDim_LabelPreservesCase()
        {
            // `dim` declares a typed variable. Symbol's source is a
            // DeclarationStatement; the label should come from its
            // VariableNameCaseSensitive, not the lowercased key.
            var items = CompleteAt("dim Score as integer\ninc |");
            var s = items.FirstOrDefault(i => string.Equals(i.Label, "Score", System.StringComparison.OrdinalIgnoreCase));
            Assert.That(s, Is.Not.Null, "Score variable should be offered");
            Assert.That(s.Label, Is.EqualTo("Score"),
                "dim-declared label should preserve user case");
        }

        [Test]
        public void Variable_DeclaredAsFunctionParameter_LabelPreservesCase()
        {
            // Function parameters land in a different symbol-source path
            // (ParameterNode). Verify case preservation there too — the
            // GetSymbolCompletions switch has a separate branch for it.
            var items = CompleteAt(
                "function add(LeftSide as integer, RightSide as integer)\n" +
                "  inc |\n" +
                "endfunction"
            );
            var left = items.FirstOrDefault(i => string.Equals(i.Label, "LeftSide", System.StringComparison.OrdinalIgnoreCase));
            Assert.That(left, Is.Not.Null, "LeftSide param should be offered");
            Assert.That(left.Label, Is.EqualTo("LeftSide"),
                "parameter label should preserve user case");
        }

        // ─── Struct field completion (`ballPos.` → `x`, `y`) ──────────────

        [Test]
        public void Cursor_AfterStructDotAccess_ReturnsFieldCompletions()
        {
            // After typing `ballPos.` the user expects field completions
            // for the struct type — `x`, `y`. This is handled by
            // GetStructCompletions when the AST node at the cursor is a
            // StructFieldReference, but in practice typing `.` mid-edit
            // often leaves the parser in an error state where the node
            // shape doesn't match. CompletionHandler needs to detect the
            // dot-trigger and route through struct completions either way.
            //
            // fbasic struct variables are declared with `local`/`global
            // <name> as <Type>`; `dim` is for array declarations.
            var src =
                "type Vec2\n" +
                "  x as integer\n" +
                "  y as integer\n" +
                "endtype\n" +
                "local ballPos as Vec2\n" +
                "ballPos.|";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "x"), Is.True,
                "expected struct field `x` after `ballPos.`");
            Assert.That(HasLabel(items, "y"), Is.True,
                "expected struct field `y` after `ballPos.`");
        }

        [Test]
        public void Cursor_AfterStructDotAccess_AssignmentLHS_ReturnsFieldCompletions()
        {
            // Same trigger but on the LHS of an assignment (where the user
            // is targeting the field). Parser puts this through an
            // AssignmentStatement path that historically returned the
            // wrong completion set.
            var src =
                "type Vec2\n" +
                "  x as integer\n" +
                "  y as integer\n" +
                "endtype\n" +
                "local ballPos as Vec2\n" +
                "ballPos.| = 5";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "x"), Is.True, "expected struct field `x` on LHS dot");
            Assert.That(HasLabel(items, "y"), Is.True, "expected struct field `y` on LHS dot");
        }

        [Test]
        public void Cursor_InsideBacktickComment_ReturnsNoCompletions()
        {
            // `... is the alternate single-line comment syntax. The lexer
            // emits the same KeywordRem token type for both, so the same
            // suppression rule covers it.
            var items = CompleteAt("` this is a comment|");
            Assert.That(items, Is.Empty,
                "no completions should appear inside a backtick comment");
        }

        // ─── IF-condition variable visibility ──────────────────────────────
        //
        // User-reported bug: inside `if <cursor>`, struct-typed locals don't
        // appear in the completion list while primitive ones (sometimes) do.
        // The AST switch has no case for "ProgramNode/IfStatement with
        // leftToken=KeywordIf", so GetCompletions returns empty and the
        // safety-net only loads commands + functions — no symbols at all.
        // Variables of any type should be offered as completion candidates
        // inside an if condition (the type system permits arbitrary
        // expressions to coerce to truthy/falsy in fbasic).

        [Test]
        public void Cursor_AfterIfKeyword_ShowsPrimitiveVariables()
        {
            var src =
                "local count as integer = 5\n" +
                "if |";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "count"), Is.True,
                "primitive local should be visible as a candidate inside `if`");
        }

        [Test]
        public void Cursor_AfterIfKeyword_ShowsStructVariables()
        {
            // The reported bug: struct-typed locals are hidden after `if `.
            var src =
                "type Vec2\n" +
                "  x as integer\n" +
                "  y as integer\n" +
                "endtype\n" +
                "local ballPos as Vec2\n" +
                "if |";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "ballPos"), Is.True,
                "struct local should be visible as a candidate inside `if`");
        }

        [Test]
        public void Cursor_AfterIfStructDot_LowercaseTypeName_ShowsFieldCompletions()
        {
            // Same as the test below but with a lowercase type name —
            // the user's example uses `vec2`, not `Vec2`. fbasic
            // identifiers are nominally case-insensitive but the LSP
            // type-table key might be case-sensitive somewhere; pin it.
            var src =
                "type vec2\n" +
                "  x as integer\n" +
                "  y as integer\n" +
                "endtype\n" +
                "local ballPos as vec2\n" +
                "if ballPos.|";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "x"), Is.True,
                "expected struct field `x` for lowercase type `vec2`");
        }

        [Test]
        public void Cursor_AfterIfStructDot_InsideFunction_ShowsFieldCompletions()
        {
            // Whole flow inside a function — local scope routing matters
            // for the rescue's identifier lookup.
            var src =
                "type Vec2\n" +
                "  x as integer\n" +
                "  y as integer\n" +
                "endtype\n" +
                "function update()\n" +
                "  local ballPos as Vec2\n" +
                "  if ballPos.|\n" +
                "  endif\n" +
                "endfunction";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "x"), Is.True,
                "expected struct field `x` inside function scope");
        }

        [Test]
        public void Cursor_AfterIfStructDot_ShowsFieldCompletions()
        {
            // The user-reported follow-up: `if ballPos.█` should show
            // struct fields `x`/`y` (the dot rescue path), not commands
            // and not Monaco's word-based fallback.
            var src =
                "type Vec2\n" +
                "  x as integer\n" +
                "  y as integer\n" +
                "endtype\n" +
                "local ballPos as Vec2\n" +
                "if ballPos.|";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "x"), Is.True,
                "expected struct field `x` after `if ballPos.`");
            Assert.That(HasLabel(items, "y"), Is.True,
                "expected struct field `y` after `if ballPos.`");
        }

        // ─── Bug 2: function names inside a for-loop body ─────────────────
        [Test]
        public void Cursor_InExpression_TopLevel_ShowsFunctionName()
        {
            // Baseline: a value-returning user function should be offered in
            // an expression position at top level.
            var src =
                "function computeThing()\n" +
                "endfunction 5\n" +
                "result = |";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "computeThing"), Is.True,
                "user function should be offered in a top-level expression");
        }

        [Test]
        public void Cursor_InExpression_InsideForLoop_ShowsFunctionName()
        {
            // Bug: inside a for-loop body the completion list comes back
            // empty even though it works at top level.
            var src =
                "function computeThing()\n" +
                "endfunction 5\n" +
                "for i = 1 to 10\n" +
                "  result = |\n" +
                "next i";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "computeThing"), Is.True,
                "user function should be offered inside a for-loop body");
        }

        [Test]
        public void Cursor_InExpression_InsideForLoop_ShowsVariables()
        {
            // Broader form of the same bug: NO completions of any kind fire
            // inside a for-loop body expression.
            var src =
                "score = 10\n" +
                "for i = 1 to 10\n" +
                "  result = |\n" +
                "next i";
            var items = CompleteAt(src);
            Assert.That(items, Is.Not.Empty,
                "expression inside a for-loop body should produce completions");
            Assert.That(HasLabel(items, "score"), Is.True,
                "variable `score` should be offered inside a for-loop body");
        }

        [Test]
        public void Cursor_InExpression_InsideNestedForLoop_ShowsFunctionName()
        {
            var src =
                "function computeThing()\n" +
                "endfunction 5\n" +
                "for i = 1 to 10\n" +
                "  for j = 1 to 10\n" +
                "    result = |\n" +
                "  next j\n" +
                "next i";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "computeThing"), Is.True,
                "user function should be offered inside a nested for-loop body");
        }

        // ─── Bug 4: struct field completion on an array element ───────────
        [Test]
        public void Cursor_AfterArrayElementDot_ReturnsFieldCompletions()
        {
            // `dim arr(n) as Struct` then `arr(0).` should surface the
            // struct's fields — same as `structVar.` but the LHS of the
            // dot is an array-index expression `arr(0)`, whose last token
            // is `)`, not an identifier.
            var src =
                "type Vec2\n" +
                "  x as integer\n" +
                "  y as integer\n" +
                "endtype\n" +
                "dim boxes(10) as Vec2\n" +
                "boxes(0).|";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "x"), Is.True,
                "expected struct field `x` after `boxes(0).`");
            Assert.That(HasLabel(items, "y"), Is.True,
                "expected struct field `y` after `boxes(0).`");
        }

        // ─── Bug 6: completion in the second operand of `+` ───────────────
        [Test]
        public void Cursor_AfterArithmeticPlus_ShowsVariables()
        {
            // `y = x + |` — the cursor is in the right-hand operand of a
            // binary `+`. Variables/functions in scope should be offered.
            var src =
                "x = 5\n" +
                "score = 10\n" +
                "y = x + |";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "score"), Is.True,
                "expected variable `score` as the second operand of `+`");
        }

        // ─── Bug 2: value-returning functions in statement position ──────
        // A value-returning function can be called as a bare statement in
        // fbasic (its result discarded), so it must appear on a fresh
        // statement line — the user first noticed the omission inside a
        // for-loop body. GetStatementCompletions used to hard-filter to
        // TypeInfo.Void, which dropped every non-void function/command.
        [Test]
        public void Cursor_FreshLine_TopLevel_ShowsValueFunction()
        {
            var items = CompleteAt("function calc()\nendfunction 5\n|");
            Assert.That(HasLabel(items, "calc"), Is.True,
                "value-returning function should appear on a fresh top-level line");
        }

        [Test]
        public void Cursor_FreshLine_InsideForLoop_ShowsValueFunction()
        {
            var src =
                "function calc()\n" +
                "endfunction 5\n" +
                "for i = 1 to 10\n" +
                "  |\n" +
                "next i";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "calc"), Is.True,
                "value-returning function should appear on a fresh line inside a for-loop");
        }

        [Test]
        public void Cursor_FreshLine_InsideForLoop_StillShowsVoidFunction()
        {
            // Regression guard: void functions must keep showing too.
            var src =
                "function doIt()\n" +
                "endfunction\n" +
                "for i = 1 to 10\n" +
                "  |\n" +
                "next i";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "doIt"), Is.True,
                "void function should still appear on a fresh line inside a for-loop");
        }

        // ─── Bug 6: variables in a partially-typed operand ───────────────
        [Test]
        public void Cursor_AfterArithmeticPlus_PartialOperand_ShowsVariables()
        {
            // `total = total + sc|` — a partial identifier as the second
            // operand of `+`. The incomplete operand drops every statement
            // node out of the cursor's span (Group is just ProgramNode), so
            // the switch returned empty and the fallback offered only
            // commands/functions. In-scope variables must appear too.
            var src =
                "total = 0\n" +
                "score = 5\n" +
                "total = total + sc|";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "score"), Is.True,
                "variable `score` should appear when typing a partial `+` operand");
        }

        [Test]
        public void Cursor_AfterIfPartialIdent_ShowsStructVariables()
        {
            // After typing a partial identifier — same problem because
            // Visit() excludes the just-typed VariableRefNode from Group
            // (cursor is past its single-token span).
            var src =
                "type Vec2\n" +
                "  x as integer\n" +
                "  y as integer\n" +
                "endtype\n" +
                "local ballPos as Vec2\n" +
                "if ball|";
            var items = CompleteAt(src);
            Assert.That(HasLabel(items, "ballPos"), Is.True,
                "struct local should be visible mid-typing of an `if` condition");
        }
    }
}
