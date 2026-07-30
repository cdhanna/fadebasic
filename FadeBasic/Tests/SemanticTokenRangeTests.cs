using System.Linq;
using FadeBasic;
using FadeBasic.Lib.Standard;
using FadeBasic.LSP.Core;
using FadeBasic.LSP.Core.Handlers;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class SemanticTokenRangeTests
{
    static FadeDocument Doc50()
    {
        var commands = new CommandCollection(new StandardCommands(), new ConsoleCommands());
        var ws = new FadeWorkspace(commands);
        var src = string.Join("\n", Enumerable.Range(0, 50).Select(i => $"x{i} = {i}"));
        return ws.SetDocument("test", src);
    }

    [Test]
    public void Range_IsSubsetOfFull()
    {
        var doc = Doc50();
        var full = SemanticTokensHandler.Compute(doc);
        var range = SemanticTokensHandler.Compute(doc, 10, 20);
        Assert.That(range.Count, Is.GreaterThan(0));
        Assert.That(range.Count, Is.LessThan(full.Count), "range should be a strict subset of the full stream");
    }

    [Test]
    public void Range_TokensAllFallInRequestedLines_RebasedFromZero()
    {
        var doc = Doc50();
        var range = SemanticTokensHandler.Compute(doc, 10, 20);
        // Decode delta-encoded stream exactly like the Playground does (start
        // at line 0, accumulate) — every reconstructed line must be in [10,20).
        var line = 0;
        for (var i = 0; i + 4 < range.Count; i += 5)
        {
            var dLine = range[i];
            if (dLine > 0) line += dLine;
            Assert.That(line, Is.InRange(10, 19), "token outside requested range leaked in");
        }
    }

    [Test]
    public void Range_EmptyWhenNoTokensInWindow()
    {
        var doc = Doc50();
        var range = SemanticTokensHandler.Compute(doc, 9000, 9100);
        Assert.That(range.Count, Is.EqualTo(0));
    }
}
