using DarkBasicYo;

namespace Tests;

public class Tests
{
    [SetUp]
    public void Setup()
    {
    }
    
    
    [Test]
    public void Tokenize_RealsHalf()
    {
        var input = @".5";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(1));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralReal));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo(".5"));
        
    }

    
    [Test]
    public void Tokenize_Command()
    {
        var input = @"print";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, StandardCommands.LimitedCommands);
        
        Assert.That(tokens.Count, Is.EqualTo(1));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo("print"));
    }
    
    
    [Test]
    public void Tokenize_CommandVar()
    {
        var input = @"print tuna";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, StandardCommands.LimitedCommands);
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo("print"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.VariableInteger));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(6));
        Assert.That(tokens[1].raw, Is.EqualTo("tuna"));
    }

    
    [Test]
    public void Tokenize_CommandVarReal()
    {
        var input = @"print tuna#";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, StandardCommands.LimitedCommands);
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo("print"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.VariableReal));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(6));
        Assert.That(tokens[1].raw, Is.EqualTo("tuna#"));
    }
    
    
    [Test]
    public void Tokenize_CommandVarStr()
    {
        var input = @"print tuna$";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, StandardCommands.LimitedCommands);
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo("print"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.VariableString));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(6));
        Assert.That(tokens[1].raw, Is.EqualTo("tuna$"));
    }

    
    [Test]
    public void Tokenize_CommandMultiPart()
    {
        var input = @"wait key";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, StandardCommands.LimitedCommands);
        
        Assert.That(tokens.Count, Is.EqualTo(1));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo("wait key"));
    }

    
    [Test]
    public void Tokenize_CommandMultiPart_AnyWhitespace()
    {
        var input = @"wait     key";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, StandardCommands.LimitedCommands);
        
        Assert.That(tokens.Count, Is.EqualTo(1));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo("wait     key"));
    }

    
    [Test]
    public void Tokenize_Word()
    {
        var input = @"tuna";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(1));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.VariableInteger));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo("tuna"));
    }

    [Test]
    public void Tokenize_Words()
    {
        var input = @"tuna fish in a can";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(5));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.VariableInteger));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo("tuna"));
        
        Assert.That(tokens[4].type, Is.EqualTo(LexemType.VariableInteger));
        Assert.That(tokens[4].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[4].charNumber, Is.EqualTo(15));
        Assert.That(tokens[4].raw, Is.EqualTo("can"));
    }

    
    [Test]
    public void Tokenize_Reals()
    {
        var input = @"23.1 5.3 .5";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(3));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralReal));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo("23.1"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.LiteralReal));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(5));
        Assert.That(tokens[1].raw, Is.EqualTo("5.3"));
        
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.LiteralReal));
        Assert.That(tokens[2].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[2].charNumber, Is.EqualTo(9));
        Assert.That(tokens[2].raw, Is.EqualTo(".5"));
    }

    [Test]
    public void Tokenize_NumbersAndWhiteSpace()
    {
        var input = @"23 5";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo("23"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(3));
        Assert.That(tokens[1].raw, Is.EqualTo("5"));
    }
    
    [Test]
    public void Tokenize_NumbersAndWhiteSpaceNewLine()
    {
        var input = "23\n5";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo("23"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(1));
        Assert.That(tokens[1].charNumber, Is.EqualTo(0));
        Assert.That(tokens[1].raw, Is.EqualTo("5"));
    }
    
    
    [Test]
    public void Tokenize_NumbersMinus()
    {
        var input = @"23-5";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(3));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo("23"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.OpMinus));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(2));
        Assert.That(tokens[1].raw, Is.EqualTo("-"));
        
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[2].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[2].charNumber, Is.EqualTo(3));
        Assert.That(tokens[2].raw, Is.EqualTo("5"));
    }
    [Test]
    public void Tokenize_NumbersAdd()
    {
        var input = @"23+5";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(3));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].raw, Is.EqualTo("23"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.OpPlus));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(2));
        Assert.That(tokens[1].raw, Is.EqualTo("+"));
        
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[2].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[2].charNumber, Is.EqualTo(3));
        Assert.That(tokens[2].raw, Is.EqualTo("5"));
    }
}