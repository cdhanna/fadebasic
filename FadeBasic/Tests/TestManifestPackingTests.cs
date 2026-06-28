using FadeBasic;
using FadeBasic.Launch;
using FadeBasic.Sdk;
using FadeBasic.Virtual;

namespace Tests;

[TestFixture]
public class TestManifestPackingTests
{
    [Test]
    public void PackUnpack_RoundTrips_PreservesEntries()
    {
        var src = @"
end

test alpha
    assert 1 = 1
endtest

abstract test fixture
endtest

test gamma from fixture
endtest
";
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting,
            out var ctx, out var errs);
        Assert.That(ok, Is.True, errs?.ToDisplay());

        var packed = LaunchUtil.PackTestManifest(ctx.Compiler.TestManifest);
        Assert.That(packed, Is.Not.Empty);

        var unpacked = LaunchUtil.UnpackTestManifest(packed);
        Assert.That(unpacked.Count, Is.EqualTo(ctx.Compiler.TestManifest.Count));

        var alphaOriginal = ctx.Compiler.TestManifest.First(t => t.name == "alpha");
        var alphaUnpacked = unpacked.First(t => t.name == "alpha");
        Assert.That(alphaUnpacked.entryPointAddress, Is.EqualTo(alphaOriginal.entryPointAddress));
        Assert.That(alphaUnpacked.isAbstract, Is.False);

        var fixtureUnpacked = unpacked.First(t => t.name == "fixture");
        Assert.That(fixtureUnpacked.isAbstract, Is.True);

        var gammaUnpacked = unpacked.First(t => t.name == "gamma");
        Assert.That(gammaUnpacked.fromParent, Is.EqualTo("fixture"));
    }

    [Test]
    public void PackUnpack_EmptyManifest_RoundTrips()
    {
        var empty = new List<TestManifestEntry>();
        var packed = LaunchUtil.PackTestManifest(empty);
        var unpacked = LaunchUtil.UnpackTestManifest(packed);
        Assert.That(unpacked.Count, Is.EqualTo(0));
    }

    [Test]
    public void Unpack_NullOrEmpty_ReturnsEmptyList()
    {
        Assert.That(LaunchUtil.UnpackTestManifest(null).Count, Is.EqualTo(0));
        Assert.That(LaunchUtil.UnpackTestManifest("").Count, Is.EqualTo(0));
    }

    // End-to-end smoke: simulate what a generated launchable does.
    // 1) Pack manifest + bytecode (compile-time analogue).
    // 2) Construct a synthetic ITestLaunchable from the unpacked artifacts.
    // 3) Dispatch a `--fade-test=name` via Launcher and verify it runs.
    [Test]
    public void GeneratedLaunchableShape_DispatchesTestArgs()
    {
        var src = @"
end

test alpha
    assert 1 = 1
endtest
";
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting,
            out var ctx, out _);
        Assert.That(ok, Is.True);

        // Pack: same operations LaunchableGenerator performs at build time.
        var packedBytecode = LaunchUtil.Pack64(ctx.Machine.program);
        var packedManifest = LaunchUtil.PackTestManifest(ctx.Compiler.TestManifest);

        // Unpack: same operations the generated class performs at startup.
        var bytecode = LaunchUtil.Unpack64(packedBytecode);
        var manifest = LaunchUtil.UnpackTestManifest(packedManifest);

        var launchable = new SyntheticTestLaunchable
        {
            bytecode = bytecode,
            collection = TestCommands.CommandsForTesting,
            manifest = manifest
        };

        var stdout = new StringWriter();
        var savedOut = Console.Out;
        try
        {
            Console.SetOut(stdout);
            var handled = Launcher.TryDispatchTestArgs(launchable,
                new[] { "--fade-test=alpha" }, out var exit);
            Assert.That(handled, Is.True);
            Assert.That(exit, Is.EqualTo(0),
                "expected pass; stdout: " + stdout);
        }
        finally
        {
            Console.SetOut(savedOut);
        }

        Assert.That(stdout.ToString(), Does.Contain("PASS"));
    }

    private class SyntheticTestLaunchable : ITestLaunchable
    {
        public byte[] bytecode;
        public CommandCollection collection;
        public IReadOnlyList<TestManifestEntry> manifest;

        public byte[] Bytecode => bytecode;
        public CommandCollection CommandCollection => collection;
        public DebugData DebugData => null;
        public IReadOnlyList<TestManifestEntry> TestManifest => manifest;
    }
}
