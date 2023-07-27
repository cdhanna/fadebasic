using DarkBasicYo;

namespace Tests;

public class ExpressionTests
{
    public Parser BuildParser(string src, out List<Token> tokens)
    {
        var lexer = new Lexer();
        tokens = lexer.Tokenize(src, TestCommands.Commands);
        var stream = new TokenStream(tokens);
        var parser = new Parser(stream, TestCommands.Commands);
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
    
    [TestCase("\"a\"", "(\"a\")")]
    [TestCase("\"a\" + \"b\"  ", "(add (\"a\"),(\"b\"))")]
    [TestCase("refDbl x", "(xcall refDbl (addr (ref x)))")]
    [TestCase("a.b + len a.c", "(add ((ref a).(ref b)),(xcall len ((ref a).(ref c))))")]
    [TestCase("*x", "(derefExpr (ref x))")]
    [TestCase("*x(3)", "(derefExpr (ref x[(3)]))")]
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
    [TestCase("x.y + 2", "(add ((ref x).(ref y)),(2))")]
    [TestCase("(z(3).x + n.g(1)) * 2", "(mult (add ((ref z[(3)]).(ref x)),((ref n).(ref g[(1)]))),(2))")]
    public void GeneralExpression(string src, string expected)
    {
        var parser = BuildParser(src, out _);
        var expr = parser.ParseWikiExpression();
        var str = expr.ToString();
        Assert.That(str, Is.EqualTo(expected));
    }
}