using FadeBasic.Virtual.HotReload;
using NUnit.Framework;

namespace Tests.HotReload;

[TestFixture]
public class SafepointTests
{
    // 0-based source lines:  0:"a = 1"  1:"a = a + 10"  2:"a = a + 100"
    const string Src = "a = 1\na = a + 10\na = a + 100\n";

    [Test]
    public void ControlGate_PausedAtStatementStart_EditIsSafe_RunsNew()
    {
        // Sitting exactly at a statement's start = re-entering it fresh; editing
        // that statement is safe (we run the NEW version).
        var h = HotReloadHarness.Start(Src);
        Assert.That(h.RunToLine(2), Is.True);
        Assert.That(h.GlobalInt("a"), Is.EqualTo(11));

        var edited = "a = 1\na = a + 10\na = a + 500\n"; // edit the statement we're paused at
        Assert.That(h.IsControlSafe(edited), Is.True);
        Assert.That(h.TryApplyNow(edited), Is.True);
        h.RunToCompletion();
        Assert.That(h.GlobalInt("a"), Is.EqualTo(511), "new version of the paused statement ran");
    }

    // main body calls bump(); pausing inside bump leaves a mid-statement return
    // address on the call frame for line 1 ("r = bump(r)").
    const string FnSrc =
        "r = 0\n" +          // 0
        "r = bump(r)\n" +    // 1  (return site — mid-statement while inside bump)
        "r = r + 1\n" +      // 2
        "end\n" +            // 3
        "function bump(v)\n" + // 4
        "  w = v + 5\n" +    // 5
        "  w = w + 100\n" +  // 6
        "endfunction w\n";   // 7

    [Test]
    public void ControlGate_MidStatementReturnSiteChanged_Unsafe()
    {
        var h = HotReloadHarness.Start(FnSrc);
        Assert.That(h.RunToLine(6), Is.True); // paused inside bump

        // edit line 1 (the call site) — it's a mid-statement return address on the stack
        var edited = FnSrc.Replace("r = bump(r)\n", "r = bump(r) + 7\n");
        Assert.That(h.IsControlSafe(edited), Is.False, "changing a mid-statement return site is unsafe");
        Assert.That(h.TryApplyNow(edited), Is.False);
    }

    [Test]
    public void ControlGate_InsideFunction_EditUnrelatedPastLine_Safe()
    {
        var h = HotReloadHarness.Start(FnSrc);
        Assert.That(h.RunToLine(6), Is.True);

        var edited = FnSrc.Replace("r = 0\n", "r = 42\n"); // line 0, already executed, not active
        Assert.That(h.IsControlSafe(edited), Is.True);
        Assert.That(h.TryApplyNow(edited), Is.True);
    }

    [Test]
    public void ControlGate_FutureStatementChanged_Safe_AndNewCodeRuns()
    {
        var h = HotReloadHarness.Start(Src);
        Assert.That(h.RunToLine(1), Is.True);      // paused BEFORE line 2
        Assert.That(h.GlobalInt("a"), Is.EqualTo(1));

        var edited = "a = 1\na = a + 10\na = a + 500\n"; // edits line 2 (NOT yet active)
        Assert.That(h.IsControlSafe(edited), Is.True);
        Assert.That(h.TryApplyNow(edited), Is.True);

        h.RunToCompletion();
        // line 1 ran (1+10=11), then the NEW line 2 (11+500=511)
        Assert.That(h.GlobalInt("a"), Is.EqualTo(511), "resumed on the new bytecode");
    }

    [Test]
    public void ControlGate_PastStatementChanged_Safe_ResumeUnaffected()
    {
        var h = HotReloadHarness.Start(Src);
        Assert.That(h.RunToLine(2), Is.True);
        Assert.That(h.GlobalInt("a"), Is.EqualTo(11));

        // edit line 0 (already executed, not active). line 2 unchanged.
        var edited = "a = 999\na = a + 10\na = a + 100\n";
        Assert.That(h.IsControlSafe(edited), Is.True);
        Assert.That(h.TryApplyNow(edited), Is.True);

        h.RunToCompletion();
        // line 0's edit is behind us; line 2 (unchanged) runs: 11 + 100 = 111
        Assert.That(h.GlobalInt("a"), Is.EqualTo(111));
    }

    [Test]
    public void PcRemap_ResumesAndRunsAppendedStatement()
    {
        var h = HotReloadHarness.Start(Src);
        Assert.That(h.RunToLine(1), Is.True);

        var edited = "a = 1\na = a + 10\na = a + 100\nb = 42\n"; // append line 3
        Assert.That(h.TryApplyNow(edited), Is.True);

        h.RunToCompletion();
        Assert.That(h.GlobalInt("a"), Is.EqualTo(111));
        Assert.That(h.GlobalInt("b"), Is.EqualTo(42), "appended statement executed after resume");
    }
}
