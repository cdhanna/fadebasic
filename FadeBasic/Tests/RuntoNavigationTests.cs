using FadeBasic;
using FadeBasic.Ast;

namespace Tests;

[TestFixture]
public class RuntoNavigationTests
{
    private ProgramNode Parse(string src, out List<ParseError> errors)
    {
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        lex.AssertNoLexErrors();
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        errors = prog.GetAllErrors();
        return prog;
    }

    [Test]
    public void Runto_DeclaredFromSymbol_PointsAtLabelDeclaration()
    {
        // After scope-resolution, a RuntoStatement's DeclaredFromSymbol should
        // point at the LabelDeclarationNode. This is what powers LSP
        // go-to-definition and find-references for runto sites.
        var src = @"
x = 5
checkpoint:
end

test foo
    runto checkpoint
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "expected clean parse; got: " + string.Join(", ", errs.Select(e => e.Display)));

        var runto = prog.tests[0].statements
            .OfType<RuntoStatement>()
            .First();
        Assert.That(runto.DeclaredFromSymbol, Is.Not.Null,
            "runto should have its target label resolved into DeclaredFromSymbol");
        Assert.That(runto.DeclaredFromSymbol.source, Is.TypeOf<LabelDeclarationNode>());

        var label = (LabelDeclarationNode)runto.DeclaredFromSymbol.source;
        Assert.That(label.label, Is.EqualTo("checkpoint"));
    }

    [Test]
    public void Runto_UnknownTarget_HasNullDeclaredFromSymbol()
    {
        // If the label doesn't exist anywhere, DeclaredFromSymbol stays null.
        // (The strictness visitor / compiler will surface the error.)
        var src = @"
end

test foo
    runto does_not_exist
endtest
";
        var prog = Parse(src, out _);
        var runto = prog.tests[0].statements
            .OfType<RuntoStatement>()
            .First();
        Assert.That(runto.DeclaredFromSymbol, Is.Null);
    }

    [Test]
    public void Runto_FunctionInternalLabel_ResolvesAcrossScopes()
    {
        // A test's `runto fnInner` should resolve to the function's internal
        // label even though the label is declared inside a function body.
        var src = @"
do_work()
end

function do_work()
fnInner:
endfunction

test foo
    runto fnInner
endtest
";
        var prog = Parse(src, out var errs);
        var runto = prog.tests[0].statements.OfType<RuntoStatement>().First();
        Assert.That(runto.DeclaredFromSymbol, Is.Not.Null);
        var label = (LabelDeclarationNode)runto.DeclaredFromSymbol.source;
        Assert.That(label.label, Is.EqualTo("fnInner").IgnoreCase);
    }
}
