using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Ast.Visitors;

namespace Tests;

public partial class ParserTests
{
    
    [Test]
    public void Haunted_ValidTokenization()
    {
        var input = @"
#macro
    x = macro return test()
    # y = [x]
#endmacro
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        prog.AssertNoParseErrors();
    }
    
    
    [Test]
    public void Haunted_Forced()
    {
        var input = @"
x = 1
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram(new ParseOptions
        {
            ignoreChecks = true
        });

        prog.AssertNoParseErrors();

        var assignment = prog.statements[0] as AssignmentStatement;
        assignment.variable.TransitiveFlags |= TransitiveTypeFlags.Haunted;
        
        // assert that the value is haunted...
        Assert.That(assignment.variable.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
    }

    
    [Test]
    public void Haunted_CarryByAssignment()
    {
        var input = @"
x = 1
y = x
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram(new ParseOptions
        {
            ignoreChecks = true
        });

        prog.AssertNoParseErrors();

        // force the first variable to be haunted. 
        var assignment = prog.statements[0] as AssignmentStatement;
        assignment.variable.TransitiveFlags |= TransitiveTypeFlags.Haunted;
        
        // run the haunting...
        prog.AddScopeRelatedErrors(new ParseOptions());
        
        // assert that the value is haunted...
        var secondAssignment = prog.statements[1] as AssignmentStatement;
        Assert.That(secondAssignment.expression.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
        Assert.That(secondAssignment.variable.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
    }
    
    [TestCase(@"
x = 1
y = x ` it is not invalid yet
refDbl x
y = x
", 1, false)]
    [TestCase(@"
x = 1
y = x
refDbl x
y = x `now it is invalid
", 3, true)]
    [TestCase(@"
x = 1
refDbl x
x = 3 `it has been reassigned. 
y = x `it is valid again
", 3, false)]
    [TestCase(@"
refDbl x
if x
    n = 1
endif
y = n
", 2, true)]
    [TestCase(@"
refDbl x
if 3
    n = 1
endif
y = n
", 2, false)]
    [TestCase(@"
refDbl x
while x
    n = 1
endwhile
y = n
", 2, true)]
    [TestCase(@"
refDbl x
do
    n = 1
loop
y = n
", 2, false)]
    [TestCase(@"
refDbl x
repeat
    n = 1
until x
y = n
", 2, true)]
    [TestCase(@"
refDbl x
if 1
    if x
        n = 1
    endif
endif
y = n
", 2, true)]
    [TestCase(@"
refDbl x
if x
    if 1
        n = 1
    endif
endif
y = n
", 2, true)]
    [TestCase(@"
refDbl x
if 1
    if x
        if 1
            n = 1
        endif
    endif
endif
y = n
", 2, true)]
    [TestCase(@"
refDbl x
for a = 1 to x
    n = 3
next
y = n
", 2, true)]
    [TestCase(@"
refDbl x
for a = x to 5
    n = 3
next
y = n
", 2, true)]
    [TestCase(@"
refDbl x
for x = 3 to 5
    n = 3
next
y = n
", 2, false)]
    [TestCase(@"
refDbl x
for a = 3 to 5 step x
    n = 3
next
y = n
", 2, true)]
    [TestCase(@"
refDbl x
select x
    case 1
        n = 1
    endcase
endselect
y = n
", 2, true)]
    [TestCase(@"
refDbl x
select x
    case default
        n = 1
    endcase
endselect
y = n
", 2, true)]
    [TestCase(@"
z = ex()
y = z

function ex()
    refDbl x
endfunction x
", 1, true)]
    [TestCase(@"
z = ex()
y = z

function ex()
    refDbl x
    y = x + 1
endfunction y
", 1, true)]
    [TestCase(@"
z = ex()
y = z

function ex()
    refDbl x
endfunction 8 + x
", 1, true)]
    [TestCase(@"
z = ex()
y = z

function ex2(n)
    exitfunction overloadA(n)
endfunction 0
function ex()
endfunction ex2(2)
", 1, true)]
    [TestCase(@"
z = ex(overloadA(3))
y = z

function ex(n)
endfunction n + 2
", 1, true)]
    [TestCase(@"
type egg 
    x
endtype
e as egg
x = 3
refDbl x
e.x = x
y = e.x
", 4, true)]
    [TestCase(@"
type egg 
    x
endtype
e as egg
x = 3
refDbl x
e.x = x
e.x = 1
y = e.x `you cannot unhaunt a struct memberwise. 
", 5, true)]
    [TestCase(@"
type egg 
    x
endtype
e as egg
e2 as egg
x = 3
refDbl x
e.x = x
e2 = e
y = e2.x
", 6, true)]
    [TestCase(@"
type egg 
    c as chi
endtype
type chi
    a
endtype
e as egg
x = overloadA(4) `x is haunted
e.c.a = x
y = e.c
", 4, true)]
    [TestCase(@"
type egg 
    c as chi
endtype
type chi
    a
endtype
e as egg
x = overloadA(4) `x is haunted
e.c.a = x
e.c.a = 4
y = e.c ` you cannot unhaunt a struct memberwise
", 5, true)]
    [TestCase(@"
type egg 
    c
endtype
e as egg
e2 as egg
x = overloadA(4) `x is haunted
e.c = x `now e is haunted
e = e2 `e is no longer huanted. 
y = e.c
", 5, false)]

    [TestCase(@"
dim x(3)
x(2) = overloadA(2)
y = x(1)
", 2, true)]
    [TestCase(@"
dim x(3)
x(2) = overloadA(2)
x(2) = 1 `the array cannot be unhaunted. 
y = x(1)
", 3, true)]
    [TestCase(@"
dim x(3)
y = x(1) `the array is not haunted YET
x(2) = overloadA(2)
", 1, false)]
    [TestCase(@"
dim x(3)
n = overloadA(2)
x(n) = 1 `the array is haunted due to arg
y = x(0)
", 3, true)]
    [TestCase(@"
dim x(3)
n = overloadA(2)
q = x(n) `the array is not haunted; q is. 
y = x(0)
", 3, false)]
    [TestCase(@"
dim x(3)
n = overloadA(2)
q = x(n) `the array is not haunted; q is. 
y = x(0)
", 2, true)]
    [TestCase(@"
type egg
    x
    y
endtype
e as egg 
e.x = overloadA(2)
e.y = 5
y = e.x
", 3, true)]
    [TestCase(@"
type egg
    x
    y
endtype
e as egg = {
    x = overloadA(2)
    y = 5
}
y = e.x
", 3, true)]
    public void Haunted_GeneralAssignment(string src, int assignmentIndex, bool isHaunt)
    {
        Console.WriteLine("\n###### SRC ####");
        Console.WriteLine(src);
        Console.WriteLine("\n###############");
        var parser = MakeParser(src);
        var prog = parser.ParseProgram(new ParseOptions
        {
            // ignoreChecks = true
        });

        prog.AssertNoParseErrors();

        var secondAssignment = prog.statements[assignmentIndex] as AssignmentStatement;
        Console.WriteLine("EXPECTING TO BE HAUNTED: " + isHaunt);
        Console.WriteLine(secondAssignment);
        Assert.That(isHaunt == secondAssignment.expression.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
        Assert.That(isHaunt == secondAssignment.variable.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
    }
    
    
    [Test]
    public void Haunted_Error_None()
    {
        HauntedErrorCheck(@"
#macro
    x = macro return test()
    y = x
    # a = 1
#endmacro
", new List<string>());
    }
    
    [Test]
    public void Haunted_Error_InsideIf()
    {
        HauntedErrorCheck(@"
#macro
    x = macro return test()
    y = x
#tokenize
a = 1
#endtokenize
#endmacro
", new List<string>
        {
            "!"
        });
    }

    public void HauntedErrorCheck(string src, List<string> errors)
    {
        var parser = MakeParser(src);
        var prog = parser.ParseProgram();
        prog.AssertParseErrors(errors.Count, out var actualErrors);
        for (var i = 0; i < errors.Count; i++)
        {
            var expected = errors[i];
            var actual = actualErrors[i];
            
            Assert.That(actual.Display, Is.EqualTo(expected));
        }
    }
    
    [Test]
    public void Haunted_Spawn_ByCommand_Return()
    {
        var input = @"
x = overloadA(4)
y = x
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram(new ParseOptions
        {
            // ignoreChecks = true
        });

        prog.AssertNoParseErrors();

        // force the first variable to be haunted. 
        var assignment = prog.statements[0] as AssignmentStatement;
        Assert.That(assignment.variable.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
        Assert.That(assignment.expression.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
        
        // assignment.variable.TransitiveFlags |= TransitiveTypeFlags.Haunted;
        
        // run the haunting...
        // prog.AddScopeRelatedErrors(new ParseOptions());
        
        // assert that the value is haunted...
        var secondAssignment = prog.statements[1] as AssignmentStatement;
        Assert.That(secondAssignment.expression.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
        Assert.That(secondAssignment.variable.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
    }
    
    
    [Test]
    public void Haunted_Spawn_ByCommand_Ref()
    {
        var input = @"
refDbl x
y = x
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram(new ParseOptions
        {
            // ignoreChecks = true
        });

        prog.AssertNoParseErrors();

        var secondAssignment = prog.statements[1] as AssignmentStatement;
        Assert.That(secondAssignment.expression.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
        Assert.That(secondAssignment.variable.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
    }

    
    [Test]
    public void Haunted_CarryByAssignment_CommandArg_Return()
    {
        var input = @"
x = 1
y = overloadA(x)
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram(new ParseOptions
        {
            ignoreChecks = true
        });

        prog.AssertNoParseErrors();

        // force the first variable to be haunted. 
        var assignment = prog.statements[0] as AssignmentStatement;
        assignment.variable.TransitiveFlags |= TransitiveTypeFlags.Haunted;
        
        // run the haunting...
        prog.AddScopeRelatedErrors(new ParseOptions());
        
        // assert that the value is haunted...
        var secondAssignment = prog.statements[1] as AssignmentStatement;
        Assert.That(secondAssignment.expression.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
        Assert.That(secondAssignment.variable.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
    }
    
    [Test]
    public void Haunted_CarryByAssignment_WithUnary()
    {
        var input = @"
x = 1
y = not x
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram(new ParseOptions
        {
            ignoreChecks = true
        });

        prog.AssertNoParseErrors();

        // force the first variable to be haunted. 
        var assignment = prog.statements[0] as AssignmentStatement;
        assignment.variable.TransitiveFlags |= TransitiveTypeFlags.Haunted;
        
        // run the haunting...
        prog.AddScopeRelatedErrors(new ParseOptions());
        
        // assert that the value is haunted...
        var secondAssignment = prog.statements[1] as AssignmentStatement;
        Assert.That(secondAssignment.expression.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
        Assert.That(secondAssignment.variable.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
    }
    
    [Test]
    public void Haunted_CarryByAssignment_WithCompound()
    {
        var input = @"
x = 1
y = 5 + x
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram(new ParseOptions
        {
            ignoreChecks = true
        });

        prog.AssertNoParseErrors();

        // force the first variable to be haunted. 
        var assignment = prog.statements[0] as AssignmentStatement;
        assignment.variable.TransitiveFlags |= TransitiveTypeFlags.Haunted;
        
        // run the haunting...
        prog.AddScopeRelatedErrors(new ParseOptions());
        
        // assert that the value is haunted...
        var secondAssignment = prog.statements[1] as AssignmentStatement;
        Assert.That(secondAssignment.expression.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
        Assert.That(secondAssignment.variable.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted));
    }
}