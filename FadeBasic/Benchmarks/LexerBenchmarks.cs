using BenchmarkDotNet.Attributes;
using FadeBasic;

namespace Benchmarks;

[MemoryDiagnoser]
public class LexerBenchmarks
{
    private Lexer _lexer;
    private CommandCollection _commands;

    [GlobalSetup]
    public void Setup()
    {
        _lexer = new Lexer();
        _commands = new CommandCollection(new FadeBasicCommands());
        ValidateCorpus(BenchmarkCorpus.Short,  nameof(BenchmarkCorpus.Short));
        ValidateCorpus(BenchmarkCorpus.Medium, nameof(BenchmarkCorpus.Medium));
        ValidateCorpus(BenchmarkCorpus.Large,  nameof(BenchmarkCorpus.Large));
    }

    private void ValidateCorpus(string source, string name)
    {
        var result = _lexer.TokenizeWithErrors(source, _commands);
        if (result.tokenErrors is { Count: > 0 })
            throw new InvalidOperationException(
                $"Corpus '{name}' produced lex errors: {result.tokenErrors[0]}");
    }

    [Benchmark(Baseline = true)]
    public LexerResults LexShort() => _lexer.TokenizeWithErrors(BenchmarkCorpus.Short, _commands);

    [Benchmark]
    public LexerResults LexMedium() => _lexer.TokenizeWithErrors(BenchmarkCorpus.Medium, _commands);

    [Benchmark]
    public LexerResults LexLarge() => _lexer.TokenizeWithErrors(BenchmarkCorpus.Large, _commands);
}
