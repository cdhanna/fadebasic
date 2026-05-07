using FadeBasic;
using FadeBasic.Ast;

namespace Tests;

[TestFixture]
public class TestScopeStrictnessTests
{
    private ProgramNode Parse(string src, out List<ParseError> errors)
    {
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        lex.AssertNoLexErrors();
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        errors = prog.GetAllErrors();
        return prog;
    }

    [Test]
    public void Strictness_PreRunto_MainBodyName_Errors()
    {
        // `x` is declared by main-body assignment. Pre-runto, it should not be
        // visible to the test.
        var src = @"
x = 5
end

test foo
    assert x = 5
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableUnreachable)),
            Is.True,
            "expected TestVariableUnreachable; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_PostRunto_NameDeclaredAfterTarget_Errors()
    {
        // The runto reaches `:earlyLabel`. `y` is declared AFTER that point in
        // main-body — should not be visible from this runto.
        var src = @"
x = 5
earlyLabel:
y = 10
end

test foo
    runto earlyLabel
    assert y = 10
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.True,
            "expected TestVariableNotYetDeclared for y; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_PostRunto_NameDeclaredBeforeTarget_Allowed()
    {
        // `x` is declared before the label, so it's visible after runto.
        var src = @"
x = 5
laterLabel:
end

test foo
    runto laterLabel
    assert x = 5
endtest
";
        Parse(src, out var errs);
        // This should pass with no scope-strictness errors (other errors might
        // exist from the main check, but our strict checks shouldn't fire).
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)
                                  || e.errorCode.Equals(ErrorCodes.TestVariableUnreachable)),
            Is.False,
            "x should be visible after runto; errors: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_GlobalDeclaration_VisibleAlways()
    {
        // `global X` declarations are visible from the start, even pre-runto.
        var src = @"
global x as integer = 7
end

test foo
    assert x = 7
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableUnreachable)
                                  || e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.False,
            "global x should be visible; errors: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_TestLocal_NotConfusedWithProgramVar()
    {
        // A test-local declaration should be visible without errors, even if a
        // program-scope variable with the same name exists.
        var src = @"
end

test foo
    local x as integer = 99
    assert x = 99
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableUnreachable)
                                  || e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.False);
    }

    [Test]
    public void Strictness_BranchedDeclarations_VisibleAfterMerge()
    {
        // Per Fade's existing branch-rule semantics, both branches of an if/else
        // contribute their declared names. After the merge point, both names are
        // considered declared.
        var src = @"
condition = 1
if condition
    a = 5
else
    b = 10
endif
mergeLabel:
end

test foo
    runto mergeLabel
    assert a >= 0
    assert b >= 0
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.False,
            "a and b should both be visible at mergeLabel; errors: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_LocalAssignmentShadowsImplicit_Allowed()
    {
        // Implicit declaration of a name not in any other scope should make it
        // a test-local and not error.
        var src = @"
end

test foo
    myCount = 0
    myCount = myCount + 1
    assert myCount = 1
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableUnreachable)
                                  || e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.False,
            "implicit test-local should be fine; errors: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_AssignmentToProgramVar_PreRunto_Errors()
    {
        // Writing to a program-scope variable that hasn't been declared yet
        // (no runto has reached it) should error.
        var src = @"
x = 0
end

test foo
    x = 99
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableUnreachable)),
            Is.True,
            "writing program-scope x pre-runto should error; got: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    // ============================================================
    // Function-internal label strictness (mid-function runto flow)
    // ============================================================
    //
    // When a test runs to a label that's declared *inside* a function body,
    // the visible name set is:
    //   globals + function parameters + function locals declared up to that label
    //
    // Main-body names that aren't `global` are NOT visible — they aren't part
    // of the function's lexical scope. This is the conservative rule: users
    // who need shared state should declare it `global`.

    [Test]
    public void Strictness_RuntoFunctionInternalLabel_FunctionParam_Visible()
    {
        var src = @"
do_work(7)
end

function do_work(seed)
    local total as integer
    total = seed * 2
fnInner:
endfunction total

test foo
    runto fnInner
    assert seed = 7
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableUnreachable)
                                  || e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.False,
            "function param `seed` should be visible at fnInner; errors: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_RuntoFunctionInternalLabel_LocalDeclaredBefore_Visible()
    {
        var src = @"
do_work(1)
end

function do_work(seed)
    local total as integer
    total = seed + 10
fnInner:
endfunction total

test foo
    runto fnInner
    assert total = 11
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableUnreachable)
                                  || e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.False,
            "function local `total` declared before fnInner should be visible; errors: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_RuntoFunctionInternalLabel_LocalDeclaredAfter_Errors()
    {
        var src = @"
do_work(1)
end

function do_work(seed)
fnInner:
    local afterValue as integer
    afterValue = seed * 100
endfunction afterValue

test foo
    runto fnInner
    assert afterValue = 100
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.True,
            "function local `afterValue` declared after fnInner should error; got: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_RuntoFunctionInternalLabel_Global_Visible()
    {
        var src = @"
global tally as integer = 0
do_work(3)
end

function do_work(seed)
    tally = tally + seed
fnInner:
endfunction tally

test foo
    runto fnInner
    assert tally >= 0
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableUnreachable)
                                  || e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.False,
            "global `tally` should be visible at fnInner; errors: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_RuntoFunctionInternalLabel_MainBodyNonGlobal_NotVisible()
    {
        // `mainCounter` is declared in main body (not `global`). Even though
        // the test runs to a function-internal label, main-body non-globals
        // should NOT be visible inside the function's lexical scope.
        var src = @"
mainCounter = 5
do_work(2)
end

function do_work(seed)
    local result as integer
    result = seed
fnInner:
endfunction result

test foo
    runto fnInner
    assert mainCounter = 5
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableUnreachable)
                                  || e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.True,
            "main-body non-global `mainCounter` should NOT be visible at fnInner; "
            + "errors: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_RuntoFunctionInternalLabel_LocalInsideIfBranch_Visible()
    {
        // Both arms of if/else contribute names at the merge point — the same
        // branch-merge rule that applies to top-level scope_at must apply
        // inside functions too.
        var src = @"
do_work(0)
end

function do_work(flag)
    if flag
        a = 1
    else
        b = 2
    endif
fnInner:
endfunction flag

test foo
    runto fnInner
    assert a >= 0
    assert b >= 0
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.False,
            "branch-merged names a, b should both be visible at fnInner; errors: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_RuntoFunctionInternalLabel_AssignmentToFunctionLocal_AfterLabel_Errors()
    {
        // Test writes to a function local declared *after* the runto target.
        // LHS visibility is enforced too — should error.
        var src = @"
do_work(1)
end

function do_work(seed)
fnInner:
    local late as integer
    late = 99
endfunction late

test foo
    runto fnInner
    late = 7
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.True,
            "writing to function local `late` declared after fnInner should error; got: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_MultipleRuntos_VisibleSetUpdatesEachTime()
    {
        // Two runtos in sequence. After the first, only `x` is visible.
        // After the second, `y` is also visible. A reference to `y` between
        // the runtos must error.
        var src = @"
x = 1
firstLabel:
y = 2
secondLabel:
end

test foo
    runto firstLabel
    assert x = 1
    assert y = 2
    runto secondLabel
    assert y = 2
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Count(e => e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.EqualTo(1),
            "exactly one error expected for `y` between runtos; got: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_RuntoInsideIf_VisibilityPropagates()
    {
        // A runto inside an if-branch updates the test's visible set, and
        // that change persists for statements after the if. (Not branch-local.)
        var src = @"
x = 1
target:
end

test foo
    local cond as integer = 1
    if cond
        runto target
    endif
    assert x = 1
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableUnreachable)
                                  || e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.False,
            "runto inside if should make x visible after the if; errors: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_FunctionLocalInsideForLoop_BeforeLabel_Visible()
    {
        // Function-local declared inside a for loop above a function-internal
        // label should still be visible at that label per branch-merge rules.
        var src = @"
do_work()
end

function do_work()
    local i as integer
    for i = 0 to 5
        innerSum = i
    next i
fnLabel:
endfunction innerSum

test foo
    runto fnLabel
    assert innerSum >= 0
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.False,
            "innerSum declared inside for-loop above fnLabel should be visible; errors: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Strictness_TwoFunctions_LabelsIndependent()
    {
        // Each function's labels get their own snapshot. Names from function A
        // shouldn't leak into function B's label snapshot.
        var src = @"
do_a(1)
do_b(2)
end

function do_a(aParam)
    local aLocal as integer
    aLocal = aParam
labelA:
endfunction aLocal

function do_b(bParam)
labelB:
endfunction bParam

test usesA
    runto labelA
    assert aLocal = 1
endtest

test usesB
    runto labelB
    assert bParam = 2
endtest

test bDoesntSeeA
    runto labelB
    assert aLocal = 1
endtest
";
        Parse(src, out var errs);
        // usesA + usesB should NOT trip strictness errors.
        // bDoesntSeeA SHOULD trip a strictness error for aLocal.
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestVariableNotYetDeclared)),
            Is.True,
            "test `bDoesntSeeA` should error referencing aLocal at labelB; errors: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }
}
