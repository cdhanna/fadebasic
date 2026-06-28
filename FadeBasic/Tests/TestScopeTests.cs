using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace Tests;

[TestFixture]
public class TestScopeTests
{
    private ProgramNode Parse(string src, out List<ParseError> errors, bool checkScope = true)
    {
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        lex.AssertNoLexErrors();
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram(new ParseOptions { ignoreChecks = !checkScope });
        errors = prog.GetAllErrors();
        return prog;
    }

    [Test]
    public void Local_InTest_Parses()
    {
        var src = @"
test foo
    local x as integer = 5
endtest
";
        var prog = Parse(src, out var errs);
        // For now, scope-check may flag this as something — that's OK; what we want
        // to verify is parser-level: the statement is recognized.
        Assert.That(prog.tests.Count, Is.EqualTo(1));
        Assert.That(prog.tests[0].testProgram.statements.Count, Is.GreaterThan(0));
    }

    [Test]
    public void Local_InTest_Compiles()
    {
        // Verify a test with a local declaration compiles cleanly.
        var src = @"
test foo
    local x as integer = 5
endtest
";
        var prog = Parse(src, out _, checkScope: false);
        var compiler = new Compiler(TestCommands.CommandsForTesting, new CompilerOptions());
        Assert.DoesNotThrow(() => compiler.Compile(prog));
        Assert.That(compiler.TestManifest.Count, Is.EqualTo(1));
    }

    [Test]
    public void Local_InTest_Executes()
    {
        // The test body's `local` declaration runs and assigns. We can't yet
        // observe the local from C# without `assert` (Stage 5), but we can at
        // least confirm execution completes without errors.
        var src = @"
test foo
    local x as integer = 5
endtest
";
        var prog = Parse(src, out _, checkScope: false);
        var compiler = new Compiler(TestCommands.CommandsForTesting, new CompilerOptions());
        compiler.Compile(prog);
        var entry = compiler.TestManifest[0];
        var program = compiler.Program.ToArray();
        var vm = new VirtualMachine(program, entry.entryPointAddress);
        vm.hostMethods = compiler.methodTable;
        Assert.DoesNotThrow(() => vm.Execute3());
    }

    [Test]
    public void RuntoBlock_WithMaxCycles_Compiles()
    {
        var src = @"
mylabel:
end

test foo
    runto mylabel
        max cycles 1000
    endrunto
endtest
";
        var prog = Parse(src, out _, checkScope: false);
        var compiler = new Compiler(TestCommands.CommandsForTesting, new CompilerOptions());
        Assert.DoesNotThrow(() => compiler.Compile(prog));
    }

    [Test]
    public void TestBody_CanContainMultipleStatements()
    {
        var src = @"
mylabel:
end

test foo
    local a as integer = 1
    local b as integer = 2
    runto mylabel
endtest
";
        var prog = Parse(src, out _, checkScope: false);
        Assert.That(prog.tests.Count, Is.EqualTo(1));
        Assert.That(prog.tests[0].testProgram.statements.Count, Is.EqualTo(3));
    }

