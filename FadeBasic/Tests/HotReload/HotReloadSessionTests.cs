using FadeBasic.Virtual.HotReload;
using NUnit.Framework;

namespace Tests.HotReload;

[TestFixture]
public class HotReloadSessionTests
{
    const string Src3 = "a = 1\nb = 2\nc = 3\n";
    const string FnSrc =
        "r = 0\n" + "r = bump(r)\n" + "r = r + 1\n" + "end\n" +
        "function bump(v)\n" + "  w = v + 5\n" + "  w = w + 100\n" + "endfunction w\n";

    [Test]
    public void Session_ApplicableImmediately_Commits_AndNewCodeRuns()
    {
        var h = HotReloadHarness.Start(Src3);
        h.RunToLine(1);
        var s = h.NewSession();

        s.Arm("a = 1\nb = 2\nc = 99\n");
        var plan = s.Tick();

        Assert.That(plan.Verdict, Is.EqualTo(Verdict.ApplicableNow));
        Assert.That(s.HasPending, Is.False, "committed edit clears the pending target");

        h.AdoptFacts(s.CurrentFacts);
        h.RunToCompletion();
        Assert.That(h.GlobalInt("c"), Is.EqualTo(99));
    }

    [Test]
    public void Session_TransientBecomesApplicable_AsVmAdvances()
    {
        var h = HotReloadHarness.Start(FnSrc);
        h.RunToLine(6); // inside bump; line 1 is a mid-statement return site
        var s = h.NewSession();

        s.Arm(FnSrc.Replace("r = bump(r)\n", "r = bump(r) + 7\n"));
        Assert.That(s.Tick().Verdict, Is.EqualTo(Verdict.PendingTransient), "blocked while the call is active");
        Assert.That(s.HasPending, Is.True, "stays armed while transient");

        h.RunToLine(2); // bump returns; back in main, past the call site
        Assert.That(s.Tick().Verdict, Is.EqualTo(Verdict.ApplicableNow), "drained → applies");
        Assert.That(s.HasPending, Is.False);
    }

    [Test]
    public void Session_PermanentlyRude_NeverApplies()
    {
        var h = HotReloadHarness.Start("x as integer\nx = 5\n");
        h.RunToCompletion();
        var s = h.NewSession();

        s.Arm("x as string\n");
        Assert.That(s.Tick().Verdict, Is.EqualTo(Verdict.PermanentlyRude));
        Assert.That(s.HasPending, Is.True, "stays armed; host must restart to pick it up");
        Assert.That(s.Tick().Verdict, Is.EqualTo(Verdict.PermanentlyRude), "waiting does not help");
    }

    [Test]
    public void Session_RearmWithNewerSource_Supersedes()
    {
        var h = HotReloadHarness.Start(Src3);
        h.RunToLine(1);
        var s = h.NewSession();

        s.Arm("a = 1\nb = 2\nc = 50\n"); // target A
        s.Arm("a = 1\nb = 2\nc = 77\n"); // target B supersedes
        Assert.That(s.Tick().Verdict, Is.EqualTo(Verdict.ApplicableNow));

        h.AdoptFacts(s.CurrentFacts);
        h.RunToCompletion();
        Assert.That(h.GlobalInt("c"), Is.EqualTo(77), "latest armed source wins");
    }

    [Test]
    public void Session_Cancel_LeavesVmUntouched()
    {
        var h = HotReloadHarness.Start(Src3);
        h.RunToLine(1);
        var s = h.NewSession();

        s.Arm("a = 1\nb = 2\nc = 99\n");
        s.Cancel();
        Assert.That(s.HasPending, Is.False);
        Assert.That(s.Tick().Verdict, Is.EqualTo(Verdict.NoChange));

        h.RunToCompletion();
        Assert.That(h.GlobalInt("c"), Is.EqualTo(3), "cancelled edit never applied");
    }

    [Test]
    public void Session_Committed_EventFires()
    {
        var h = HotReloadHarness.Start(Src3);
        h.RunToLine(1);
        var s = h.NewSession();

        ReconcilePlan committed = null;
        s.OnCommitted += p => committed = p;

        s.Arm("a = 1\nb = 2\nc = 99\n");
        s.Tick();
        Assert.That(committed, Is.Not.Null);
        Assert.That(committed.Verdict, Is.EqualTo(Verdict.ApplicableNow));
    }
}
