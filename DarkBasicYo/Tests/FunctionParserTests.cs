namespace Tests;

public partial class ParserTests
{
    
    [Test]
    public void Function_Simple()
    {
        var input = @"
FUNCTION hello()
    x = 1
ENDFUNCTION
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(func hello (),((= (ref x),(1))))
)".ReplaceLineEndings("")));
    }

    
    
    
    [Test]
    public void Function_Return_Explicit()
    {
        var input = @"
FUNCTION hello()
    x = 1
EXITFUNCTION 3
ENDFUNCTION
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(func hello (),((= (ref x),(1)),(retfunc (3))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Function_Return_Prim()
    {
        var input = @"
FUNCTION hello()
    x = 1
ENDFUNCTION 5
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(func hello (),((= (ref x),(1)),(retfunc (5)))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Function_Arg()
    {
        var input = @"
FUNCTION hello(x)
    x = 1
ENDFUNCTION
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(func hello ((arg (ref x) as (integer))),((= (ref x),(1))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Function_Arg_AsPrim()
    {
        var input = @"
FUNCTION hello(x as byte)
    x = 1
ENDFUNCTION
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(func hello ((arg (ref x) as (byte))),((= (ref x),(1))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Function_Arg_AsStruct()
    {
        var input = @"
FUNCTION hello(x as myType)
    x = 1
ENDFUNCTION
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(func hello ((arg (ref x) as (typeRef mytype))),((= (ref x),(1))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Function_ArgMulti()
    {
        var input = @"
FUNCTION hello(x, y$)
    x = 1
ENDFUNCTION
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(func hello ((arg (ref x) as (integer)),(arg (ref y$) as (string))),((= (ref x),(1))))
)".ReplaceLineEndings("")));
    }

    [Test]
    public void Function_ArgMulti_Lines()
    {
        var input = @"
FUNCTION hello (
    x, 
    y$
)
    x = 1
ENDFUNCTION
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(func hello ((arg (ref x) as (integer)),(arg (ref y$) as (string))),((= (ref x),(1))))
)".ReplaceLineEndings("")));
    }


}