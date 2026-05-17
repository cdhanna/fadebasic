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
        exitmock 42
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
        exitmock 99
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
        exitmock 42
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
        exitmock 42
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
        // Phase B: the body itself is a dispatched bytecode block, so the
        // innermost frame is the mock body (named after the command). The
        // frame immediately below shows where the forbidden call originated
        // — `trigger()` in this case.
        Assert.That(result.failureFrames.Count, Is.GreaterThanOrEqualTo(2),
            "expected at least mock-body frame + caller frame");
        Assert.That(result.failureFrames[0].functionName, Is.EqualTo("wait ms"),
            "innermost frame is the mock body, named after the mocked command");
        Assert.That(result.failureFrames[1].functionName, Is.EqualTo("trigger"),
            "frame below the mock body shows where the forbidden call originated");
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

    // ── Phase B: mock body as mini-function ────────────────────────────────

    [Test]
    public void MockBody_RunsStatementsAtCallTime()
    {
        // The body executes every time the mocked command is called, not at
        // install time. We use the host-side staticPrintBuffer to observe.
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
end

test body_runs
    mock screen width
        static print ""called""
        exitmock 7
    endmock
    local w as integer = screen width()
    local w2 as integer = screen width()
    assert w = 7
    assert w2 = 7
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("body_runs");
        Assert.That(result.passed, Is.True, result.failureMessage);
        Assert.That(TestCommands.staticPrintBuffer, Is.EqualTo(new[] { "called", "called" }),
            "body should run once per call, not at install time");
    }

    [Test]
    public void MockBody_LocalAndIf_Work()
    {
        // Arbitrary test-block statements (local, if/then) inside a body.
        var src = @"
end

test body_with_local
    mock screen width
        local result as integer
        result = 100
        if result > 50 then result = 99
        exitmock result
    endmock
    assert screen width() = 99
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("body_with_local");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_ParamsBoundToArgs()
    {
        // `mock <cmd> <param>` binds the command's arg to a local named
        // <param> inside the body. The body can read it to compute a return
        // value based on the input.
        var src = @"
end

test param_binding
    mock prim test di n
        exitmock n * 3
    endmock
    local x as long = prim test di(5)
    assert x = 15
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("param_binding");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    // ── Ref-arg writes inside a mock body ──────────────────────────────────

    [Test]
    public void MockBody_RefArgWrite_BackToCaller()
    {
        // `inc` takes `(ref int variable, int amount = 1)`. A mock body that
        // names the ref param and writes to it via plain assignment should
        // mutate the caller's variable.
        var src = @"
end

test ref_write
    mock inc target, amount
        target = 99
    endmock
    local x as integer = 5
    inc x, 1
    assert x = 99
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("ref_write");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_RefArg_CoercesType()
    {
        // `target = 200` writes an int literal through a ref to an integer;
        // the same coercion rules as `local n as long = 5` apply.
        var src = @"
end

test ref_coerce
    mock inc target, amount
        target = 200
    endmock
    local x as integer
    x = 0
    inc x, 1
    assert x = 200
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("ref_coerce");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_EndmockWithExpression_ReturnsValue()
    {
        // `endmock <expr>` provides the fall-through return value, mirroring
        // `endfunction <expr>` for functions. No `exitmock` needed for the
        // simple case.
        var src = @"
end

test endmock_expr
    mock screen width
    endmock 7
    assert screen width() = 7
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("endmock_expr");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_ExitmockEarlyExit_OverridesEndmock()
    {
        // `exitmock` is an early return. If hit, the fall-through
        // `endmock <expr>` is bypassed.
        var src = @"
end

test exitmock_short_circuit
    mock screen width
        exitmock 100
    endmock 200
    assert screen width() = 100
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("exitmock_short_circuit");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_RefParamReadsInitialCallerValue()
    {
        // The body's value-register for a ref param is seeded from the
        // caller's variable at body entry, so `static print target` shows
        // whatever the caller passed in — not the pointer bytes.
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
end

test ref_read_initial
    mock inc target, amount
        static print str$(target)
        target = 99
    endmock
    local x as integer = 5
    inc x, 1
    assert x = 99
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("ref_read_initial");
        Assert.That(result.passed, Is.True, result.failureMessage);
        Assert.That(TestCommands.staticPrintBuffer, Is.EqualTo(new[] { "5" }),
            "body should read the caller's pre-call value through the ref param");
    }

    [Test]
    public void MockBody_RefParamReadsAfterWrite()
    {
        // After the body assigns `target`, subsequent reads of `target`
        // inside the body see the new value (it's a normal local).
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
end

test ref_read_after_write
    mock inc target, amount
        target = 99
        static print str$(target)
    endmock
    local x as integer = 5
    inc x, 1
    assert x = 99
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("ref_read_after_write");
        Assert.That(result.passed, Is.True, result.failureMessage);
        Assert.That(TestCommands.staticPrintBuffer, Is.EqualTo(new[] { "99" }),
            "body should read its own latest write to the ref param");
    }

    // ── Params arg gathered into a Fade array inside a mock body ──────────

    [Test]
    public void MockBody_ParamsArg_LenReturnsCount()
    {
        // `sum(params int[] numbers)` — a mock body that names the params
        // arg should receive it as a Fade array. `len(nums)` returns the
        // count the caller passed.
        var src = @"
end

test params_len
    mock sum(nums)
    endmock len(nums)
    assert sum(10, 20, 30) = 3
    assert sum() = 0
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("params_len");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_ParamsArg_IndexedAccess()
    {
        // The body can read individual elements by index. Returning the
        // first element proves indexing works.
        var src = @"
end

test params_index
    mock sum(nums)
    endmock nums(0)
    assert sum(42, 100, 7) = 42
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("params_index");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_ParamsArg_SumViaIteration()
    {
        // Sum the elements via for/len inside the body. Verifies the
        // gathered array round-trips length + indexing + control flow.
        var src = @"
end

test params_sum_via_iter
    mock sum(nums)
        total = 0
        for i = 0 to len(nums) - 1
            total = total + nums(i)
        next i
    endmock total
    assert sum(1, 2, 3, 4) = 10
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("params_sum_via_iter");
        Assert.That(result.passed, Is.True, result.failureMessage);
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
        exitmock 42
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

    // ── Self-recursive call: mocked command name inside body → real ────────

    [Test]
    public void MockBody_SelfCall_VoidCommand_RunsRealCommand()
    {
        // Inside the mock for `inc`, writing `inc target, amount` calls
        // the real underlying C# Inc rather than recursing into the mock.
        var src = @"
end

test selfcall_void
    mock inc target, amount
        inc target, amount
    endmock
    local x as integer = 10
    inc x, 5
    assert x = 15
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("selfcall_void");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_SelfCall_ReturningCommand_CapturesValue()
    {
        // `screen width()` returns 5 in TestCommands. Inside the mock, the
        // same expression invokes the real command.
        var src = @"
end

test selfcall_return
    mock screen width
        real_width = screen width()
        exitmock real_width + 100
    endmock
    assert screen width() = 105
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("selfcall_return");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_SelfCall_RefArgFlushesUserChangeThenRealRuns()
    {
        // User writes `target = 100` then self-calls. The compiler flushes
        // the value-reg through the hidden ptr first, so the real Inc reads
        // 100 and adds 1 → caller's x = 101.
        var src = @"
end

test selfcall_ref_flush
    mock inc target, amount
        target = 100
        inc target, amount
    endmock
    local x as integer = 0
    inc x, 1
    assert x = 101
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("selfcall_ref_flush");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_SelfCall_RefRefreshesLocalAfterCall()
    {
        // After the self-call runs the real Inc, the body's `target` value
        // reg is refreshed from the caller — subsequent reads observe the
        // real output. A trailing user write to `target` still wins at exit.
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
end

test selfcall_ref_refresh
    mock inc target, amount
        inc target, amount
        static print str$(target)
        target = 999
    endmock
    local x as integer = 10
    inc x, 5
    assert x = 999
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("selfcall_ref_refresh");
        Assert.That(result.passed, Is.True, result.failureMessage);
        Assert.That(TestCommands.staticPrintBuffer, Is.EqualTo(new[] { "15" }),
            "after the self-call the body should observe the real-Inc result (10+5)");
    }

    [Test]
    public void MockBody_SelfCall_ModifiedValueArg()
    {
        // The user supplies any expression at value positions. Here the
        // mock calls the real Inc with a doubled amount.
        var src = @"
end

test selfcall_modified_value
    mock inc target, amount
        inc target, amount * 2
    endmock
    local x as integer = 0
    inc x, 5
    assert x = 10
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("selfcall_modified_value");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_SelfCall_LiteralOverride()
    {
        // The mock ignores the user's `amount` entirely, calling the real
        // Inc with a hard-coded literal.
        var src = @"
end

test selfcall_literal
    mock inc target, amount
        inc target, 100
    endmock
    local x as integer = 0
    inc x, 5
    assert x = 100
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("selfcall_literal");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_SelfCall_ParamsArgSpread()
    {
        // The body owns the gathered array as `nums`. Passing it as the
        // sole arg at the params position spreads it through to the real
        // sum, which sums the mutated values.
        var src = @"
end

test selfcall_params_spread
    mock sum(nums)
        for i = 0 to len(nums) - 1
            nums(i) = nums(i) * 10
        next i
        exitmock sum(nums)
    endmock
    assert sum(1, 2, 3) = 60
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("selfcall_params_spread");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_SelfCall_RefArgMustBeBoundParam()
    {
        // A self-recursive call must pass one of the mock's bound ref
        // params at each ref position. A body-local int would yield a
        // PTR_REG into the body's scope, which the scope swap in
        // CALL_HOST_REAL turns into a write to the wrong cell.
        var src = @"
end

test bad_selfcall_ref
    mock inc target, amount
        local fake as integer
        inc fake, amount
    endmock
endtest
";
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting,
            out _, out var errors);
        Assert.That(ok, Is.False, "expected compile failure when ref arg isn't a bound ref param");
        Assert.That(errors.ParserErrors.Any(
                e => e.errorCode.Equals(ErrorCodes.MockBodyRefArgMustBeBoundRefParam)),
            Is.True,
            "expected MockBodyRefArgMustBeBoundRefParam; got: " + errors.ToDisplay());
    }

    [Test]
    public void MockBody_ParamsObjectArray_NamingFails_Cleanly()
    {
        // `static print` is `params object[]`. Naming the params slot
        // (e.g. `mock static print(args)`) requires mixed-type element
        // storage that the body array model doesn't support — surface a
        // clean error rather than crashing the compiler on SIZE_TABLE[ANY].
        var src = @"
end

test bad_params_object_named
    mock static print(args)
    endmock
endtest
";
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting,
            out _, out var errors);
        Assert.That(ok, Is.False,
            "expected compile failure on named params object[] slot");
        var match = errors.ParserErrors.FirstOrDefault(
            e => e.errorCode.Equals(ErrorCodes.MockParamsObjectArrayUnnamable));
        Assert.That(match, Is.Not.Null,
            "expected MockParamsObjectArrayUnnamable; got: " + errors.ToDisplay());
        // The site-specific detail names the offending param, the command
        // being mocked, and the rewrite the user should reach for.
        Assert.That(match.message, Does.Contain("args"),
            "error should name the param the user tried to bind");
        Assert.That(match.message, Does.Contain("static print"),
            "error should name the command being mocked");
        Assert.That(match.message, Does.Contain("params object[]"),
            "error should call out the param shape causing the limitation");
        Assert.That(match.message, Does.Contain("mock static print"),
            "error should show the rewrite (mock with no param name)");
    }

    [Test]
    public void MockBody_ParamsObjectArray_UnnamedFormCompiles()
    {
        // The workaround: don't name the params slot. The mock still
        // installs and the real call is suppressed/handled.
        var src = @"
end

test params_object_unnamed
    mock static print
    endmock
    static print ""a"", ""b""
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("params_object_unnamed");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void MockBody_SelfCall_SatisfiesRefAssignedCheck()
    {
        // A bare self-call writes through every ref it's handed, so the
        // mock body doesn't need a separate `target = ...` assignment.
        var src = @"
end

test selfcall_satisfies_ref_check
    mock inc target, amount
        inc target, amount
    endmock
    local x as integer = 3
    inc x, 4
    assert x = 7
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("selfcall_satisfies_ref_check");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }
}
