using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Ast.Visitors;
using FadeBasic.Virtual;

namespace Tests;

public static class ParserTestUtil
{
    public static void AssertNoLexErrors(this LexerResults lex)
    {
        Assert.That(lex.tokenErrors.Count, Is.EqualTo(0), $"token errors: {string.Join(",", lex.tokenErrors.Select(x =>x.Display))}");

    }
    public static void AssertLexErrorCount(this LexerResults lex, int count)
    {
        Assert.That(lex.tokenErrors.Count, Is.EqualTo(count), $"token errors: {string.Join(",", lex.tokenErrors.Select(x =>x.Display))}");

    }
    public static void AssertNoParseErrors(this ProgramNode prog)
    {
        prog.AssertParseErrors(0);
        // Assert.That(prog.GetAllErrors().Count, Is.EqualTo(0), "parse errors: " + string.Join("\n", prog.GetAllErrors().Select(x => x.Display)));
    }

    public static void AssertParseErrors(this ProgramNode prog, int count)
    {
        AssertParseErrors(prog, count, out _);
    }
    public static void AssertParseErrors(this ProgramNode prog, int count, out List<ParseError> errors)
    {
        errors = prog.GetAllErrors();
        
        Assert.That(errors.Count, Is.EqualTo(count), "parse errors: " + string.Join("\n", prog.GetAllErrors().Select(x => x.Display)));
    }
}
public partial class ParserTests
{
    private Lexer _lexer;
    private CommandCollection _commands;
    private LexerResults? _lexerResults;

    [SetUp]
    public void Setup()
    {
        _lexer = new Lexer();
        _commands = TestCommands.CommandsForTesting;
    }

    TokenStream Tokenize(string input)
    {
        _lexerResults = _lexer.TokenizeWithErrors(input, _commands);
        return _lexerResults.stream;
        // new TokenStream(_lexer.Tokenize(input, _commands));
    } 

