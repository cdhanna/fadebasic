using DarkBasicYo;

namespace Tests;

public class ExpressionTests
{
    public Parser BuildParser(string src, out List<Token> tokens)
    {
        var lexer = new Lexer();
        tokens = lexer.Tokenize(src);
        var stream = new TokenStream(tokens);
        var parser = new Parser(stream, StandardCommands.LimitedCommands);
        return parser;
    }
    
    // [Test]
    // public void Simple()
    // {
    //     var src = "1 + 2";
    //     var parser = BuildParser(src, out var tokens);
    //     var expr = parser.ParseCoolExpression(0, tokens.Count -1);
    //     var str = expr.ToString();
    //     Assert.That(str, Is.EqualTo("(add (1),(2))"));
    // }
    //
    // [Test]
    // public void MultiplyLater()
    // {
    //     var src = "1 + 2 * 3";
    //     var parser = BuildParser(src, out var tokens);
    //     var expr = parser.ParseCoolExpression(0, tokens.Count - 1);
    //     var str = expr.ToString();
    //     Assert.That(str, Is.EqualTo("(mult (add (1),(2)),(3))"));
    // }
    
    [TestCase("x", "(ref x)")]
    [TestCase("1", "(1)")]
    [TestCase("1+2", "(add (1),(2))")]
    [TestCase("(2)", "(2)")]
    [TestCase("1+2+3", "(add (1),(add (2),(3)))")]
    [TestCase("1+2+3+4", "(add (1),(add (2),(add (3),(4))))")]
    [TestCase("1+(2+3)+4", "(add (1),(add (add (2),(3)),(4)))")]
    [TestCase("(1+2)+3", "(add (add (1),(2)),(3))")]
    [TestCase("1+2*3", "(add (1),(mult (2),(3)))")]
    [TestCase("1*2+3", "(add (mult (1),(2)),(3))")]
    [TestCase("1-2+3", "(subtract (1),(add (2),(3)))")]
    [TestCase("1+(2+3*(4+5)+6)", "(add (1),(add (2),(add (mult (3),(add (4),(5))),(6))))")]
    public void GeneralExpression(string src, string expected)
    {
        var parser = BuildParser(src, out _);
        var expr = parser.ParseWikiExpression();
        var str = expr.ToString();
        Assert.That(str, Is.EqualTo(expected));
    }
}