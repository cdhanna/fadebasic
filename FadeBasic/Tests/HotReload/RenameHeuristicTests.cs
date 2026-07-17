using System.Linq;
using FadeBasic.Virtual.HotReload;
using NUnit.Framework;

namespace Tests.HotReload;

[TestFixture]
public class RenameHeuristicTests
{
    static readonly StructuralDiffOptions Detect = new StructuralDiffOptions { DetectRenames = true };

    static TypeEdit VecEdit(string oldBody, string newBody, StructuralDiffOptions opts)
    {
        var oldSrc = "type vec\n" + oldBody + "endtype\nv as vec\n";
        var newSrc = "type vec\n" + newBody + "endtype\nv as vec\n";
        var edits = HotReloadHarness.DiffSources(oldSrc, newSrc, opts);
        return edits.TypeEdits.FirstOrDefault(t => t.TypeName == "vec");
    }

    [Test]
    public void Rename_SingleFieldSameType_Proposed()
    {
        var te = VecEdit("  x\n  y\n", "  x\n  w\n", Detect);
        Assert.That(te, Is.Not.Null);
        Assert.That(te.FieldEdits.Any(f => f.Kind == FieldEditKind.Renamed && f.OldName == "y" && f.Name == "w"), Is.True);
        Assert.That(te.FieldEdits.Any(f => f.Kind == FieldEditKind.Added || f.Kind == FieldEditKind.Removed), Is.False,
            "a confident rename is not reported as add+remove");
    }

    [Test]
    public void Rename_TwoCandidates_Ambiguous_NotPaired()
    {
        // both removed (x,y) and both added (a,b) are same-typed → ambiguous → no rename
        var te = VecEdit("  x\n  y\n", "  a\n  b\n", Detect);
        Assert.That(te, Is.Not.Null);
        Assert.That(te.FieldEdits.Any(f => f.Kind == FieldEditKind.Renamed), Is.False, "ambiguous pairing must not guess");
        Assert.That(te.FieldEdits.Count(f => f.Kind == FieldEditKind.Removed), Is.EqualTo(2));
        Assert.That(te.FieldEdits.Count(f => f.Kind == FieldEditKind.Added), Is.EqualTo(2));
    }

    [Test]
    public void Rename_TypeChanged_NotPaired()
    {
        var te = VecEdit("  x\n  y as integer\n", "  x\n  w as float\n", Detect);
        Assert.That(te, Is.Not.Null);
        Assert.That(te.FieldEdits.Any(f => f.Kind == FieldEditKind.Renamed), Is.False,
            "different field types are not a rename candidate");
    }

    [Test]
    public void Rename_DisabledByDefault_ReportsAddRemove()
    {
        var te = VecEdit("  x\n  y\n", "  x\n  w\n", StructuralDiffOptions.Default);
        Assert.That(te, Is.Not.Null);
        Assert.That(te.FieldEdits.Any(f => f.Kind == FieldEditKind.Renamed), Is.False);
        Assert.That(te.FieldEdits.Any(f => f.Kind == FieldEditKind.Removed && f.Name == "y"), Is.True);
        Assert.That(te.FieldEdits.Any(f => f.Kind == FieldEditKind.Added && f.Name == "w"), Is.True);
    }

    [Test]
    public void Rename_HeapMigration_CarriesFieldValue()
    {
        // v.y = 20 in the old program; rename y -> w; the value must follow to v.w.
        var oldSrc = "type vec\n  x\n  y\nendtype\nv as vec\nv.x = 10\nv.y = 20\nmarker = 0\nmarker = 1\n";
        var newSrc = "type vec\n  x\n  w\nendtype\nv as vec\nv.x = 10\nv.w = 20\nmarker = 0\nmarker = 1\n";

        var h = HotReloadHarness.Start(oldSrc);
        Assert.That(h.RunToLine(8), Is.True); // fields set; paused at unrelated marker

        Assert.That(h.TryApplyNow(newSrc, migrateHeap: true), Is.True);
        Assert.That(h.StructFieldInt("v", "x"), Is.EqualTo(10));
        Assert.That(h.StructFieldInt("v", "w"), Is.EqualTo(20), "renamed field keeps the old field's value");
    }
}
