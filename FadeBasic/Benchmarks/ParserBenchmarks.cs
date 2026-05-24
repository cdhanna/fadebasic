using BenchmarkDotNet.Attributes;
using FadeBasic;
using FadeBasic.Ast;
using System.Linq;

namespace Benchmarks;

[MemoryDiagnoser]
public class ParserBenchmarks
{
    private CommandCollection _commands;
    private LexerResults _shortLex;
    private LexerResults _mediumLex;
    private LexerResults _largeLex;

    [GlobalSetup]
    public void Setup()
    {
        var lexer = new Lexer();
        _commands = new CommandCollection(new FadeBasicCommands());
        _shortLex  = lexer.TokenizeWithErrors(BenchmarkCorpus.Short,  _commands);
        _mediumLex = lexer.TokenizeWithErrors(BenchmarkCorpus.Medium, _commands);
        _largeLex  = lexer.TokenizeWithErrors(BenchmarkCorpus.Large,  _commands);

        ValidateCorpus(_shortLex,  nameof(BenchmarkCorpus.Short));
        ValidateCorpus(_mediumLex, nameof(BenchmarkCorpus.Medium));
        ValidateCorpus(_largeLex,  nameof(BenchmarkCorpus.Large));
    }

    private void ValidateCorpus(LexerResults lex, string name)
    {
        if (lex.tokenErrors is { Count: > 0 })
            throw new InvalidOperationException(
                $"Corpus '{name}' produced lex errors: {string.Join(", ", lex.tokenErrors.Select(e => e.Display))}");
    }

    [Benchmark(Baseline = true)]
    public ProgramNode ParseShort()
    {
        var stream = new TokenStream(_shortLex.tokens, _shortLex.tokenErrors);
        return new Parser(stream, _commands).ParseProgram();
    }

    [Benchmark]
    public ProgramNode ParseMedium()
    {
        var stream = new TokenStream(_mediumLex.tokens, _mediumLex.tokenErrors);
        return new Parser(stream, _commands).ParseProgram();
    }

    [Benchmark]
    public ProgramNode ParseLarge()
    {
        var stream = new TokenStream(_largeLex.tokens, _largeLex.tokenErrors);
        return new Parser(stream, _commands).ParseProgram();
    }
}
