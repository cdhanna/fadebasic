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
    public void IntegerAssignment()
    {
        var input = "x = 2";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<AssignmentStatement>());
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (x),(2)))"));
    }
}