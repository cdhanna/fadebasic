using DarkBasicYo;
using DarkBasicYo.Ast;

namespace Tests;

public class ParserTests
{
    private Lexer _lexer;
    private CommandCollection _commands;

    [SetUp]
    public void Setup()
    {
        _lexer = new Lexer();
        _commands = TestCommands.Commands;
    }

    TokenStream Tokenize(string input) => new TokenStream(_lexer.Tokenize(input, _commands));

    Parser MakeParser(string input) => new Parser(Tokenize(input), _commands);
    
    [Test]
    public void Simple()
    {
        var input = "print 12";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<CommandStatement>());
        Assert.That(((CommandStatement)prog.statements[0]).command.command, Is.EqualTo("print"));
        Assert.That(((CommandStatement)prog.statements[0]).args.Count, Is.EqualTo(1));

        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((call print (12)))"));
    }
    
    
    [Test]
    public void String_Assign()
    {
        var input = "x$ = \"hello\"";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x$),(\"hello\")))"));
    }


    [Test]
    public void String_Assign2()
    {
        var input = "x = \"hello\"";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(\"hello\")))"));
    }

    
    [Test]
    public void String_Declare()
    {
        var input = "x as string";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((decl local,x,(string)))"));
    }

    
    [Test]
    public void MultiStatement()
    {
        var input = @"
x= 5
print x";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(5)),(call print (ref x)))"));
    }

    
    [Test]
    public void CallHostStatement()
    {
        var input = @"callTest";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.Commands));
        var parser = new Parser(tokenStream, TestCommands.Commands);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((call callTest))"));
    }

    [Test]
    public void CallHostStatement_Expr()
    {
        var input = @"x = 1 + add 2,3";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.Commands));
        var parser = new Parser(tokenStream, TestCommands.Commands);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(add (1),(xcall add (2),(3)))))"));
    }


    
    [Test]
    public void DecrementAssign()
    {
        var input = @"
x = x - 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(subtract (ref x),(1))))"));
    }
    
    
    [Test]
    public void AnasUnfunTest()
    {
        var input = @"
x = 1 + 2 > 3
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(greaterthan (add (1),(2)),(3))))"));
    }

    [Test]
    public void Doop()
    {
        var x = (1 * 3) > 3;
    }
        
    [Test]
    public void AnasUnfunTest_NegativeEdition()
    {
        var input = @"
x = -3 - -1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (x),(add (6),(subtract (666),(add (6),(greaterthan (6666666),(6)))))))"));
    }

    
    [Test]
    public void WhileLoop()
    {
        var input = @"
x= 5
while x > 1
print x
x = x - 1
endwhile
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((= (ref x),(5)),(while (greaterthan (ref x),(1)) (call print (ref x)),(= (ref x),(subtract (ref x),(1)))))"));
    }

    
    
    [Test]
    public void ArrayAssign()
    {
        var input = @"
dim x(10)
x(1) = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(dim global,x,(integer),((10))),
(= (ref x[(1)]),(2))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void ArrayAssignFlip()
    {
        var input = @"
y = x(1)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref y),(ref x[(1)]))
)".ReplaceLineEndings("")));
    }

    
    
    [Test]
    public void SimpleArray()
    {
        var input = @"dim x(10)";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((dim global,x,(integer),((10))))"));
    }

    
    [Test]
    public void SimpleDimLocal()
    {
        var input = @"local dim x(10)";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((dim local,x,(integer),((10))))"));
    }

    [Test]
    public void SimpleDimTyped()
    {
        var input = @"dim x(10) as byte";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((dim global,x,(byte),((10))))"));
    }
    
    [Test]
    public void SimpleDimScopedTyped()
    {
        var input = @"local dim x(10,n) as word";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((dim local,x,(word),((10),(ref n))))"));
    }

    
    [Test]
    public void SimpleArrayMultiDimension()
    {
        var input = @"dim x(10,2)";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((dim global,x,(integer),((10),(2))))"));
    }

    [Test]
    public void SimpleArrayMultiDimensionWithVars()
    {
        var input = @"
y = 3
dim x(y,y*2)";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((= (ref y),(3)),(dim global,x,(integer),((ref y),(mult (ref y),(2)))))"));
    }
    
    
    
    [Test]
    public void Type_Easy()
    {
        var input = @"
type egg
x as integer
endtype";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.typeDefinitions.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(type egg ((ref x) as (integer)))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Type_AnonField()
    {
        var input = @"
type egg
x
y
endtype";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.typeDefinitions.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(type egg ((ref x) as (integer)),((ref y) as (integer)))
)".ReplaceLineEndings("")));
    }
    
    
    [Test]
    public void Type_AnonField_String()
    {
        var input = @"
type egg
y$
endtype";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.typeDefinitions.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(type egg ((ref y$) as (string)))
)".ReplaceLineEndings("")));
    }
    
    
    [Test]
    public void Type_Nested()
    {
        var input = @"
type chicken
x as egg
endtype
type egg
y$
endtype";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(type chicken ((ref x) as (typeRef egg))),
(type egg ((ref y$) as (string)))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Type_Assignment()
    {
        var input = @"
type hotdog
x
endtype
y as hotdog
y.x = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(type hotdog ((ref x) as (integer))),
(decl local,y,(typeRef hotdog)),
(= ((ref y).(ref x)),(2))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Type_AfterOtherStuff()
    {
        var input = @"
y = 2
type hotdog
x
endtype
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(type hotdog ((ref x) as (integer))),
(= (ref y),(2))
)".ReplaceLineEndings("")));
    }

    
    
    [Test]
    public void AssignmentWithCommandAndField()
    {
        var input = @"
x = a.b + len a.c
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref x),(add ((ref a).(ref b)),(xcall len ((ref a).(ref c)))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void IntegerAssignment()
    {
        var input = "x = 2";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<AssignmentStatement>());

        var assignment = prog.statements[0] as AssignmentStatement;
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (x),(2)))"));
    }
    
    [Test]
    public void IntegerAssignmentToMath()
    {
        var input = "x = 1 + 2";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<AssignmentStatement>());

        var assignment = prog.statements[0] as AssignmentStatement;
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(add (1),(2))))"));
    }
    
    [Test]
    public void RealAssignment()
    {
        var input = "x# = 2.1";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<AssignmentStatement>());

        var assignment = prog.statements[0] as AssignmentStatement;
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x#),(2.1)))"));
    }
    
    
    [Test]
    public void InvalidAssignment_RealToInteger()
    {
        var input = "x = 2.1";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<AssignmentStatement>());

        var assignment = prog.statements[0] as AssignmentStatement;
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(2.1)))"));
    }
    
    [Test]
    public void Decl_Integer()
    {
        var input = "x AS INTEGER";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<DeclarationStatement>());

        var decl = prog.statements[0] as DeclarationStatement;
        Assert.That(decl.type.variableType, Is.EqualTo(VariableType.Integer));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((decl local,x,(integer)))"));
    }
    
    
    [Test]
    public void Decl_IntegerLocal()
    {
        var input = "local x AS INTEGER";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<DeclarationStatement>());

        var decl = prog.statements[0] as DeclarationStatement;
        Assert.That(decl.type.variableType, Is.EqualTo(VariableType.Integer));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((decl local,x,(integer)))"));
    }
    
    
    [Test]
    public void Decl_IntegerGlobal()
    {
        var input = "GLOBAL x AS INTEGER";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<DeclarationStatement>());

        var decl = prog.statements[0] as DeclarationStatement;
        Assert.That(decl.type.variableType, Is.EqualTo(VariableType.Integer));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((decl global,x,(integer)))"));
    }
}