using FadeBasic;
using FadeBasic.Ast;
using System.Linq;

namespace Tests;

[TestFixture]
public class BenchmarkCorpusTests
{
    private static CommandCollection Commands() => new CommandCollection(new FadeBasicCommands());

    private static (LexerResults lex, ProgramNode prog) Compile(string src)
    {
        var commands = Commands();
        var lex = new Lexer().TokenizeWithErrors(src, commands);
        var stream = new TokenStream(lex.tokens, lex.tokenErrors);
        var prog = new Parser(stream, commands).ParseProgram();
        return (lex, prog);
    }

    [Test]
    public void Short_NoLexErrors()
    {
        var (lex, _) = Compile(BenchmarkCorpus.Short);
        Assert.That(lex.tokenErrors?.Count ?? 0, Is.EqualTo(0),
            string.Join(", ", lex.tokenErrors?.Select(e => e.Display) ?? Enumerable.Empty<string>()));
    }

    [Test]
    public void Short_NoParseErrors()
    {
        var (_, prog) = Compile(BenchmarkCorpus.Short);
        var errs = prog.GetAllErrors();
        Assert.That(errs.Count, Is.EqualTo(0),
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Medium_NoLexErrors()
    {
        var (lex, _) = Compile(BenchmarkCorpus.Medium);
        Assert.That(lex.tokenErrors?.Count ?? 0, Is.EqualTo(0),
            string.Join(", ", lex.tokenErrors?.Select(e => e.Display) ?? Enumerable.Empty<string>()));
    }

    [Test]
    public void Medium_NoParseErrors()
    {
        var (_, prog) = Compile(BenchmarkCorpus.Medium);
        var errs = prog.GetAllErrors();
        Assert.That(errs.Count, Is.EqualTo(0),
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Large_NoLexErrors()
    {
        var (lex, _) = Compile(BenchmarkCorpus.Large);
        Assert.That(lex.tokenErrors?.Count ?? 0, Is.EqualTo(0),
            string.Join(", ", lex.tokenErrors?.Select(e => e.Display) ?? Enumerable.Empty<string>()));
    }

    [Test]
    public void Large_NoParseErrors()
    {
        var (_, prog) = Compile(BenchmarkCorpus.Large);
        var errs = prog.GetAllErrors();
        Assert.That(errs.Count, Is.EqualTo(0),
            string.Join(", ", errs.Select(e => e.Display)));
    }
}
