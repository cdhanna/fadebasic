using FadeBasic.Virtual.HotReload;
using NUnit.Framework;

namespace Tests.HotReload;

[TestFixture]
public class VariableMigrationTests
{
    static ProgramFacts Facts(string src) => HotReloadHarness.FactsFor(src);

    [Test]
    public void Migrate_ReorderDecls_ValuesPreservedByName()
    {
        var h = HotReloadHarness.Start("a = 5\nb = 7\n");
        h.RunToCompletion();
        Assert.That(h.GlobalInt("a"), Is.EqualTo(5));
        Assert.That(h.GlobalInt("b"), Is.EqualTo(7));

        var newFacts = Facts("b = 7\na = 5\n"); // reordered → addresses swap
        Migrator.RemapGlobals(h.Vm, h.Facts, newFacts);
        h.AdoptFacts(newFacts);

        Assert.That(h.GlobalInt("a"), Is.EqualTo(5), "a preserved by name across address shift");
        Assert.That(h.GlobalInt("b"), Is.EqualTo(7), "b preserved by name across address shift");
    }

    [Test]
    public void Migrate_AddGlobal_ExistingPreserved_NewDefaulted()
    {
        var h = HotReloadHarness.Start("a = 5\nb = 7\n");
        h.RunToCompletion();

        var newFacts = Facts("a = 5\nb = 7\nc = 9\n");
        Migrator.RemapGlobals(h.Vm, h.Facts, newFacts);
        h.AdoptFacts(newFacts);

        Assert.That(h.GlobalInt("a"), Is.EqualTo(5));
        Assert.That(h.GlobalInt("b"), Is.EqualTo(7));
        // c is new; new code hasn't run yet, so it defaults (not 9)
        Assert.That(h.GlobalInt("c"), Is.EqualTo(0), "newly-added global defaults until new code runs");
    }

    [Test]
    public void Migrate_RemoveGlobal_OthersPreserved()
    {
        var h = HotReloadHarness.Start("a = 5\nb = 7\nc = 9\n");
        h.RunToCompletion();

        var newFacts = Facts("a = 5\nb = 7\n");
        Migrator.RemapGlobals(h.Vm, h.Facts, newFacts);
        h.AdoptFacts(newFacts);

        Assert.That(h.GlobalInt("a"), Is.EqualTo(5));
        Assert.That(h.GlobalInt("b"), Is.EqualTo(7));
        Assert.That(h.HasGlobal("c"), Is.False, "removed global is gone from the new layout");
    }

    [Test]
    public void Migrate_FloatGlobal_ValuePreserved()
    {
        var h = HotReloadHarness.Start("a as float\na = 3.5\n");
        h.RunToCompletion();
        Assert.That(h.GlobalFloat("a"), Is.EqualTo(3.5f).Within(0.0001f));

        var newFacts = Facts("z = 0\na as float\na = 3.5\n"); // insert a var before → a's address shifts
        Migrator.RemapGlobals(h.Vm, h.Facts, newFacts);
        h.AdoptFacts(newFacts);

        Assert.That(h.GlobalFloat("a"), Is.EqualTo(3.5f).Within(0.0001f), "float value preserved across address shift");
    }
}