    Parser MakeParser(string input) => new Parser(Tokenize(input), _commands);
    
    
    [Test]
    public void Simple()
    {
        var input = "print 12";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<CommandStatement>());
        Assert.That(((CommandStatement)prog.statements[0]).command.name, Is.EqualTo("print"));
        Assert.That(((CommandStatement)prog.statements[0]).args.Count, Is.EqualTo(1));

        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((call print (12)))"));
    }
    
    
    
    [Test]
    public void Trivia_Variable_Declare()
    {
        var input = @"
`this is trivia
global a = 3
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AddTrivia(_lexerResults);
        prog.AssertNoParseErrors();
        
        Assert.That(((DeclarationStatement)prog.statements[0]).Trivia, Is.EqualTo("this is trivia"));
    }

    [Test]
    public void Trivia_Variable_Declare_Implicit()
    {
        var input = @"
`this is trivia
a = 3
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AddTrivia(_lexerResults);
        prog.AssertNoParseErrors();
        Assert.That(((AssignmentStatement)prog.statements[0]).Trivia, Is.EqualTo("this is trivia"));
    }

    
    [Test]
    public void Function_Docs_Trivia()
    {
        var input = @"
a = 3
` this is trivia
FUNCTION soap()

ENDFUNCTION
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AddTrivia(_lexerResults);
        prog.AssertNoParseErrors();
        
        Assert.That(prog.functions[0].Trivia, Is.EqualTo("this is trivia"));
    }
    
    [Test]
    public void Function_Docs_Trivia_MultiLine()
    {
        var input = @"
a = 3
` this is trivia
`  so is this
FUNCTION soap()

ENDFUNCTION
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AddTrivia(_lexerResults);
        prog.AssertNoParseErrors();
        
        Assert.That(prog.functions[0].Trivia, Is.EqualTo("this is trivia\nso is this"));
    }
    
    
    [Test]
    public void Function_Docs_Trivia_MultiLine_Blank()
    {
        var input = @"
a = 3
` this is trivia

`  so is this
FUNCTION soap()

ENDFUNCTION
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AddTrivia(_lexerResults);
        prog.AssertNoParseErrors();
        
        Assert.That(prog.functions[0].Trivia, Is.EqualTo("this is trivia\nso is this"));
    }
    
    
    [Test]
    public void Function_Docs_Trivia_MultiLine_BlankComment()
    {
        var input = @"
a = 3
` this is trivia
`
`  so is this
FUNCTION soap()

ENDFUNCTION
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AddTrivia(_lexerResults);
        prog.AssertNoParseErrors();
        
        Assert.That(prog.functions[0].Trivia, Is.EqualTo("this is trivia\n\nso is this"));
    }
    
    
    [Test]
    public void Function_Docs_Trivia_RemStart()
    {
        var input = @"
a = 3
REMSTART this is trivia

so is this
REMEND
FUNCTION soap()

ENDFUNCTION
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AddTrivia(_lexerResults);
        prog.AssertNoParseErrors();
        
        Assert.That(prog.functions[0].Trivia, Is.EqualTo("this is trivia\n\nso is this"));
    }
    
    
    [Test]
    public void Function_Docs_Trivia_RemStart_WithRemEnd()
    {
        var input = @"
a = 3
REMSTART this is trivia

so is this
tuna REMEND
FUNCTION soap()

ENDFUNCTION
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AddTrivia(_lexerResults);
        prog.AssertNoParseErrors();
        
        Assert.That(prog.functions[0].Trivia, Is.EqualTo("this is trivia\n\nso is this\ntuna"));
    }
    
    [Test]
    public void String_Assign()
    {
        var input = "x$ = \"hello\"";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x$),(\"hello\")))"));
    }

    [Test]
    public void String_Assign_WithBackslashQuote()
    {
        var input = "x$=\"\\\"\"";
        var parser = MakeParser(input);
        _lexerResults.AssertNoLexErrors();
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x$),(\"\"\")))"));
    }
    
    
    [Test]
    public void String_Assign_WithBackslash_Error_NeedDoubleSlash()
    {
        var input = "x$=\"\\\"";
        var parser = MakeParser(input);
        _lexerResults.AssertLexErrorCount(1);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x$),(\"\")))"));
    }


    [Test]
    public void String_Assign_WithBackslash()
    {
        var input = "x$=\"\\\\\"";
        var parser = MakeParser(input);
        _lexerResults.AssertNoLexErrors();
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x$),(\"\\\")))"));
    }
    
    [Test]
    public void String_Assign_QuotedWord()
    {
        var input = "x$=\"hello \\\"cruel\\\" world\"";
        var parser = MakeParser(input);
        _lexerResults.AssertNoLexErrors();
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x$),(\"hello \"cruel\" world\")))"));
    }
    
    [Test]
    public void String_Assign_WithBackslash_Error_LastChar()
    {
        var input = "x$=\"\\\\";
        var parser = MakeParser(input);
        _lexerResults.AssertLexErrorCount(1);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x$),(\"\\)))"));
    }

    
    [Test]
    public void String_Assign2()
    {
        var input = "x = \"hello\"";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(\"hello\")))"));
    }

    [Test]
    public void String_Assign_Capitals()
    {
        var input = "x = \"Hello World\"";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(\"Hello World\")))"));
    }
    
    [Test]
    public void String_Declare()
    {
        var input = "x as string";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((decl local,x,(string)))"));
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
        Assert.That(code, Is.EqualTo("((= (ref x),(5)),(call print (ref x)))"));
    }

    
    [Test]
    public void CallHostStatement()
    {
        var input = @"callTest";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.CommandsForTesting));
        var parser = new Parser(tokenStream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((call callTest))"));
    }
    
    
    [Test]
    public void CallHostStatement_InkIssue()
    {
        var input = @"ink 5, 2";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.CommandsForTesting));
        var parser = new Parser(tokenStream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((call ink (5),(2)))"));
    }

    [Test]
    public void CallHostStatement_ClsIssue()
    {
        var input = @"cls";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.CommandsForTesting));
        var parser = new Parser(tokenStream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((call cls))"));
    }


    [Test]
    public void CallHostStatement_Expr()
    {
        var input = @"x = 1 + add(2,3)";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.CommandsForTesting));
        var parser = new Parser(tokenStream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(+ (1),(xcall add (2),(3)))))"));
    }
    
    
    [Test]
    public void CallHostStatement_WithParens()
    {
        var input = @"x$ = str$(32)";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.CommandsForTesting));
        var parser = new Parser(tokenStream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x$),(xcall str$ (32))))"));
    }
    [Test]
    public void CallHostStatement_WithParens_Without()
    {
        var input = @"x$ = str$(32)";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.CommandsForTesting));
        var parser = new Parser(tokenStream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x$),(xcall str$ (32))))"));
    }
    
    
    [Test]
    public void CallHostStatement_WithFirstArgUsingParens()
    {
        var input = @"add(x + 1, 2)";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.CommandsForTesting));
        var parser = new Parser(tokenStream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((call add (+ (ref x),(1)),(2)))"));
    }


    [Test]
    public void CallHostStatement_WithFirstArgUsingParens3()
    {
        var input = @"add (x + 1, 2)";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.CommandsForTesting));
        var parser = new Parser(tokenStream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((call add (+ (ref x),(1)),(2)))"));
    }


    
    [Test]
    public void CallHostStatement_WithEmptyParens()
    {
        var input = @"screen width()";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.CommandsForTesting));
        var parser = new Parser(tokenStream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((call screen width))"));
    }
    
    
    [Test]
    public void CallHostStatement_WithParensSingle()
    {
        // y = add(x, x) + 1
        var input = @"x = file end (2)=2";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.CommandsForTesting));
        var parser = new Parser(tokenStream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(?= (xcall file end (2)),(2))))"));
    }
    
    
    [Test]
    public void CallHost_Params_1()
    {
        var input = @"print a, b, c";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.CommandsForTesting));
        var parser = new Parser(tokenStream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((call print (ref a),(ref b),(ref c)))"));
    }


    [Test]
    public void CallHost_Params_WithNestedFunction()
    {
        var input = @"print a, rgb(255,128,0)";
        var tokenStream = new TokenStream(_lexer.Tokenize(input, TestCommands.CommandsForTesting));
        var parser = new Parser(tokenStream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((call print (ref a),(xcall rgb (255),(128),(0))))"));
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
        Assert.That(code, Is.EqualTo("((= (ref x),(- (ref x),(1))))"));
    }
    
    [Test]
    public void Shorthand_DecrementAssign()
    {
        var input = @"
x = 3
x -= 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(3)),(= (ref x),(- (ref x),(1))))"));
    }
    
    
    [Test]
    public void Shorthand_AddAssign()
    {
        var input = @"
x = 3
x += 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(3)),(= (ref x),(+ (ref x),(1))))"));
    }
    
    
    [TestCase("x=3,y=2")]
    [TestCase(@"
x=3,
y=2")]
    public void Assign_MultipleOnOneLine(string input)
    {
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("(" +
                                     "(= (ref x),(3))," +
                                     "(= (ref y),(2))" +
                                     ")".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Declare_Multiple_CannotIncludeOtherStatements()
    {
        var input = @"
global x as integer, x = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].errorCode, Is.EqualTo(ErrorCodes.SymbolAlreadyDeclared));
    }

    
    
    [Test]
    public void DeclareFromSymbol_Function()
    {
        var input = @"
igloo(3)
function igloo(n)
endfunction n * 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var expr = prog.statements[0] as ExpressionStatement;
        var check = expr.expression.DeclaredFromSymbol;
        
        Assert.That(check.source, Is.Not.Null);
        Assert.That(check.source, Is.EqualTo(prog.functions[0]));
    }
    
    
    [Test]
    public void DeclareFromSymbol_Variable()
    {
        var input = @"
x = 3
y = x
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var expr = prog.statements[1] as AssignmentStatement;
        var check = expr.expression.DeclaredFromSymbol;
        
        Assert.That(check.source, Is.Not.Null);
    }
    
    
    [Test]
    public void DeclareFromSymbol_Variable_InForLoop()
    {
        var input = @"
do
    if 3 > 2
        x = 3
        for n = 1 to 3
            if x = 1
                x = 3
            endif
        next
    endif
loop
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var expr = prog.statements[0] as DoLoopStatement;
        var outter = expr.statements[0] as IfStatement;
        var forLoop = outter.positiveStatements[1] as ForStatement;
        var inner = forLoop.statements[0] as IfStatement;
        var assign = inner.positiveStatements[0] as AssignmentStatement;
        var check = assign.variable.DeclaredFromSymbol;
        
        Assert.That(check, Is.Not.Null);
    }
    
    
    [Test]
    public void DeclareFromSymbol_Variable_Lhs()
    {
        var input = @"
x = 3
x = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var expr = prog.statements[1] as AssignmentStatement;
        var check = expr.variable.DeclaredFromSymbol;
        
        Assert.That(check, Is.Not.Null);
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
        Assert.That(code, Is.EqualTo("((= (ref x),(?> (+ (1),(2)),(3))))"));
    }
    
    
    [Test]
    public void Default_int()
    {
        var input = @"

x = default ` reset the object
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(default)))"));
    }
    
    [Test]
    public void Default_Type()
    {
        var input = @"
type egg
    x, y
endtype

e as egg = default ` reset the object
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((type egg ((ref x) as (integer)),((ref y) as (integer))),(decl local,e,(typeRef egg),(default)))"));
    }

    [Test]
    public void Initializers_Test()
    {
        var input = @"
type egg
    x, y
endtype

e as egg = { 
    x = 1
    y = 2
}
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        Assert.That(prog.statements.Count, Is.EqualTo(3));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo(@"(
(type egg 
((ref x) as (integer)),
((ref y) as (integer))
),
(decl local,e,(typeRef egg)),
(= ((ref e).(ref x)),(1)),
(= ((ref e).(ref y)),(2)))".ReplaceLineEndings("").Replace("\t", "")));
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
        Assert.That(code, Is.EqualTo("((= (ref x),(- (neg (3)),(neg (1)))))"));
    }

    [Test]
    public void AnasUnfunTest_NegativeEdition_Trivial()
    {
        var input = @"
x = -1
";
      
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(neg (1))))"));
    }

    [Test]
    public void AnasUnfunTest_NegativeEdition_Trivial_Stupid()
    {
        var input = @"
x = - - - 1
";
      
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(neg (neg (neg (1))))))"));
    }
    
    [Test]
    public void AnasUnfunTest_NegativeEdition_Deluxe()
    {
        var input = @"
x = -(3 - (- (-1)) )
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(neg (- (3),(neg (neg (1)))))))"));
    }

    [Test]
    public void AnasUnfunTest_NegativeEdition_Deluxe_Mul_Official_Seal_Of_Approval()
    {
        var input = @"
x = 3 * - - 1 - 2 * 4
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(- (* (3),(neg (neg (1)))),(* (2),(4)))))"));
    }

    
    [Test]
    public void AnasUnfunTest_NegativeEdition_Parens()
    {
        var input = @"
x = -(3 - -1)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((= (ref x),(neg (- (3),(neg (1))))))"));
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
        Assert.That(code, Is.EqualTo("((= (ref x),(5)),(while (?> (ref x),(1)) (call print (ref x)),(= (ref x),(- (ref x),(1)))))"));
    }

    
    [Test]
    public void WhileExit()
    {
        var input = @"
x= 5
while x > 1
exit
endwhile
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((= (ref x),(5)),(while (?> (ref x),(1)) (break)))"));
    }

    
    [Test]
    public void ForLoop_Next()
    {
        var input = @"
FOR x = 1 to 10
 y = x
NEXT
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        prog.AssertNoParseErrors();
        Assert.That(code, Is.EqualTo(@"(
(for (ref x),(1),(10),(1),((= (ref y),(ref x))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void ForLoop_Next_Step()
    {
        var input = @"
FOR x = 1 to 10 step 4
 y = x
NEXT
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        prog.AssertNoParseErrors();

        Assert.That(code, Is.EqualTo(@"(
(for (ref x),(1),(10),(4),((= (ref y),(ref x))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void ForLoop_ArrayIndex()
    {
        var input = @"
FOR x(3) = 1 to 10 step 4
 y = x
NEXT
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        // prog.AssertNoParseErrors();
        Assert.That(prog.statements.Count, Is.EqualTo(1)); // technically the x array isn't defined, but it doesnt matter for this test
        var code = prog.ToString();
        Console.WriteLine(code);
        
        Assert.That(code, Is.EqualTo(@"(
(for (ref x[(3)]),(1),(10),(4),((= (ref y),(ref x))))
)".ReplaceLineEndings("")));
    }
    
    [Test]
    public void ArrayAssign()
    {
        var input = @"
dim x(10)
x(1) = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(dim global,x,(integer),((10))),
(= (ref x[(1)]),(2))
)".ReplaceLineEndings("")));
    }

    
    
    [Test]
    public void ArrayAssign_ReDim_MultiDim()
    {
        var input = @"
dim x(10,4)
redim x(5,1)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void ArrayAssign_ReDim()
    {
        var input = @"
dim x(10)
x(1) = 2

redim x(5)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void ArrayAssign_ReDim_Implicit()
    {
        var input = @"
dim x(10)
x(1) = 2

redim x `implies same size as original, clears array contents
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var redim = prog.statements[2] as RedimStatement;
        Assert.That(redim.ranks.Length, Is.EqualTo(1));
        Assert.That((redim.ranks[0] as LiteralIntExpression).value, Is.EqualTo(10));
    }
    
    
    [Test]
    public void ArrayAssign_ReDim_Error_BadRankCount()
    {
        var input = @"
dim x(10)
x(1) = 2

redim x(5,2)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].Display, Is.EqualTo($"[4:0,4:11] - {ErrorCodes.ReDimHasIncorrectNumberOfRanks}"));

    }
    
    
    [Test]
    public void ArrayAssign_ReDim_Error_BeforeDecl()
    {
        var input = @"
redim x(5)
dim x(10)
x(1) = 2

";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].Display, Is.EqualTo($"[1:6] - {ErrorCodes.SymbolNotDeclaredYet} | symbol, x"));

    }
    
    
    [Test]
    public void ArrayAssign_Default_ErrorBecauseItDoesntWorkYet()
    {
        var input = @"
dim x(10)
x = default
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4] - {ErrorCodes.ArrayCannotAssignFromDefault}"));

    }
    
    
    // [Test]
    public void ArrayAssign_Reassign_fromNested()
    {
        // TODO: Make this test work
        Assert.Fail("It would be cool if this didn't need to fail");
        var input = @"
dim x(10)
dim y(10,5)
x(1) = 2

x = y(2) `acts as a FULL COPY of the array
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

    
    
    [Test]
    public void ArrayAssign_Reassign_ErrorRanks()
    {
        var input = @"
dim x(10)
dim y(10,5)
x(1) = 2
x = y
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].Display, Is.EqualTo($"[4:0] - {ErrorCodes.ArrayRankMismatch}"));
    }

    
    [Test]
    public void ArrayAssign_Reassign_ErrorType()
    {
        var input = @"
dim x(10)
dim y(10) as word
x(1) = 2

x = y
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].Display, Is.EqualTo($"[5:0] - {ErrorCodes.InvalidCast} | array is wrong type"));
    }
    
    [Test]
    public void ArrayAssign_Reassign()
    {
        var input = @"
dim x(10)
dim y(10)
x(1) = 2

x = y `acts as a FULL COPY of the array
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void ArrayAssign_Reassign_Structs()
    {
        var input = @"
type egg
    size
endtype
dim x(10) as egg
dim y(10) as egg
e as egg
x(1) = e

x = y `acts as a FULL COPY of the array
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

    
    [Test]
    public void ArrayAssign_Reassign_ShouldErrorOnDiffType()
    {
        var input = @"
dim x(10)
dim y(10) as string
x(1) = 2

x = y `acts as a FULL COPY of the array
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1, out var errors);
    }
    
    [Test]
    public void ArrayAssignFlip()
    {
        var input = @"
y = x(1)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref y),(ref x[(1)]))
)".ReplaceLineEndings("")));
    }

    
    
    [Test]
    public void SimpleArray()
    {
        var input = @"dim x(10)";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((dim global,x,(integer),((10))))"));
    }

    
    [Test]
    public void SimpleDimLocal()
    {
        var input = @"local dim x(10)";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((dim local,x,(integer),((10))))"));
    }

    [Test]
    public void SimpleDimTyped()
    {
        var input = @"dim x(10) as byte";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((dim global,x,(byte),((10))))"));
    }
    
    [Test]
    public void SimpleDimScopedTyped()
    {
        var input = @"local dim x(10,n) as word";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((dim local,x,(word),((10),(ref n))))"));
    }

    
    [Test]
    public void SimpleArrayMultiDimension()
    {
        var input = @"dim x(10,2)";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((dim global,x,(integer),((10),(2))))"));
    }

    [Test]
    public void SimpleArrayMultiDimensionWithVars()
    {
        var input = @"
y = 3
dim x(y,y*2)";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((= (ref y),(3)),(dim global,x,(integer),((ref y),(* (ref y),(2)))))"));
    }
    
    
    [Test]
    public void Goto_Simple()
    {
        var input = @"
Label:
GOTO Label
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(label label),
(goto label)
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void GoSub_WithoutReturn()
    {
        var input = @"
GOSUB Label

Label:
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(gosub label),
(label label)
)".ReplaceLineEndings("")));
    }

    [Test]
    public void GoSub_WithReturn()
    {
        var input = @"
GOSUB Label

Label:
RETURN
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.statements.Count, Is.EqualTo(3));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(gosub label),
(label label),
(ret)
)".ReplaceLineEndings("")));
    }
    
    [Test]
    public void Type_Easy()
    {
        var input = @"
type egg
x as integer
endtype";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.typeDefinitions.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(type egg ((ref x) as (integer)))
)".ReplaceLineEndings("")));
    }

    [TestCase("type egg x,y endtype")]
    [TestCase(@"
type egg
x,y
endtype")]
    [TestCase(@"
type egg
x
y
endtype")]
    public void Type_AnonField(string input)
    {
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        Assert.That(prog.typeDefinitions.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(type egg ((ref x) as (integer)),((ref y) as (integer)))
)".ReplaceLineEndings("")));
    }
    
    
    [Test]
    public void Type_AnonField_String()
    {
        var input = @"
type egg
y$
endtype";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.typeDefinitions.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(type egg ((ref y$) as (string)))
)".ReplaceLineEndings("")));
    }
    
    
    [Test]
    public void Type_Nested()
    {
        var input = @"
type chicken
x as egg
endtype
type egg
y$
endtype";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(type egg ((ref y$) as (string))),
(type chicken ((ref x) as (typeRef egg)))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Type_Assignment()
    {
        var input = @"
type hotdog
x
endtype
y as hotdog
y.x = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(type hotdog ((ref x) as (integer))),
(decl local,y,(typeRef hotdog)),
(= ((ref y).(ref x)),(2))
)".ReplaceLineEndings("")));
    }
    
    
//     [Test]
//     public void Type_Assignment_Nested()
//     {
//         var input = @"
// type hotdog
// x
// endtype
// type food 
//     h as hotdog
// endtype
// type cave
//     f as food
// endtype
// y as cave
// y.f.h.x = 2
// ";
//         var parser = MakeParser(input);
//         var prog = parser.ParseProgram();
//         prog.AssertNoParseErrors();
//
//         var code = prog.ToString();
//         Console.WriteLine(code);
//         Assert.That(code, Is.EqualTo(@"(
// (type hotdog ((ref x) as (integer))),
// (decl local,y,(typeRef hotdog)),
// (= ((ref y).(ref x)),(2))
// )".ReplaceLineEndings("")));
//     }

    
    [Test]
    public void Type_AfterOtherStuff()
    {
        var input = @"
y = 2
type hotdog
x
endtype
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(type hotdog ((ref x) as (integer))),
(= (ref y),(2))
)".ReplaceLineEndings("")));
    }

    
    
    [Test]
    public void IfStatement_Then()
    {
        var input = @"
IF 3 THEN x = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(if (3) ((= (ref x),(1))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void IfStatement_Then_WithNextStatement()
    {
        var input = @"
IF 3 THEN x = 1
x = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(if (3) ((= (ref x),(1)))),
(= (ref x),(2))
)".ReplaceLineEndings("")));
    }

    
    
    [TestCase("1", "1")]
    [TestCase("101", "5")]
    [TestCase("01000001", "65")]
    public void Literal_Binary_1(string bin, string expected)
    {
        var input = @$"
x = %{bin}
";
        var parser = MakeParser(input);
        _lexerResults.AssertNoLexErrors();
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.ToString(), Is.EqualTo($@"(
(= (ref x),({expected}))
)".ReplaceLineEndings("")));
    }

    [TestCase("1", "1")]
    [TestCase("B", "11")]
    [TestCase("41", "65")]
    public void Literal_Hex_1(string bin, string expected)
    {
        for (var x = 0; x < 2; x++)
        {
            var xValue = x == 0 ? 'x' : 'X';
            var input = @$"
x = 0{xValue}{bin}
";
            var parser = MakeParser(input);
            _lexerResults.AssertNoLexErrors();
            var prog = parser.ParseProgram();
            prog.AssertNoParseErrors();

            Assert.That(prog.statements.Count, Is.EqualTo(1));
            Assert.That(prog.ToString(), Is.EqualTo($@"(
(= (ref x),({expected}))
)".ReplaceLineEndings("")));
        }
    }
    
    
    [TestCase("1", "1")]
    [TestCase("7", "7")]
    [TestCase("17", "15")]
    [TestCase("101", "65")]
    public void Literal_Octal_1(string bin, string expected)
    {
        for (var x = 0; x < 2; x++)
        {
            var xValue = x == 0 ? 'c' : 'C';
            var input = @$"
x = 0{xValue}{bin}
";
            var parser = MakeParser(input);
            _lexerResults.AssertNoLexErrors();
            var prog = parser.ParseProgram();
            prog.AssertNoParseErrors();

            Assert.That(prog.statements.Count, Is.EqualTo(1));
            Assert.That(prog.ToString(), Is.EqualTo($@"(
(= (ref x),({expected}))
)".ReplaceLineEndings("")));
        }
    }
    
    
    [Test]
    public void Assign()
    {
        var input = @"
x = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref x),(2))
)".ReplaceLineEndings("")));
    }
    
    [Test]
    public void RemStart()
    {
        var input = @"
REMSTART hello
blah blah
nothing
REMEND
x = 1
";
        var lex = _lexer.TokenizeWithErrors(input, _commands);
        Assert.That(lex.comments.Count, Is.EqualTo(4));

        
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((= (ref x),(1)))"));
        Assert.That(lex.comments[0].raw.Equals("REMSTART hello"));
        Assert.That(lex.comments[1].raw.Equals("blah blah"));
        Assert.That(lex.comments[2].raw.Equals("nothing"));
        Assert.That(lex.comments[3].raw.Equals("REMEND"));

    }
    
    
    [Test]
    public void RemStart_Midline()
    {
        var input = @"
x = 1 REMSTART hello
y = 2 REMEND z = 1
";
        var lex = _lexer.TokenizeWithErrors(input, _commands);
        Assert.That(lex.comments.Count, Is.EqualTo(2));

        
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo("((= (ref x),(1)),(= (ref z),(1)))"));
        Assert.That(lex.comments[0].raw.Equals("REMSTART hello"));
        Assert.That(lex.comments[1].raw.Equals("y = 2 REMEND"));

    }

    
    [Test]
    public void RemStart_WithNoEnd()
    {
        var input = @"
REMSTART hello
blah blah
nothing
x = 1
";
        var lex = _lexer.TokenizeWithErrors(input, _commands);
        Assert.That(lex.comments.Count, Is.EqualTo(6));

        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(0));
    }

    
    [TestCase("REM")]
    [TestCase("`")]
    public void Rem(string commentPhrase)
    {
        var input = $@"
{commentPhrase} hello x = 1 this is a "" line
x = 1
";
        
        var lex = _lexer.TokenizeWithErrors(input, _commands);
        Assert.That(lex.comments.Count, Is.EqualTo(1));
        Assert.That(lex.comments[0].raw, Is.EqualTo($"{commentPhrase} hello x = 1 this is a \" line"));

        
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref x),(1))
)".ReplaceLineEndings("")));
    }

    [TestCase("REM")]
    [TestCase("`")]
    public void RemMidLine(string commentPhrase)
    {
        var input = $@"
x = 1 {commentPhrase} what
x = 2
";
        var lex = _lexer.TokenizeWithErrors(input, _commands);
        Assert.That(lex.comments.Count, Is.EqualTo(1));

        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref x),(1)),
(= (ref x),(2))
)".ReplaceLineEndings("")));
    }
    
    
    [Test]
    public void Switch_Easy()
    {
        var input = @"
SELECT x
    CASE 1
        x = 1
    ENDCASE
ENDSELECT
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(switch (ref x) ((case (1) ((= (ref x),(1))))))
)".ReplaceLineEndings("")));
    }
    
    
    [Test]
    public void Switch_Default()
    {
        var input = @"
SELECT x
    CASE DEFAULT
        x = 1
    ENDCASE
ENDSELECT
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(switch (ref x) ((case default ((= (ref x),(1))))))
)".ReplaceLineEndings("")));
    }

     
    [Test]
    public void Switch_DefaultAndCase()
    {
        var input = @"
SELECT x
    CASE DEFAULT
        x = 1
    ENDCASE
    CASE 1
        x = 1
    ENDCASE
ENDSELECT
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(switch (ref x) ((case (1) ((= (ref x),(1)))),(case default ((= (ref x),(1))))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Switch_MultipleCases()
    {
        var input = @"
SELECT x
    CASE 1
        x = 1
    ENDCASE
    CASE 2
        x = 2
    ENDCASE
ENDSELECT
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(switch (ref x) ((case (1) ((= (ref x),(1)))),(case (2) ((= (ref x),(2))))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void Switch_Multiple_Values()
    {
        var input = @"
SELECT x
    CASE 1, 2, 5
        x = 1
    ENDCASE
ENDSELECT
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(switch (ref x) ((case (1),(2),(5) ((= (ref x),(1))))))
)".ReplaceLineEndings("")));
    }
    
    [Test]
    public void Switch_Multiple_ValuesOnMultipleLines()
    {
        var input = @"
x = 1
SELECT x
    CASE    1, 
            2
            ,5
        x = 1
    ENDCASE
ENDSELECT
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.statements.Count, Is.EqualTo(2));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref x),(1)),
(switch (ref x) ((case (1),(2),(5) ((= (ref x),(1))))))
)".ReplaceLineEndings("")));
    }
    
    
    
    [Test]
    public void OtherIfStatement_Easy()
    {
        var input = @"
IF 3 THEN x = 1: x = 2 ELSE x = 3
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(if (3) ((= (ref x),(1)),(= (ref x),(2))) ((= (ref x),(3)))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void IfStatement_Else()
    {
        var input = @"
IF 3
    x = 1
ELSE 
    x = 2
ENDIF
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(if (3) ((= (ref x),(1))) ((= (ref x),(2)))
)".ReplaceLineEndings("")));
    }


    
    [Test]
    public void IfStatement_Easy()
    {
        var input = @"
IF 3
    x = 1
ENDIF
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(if (3) ((= (ref x),(1))))
)".ReplaceLineEndings("")));
    }
    
    
    [Test]
    public void IfStatement_Nested()
    {
        var input = @"
IF 3
    x = 1
    IF 4
        x = 2
    ENDIF
ENDIF
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(if (3) ((= (ref x),(1)),(if (4) ((= (ref x),(2))))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void IfStatement_MultiStatements()
    {
        var input = @"
IF 3
    x = 1
    x = 2
ENDIF
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();

        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(if (3) ((= (ref x),(1)),(= (ref x),(2))))
)".ReplaceLineEndings("")));
    }

    
    [Test]
    public void IfStatement_Conditional()
    {
        var input = @"
IF a+b>10 AND c
    x = 1
ENDIF
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(if (and (?> (+ (ref a),(ref b)),(10)),(ref c)) ((= (ref x),(1))))
)".ReplaceLineEndings("")));
    }

    
    
    [Test]
    public void AssignmentWithCommandAndField()
    {
        var input = @"
x = a.b + len(a.c)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        var code = prog.ToString();
        Console.WriteLine(code);
        Assert.That(code, Is.EqualTo(@"(
(= (ref x),(+ ((ref a).(ref b)),(xcall len ((ref a).(ref c)))))
)".ReplaceLineEndings("")));
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
        Assert.That(code, Is.EqualTo("((= (ref x),(2)))"));
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
        Assert.That(code, Is.EqualTo("((= (ref x),(+ (1),(2))))"));
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
        Assert.That(code, Is.EqualTo("((= (ref x#),(2.1)))"));
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
        Assert.That(code, Is.EqualTo("((= (ref x),(2.1)))"));
    }
    
    
    [Test]
    public void DeclAssign_Scope()
    {
        var input = "Global x = 3";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<DeclarationStatement>());

        var decl = prog.statements[0] as DeclarationStatement;
        Assert.That(decl.type.variableType, Is.EqualTo(VariableType.Integer));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((decl global,x,(integer),(3)))"));
    }
    
    [Test]
    public void DeclAssign_Integer()
    {
        var input = "x AS INTEGER = 3";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<DeclarationStatement>());

        var decl = prog.statements[0] as DeclarationStatement;
        Assert.That(decl.type.variableType, Is.EqualTo(VariableType.Integer));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((decl local,x,(integer),(3)))"));
    }
    
    
    [Test]
    public void DeclAssign_Multi_Integer()
    {
        var input = "x AS WORD = 3, y AS WORD = 4";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("(" +
                                     "(decl local,x,(word),(3))," +
                                     "(decl local,y,(word),(4))" +
                                     ")".ReplaceLineEndings("")));
    }
    
    [Test]
    public void DeclAssign_Multi_Integer_DiffTypes()
    {
        var input = "x AS WORD = 3, y AS integer = 4";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("(" +
                                     "(decl local,x,(word),(3))," +
                                     "(decl local,y,(integer),(4))" +
                                     ")".ReplaceLineEndings("")));
    }
    
    
    [Test]
    public void DeclAssign_Multi_Integer_DiffTypes_ErrorWhenDefaultTypeDoesNotMatch()
    {
        var input = "x AS WORD = 3, y$ = \"toast\"";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].errorCode, Is.EqualTo(ErrorCodes.MultiLineDeclareCannotInferType));
    }
    
    [Test]
    public void DeclAssign_Multi_Integer_DiffTypes2()
    {
        var input = "x AS WORD = 3, y$ as string = \"toast\"";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("(" +
                                     "(decl local,x,(word),(3))," +
                                     "(decl local,y$,(string),(\"toast\"))" +
                                     ")".ReplaceLineEndings("")));
    }
    
    
    [Test]
    public void DeclAssign_Multi_Structs()
    {
        var input = @"
TYPE vec
    x
    y
ENDTYPE
v as vec, b as vec
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        prog.typeDefinitions.Clear(); // HACK for test clarity.
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("(" +
                                     "(decl local,v,(typeRef vec))," +
                                     "(decl local,b,(typeRef vec))" +
                                     ")".ReplaceLineEndings("")));
    }
    
    [Test]
    public void DeclAssign_Multi_Structs2()
    {
        var input = @"
TYPE vec
    x
    y
ENDTYPE
v as vec, b
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.typeDefinitions.Clear(); // HACK for test clarity.
        prog.AssertParseErrors(2, out var errors);
        Assert.That(errors[0].errorCode, Is.EqualTo(ErrorCodes.MultiLineDeclareCannotInferType));
    }
    
    
    [Test]
    public void DeclAssign_Multi_FailCaseForArrays()
    {
        var input = @"
DIM x(10)
y as integer, x(1) = 8
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.typeDefinitions.Clear(); // HACK for test clarity.
        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].errorCode, Is.EqualTo(ErrorCodes.MultiLineDeclareInvalidVariable));
    }
    
    [Test]
    public void ParseError_DeclAssign_Integer_Recursive()
    {
        var input = "x AS INTEGER = x";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
    }

    
    // classic DBP names
    [TestCase("word", "word", VariableType.Word)]
    [TestCase("dword", "dword", VariableType.DWord)]
    [TestCase("byte", "byte", VariableType.Byte)]
    [TestCase("double integer", "doubleinteger", VariableType.DoubleInteger)]
    [TestCase("double float", "doublefloat", VariableType.DoubleFloat)]
    [TestCase("integer", "integer", VariableType.Integer)]
    [TestCase("float", "float", VariableType.Float)]
    [TestCase("boolean", "boolean", VariableType.Boolean)]
    
    // C# names
    [TestCase("ushort", "word", VariableType.Word)]
    [TestCase("uint", "dword", VariableType.DWord)]
    [TestCase("long", "doubleinteger", VariableType.DoubleInteger)]
    [TestCase("double", "doublefloat", VariableType.DoubleFloat)]
    [TestCase("int", "integer", VariableType.Integer)]
    [TestCase("float", "float", VariableType.Float)]
    [TestCase("bool", "boolean", VariableType.Boolean)]
    public void Decl(string type, string name, VariableType vt)
    {
        var input = @$"x as {type}";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<DeclarationStatement>());

        var decl = prog.statements[0] as DeclarationStatement;
        Assert.That(decl.type.variableType, Is.EqualTo(vt));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo($"((decl local,x,({name})))"));
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
        
        prog.AssertNoParseErrors();
        Assert.That(prog.statements.Count, Is.EqualTo(1));
        Assert.That(prog.statements[0], Is.AssignableTo<DeclarationStatement>());

        var decl = prog.statements[0] as DeclarationStatement;
        Assert.That(decl.type.variableType, Is.EqualTo(VariableType.Integer));
        var code = prog.ToString();
        Assert.That(code, Is.EqualTo("((decl global,x,(integer)))"));
    }
    
    [Test]
    public void Decl_IntegerGlobal_2()
    {
        var input = @"
for x = 0 to 3
    global y as integer = x
next
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void Decl_IntegerGlobal_3()
    {
        var input = @"
    global y as integer = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

}