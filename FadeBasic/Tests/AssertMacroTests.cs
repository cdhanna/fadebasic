using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Sdk;
using FadeBasic.Virtual;

namespace Tests;

[TestFixture]
public class AssertMacroTests
{
    private (Compiler compiler, byte[] program) Compile(string src)
    {
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        lex.AssertNoLexErrors();
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram(new ParseOptions { ignoreChecks = true });
        prog.AssertNoParseErrors();
        // Generate debug data so stack-frame resolution works in tests that
        // exercise FadeTestExecutor.BuildFrames; the SDK enables this by default.
        var compiler = new Compiler(TestCommands.CommandsForTesting,
            new CompilerOptions { GenerateDebugData = true });
        compiler.Compile(prog);
        return (compiler, compiler.Program.ToArray());
    }

    private VirtualMachine RunTest(string src, string testName)
    {
        var (compiler, program) = Compile(src);
        var entry = compiler.TestManifest.First(t => t.name == testName);
        var vm = new VirtualMachine(program, entry.entryPointAddress);
        vm.hostMethods = compiler.methodTable;
        // Mirror the SDK test runner so assert behavior matches production:
        // failures record TestFailure instead of throwing.
        vm.isTestExecution = true;
        vm.Execute3();
        return vm;
    }

    private VirtualMachine RunMain(string src)
    {
        var (compiler, program) = Compile(src);
        var vm = new VirtualMachine(program);
        vm.hostMethods = compiler.methodTable;
        vm.Execute3();
        return vm;
    }

    [Test]
    public void Assert_True_TestPasses()
    {
        var src = @"
test foo
    assert 1
endtest
";
        var vm = RunTest(src, "foo");
        Assert.That(vm.assertionFailure, Is.Null, "assert 1 should pass");
    }

    [Test]
    public void Assert_NonZeroExpression_TestPasses()
    {
        var src = @"
test foo
    local x as integer = 5
    assert x
endtest
";
        var vm = RunTest(src, "foo");
        Assert.That(vm.assertionFailure, Is.Null);
    }

    [Test]
    public void Assert_Zero_TestFails()
    {
        var src = @"
test foo
    assert 0
endtest
";
        var vm = RunTest(src, "foo");
        Assert.That(vm.assertionFailure, Is.Not.Null, "assert 0 should fail the test");
    }

    [Test]
    public void Assert_FalseExpression_TestFails()
    {
        var src = @"
test foo
    local x as integer = 5
    assert x = 6
endtest
";
        var vm = RunTest(src, "foo");
        Assert.That(vm.assertionFailure, Is.Not.Null);
    }

    [Test]
    public void Assert_TrueComparison_TestPasses()
    {
        var src = @"
test foo
    local x as integer = 5
    assert x = 5
endtest
";
        var vm = RunTest(src, "foo");
        Assert.That(vm.assertionFailure, Is.Null);
    }

    [Test]
    public void Assert_Failure_CapturesSourceText()
    {
        var src = @"
test foo
    local x as integer = 5
    assert x = 99
endtest
";
        var vm = RunTest(src, "foo");
        Assert.That(vm.assertionFailure, Is.Not.Null);
        // Source text should mention `x` and `99` somewhere.
        Assert.That(vm.assertionFailure.sourceText, Does.Contain("x"));
        Assert.That(vm.assertionFailure.sourceText, Does.Contain("99"));
    }

    [Test]
    public void Assert_FailureHaltsExecution()
    {
        // After a failed assert, subsequent statements should not execute.
        // We verify by making the failing assert come BEFORE another statement
        // that would otherwise alter VM state observably. Since we can only
        // observe via assertionFailure (no execution-side-effect to Fade-side)
        // for now, we settle for: failure halt means no second assert runs.
        var src = @"
test foo
    assert 0
    assert 1
endtest
";
        var vm = RunTest(src, "foo");
        Assert.That(vm.assertionFailure, Is.Not.Null,
            "first assert fails — second assert should not run, but failure should be present");
    }

