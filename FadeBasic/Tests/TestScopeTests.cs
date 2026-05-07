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
        Assert.That(prog.tests[0].statements.Count, Is.GreaterThan(0));
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
        Assert.DoesNotThrow(() => vm.Execute().MoveNext());
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
        Assert.That(prog.tests[0].statements.Count, Is.EqualTo(3));
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
}
