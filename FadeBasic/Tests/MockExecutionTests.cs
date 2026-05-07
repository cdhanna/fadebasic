using FadeBasic;
using FadeBasic.Sdk;

namespace Tests;

[TestFixture]
public class MockExecutionTests
{
    [SetUp]
    public void Reset()
    {
        TestCommands.waitMsCallCount = 0;
    }

    private FadeRuntimeContext CreateContext(string src)
    {
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting,
            out var ctx, out var errors);
        Assert.That(ok, Is.True,
            "expected clean compile; got: " + (errors == null ? "(null)" : errors.ToDisplay()));
        return ctx;
    }

    [Test]
    public void MockVoid_BareForm_SuppressesRealCall()
    {
        // `mock wait ms` (no body) installs a void mock. The C# WiatMs
        // method should NOT be called, so waitMsCallCount stays at 0.
        var src = @"
checkpoint:
wait ms 50
end

test no_real_wait
    mock wait ms
    runto checkpoint
    wait ms 100
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("no_real_wait");
        Assert.That(result.passed, Is.True, result.failureMessage);
        Assert.That(TestCommands.waitMsCallCount, Is.EqualTo(0),
            "wait ms should have been mocked away both in main-body (via runto) and test body");
    }

    [Test]
    public void MockReturns_OverridesReturnValue()
    {
        var src = @"
end

test mocked_screen_width
    mock screen width returns 42
    assert screen width() = 42
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("mocked_screen_width");
        Assert.That(result.passed, Is.True,
            "expected screen width() to return 42; failure: " + result.failureMessage);
    }

    [Test]
    public void NoMock_RealCommandRuns()
    {
        // Without any mock, screen width returns its default (5 per TestCommands).
        var src = @"
end

test no_mock
    assert screen width() = 5
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("no_mock");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockForbid_FailsTestWhenCommandCalled()
    {
        var src = @"
end

test forbidden
    mock wait ms forbid
    wait ms 1
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("forbidden");
        Assert.That(result.passed, Is.False);
        Assert.That(result.failureMessage, Does.Contain("forbidden"));
        Assert.That(result.failureMessage, Does.Contain("wait ms"));
    }

    [Test]
    public void MockReturns_AppliesToProgramRunByRunto()
    {
        // The mock installs first; then runto drives the program past a call
        // to screen width. The program-side call must also see the mocked value.
        var src = @"
local w as integer
w = screen width()
checkpoint:
end

test mocked_via_runto
    mock screen width returns 99
    runto checkpoint
    assert w = 99
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("mocked_via_runto");
        Assert.That(result.passed, Is.True,
            "program code should observe the mocked value when run via runto; failure: "
            + result.failureMessage);
    }

    [Test]
    public void ClearMock_RestoresRealBehavior()
    {
        var src = @"
end

test clear_mock
    mock screen width returns 42
    assert screen width() = 42
    clear mock screen width
    assert screen width() = 5
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("clear_mock");
        Assert.That(result.passed, Is.True,
            "after `clear mock`, real implementation should run again; failure: "
            + result.failureMessage);
    }

    [Test]
    public void ClearMocks_RemovesAllRegistrations()
    {
        var src = @"
end

test clear_all
    mock screen width returns 42
    mock wait ms
    clear mocks
    assert screen width() = 5
    wait ms 1
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("clear_all");
        Assert.That(result.passed, Is.True, result.failureMessage);
        Assert.That(TestCommands.waitMsCallCount, Is.EqualTo(1),
            "wait ms should have been called once after `clear mocks`");
    }

    [Test]
    public void MockReturns_OnVoidCommand_DegradesToVoid()
    {
        // A user mocking a void command sometimes writes a `returns` body
        // thinking it's required. The compiler should silently treat that
        // as a void mock (no value pushed) rather than corrupting the stack
        // and falling through to the real implementation.
        var src = @"
checkpoint:
wait ms 50
end

test mocked_with_returns
    mock wait ms
        returns 0
    endmock
    runto checkpoint
    wait ms 100
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("mocked_with_returns");
        Assert.That(result.passed, Is.True, result.failureMessage);
        Assert.That(TestCommands.waitMsCallCount, Is.EqualTo(0),
            "wait ms should be fully suppressed even when written as `returns 0`");
    }

    [Test]
    public void MockIsolation_BetweenTestRuns()
    {
        // Each RunTest gets a fresh VM. A mock installed in one test must
        // not affect a sibling test in the same context.
        var src = @"
end

test installs_mock
    mock screen width returns 42
    assert screen width() = 42
endtest

test sees_no_mock
    assert screen width() = 5
endtest
";
        var ctx = CreateContext(src);
        var first = ctx.RunTest("installs_mock");
        var second = ctx.RunTest("sees_no_mock");
        Assert.That(first.passed, Is.True, first.failureMessage);
        Assert.That(second.passed, Is.True,
            "second test must not see the first test's mock; failure: " + second.failureMessage);
    }
}
