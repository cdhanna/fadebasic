namespace Tests;

public partial class ParserTests
{
    
    
    [Test]
    public void Invoke_Simple()
    {
        var input = @"
x = Test()
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref x),(ref test[]))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Invoke_WithArg()
    {
        var input = @"
x = Test(1)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref x),(ref test[(1)]))
)".ReplaceLineEndings("")));
    }
    
    
    [Test]
    public void Invoke_Statement()
    {
        var input = @"
Test(1)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(expr (ref test[(1)]))
)".ReplaceLineEndings("")));
    }

    
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
        
        Assert.That(prog.functions.Count, Is.EqualTo(1));
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
ENDFUNCTION 0
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.functions.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(func hello (),((= (ref x),(1)),(retfunc (3)),(retfunc (0))))
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
        prog.AssertNoParseErrors();

        Assert.That(prog.functions.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(func hello (),((= (ref x),(1)),(retfunc (5))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Function_WithFollowingStatement()
    {
        var input = @"
FUNCTION hello()
    x = 1
ENDFUNCTION
x = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.functions.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref x),(2)),
(func hello (),((= (ref x),(1))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Function_ReturnExpr_WithFollowingStatement()
    {
        var input = @"
FUNCTION hello()
    x = 1
ENDFUNCTION x + 1
x = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.functions.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref x),(2)),
(func hello (),((= (ref x),(1)),(retfunc (+ (ref x),(1)))))
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
        
        Assert.That(prog.functions.Count, Is.EqualTo(1));
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
        
        Assert.That(prog.functions.Count, Is.EqualTo(1));
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
        
        Assert.That(prog.functions.Count, Is.EqualTo(1));
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
        
        Assert.That(prog.functions.Count, Is.EqualTo(1));
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
        
        Assert.That(prog.functions.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(func hello ((arg (ref x) as (integer)),(arg (ref y$) as (string))),((= (ref x),(1))))
)".ReplaceLineEndings("")));
    }


    [Test]
    public void Constant_LiteralInt()
    {
        var input = @"
#constant x 1
y = x
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        prog.AssertNoParseErrors();
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref y),(1))
)".ReplaceLineEndings("")));
    }
    
    
    [TestCase("ONE", "ONE")]
    [TestCase("ONE", "one")]
    [TestCase("one", "ONE")]
    [TestCase("one", "one")]
    [TestCase("oNe", "OnE")]
    public void Constant_LiteralInt_Casing(string macro, string usage)
    {
        var input = $@"
#constant {macro} 1
y = {usage} 
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        prog.AssertNoParseErrors();
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref y),(1))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Constant_LiteralInt2()
    {
        var input = @"
#constant x 1 
#constant z 3  
y = x + z
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref y),(+ (1),(3)))
)".ReplaceLineEndings("")));
    }

    
}