    [Test]
    public void Assert_OutsideTest_Passing_RunsWithoutCrash()
    {
        // A truthy assert in the main program runs cleanly — no VM crash, no
        // assertionFailure recorded.
        var src = @"
assert 1
end
";
        var vm = RunMain(src);
        Assert.That(vm.assertionFailure, Is.Null);
        Assert.That(vm.error.type, Is.EqualTo(VirtualRuntimeErrorType.NONE));
    }

    [Test]
    public void Assert_OutsideTest_Failing_CrashesVm()
    {
        // A failing assert in the main program triggers a VM runtime crash
        // (VirtualRuntimeException), the same shape as divide-by-zero etc.
        var src = @"
assert 0, ""kaboom""
end
";
        var (compiler, program) = Compile(src);
        var vm = new VirtualMachine(program) { hostMethods = compiler.methodTable };
        var ex = Assert.Throws<VirtualRuntimeException>(() => vm.Execute3());
        Assert.That(ex.Error.type, Is.EqualTo(VirtualRuntimeErrorType.ASSERT_FAILED));
        Assert.That(ex.Error.message, Does.Contain("kaboom"),
            "crash message should surface the assert's reason");
    }

    [Test]
    public void Assert_OutsideTest_Failing_NoReason_StillCrashesVm()
    {
        var src = @"
assert 0
end
";
        var (compiler, program) = Compile(src);
        var vm = new VirtualMachine(program) { hostMethods = compiler.methodTable };
        var ex = Assert.Throws<VirtualRuntimeException>(() => vm.Execute3());
        Assert.That(ex.Error.type, Is.EqualTo(VirtualRuntimeErrorType.ASSERT_FAILED));
    }

    [Test]
    public void Assert_InMainProgram_ViaRunto_FailsTheTest()
    {
        // When a test runtos into main-program code that contains a failing
        // assert, the test should record the failure (not crash the VM).
        var src = @"
assert 0, ""main-program assert""
checkpoint:
end

test runto_test
    runto checkpoint
endtest
";
        var vm = RunTest(src, "runto_test");
        Assert.That(vm.assertionFailure, Is.Not.Null,
            "main-program assert reached via runto must mark the test as failed");
        Assert.That(vm.assertionFailure.reason, Is.EqualTo("main-program assert"));
    }

    [Test]
    public void Assert_InMainProgram_ViaRunto_Passing_DoesNotFailTest()
    {
        var src = @"
assert 1
checkpoint:
end

test runto_test
    runto checkpoint
endtest
";
        var vm = RunTest(src, "runto_test");
        Assert.That(vm.assertionFailure, Is.Null);
    }

    [Test]
    public void Assert_TwoSequentialPasses_BothExecute()
    {
        var src = @"
test foo
    assert 1
    assert 2
    assert 1 + 1
endtest
";
        var vm = RunTest(src, "foo");
        Assert.That(vm.assertionFailure, Is.Null);
    }

    [Test]
    public void Assert_WithReasonLiteral_FailureCapturesReason()
    {
        var src = @"
test foo
    local x as integer = 5
    assert x = 99, ""x should be 99""
endtest
";
        var vm = RunTest(src, "foo");
        Assert.That(vm.assertionFailure, Is.Not.Null);
        Assert.That(vm.assertionFailure.reason, Is.EqualTo("x should be 99"));
        Assert.That(vm.assertionFailure.sourceText, Does.Contain("x"));
    }

    [Test]
    public void Assert_WithReasonLiteral_PassDoesNotPopulateReason()
    {
        // When the assert passes, no failure is recorded — and the reason
        // expression must not have run side-effects (no observable way to
        // check from a pure-eval literal, but at minimum no failure exists).
        var src = @"
test foo
    assert 1 = 1, ""never seen""
endtest
";
        var vm = RunTest(src, "foo");
        Assert.That(vm.assertionFailure, Is.Null);
    }

    [Test]
    public void Assert_WithReasonVariable_FailureCapturesReason()
    {
        var src = @"
test foo
    local msg as string = ""boom""
    assert 0, msg
endtest
";
        var vm = RunTest(src, "foo");
        Assert.That(vm.assertionFailure, Is.Not.Null);
        Assert.That(vm.assertionFailure.reason, Is.EqualTo("boom"));
    }

