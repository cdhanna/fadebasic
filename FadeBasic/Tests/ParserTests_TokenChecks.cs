using FadeBasic.Ast;

namespace Tests;

public partial class ParserTests
{
    
    [Test]
    public void TokenCheck_CommandStatement()
    {
        var input = @"
print ""tuna""
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var statement = prog.statements[0];
        Assert.That(statement.StartToken, Is.EqualTo(_lexerResults.tokens[0]));
        Assert.That(statement.EndToken, Is.EqualTo(_lexerResults.tokens[1]));
    }
    
    [Test]
    public void TokenCheck_ArrayRankInStatement()
    {
        var input = @"
dim sampleArr(5)
testFunc()
function testFunc()
    for x = 1 to 3
        if 3 > 5 then print sampleArr(x)
    next
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var forStatement = prog.functions[0].statements[0] as ForStatement;
        var ifStatement = forStatement.statements[0] as IfStatement;
        var positiveStatement = ifStatement.positiveStatements[0] as CommandStatement;
        var arrayExpr = positiveStatement.args[0] as ArrayIndexReference;
        var rankExpr = arrayExpr.rankExpressions[0];
        
        Assert.That(rankExpr.DeclaredFromSymbol, Is.Not.Null);
    }
    
    [Test]
    public void TokenCheck_ArrayRankInAssignment()
    {
        var input = @"
dim sampleArr(5)
testFunc()
function testFunc()
    for x = 1 to 3
        if 3 > 5 then sampleArr(x) = x
    next
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var forStatement = prog.functions[0].statements[0] as ForStatement;
        var ifStatement = forStatement.statements[0] as IfStatement;
        var positiveStatement = ifStatement.positiveStatements[0] as AssignmentStatement;
        var arrayExpr = positiveStatement.variable as ArrayIndexReference;
        var rankExpr = arrayExpr.rankExpressions[0];
        
        Assert.That(rankExpr.DeclaredFromSymbol, Is.Not.Null);
    }
    
    
    [Test]
    public void TokenCheck_Gosub()
    {
        var input = @"
gosub beep
beep:
return
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var gosub = prog.statements[0] as GoSubStatement;
        Assert.That(gosub.DeclaredFromSymbol, Is.Not.Null);
    }
    
    [Test]
    public void TokenCheck_Goto()
    {
        var input = @"
goto beep
beep:
return
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var gosub = prog.statements[0] as GotoStatement;
        Assert.That(gosub.DeclaredFromSymbol, Is.Not.Null);
    }

}