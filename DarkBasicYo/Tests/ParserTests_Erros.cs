using DarkBasicYo;
using DarkBasicYo.Ast;

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
    public void ParseError_VariableReference_ArrayIndexWithoutClosingParen()
    {
        var input = @"
x = y(
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:5] - {ErrorCodes.VariableIndexMissingCloseParen}"));
    }
    
    
    [Test]
    public void ParseError_VariableReference_MissingAfterDotIndex()
    {
        var input = @"
x = y.
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:5] - {ErrorCodes.VariableReferenceMissing}"));
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
    public void ParseError_Scoped()
    {
        var input = @"
local x";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(2));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:7] - {ErrorCodes.ScopedDeclarationExpectedAs}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[1:6] - {ErrorCodes.DeclarationMissingTypeRef}"));

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
        Assert.That(errors[1].Display, Is.EqualTo($"[2:6] - {ErrorCodes.ExpressionMissing}"));
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
select x

";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.SelectStatementMissingEndSelect}"));
    }
    
    
    
    [Test]
    public void ParseError_Switch_BadToken()
    {
        var input = @"
select x
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
    public void ParseError_Switch_Case_MissingEndCase()
    {
        var input = @"
select x
    case 1
endselect
";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Display, Is.EqualTo($"[2:4] - {ErrorCodes.CaseStatementMissingEndCase}"));
    }
    
    
    [Test]
    public void ParseError_Switch_Case_MissingCondition()
    {
        var input = @"
select x
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
select x
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
select x
    case";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(3));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.SelectStatementMissingEndSelect}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[2:4] - {ErrorCodes.CaseStatementMissingEndCase}"));
        Assert.That(errors[2].Display, Is.EqualTo($"[2:4] - {ErrorCodes.ExpectedLiteralInt}"));
    }
    
    
    [Test]
    public void ParseError_Switch_DefaultCase_Eof()
    {
        var input = @"
select x
    case default";
        var parser = MakeParser(input);
        var prog = parser.ParseProgram();

        var errors = prog.GetAllErrors();
        Assert.That(errors.Count, Is.EqualTo(2));
        Assert.That(errors[0].Display, Is.EqualTo($"[1:0] - {ErrorCodes.SelectStatementMissingEndSelect}"));
        Assert.That(errors[1].Display, Is.EqualTo($"[2:4] - {ErrorCodes.CaseStatementMissingEndCase}"));
    }
    
    
    [Test]
    public void ParseError_Switch_DefaultCase_MissingEndCase()
    {
        var input = @"
select x
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
select x
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
select x
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