    [Test]
    public void Assert_WithoutReason_HasEmptyReason()
    {
        var src = @"
test foo
    assert 0
endtest
";
        var vm = RunTest(src, "foo");
        Assert.That(vm.assertionFailure, Is.Not.Null);
        Assert.That(vm.assertionFailure.reason, Is.EqualTo(""));
    }

    [Test]
    public void Assert_ReasonMustBeString_NonStringReportsError()
    {
        // Passing a non-string reason (here, an integer) should fail
        // type-checking, surfacing AssertReasonMustBeString.
        var src = @"
test foo
    assert 1 = 1, 42
endtest
";
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        lex.AssertNoLexErrors();
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        var allErrors = prog.GetAllErrors();
        Assert.That(allErrors.Any(e => e.errorCode.code == ErrorCodes.AssertReasonMustBeString.code),
            "expected AssertReasonMustBeString error; got: " +
            string.Join(", ", allErrors.Select(e => e.errorCode.code.ToString())));
    }

    // ── Defer-on-assert-failure tests ──────────────────────────────────────
    // These verify the unwind trampoline: on a failed assert inside a test,
    // every live scope's defers run (LIFO), then the failure is reported.
    // Main-program asserts (running standalone) deliberately skip defers.

    [Test]
    public void Assert_TestBodyDefer_RunsOnFailure()
    {
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
test defer_runs_on_fail
    defer static print ""cleanup""
    assert 0, ""boom""
endtest
";
        var vm = RunTest(src, "defer_runs_on_fail");
        Assert.That(vm.assertionFailure, Is.Not.Null);
        Assert.That(vm.assertionFailure.reason, Is.EqualTo("boom"));
        Assert.That(TestCommands.staticPrintBuffer, Is.EqualTo(new[] { "cleanup" }),
            "test-body defer must run during assert-unwind");
    }

    [Test]
    public void Assert_TestBodyDefers_RunInLifoOrder()
    {
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
test multi_defer
    defer static print ""a""
    defer static print ""b""
    defer static print ""c""
    assert 0
endtest
";
        var vm = RunTest(src, "multi_defer");
        Assert.That(vm.assertionFailure, Is.Not.Null);
        Assert.That(TestCommands.staticPrintBuffer, Is.EqualTo(new[] { "c", "b", "a" }),
            "defers must run in LIFO order during unwind");
    }

    [Test]
    public void Assert_FunctionDefer_AndTestDefer_BothRun()
    {
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
function helper()
    defer static print ""func""
    assert 0, ""inside helper""
endfunction

test cross_scope
    defer static print ""test""
    helper()
endtest
";
        var vm = RunTest(src, "cross_scope");
        Assert.That(vm.assertionFailure, Is.Not.Null);
        Assert.That(vm.assertionFailure.reason, Is.EqualTo("inside helper"));
        // helper's scope drains first (innermost), then test's scope.
        Assert.That(TestCommands.staticPrintBuffer, Is.EqualTo(new[] { "func", "test" }),
            "function-scope defers must drain before unwinding back to test scope");
    }

    [Test]
    public void Assert_PassingTest_StillRunsDefers()
    {
        // Sanity: defers also run on the success path (existing behavior;
        // this just guards against the trampoline accidentally bypassing
        // normal scope-exit defer draining for passing tests).
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
test passing_with_defer
    defer static print ""cleanup""
    assert 1
endtest
";
        var vm = RunTest(src, "passing_with_defer");
        Assert.That(vm.assertionFailure, Is.Null);
        Assert.That(TestCommands.staticPrintBuffer, Is.EqualTo(new[] { "cleanup" }));
    }

    [Test]
    public void Assert_MainProgramFailure_SkipsDefers()
    {
        // Non-test execution: a failed assert is a hard crash; defers do
        // NOT run (matches divide-by-zero etc.).
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
defer static print ""never seen""
assert 0, ""crash""
end
";
        var (compiler, program) = Compile(src);
        var vm = new VirtualMachine(program) { hostMethods = compiler.methodTable };
        Assert.Throws<VirtualRuntimeException>(() => vm.Execute3());
        Assert.That(TestCommands.staticPrintBuffer, Is.Empty,
            "main-program assert is a hard crash; defers must not run");
    }

