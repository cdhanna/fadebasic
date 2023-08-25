using DarkBasicYo;

namespace Tests;

public class TokenizeTests
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
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralReal));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo(".5"));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.EndStatement));
    }

    
    [Test]
    public void Tokenize_Command()
    {
        var input = @"print";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, TestCommands.CommandsForTesting);
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("print"));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.EndStatement));

    }
    
    
    [Test]
    public void Tokenize_CommandArgs()
    {
        var input = @"print 1,  4  , 2";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, TestCommands.CommandsForTesting);
        
        Assert.That(tokens.Count, Is.EqualTo(7));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("print"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[1].caseInsensitiveRaw, Is.EqualTo("1"));
        
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.ArgSplitter));

        Assert.That(tokens[3].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[3].caseInsensitiveRaw, Is.EqualTo("4"));
        
        Assert.That(tokens[4].type, Is.EqualTo(LexemType.ArgSplitter));
        
        Assert.That(tokens[5].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[5].caseInsensitiveRaw, Is.EqualTo("2"));
        
        Assert.That(tokens[6].type, Is.EqualTo(LexemType.EndStatement));

    }

    
    [Test]
    public void Tokenize_CommandVar()
    {
        var input = @"print hank";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, TestCommands.CommandsForTesting);
        
        Assert.That(tokens.Count, Is.EqualTo(3));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("print"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.VariableGeneral));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(6));
        Assert.That(tokens[1].caseInsensitiveRaw, Is.EqualTo("hank"));
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.EndStatement));

    }

    
    [Test]
    public void Tokenize_CommandVarReal()
    {
        var input = @"print f#";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, TestCommands.CommandsForTesting);
        
        Assert.That(tokens.Count, Is.EqualTo(3));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("print"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.VariableReal));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(6));
        Assert.That(tokens[1].caseInsensitiveRaw, Is.EqualTo("f#"));
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.EndStatement));

    }


    private static void CheckTokens(LexemType[] expectedLexemTypes, List<Token> tokens)
    {
        
        for (var i = 0; i < expectedLexemTypes.Length && i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (i >= expectedLexemTypes.Length)
            {
                Assert.Fail("there are too many tokens!");
            }

            var expected = expectedLexemTypes[i];
            Assert.That(token.type, Is.EqualTo(expected), $"at index=[{i}], expected to find {expected}, but found {token.type}, as [{token.caseInsensitiveRaw}]");
        }
        Assert.That(tokens.Count, Is.EqualTo(expectedLexemTypes.Length));
    }

    [Test]
    public void Tokenize_Command_Eos()
    {
    var input = @"
x = min 5
, 8 * 2";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, TestCommands.CommandsForTesting);

        var expectedLexemTypes = new LexemType[]
        {
            LexemType.VariableGeneral,
            LexemType.OpEqual,
            LexemType.CommandWord,
            LexemType.LiteralInt,
            LexemType.ArgSplitter,
            LexemType.LiteralInt,
            LexemType.OpMultiply,
            LexemType.LiteralInt,
            LexemType.EndStatement,
        };
        CheckTokens(expectedLexemTypes, tokens);
    }

    [Test]
    public void Tokenize_Command_Eos2()
    {
        var input = @"
x = min 5,
8 * 2";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, TestCommands.CommandsForTesting);

        var expectedLexemTypes = new LexemType[]
        {
            LexemType.VariableGeneral,
            LexemType.OpEqual,
            LexemType.CommandWord,
            LexemType.LiteralInt,
            LexemType.ArgSplitter,
            LexemType.LiteralInt,
            LexemType.OpMultiply,
            LexemType.LiteralInt,
            LexemType.EndStatement,
        };
        CheckTokens(expectedLexemTypes, tokens);
    }



    [Test]
    public void Tokenize_CommandVarStr()
    {
        var input = @"print f$";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, TestCommands.CommandsForTesting);
        
        Assert.That(tokens.Count, Is.EqualTo(3));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("print"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.VariableString));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(6));
        Assert.That(tokens[1].caseInsensitiveRaw, Is.EqualTo("f$"));
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.EndStatement));

    }

    
    [Test]
    public void Tokenize_CommandMultiPart()
    {
        var input = @"wait key";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, TestCommands.CommandsForTesting);
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("wait key"));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.EndStatement));

    }

    
    [Test]
    public void Tokenize_CommandMultiPart_AnyWhitespace()
    {
        var input = @"wait     key";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, TestCommands.CommandsForTesting);
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("wait     key"));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.EndStatement));

    }

    
    [Test]
    public void Tokenize_Word()
    {
        var input = @"tuna";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.VariableGeneral));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("tuna"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.EndStatement));

    }

    [Test]
    public void Tokenize_Words()
    {
        var input = @"tuna fish in a can";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(6));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.VariableGeneral));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("tuna"));
        
        Assert.That(tokens[4].type, Is.EqualTo(LexemType.VariableGeneral));
        Assert.That(tokens[4].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[4].charNumber, Is.EqualTo(15));
        Assert.That(tokens[4].caseInsensitiveRaw, Is.EqualTo("can"));
        Assert.That(tokens[5].type, Is.EqualTo(LexemType.EndStatement));

    }

    
    [Test]
    public void Tokenize_Comma()
    {
        var input = @"y,y * 2";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(6));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.VariableGeneral));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.ArgSplitter));
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.VariableGeneral));
        Assert.That(tokens[3].type, Is.EqualTo(LexemType.OpMultiply));
        Assert.That(tokens[4].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[5].type, Is.EqualTo(LexemType.EndStatement));

    }
    
    
    [Test]
    public void Tokenize_RemStart_RemEnd()
    {
        var input = @"
Remstart
okay
Remend
";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.KeywordRemStart));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.KeywordRemEnd));
        // Assert.That(tokens[2].type, Is.EqualTo(LexemType.EndStatement));

    }

    
    [Test]
    public void Tokenize_CaseInsensitiveKeywords()
    {
        var input = @"as As AS aS";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(5));

        foreach (var token in tokens.Take(4))
        {
            Assert.That(token.type, Is.EqualTo(LexemType.KeywordAs));
        }
        Assert.That(tokens[4].type, Is.EqualTo(LexemType.EndStatement));

    }

    [Test]
    public void Tokenize_While()
    {
        var input = @"
while x
print x
endwhile
";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, TestCommands.CommandsForTesting);
        
        Assert.That(tokens.Count, Is.EqualTo(8));
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.KeywordWhile));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.VariableGeneral));
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.EndStatement));

        Assert.That(tokens[3].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[4].type, Is.EqualTo(LexemType.VariableGeneral));
        Assert.That(tokens[5].type, Is.EqualTo(LexemType.EndStatement));

        Assert.That(tokens[6].type, Is.EqualTo(LexemType.KeywordEndWhile));
        Assert.That(tokens[7].type, Is.EqualTo(LexemType.EndStatement));


    }
    
    
    [Test]
    public void Tokenize_MultiLineWithMultipleStatements()
    {
        var input = @"x= 5
print x";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, TestCommands.CommandsForTesting);
        
        Assert.That(tokens.Count, Is.EqualTo(7));
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.VariableGeneral));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.OpEqual));
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[3].type, Is.EqualTo(LexemType.EndStatement));
        Assert.That(tokens[4].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[5].type, Is.EqualTo(LexemType.VariableGeneral));
        Assert.That(tokens[6].type, Is.EqualTo(LexemType.EndStatement));

    }
    [Test]
    public void Tokenize_AsInteger()
    {
        var input = @"x AS INTEGER";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(4));
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.VariableGeneral));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.KeywordAs));
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.KeywordTypeInteger));
        Assert.That(tokens[3].type, Is.EqualTo(LexemType.EndStatement));

    }
    
    
    [Test]
    public void Tokenize_Ptr_AddressOf()
    {
        var input = @"*";
        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        Assert.That(tokens.Count, Is.EqualTo(2));
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.OpMultiply));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.EndStatement));
    }

    [Test]
    public void Tokenize_Ptr_AddressOfVar()
    {
        var input = @"*x$";
        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        Assert.That(tokens.Count, Is.EqualTo(3));
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.OpMultiply));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.VariableString));
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.EndStatement));
    }

    
    [Test]
    public void Tokenize_Reals()
    {
        var input = @"23.1 5.3 .5";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(4));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralReal));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("23.1"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.LiteralReal));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(5));
        Assert.That(tokens[1].caseInsensitiveRaw, Is.EqualTo("5.3"));
        
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.LiteralReal));
        Assert.That(tokens[2].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[2].charNumber, Is.EqualTo(9));
        Assert.That(tokens[2].caseInsensitiveRaw, Is.EqualTo(".5"));
        
        Assert.That(tokens[3].type, Is.EqualTo(LexemType.EndStatement));

    }
    
    
    [Test]
    public void Tokenize_String()
    {
        var input = "\"hello world\"";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralString));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("\"hello world\""));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.EndStatement));

        
    }
    [Test]
    public void Tokenize_StringExpr()
    {
        var input = "\"a\" + \"b\" ";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(4));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralString));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("\"a\""));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.OpPlus));
        
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.LiteralString));
        Assert.That(tokens[2].caseInsensitiveRaw, Is.EqualTo("\"b\""));
        
        Assert.That(tokens[3].type, Is.EqualTo(LexemType.EndStatement));
    }
    
    
    [Test]
    public void Tokenize_StringWithInnerQuote()
    {
        var input = "1 \"hello \\\" world\" 2";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(4));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.LiteralString));
        Assert.That(tokens[1].caseInsensitiveRaw, Is.EqualTo("\"hello \\\" world\""));
        
        
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("1"));
        
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[2].caseInsensitiveRaw, Is.EqualTo("2"));
        Assert.That(tokens[3].type, Is.EqualTo(LexemType.EndStatement));


    }
    
    
    
    [Test]
    public void Tokenize_Ifs()
    {
        var input = @"if then else endif";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(5));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.KeywordIf));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.KeywordThen));
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.KeywordElse));
        Assert.That(tokens[3].type, Is.EqualTo(LexemType.KeywordEndIf));

    }

    
    [Test]
    public void Tokenize_Label()
    {
        var input = @"tuna:";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.VariableGeneral));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("tuna"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.EndStatement));

    }

    
    [Test]
    public void Tokenize_MultiCommand()
    {
        var input = @"wait key";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input, TestCommands.CommandsForTesting);
        
        Assert.That(tokens.Count, Is.EqualTo(2));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.CommandWord));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("wait key"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.EndStatement));

    }
    
    [Test]
    public void Tokenize_Power()
    {
        var input = @"a ^ 2";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(4));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.VariableGeneral));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("a"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.OpPower));
        Assert.That(tokens[1].caseInsensitiveRaw, Is.EqualTo("^"));
        
        
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[2].caseInsensitiveRaw, Is.EqualTo("2"));
        
        Assert.That(tokens[3].type, Is.EqualTo(LexemType.EndStatement));

    }

    
    [Test]
    public void Tokenize_NumbersAndWhiteSpace()
    {
        var input = @"23 5";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(3));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("23"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(3));
        Assert.That(tokens[1].caseInsensitiveRaw, Is.EqualTo("5"));
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.EndStatement));

    }
    
    [Test]
    public void Tokenize_NumbersAndWhiteSpaceNewLine()
    {
        var input = "23\n5";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(4));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("23"));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.EndStatement));

        Assert.That(tokens[2].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[2].lineNumber, Is.EqualTo(1));
        Assert.That(tokens[2].charNumber, Is.EqualTo(0));
        Assert.That(tokens[2].caseInsensitiveRaw, Is.EqualTo("5"));
        Assert.That(tokens[3].type, Is.EqualTo(LexemType.EndStatement));

    }
    
    
    [Test]
    public void Tokenize_NumbersMinus()
    {
        var input = @"23-5";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(4));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("23"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.OpMinus));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(2));
        Assert.That(tokens[1].caseInsensitiveRaw, Is.EqualTo("-"));
        
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[2].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[2].charNumber, Is.EqualTo(3));
        Assert.That(tokens[2].caseInsensitiveRaw, Is.EqualTo("5"));
        Assert.That(tokens[3].type, Is.EqualTo(LexemType.EndStatement));

    }
    [Test]
    public void Tokenize_NumbersAdd()
    {
        var input = @"23+5";

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(input);
        
        Assert.That(tokens.Count, Is.EqualTo(4));
        
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[0].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[0].charNumber, Is.EqualTo(0));
        Assert.That(tokens[0].caseInsensitiveRaw, Is.EqualTo("23"));
        
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.OpPlus));
        Assert.That(tokens[1].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[1].charNumber, Is.EqualTo(2));
        Assert.That(tokens[1].caseInsensitiveRaw, Is.EqualTo("+"));
        
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.LiteralInt));
        Assert.That(tokens[2].lineNumber, Is.EqualTo(0));
        Assert.That(tokens[2].charNumber, Is.EqualTo(3));
        Assert.That(tokens[2].caseInsensitiveRaw, Is.EqualTo("5"));
        
        Assert.That(tokens[3].type, Is.EqualTo(LexemType.EndStatement));

    }
}