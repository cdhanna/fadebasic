using FadeBasic;
using FadeBasic.Ast;

namespace Tests;

/// <summary>
/// Mid-edit error-recovery behavior: an unterminated block (the state the
/// editor sees on every keystroke before the user types the closer) must
/// produce exactly one error, anchored at the unfinished block — never a
/// cascade through enclosing blocks or call sites.
/// </summary>
public partial class ParserTests
{
    List<ParseError> ParseAndCollect(string src)
    {
        var parser = MakeParser(src);
        var prog = parser.ParseProgram();
        return prog.GetAllErrors();
    }

    [Test]
    public void UnterminatedIf_InsideFunction_SingleError_FunctionStaysIntact()
    {
        // while the user is typing the `if`, the enclosing function must
        // still close (endfunction is NOT swallowed into the if body), and
        // the call site must not produce a void-conversion error.
        var errors = ParseAndCollect(@"
function foo(n)
    if n > 0
    a = 1
    b = 2
endfunction a
result = foo(3)
");
        Assert.That(errors.Count, Is.EqualTo(1), string.Join("\n", errors.Select(e => e.Display)));
        Assert.That(errors[0].Display, Does.Contain("0108")); // missing EndIf, anchored at the if
    }

    [Test]
    public void UnterminatedIf_AboveLoops_SingleError()
    {
        var errors = ParseAndCollect(@"
x = 1
if x > 0
for n = 1 to 10
    print n
next n
");
        Assert.That(errors.Count, Is.EqualTo(1), string.Join("\n", errors.Select(e => e.Display)));
        Assert.That(errors[0].Display, Does.Contain("0108"));
    }

    [Test]
    public void UnterminatedFor_InsideFunction_SingleError_FunctionStaysIntact()
    {
        var errors = ParseAndCollect(@"
function foo(n)
    for i = 1 to n
    a = 1
endfunction a
result = foo(3)
");
        Assert.That(errors.Count, Is.EqualTo(1), string.Join("\n", errors.Select(e => e.Display)));
        Assert.That(errors[0].Display, Does.Contain("0114")); // missing Next
    }

    [Test]
    public void UnterminatedWhile_BeforeEndIf_SingleError_IfStaysIntact()
    {
        var errors = ParseAndCollect(@"
x = 1
if x > 0
    while x < 10
    x = x + 1
endif
");
        Assert.That(errors.Count, Is.EqualTo(1), string.Join("\n", errors.Select(e => e.Display)));
        Assert.That(errors[0].Display, Does.Contain("0109")); // missing EndWhile
    }

    [Test]
    public void UnterminatedRepeat_BeforeNext_SingleError_ForStaysIntact()
    {
        var errors = ParseAndCollect(@"
for n = 1 to 3
    repeat
    n = n + 1
next n
");
        Assert.That(errors.Count, Is.EqualTo(1), string.Join("\n", errors.Select(e => e.Display)));
        Assert.That(errors[0].Display, Does.Contain("0110")); // missing Until
    }

    [Test]
    public void BrokenFunction_PoisonsReturnType_NoCallSiteCascade()
    {
        // a function whose body is structurally broken has an UNKNOWN return
        // type, not void — assigning its result must not add a second error.
        var errors = ParseAndCollect(@"
function broken(n)
    if n >
endfunction a
result = broken(3)
print result
");
        foreach (var e in errors)
        {
            Assert.That(e.Display, Does.Not.Contain("0301"),
                "call site must not report a cast error for a broken function:\n" + e.Display);
        }
    }

    [Test]
    public void TerminatedBlocks_StillParseClean()
    {
        // sanity: complete versions of all the constructs above are error-free.
        var errors = ParseAndCollect(@"
function foo(n)
    if n > 0
        for i = 1 to n
            while i < 5
                repeat
                    i = i + 1
                until i > 2
            endwhile
        next i
    endif
endfunction n
result = foo(3)
print result
");
        Assert.That(errors.Count, Is.EqualTo(0), string.Join("\n", errors.Select(e => e.Display)));
    }
}
