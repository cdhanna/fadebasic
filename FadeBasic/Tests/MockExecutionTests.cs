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
    public void MockEmpty_SuppressesRealCall()
    {
        // `mock wait ms / endmock` installs a void mock. The C# WaitMs
        // method should NOT be called, so waitMsCallCount stays at 0.
        var src = @"
checkpoint:
wait ms 50
end

test no_real_wait
    mock wait ms
    endmock
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
    mock screen width
        returns 42
    endmock
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
    mock wait ms
        forbid
    endmock
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
    mock screen width
        returns 99
    endmock
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
    mock screen width
        returns 42
    endmock
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
    mock screen width
        returns 42
    endmock
    mock wait ms
    endmock
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
    public void MockForbid_WithReason_CapturesReason()
    {
        var src = @"
end

test forbid_with_reason
    mock wait ms
        forbid ""no waiting in tests""
    endmock
    wait ms 1
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("forbid_with_reason");
        Assert.That(result.passed, Is.False);
        Assert.That(result.failureReason, Is.EqualTo("no waiting in tests"));
        Assert.That(result.failureMessage, Does.Contain("no waiting in tests"),
            "user-supplied reason should surface in the failure message");
    }

    [Test]
    public void MockForbid_RunsDefersOnFailure()
    {
        // Forbid now goes through the assert-unwind trampoline, so defers
        // in every live scope drain before the test runner sees the result.
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
end

test forbid_drains_defers
    defer static print ""cleanup""
    mock wait ms
        forbid
    endmock
    wait ms 1
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("forbid_drains_defers");
        Assert.That(result.passed, Is.False);
        Assert.That(TestCommands.staticPrintBuffer, Is.EqualTo(new[] { "cleanup" }),
            "forbid failure must drain test-scope defers");
    }

    [Test]
    public void MockForbid_CapturesCallStack()
    {
        // Forbid carries a source-located stack like an assert does.
        var src = @"
function trigger()
    wait ms 1
endfunction
trigger()
checkpoint:
end

test forbid_stack
    mock wait ms
        forbid ""nope""
    endmock
    runto checkpoint
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("forbid_stack");
        Assert.That(result.passed, Is.False);
        Assert.That(result.failureFrames, Is.Not.Empty,
            "forbid failure should resolve to source frames when DebugData is present");
        // Innermost frame is inside `trigger()` (where wait ms was called).
        Assert.That(result.failureFrames[0].functionName, Is.EqualTo("trigger"));
    }

    // ── call count <command> ───────────────────────────────────────────────

    [Test]
    public void CallCount_CountsHostInvocations()
    {
        // No mock installed — the real command runs and gets counted. The
        // counter increments on every CALL_HOST in test mode.
        var src = @"
end

test count_real_calls
    wait ms 1
    wait ms 1
    wait ms 1
    assert call count wait ms = 3
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("count_real_calls");
        Assert.That(result.passed, Is.True,
            "expected count=3; failure: " + result.failureMessage);
    }

    [Test]
    public void CallCount_ZeroForUncalledCommand()
    {
        // A command that's never called returns 0. No mock needed; the
        // counter starts empty and the runtime treats missing keys as 0.
        var src = @"
end

test never_called
    assert call count wait ms = 0
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("never_called");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void CallCount_CountsMockedCalls()
    {
        // Mocking doesn't suppress counting — the count includes calls that
        // hit a mock too.
        var src = @"
end

test count_mocked
    mock wait ms
    endmock
    wait ms 1
    wait ms 2
    assert call count wait ms = 2
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("count_mocked");
        Assert.That(result.passed, Is.True,
            "mocked calls should still be counted; failure: " + result.failureMessage);
    }

    [Test]
    public void CallCount_IsolatedBetweenTests()
    {
        // Counts reset per test (each test gets a fresh VM).
        var src = @"
end

test first
    wait ms 1
    assert call count wait ms = 1
endtest

test second
    assert call count wait ms = 0
endtest
";
        var ctx = CreateContext(src);
        var first = ctx.RunTest("first");
        var second = ctx.RunTest("second");
        Assert.That(first.passed, Is.True, first.failureMessage);
        Assert.That(second.passed, Is.True,
            "second test must see count=0; failure: " + second.failureMessage);
    }

    [Test]
    public void MockIsolation_BetweenTestRuns()
    {
        // Each RunTest gets a fresh VM. A mock installed in one test must
        // not affect a sibling test in the same context.
        var src = @"
end

test installs_mock
    mock screen width
        returns 42
    endmock
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
