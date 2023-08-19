using DarkBasicYo;

namespace Tests;

public class VariableTests
{
    
    public Parser BuildParser(string src, out List<Token> tokens)
    {
        var lexer = new Lexer();
        tokens = lexer.Tokenize(src);
        var stream = new TokenStream(tokens);
        var parser = new Parser(stream, TestCommands.CommandsForTesting);
        return parser;
    }

    [TestCase("x", "(ref x)")]
    [TestCase("x(1)", "(ref x[(1)])")]
    [TestCase("x(1, 2)", "(ref x[(1),(2)])")]
    [TestCase("x(1, 2).z", "((ref x[(1),(2)]).(ref z))")]
    [TestCase("x.y", "((ref x).(ref y))")]
    [TestCase("x.y(1)", "((ref x).(ref y[(1)]))")]
    [TestCase("x.y.z", "((ref x).((ref y).(ref z)))")]
    [TestCase("*ptr", "(deref (ref ptr))")]
    public void GeneralVariable(string src, string expected)
    {
        var parser = BuildParser(src, out _);
        var expr = parser.ParseVariableReference();
        var str = expr.ToString();
        Assert.That(str, Is.EqualTo(expected));
    }
}