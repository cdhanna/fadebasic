using FadeBasic.Virtual.HotReload;
using NUnit.Framework;

namespace Tests.HotReload;

[TestFixture]
public class ReconcileClassifierTests
{
    // 0-based: 0:"a = 1"  1:"b = 2"  2:"c = 3"
    const string Src3 = "a = 1\nb = 2\nc = 3\n";

    [Test]
    public void Verdict_IdenticalSource_NoChange()
    {
        var h = HotReloadHarness.Start(Src3);
        h.RunToLine(1);
        Assert.That(h.Classify(Src3).Verdict, Is.EqualTo(Verdict.NoChange));
    }

    [Test]
    public void Verdict_FutureStatementEdit_ApplicableNow()
    {
        var h = HotReloadHarness.Start(Src3);
        h.RunToLine(1);                               // active = line 1
        var plan = h.Classify("a = 1\nb = 2\nc = 99\n"); // edit line 2 (not active)
        Assert.That(plan.Verdict, Is.EqualTo(Verdict.ApplicableNow));
    }

    const string FnSrc =
        "r = 0\n" + "r = bump(r)\n" + "r = r + 1\n" + "end\n" +
        "function bump(v)\n" + "  w = v + 5\n" + "  w = w + 100\n" + "endfunction w\n";

    [Test]
    public void Verdict_MidStatementReturnSiteEdit_PendingTransient()
    {
        var h = HotReloadHarness.Start(FnSrc);
        h.RunToLine(6);                               // paused inside bump; line 1 is a mid-statement return site
        var plan = h.Classify(FnSrc.Replace("r = bump(r)\n", "r = bump(r) + 7\n"));
        Assert.That(plan.Verdict, Is.EqualTo(Verdict.PendingTransient));
        Assert.That(plan.BlockingStatements, Is.Not.Empty);
    }

    [Test]
    public void Verdict_AddGlobal_ApplicableNow()
    {
        var h = HotReloadHarness.Start(Src3);
        h.RunToLine(1);
        var plan = h.Classify("a = 1\nb = 2\nc = 3\nd = 4\n");
        Assert.That(plan.Verdict, Is.EqualTo(Verdict.ApplicableNow));
    }

    [Test]
    public void Verdict_RemoveGlobal_ApplicableNow()
    {
        var h = HotReloadHarness.Start(Src3);
        h.RunToLine(1);
        var plan = h.Classify("a = 1\nb = 2\n"); // removes line 2 (c), not active
        Assert.That(plan.Verdict, Is.EqualTo(Verdict.ApplicableNow));
    }

    [Test]
    public void Verdict_RetypeGlobal_PermanentlyRude()
    {
        var h = HotReloadHarness.Start("x as integer\nx = 5\n");
        h.RunToCompletion();
        var plan = h.Classify("x as string\n");
        Assert.That(plan.Verdict, Is.EqualTo(Verdict.PermanentlyRude));
        Assert.That(plan.RudeReason, Does.Contain("x"));
    }
}
