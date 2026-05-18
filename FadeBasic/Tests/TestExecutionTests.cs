using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace Tests;

[TestFixture]
public class TestExecutionTests
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
        vm.Execute3();
        return vm;
    }

    [Test]
    public void Execute_EmptyTest_RunsToCompletion()
    {
        var src = @"
test empty
endtest
";
        var vm = RunTest(src, "empty");
        // Just verify no exceptions and the vm halted normally.
        Assert.That(vm.runtoStack.Count, Is.EqualTo(0));
    }

    
    [Test]
    public void Execute_EmptyTest_CanHaveFunction()
    {
        var src = @"
test funcSupport
    x()

    function x()
    endfunction
endtest
";
        var vm = RunTest(src, "funcSupport");
        // Just verify no exceptions and the vm halted normally.
        Assert.That(vm.runtoStack.Count, Is.EqualTo(0));
    }

    
    [Test]
    public void Execute_TestRunsToProgramLabel()
    {
        // A test that issues `runto :start`. The program top-level body has
        // a `start:` label. After the test runs, programResumeIP should sit
        // right after the label.
        var src = @"
x = 1
start:
x = 2
end

test foo
    runto start
endtest
";
        var (compiler, program) = Compile(src);
        var entry = compiler.TestManifest.First(t => t.name == "foo");
        var vm = new VirtualMachine(program, entry.entryPointAddress);
        vm.hostMethods = compiler.methodTable;
        vm.Execute3();

        // After yield, runtoStack is empty and programResumeIP is the post-yield IP.
        Assert.That(vm.runtoStack.Count, Is.EqualTo(0));
        Assert.That(vm.programResumeIP, Is.GreaterThan(4),
            "programResumeIP should have advanced past the entry header");
    }

    [Test]
    public void Execute_MultipleRuntos_ProgressThroughProgram()
    {
        var src = @"
first:
x = 1
second:
x = 2
end

test foo
    runto first
    runto second
endtest
";
        var (compiler, program) = Compile(src);
        var entry = compiler.TestManifest.First(t => t.name == "foo");
        var vm = new VirtualMachine(program, entry.entryPointAddress);
        vm.hostMethods = compiler.methodTable;
        vm.Execute3();

        Assert.That(vm.runtoStack.Count, Is.EqualTo(0));
    }

    [Test]
    public void Execute_DefaultEntryPoint_RunsProgramNotTest()
    {
        // Without specifying entry point, the VM starts at default (4) and
        // runs the program body. The test body should not be entered.
        var src = @"
somewhere:
x = 7
end

test foo
    runto somewhere
endtest
";
        var (compiler, program) = Compile(src);
        var vm = new VirtualMachine(program); // default entry
        vm.hostMethods = compiler.methodTable;
        vm.Execute3();

        Assert.That(vm.runtoStack.Count, Is.EqualTo(0),
            "default entry runs program code only; runtoStack should never get touched");
    }

    [Test]
    public void Execute_TwoTests_IndependentVMs()
    {
        // Each test gets its own fresh VM. They shouldn't share state.
        var src = @"
test alpha
endtest

test beta
endtest
";
        var (compiler, program) = Compile(src);
        var alpha = compiler.TestManifest.First(t => t.name == "alpha");
        var beta = compiler.TestManifest.First(t => t.name == "beta");
        Assert.That(alpha.entryPointAddress, Is.Not.EqualTo(beta.entryPointAddress));

        var vmA = new VirtualMachine(program, alpha.entryPointAddress);
        vmA.hostMethods = compiler.methodTable;
        vmA.Execute3();

        var vmB = new VirtualMachine(program, beta.entryPointAddress);
        vmB.hostMethods = compiler.methodTable;
        vmB.Execute3();

        // Both halt cleanly.
        Assert.That(vmA.runtoStack.Count, Is.EqualTo(0));
        Assert.That(vmB.runtoStack.Count, Is.EqualTo(0));
    }

    [Test]
    public void Execute_NormalProgram_StillRunsUnchanged()
    {
        // Smoke test: a regular Fade program with no tests should compile and
        // execute exactly as before. Tests-related code paths add no overhead
        // and don't alter behavior when no tests are present.
        var src = @"
x = 5
y = x + 3
end
";
        var (compiler, program) = Compile(src);
        var vm = new VirtualMachine(program);
        vm.hostMethods = compiler.methodTable;
        vm.Execute3();

        // y should have been computed as 8
        Assert.That(vm.dataRegisters[1], Is.EqualTo(8));
    }
    
    

    [Test]
    public void Execute_GlobalVariables()
    {
        // `GLOBAL x = 32` lives inside the test body, so `x` is scoped to
        // the test — main-body code cannot see it. Referencing `x` from
        // the main program is a hard parse error.
        var src = @"
test foo
    GLOBAL x = 32
endtest

print x `this should result in an error.
";
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        lex.AssertNoLexErrors();
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        var errs = prog.GetAllErrors();
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.SymbolNotDeclaredYet)),
            Is.True,
            "expected SymbolNotDeclaredYet on the main-body `print x` reference; got: "
            + string.Join("; ", errs.Select(e => e.Display)));
    }
}
