using BenchmarkDotNet.Attributes;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;
using System.Linq;

namespace Benchmarks;

[MemoryDiagnoser]
public class VmBenchmarks
{
    private FadeBasic.Virtual.HostMethodTable _methodTable;
    private byte[] _shortBytecode;
    private byte[] _mediumBytecode;
    private byte[] _largeBytecode;

    [GlobalSetup]
    public void Setup()
    {
        var lexer = new Lexer();
        var commands = new CommandCollection(new FadeBasicCommands());

        _shortBytecode  = Compile(lexer, commands, BenchmarkCorpus.Short,  nameof(BenchmarkCorpus.Short));
        _mediumBytecode = Compile(lexer, commands, BenchmarkCorpus.Medium, nameof(BenchmarkCorpus.Medium));
        _largeBytecode  = Compile(lexer, commands, BenchmarkCorpus.Large,  nameof(BenchmarkCorpus.Large));

        // All three share the same command set so one MethodTable covers all.
        var compiler = new Compiler(commands, CompilerOptions.Default);
        compiler.Compile(new Parser(
            new TokenStream(lexer.TokenizeWithErrors(BenchmarkCorpus.Short, commands).tokens),
            commands).ParseProgram());
        _methodTable = compiler.methodTable;
    }

    private static byte[] Compile(Lexer lexer, CommandCollection commands, string source, string name)
    {
        var lex = lexer.TokenizeWithErrors(source, commands);
        if (lex.tokenErrors is { Count: > 0 })
            throw new InvalidOperationException(
                $"Corpus '{name}' lex errors: {string.Join(", ", lex.tokenErrors.Select(e => e.Display))}");

        var stream = new TokenStream(lex.tokens, lex.tokenErrors);
        var prog = new Parser(stream, commands).ParseProgram();
        var errs = prog.GetAllErrors();
        if (errs.Count > 0)
            throw new InvalidOperationException(
                $"Corpus '{name}' parse errors: {string.Join(", ", errs.Select(e => e.Display))}");

        var compiler = new Compiler(commands, CompilerOptions.Default);
        compiler.Compile(prog);
        return compiler.Program.ToArray();
    }

    // ── Full-run benchmarks ──────────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    public VirtualMachine RunShort()
    {
        var vm = new VirtualMachine(_shortBytecode);
        vm.hostMethods = _methodTable;
        vm.Execute3(0);
        return vm;
    }

    [Benchmark]
    public VirtualMachine RunMedium()
    {
        var vm = new VirtualMachine(_mediumBytecode);
        vm.hostMethods = _methodTable;
        vm.Execute3(0);
        return vm;
    }

    [Benchmark]
    public VirtualMachine RunLarge()
    {
        var vm = new VirtualMachine(_largeBytecode);
        vm.hostMethods = _methodTable;
        vm.Execute3(0);
        return vm;
    }

    // ── Budgeted tight-loop benchmarks (budget = 100 instructions/slice) ─────

    [Benchmark]
    public VirtualMachine RunShort_Budget100()
    {
        var vm = new VirtualMachine(_shortBytecode);
        vm.hostMethods = _methodTable;
        while (vm.instructionIndex < vm.program.Length &&
               vm.error.type == VirtualRuntimeErrorType.NONE)
            vm.Execute3(100);
        return vm;
    }

    [Benchmark]
    public VirtualMachine RunMedium_Budget100()
    {
        var vm = new VirtualMachine(_mediumBytecode);
        vm.hostMethods = _methodTable;
        while (vm.instructionIndex < vm.program.Length &&
               vm.error.type == VirtualRuntimeErrorType.NONE)
            vm.Execute3(100);
        return vm;
    }

    [Benchmark]
    public VirtualMachine RunLarge_Budget100()
    {
        var vm = new VirtualMachine(_largeBytecode);
        vm.hostMethods = _methodTable;
        while (vm.instructionIndex < vm.program.Length &&
               vm.error.type == VirtualRuntimeErrorType.NONE)
            vm.Execute3(100);
        return vm;
    }
}
