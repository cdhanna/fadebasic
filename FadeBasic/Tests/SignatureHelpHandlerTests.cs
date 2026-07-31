// Behavioral tests for SignatureHelpHandler.Compute, focused on how it
// presents COMMAND OVERLOADS: the full overload set is listed, and
// ActiveSignature points at whichever overload the call resolved to.

using FadeBasic.LSP.Core;
using FadeBasic.LSP.Core.Handlers;
using NUnit.Framework;

namespace Tests
{
    [TestFixture]
    public class SignatureHelpHandlerTests
    {
        // Run Compute on a source string with `|` marking the cursor.
        private static LspSignatureHelp SignatureAt(string sourceWithCursor)
        {
            var cursorIdx = sourceWithCursor.IndexOf('|');
            Assert.That(cursorIdx, Is.GreaterThanOrEqualTo(0), "test source must contain a '|' cursor marker");
            var source = sourceWithCursor.Remove(cursorIdx, 1);
            int line = 0, ch = 0;
            for (var i = 0; i < cursorIdx; i++)
            {
                if (source[i] == '\n') { line++; ch = 0; }
                else { ch++; }
            }
            var workspace = new FadeWorkspace(TestCommands.CommandsForTesting);
            var doc = workspace.SetDocument("test://sig.fbasic", source);
            return SignatureHelpHandler.Compute(doc, line, ch);
        }

        // Both ovrbump overloads (ref-int and ref-float) are surfaced, and the
        // int target selects the ref-int overload as active.
        [Test]
        public void Overloaded_ListsAllOverloads_IntTargetActive()
        {
            var help = SignatureAt("n = 5\novrbump n, 2|");
            Assert.That(help, Is.Not.Null);
            Assert.That(help.Signatures.Count, Is.EqualTo(2), "both ovrbump overloads should be listed");
            Assert.That(help.Signatures[help.ActiveSignature].Label, Does.Contain("Integer"),
                "int target selects the ref-int overload");
        }

        // A float target flips ActiveSignature to the ref-float overload — same
        // command, same overload list, different one highlighted.
        [Test]
        public void Overloaded_FloatTargetSelectsRealOverload()
        {
            var help = SignatureAt("f as float = 5\novrbump f, 2|");
            Assert.That(help, Is.Not.Null);
            Assert.That(help.Signatures.Count, Is.EqualTo(2));
            Assert.That(help.Signatures[help.ActiveSignature].Label, Does.Contain("Float"),
                "float target selects the ref-float overload");
        }

        // Different-arity overloads are all listed too.
        [Test]
        public void Overloaded_DifferentArity_ListsAll()
        {
            var help = SignatureAt("x = ovrmix(3, 4|)");
            Assert.That(help, Is.Not.Null);
            // ovrmix has three overloads: (int), (float), (int,int).
            Assert.That(help.Signatures.Count, Is.EqualTo(3));
        }

        // A single-overload command still yields exactly one signature.
        [Test]
        public void SingleOverload_YieldsOneSignature()
        {
            var help = SignatureAt("n = 5\ninc n, 2|");
            Assert.That(help, Is.Not.Null);
            Assert.That(help.Signatures.Count, Is.EqualTo(1));
        }
    }
}
