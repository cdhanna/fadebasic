using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace Tests;

[TestFixture]
public class TestBlockParserTests
{
    private Lexer _lexer;
    private CommandCollection _commands;

    [SetUp]
    public void Setup()
    {
        _lexer = new Lexer();
        _commands = TestCommands.CommandsForTesting;
    }

    private ProgramNode Parse(string src, out List<ParseError> errors)
    {
        var lex = _lexer.TokenizeWithErrors(src, _commands);
        lex.AssertNoLexErrors();
        var parser = new Parser(lex.stream, _commands);
        var prog = parser.ParseProgram(new ParseOptions { ignoreChecks = true });
        errors = prog.GetAllErrors();
        return prog;
    }

    private ProgramNode ParseClean(string src)
    {
        var prog = Parse(src, out var errs);
        Assert.That(errs.Count, Is.EqualTo(0),
            "expected no parse errors, got: " + string.Join("\n", errs.Select(e => e.Display)));
        return prog;
    }

    [Test]
    public void Test_EmptyBlock_Parses()
    {
        var src = @"
test foo
endtest
";
        var prog = ParseClean(src);
        Assert.That(prog.tests.Count, Is.EqualTo(1));
        Assert.That(prog.tests[0].name, Is.EqualTo("foo"));
        Assert.That(prog.tests[0].isAbstract, Is.False);
        Assert.That(prog.tests[0].fromParent, Is.Null);
        Assert.That(prog.tests[0].testProgram.statements.Count, Is.EqualTo(0));
    }

    [Test]
    public void Test_AbstractBlock_Parses()
    {
        var src = @"
abstract test foo
endtest
";
        var prog = ParseClean(src);
        Assert.That(prog.tests.Count, Is.EqualTo(1));
        Assert.That(prog.tests[0].isAbstract, Is.True);
        Assert.That(prog.tests[0].name, Is.EqualTo("foo"));
    }

    [Test]
    public void Test_FromParent_Parses()
    {
        var src = @"
test child from root
endtest
";
        var prog = ParseClean(src);
        Assert.That(prog.tests.Count, Is.EqualTo(1));
        Assert.That(prog.tests[0].name, Is.EqualTo("child"));
        Assert.That(prog.tests[0].fromParent, Is.EqualTo("root"));
    }

    [Test]
    public void Test_AbstractFromParent_Parses()
    {
        var src = @"
abstract test base from grand
endtest
";
        var prog = ParseClean(src);
        Assert.That(prog.tests.Count, Is.EqualTo(1));
        Assert.That(prog.tests[0].isAbstract, Is.True);
        Assert.That(prog.tests[0].fromParent, Is.EqualTo("grand"));
    }

    [Test]
    public void Test_MissingEndtest_Errors()
    {
        var src = @"
test foo
";
        var prog = Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestMissingEndTest)), Is.True,
            "expected TestMissingEndTest error");
    }

    [Test]
    public void Test_MissingName_Errors()
    {
        var src = @"
test
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestMissingName)), Is.True,
            "expected TestMissingName error");
    }

    [Test]
    public void Test_AbstractWithoutTest_Errors()
    {
        var src = @"
abstract foo
";
        var prog = Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.AbstractRequiresTest)), Is.True,
            "expected AbstractRequiresTest error");
    }

    [Test]
    public void Test_NestedInsideTest_Errors()
    {
        var src = @"
test outer
    test inner
    endtest
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.TestDefinedInsideTest)), Is.True,
            "expected TestDefinedInsideTest error");
    }

    [Test]
    public void Test_MultipleBlocks_AllParsed()
    {
        var src = @"
test alpha
endtest

test beta
endtest

test gamma
endtest
";
        var prog = ParseClean(src);
        Assert.That(prog.tests.Count, Is.EqualTo(3));
        Assert.That(prog.tests[0].name, Is.EqualTo("alpha"));
        Assert.That(prog.tests[1].name, Is.EqualTo("beta"));
        Assert.That(prog.tests[2].name, Is.EqualTo("gamma"));
    }

    [Test]
    public void Test_BlockContainsStatements()
    {
        var src = @"
test foo
    x = 5
    y = 10
endtest
";
        var prog = ParseClean(src);
        Assert.That(prog.tests.Count, Is.EqualTo(1));
        Assert.That(prog.tests[0].testProgram.statements.Count, Is.EqualTo(2));
    }

    [Test]
    public void Test_TestNodeNotInProgramFunctions()
    {
        var src = @"
test foo
endtest
";
        var prog = ParseClean(src);
        Assert.That(prog.functions.Count, Is.EqualTo(0));
        Assert.That(prog.tests.Count, Is.EqualTo(1));
    }

    [Test]
    public void Test_ProgramAndTest_BothParsed()
    {
        var src = @"
x = 5

test foo
    y = 10
endtest
";
        var prog = ParseClean(src);
        Assert.That(prog.tests.Count, Is.EqualTo(1));
        Assert.That(prog.statements.Count, Is.GreaterThan(0));
    }

    [Test]
    public void Test_ToString_ShowsTest()
    {
        var src = @"
test foo
endtest
";
        var prog = ParseClean(src);
        Assert.That(prog.ToString(), Does.Contain("test foo"));
    }

    [Test]
    public void Test_Abstract_ToString_ShowsAbstract()
    {
        var src = @"
abstract test foo
endtest
";
        var prog = ParseClean(src);
        Assert.That(prog.ToString(), Does.Contain("abstract test foo"));
    }
}
