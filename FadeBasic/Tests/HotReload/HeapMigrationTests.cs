using FadeBasic.Virtual.HotReload;
using NUnit.Framework;

namespace Tests.HotReload;

[TestFixture]
public class HeapMigrationTests
{
    // vec instance created + fields set early; then trailing marker statements
    // give us a safe (unrelated) place to pause and reload.
    static string Src(string typeBody) =>
        "type vec\n" + typeBody + "endtype\n" +
        "v as vec\n" +
        "v.x = 10\n" +
        "v.y = 20\n" +
        "marker = 0\n" +
        "marker = 1\n";

    const string XY = "  x\n  y\n";

    [Test]
    public void HeapInspector_ReadsFieldValues()
    {
        var h = HotReloadHarness.Start(Src(XY));
        h.RunToCompletion();
        Assert.That(h.StructFieldInt("v", "x"), Is.EqualTo(10));
        Assert.That(h.StructFieldInt("v", "y"), Is.EqualTo(20));
        Assert.That(h.LiveInstances("vec"), Is.EqualTo(1));
    }

    [Test]
    public void Heap_ReorderFields_ValuesPreservedByName()
    {
        var h = HotReloadHarness.Start(Src(XY));
        Assert.That(h.RunToLine(8), Is.True);        // paused at "marker = 1" (unrelated), fields already set

        // reorder fields y,x (same size) → offsets swap
        Assert.That(h.TryApplyNow(Src("  y\n  x\n"), migrateHeap: true), Is.True);

        Assert.That(h.StructFieldInt("v", "x"), Is.EqualTo(10), "x follows its name across the offset swap");
        Assert.That(h.StructFieldInt("v", "y"), Is.EqualTo(20), "y follows its name across the offset swap");
    }

    [Test]
    public void Heap_RemoveField_SurvivorPreserved()
    {
        var h = HotReloadHarness.Start(Src(XY));
        Assert.That(h.RunToLine(8), Is.True);

        // remove y → struct shrinks; x survives. Blank-pad so line numbers of
        // the (unchanged) marker statements don't shift — the coarse line-based
        // matcher needs the active statement's line to be stable.
        var newSrc = "type vec\n  x\n\nendtype\nv as vec\nv.x = 10\n\nmarker = 0\nmarker = 1\n";
        var plan = h.Classify(newSrc);
        Assert.That(plan.Verdict, Is.EqualTo(Verdict.ApplicableNow));
        Assert.That(h.TryApplyNow(newSrc, migrateHeap: true), Is.True);
        Assert.That(h.StructFieldInt("v", "x"), Is.EqualTo(10));
    }

    [Test]
    public void Heap_AddField_Growth_WithLiveInstance_PermanentlyRude()
    {
        var h = HotReloadHarness.Start(Src(XY));
        Assert.That(h.RunToLine(8), Is.True);
        Assert.That(h.LiveInstances("vec"), Is.EqualTo(1));

        var plan = h.Classify(Src("  x\n  y\n  z\n")); // add field → grows
        Assert.That(plan.Verdict, Is.EqualTo(Verdict.PermanentlyRude));
        Assert.That(plan.RudeReason, Does.Contain("grew"));
    }

    [Test]
    public void Heap_LineShiftingEdit_ConservativelyPendingTransient()
    {
        // KNOWN LIMITATION (coarse tier): deleting lines above the paused
        // statement shifts its line number, so the line-based matcher can't map
        // it and conservatively refuses to apply. The finer AST-diff tier (see
        // design doc) would resolve this. Documented here so the behavior is
        // intentional, not a silent surprise.
        var h = HotReloadHarness.Start(Src(XY));
        Assert.That(h.RunToLine(8), Is.True);
        var lineShifting = "type vec\n  x\nendtype\nv as vec\nv.x = 10\nmarker = 0\nmarker = 1\n";
        Assert.That(h.Classify(lineShifting).Verdict, Is.EqualTo(Verdict.PendingTransient));
    }

    [Test]
    public void Heap_AfterReorderMigration_GcKeepsInstanceIntact()
    {
        var h = HotReloadHarness.Start(Src(XY));
        Assert.That(h.RunToLine(8), Is.True);
        Assert.That(h.TryApplyNow(Src("  y\n  x\n"), migrateHeap: true), Is.True);

        h.CollectGarbage(); // the migrated instance is still rooted by global v
        Assert.That(h.LiveInstances("vec"), Is.EqualTo(1), "instance survives GC after migration");
        Assert.That(h.StructFieldInt("v", "x"), Is.EqualTo(10));
        Assert.That(h.StructFieldInt("v", "y"), Is.EqualTo(20));
    }
}
