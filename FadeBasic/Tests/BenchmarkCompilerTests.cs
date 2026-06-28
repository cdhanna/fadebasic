using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;
using System.Linq;

namespace Tests;

[TestFixture]
public class BenchmarkCompilerTests
{
    private static CommandCollection Commands() => new CommandCollection(new FadeBasicCommands());

    private static (LexerResults lex, ProgramNode prog, List<byte> bytecode) Compile(string src)
    {
        var commands = Commands();
        var lex = new Lexer().TokenizeWithErrors(src, commands);
        var stream = new TokenStream(lex.tokens, lex.tokenErrors);
        var prog = new Parser(stream, commands).ParseProgram();
        var compiler = new Compiler(commands, CompilerOptions.Default);
        compiler.Compile(prog);
        return (lex, prog, compiler.Program);
    }

    [Test]
    public void Short_NoCompileErrors()
    {
        var (_, prog, _) = Compile(BenchmarkCorpus.Short);
        var errs = prog.GetAllErrors();
        Assert.That(errs.Count, Is.EqualTo(0),
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Short_ProducesBytecode()
    {
        var (_, _, bytecode) = Compile(BenchmarkCorpus.Short);
        Assert.That(bytecode.Count, Is.GreaterThan(0));
    }

    [Test]
    public void Medium_NoCompileErrors()
    {
        var (_, prog, _) = Compile(BenchmarkCorpus.Medium);
        var errs = prog.GetAllErrors();
        Assert.That(errs.Count, Is.EqualTo(0),
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Medium_ProducesBytecode()
    {
        var (_, _, bytecode) = Compile(BenchmarkCorpus.Medium);
        Assert.That(bytecode.Count, Is.GreaterThan(0));
    }

    [Test]
    public void Large_NoCompileErrors()
    {
        var (_, prog, _) = Compile(BenchmarkCorpus.Large);
        var errs = prog.GetAllErrors();
        Assert.That(errs.Count, Is.EqualTo(0),
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Large_ProducesBytecode()
    {
        var (_, _, bytecode) = Compile(BenchmarkCorpus.Large);
        Assert.That(bytecode.Count, Is.GreaterThan(0));
    }
}
