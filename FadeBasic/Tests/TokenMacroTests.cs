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
        Assert.Fail("This should not be allowed");
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