    [Test]
    public void Local_ScopeChecksClean()
    {
        // local declared and used in a test: scope check should pass.
        var src = @"
test foo
    local x as integer
    x = 5
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs.Count, Is.EqualTo(0),
            "expected no errors, got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void GlobalDeclared_VisibleInTest()
    {
        // `global x` is declared before the test; the test reads it.
        // After we have read-through-to-globals semantics, this should be clean.
        var src = @"
global x as integer = 7
end

test foo
    local y as integer
    y = x
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs.Count, Is.EqualTo(0),
            "expected no errors, got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void TwoTests_LocalsDontLeak()
    {
        // Each test has its own local-variable scope. A `local` in test alpha
        // should not be visible to test beta.
        var src = @"
test alpha
    local x as integer = 5
endtest

test beta
    local x as integer = 10
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs.Count, Is.EqualTo(0),
            "fresh scope per test means same local name in two tests is fine; got: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void ProgramLocal_NotVisibleInTest_CommandArg()
    {
        // `local x` is a top-level declaration. The test has no runto, so its
        // visible-program-set is just globals (empty). Referencing `x` inside
        // a command arg (`print x`) must be flagged TestVariableUnreachable —
        // exposes the missing CommandStatement case in TestScopeStrictnessVisitor.
        var src = @"
local x = 42
_L1:

test sample
    print x
endtest
";
        AssertHasUnreachable(src, "x", "print x");
    }

    [Test]
    public void ProgramRefCommandIntroducedVar_NotVisibleInTest()
    {
        // `inc x` at program top-level introduces `x` as a program variable —
        // the base scope checker treats ref-command args as bindings
        // (Parser.cs Scope.AddCommand -> TryAddVariable). A test that
        // references `x` without a runto past this point must be flagged
        // TestVariableUnreachable.
        //
        // Note: `inc x` *inside* the test body is NOT this scenario — there
        // it acts like `x = ...`, implicitly declaring a test-local. That
        // case is allowed and should not error.
        //
        // Exposes two compound gaps in TestScopeStrictnessVisitor:
        //   1. WalkStatements has no CommandStatement case, so top-level
        //      ref-command bindings never enter allTopLevelNames or any
        //      scope_at snapshot.
        //   2. VisitStatement has no CommandStatement case, so the test's
        //      `print x` arg is never validated.
        // Both fixes are needed before this test passes.
        var src = @"
inc x
_L1:

test sample
    print x
endtest
";
        AssertHasUnreachable(src, "x", "print x (x introduced by program `inc x`)");
    }

    [Test]
    public void ProgramLocal_NotVisibleInTest_WhileCondition()
    {
        // The visitor descends into `while` body statements but never visits
        // `whileStmt.condition`. A reference there slips past validation.
        var src = @"
local x = 42
_L1:

test sample
    while x > 0
    endwhile
endtest
";
        AssertHasUnreachable(src, "x", "while x > 0");
    }

    [Test]
    public void ProgramLocal_NotVisibleInTest_ForBounds()
    {
        // ForStatement case adds the iterator to test-locals and walks the
        // body, but doesn't check startValue/endValue/stepValue expressions.
        var src = @"
local x = 42
_L1:

test sample
    for i = 1 to x
    next
endtest
";
        AssertHasUnreachable(src, "x", "for i = 1 to x");
    }

    [Test]
    public void ProgramLocal_NotVisibleInTest_RepeatUntilCondition()
    {
        // `RepeatUntilStatement` is handled in WalkStatements but completely
        // absent from VisitStatement — body and `until` condition both skipped.
        var src = @"
local x = 42
_L1:

test sample
    repeat
    until x > 0
endtest
";
        AssertHasUnreachable(src, "x", "until x > 0");
    }

    [Test]
    public void ProgramLocal_NotVisibleInTest_SwitchExpression()
    {
        // `SwitchStatement` is in WalkStatements but absent from VisitStatement.
        // The `select` expression and case bodies are both unchecked.
        var src = @"
local x = 42
_L1:

test sample
    select x
        case 1
        endcase
    endselect
endtest
";
        AssertHasUnreachable(src, "x", "select x");
    }

    [Test]
    public void ProgramLocal_NotVisibleInTest_NestedExpressionInCommand()
    {
        // Even nested inside arithmetic + a function-call arg, the reference
        // must be caught. Same root cause (no CommandStatement case) but
        // exercises deeper expression walking once that case is added.
        var src = @"
local x = 42
_L1:

test sample
    print add(x + 1, 2)
endtest
";
        AssertHasUnreachable(src, "x", "print add(x + 1, 2)");
    }

    [Test]
    public void ProgramStructLocal_FieldAccess_NotVisibleInTest()
    {
        // A struct local declared at program top-level. The test references
        // a field via `p.x`. CheckExpression walks the StructFieldReference
        // and finds `p` as a VariableRefNode — same visibility rule applies,
        // so referencing it without a runto must flag TestVariableUnreachable.
        var src = @"
type pt
    x
    y
endtype
local p as pt
_L1:

test sample
    print p.x
endtest
";
        AssertHasUnreachable(src, "p", "print p.x");
    }

    [Test]
    public void TestLocalStruct_FieldAccess_NoError()
    {
        // Sanity counterpart: when the struct is declared inside the test,
        // field access is fine — `p` is a test-local, so the visibility rule
        // doesn't fire.
        var src = @"
type pt
    x
    y
endtype

test sample
    local p as pt
    p.x = 5
    print p.x
endtest
";
        Parse(src, out var errs);
        Assert.That(
            errs.Any(e => e.errorCode.code == ErrorCodes.TestVariableUnreachable.code
                       || e.errorCode.code == ErrorCodes.TestVariableNotYetDeclared.code),
            Is.False,
            "expected no strict-test scope errors; got: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void StructFieldName_CollidesWithTopLevelVar_FalsePositive()
    {
        // BUG DEMO (currently failing): `p.foo` is a StructFieldReference
        // whose `right` side is a VariableRefNode("foo"). CheckExpression
        // walks every VariableRefNode in the tree and treats `foo` as if it
        // were a variable lookup. When a top-level `local foo` happens to
        // share the field's name, the visitor flags the field side as
        // TestVariableUnreachable even though it's just a struct member.
        //
        // The fix is roughly: skip the `right` side of a StructFieldReference
        // when walking (or only walk the left chain). Until then, this test
        // documents the false positive.
        var src = @"
type pt
    foo
endtype

local foo = 7

test sample
    local p as pt
    print p.foo
endtest
";
        Parse(src, out var errs);
        Assert.That(
            errs.Any(e => e.errorCode.code == ErrorCodes.TestVariableUnreachable.code),
            Is.False,
            "field name `foo` on test-local `p` must not be treated as a variable lookup; got: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void ProgramStructLocal_FieldAccess_VisibleAfterRunto()
    {
        // With a runto past the declaration, the struct local is in scope_at
        // and `p.x` should validate cleanly. Proves the runto -> scope_at
        // path composes with StructFieldReference walking.
        var src = @"
type pt
    x
    y
endtype
local p as pt
_L1:

test sample
    runto _L1
    print p.x
endtest
";
        Parse(src, out var errs);
        Assert.That(
            errs.Any(e => e.errorCode.code == ErrorCodes.TestVariableUnreachable.code
                       || e.errorCode.code == ErrorCodes.TestVariableNotYetDeclared.code),
            Is.False,
            "expected no strict-test scope errors after runto past declaration; got: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Runto_AcrossFunctionBoundaries_VisibilityReplacesNotUnions()
    {
        // Documents how `visible` shifts as a test runtos across function
        // boundaries. Today the visitor REPLACES the visible set on each
        // runto (no union), so each scope sees only its own snapshot plus
        // globals.
        //
        // Stages:
        //   1. runto _main1      visible = scope_at[_main1] = { top1 }
        //   2. runto _inA        visible = scope_at[_inA]   = { a1 }
        //                        (top1 from earlier runto is gone)
        //   3. runto _inB        visible = scope_at[_inB]   = { b1 }
        //                        (a1 gone)
        //   4. runto _inA again  visible reverts to { a1 }
        //                        (b1 gone)
        //
        // Each `print X` after a runto is checked against the snapshot at
        // that moment; references that aren't visible become
        // TestVariableNotYetDeclared (runtoTarget != null after the first
        // runto). The expected set of error names captures exactly which
        // references the visitor flags.
        var src = @"
local top1 = 1
_main1:
local top2 = 2
helper_a()
end

function helper_a()
    local a1 = 10
    _inA:
    local a2 = 20
    helper_b()
endfunction

function helper_b()
    local b1 = 100
    _inB:
    local b2 = 200
endfunction

test sample
    runto _main1
    print top1
    print top2

    runto _inA
    print a1
    print top1
    print a2

    runto _inB
    print b1
    print a1

    runto _inA
    print a1
    print b1
endtest
";
        Parse(src, out var errs);

        var visibilityErrs = errs
            .Where(e => e.errorCode.code == ErrorCodes.TestVariableNotYetDeclared.code
                     || e.errorCode.code == ErrorCodes.TestVariableUnreachable.code)
            .Select(e => e.message)
            .OrderBy(s => s)
            .ToList();

        // Stage 1: top2 (declared after _main1)
        // Stage 2: top1 (not in fnA scope), a2 (declared after _inA)
        // Stage 3: a1 (not in fnB scope)
        // Stage 4: b1 (not in fnA scope after re-entry)
        var expected = new[] { "a1", "a2", "b1", "top1", "top2" };

        Assert.That(visibilityErrs, Is.EquivalentTo(expected),
            "expected exactly these visibility errors; got: "
            + string.Join(" | ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Runto_IntoFunction_SeesImplicitAssignmentBeforeLabel()
    {
        // After `runto _L2`, execution is inside `tuna()` and `y = 24` has
        // already run, so the test's `print y` should be clean. The base
        // scope checker inserts an implicit `local y` declaration when it
        // encounters the bare assignment, which means the strict visitor's
        // fnState should pick up `y` before it snapshots `_L2`.
        var src = @"
local x = 42
_L1:
helper()

function helper()
    y = 24
    _L2:
endfunction

test sample
    runto _L1
    print x
    runto _L2
    print y
endtest
";
        Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "expected no errors of any kind; got: "
            + string.Join(" | ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Runto_IntoFunction_ParamVisibleAfterRunto()
    {
        // ComputeFunctionInternalScopeAts adds function parameters to fnState
        // before walking the body, so scope_at[_inside] includes `p`.
        // After `runto _inside`, the test should see `p`.
        var src = @"
helper(5)
end

function helper(p)
    _inside:
    local q = p + 1
endfunction

test sample
    runto _inside
    print p
endtest
";
        Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "expected no errors; got: "
            + string.Join(" | ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Runto_IntoFunction_ParamNotVisibleWithoutRunto()
    {
        // Mirror of the previous test: without a runto into helper, the param
        // `p` is in allTopLevelNames (so the base check resolves it via our
        // parent-fn copy) but not in visible -> TestVariableUnreachable.
        var src = @"
helper(5)
end

function helper(p)
endfunction

test sample
    print p
endtest
";
        AssertHasUnreachable(src, "p", "print p without runto into helper");
    }

    [Test]
    public void FunctionInternalLocal_NotVisibleWithoutRunto()
    {
        // Negative counterpart to Runto_IntoFunction_SeesImplicitAssignmentBeforeLabel.
        // The base scope checker now resolves `y` (we copy parent fn locals into
        // the test scope), but the strict visitor must still flag it as
        // unreachable when no runto reaches the function-internal label.
        var src = @"
helper()
end

function helper()
    y = 24
    _L2:
endfunction

test sample
    print y
endtest
";
        AssertHasUnreachable(src, "y", "print y without runto into helper");
    }

    [Test]
    public void TwoFunctions_SameLocalName_EachRuntoSeesItsOwn()
    {
        // fnA and fnB both declare `local result`. After `runto _inA`, the
        // visible set is scope_at[_inA] = {result}; same after `runto _inB`.
        // Strict visitor validates clean for both. The base checker resolves
        // `result` to fnA's symbol (first-source-wins) for both references,
        // which is fine for visibility-only purposes.
        var src = @"
fnA()
fnB()
end

function fnA()
    local result = 1
    _inA:
endfunction

function fnB()
    local result = 2
    _inB:
endfunction

test sample
    runto _inA
    print result
    runto _inB
    print result
endtest
";
        Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "expected no errors; got: "
            + string.Join(" | ", errs.Select(e => e.Display)));
    }

    [Test]
    public void TestLocal_ShadowsParentFunctionInternal()
    {
        // Test body declares `local y = 99`. Parent fn `helper` also has an
        // internal `y`. The test's `y` should win — VisitStatement's
        // CheckExpression checks testLocals first and short-circuits before
        // allTopLevelNames. Reference to `y` in the test must be clean.
        var src = @"
helper()
end

function helper()
    y = 24
    _L2:
endfunction

test sample
    local y = 99
    print y
endtest
";
        Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "expected no errors; got: "
            + string.Join(" | ", errs.Select(e => e.Display)));
    }

    [Test]
    public void TestLocal_ConflictsWithRuntoExposedName_DeclThenRunto()
    {
        // Declaration first, then runto brings the conflicting name into
        // view -> TestRuntoShadowsLocal at the runto.
        var src = @"
helper()
end

function helper()
    y = 24
    _L2:
endfunction

test sample
    local y = 99
    runto _L2
endtest
";
        Parse(src, out var errs);
        Assert.That(
            errs.Any(e => e.errorCode.code == ErrorCodes.TestRuntoShadowsLocal.code
                       && e.message == "y"),
            Is.True,
            "expected TestRuntoShadowsLocal at runto site for `y`; got: "
            + string.Join(" | ", errs.Select(e => e.Display)));
    }

    [Test]
    public void TestLocal_ConflictsWithRuntoExposedName_RuntoThenDecl()
    {
        // Runto brings `y` into view first, then the test declares a local
        // of the same name -> TestRuntoShadowsLocal at the declaration.
        var src = @"
helper()
end

function helper()
    y = 24
    _L2:
endfunction

test sample
    runto _L2
    local y = 99
endtest
";
        Parse(src, out var errs);
        Assert.That(
            errs.Any(e => e.errorCode.code == ErrorCodes.TestRuntoShadowsLocal.code
                       && e.message == "y"),
            Is.True,
            "expected TestRuntoShadowsLocal at declaration for `y`; got: "
            + string.Join(" | ", errs.Select(e => e.Display)));
    }

    [Test]
    public void BareAssignment_InTest_ImplicitTestLocal_NoError()
    {
        // `x = 4` at top level introduces `x` as a program top-level local
        // (via the base checker's implicit-decl). The test then does its
        // own bare `x = 12` with no runto -> should be a fresh implicit
        // test-local, same way `x = 12` inside a function body would be
        // an implicit function-local. The strict visitor must NOT flag it
        // as TestVariableUnreachable.
        var src = @"
x = 4
_L1:
test sample
    x = 12
endtest
";
        Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "expected no errors; got: "
            + string.Join(" | ", errs.Select(e => e.Display)));
    }

    [Test]
    public void TestLocal_ShadowsGlobal_NoConflict()
    {
        // Globals are always-shadowable: a test-local with the same name as
        // a global must not fire TestRuntoShadowsLocal.
        var src = @"
global g = 5

test sample
    local g = 10
endtest
";
        Parse(src, out var errs);
        Assert.That(
            errs.Any(e => e.errorCode.code == ErrorCodes.TestRuntoShadowsLocal.code),
            Is.False,
            "shadowing a global should not flag TestRuntoShadowsLocal; got: "
            + string.Join(" | ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Runto_UnknownLabel_EmitsParseError()
    {
        // A runto whose target label doesn't exist anywhere in the program
        // should be a hard parse error (RuntoUnknownLabel is already defined
        // in Errors.cs but currently never emitted). Until that's wired up,
        // the visitor silently sets currentRuntoTarget and produces
        // confusing "not yet declared" errors for *every* subsequent
        // reference in the test.
        var src = @"
local x = 1
_real:

test sample
    runto _does_not_exist
endtest
";
        Parse(src, out var errs);
        Assert.That(
            errs.Any(e => e.errorCode.code == ErrorCodes.RuntoUnknownLabel.code),
            Is.True,
            "expected RuntoUnknownLabel for `_does_not_exist`; got: "
            + string.Join(" | ", errs.Select(e => e.Display)));
    }

    private void AssertHasUnreachable(string src, string varName, string context)
    {
        Parse(src, out var errs);
        Assert.That(
            errs.Any(e => e.errorCode.code == ErrorCodes.TestVariableUnreachable.code),
            Is.True,
            $"expected TestVariableUnreachable for `{varName}` in `{context}`; got: "
            + string.Join(", ", errs.Select(e => e.Display)));
    }
}
