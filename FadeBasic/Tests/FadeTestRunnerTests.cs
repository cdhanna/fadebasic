using FadeBasic;
using FadeBasic.Sdk;

namespace Tests;

[TestFixture]
public class FadeTestRunnerTests
{
    private FadeRuntimeContext CreateContext(string src)
    {
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting, out var ctx, out var errors);
        Assert.That(ok, Is.True,
            "expected clean compile; got: " + (errors == null ? "(null)" : errors.ToDisplay()));
        return ctx;
    }

    [Test]
    public void RunTest_PassingTest_Passes()
    {
        var src = @"
end

test foo
    assert 1 = 1
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("foo");
        Assert.That(result.passed, Is.True,
            "expected pass; failure: " + result.failureMessage);
        Assert.That(result.testName, Is.EqualTo("foo"));
        Assert.That(result.failureMessage, Is.Null);
    }

    [Test]
    public void RunTest_FailingAssert_Fails()
    {
        var src = @"
end

test foo
    assert 1 = 2
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("foo");
        Assert.That(result.passed, Is.False);
        Assert.That(result.failureSourceText, Does.Contain("1 = 2"),
            "captured assert text should appear in failure source text; got: "
            + result.failureSourceText);
    }

    [Test]
    public void RunTest_UnknownName_ReturnsFailureResult()
    {
        var src = @"
end

test foo
    assert 1 = 1
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("does_not_exist");
        Assert.That(result.passed, Is.False);
        Assert.That(result.failureMessage, Does.Contain("does_not_exist"));
    }

    [Test]
    public void RunAllTests_MixedResults_CountsCorrect()
    {
        var src = @"
end

test alpha
    assert 1 = 1
endtest

test beta
    assert 1 = 2
endtest

test gamma
    assert 5 > 0
endtest
";
        var ctx = CreateContext(src);
        var run = ctx.RunAllTests();
        Assert.That(run.tests.Count, Is.EqualTo(3));
        Assert.That(run.passedCount, Is.EqualTo(2));
        Assert.That(run.failedCount, Is.EqualTo(1));
        Assert.That(run.AllPassed, Is.False);

        var betaResult = run.tests.First(r => r.testName == "beta");
        Assert.That(betaResult.passed, Is.False);
    }

    [Test]
    public void RunAllTests_AllPassing_AllPassedTrue()
    {
        var src = @"
end

test alpha
    assert 1 = 1
endtest

test beta
    assert 2 = 2
endtest
";
        var ctx = CreateContext(src);
        var run = ctx.RunAllTests();
        Assert.That(run.AllPassed, Is.True);
        Assert.That(run.passedCount, Is.EqualTo(2));
        Assert.That(run.failedCount, Is.EqualTo(0));
    }

    [Test]
    public void Tests_Property_ListsManifestEntries()
    {
        var src = @"
end

test alpha
endtest

test beta
endtest
";
        var ctx = CreateContext(src);
        var names = ctx.Tests.Select(t => t.name).ToList();
        Assert.That(names, Does.Contain("alpha"));
        Assert.That(names, Does.Contain("beta"));
    }

    [Test]
    public void RunTest_CalledTwice_StatePerCallIsIsolated()
    {
        // Each RunTest spins up a fresh VM at the test entry point. Running
        // twice in a row should produce identical results (no leftover state).
        var src = @"
end

test counter
    local n as integer = 0
    n = n + 1
    assert n = 1
endtest
";
        var ctx = CreateContext(src);
        var first = ctx.RunTest("counter");
        var second = ctx.RunTest("counter");
        Assert.That(first.passed, Is.True);
        Assert.That(second.passed, Is.True);
    }

    [Test]
    public void RunTest_AfterRunto_VisibleProgramStateIsAvailable()
    {
        // Verifies the runto path: the program runs up to the label, then the
        // test body asserts on the resulting program state.
        var src = @"
x = 0
x = 42
checkpoint:
end

test usesRunto
    runto checkpoint
    assert x = 42
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("usesRunto");
        Assert.That(result.passed, Is.True,
            "expected pass; failure: " + result.failureMessage);
    }
}
