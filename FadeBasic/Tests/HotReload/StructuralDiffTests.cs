using System.Linq;
using FadeBasic.Virtual.HotReload;
using NUnit.Framework;

namespace Tests.HotReload;

[TestFixture]
public class StructuralDiffTests
{
    [Test]
    public void Diff_IdenticalSource_EmptyEditSet()
    {
        var src = "x = 10\ny = 20\n";
        var edits = HotReloadHarness.DiffSources(src, src);
        Assert.That(edits.IsEmpty, Is.True, edits.ToString());
    }

    [Test]
    public void Diff_AddGlobal_ReportsGlobalAdded()
    {
        var edits = HotReloadHarness.DiffSources("x = 10\ny = 20\n", "x = 10\ny = 20\nz = 30\n");
        Assert.That(edits.VariableEdits.Any(e => e.Kind == VarEditKind.Added && e.Name == "z"), Is.True);
        Assert.That(edits.VariableEdits.Any(e => e.Kind == VarEditKind.Removed), Is.False);
    }

    [Test]
    public void Diff_RemoveGlobal_ReportsGlobalRemoved()
    {
        var edits = HotReloadHarness.DiffSources("x = 10\ny = 20\nz = 30\n", "x = 10\ny = 20\n");
        Assert.That(edits.VariableEdits.Any(e => e.Kind == VarEditKind.Removed && e.Name == "z"), Is.True);
    }

    [Test]
    public void Diff_ReorderDecls_NoAddRemove()
    {
        // reordering first-use shifts register addresses but must NOT look like add/remove
        var edits = HotReloadHarness.DiffSources("a = 1\nb = 2\n", "b = 2\na = 1\n");
        Assert.That(edits.VariableEdits.Any(e => e.Kind == VarEditKind.Added || e.Kind == VarEditKind.Removed),
            Is.False, "reorder must be name-matched, not add+remove");
    }

    [Test]
    public void Diff_RetypeGlobal_IntToString_ReportsRetype()
    {
        var edits = HotReloadHarness.DiffSources("x as integer\n", "x as string\n");
        Assert.That(edits.VariableEdits.Any(e => e.Kind == VarEditKind.Retyped && e.Name == "x"), Is.True);
    }

    [Test]
    public void Diff_AddType_ReportsTypeAdded()
    {
        var oldSrc = "x = 1\n";
        var newSrc = "type vec\n  x as integer\n  y as integer\nendtype\np as vec\n";
        var edits = HotReloadHarness.DiffSources(oldSrc, newSrc);
        Assert.That(edits.TypeEdits.Any(t => t.Added && t.TypeName == "vec"), Is.True);
    }

    [Test]
    public void Diff_AddTypeField_ReportsFieldAdded()
    {
        var oldSrc = "type vec\n  x as integer\nendtype\np as vec\n";
        var newSrc = "type vec\n  x as integer\n  y as integer\nendtype\np as vec\n";
        var edits = HotReloadHarness.DiffSources(oldSrc, newSrc);
        var te = edits.TypeEdits.FirstOrDefault(t => t.TypeName == "vec");
        Assert.That(te, Is.Not.Null);
        Assert.That(te.FieldEdits.Any(f => f.Kind == FieldEditKind.Added && f.Name == "y"), Is.True);
    }

    [Test]
    public void Diff_RemoveTypeField_ReportsFieldRemoved()
    {
        var oldSrc = "type vec\n  x as integer\n  y as integer\nendtype\np as vec\n";
        var newSrc = "type vec\n  x as integer\nendtype\np as vec\n";
        var edits = HotReloadHarness.DiffSources(oldSrc, newSrc);
        var te = edits.TypeEdits.FirstOrDefault(t => t.TypeName == "vec");
        Assert.That(te, Is.Not.Null);
        Assert.That(te.FieldEdits.Any(f => f.Kind == FieldEditKind.Removed && f.Name == "y"), Is.True);
    }

    [Test]
    public void Diff_RetypeTypeField_ReportsRetype()
    {
        var oldSrc = "type vec\n  x as integer\nendtype\np as vec\n";
        var newSrc = "type vec\n  x as float\nendtype\np as vec\n";
        var edits = HotReloadHarness.DiffSources(oldSrc, newSrc);
        var te = edits.TypeEdits.FirstOrDefault(t => t.TypeName == "vec");
        Assert.That(te, Is.Not.Null);
        Assert.That(te.FieldEdits.Any(f => f.Kind == FieldEditKind.Retyped && f.Name == "x"), Is.True);
    }

    [Test]
    public void Diff_EditBodyStatement_ComputesChangedStatements()
    {
        var oldSrc = "x = 1\nx = x + 2\nx = x + 3\n";
        var newSrc = "x = 1\nx = x + 99\nx = x + 3\n";
        var edits = HotReloadHarness.DiffSources(oldSrc, newSrc);
        Assert.That(edits.ChangedStatementInstructions.Count > 0 || edits.CoarseBodyChanged, Is.True,
            "an edited statement must register in S (or coarse fallback)");
    }

    [Test]
    public void Diff_UnchangedBody_NoChangedStatements()
    {
        var src = "x = 1\nx = x + 2\nx = x + 3\n";
        var edits = HotReloadHarness.DiffSources(src, src);
        Assert.That(edits.ChangedStatementInstructions.Count, Is.EqualTo(0));
        Assert.That(edits.CoarseBodyChanged, Is.False);
    }
}
