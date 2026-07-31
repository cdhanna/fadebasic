// Tests for HoverHandler.BuildOverloadedCommandMarkdown — the help-browser
// renderer that shows every overload of a command name, each with its own
// signature and doc decoration.

using System;
using System.Linq;
using FadeBasic.LSP.Core.Handlers;
using NUnit.Framework;

namespace Tests
{
    [TestFixture]
    public class HoverOverloadMarkdownTests
    {
        // The name is shown once, and every overload's signature is listed so
        // the reader sees all available variants.
        [Test]
        public void OverloadedCommand_ListsEveryOverloadSignatureUnderOneHeader()
        {
            var overloads = TestCommands.CommandsForTesting.Commands
                .Where(c => c.name == "ovrbump").ToList();
            Assert.That(overloads.Count, Is.EqualTo(2), "fixture should have 2 ovrbump overloads");

            var md = HoverHandler.BuildOverloadedCommandMarkdown("ovrbump", overloads, null);

            // Command name header appears exactly once.
            var headerCount = md.Split(new[] { "### ovrbump" }, StringSplitOptions.None).Length - 1;
            Assert.That(headerCount, Is.EqualTo(1), "the `### name` header should appear once");

            // Both overload signatures are present.
            Assert.That(md, Does.Contain("ref Integer"), "ref-int overload signature");
            Assert.That(md, Does.Contain("ref Float"), "ref-float overload signature");
        }

        // A single-overload command doesn't get a redundant per-overload
        // signature sub-header — it reads like an ordinary command entry.
        [Test]
        public void SingleOverloadCommand_OmitsPerOverloadSignatureHeader()
        {
            var overloads = TestCommands.CommandsForTesting.Commands
                .Where(c => c.name == "add").ToList();
            Assert.That(overloads.Count, Is.EqualTo(1));

            var md = HoverHandler.BuildOverloadedCommandMarkdown("add", overloads, null);
            Assert.That(md, Does.Contain("### add"));
            Assert.That(md, Does.Not.Contain("#### `add("), "no per-overload signature for a single overload");
        }
    }
}
