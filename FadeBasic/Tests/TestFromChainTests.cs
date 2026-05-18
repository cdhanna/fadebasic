using FadeBasic;
using FadeBasic.Sdk;

namespace Tests;

[TestFixture]
public class TestFromChainTests
{
    // ── End-to-end runtime tests via the SDK runner ────────────────────────

    private FadeRuntimeContext CreateContext(string src)
    {
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting,
            out var ctx, out var errors);
        Assert.That(ok, Is.True,
            "expected clean compile; got: " + (errors == null ? "(null)" : errors.ToDisplay()));
        return ctx;
    }

    [Test]
    public void FromChain_ChildSeesParentsRuntoState()
    {
        // The motivating case: child references a main-body variable that
        // parent brought into view via runto. Without inheritance, this
        // errors at the visitor and crashes at runtime; with the chain
        // launcher, parent's runto runs first and `x` is in registers
        // before child's assert reads it.
        var src = @"
x = 3
_L1:
end

test sample
    runto _L1
    assert x = 3
endtest

test sample2 from sample
    assert x = 3
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("sample2");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void FromChain_ChildSeesParentMainBodyAssignment()
    {
        // Parent's runto brings a main-body variable into view; child
        // reads it. Tests static visibility (visitor) + runtime persistence
        // (shared registers) end-to-end.
        var src = @"
foo = 42
_L:
end

test parent
    runto _L
endtest

test child from parent
    assert foo = 42
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("child");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void FromChain_ChildSeesParentTestLocal()
    {
        // Parent declares a test-local and assigns to it. Child references
        // it directly. The base scope checker now walks chained tests in
        // topological order, copying parent's scope state (locals + funcs)
        // into the child's fresh scope before validating — the same way
        // a test's sub-program already inherits from the outer program.
        var src = @"
end

test parent
    local foo as integer
    foo = 42
endtest

test child from parent
    assert foo = 42
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("child");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void FromChain_ChildInheritsMocksInstalledByParent()
    {
        // Parent installs a mock that overrides `screen width` to return
        // 42. Child references `screen width()` directly. The mock survives
        // into the child run because it lives in the VM's mockTable, which
        // is wholly shared across chain segments.
        var src = @"
end

test parent
    mock screen width
        exitmock 42
    endmock
endtest

test child from parent
    assert screen width() = 42
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("child");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void FromChain_ChildSeesParentRuntoSideEffects()
    {
        // Parent's `runto` causes the main program to execute up to the
        // label, including this `inc` call which writes to register `n`.
        // Child should see n = 1 because parent's runto ran before child's
        // body started.
        var src = @"
n = 0
inc n
_L1:
end

test parent
    runto _L1
endtest

test child from parent
    assert n = 1
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("child");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void FromChain_ParentAssertFailure_CascadesToChild()
    {
        // Parent fails an assert. The trampoline halts the VM mid-chain.
        // Child's body never runs; the child run is reported as failed.
        var src = @"
end

test parent
    assert 0, ""parent always fails""
endtest

test child from parent
    assert 1 = 1
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("child");
        Assert.That(result.passed, Is.False,
            "child should fail because parent's assert failed");
        Assert.That(result.failureMessage, Does.Contain("parent always fails"),
            "failure should propagate parent's reason; got: " + result.failureMessage);
    }

    [Test]
    public void FromChain_ThreeLevelChain_RunsAllInOrder()
    {
        // A → B → C. Each ancestor mutates a register; the final assert
        // proves all three segments ran in order, sharing state.
        var src = @"
n = 0
inc n
_L1:
end

test a
    runto _L1
endtest

test b from a
    n = n + 10
endtest

test c from b
    n = n + 100
    assert n = 111
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("c");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void FromChain_StandaloneParent_StillRunsAlone()
    {
        // Running parent directly still works — its launcher is just
        // [GOSUB self_body, HALT] with no ancestor links. Child's launcher
        // points to a different address with the chain.
        var src = @"
n = 5
_L:
end

test parent
    runto _L
    assert n = 5
endtest

test child from parent
    assert n = 5
endtest
";
        var ctx = CreateContext(src);
        var parentResult = ctx.RunTest("parent");
        Assert.That(parentResult.passed, Is.True, parentResult.failureMessage);
        var childResult = ctx.RunTest("child");
        Assert.That(childResult.passed, Is.True, childResult.failureMessage);
    }

    [Test]
    public void FromChain_AbstractParent_NotInRunnableList_ButInherited()
    {
        // Abstract tests aren't runnable directly but their body still runs
        // as part of a child's chain. The manifest flags it isAbstract;
        // the runner skips it for top-level execution but the launcher
        // GOSUBs into its body all the same.
        var src = @"
x = 100
_L:
end

abstract test setup
    runto _L
endtest

test concrete from setup
    assert x = 100
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("concrete");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    [Test]
    public void FromChain_TwoSiblings_IsolatedAtStatic_ButShareRuntimeRegisters()
    {
        // Both `childA` and `childB` inherit from `parent`. Each gets its own
        // fresh Scope at validation time (no leakage between siblings — the
        // scope copy is one-way, parent → child). Runtime-wise they share
        // the same global register file, so a sibling's locals collide if
        // they have the same name — but that's already how cross-test state
        // works in the existing language and isn't specific to chains.
        //
        // The test demonstrates static isolation: childA declares a local
        // `siblingA_only`; childB declares `siblingB_only`. Neither sibling
        // sees the other's local. (If isolation were broken, the visitor
        // would let `siblingA_only` slip into childB and either spuriously
        // accept it or alias to a parent-declared name.)
        var src = @"
end

test parent
    local shared as integer = 5
endtest

test childA from parent
    local siblingA_only as integer = 1
    assert shared = 5
    assert siblingA_only = 1
endtest

test childB from parent
    local siblingB_only as integer = 2
    assert shared = 5
    assert siblingB_only = 2
endtest
";
        var ctx = CreateContext(src);
        var ra = ctx.RunTest("childA");
        Assert.That(ra.passed, Is.True, ra.failureMessage);
        var rb = ctx.RunTest("childB");
        Assert.That(rb.passed, Is.True, rb.failureMessage);
    }

    [Test]
    public void FromChain_SiblingCannotSeeOtherSiblingsLocal()
    {
        // Static isolation check: childA declares `priv` as a local; childB
        // shouldn't see it. If sibling state were leaking (e.g. via shared
        // scope mutation during validation), childB's reference to `priv`
        // would spuriously succeed.
        var src = @"
end

test parent
endtest

test childA from parent
    local priv as integer = 1
endtest

test childB from parent
    n = priv
endtest
";
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting,
            out _, out var errors);
        Assert.That(ok, Is.False,
            "childB should NOT see childA's local — they're siblings, not parent/child");
        Assert.That(errors.ParserErrors.Any(e => e.Display.Contains("priv")),
            Is.True,
            "expected an unknown-symbol error mentioning `priv`; got: " + errors.ToDisplay());
    }

    [Test]
    public void FromChain_ParentDefer_RunsAfterChildBody()
    {
        // The motivating bug. Parent registers a DEFER (teardown) inside
        // its body. With per-body defer drains, teardown fires at parent's
        // RETURN — i.e., BEFORE the child runs — which is semantically
        // wrong (child is supposed to be a continuation of parent).
        //
        // Expected order: child body runs first, THEN parent's defer.
        // The shared deferredJumps stack accumulates parent's defer
        // during the chain; the launcher's tail drains it after every
        // body has run.
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
counter = 0
_L1:
do
    counter = counter + 1
    _L2:
loop

abstract test parent
    defer
        static print ""teardown""
    enddefer
endtest

test sample from parent
    runto _L1

    while counter < 3
        static print ""looping""
        runto _L2
    endwhile

    static print str$(counter)
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("sample");
        Assert.That(result.passed, Is.True, result.failureMessage);
        Assert.That(TestCommands.staticPrintBuffer,
            Is.EqualTo(new[] { "looping", "looping", "looping", "3", "teardown" }),
            "parent's defer should fire AFTER the child's body, not before");
    }

    [Test]
    public void FromChain_TwoLevelDefers_LIFOAcrossChain()
    {
        // Parent A and parent B both register defers. C runs in the
        // middle. At chain end, defers drain LIFO — B's first, then A's.
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
end

abstract test a
    defer
        static print ""a_teardown""
    enddefer
endtest

abstract test b from a
    defer
        static print ""b_teardown""
    enddefer
endtest

test c from b
    static print ""body""
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("c");
        Assert.That(result.passed, Is.True, result.failureMessage);
        Assert.That(TestCommands.staticPrintBuffer,
            Is.EqualTo(new[] { "body", "b_teardown", "a_teardown" }),
            "defers should drain LIFO at chain end: child body, then b's defer, then a's defer");
    }

    [Test]
    public void FromChain_StandaloneDefer_StillDrainsAtTestEnd()
    {
        // No `from`-parent — just a normal test with a defer. The defer
        // should still fire when the test body completes (after the
        // body's other statements). Confirms the launcher-tail drain
        // works for the simple case.
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
end

test solo
    defer
        static print ""teardown""
    enddefer
    static print ""body""
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("solo");
        Assert.That(result.passed, Is.True, result.failureMessage);
        Assert.That(TestCommands.staticPrintBuffer,
            Is.EqualTo(new[] { "body", "teardown" }),
            "standalone defer should fire after body — same as before");
    }

    [Test]
    public void LabelScoping_SameLabelInTwoTests_DoesNotCollide()
    {
        // Bug repro: each test's `retry_done` label was sharing a global
        // dictionary entry, so test alpha's `goto retry_done` resolved to
        // beta's label and execution fell into beta's `assert 0`.
        // Both tests should resolve their labels independently — alpha
        // passes, beta fails as intended.
        var src = @"
end

test alpha
retry:
    goto retry_done
retry_done:
endtest

test beta
retry:
    goto retry_done
retry_done:
    assert 0, ""boooo""
endtest
";
        var ctx = CreateContext(src);
        var alpha = ctx.RunTest("alpha");
        Assert.That(alpha.passed, Is.True,
            "alpha has no failing assert — should pass; got: " + alpha.failureMessage);
        var beta = ctx.RunTest("beta");
        Assert.That(beta.passed, Is.False,
            "beta's assert 0 must fail when its OWN label was reached");
        Assert.That(beta.failureMessage, Does.Contain("boooo"),
            "beta should fail with its own message; got: " + beta.failureMessage);
    }

    [Test]
    public void LabelScoping_RuntoFromTest_StillFindsMainBodyLabel()
    {
        // Sanity check: even though labels are region-scoped, runto from
        // a test resolves against the main-body region. Otherwise the
        // visitor would flag it and the compiler would fail to bake the
        // runto target's address.
        var src = @"
n = 0
_pause:
n = n + 5
end

test foo
    runto _pause
    assert n = 0
endtest
";
        var ctx = CreateContext(src);
        var result = ctx.RunTest("foo");
        Assert.That(result.passed, Is.True, result.failureMessage);
    }

    // ── Compile-time validation: cycles and unknown parents ────────────────

    [Test]
    public void FromChain_UnknownParent_Errors()
    {
        var src = @"
end

test child from nonexistent
    assert 1 = 1
endtest
";
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting,
            out _, out var errors);
        Assert.That(ok, Is.False,
            "expected compile failure when fromParent doesn't name a test");
        Assert.That(errors.ParserErrors.Any(
                e => e.errorCode.Equals(ErrorCodes.TestFromParentUnknown)),
            Is.True,
            "expected TestFromParentUnknown; got: " + errors.ToDisplay());
    }

    [Test]
    public void FromChain_DirectCycle_Errors()
    {
        // `selfref` from itself — simplest self-cycle. (Avoiding `loop`
        // as a test name because it collides with the do/loop keyword.)
        var src = @"
end

test selfref from selfref
    assert 1 = 1
endtest
";
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting,
            out _, out var errors);
        Assert.That(ok, Is.False,
            "expected compile failure on self-from cycle");
        Assert.That(errors.ParserErrors.Any(
                e => e.errorCode.Equals(ErrorCodes.TestFromParentCycle)),
            Is.True,
            "expected TestFromParentCycle; got: " + errors.ToDisplay());
    }

    [Test]
    public void TestNames_DuplicateAcrossTopLevel_Errors()
    {
        // Two tests sharing a name confuse the runner's manifest lookup
        // and obscure intent. Surface a clean compile error at the second
        // (and any further) occurrence; the first one keeps the name.
        var src = @"
end

test N
    assert 1
endtest

test N
    assert 0
endtest
";
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting,
            out _, out var errors);
        Assert.That(ok, Is.False,
            "expected compile failure when two tests share a name");
        var dupes = errors.ParserErrors
            .Where(e => e.errorCode.Equals(ErrorCodes.TestDuplicateName))
            .ToList();
        Assert.That(dupes, Has.Count.EqualTo(1),
            "exactly one duplicate flagged (first occurrence keeps the name); got: "
            + errors.ToDisplay());
        Assert.That(dupes[0].message, Does.Contain("N"),
            "error detail should name the offending test; got: " + dupes[0].Display);
    }

    [Test]
    public void TestNames_DuplicateCaseInsensitive_Errors()
    {
        // Lookups (FindTestByName, runner manifest) are case-insensitive,
        // so `test Foo` + `test foo` collide just as much as two `Foo`s.
        var src = @"
end

test Foo
    assert 1
endtest

test foo
    assert 1
endtest
";
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting,
            out _, out var errors);
        Assert.That(ok, Is.False,
            "expected compile failure on case-insensitive duplicate names");
        Assert.That(errors.ParserErrors.Any(
                e => e.errorCode.Equals(ErrorCodes.TestDuplicateName)),
            Is.True,
            "expected TestDuplicateName; got: " + errors.ToDisplay());
    }

    [Test]
    public void FromChain_IndirectCycle_Errors()
    {
        // A from B, B from C, C from A — three-node cycle.
        var src = @"
end

test a from c
    assert 1 = 1
endtest

test b from a
    assert 1 = 1
endtest

test c from b
    assert 1 = 1
endtest
";
        var ok = Fade.TryCreateFromString(src, TestCommands.CommandsForTesting,
            out _, out var errors);
        Assert.That(ok, Is.False,
            "expected compile failure on indirect cycle");
        Assert.That(errors.ParserErrors.Any(
                e => e.errorCode.Equals(ErrorCodes.TestFromParentCycle)),
            Is.True,
            "expected TestFromParentCycle; got: " + errors.ToDisplay());
    }
}
