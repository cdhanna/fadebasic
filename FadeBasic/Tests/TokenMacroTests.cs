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
        var results = lexer.TokenizeWithErrors(src, TestCommands.CommandsForTesting, TestMacroCommands.CommandsForTesting);
        results.AssertNoLexErrors();
        tokens = results.tokens;
        var stream = new TokenStream(tokens);
        var parser = new Parser(stream, TestCommands.CommandsForTesting);
        return parser;
    }
    

    
    [Test]
    public void Macro_Simple_SingleLine_Tokenize1 ()
    {
        var input = @"
a = 3
#macro
    # b = 1
#endmacro
c = 2
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref a),(3)),(= (ref b),(1)),(= (ref c),(2)))"));
    }

    
    [Test]
    public void Macro_Simple_Subst ()
    {
        var input = @"
#macro
    #tokenize
        a = [1]
    #endtokenize
#endmacro
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref a),(1)))"));
    }

    
    [Test]
    public void Macro_Simple_Tokenize1 ()
    {
        var input = @"
a = 3
#macro
    #tokenize
        b = 1
    #endtokenize
#endmacro
c = 2
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref a),(3)),(= (ref b),(1)),(= (ref c),(2)))"));
    }
    
    
    [Test]
    public void Macro_Simple_Tokenize2 ()
    {
        var input = @"
a = 3
#macro
    #tokenize
        b = 1
    #endtokenize
    x = 1
    #tokenize
        c = 2
    #endtokenize
#endmacro
c = 3
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref a),(3)),(= (ref b),(1)),(= (ref c),(2)),(= (ref c),(3)))"));
    }
    
    
    [Test]
    public void Macro_Simple_Tokenize2_WithSomeSingle ()
    {
        var input = @"
a = 3
#macro
    #tokenize
        b = 1
    #endtokenize
    x = 1
    #c=2
#endmacro
c = 3
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref a),(3)),(= (ref b),(1)),(= (ref c),(2)),(= (ref c),(3)))"));
    }

    
    [Test]
    public void Macro_Simple_Tokenize2_WithBothSingle ()
    {
        var input = @"
a = 3
#macro
    # b = 1
    x = 1
    #c=2
#endmacro
c = 3
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref a),(3)),(= (ref b),(1)),(= (ref c),(2)),(= (ref c),(3)))"));
    }
    
    [Test]
    public void Macro_Multiple_Tokenize_1 ()
    {
        var input = @"
a = 3
#macro
    ` intentionally blank
#endmacro
c = 3
#macro
# x = 1
#endmacro
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref a),(3)),(= (ref c),(3)),(= (ref x),(1)))"));
    }

    
    [Test]
    public void Macro_Multiple_Tokenize_2 ()
    {
        var input = @"
a = 3
#macro
    # b = 1
#endmacro
c = 3
#macro
# x = 1
#endmacro
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref a),(3)),(= (ref b),(1)),(= (ref c),(3)),(= (ref x),(1)))"));
    }

    
    [Test]
    public void Macro_Multiple_Tokenize_3 ()
    {
        var input = @"
a = 3
#macro
    # b = 1
    ignore = 1
    #tokenize
    c = 2
    d = 3
    #endtokenize
#endmacro
e = 4

#macro
ignore = 2
if 2 = 2
    #tokenize
        f = 5
    #endtokenize
endif
# g = 1
#endmacro
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref a),(3)),(= (ref b),(1)),(= (ref c),(2)),(= (ref d),(3)),(= (ref e),(4)),(= (ref f),(5)),(= (ref g),(1)))"));
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
    public void Macro_Compile_MultiLine ()
    {
        var input = @"
#macro
    a = 3
    #tokenize
        b = [a]
        b2 = [a + 1]
    #endtokenize
    a = 4
#endmacro
c = b `b should be 3, because of the tokenization

";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref b),(3)),(= (ref b2),(4)),(= (ref c),(ref b)))"));
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
    public void Macro_Compile_InjectObject ()
    {
        var input = @"
#macro
    a = 3
    type egg
        n
    endtype
    e as egg
    e.n = a
    #tokenize
        [e] `what would this even do!? How does one inject an object into a macro? 
    #endtokenize
#endmacro

";
        Assert.Fail("come back and think about this case. I think probably there needs to be an error case where you can only inject literals");
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref b_3),(12)),(= (ref c),(ref b_3)))"));
    }
    
    
    [Test]
    public void Macro_Compile_CustomVariable_Float ()
    {
        var input = @"
#macro
    a = 3
    #tokenize

        b_[a]# = 12 `concat token to left. ex: b_3
       
    #endtokenize
#endmacro
c = b_3#

";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref b_3#),(12)),(= (ref c),(ref b_3#)))"));
    }
    
    
    [Test]
    public void Macro_Compile_BuildArray ()
    {
        var input = @"
#macro
    a = 3
    #tokenize    
    dim x(10)
    #endtokenize
    for n = 0 to 9
        #tokenize
            x([n]) = [(n+1) * 2]
        #endtokenize
    next
#endmacro
c = x(0) + x(3) `(1*2) + (3*2)

";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((dim global,x,(integer),((10))),(= (ref x[(0)]),(2)),(= (ref x[(1)]),(4)),(= (ref x[(2)]),(6)),(= (ref x[(3)]),(8)),(= (ref x[(4)]),(10)),(= (ref x[(5)]),(12)),(= (ref x[(6)]),(14)),(= (ref x[(7)]),(16)),(= (ref x[(8)]),(18)),(= (ref x[(9)]),(20)),(= (ref c),(+ (ref x[(0)]),(ref x[(3)]))))"));
    }

    
    [Test]
    public void Macro_Compile_CustomVariable_Right ()
    {
        var input = @"
#macro
    a = 3
    #tokenize

        b_[a] = 12 `concat token to left. ex: b_3
       
        `[""_"" + str$(a) ]x = 12 `concat token to right. ex: _3x

        `b_[a]_gen = 12 `concat together. 'b_' + whatever is + '_gen'. ex: b_3_gen
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
    public void Macro_Compile_CustomVariable_Left ()
    {
        var input = @"
#macro
    a = 3
    #tokenize
        [""x""]a = 12
    #endtokenize
#endmacro
c = xa

";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref xa),(12)),(= (ref c),(ref xa)))"));
    }
    
    [Test]
    public void Macro_Compile_SingleLineTokenize ()
    {
        var input = @"
#macro
    a = 3
    # b[""x""]a = 12
#endmacro
c = bxa

";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref bxa),(12)),(= (ref c),(ref bxa)))"));
    }
    
    [Test]
    public void Macro_Compile_SingleLineTokenize2 ()
    {
        var input = @"
#macro
    # a = [1]
    # b = [2]
#endmacro
c = a + b
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref a),(1)),(= (ref b),(2)),(= (ref c),(+ (ref a),(ref b))))"));
    }
    
    [Test]
    public void Macro_Compile_SingleLineTokenize3 ()
    {
        var input = @"
#macro
    # [""a""] = 1
    # [""b""] = 2
#endmacro
c = a + b
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref a),(1)),(= (ref b),(2)),(= (ref c),(+ (ref a),(ref b))))"));
    }
    
    [Test]
    public void Macro_Compile_CustomVariable_Center ()
    {
        var input = @"
#macro
    a = 3
    #tokenize
        b[""x""]a = 12
    #endtokenize
#endmacro
c = bxa

";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref bxa),(12)),(= (ref c),(ref bxa)))"));
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
    public void Macro_Compile_Function_InvokedFromAnotherMacro_SingleLine ()
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