    // ── Call-stack capture & source-location resolution ────────────────────
    // These exercise BuildFrames against a real compile+run so we know the
    // VM's methodStack snapshot survives the unwind and that DebugData
    // resolves it to the expected lines.

    private FadeTestResult RunTestThroughExecutor(string src, string testName)
    {
        var (compiler, program) = Compile(src);
        var entry = compiler.TestManifest.First(t => t.name == testName);
        return FadeTestExecutor.RunTest(program, compiler.methodTable, entry, compiler.DebugData);
    }

    [Test]
    public void Assert_StackTrace_ReportsAssertLine_NotTestLine()
    {
        // Mirrors the user-reported scenario: assert lives inside a function
        // called from the main program, which is reached via runto from a
        // test. The innermost frame must point at the assert's actual line,
        // not the test entry's line. Line numbers are 0-based (lexer's
        // coordinate space); displayed as 1-based by adapters that add 1.
        var src = @"function ex(x)
    assert x > 0, ""x must be positive""
endfunction
ex(0)
checkpoint:
end

test sample
    runto checkpoint
endtest
";
        var result = RunTestThroughExecutor(src, "sample");
        Assert.That(result.passed, Is.False);
        Assert.That(result.failureFrames, Is.Not.Empty,
            "DebugData was enabled — frames should resolve");

        // Innermost frame = the assert inside `ex`. Source line 1 (0-based),
        // which is line 2 when displayed.
        var innermost = result.failureFrames[0];
        Assert.That(innermost.functionName, Is.EqualTo("ex"));
        Assert.That(innermost.lineNumber, Is.EqualTo(1));
    }

    [Test]
    public void Assert_StackTrace_IncludesCallerOfFunction()
    {
        // The frame above the assert is the caller (`ex(0)`), on 0-based
        // line 3 (displayed as line 4).
        var src = @"function ex(x)
    assert x > 0, ""x must be positive""
endfunction
ex(0)
checkpoint:
end

test sample
    runto checkpoint
endtest
";
        var result = RunTestThroughExecutor(src, "sample");
        Assert.That(result.failureFrames.Count, Is.GreaterThanOrEqualTo(2));

        var outermost = result.failureFrames[^1];
        Assert.That(outermost.functionName, Is.Empty,
            "outermost frame has no function name (it's the main program / test entry)");
        Assert.That(outermost.lineNumber, Is.EqualTo(3));
    }

    [Test]
    public void Assert_StackTrace_AssertInTestBody_OneFrame()
    {
        // No function calls — the entire failure is at the test entry level.
        // We still get one frame (the assert site at 0-based line 1).
        var src = @"test sample
    assert 0, ""boom""
endtest
";
        var result = RunTestThroughExecutor(src, "sample");
        Assert.That(result.failureFrames, Is.Not.Empty);
        Assert.That(result.failureFrames.Count, Is.EqualTo(1));
        Assert.That(result.failureFrames[0].functionName, Is.Empty);
        Assert.That(result.failureFrames[0].lineNumber, Is.EqualTo(1));
    }

    [Test]
    public void Assert_StackTrace_EmptyWhenNoDebugData()
    {
        // Without DebugData the runner can't resolve frames; failureFrames
        // stays empty and the adapter falls back to entry.sourceLine.
        var src = @"test sample
    assert 0
endtest
";
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        lex.AssertNoLexErrors();
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram(new ParseOptions { ignoreChecks = true });
        prog.AssertNoParseErrors();
        var compiler = new Compiler(TestCommands.CommandsForTesting,
            new CompilerOptions { GenerateDebugData = false });
        compiler.Compile(prog);

        var entry = compiler.TestManifest.First(t => t.name == "sample");
        var result = FadeTestExecutor.RunTest(
            compiler.Program.ToArray(), compiler.methodTable, entry, debugData: null);

        Assert.That(result.passed, Is.False);
        Assert.That(result.failureFrames, Is.Empty);
    }
}
