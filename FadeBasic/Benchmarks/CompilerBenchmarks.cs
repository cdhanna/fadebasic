using BenchmarkDotNet.Attributes;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;
using System.Linq;

namespace Benchmarks;

[MemoryDiagnoser]
public class CompilerBenchmarks
{
    private CommandCollection _commands;
    private ProgramNode _shortProg;
    private ProgramNode _mediumProg;
    private ProgramNode _largeProg;

    [GlobalSetup]
    public void Setup()
    {
        var lexer = new Lexer();
        _commands = new CommandCollection(new FadeBasicCommands());

        _shortProg  = Parse(lexer, BenchmarkCorpus.Short,  nameof(BenchmarkCorpus.Short));
        _mediumProg = Parse(lexer, BenchmarkCorpus.Medium, nameof(BenchmarkCorpus.Medium));
        _largeProg  = Parse(lexer, BenchmarkCorpus.Large,  nameof(BenchmarkCorpus.Large));
    }

    private ProgramNode Parse(Lexer lexer, string source, string name)
    {
        var lex = lexer.TokenizeWithErrors(source, _commands);
        if (lex.tokenErrors is { Count: > 0 })
            throw new InvalidOperationException(
                $"Corpus '{name}' produced lex errors: {string.Join(", ", lex.tokenErrors.Select(e => e.Display))}");

        var stream = new TokenStream(lex.tokens, lex.tokenErrors);
        var prog = new Parser(stream, _commands).ParseProgram();
        var errs = prog.GetAllErrors();
        if (errs.Count > 0)
            throw new InvalidOperationException(
                $"Corpus '{name}' produced parse errors: {string.Join(", ", errs.Select(e => e.Display))}");

        return prog;
    }

    [Benchmark(Baseline = true)]
    public List<byte> CompileShort()
    {
        var compiler = new Compiler(_commands, CompilerOptions.Default);
        compiler.Compile(_shortProg);
        return compiler.Program;
    }

    [Benchmark]
    public List<byte> CompileMedium()
    {
        var compiler = new Compiler(_commands, CompilerOptions.Default);
        compiler.Compile(_mediumProg);
        return compiler.Program;
    }

    [Benchmark]
    public List<byte> CompileLarge()
    {
        var compiler = new Compiler(_commands, CompilerOptions.Default);
        compiler.Compile(_largeProg);
        return compiler.Program;
    }
}
