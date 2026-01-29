namespace Tests;

public partial class ParserTests
{
    
    [Test]
    public void Taint_ValidTokenization()
    {
        var input = @"
#macro
    x = macro return test()
    # y = [x]
#endmacro
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertNoParseErrors();
    }

}