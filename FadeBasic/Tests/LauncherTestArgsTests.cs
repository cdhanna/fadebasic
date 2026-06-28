using FadeBasic;
using FadeBasic.Launch;
using FadeBasic.Sdk;

namespace Tests;

[TestFixture]
public class LauncherTestArgsTests
{
    private FadeRuntimeContext CreateContext(string src)
    {
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting, out var ctx, out var errors);
        Assert.That(ok, Is.True,
            "expected clean compile; got: " + (errors == null ? "(null)" : errors.ToDisplay()));
        return ctx;
    }

    private (int exit, string stdout, string stderr) DispatchWithCapture(ITestLaunchable launchable, string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var savedOut = Console.Out;
        var savedErr = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var handled = Launcher.TryDispatchTestArgs(launchable, args, out var exit);
            Assert.That(handled, Is.True, "expected test args to be handled");
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
    }

    [Test]
    public void Dispatch_FadeListTests_PrintsManifestAndReturnsZero()
    {
        var src = @"
end

test alpha
endtest

test beta
endtest

abstract test fixture
endtest
";
        var ctx = CreateContext(src);
        var (exit, stdout, _) = DispatchWithCapture(ctx, new[] { "--fade-list-tests" });
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(stdout, Does.Contain("alpha"));
        Assert.That(stdout, Does.Contain("beta"));
        Assert.That(stdout, Does.Not.Contain("fixture"),
            "abstract tests should not appear in --fade-list-tests output");
    }

    [Test]
    public void Dispatch_FadeTestEqualsName_RunsSingleTest_ExitsZero()
    {
        var src = @"
end

test alpha
    assert 1 = 1
endtest
";
        var ctx = CreateContext(src);
        var (exit, stdout, _) = DispatchWithCapture(ctx, new[] { "--fade-test=alpha" });
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(stdout, Does.Contain("PASS"));
        Assert.That(stdout, Does.Contain("alpha"));
    }

    [Test]
    public void Dispatch_FadeTestSpaceName_AlsoSupported()
    {
        var src = @"
end

test alpha
    assert 1 = 1
endtest
";
        var ctx = CreateContext(src);
        var (exit, _, _) = DispatchWithCapture(ctx, new[] { "--fade-test", "alpha" });
        Assert.That(exit, Is.EqualTo(0));
    }

    [Test]
    public void Dispatch_FadeTest_FailingTest_ExitsOne()
    {
        var src = @"
end

test broken
    assert 1 = 2
endtest
";
        var ctx = CreateContext(src);
        var (exit, stdout, _) = DispatchWithCapture(ctx, new[] { "--fade-test=broken" });
        Assert.That(exit, Is.EqualTo(1));
        Assert.That(stdout, Does.Contain("FAIL"));
    }

    [Test]
    public void Dispatch_FadeTestAll_RunsAllTests()
    {
        var src = @"
end

test passes
    assert 1 = 1
endtest

test fails
    assert 1 = 2
endtest
";
        var ctx = CreateContext(src);
        var (exit, stdout, _) = DispatchWithCapture(ctx, new[] { "--fade-test-all" });
        Assert.That(exit, Is.EqualTo(1), "any failure should produce exit code 1");
        Assert.That(stdout, Does.Contain("PASS"));
        Assert.That(stdout, Does.Contain("FAIL"));
        Assert.That(stdout, Does.Contain("1 passed"));
        Assert.That(stdout, Does.Contain("1 failed"));
    }

    [Test]
    public void Dispatch_FadeTestAll_AllPassing_ExitZero()
    {
        var src = @"
end

test a
    assert 1 = 1
endtest

test b
    assert 2 = 2
endtest
";
        var ctx = CreateContext(src);
        var (exit, _, _) = DispatchWithCapture(ctx, new[] { "--fade-test-all" });
        Assert.That(exit, Is.EqualTo(0));
    }

    [Test]
    public void Dispatch_UnknownTestName_ExitsOne()
    {
        var src = @"
end

test foo
endtest
";
        var ctx = CreateContext(src);
        var (exit, _, stderr) = DispatchWithCapture(ctx, new[] { "--fade-test=does_not_exist" });
        Assert.That(exit, Is.EqualTo(1));
        Assert.That(stderr, Does.Contain("does_not_exist"));
    }

    [Test]
    public void Dispatch_NoTestArgs_ReturnsFalse()
    {
        var src = @"
end

test foo
endtest
";
        var ctx = CreateContext(src);
        var handled = Launcher.TryDispatchTestArgs(ctx, new[] { "--something-else" }, out var exit);
        Assert.That(handled, Is.False,
            "non-test args should NOT be handled by the test dispatcher");
    }
}
