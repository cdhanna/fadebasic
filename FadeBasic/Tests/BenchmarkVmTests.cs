using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;
using System.Linq;

namespace Tests;

[TestFixture]
public class BenchmarkVmTests
{
    private static CommandCollection Commands() => new CommandCollection(new FadeBasicCommands());

    private static (byte[] bytecode, HostMethodTable methodTable) Compile(string src)
    {
        var commands = Commands();
        var lex = new Lexer().TokenizeWithErrors(src, commands);
        var stream = new TokenStream(lex.tokens, lex.tokenErrors);
        var prog = new Parser(stream, commands).ParseProgram();
        var compiler = new Compiler(commands, CompilerOptions.Default);
        compiler.Compile(prog);
        return (compiler.Program.ToArray(), compiler.methodTable);
    }

    private static VirtualMachine Run(string src)
    {
        var (bytecode, methodTable) = Compile(src);
        var vm = new VirtualMachine(bytecode);
        vm.hostMethods = methodTable;
        vm.Execute3(0);
        return vm;
    }

    private static VirtualMachine RunBudgeted(string src, int budget)
    {
        var (bytecode, methodTable) = Compile(src);
        var vm = new VirtualMachine(bytecode);
        vm.hostMethods = methodTable;
        while (vm.instructionIndex < vm.program.Length &&
               vm.error.type == VirtualRuntimeErrorType.NONE)
            vm.Execute3(budget);
        return vm;
    }

    // ── Full-run tests ───────────────────────────────────────────────────────

    [Test]
    public void Short_RunsToCompletion()
    {
        var vm = Run(BenchmarkCorpus.Short);
        Assert.That(vm.error.type, Is.EqualTo(VirtualRuntimeErrorType.NONE));
        Assert.That(vm.instructionIndex, Is.GreaterThanOrEqualTo(vm.program.Length));
    }

    [Test]
    public void Medium_RunsToCompletion()
    {
        var vm = Run(BenchmarkCorpus.Medium);
        Assert.That(vm.error.type, Is.EqualTo(VirtualRuntimeErrorType.NONE));
        Assert.That(vm.instructionIndex, Is.GreaterThanOrEqualTo(vm.program.Length));
    }

    [Test]
    public void Large_RunsToCompletion()
    {
        var vm = Run(BenchmarkCorpus.Large);
        Assert.That(vm.error.type, Is.EqualTo(VirtualRuntimeErrorType.NONE));
        Assert.That(vm.instructionIndex, Is.GreaterThanOrEqualTo(vm.program.Length));
    }

    // ── Budgeted tight-loop tests ────────────────────────────────────────────

    [Test]
    public void Short_Budget100_RunsToCompletion()
    {
        var vm = RunBudgeted(BenchmarkCorpus.Short, 100);
        Assert.That(vm.error.type, Is.EqualTo(VirtualRuntimeErrorType.NONE));
        Assert.That(vm.instructionIndex, Is.GreaterThanOrEqualTo(vm.program.Length));
    }

    [Test]
    public void Medium_Budget100_RunsToCompletion()
    {
        var vm = RunBudgeted(BenchmarkCorpus.Medium, 100);
        Assert.That(vm.error.type, Is.EqualTo(VirtualRuntimeErrorType.NONE));
        Assert.That(vm.instructionIndex, Is.GreaterThanOrEqualTo(vm.program.Length));
    }

    [Test]
    public void Large_Budget100_RunsToCompletion()
    {
        var vm = RunBudgeted(BenchmarkCorpus.Large, 100);
        Assert.That(vm.error.type, Is.EqualTo(VirtualRuntimeErrorType.NONE));
        Assert.That(vm.instructionIndex, Is.GreaterThanOrEqualTo(vm.program.Length));
    }
}
