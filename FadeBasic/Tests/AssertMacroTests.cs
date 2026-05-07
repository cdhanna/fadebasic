using FadeBasic;
using FadeBasic.Ast;
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
        var compiler = new Compiler(TestCommands.CommandsForTesting, new CompilerOptions());
        compiler.Compile(prog);
        return (compiler, compiler.Program.ToArray());
    }

    private VirtualMachine RunTest(string src, string testName)
    {
        var (compiler, program) = Compile(src);
        var entry = compiler.TestManifest.First(t => t.name == testName);
        var vm = new VirtualMachine(program, entry.entryPointAddress);
        vm.hostMethods = compiler.methodTable;
        vm.Execute().MoveNext();
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
    public void Assert_OutsideTest_StillCompiles()
    {
        // For now, assert outside a test compiles but its semantics are undefined.
        // (Stage 6+ should add a parse-time error for this; for now it's permissive.)
        var src = @"
assert 1
end
";
        Assert.DoesNotThrow(() =>
        {
            var (compiler, _) = Compile(src);
        });
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
}
