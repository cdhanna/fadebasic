using ApplicationSupport.Code;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Launch;
using FadeBasic.Sdk;
using FadeBasic.Virtual;

namespace Tests;

public class SourceMapTests
{
    
    [Test]
    public void Demo()
    {
        var file = @"print ""hello""
x = 3
print ""igloo""";
        var fileName = "mock.fbasic";

        var map = SourceMap.CreateSourceMap(new List<string> { fileName }, _ => file.SplitNewLines());
        var unit = map.Parse(TestCommands.CommandsForTesting);

        var expr = unit.program.statements[2];
        var range = map.GetOriginalRange(new TokenRange { start = expr.StartToken, end = expr.EndToken });
        
        // the fact that code reaches here is good!
        Assert.That(range.startLine, Is.EqualTo(2));
        Assert.That(range.endLine, Is.EqualTo(2));
    }
    
    
    [Test]
    public void Test2()
    {
        var file = @"print ""hello""
a = 1
print str$(3)";
        var fileName = "mock.fbasic";

        var map = SourceMap.CreateSourceMap(new List<string> { fileName }, _ => file.SplitNewLines());
        var unit = map.Parse(TestCommands.CommandsForTesting);

        var expr = unit.program.statements[2];
        var range = map.GetOriginalRange(new TokenRange { start = expr.StartToken, end = expr.EndToken });

        // the fact that code reaches here is good!
        Assert.That(range.startLine, Is.EqualTo(2));
        Assert.That(range.endLine, Is.EqualTo(2));
    }

    [Test]
    public void ApplySourceMap_StampsFilePath_AndRemapsLineNumbers()
    {
        // Two files concatenated; the manifest entry for `bar`'s test
        // sits in the second file at its own (in-file) line number.
        // ApplySourceMap should:
        //   - stamp sourceFilePath = "bar.fbasic"
        //   - rewrite sourceLine from concatenated-coords back to in-file coords
        var fileA = @"print ""hello""

test alpha
endtest";
        var fileB = @"



test beta
endtest";

        var map = SourceMap.CreateSourceMap(
            new List<string> { "foo.fbasic", "bar.fbasic" },
            path => path.EndsWith("foo.fbasic") ? fileA.SplitNewLines() : fileB.SplitNewLines());

        // Compile from the concatenated source. The compiler stamps each
        // entry's sourceLine in concatenated coords.
        FadeRuntimeContext.TryFromSource(map.fullSource, TestCommands.CommandsForTesting,
            out var ctx, out var errs, map);
        Assert.That(errs, Is.Null,
            "expected a clean compile; got: " + (errs?.ToDisplay() ?? ""));

        var alpha = ctx.Compiler.TestManifest.First(t => t.name == "alpha");
        var beta  = ctx.Compiler.TestManifest.First(t => t.name == "beta");

        Assert.That(alpha.sourceFilePath, Does.EndWith("foo.fbasic"),
            "alpha lives in foo.fbasic");
        Assert.That(beta.sourceFilePath, Does.EndWith("bar.fbasic"),
            "beta lives in bar.fbasic — the per-entry plumbing is what enables this");
        // After ApplySourceMap, sourceLine is the in-file line, not the
        // concatenated-source line. beta's `test` is on line 4 of bar.fbasic
        // (0-based: 4), not somewhere far down in the concat.
        Assert.That(beta.sourceLine, Is.LessThan(10),
            "beta's sourceLine should be in bar.fbasic-local coordinates");
    }

    [Test]
    public void ApplySourceMap_Idempotent_DoesNotDoubleShift()
    {
        // If a manifest entry has already been remapped (sourceFilePath set),
        // a second call shouldn't move sourceLine again. The build pipeline
        // and the SDK can both reach this code path; the two together must
        // not double-shift.
        var entry = new TestManifestEntry
        {
            name = "foo",
            sourceLine = 3,
            sourceFilePath = "already.fbasic"
        };
        var map = SourceMap.CreateSourceMap(
            new List<string> { "x.fbasic" },
            _ => new[] { "a", "b", "c" });

        LaunchUtil.ApplySourceMap(new[] { entry }, map);

        Assert.That(entry.sourceFilePath, Is.EqualTo("already.fbasic"));
        Assert.That(entry.sourceLine, Is.EqualTo(3),
            "an entry that already carries a source path must not be re-mapped");
    }
}