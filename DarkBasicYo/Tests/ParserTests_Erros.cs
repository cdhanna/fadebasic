namespace Tests;

public partial class ParserTests
{
    [Test]
    public void Errors_Simple()
    {
        var input = @"
1 +
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
    }
}