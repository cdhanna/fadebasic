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
}