# declareArray(""toast"", 5)

for n = 0 to toastSize - 1
    print toast(n)
next 
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [TestCase("single-line macro", @"
a = 3
", 
        @"
# b = 2
a = 3
")]
    [TestCase("single-line tokenize",@"
a = 3
c = 1
", 
        @"
#macro
# c = 1
#endmacro
a = 3
")]
    [TestCase("blank macro",@"
a = 3
", 
        @"
#macro
#endmacro
a = 3
")]
    [TestCase("blank single line macro",@"
", 
        @"
#
")]
    [TestCase("lots of blank single line macro",@"


", 
        @"
#
#
#
")]
    [TestCase("no tokenization",@"
a = 3
", 
        @"
#macro
` these tokens should not appear in the output
b = 2
c = 1
#endmacro
a = 3
")]
    
    [TestCase("tokenize",@"
a = 3
c = 1
", 
        @"
#macro
    b = 2
    #tokenize
        c = 1
    #endtokenize
#endmacro

a = 3
")]
    
    [TestCase("empty tokenize maps to nothing",@"
", 
        @"
#macro
b = 2
#tokenize
#endtokenize
#endmacro
")]
    [TestCase("substitution",@"
c = 2
", 
        @"
#macro
    b = 2
    #tokenize
        c = [b]
    #endtokenize
#endmacro
")]
    [TestCase("double macro",@"
c = 2
", 
        @"
#macro
    b = 2
#endmacro

#macro
    #tokenize
        c = [b]
    #endtokenize
#endmacro
")]
    [TestCase("double macro with single line tokenize",@"
c = 2
", 
        @"
#macro
b = 2
#endmacro

#macro
    # c = [b]
#endmacro
")]
    [TestCase("double macro function",@"
c = 2
", 
        @"
#macro
function decl(x)
    # c = [x]
endfunction
#endmacro

#macro
decl(2)
#endmacro
")]
    [TestCase("double macro function on single line (reverse)",@"
c = 2
", 
        @"
# decl(2)
#macro
function decl(x)
    # c = [x]
endfunction
#endmacro

")]
    [TestCase("double macro function on single line",@"
c = 2
", 
        @"
#macro
function decl(x)
    # c = [x]
endfunction
#endmacro

# decl(2)
")]
    [TestCase("blank tokenize",@"
", 
        @"
#macro
#tokenize
#endtokenize
#endmacro
")]
    [TestCase("blank single line tokenize",@"
", 
        @"
#macro
#
#endmacro
")]
    [TestCase("token patching",@"
x = 
1
", 
        @"
#macro
# x = 
#endmacro
1
")]
    [TestCase("token patching 2",@"
x = 
1
", 
        @"
#macro
    #tokenize
        x = 
    #endtokenize
#endmacro
1
")]
    public void Macro_TokenComparison(string explanation, string a, string b)
    {
        var lexer = new Lexer();
        var aResults = lexer.TokenizeWithErrors(a, TestCommands.CommandsForTesting);
        var bResults = lexer.TokenizeWithErrors(b, TestCommands.CommandsForTesting);
         
        Console.WriteLine("------start");
        Console.WriteLine(a);
        Console.WriteLine("------mid");
        Console.WriteLine(b);
        Console.WriteLine("------end");
        TokenizeTests.CheckTokens(aResults.tokens, bResults.tokens);   
    }
    
    [Test]
    public void Macro_Compile_ReverseSubst_BaseCase2()
    {
        var input = @"
# a = 4
#macro
#tokenize
b = [a]
#endtokenize
#endmacro
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        
        Assert.That(code, Is.EqualTo("((= (ref b),(4)))"));
    }
    
    [Test]
    public void Macro_Compile_ReverseSubst_BaseCase()
    {
        var input = @"
`# a = 4
#macro
a = 4
#endmacro
#macro
#tokenize
b = [a]
#endtokenize
#endmacro
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        
        Assert.That(code, Is.EqualTo("((= (ref b),(4)))"));
    }
    
    [Test]
    public void Macro_Constant()
    {
        var input = @"
#constant x 1
#macro
a = x
#endmacro
b = [a] + x
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        
        Assert.That(code, Is.EqualTo("((= (ref b),(+ (1),(1))))"));
    }
    
    
    [Test]
    public void Macro_Commands()
    {
        var input = @"

#macro
macroFuncTest 6, myImage
#endmacro
a = [myImage]
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        
        Assert.That(code, Is.EqualTo("((= (ref a),(12)))"));
    }
    
    [Test]
    public void Macro_Compile_ReverseSubst()
    {
        var input = @"
# a = 4
b = [a]
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        
        Assert.That(code, Is.EqualTo("((= (ref b),(4)))"));
    }
    
    [Test]
    public void Macro_Compile_BigArrays_Literal()
    {
        var input = @"
DIM sparky(4)
sparky(0) = 1
sparky(1) = 2
sparky(2) = 3
sparky(3) = 4
c = sparky(0)
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        
    }
    
    [Test]
    public void Macro_Compile_BigArrays()
    {
        var input = @"
#macro
 function CREATE_BIG_ARRAY(name$, size) `imagine this was called with ""tuna"" and 4
  # DIM [name$]([size]) `creates the line ""DIM tuna(4)"", which allocates an array called tuna
  for n = 0 to size - 1
   # [name$]([n]) = [n + 1] `writes lines like ""tuna(0) = 1""
  next
 endfunction
#endmacro

# CREATE_BIG_ARRAY(""sparky"", 4)

c = sparky(0) + sparky(1)
";
        var parser = BuildParser(input, out _);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        
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
        var allTokens = output.allTokens; 
        Assert.That(allTokens.Count, Is.EqualTo(4));
        Assert.That(allTokens[0].type, Is.EqualTo(LexemType.ConstantBegin));
        Assert.That(allTokens[1].type, Is.EqualTo(LexemType.EndStatement));
        Assert.That(allTokens[2].type, Is.EqualTo(LexemType.ConstantEnd));
        Assert.That(allTokens[3].type, Is.EqualTo(LexemType.EndStatement));

        var tokens = output.tokens;
        Assert.That(tokens.Count, Is.EqualTo(0));

    }
}