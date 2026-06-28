using BenchmarkDotNet.Attributes;
using FadeBasic;
using FadeBasic.Virtual;

namespace Benchmarks;

[MemoryDiagnoser]
public class Vms
{
    public string Source { get; set; } =
        "dim x(4):x(0) = 2:x(1) = x(0) * 2:x(2) = x(1) * x(0):x(3) = x(2) * x(1) * x(0):y = x(3)";

    private List<byte> _program;
    private VirtualMachine _vm;
    private CommandCollection _commands;

    [GlobalSetup]
    public void Setup()
    {
        _commands = new CommandCollection();
        var lexer = new Lexer();
        var tokens = lexer.TokenizeWithErrors(Source, _commands);
        var parser = new Parser(tokens.stream, _commands);
        var ast = parser.ParseProgram();
        var compiler = new Compiler(_commands);
        compiler.Compile(ast);
        _program = compiler.Program;
        _vm = new VirtualMachine(_program);
    }

    // [Benchmark]
    // public void Execute() => _vm.Execute3();
}
