using FadeBasic;
using FadeBasic.Sdk;
using FadeBasic.Lib.Standard;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class MusicFbasicReproTests
{
    private const string Src = @"
print ""starting""

global x = 4

wait ms 1000
lbl1:

y = add(2)
lbl2:

function add(a)
    sum = a + x

    addfinal: 
endfunction sum

test abc
    print ""running test""
    mock wait ms
    endmock

    runto lbl1
    assert x = 4
    runto lbl2:
    assert y > x
endtest
";

    [Test]
    public void MusicFbasic_RunsCleanly()
    {
        var commands = new CommandCollection(new ConsoleCommands(), new StandardCommands());
        var ok = Fade.TryCreateFromString(Src, commands, out var ctx, out var errors);
        Assert.That(ok, Is.True, errors?.ToDisplay() ?? "(null errors)");

        var result = ctx.RunTest("abc");
        Assert.That(result.passed, Is.True,
            "expected pass; failure: " + result.failureMessage);
    }

    // Mirrors the user-reported scenario: mock the 1-arg overload of INPUT
    // and verify it writes back through the ref param. INPUT has two
    // overloads (`input(ref string)` and `input(string, ref string)`), so
    // the compiler must filter to the 1-arg version based on param count
    // — otherwise the body's prelude binds against the wrong signature
    // and the VM stack underflows at dispatch.
    // The user's "I expected this to error" scenario: mock the 1-arg
    // ref overload of `input` but never assign `val$` in the body. The
    // ref-not-assigned check should fire — but only if the visitor picks
    // the OVERLOAD MATCHING the user's param count, not just overloads[0]
    // (which for input is the 2-arg `(prompt, ref output)` form, where
    // overload[0].arg0 is a value `prompt`, not a ref).
    [Test]
    public void InputOverloadMock_RefUnassigned_Errors()
    {
        var src = @"
input x$
_L1:
end

test sample
    mock input(val$)
        ` val$ never assigned
    endmock
    runto _L1
    assert x$ = ""toast""
endtest
";
        var commands = new CommandCollection(new ConsoleCommands(), new StandardCommands());
        Fade.TryCreateFromString(src, commands, out _, out var errors);
        var hasRefError = errors != null
            && errors.ParserErrors.Any(e => e.errorCode.Equals(ErrorCodes.MockRefParamNotAssigned));
        Assert.That(hasRefError, Is.True,
            "expected MockRefParamNotAssigned on `val$`; got: "
            + (errors == null ? "(null errors)" : errors.ToDisplay()));
    }

    [Test]
    public void InputOverloadMock_WritesBackToCaller()
    {
        var src = @"
input x$
_L1:
end

test sample
    mock input(val$)
        val$ = ""toast""
    endmock
    runto _L1
    assert x$ = ""toast""
endtest
";
        var commands = new CommandCollection(new ConsoleCommands(), new StandardCommands());
        var ok = Fade.TryCreateFromString(src, commands, out var ctx, out var errors);
        Assert.That(ok, Is.True, errors?.ToDisplay() ?? "(null errors)");

        var result = ctx.RunTest("sample");
        Assert.That(result.passed, Is.True,
            "expected pass; failure: " + result.failureMessage);
    }
}
