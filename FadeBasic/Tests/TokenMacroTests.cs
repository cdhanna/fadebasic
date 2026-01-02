using FadeBasic;

namespace Tests;

public class TokenMacroTests
{
    [SetUp]
    public void Setup()
    {
    }
    
    public Parser BuildParser(string src, out List<Token> tokens)
    {
        var lexer = new Lexer();
        var results = lexer.TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        results.AssertNoLexErrors();
        tokens = results.tokens;
        var stream = new TokenStream(tokens);
        var parser = new Parser(stream, TestCommands.CommandsForTesting);
        return parser;
    }
    
    [Test]
    public void Macro_Parse_Subst_CanExist()
    {
        var input = @"
[ ]
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var str = prog.ToString();
        
    }

    [Test]
    public void Macro_Parse ()
    {
        var input = @"
a = 3
#tokenize
b = [a]
#endtokenize
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref a),(3)),(tokenize ((subst ((ref a))))))"));
    }

    
    [Test]
    public void Macro_Compile ()
    {
        var input = @"
#macro
    a = 3
    #tokenize
        b = [a]
    #endtokenize
    a = 4
#endmacro
c = b `b should be 3, because of the tokenization

";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref b),(3)),(= (ref c),(ref b)))"));
    }

    [Test]
    public void Macro_Compile_Math ()
    {
        var input = @"
#macro
    a = 3
    #tokenize
        b = [a + 2]
    #endtokenize
    a = 4
#endmacro
c = b

";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref b),(5)),(= (ref c),(ref b)))"));
    }

    [Test]
    public void Macro_Compile_CustomVariable ()
    {
        var input = @"
#macro
    a = 3
    #tokenize
        b_[a] = 12
    #endtokenize
#endmacro
c = b_3

";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref b_3),(12)),(= (ref c),(ref b_3)))"));
    }

    [Test]
    public void Macro_Compile_CustomVariables ()
    {
        var input = @"
#macro
    for n = 1 to 4
    #tokenize
        n[n] = [n]
    #endtokenize
    next 
#endmacro
c = n1 + n2 + n3 + n4

";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void Macro_Compile_NestedMacro ()
    {
        var input = @"
#macro
    #tokenize
        #macro
            x = 1
            #tokenize
                n = x
            #endtokenize
        #endmacro
    #endtokenize
#endmacro
c = n

";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    

    [Test]
    public void Macro_Compile_Function ()
    {
        var input = @"
#macro
    function ex(n)
        #tokenize
            n[n] = [n]
        #endtokenize
    endfunction
    ex(1)
    ex(2)
#endmacro

c = n1 + n2

";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void Macro_Compile_Function2 ()
    {
        var input = @"
#macro
    function declareArray(name$, size)
        #tokenize
            dim [name$]([size])
            [name$]Size = [size]
        #endtokenize
    endfunction
    declareArray(""toast"", 5)
#endmacro

for n = 0 to toastSize - 1
    print toast(n)
next 
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void Macro_Compile_Function_InvokedFromAnotherMacro ()
    {
        var input = @"
#macro
    function declareArray(name$, size)
        #tokenize
            dim [name$]([size])
            [name$]Size = [size]
        #endtokenize
    endfunction
#endmacro

#macro
declareArray(""toast"", 5)
#endmacro
for n = 0 to toastSize - 1
    print toast(n)
next 
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void Macro_BracketNotation()
    {
        var input = @"
[ ]
";
        var lexer = new Lexer();
        var output = lexer.TokenizeWithErrors(input);
        output.AssertNoLexErrors();
        var tokens = output.tokens; 
        Assert.That(tokens.Count, Is.EqualTo(3));
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.ConstantBracketOpen));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.ConstantBracketClose));
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.EndStatement));
    }
    
    [Test]
    public void Macro_BeginEnd()
    {
        var input = @"
#macro
#endmacro
";
        var lexer = new Lexer();
        var output = lexer.TokenizeWithErrors(input);
        output.AssertNoLexErrors();
        var tokens = output.allTokens; 
        Assert.That(tokens.Count, Is.EqualTo(4));
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.ConstantBegin));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.EndStatement));
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.ConstantEnd));
        Assert.That(tokens[3].type, Is.EqualTo(LexemType.EndStatement));
    }
    [Test]
    public void Macro_CommitEndCommit()
    {
        var input = @"
#tokenize
#endtokenize
";
        var lexer = new Lexer();
        var output = lexer.TokenizeWithErrors(input);
        output.AssertNoLexErrors();
        var tokens = output.tokens; 
        Assert.That(tokens.Count, Is.EqualTo(4));
        Assert.That(tokens[0].type, Is.EqualTo(LexemType.ConstantTokenize));
        Assert.That(tokens[1].type, Is.EqualTo(LexemType.EndStatement));
        Assert.That(tokens[2].type, Is.EqualTo(LexemType.ConstantEndTokenize));
        Assert.That(tokens[3].type, Is.EqualTo(LexemType.EndStatement));
    }

    // TODO: add test for nested begin/ends, it should fail. 
    
    [Test]
    public void Macro_Test()
    {
        var input = @"
#begin
a = 3

for n = 1 to 3
    #commit
        name_{{n}} = {{a + n}} `name_1 = 4
    #endcommit
next

`[""n""] = a `use [] to escape into runtime
#end
b = 2
`d = {a} `use {} to escape into compiletime
`c = n
";
        var lexer = new Lexer();
        var output = lexer.TokenizeWithErrors(input);
        output.AssertNoLexErrors();
        var tokens = output.tokens; 
    }
}