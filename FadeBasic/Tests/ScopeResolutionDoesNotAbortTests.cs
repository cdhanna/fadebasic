// Regression tests for the class of bug where the scope-resolution pass
// (AddScopeRelatedErrors) THREW NotImplementedException on constructs it
// didn't handle. Because the throw propagated uncaught out of ParseProgram /
// SetDocument, a single bad node aborted analysis for the ENTIRE file — every
// variable after it silently vanished from diagnostics AND completions (this
// is what hid cross-file variables like killcode's `backgroundColor`).
//
// Each throw is now a recorded diagnostic. These tests pin that: the parse
// must not throw, the offending construct must surface an error, and — the
// core invariant — variables declared AFTER the bad construct must still
// resolve (i.e. resolution did not abort).

using System.Collections.Generic;
using System.Linq;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.LSP.Core;
using FadeBasic.LSP.Core.Handlers;
using NUnit.Framework;

namespace Tests
{
    [TestFixture]
    public class ScopeResolutionDoesNotAbortTests
    {
        static FadeDocument Build(string src)
        {
            var ws = new FadeWorkspace(TestCommands.CommandsForTesting);
            return ws.SetDocument("test://scope.fbasic", src);
        }

        static List<ParseError> AllErrors(FadeDocument doc)
        {
            var errs = new List<ParseError>();
            doc.Program.Visit(n => { if (n is IAstNode a && a.Errors != null) errs.AddRange(a.Errors); });
            return errs;
        }

        static bool HasCode(IEnumerable<ParseError> errs, ErrorCode code) => errs.Any(e => e.errorCode.code == code.code);

        // Complete on a fresh trailing line; returns the completion labels.
        static List<string> CompletionLabelsAfter(string src)
        {
            var full = src + "\nprobeXYZ = ";
            var doc = Build(full);
            int line = full.Count(c => c == '\n');
            int ch = "probeXYZ = ".Length;
            return CompletionHandler.Compute(doc, line, ch).Select(i => i.Label).ToList();
        }

        static void Dump(string label, string src)
        {
            var doc = Build(src);
            var errs = AllErrors(doc);
            TestContext.WriteLine($"[{label}] errors: " + string.Join("; ",
                errs.Select(e => e.errorCode.code + (string.IsNullOrEmpty(e.message) ? "" : " " + e.message))));
        }

        // ── The core invariant: a throw-prone construct must not abort the
        //    pass. `afterVar` (declared later) must still show in completions. ──

        [Test]
        public void ArrayRefToUnknownVariable_DoesNotAbort()
        {
            // `ghost(5)` — ghost isn't a command or a declared array, so it's an
            // array-index reference to a not-in-scope variable (the killcode
            // trigger). Used to throw at EnsureArrayReferenceIsValid.
            const string src = "badRef = ghost(5)\nafterVar = 10";
            FadeDocument doc = null;
            Assert.DoesNotThrow(() => doc = Build(src));
            var errs = AllErrors(doc);
            Dump("array-ref-unknown", src);
            Assert.That(HasCode(errs, ErrorCodes.InvalidReference), Is.True, "the bad array ref should be diagnosed");
            Assert.That(CompletionLabelsAfter(src), Does.Contain("afterVar"),
                "variable declared AFTER the bad array ref must still resolve");
        }

        // A malformed member access like `v.5` — the `.5` lexes as a float, so
        // the parser produces an unparseable statement rather than a clean
        // StructFieldReference. That reaches the CheckStatements default, which
        // used to throw NotImplementedException($"cannot check statement...").
        // Now it's an UnknownStatement diagnostic and resolution continues.
        [Test]
        public void MalformedStatement_DoesNotAbort()
        {
            const string src =
                "type Vec\n  x as integer\nendtype\n" +
                "v as Vec\n" +
                "bad = v.5\n" +
                "afterVar = 10";
            FadeDocument doc = null;
            Assert.DoesNotThrow(() => doc = Build(src));
            Dump("malformed-statement", src);
            Assert.That(HasCode(AllErrors(doc), ErrorCodes.UnknownStatement), Is.True,
                "the unparseable statement should be diagnosed, not thrown");
            Assert.That(CompletionLabelsAfter(src), Does.Contain("afterVar"),
                "variable after the malformed statement must still resolve");
        }

        // NOTE on the other three converted throws (EnsureStructRefRight's
        // non-field right side, EnsureStructField's non-variable left side, and
        // the unresolved-parameter-type default): the parser routes the inputs
        // that would reach them to the statement-level path above instead, so
        // they aren't reachable through normal source today. They're converted
        // to diagnostics defensively — the invariant that matters (no throw ever
        // aborts the whole pass) is covered by MalformedReference_NeverAborts.

        // Broad sweep: throw a pile of malformed references at the resolver and
        // assert it never throws + the trailing variable always resolves.
        [TestCase("a = ghost(5)")]
        [TestCase("a = ghost(5, 6, 7)")]
        [TestCase("a = 5.field")]
        [TestCase("a = ghost(5).x")]
        [TestCase("dim arr(3)\na = arr(1).x")]
        public void MalformedReference_NeverAborts(string bad)
        {
            var src = bad + "\ntrailingVar = 123";
            Assert.DoesNotThrow(() => Build(src), $"resolving `{bad}` should not throw");
            Assert.That(CompletionLabelsAfter(src), Does.Contain("trailingVar"),
                $"variable after `{bad}` must still resolve");
        }
    }
}
