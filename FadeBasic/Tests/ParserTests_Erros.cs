using System.Text;
using FadeBasic;
using FadeBasic.Ast;

namespace Tests;

public partial class ParserTests
{
    [Test]
    public void Errors_Simple()
    {
        var input = @"
x = 
n = 3
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        
        Assert.That(errors.Count, Is.EqualTo(1));
        
    }
    
    [Test]
    public void ParseError_UseVariableBeforeDefined()
    {
        var input = @"
x = a
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:4] - {ErrorCodes.InvalidReference} | unknown symbol, a"));
    }
    
    
    [Test]
    public void ParseError_UseVariableBeforeDefined_SelfReference()
    {
        var input = @"
x = x
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:4] - {ErrorCodes.InvalidReference} | unknown symbol, x"));
    }

    
    [Test]
    public void ParseError_UseVariableDeclaredThroughCommand()
    {
        var input = @"
inc a
x = a
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(0), string.Join("\n", errors.Select(x => x.Display)));
        // Assert.That(errors[0].Display, Is.EqualTo($"[1:4] - {ErrorCodes.InvalidReference} | unknown symbol, a"));
    }
    
    
    [Test]
    public void ParseError_UseVariableDeclaredThroughCommand_Compound()
    {
        // this shouldn't fail, because retandref declares a before it is used in the second parameter slot.
        var input = @"
add(retandref(a), a)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(0), string.Join("\n", errors.Select(x => x.Display)));
    }

    
    [Test]
    public void ParseError_UseVariableBeforeDefined_Safe()
    {
        var input = @"
a = 1
x = a
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(0));
        // Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.GotoMissingLabel}"));
    }
    
    
    [Test]
    public void ParseError_Command_ReferenceUnknownVariable()
    {
        var input = @"
add(1,a)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:6] - {ErrorCodes.InvalidReference} | unknown symbol, a"));
    }

    
    [Test]
    public void ParseError_Command_Unknown()
    {
        var input = @"
thisCommandDoesNotExist()
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.InvalidReference} | unknown symbol, thiscommanddoesnotexist"));
    }
    
    
    [Test]
    public void ParseError_Command_Incomplete()
    {
        // this used to hang forever... figure out why!
        var input = @"
for x = 1 to 10
    jerk
next
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4] - {ErrorCodes.CommandNoOverloadFound}"));
    }
    
    
    [Test]
    public void ParseError_Command_Incomplete_WithFunction()
    {
        // this used to hang forever... figure out why!
        var input = @"
for x = 1 to 10
    jerk randoThing(
next
function randoThing(a, b)
endfunction a + b
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(2);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:19] - {ErrorCodes.VariableIndexMissingCloseParen}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[2:9] - {ErrorCodes.FunctionParameterCardinalityMismatch}"));
    }
    
    
    [Test]
    public void ParseError_Command_Incomplete_WithFunction_AndComma()
    {
        // this used to hang forever... figure out why!
        var input = @"
for x = 1 to 10
    jerk randoThing(1,
next
function randoThing(a, b)
endfunction a + b
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(2);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:19] - {ErrorCodes.VariableIndexMissingCloseParen}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[2:9] - {ErrorCodes.FunctionParameterCardinalityMismatch}"));

    }
    
    [Test]
    public void ParseError_Function_CannotUseCommandName()
    {
        // this used to hang forever... figure out why!
        var input = @"
function add(a, b)
endfunction a + b
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        var errors = prog.GetAllErrors();

        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.FunctionCannotUseCommandName}"));
    }
    
    [Test]
    public void ParseError_Function_ShouldWork()
    {
        // this used to hang forever... figure out why!
        var input = @"
function randoThing(a, b)
endfunction a + b
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void ParseError_Function_VoidEarlyReturn_ShouldWork()
    {
        // this used to hang forever... figure out why!
        var input = @"
function randoThing(a, b)
    exitfunction
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertNoParseErrors();
    }

    [Test]
    public void ParseError_Command_TooManyParameters()
    {
        var input = @"
add(1,2,3)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.CommandNoOverloadFound}"));
    }
    
    [Test]
    public void ParseError_Command_TooFewParameters()
    {
        var input = @"
add(1)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.CommandNoOverloadFound}"));
    }
    
    
    [Test]
    public void ParseError_Command_InvalidTypes()
    {
        var input = @"
add(1, ""toast"")
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        // Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.GotoMissingLabel}"));
    }
    
    
    [Test]
    public void ParseError_TypeCheck_IntToFloat()
    {
        var input = @"
x = 1.2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        // Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.GotoMissingLabel}"));
    }
    
    
    [Test]
    public void ParseError_TypeCheck_Default_String()
    {
        var input = @"
x$ = default
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void ParseError_TypeCheck_Default_Array_Element_Int_Okay()
    {
        var input = @"
DIM x(3)
x(1) = default
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    
    [Test]
    public void ParseError_TypeCheck_Default_Array_Element_Type_Okay()
    {
        var input = @"
TYPE egg 
    x, y
ENDTYPE
DIM x(3) as egg
x(1) = default
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

    
    [Test]
    public void ParseError_TypeCheck_Default_Decl_TypeOkay()
    {
        var input = @"
type egg
    x, y
endtype
e as egg = default";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

    
    [Test]
    public void ParseError_TypeCheck_Default_Assign_TypeOkay()
    {
        var input = @"
type egg
    x, y
endtype
e as egg

e = default
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

    
    [Test]
    public void ParseError_TypeCheck_Default_Assign_IntOkay()
    {
        var input = @"
e = default
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    
    [TestCase("{}")]
    [TestCase(@"{
    x = 2
}")]
    [TestCase(@"{
    x = 2
    y = 1
}")]
    public void ParseError_TypeCheck_Init_Okay(string snippets)
    {
        var input = @"
type egg
    x, y
endtype

e as egg = " + snippets + "\n";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    
    [TestCase("{}")]
    [TestCase(@"{
    a = 3
}")]
    [TestCase(@"{
    nest.x = 1
}")]
    [TestCase(@"{
    nest = { 
        x = 1
    }
}")]
    public void ParseError_TypeCheck_Init_NestedOkay(string snippets)
    {
        var input = @"
type egg
    x, y
endtype
type chicken
    a
    nest as egg
endtype

c as chicken = " + snippets + "\n";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

    
    
    [Test]
    public void ParseError_TypeCheck_DefaultNotOkayInNonLiterals()
    {
        var input = @"
x = default + 3";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].Display, Is.EqualTo($"[1:4] - {ErrorCodes.DefaultExpressionUnknownType}"));
    }
    
    
    [Test]
    public void ParseError_TypeCheck_Init_AssignmentOkay()
    {
        var input = @"
type egg
    x, y
endtype
e as egg
e = { 
}";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(0, out var errors);
        // Assert.That(errors[0].Display, Is.EqualTo($"[1:4] - {ErrorCodes.InitializerNotAllowed}"));
    }
    
    
    [Test]
    public void ParseError_InitNeedsAssignments()
    {
        var input = @"
type egg
    x, y
endtype
e as egg
e = { 
 1, 2
}";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].Display, Is.EqualTo($"[6:1,7:5] - {ErrorCodes.InitializerCanOnlyHaveAssignments}"));
    }

    
    [Test]
    public void ParseError_TypeCheck_Function_ReturnTypeAmbig()
    {
        var input = @"
`foo returns int, so cannot assign to string
x = foo()

function foo()
if 1 > 3
    exitfunction 3
endif
endfunction ""toast""
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();
        Assert.That(errors[0].Display, Is.EqualTo($"[8:12] - {ErrorCodes.AmbiguousFunctionReturnType}"));
    }
    
    
    [Test]
    public void ParseError_TypeCheck_Function_ReturnTypeNotAmbig()
    {
        var input = @"
`foo returns int, so cannot assign to string
x = foo()

function foo()
if 1 > 3
    exitfunction 3
endif
endfunction 8 + 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        // var errors = prog.GetAllErrors();
        // Assert.That(errors[0].Display, Is.EqualTo($"[8:12] - {ErrorCodes.AmbiguousFunctionReturnType}"));
    }
    
    
    [Test]
    public void ParseError_TypeCheck_Function_ReturnDoesNotMatch_Chain()
    {
        var input = @"
`foo returns int, so cannot assign to string
x$ = bar()

function foo()
endfunction 3
function bar()
endfunction foo()

";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0] - {ErrorCodes.InvalidCast} | cannot convert int to string"));
    }
    
    
    [Test]
    public void ParseError_TypeCheck_Function_Recursive2_Works()
    {
        var input = @"
x$ = bar(""test"")
y$ = foo(""toast"")

function foo(c$) ` foo returns a string
    a$ = bar(c$ + ""a"") `the processing of this statement causes a loop
endfunction a$

function bar(b$) ` bar returns a string
    a$ = foo(b$)

    if 1 > 2 `fake magic
        exitfunction b$
    endif
endfunction a$

";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    
    [Test]
    public void ParseError_ExitWithoutLoop()
    {
        string src = @"exit";
        var parser = MakeParser(src);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();

        Assert.That(errors[0].Display, Is.EqualTo($"[0:0] - {ErrorCodes.ExitStatementFoundOutsideOfLoop}"));
    }
    
    [Test]
    public void ParseError_SkipWithoutLoop()
    {
        string src = @"skip";
        var parser = MakeParser(src);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();

        Assert.That(errors[0].Display, Is.EqualTo($"[0:0] - {ErrorCodes.SkipStatementFoundOutsideOfLoop}"));
    }


    [Test]
    public void ParseError_ExitAllowed_While()
    {
        string src = @"
WHILE 1
    EXIT
ENDWHILE
";
        var parser = MakeParser(src);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void ParseError_SkipAllowed_While()
    {
        string src = @"
WHILE 1
    SKIP
ENDWHILE
";
        var parser = MakeParser(src);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

    [Test]
    public void ParseError_ExitAllowed_Do()
    {
        string src = @"do
exit
loop";
        var parser = MakeParser(src);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void ParseError_SkipAllowed_Do()
    {
        string src = @"do
skip
loop";
        var parser = MakeParser(src);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

    
    [Test]
    public void ParseError_Conditionals_Errors_If()
    {
        var input = @$"
IF ""toast""
ENDIF
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();

        Assert.That(errors[0].Display, Is.EqualTo($"[1:3] - {ErrorCodes.InvalidCast} | cannot convert string to int"));
    }
    
    [Test]
    public void ParseError_Conditionals_Errors_WorksForEqualityCheck()
    {
        var input = @"
x$ = ""toast""
IF x$ = ""toast""
ENDIF
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

    [Test]
    public void ParseError_Conditionals_Errors_WorksForEqualityCheck2()
    {
        var input = @"
x# = 2.2
IF x# > 1.0
ENDIF
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

    
    [Test]
    public void ParseError_Conditionals_Errors_While()
    {
        var input = @$"
WHILE ""toast""
ENDWHILE
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();

        Assert.That(errors[0].Display, Is.EqualTo($"[1:6] - {ErrorCodes.InvalidCast} | cannot convert string to int"));
    }
    
    
    [Test]
    public void ParseError_Conditionals_Errors_Repeat()
    {
        var input = @$"
REPEAT
UNTIL ""toast""
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();

        Assert.That(errors[0].Display, Is.EqualTo($"[2:6] - {ErrorCodes.InvalidCast} | cannot convert string to int"));
    }
    
    [Test]
    public void ParseError_TypeCheck_Function_Recursive_Infinite()
    {
        var input = @"

FUNCTION death()
ENDFUNCTION spiral()

FUNCTION spiral()
ENDFUNCTION death()

";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(2);
        var errors = prog.GetAllErrors();

        Assert.That(errors[0].Display, Is.EqualTo($"[2:0] - {ErrorCodes.UnknowableFunctionReturnType}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[5:0] - {ErrorCodes.UnknowableFunctionReturnType}"));

    }
    
    [Test]
    public void ParseError_TypeCheck_Function_VoidAndNonVoidReturns()
    {
        var input = @"
function foo(n)
    exitfunction 2
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();

        Assert.That(errors[0].Display, Is.EqualTo($"[2:17] - {ErrorCodes.AmbiguousFunctionReturnType}"));

    }

    
    [Test]
    public void ParseError_TypeCheck_Function_Recursive()
    {
        var input = @"
`foo returns int, so cannot assign to string
x$ = foo()

function foo()
    if 3 > 2
        exitfunction bar() + 2
    endif
endfunction 3
function bar()
endfunction foo()

";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0] - {ErrorCodes.InvalidCast} | cannot convert int to string"));
    }
    
    
    [Test]
    public void ParseError_TypeCheck_Function_Recursive_Works()
    {
        var input = @"
`foo returns int, so cannot assign to string
x = foo()

function foo()
    if 3 > 2
        exitfunction bar()
    endif
endfunction bar()

function bar()
    if 1 > 2
        exitfunction 3
    endif
endfunction foo()

";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void ParseError_TypeCheck_Function_ReturnTypeDoesNotMatch()
    {
        var input = @"
`foo returns int, so cannot assign to string
x$ = foo(3)

function foo(a)
endfunction a*2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0] - {ErrorCodes.InvalidCast} | cannot convert int to string"));
    }
    
    
    [Test]
    public void ParseError_TypeCheck_Function_ReturnTypeDoesNotMatch_2()
    {
        var input = @"
`foo returns int, so cannot assign to string
x = foo()

function foo()
endfunction ""hello""
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0] - {ErrorCodes.InvalidCast} | cannot convert string to int"));
    }
    
    
    [Test]
    public void ParseError_TypeCheck_Function_ReturnTypeDoesNotMatch_Void()
    {
        var input = @"
`foo returns int, so cannot assign to string
x = foo()

function foo()
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0] - {ErrorCodes.InvalidCast} | cannot convert void to int"));
    }
    
    
    [Test]
    public void ParseError_TypeCheck_Function_ReturnIsDropped_ShouldBeOkay()
    {
        var input = @"
`foo returns a value, but noting listens for it; which should be okay.
foo()
function foo()
endfunction 3
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        
    }

    
    [Test]
    public void ParseError_TypeCheck_Function_ReturnTypeCannotBeArray()
    {
        var input = @"
foo()

function foo()
    DIM n(3)
endfunction n
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].Display, Is.EqualTo($"[5:12] - {ErrorCodes.InvalidFunctionReturnType}"));
    }

    
    [Test]
    public void ParseError_TypeCheck_Function_ReturnTypeDoesNotMatch_Void_PassToCommand()
    {
        var input = @"
`foo returns nothing, and cannot be passed to an argument
print foo()

function foo()
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();
        Assert.That(errors[0].Display, Is.EqualTo($"[2:6,2:10] - {ErrorCodes.InvalidCast} | cannot convert void to any"));
    }

    
    [Test]
    public void ParseError_TypeCheck_Function_ReturnTypeDoesNotMatch_StrToCommand()
    {
        var input = @"
x = rnd(""toast"")
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();
        Assert.That(errors[0].Display, Is.EqualTo($"[1:8] - {ErrorCodes.InvalidCast} | cannot convert string to int"));
    }

    
    [Test]
    public void ParseError_TypeCheck_Function_ReturnTypeDoesMatch()
    {
        var input = @"
x$ = foo(""world"")

function foo(a$)
endfunction a$ + ""hello""
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        // var errors = prog.GetAllErrors();
        // Assert.That(errors[0].Display, Is.EqualTo($"[2:0] - {ErrorCodes.InvalidCast} | cannot convert int to string"));
    }
    
    
    [TestCase("", "1", "a$", "cannot convert int to string")]
    [TestCase("", "1", "a as string", "cannot convert int to string")]
    [TestCase("", "\"a\"", "a as string", null)]
    [TestCase("type egg\nx\nendtype\na as egg", "a", "a as egg", null)]
    [TestCase("type egg\nx\nendtype\na as egg", "a", "b", "cannot convert egg to int")]
    [TestCase("type egg\nx\nendtype\nc = 3", "c", "b as egg", "cannot convert int to egg")]
    public void ParseError_TypeCheck_Function_Parameter(string setup, string callSite, string sig, string error)
    {
        var input = @$"
{setup}
foo({callSite})
function foo({sig})

endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        if (string.IsNullOrEmpty(error))
        {
            prog.AssertNoParseErrors();
        }
        else
        {
            prog.AssertParseErrors(1);
            var errors = prog.GetAllErrors();
            Assert.That(errors[0].Display.Substring("[1:0]".Length),
                Is.EqualTo($" - {ErrorCodes.InvalidCast} | {error}"));
        }
    }
    
    
    [TestCase(null, null, "a = 1 + \"no\"",  "")]
    [TestCase("type egg\nx\nendtype", "b as egg", "a = 1 + b",  "")]
    public void ParseError_TypeCheck_BinaryOps(string a, string b, string op, string error)
    {
        var input = @$"
{a}
{b}
{op}
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        if (error == null)
        {
            prog.AssertNoParseErrors();
        }
        else
        {
            prog.AssertParseErrors(1);
            var errors = prog.GetAllErrors();
            Assert.That(errors[0].CombinedMessage,
                Is.EqualTo($"{ErrorCodes.InvalidCast}{(error.Length > 0 ? ($" | {error}") : "")}"));
        }
    }

    [TestCase(null, "x", @"""toast""",  "cannot convert string to int")]
    [TestCase(null, "x#", @"""toast""", "cannot convert string to float")]
    [TestCase(null, "x$", @"""toast""", null)]
    [TestCase(null, "x", "3*2", null)]
    [TestCase(null, "x", "3", null)]
    [TestCase(null, "x", "3.2", null)]
    [TestCase(null, "x#", "3.2", null)]
    [TestCase(null, "x#", "3", null)]
    [TestCase(null, "x$", "3", "cannot convert int to string")]
    [TestCase(null, "x$", "3.2", "cannot convert float to string")]
    [TestCase(@"DIM arr(3)
x =3", "arr", "x", "cannot assign to array")]
    [TestCase(@"DIM arr(3)
x =3", "arr(1)", "x", null)]
    [TestCase(@"DIM arr(3)", "arr(1)", @"""toast""", "cannot convert string to int")]
    [TestCase(@"
TYPE egg
ENDTYPE
DIM arr(3) as egg
", "arr(1)", @"5", "cannot convert int to egg")]
    public void ParseError_TypeCheck_SimpleAssigns(string setup, string leftSide, string rightSide, string error)
    {
        var input = @$"
{setup}
{leftSide} = {rightSide}
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        if (string.IsNullOrEmpty(error))
        {
            prog.AssertNoParseErrors();
        }
        else
        {
            prog.AssertParseErrors(1);
            var errors = prog.GetAllErrors();

            var expected = $" - {ErrorCodes.InvalidCast} | {error}";
            Assert.That(errors[0].Display.EndsWith(expected), $"error does not match expected. actual=[{errors[0].Display}] expected=[{expected}]");
            
        }
    }
    
    #region AI generated tests
    [TestCase("x as integer = 42", null)]
    [TestCase("x as integer = 42.5", null)] // Allowed, with precision loss
    [TestCase("x as float = 42", null)]
    [TestCase("x as float = 42.5", null)]
    [TestCase("x as float = \"hello\"", "cannot convert string to float")]
    [TestCase("x as string = 42", "cannot convert int to string")]
    [TestCase("x$ as string = 42", "cannot convert int to string")]
    [TestCase("x$ as string = \"hello\"", null)]
    public void AI_ParseError_TypeCheck_BasicAssignments(string assign, string error)
    {
        var input = @$"{assign}";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        if (string.IsNullOrEmpty(error))
        {
            prog.AssertNoParseErrors();
        }
        else
        {
            prog.AssertParseErrors(1);
            var errors = prog.GetAllErrors();
            Assert.That(errors[0].CombinedMessage,
                Is.EqualTo($"{ErrorCodes.InvalidCast} | {error}"));
        }
    }

    [TestCase("x as integer\ny as integer\nx = y + 1", false)]
    [TestCase("x as integer\ny as float\nx = y + 1", false)] // Allowed, with precision loss
    [TestCase("x as float\ny as integer\nx = y + 1", false)]
    [TestCase("x$ as string\ny$ as string\nx$ = y$ + 1", true)]
    [TestCase("x$ as string\ny$ as string\nx$ = y$ + \"world\"", false)]
    public void AI_ParseError_TypeCheck_OperationAssignments(string assign, bool error)
    {
        var input = @$"{assign}";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        if (!error)
        {
            prog.AssertNoParseErrors();
        }
        else
        {
            prog.AssertParseErrors(1);
            var errors = prog.GetAllErrors();
            Assert.IsTrue(errors[0].CombinedMessage.EndsWith(ErrorCodes.InvalidCast.message));
            // ($"{ErrorCodes.InvalidCast} | {error}"));
        }
    }

    [TestCase("x as float\ny as integer\nx = y", null)]
    [TestCase("x as float\ny as integer\nx = y", null)]
    [TestCase("x as integer\ny as float\nx = y", null)] // Allowed, with precision loss
    [TestCase("x as string\ny as integer\nx = y", "cannot convert int to string")]
    [TestCase("x as integer\ny as string\nx = y", "cannot convert string to int")]
    public void AI_ParseError_TypeCheck_ImplicitConversion(string assign, string error)
    {
        var input = @$"{assign}";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        if (string.IsNullOrEmpty(error))
        {
            prog.AssertNoParseErrors();
        }
        else
        {
            prog.AssertParseErrors(1);
            var errors = prog.GetAllErrors();
            Assert.That(errors[0].CombinedMessage,
                Is.EqualTo($"{ErrorCodes.InvalidCast} | {error}"));
        }
    }

    [TestCase("dim arr(10) as integer\narr(0) = 42", null)]
    [TestCase("dim arr(10) as integer\narr(0) = 42.5", null)] // Allowed, with precision loss
    [TestCase("dim arr(10) as string\narr(0) = 42", "cannot convert int to string")]
    [TestCase("dim arr(10) as float\narr(0) = 42.5", null)]
    [TestCase("dim arr(10) as float\narr(0) = \"hello\"", "cannot convert string to float")]
    public void AI_ParseError_TypeCheck_ArrayAssignments(string assign, string error)
    {
        var input = @$"{assign}";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        if (string.IsNullOrEmpty(error))
        {
            prog.AssertNoParseErrors();
        }
        else
        {
            prog.AssertParseErrors(1);
            var errors = prog.GetAllErrors();
            Assert.That(errors[0].CombinedMessage,
                Is.EqualTo($"{ErrorCodes.InvalidCast} | {error}"));
        }
    }
    
    
     [TestCase("x as byte\ny as word\nx = y", null)] // Word to Byte
    [TestCase("x as byte\ny as dword\nx = y", null)] // DWORD to Byte
    [TestCase("x as byte\ny as integer\nx = y", null)] // Integer to Byte
    [TestCase("x as byte\ny as double integer\nx = y", null)] // Double Integer to Byte
    [TestCase("x as byte\ny as float\nx = y", null)] // Float to Byte
    [TestCase("x as byte\ny as double float\nx = y", null)] // Double Float to Byte

    [TestCase("x as word\ny as byte\nx = y", null)] // Byte to Word
    [TestCase("x as word\ny as dword\nx = y", null)] // DWORD to Word
    [TestCase("x as word\ny as integer\nx = y", null)] // Integer to Word
    [TestCase("x as word\ny as double integer\nx = y", null)] // Double Integer to Word
    [TestCase("x as word\ny as float\nx = y", null)] // Float to Word
    [TestCase("x as word\ny as double float\nx = y", null)] // Double Float to Word

    [TestCase("x as dword\ny as byte\nx = y", null)] // Byte to DWORD
    [TestCase("x as dword\ny as word\nx = y", null)] // Word to DWORD
    [TestCase("x as dword\ny as integer\nx = y", null)] // Integer to DWORD
    [TestCase("x as dword\ny as double integer\nx = y", null)] // Double Integer to DWORD
    [TestCase("x as dword\ny as float\nx = y", null)] // Float to DWORD
    [TestCase("x as dword\ny as double float\nx = y", null)] // Double Float to DWORD

    [TestCase("x as integer\ny as byte\nx = y", null)] // Byte to Integer
    [TestCase("x as integer\ny as word\nx = y", null)] // Word to Integer
    [TestCase("x as integer\ny as dword\nx = y", null)] // DWORD to Integer
    [TestCase("x as integer\ny as double integer\nx = y", null)] // Double Integer to Integer
    [TestCase("x as integer\ny as float\nx = y", null)] // Float to Integer
    [TestCase("x as integer\ny as double float\nx = y", null)] // Double Float to Integer

    [TestCase("x as double integer\ny as byte\nx = y", null)] // Byte to Double Integer
    [TestCase("x as double integer\ny as word\nx = y", null)] // Word to Double Integer
    [TestCase("x as double integer\ny as dword\nx = y", null)] // DWORD to Double Integer
    [TestCase("x as double integer\ny as integer\nx = y", null)] // Integer to Double Integer
    [TestCase("x as double integer\ny as float\nx = y", null)] // Float to Double Integer
    [TestCase("x as double integer\ny as double float\nx = y", null)] // Double Float to Double Integer

    [TestCase("x as float\ny as byte\nx = y", null)] // Byte to Float
    [TestCase("x as float\ny as word\nx = y", null)] // Word to Float
    [TestCase("x as float\ny as dword\nx = y", null)] // DWORD to Float
    [TestCase("x as float\ny as integer\nx = y", null)] // Integer to Float
    [TestCase("x as float\ny as double integer\nx = y", null)] // Double Integer to Float
    [TestCase("x as float\ny as double float\nx = y", null)] // Double Float to Float

    [TestCase("x as double float\ny as byte\nx = y", null)] // Byte to Double Float
    [TestCase("x as double float\ny as word\nx = y", null)] // Word to Double Float
    [TestCase("x as double float\ny as dword\nx = y", null)] // DWORD to Double Float
    [TestCase("x as double float\ny as integer\nx = y", null)] // Integer to Double Float
    [TestCase("x as double float\ny as double integer\nx = y", null)] // Double Integer to Double Float
    [TestCase("x as double float\ny as float\nx = y", null)] // Float to Double Float

    [TestCase("x as boolean\ny as byte\nx = y", null)] // Byte to Boolean
    [TestCase("x as boolean\ny as word\nx = y", null)] // Word to Boolean
    [TestCase("x as boolean\ny as dword\nx = y", null)] // DWORD to Boolean
    [TestCase("x as boolean\ny as integer\nx = y", null)] // Integer to Boolean
    [TestCase("x as boolean\ny as double integer\nx = y", null)] // Double Integer to Boolean
    [TestCase("x as boolean\ny as float\nx = y", null)] // Float to Boolean
    [TestCase("x as boolean\ny as double float\nx = y", null)] // Double Float to Boolean
     
    [TestCase("x as string\ny as byte\nx = y", "cannot convert byte to string")]
    [TestCase("x as string\ny as word\nx = y", "cannot convert ushort to string")]
    [TestCase("x as string\ny as dword\nx = y", "cannot convert uint to string")]
    [TestCase("x as string\ny as integer\nx = y", "cannot convert int to string")]
    [TestCase("x as string\ny as double integer\nx = y", "cannot convert long to string")]
    [TestCase("x as string\ny as float\nx = y", "cannot convert float to string")]
    [TestCase("x as string\ny as double float\nx = y", "cannot convert double to string")]
    [TestCase("x as string\ny as boolean\nx = y", "cannot convert bool to string")]
    public void AI_ParseError_TypeCheck_ImplicitCasting(string assign, string error)
    {
        var input = @$"{assign}";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        if (string.IsNullOrEmpty(error))
        {
            prog.AssertNoParseErrors();
        }
        else
        {
            prog.AssertParseErrors(1);
            var errors = prog.GetAllErrors();
            Assert.That(errors[0].CombinedMessage,
                Is.EqualTo($"{ErrorCodes.InvalidCast} | {error}"));
        }
    }
    
    #endregion
    
    [TestCase("y = x",  null)] // this one is interesting, because by it COULD just as easily be an error; but its a conceit in the language to make it easier to type things out.
    [TestCase("y$ = x",  "cannot convert egg to string")]
    [TestCase("y# = x",  "cannot convert egg to float")]
    [TestCase("y as egg = x",  null)]
    [TestCase("y = x.z",  null)]
    [TestCase("y# = x.z",  null)]
    [TestCase("type egg2\n z\nendtype\ny as egg2\ny=x",  "cannot convert egg to egg2")]
    [TestCase("type egg2\n z\nendtype\ny as egg2 = x",  "cannot convert egg to egg2")]
    public void ParseError_TypeCheck_IntToType(string assign, string error)
    {
        var input = @$"
type egg
    z
endtype
x as egg
{assign}
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        if (string.IsNullOrEmpty(error))
        {
            prog.AssertNoParseErrors();
        }
        else
        {
            prog.AssertParseErrors(1);
            var errors = prog.GetAllErrors();
            Assert.That(errors[0].CombinedMessage,
                Is.EqualTo($"{ErrorCodes.InvalidCast} | {error}"));
        }
    }
    
    [TestCase(null, "x as string", @"x = ""toast""",  null)]
    [TestCase(null, "x as integer", @"x = ""toast""",  "cannot convert string to int")]
    [TestCase(null, "x as float", @"x = ""toast""",  "cannot convert string to float")]
    [TestCase(null, "x$ as string", @"x$ = 1",  "cannot convert int to string")]
    public void ParseError_TypeCheck_DeclareAndAssign(string setup, string decl, string assign, string error)
    {
        var input = @$"{setup}
{decl}
{assign}
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        if (string.IsNullOrEmpty(error))
        {
            prog.AssertNoParseErrors();
        }
        else
        {
            prog.AssertParseErrors(1);
            var errors = prog.GetAllErrors();
            Assert.That(errors[0].Display.Substring("[1:0]".Length),
                Is.EqualTo($" - {ErrorCodes.InvalidCast} | {error}"));
        }
    }
    
    [TestCase(null, "x", "y$", @"""toast""",  "cannot convert string to int")]
    public void ParseError_TypeCheck_SimpleAssignVariable(string setup, string leftSide, string variable, string rightSide, string error)
    {
        var input = @$"{setup}
{variable} = {rightSide}
{leftSide} = {variable}
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        if (string.IsNullOrEmpty(error))
        {
            prog.AssertNoParseErrors();
        }
        else
        {
            prog.AssertParseErrors(1);
            var errors = prog.GetAllErrors();
            Assert.That(errors[0].Display.Substring("[1:0]".Length),
                Is.EqualTo($" - {ErrorCodes.InvalidCast} | {error}"));
        }
    }
    
    
    
    [Test]
    public void Primitives_All()
    {
       
        var types = new (string, string[])[]
        {
            ("INTEGER", new string[]
            {
                "5", "-3"
            }),
            ("DOUBLE INTEGER", new string[]
            {
                "5", "-5"
            }),
            ("BYTE", new string[]
            {
                "5", "-2"
            }),
            ("WORD", new string[]
            {
                "5"
            }),
            ("DWORD", new string[]
            {
                "5"
            }),
            ("BOOLEAN", new string[]
            {
                "1", "0"
            }),
            ("FLOAT", new string[]
            {
                "5"
            }),
            ("DOUBLE FLOAT", new string[]
            {
                "5"
            }),
            ("STRING", new string[]
            {
                "\"toast\""
            }),
        };

        var sourceBuilder = new StringBuilder();

        for (var i = 0 ; i < types.Length; i ++)
        {
            var data = types[i];
            var type = data.Item1;
            var values = data.Item2;

            for (var j = 0; j < values.Length; j++)
            {
                var varName = "v" + i + "_" + j;
                var value = values[j];
                sourceBuilder.AppendLine($"{varName} AS {type} = {value}");
            }
        }
        
        
        var input = sourceBuilder.ToString();
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertNoParseErrors();
    }

    [Test]
    public void ParseError_VariableReference_ArrayIndexWithoutClosingParen()
    {
        var input = @"
DIM y(1)
x = y(
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:5] - {ErrorCodes.VariableIndexMissingCloseParen}"));
    }
    
    
    [Test]
    public void ParseError_VariableReference_MissingAfterDotIndex()
    {
        var input = @"
y = 1
x = y.
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertParseErrors(2);
        // TODO: this test is breaking because the progressive parser is injecting a "_" variable name silently, but that isn't referencable.
        var errors = prog.GetAllErrors();
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4] - {ErrorCodes.ExpressionIsNotAStruct}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[2:5] - {ErrorCodes.VariableReferenceMissing}"));
    }


    [Test]
    public void ParseError_Goto_CanWork()
    {
        var input = @"
goto lbl
lbl: 
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertNoParseErrors();
    }

    
    [Test]
    public void ParseError_Goto_InsideFunction()
    {
        var input = @"

function aFunc()
    goto lbl
    lbl:
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertNoParseErrors();
        
    }

    
    [Test]
    public void ParseError_Goto_BetweenScopes_IntoFunction()
    {
        var input = @"
goto lbl
function aFunc()
    lbl: 
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0,1:5] - {ErrorCodes.TraverseLabelBetweenScopes}"));

    }
    
    
    [Test]
    public void ParseError_Goto_BetweenScopes_OutOfFunction()
    {
        var input = @"
lbl:
function aFunc()
    goto lbl
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[3:4,3:9] - {ErrorCodes.TraverseLabelBetweenScopes}"));

    }
    
    
    [Test]
    public void ParseError_Goto_BetweenScopes_BetweenFunctions()
    {
        var input = @"
function bFunc()
    lbl:
endfunction
function aFunc()
    goto lbl
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[5:4,5:9] - {ErrorCodes.TraverseLabelBetweenScopes}"));
    }


    
    [Test]
    public void ParseError_Goto_Missing()
    {
        var input = @"
goto
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.GotoMissingLabel}"));
    }
    
    
    [Test]
    public void ParseError_Gosub_Missing()
    {
        var input = @"
gosub
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.GoSubMissingLabel}"));
    }
    
    
    [Test]
    public void ParseError_Function_MissingName()
    {
        var input = @"
function ()
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.FunctionMissingName}"));
    }
    
    
    [Test]
    public void ParseError_Function_CallBeforeDefined_Works()
    {
        var input = @"
x = test(1)
function test(a)
endfunction a
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void ParseError_Function_DefinedTwice()
    {
        var input = @"
function test()
endfunction
function test(a)
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[3:9] - {ErrorCodes.FunctionAlreadyDeclared}"));
    }
    
    [Test]
    public void ParseError_Function_MissingOpenParen()
    {
        var input = @"
function toast)
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.FunctionMissingOpenParen}"));
    }
    
    
    [Test]
    public void ParseError_Function_IncorrectParameters()
    {
        var input = @"
for x = 1 to 3
    jerk toast(x,) 
next

end

function toast(a, b)
endfunction a + b
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:9] - {ErrorCodes.FunctionParameterCardinalityMismatch}"));
    }
    
    [Test]
    public void ParseError_Function_MissingCloseParen()
    {
        var input = @"
function toast(
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:14] - {ErrorCodes.FunctionMissingCloseParen}"));
    }
    
    [Test]
    public void ParseError_Function_HeaderEof()
    {
        var input = @"
function toast(";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(2));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:14] - {ErrorCodes.FunctionMissingCloseParen}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[1:0] - {ErrorCodes.FunctionMissingEndFunction}"));
    }
    
    
    [Test]
    public void ParseError_Function_InvalidArg()
    {
        var input = @"
function toast(3)
endfunction";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:15] - {ErrorCodes.ExpectedParameter}"));
    }
    
    [Test]
    public void ParseError_Function_FunctionInner()
    {
        var input = @"
function toast(x)
    function eggs(y)
    endfunction
endfunction";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4] - {ErrorCodes.FunctionDefinedInsideFunction}"));
    }
    
    [Test]
    public void ParseError_Function_ParameterOutOfScope()
    {
        var input = @"
function toast(x)
endfunction
a = x";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[3:4] - {ErrorCodes.InvalidReference} | unknown symbol, x"));
    }
    
    
    
    
    [Test]
    public void ParseError_StructCastToForLoopVariable()
    {
        var input = $@"
type egg
    x
endtype
n as egg

for n = 1 to 4
next
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].Display, Is.EqualTo($"[6:4] - {ErrorCodes.InvalidCast} | cannot convert int to egg"));
    }

    
    [Test]
    public void ParseError_GlobalUsedBeforeDeclared()
    {
        var input = $@"

global x1 = 1
result = f1()

global x2 = 2
function f1() 
endfunction x1 + x2


";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].Display, Is.EqualTo($"[7:17] - {ErrorCodes.SymbolNotDeclaredYet} | symbol, x2"));
    }

    
    [Test]
    public void ParseError_Function_CanUseGlobal()
    {
        var input = @"
global y as integer
function toast(x)
x = y
endfunction";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void ParseError_Function_CanUseGlobal2()
    {
        var input = @"
global number as integer

function mult(a, b)
    for n = 1 to b
        number = number + a
    next
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertNoParseErrors();
    }


    
    [Test]
    public void ParseError_Function_InvalidSymbol()
    {
        var input = @"
function toast(x)
x = y
endfunction";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4] - {ErrorCodes.InvalidReference} | unknown symbol, y"));
    }
    
    [Test]
    public void ParseError_Function_InvalidArg_Multiple()
    {
        var input = @"
function toast(3, x, for)
endfunction";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(2));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:15] - {ErrorCodes.ExpectedParameter}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[1:21] - {ErrorCodes.ExpectedParameter}"));
    }
    
    
    [Test]
    public void ParseError_Scoped_Works()
    {
        var input = @"
local x";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        // var errors = prog.GetAllErrors();
        prog.AssertNoParseErrors();
        // Assert.That(errors[0].Display, Is.EqualTo($"[1:7] - {ErrorCodes.ScopedDeclarationExpectedAs}"));
        // Assert.That(errors[1].Display, Is.EqualTo($"[1:6] - {ErrorCodes.DeclarationMissingTypeRef}"));

    }
    
    
    [Test]
    public void ParseError_Scope_InvalidKeyword()
    {
        var input = @"
local for";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:9] - {ErrorCodes.ScopedDeclarationInvalid}"));

    }
    
    [Test]
    public void ParseError_PointerUnsupported()
    {
        var input = @"
x = 4
y = *x
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4] - {ErrorCodes.PointersAreNotSupported}"));

    }
    
       
    [Test]
    public void ParseError_Scope_Dim()
    {
        var input = @"
local dim";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:9] - {ErrorCodes.ArrayDeclarationInvalid}"));

    }

    
    [Test]
    public void ParseError_Scope_Dim_MissingOpenCloseAndOther()
    {
        var input = @"
local dim x";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(3));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:11] - {ErrorCodes.ArrayDeclarationMissingOpenParen}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[1:10] - {ErrorCodes.ArrayDeclarationRequiresSize}"));
        Assert.That(errors[2].Display, Is.EqualTo($"[1:11] - {ErrorCodes.ArrayDeclarationMissingCloseParen}"));

    }
    
    
    [Test]
    public void ParseError_Scope_Dim_MissingOpen()
    {
        var input = @"
local dim x 3)";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:12] - {ErrorCodes.ArrayDeclarationMissingOpenParen}"));
    }

    
    [Test]
    public void ParseError_Scope_Dim_MissingClose()
    {
        var input = @"
local dim x(3";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:13] - {ErrorCodes.ArrayDeclarationMissingCloseParen}"));
    }

    [Test]
    public void ParseError_Scope_Dim_MissingSize()
    {
        var input = @"
local dim x()";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:11] - {ErrorCodes.ArrayDeclarationRequiresSize}"));
    }
    
    
    [Test]
    public void ParseError_Scope_Dim_InvalidSize()
    {
        var input = @"
local dim x(for)";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(2));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:11] - {ErrorCodes.ArrayDeclarationRequiresSize}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[1:12] - {ErrorCodes.ArrayDeclarationInvalidSizeExpression}"));
    }

    
    [Test]
    public void ParseError_Scope_Dim_TooManySizes()
    {
        var input = @"
local dim x(1,2,3,4,5,842)";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:21] - {ErrorCodes.ArrayDeclarationSizeLimit}"));
    }

    
    [Test]
    public void ParseError_Scope_Dim_TooManySizes_InvalidLate()
    {
        var input = @"
local dim x(1,2,3,4,5,842, for)";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(2));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:21] - {ErrorCodes.ArrayDeclarationSizeLimit}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[1:27] - {ErrorCodes.ArrayDeclarationInvalidSizeExpression}"));
    }

    
    [Test]
    public void ParseError_TypeDef_MissingEndType()
    {
        var input = @"
Type someTypeName
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.TypeDefMissingEndType}"));
    }
    
    
    [Test]
    public void ParseError_TypeDef_MissingName()
    {
        var input = @"
Type
    x
endtype
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.TypeDefMissingName}"));
    }

    [Test]
    public void ParseError_Array_Assign_Implicit()
    {
        var input = @"
dim x(3) as word
y = x
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0] - {ErrorCodes.ImplicitArrayDeclaration}"));

    }
    
    
    [Test]
    public void ParseError_Array_Assign_NoErrors()
    {
        var input = @"
dim x(3) as word
y = x(1)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

    
    [Test]
    public void ParseError_TypeDef_AssignmentImplicit__NoErrors()
    {
        var input = @"
TYPE egg
    x
ENDTYPE
a as egg
b = a
b.x = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    [Test]
    public void ParseError_TypeDef__AssignmentImplicit_Array_NoErrors()
    {
        var input = @"
TYPE egg
    x
ENDTYPE
DIM eggs(3) AS egg
e = eggs(1)
n = e.x
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

    [Test]
    public void ParseError_TypeDef_AssignmentImplicit_StructRef_NoErrors()
    {
        var input = @"
TYPE egg
    x as chicken
ENDTYPE
TYPE chicken
    y 
ENDTYPE
test as egg
y = test.x
z = y.y
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void ParseError_TypeDef_BadFieldAssignment()
    {
        var input = @"
TYPE egg
    x
ENDTYPE
fish AS egg
fish.y = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[5:5] - {ErrorCodes.StructFieldDoesNotExist}"));
    }
    
    
    
    
    [Test]
    public void ParseError_TypeDef_InsideFunction()
    {
        var input = @"
function aFunc()
    type vec
    endtype
endfunction
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:9] - {ErrorCodes.TypeMustBeTopLevel}"));
    }
    
    
    [Test]
    public void ParseError_TypeDef_InsideTypeDef()
    {
        var input = @"
type outer
    type vec
    endtype
endtype
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(2);
    }

    
    [Test]
    public void ParseError_TypeDef_FieldDeclaredTwice()
    {
        var input = @"
TYPE nut
    y
    x
    y
ENDTYPE
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[4:4] - {ErrorCodes.SymbolAlreadyDeclared}"));
    }

    
    [Test]
    public void ParseError_TypeDef_UnknownSubField()
    {
        var input = @"
TYPE nut
    y as egg
ENDTYPE
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4,2:9] - {ErrorCodes.StructFieldReferencesUnknownStruct}"));
    }
    
    
    [Test]
    public void ParseError_TypeDef_DeclareToSomethingThatDoesNotExist()
    {
        var input = @"
GLOBAL x AS tunafish
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:12] - {ErrorCodes.UnknownType}"));
    }
    
    [Test]
    public void ParseError_TypeDef_RecursiveSelf()
    {
        var input = @"
TYPE nut
    y as nut
ENDTYPE
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:5] - {ErrorCodes.StructFieldsRecursive}"));
    }
    
    
    [Test]
    public void ParseError_TypeDef_Recursive_None_ButMultipleReferences()
    {
        var input = @"
TYPE vector
    x as float
    y as float
ENDTYPE
TYPE object 
    pos as vector
    vel as vector
ENDTYPE
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }

    
    [Test]
    public void ParseError_TypeDef_RecursiveChain2()
    {
        var input = @"
TYPE chicken
    y as egg
ENDTYPE
TYPE egg 
    x as chicken
ENDTYPE
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(2);
        Assert.That(errors[0].Display, Is.EqualTo($"[1:5] - {ErrorCodes.StructFieldsRecursive}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[4:5] - {ErrorCodes.StructFieldsRecursive}"));
    }
    
    
    [Test]
    public void ParseError_TypeDef_RecursiveChain_3()
    {
        var input = @"
remstart

a -> dep
b -> a
b -> dep

remend

TYPE dep
    x
ENDTYPE
TYPE a 
    d as dep
ENDTYPE
TYPE b
    d as dep
    a as a
ENDTYPE
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertNoParseErrors();
    }

    
    [Test]
    public void ParseError_TypeDef_RecursiveChain2_Casing()
    {
        var input = @"
TYPE chicken
    y as egg
ENDTYPE
TYPE egg 
    x as CHICKEN
ENDTYPE
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(2);
        
        Assert.That(errors[0].Display, Is.EqualTo($"[1:5] - {ErrorCodes.StructFieldsRecursive}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[4:5] - {ErrorCodes.StructFieldsRecursive}"));
    }
    
    [Test]
    public void ParseError_TypeDef_BadFieldAssignment_Nested_Works()
    {
        var input = @"
TYPE egg
    x
ENDTYPE
TYPE nut
    y as egg
ENDTYPE
fish AS nut
fish.y.x = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void ParseError_TypeDef_BadFieldAssignment_Nested()
    {
        var input = @"
TYPE egg
    x
ENDTYPE
TYPE nut
    y as egg
ENDTYPE
fish AS nut
fish.y.z = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[8:7] - {ErrorCodes.StructFieldDoesNotExist}"));
    }
    [Test]
    public void ParseError_TypeDef_BadFieldAssignment_NotAStruct()
    {
        var input = @"
fish AS integer
fish.y = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0] - {ErrorCodes.ExpressionIsNotAStruct}"));
    }
    
    [Test]
    public void ParseError_TypeDef_UnknownVariableRoot()
    {
        var input = @"
TYPE egg
    x
ENDTYPE
fish AS egg
notfish.y = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[5:0] - {ErrorCodes.InvalidReference} | unknown symbol, notfish"));
    }
    
    [Test]
    public void ParseError_TypeDef_ReferenceOnRight_Works()
    {
        var input = @"
TYPE vector
    x as float
    y as float
ENDTYPE

TYPE object
    pos as vector
    vel as vector
ENDTYPE

player as object
player.vel.x = 1
player.pos.x = 2
player.pos.x = player.pos.x + player.vel.x

x = player.pos.x
";;
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
    }
    
    
    [Test]
    public void ParseError_TypeDef_ReferenceOnRight_InvalidTypeo()
    {
        var input = @"
TYPE vector
    x as float
    y as float
ENDTYPE

TYPE object
    pos as vector
    vel as vector
ENDTYPE

player as object
player.vel.x = 1
player.pos.x = 2
player.pos.x = player.posd.x + player.vel.x

x = player.pos.x
";;
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(1);
    }

    
    [Test]
    public void ParseError_TypeDef_BadFieldAssignment_NoVariable()
    {
        var input = @"
TYPE sampleToastThing
    x
ENDTYPE
nothing.x = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[4:0] - {ErrorCodes.InvalidReference} | unknown symbol, nothing"));
    }

    
    [Test]
    public void ParseError_If_NoEndIf()
    {
        var input = @"
if 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.IfStatementMissingEndIf}"));
    }
    [Test]
    public void ParseError_If_NoCondition()
    {
        var input = @"
if 
endif
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.ExpressionMissing}"));
    }
    
    
    [Test]
    public void ParseError_While_MissingEndwhile()
    {
        var input = @"
while 4
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.WhileStatementMissingEndWhile}"));
    }
    
    
    [Test]
    public void ParseError_While_NoCondition()
    {
        var input = @"
while 
endwhile
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.ExpressionMissing}"));
    }

    
    [Test]
    public void ParseError_While_NoCondition_FindsInnerErrors()
    {
        var input = @"
while 
x =
endwhile
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(2));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.ExpressionMissing}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[2:2] - {ErrorCodes.ExpressionMissing}"));
    }

    
    [Test]
    public void ParseError_RepeatUntil_MissingUntil()
    {
        var input = @"
repeat
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(2)); 
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.RepeatStatementMissingUntil}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[2:0] - {ErrorCodes.ExpressionMissing}"));
    }
    
    
    [Test]
    public void ParseError_RepeatUntil_MissingCondition()
    {
        var input = @"
repeat
until
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0] - {ErrorCodes.ExpressionMissing}"));
    }
    
    [Test]
    public void ParseError_DoLoop_MissingLoop()
    {
        var input = @"
do
x = 3
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.DoStatementMissingLoop}"));
    }
    
    
    [Test]
    public void ParseError_Switch_MissingEndselect()
    {
        var input = @"
x=1
select x

";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0] - {ErrorCodes.SelectStatementMissingEndSelect}"));
    }
    
    
    
    [Test]
    public void ParseError_Switch_BadToken()
    {
        var input = @"
x=1:select x
    for
endselect
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4] - {ErrorCodes.SelectStatementUnknownCase}"));
    }

    
    
    [Test]
    public void ParseError_Switch_InvalidSwitchVariable()
    {
        var input = @"
select x
    case 1
    endcase
endselect
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:7] - {ErrorCodes.InvalidReference} | unknown symbol, x"));
    }
    
    
    [Test]
    public void ParseError_Switch_InnerScopeErros()
    {
        var input = @"
x = 1
select x
    case 1
        y = n
    endcase
endselect
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[4:12] - {ErrorCodes.InvalidReference} | unknown symbol, n"));
    }
    
    [Test]
    public void ParseError_Switch_Case_MissingEndCase()
    {
        var input = @"
x = 1
select x
    case 1
endselect
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[3:4] - {ErrorCodes.CaseStatementMissingEndCase}"));
    }
    
    
    [Test]
    public void ParseError_Switch_Case_MissingCondition()
    {
        var input = @"
x = 1: select x
    case 
    endcase
endselect
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4] - {ErrorCodes.ExpectedLiteralInt}"));
    }
    
    
    [Test]
    public void ParseError_Switch_Case_BadToken()
    {
        var input = @"
x=1:select x
    case for
    endcase
endselect
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4] - {ErrorCodes.ExpectedLiteralInt}"));
    }
    
    
    [Test]
    public void ParseError_Switch_Case_Eof()
    {
        var input = @"
x=1:select x
    case";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(3));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:4] - {ErrorCodes.SelectStatementMissingEndSelect}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[2:4] - {ErrorCodes.CaseStatementMissingEndCase}"));
        Assert.That(errors[2].Display, Is.EqualTo($"[2:4] - {ErrorCodes.ExpectedLiteralInt}"));
    }
    
    
    [Test]
    public void ParseError_Switch_DefaultCase_Eof()
    {
        var input = @"
x = 1
select x
    case default";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(2));
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0] - {ErrorCodes.SelectStatementMissingEndSelect}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[3:4] - {ErrorCodes.CaseStatementMissingEndCase}"));
    }
    
    
    [Test]
    public void ParseError_Switch_DefaultCase_MissingEndCase()
    {
        var input = @"
x=1:select x
    case default
endselect
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4] - {ErrorCodes.CaseStatementMissingEndCase}"));
    }

    
    [Test]
    public void ParseError_Switch_DoubleDefault()
    {
        var input = @"
x=1:select x
    case default
    endcase
    case default
    endcase
endselect";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[4:9] - {ErrorCodes.MultipleDefaultCasesFound}"));
    }
    
    
    [Test]
    public void ParseError_Switch_DoubleDefault_FindsInnerErrors()
    {
        var input = @"
x=1:select x
    case default
    endcase
    case default
        x = 
    endcase
endselect";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(2));
        Assert.That(errors[0].Display, Is.EqualTo($"[4:9] - {ErrorCodes.MultipleDefaultCasesFound}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[5:10] - {ErrorCodes.ExpressionMissing}"));
    }
    
    [Test]
    public void ParseError_For_MissingEverything()
    {
        var input = @"
for
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(2));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.ForStatementMissingOpening}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[1:0] - {ErrorCodes.VariableReferenceMissing}"));
    }
    
    
    [Test]
    public void ParseError_For_MissingEquals()
    {
        var input = @"
for x
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(5));
        Assert.That(errors[0].errorCode, Is.EqualTo(ErrorCodes.ForStatementMissingOpening));
        Assert.That(errors[1].errorCode, Is.EqualTo(ErrorCodes.ForStatementMissingTo));
        Assert.That(errors[2].errorCode, Is.EqualTo(ErrorCodes.ForStatementMissingNext));
        Assert.That(errors[3].errorCode, Is.EqualTo(ErrorCodes.ExpressionMissing));
        Assert.That(errors[4].errorCode, Is.EqualTo(ErrorCodes.ExpressionMissing));
    }
    
    [Test]
    public void ParseError_For_MissingTo()
    {
        var input = @"
for x = 1
next
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(2));
        Assert.That(errors[0].errorCode, Is.EqualTo(ErrorCodes.ForStatementMissingTo));
        Assert.That(errors[1].errorCode, Is.EqualTo(ErrorCodes.ExpressionMissing));
    }
    
    
    [Test]
    public void ParseError_For_MissingStepExpr()
    {
        var input = @"
for x = 1 to
next
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].errorCode, Is.EqualTo(ErrorCodes.ExpressionMissing));
    }
    
    
    [Test]
    public void ParseError_For_MissingClose()
    {
        var input = @"
for x = 1 to 3
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].errorCode, Is.EqualTo(ErrorCodes.ForStatementMissingNext));
    }
    
    
    [Test]
    public void ParseError_For_BrokenStep()
    {
        var input = @"
for x = 1 to 3 step
next
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].errorCode, Is.EqualTo(ErrorCodes.ExpressionMissing));
    }
    
    
    [Test]
    public void ParseError_UnknownToken()
    {
        var input = @"
3
x = 1 + 3
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(prog.statements.Count, Is.EqualTo(2));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.UnknownStatement}"));
    }


    [Test]
    public void ParseError_VariableDecl_RedeclAssign()
    {
        var input = @"
x as integer
x as word
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        // Assert.That(errors[0].Display, Is.EqualTo($"[1:2] - {ErrorCodes.AmbiguousDeclarationOrAssignment}"));
    }

    
    [Test]
    public void ParseError_BadBinary_Number()
    {
        var input = @"
x = %3
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].Display, Is.EqualTo($"[1:2] - {ErrorCodes.ExpressionMissing}"));
    }
    
    [Test]
    public void ParseError_BadBinary_Token()
    {
        var input = @"
x = %for
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertParseErrors(1, out var errors);
        Assert.That(errors[0].Display, Is.EqualTo($"[1:2] - {ErrorCodes.ExpressionMissing}"));
    }
    
    [Test]
    public void ParseError_VariableDecl_Unknown()
    {
        var input = @"
x 3 +2
n = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:2] - {ErrorCodes.AmbiguousDeclarationOrAssignment}"));
    }

    
    [Test]
    public void ParseError_VariableDecl_BadType()
    {
        var input = @"
x as tuna
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:5] - {ErrorCodes.DeclarationInvalidTypeRef}"));
    }

    
    [Test]
    public void ParseError_VariableDecl_BadType_Eof()
    {
        var input = @"
x as 
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:2] - {ErrorCodes.DeclarationMissingTypeRef}"));
    }

    
    [Test]
    public void ParseError_VariableDec_Double_Branch()
    {
        var input = @"
y = 3
if y > 2
    x as integer
else
    x as float
endif
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[5:4] - {ErrorCodes.SymbolAlreadyDeclared}"));
    }


    
    [Test]
    public void ParseError_VariableDec_Double()
    {
        var input = @"
x as integer
x as integer
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0] - {ErrorCodes.SymbolAlreadyDeclared}"));
    }

    
    [Test]
    public void ParseError_Index_NotArray_AssignRhs()
    {
        var input = @"
x = 3
y = x(1)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4] - {ErrorCodes.CannotIndexIntoNonArray}"));

    }
    [Test]
    public void ParseError_Index_NotArray_AssignLhs()
    {
        var input = @"
x = 3
x(1) = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0,2:3] - {ErrorCodes.CannotIndexIntoNonArray}"));

    }
    
    [Test]
    public void ParseError_Assignment_Array()
    {
        var input = @"
DIM x(3)
x(2) = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertNoParseErrors();
    }
    
    [Test]
    public void ParseError_Assignment_Array_IndexWrongType_HasError()
    {
        var input = @"
DIM x(""bug"")
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[1:6] - {ErrorCodes.ArrayRankMustBeInteger}"));
    }
    
    
    [TestCase("foo$")]
    [TestCase(@"""toast""")]
    public void ParseError_Assignment_Array_IndexWrongType_HasError2(string accessor)
    {
        var input = $@"
foo$ = ""bug""
DIM x(5)
y = x({accessor})
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[3:6] - {ErrorCodes.ArrayRankMustBeInteger}"));
    }

    
    
    [TestCase("foo$")]
    [TestCase(@"""toast""")]
    public void ParseError_Assignment_Array_IndexWrongType_HasError3(string accessor)
    {
        var input = $@"
foo$ = ""bug""
DIM x(5)
x({accessor}) = 3
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[3:2] - {ErrorCodes.ArrayRankMustBeInteger}"));
    }

    
    [Test]
    public void ParseError_Assignment_Array_Idk()
    {
        // this doesn't work in LSP for some reason?
        var input = @"

DIM arr(10)
function rags(n)
    p = arr(n)
    arr(p) = 4
endfunction
end

";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertNoParseErrors();
        // Assert.That(errors[0].Display, Is.EqualTo($"[1:6] - {ErrorCodes.ExpressionMissing}"));
    }
    
    [Test]
    public void ParseError_Assignment_Array_CardinalityError_MissingExpr()
    {
        var input = @"
DIM x(3,2)
x(2) = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0,2:3] - {ErrorCodes.ArrayCardinalityMismatch}"));
    }
    
    [Test]
    public void ParseError_Assignment_Array_CardinalityError_MissingExpr2()
    {
        var input = @"
DIM x(3,2)
n = x(2)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4,2:7] - {ErrorCodes.ArrayCardinalityMismatch}"));
    }
    
    [Test]
    public void ParseError_Assignment_Array_CardinalityError_ExtraExpr()
    {
        var input = @"
DIM x(3,2)
x(2,3,1) = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[2:0,2:7] - {ErrorCodes.ArrayCardinalityMismatch}"));
    }

    [Test]
    public void ParseError_Assignment_ArrayStruct_Works()
    {
        var input = @"
TYPE egg
    x
ENDTYPE
DIM x(3) as egg
x(2).x = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertNoParseErrors();
        // Assert.That(errors[0].Display, Is.EqualTo($"[1:6] - {ErrorCodes.ExpressionMissing}"));
    }
    
    
    [Test]
    public void ParseError_Assignment_ArrayStruct_InvalidExpr()
    {
        var input = @"
TYPE egg
    x
ENDTYPE
DIM x(3) as egg
x(y).x = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[5:2] - {ErrorCodes.InvalidReference} | unknown symbol, y"));
    }
    
    
    [Test]
    public void ParseError_Assignment_ArrayStruct_ExprsDeclaredInOrder()
    {
        var input = @"
TYPE egg
    x
ENDTYPE
DIM x(3,2) as egg
`this should be valid because retandref declares a
x(retandref(a),a).x = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(0);
        // Assert.That(errors[0].Display, Is.EqualTo($"[5:2] - {ErrorCodes.InvalidReference} | unknown symbol, y"));
    }
    
    
    [Test]
    public void ParseError_Assignment_ArrayStruct_InvalidCardinality()
    {
        var input = @"
TYPE egg
    x
ENDTYPE
DIM x(3,2) as egg
x(2).x = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[5:0,5:3] - {ErrorCodes.ArrayCardinalityMismatch}"));
    }
    
    
    [Test]
    public void ParseError_Assignment_ArrayStruct_InvalidField()
    {
        var input = @"
TYPE egg
    x
ENDTYPE
DIM x(3) as egg
x(2).y = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[5:5] - {ErrorCodes.StructFieldDoesNotExist}"));
    }

    
    [Test]
    public void ParseError_Assignment_ArrayStruct_Undeclared()
    {
        var input = @"
x(2).x = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0,1:3] - {ErrorCodes.InvalidReference}"));
    }
    
    [Test]
    public void ParseError_Assignment_ArrayStruct_NotArray()
    {
        var input = @"
TYPE egg
    x
ENDTYPE
x as egg
x(2).x = 2
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        prog.AssertParseErrors(1);
        Assert.That(errors[0].Display, Is.EqualTo($"[5:0,5:3] - {ErrorCodes.CannotIndexIntoNonArray}"));
    }

    
    [Test]
    public void ParseError_MissingExpression_APlusNothing()
    {
        var input = @"
x = 3 + 
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:6] - {ErrorCodes.ExpressionMissing}"));
    }
    
    [Test]
    public void ParseError_MissingExpression_AEqualNothing()
    {
        var input = @"
x = 
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:2] - {ErrorCodes.ExpressionMissing}"));
    }
    
    
    [Test]
    public void ParseError_LexerError()
    {
        var input = @"
x = |
";
        var parser = MakeParser(input);
        
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        var lexingErrors = parser.GetLexingErrors();
        
        Assert.That(lexingErrors.Count, Is.EqualTo(1));

        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:2] - {ErrorCodes.ExpressionMissing}"));
    }
    
    [Test]
    public void ParseError_MissingExpression_LeftParen()
    {
        var input = @"
x = (
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:4] - {ErrorCodes.ExpressionMissingAfterOpenParen}"));
    }
    
    [Test]
    public void ParseError_MissingExpression_MissingRightParen()
    {
        var input = @"
x = ( 3
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:4] - {ErrorCodes.ExpressionMissingCloseParen}"));
    }
     
    [Test]
    public void ParseError_MissingExpression_MissingRightParen_Nested()
    {
        var input = @"
x = ( ( 3 )
y = 2 + 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:4] - {ErrorCodes.ExpressionMissingCloseParen}"));
    }
}