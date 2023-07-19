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
        _commands = StandardCommands.LimitedCommands;
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
        Assert.That(code, Is.EqualTo("((print (12)))"));
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
        Assert.That(code, Is.EqualTo("((= (x),(5)),(print (x)))"));
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
        Assert.That(code, Is.EqualTo("((= (x),(subtract (x),(1))))"));
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
        Assert.That(code, Is.EqualTo("((= (x),(greaterthan (add (1),(2)),(3))))"));
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
        Assert.That(code, Is.EqualTo("((= (x),(5)),(while (greaterthan (x),(1)) (print (x)),(= (x),(subtract (x),(1)))))"));
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
        Assert.That(code, Is.EqualTo("((= (x),(add (1),(2))))"));
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
        Assert.That(code, Is.EqualTo("((= (x#),(2.1)))"));
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
        Assert.That(code, Is.EqualTo("((= (x),(2.1)))"));
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