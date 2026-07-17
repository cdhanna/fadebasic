using System;
using System.Collections.Generic;
using System.IO;
using FadeBasic.Launch;
using FadeBasic.Sdk;
using FadeBasic.Virtual;
using FadeBasic.Virtual.HotReload;
using NUnit.Framework;

namespace Tests.HotReload;

[TestFixture]
public class LauncherWatchTests
{
    [Test]
    public void Inference_SourceMapConcat_CompilesAllFilesInOrder()
    {
        // Mirrors the --fade-watch launchable-inference path: recompose the exact
        // built source set via SourceMap.CreateSourceMap (the same join the build
        // uses) and compile it. All files' globals must be present.
        var dir = Path.Combine(Path.GetTempPath(), "fadewatch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var main = Path.Combine(dir, "main.fbasic");
            var a = Path.Combine(dir, "a.fbasic");
            var b = Path.Combine(dir, "b.fbasic");
            File.WriteAllText(main, "m = 9\n");
            File.WriteAllText(a, "aa = 1\n");
            File.WriteAllText(b, "bb = 2\n");

            // explicit order, as the launchable would carry it
            var paths = new List<string> { main, a, b };
            var joined = SourceMap.CreateSourceMap(paths).fullSource;

            var ok = Launcher.TryCompileSource(joined, HotReloadHarness.Commands, out var compiler, out var err);
            Assert.That(ok, Is.True, err);
            var facts = ProgramFacts.FromCompiler(compiler);
            Assert.That(facts.Globals.ContainsKey("m"), Is.True);
            Assert.That(facts.Globals.ContainsKey("aa"), Is.True);
            Assert.That(facts.Globals.ContainsKey("bb"), Is.True);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void ComposeDirectory_JoinsAllFbasic_MainFirst_AndCompiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fadewatch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // deliberately non-alphabetical so ordering rules are exercised
            File.WriteAllText(Path.Combine(dir, "zeta.fbasic"), "z = 3\n");
            File.WriteAllText(Path.Combine(dir, "alpha.fbasic"), "a = 1\n");
            File.WriteAllText(Path.Combine(dir, "main.fbasic"), "m = 9\n");

            var joined = Launcher.ComposeDirectory(dir);

            // main.fbasic must come first
            Assert.That(joined.IndexOf("m = 9", StringComparison.Ordinal),
                Is.LessThan(joined.IndexOf("a = 1", StringComparison.Ordinal)));
            Assert.That(joined.IndexOf("a = 1", StringComparison.Ordinal),
                Is.LessThan(joined.IndexOf("z = 3", StringComparison.Ordinal)));

            // the joined program compiles and exposes globals from every file
            var ok = Launcher.TryCompileSource(joined, HotReloadHarness.Commands, out var compiler, out var err);
            Assert.That(ok, Is.True, err);
            var facts = ProgramFacts.FromCompiler(compiler);
            Assert.That(facts.Globals.ContainsKey("m"), Is.True);
            Assert.That(facts.Globals.ContainsKey("a"), Is.True);
            Assert.That(facts.Globals.ContainsKey("z"), Is.True);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void ComposeDirectory_Recurses_IntoSubfolders()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fadewatch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "main.fbasic"), "m = 1\n");
            File.WriteAllText(Path.Combine(dir, "sub", "extra.fbasic"), "e = 2\n");
            var joined = Launcher.ComposeDirectory(dir);
            Assert.That(joined, Does.Contain("m = 1"));
            Assert.That(joined, Does.Contain("e = 2"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void TryCompileSource_Valid_ProducesUsableCompiler()
    {
        var ok = Launcher.TryCompileSource("x = 1\nx = x + 2\n", HotReloadHarness.Commands, out var compiler, out var err);
        Assert.That(ok, Is.True, err);
        Assert.That(compiler, Is.Not.Null);
        Assert.That(compiler.DebugData, Is.Not.Null, "watch always compiles with debug data");
        var facts = ProgramFacts.FromCompiler(compiler);
        Assert.That(facts.Globals.ContainsKey("x"), Is.True);
    }

    [Test]
    public void TryCompileSource_ParseError_ReturnsFalseWithMessage()
    {
        var ok = Launcher.TryCompileSource("x = = = 1\n", HotReloadHarness.Commands, out var compiler, out var err);
        Assert.That(ok, Is.False);
        Assert.That(compiler, Is.Null);
        Assert.That(err, Is.Not.Null.And.Not.Empty);
    }

    // Reproduces RunWithWatch's inner loop deterministically (no FileSystemWatcher,
    // no threads): arm an edit, pump the VM in batches suspending at statement
    // safepoints when pending, Tick to commit, and confirm the program finishes on
    // the new code.
    [Test]
    public void WatchPump_AppliesArmedEdit_RunsNewCodeToCompletion()
    {
        var src = "x = 0\nx = x + 1\nx = x + 1\ny = 100\nend\n";
        var h = HotReloadHarness.Start(src);
        var s = h.NewSession();

        // as if the watcher fired: edit the (not-yet-run) y line
        s.Arm("x = 0\nx = x + 1\nx = x + 1\ny = 500\nend\n");

        int guard = 0;
        while (h.Vm.instructionIndex < h.Vm.program.Length
               && h.Vm.error.type == VirtualRuntimeErrorType.NONE
               && guard++ < 10000)
        {
            h.Vm.Execute2(64, ins =>
                s.HasPending
                && HotReloadUtil.StatementStartForInstruction(s.CurrentFacts, ins) == ins);
            if (s.HasPending) s.Tick();
        }

        Assert.That(s.HasPending, Is.False, "armed edit committed during the run");
        h.AdoptFacts(s.CurrentFacts);
        Assert.That(h.GlobalInt("y"), Is.EqualTo(500), "program finished on the hot-reloaded code");
    }
}
