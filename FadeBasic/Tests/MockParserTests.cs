using FadeBasic;
using FadeBasic.Ast;

namespace Tests;

[TestFixture]
public class MockParserTests
{
    private ProgramNode Parse(string src, out List<ParseError> errors)
    {
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        lex.AssertNoLexErrors();
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        errors = prog.GetAllErrors();
        return prog;
    }

    private MockStatement FindFirstMock(ProgramNode prog)
    {
        foreach (var t in prog.tests)
        {
            foreach (var stmt in t.statements)
            {
                if (stmt is MockStatement m) return m;
            }
        }
        return null;
    }

    private ClearMockStatement FindFirstClearMock(ProgramNode prog)
    {
        foreach (var t in prog.tests)
        {
            foreach (var stmt in t.statements)
            {
                if (stmt is ClearMockStatement c) return c;
            }
        }
        return null;
    }

    [Test]
    public void Mock_InlineReturns_ParsesAsAlwaysFrequency()
    {
        var src = @"
test foo
    mock screen width returns 10
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "no parse errors expected; got: " + string.Join(", ", errs.Select(e => e.Display)));

        var mock = FindFirstMock(prog);
        Assert.That(mock, Is.Not.Null);
        Assert.That(mock.commandName, Is.EqualTo("screen width"));
        Assert.That(mock.entries.Count, Is.EqualTo(1));
        Assert.That(mock.entries[0].kind, Is.EqualTo(MockEntryKind.Returns));
        Assert.That(mock.entries[0].frequency, Is.EqualTo(MockFrequencyKind.Always));
        Assert.That(mock.entries[0].returnExpression, Is.Not.Null);
    }

    [Test]
    public void Mock_InlineForbid_ParsesAsAlwaysFrequency()
    {
        var src = @"
test foo
    mock screen width forbid
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty);
        var mock = FindFirstMock(prog);
        Assert.That(mock.entries.Count, Is.EqualTo(1));
        Assert.That(mock.entries[0].kind, Is.EqualTo(MockEntryKind.Forbid));
        Assert.That(mock.entries[0].frequency, Is.EqualTo(MockFrequencyKind.Always));
    }

    [Test]
    public void Mock_FrequencyOnce_Parses()
    {
        var src = @"
test foo
    mock screen width returns 10 once
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty);
        var mock = FindFirstMock(prog);
        Assert.That(mock.entries[0].frequency, Is.EqualTo(MockFrequencyKind.Once));
    }

    [Test]
    public void Mock_FrequencyNTimes_Parses()
    {
        var src = @"
test foo
    mock screen width returns 10 3 times
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "expected clean parse; got: " + string.Join(", ", errs.Select(e => e.Display)));
        var mock = FindFirstMock(prog);
        Assert.That(mock.entries[0].frequency, Is.EqualTo(MockFrequencyKind.NTimes));
        Assert.That(mock.entries[0].countExpression, Is.Not.Null);
    }

    [Test]
    public void Mock_FrequencyAlwaysExplicit_Parses()
    {
        var src = @"
test foo
    mock screen width returns 10 always
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty);
        var mock = FindFirstMock(prog);
        Assert.That(mock.entries[0].frequency, Is.EqualTo(MockFrequencyKind.Always));
    }

    [Test]
    public void Mock_BlockForm_MultipleEntries_Parses()
    {
        var src = @"
test foo
    mock screen width
        returns 10 once
        returns 20 once
        returns 5 always
    endmock
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "expected clean parse; got: " + string.Join(", ", errs.Select(e => e.Display)));
        var mock = FindFirstMock(prog);
        Assert.That(mock.entries.Count, Is.EqualTo(3));
        Assert.That(mock.entries[0].frequency, Is.EqualTo(MockFrequencyKind.Once));
        Assert.That(mock.entries[1].frequency, Is.EqualTo(MockFrequencyKind.Once));
        Assert.That(mock.entries[2].frequency, Is.EqualTo(MockFrequencyKind.Always));
    }

    [Test]
    public void Mock_MissingEndMock_Errors()
    {
        var src = @"
test foo
    mock screen width
        returns 10 once
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockMissingEndMock)),
            Is.True,
            "expected MockMissingEndMock; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_UnknownCommand_Errors()
    {
        // `not_a_real_command` won't merge into a CommandWord token, so the
        // parser sees a missing command name.
        var src = @"
test foo
    mock not_a_real_command returns 10
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockMissingCommandName)),
            Is.True,
            "expected MockMissingCommandName; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_UnreachableEntry_AfterAlways_Warns()
    {
        var src = @"
test foo
    mock screen width
        returns 10 always
        returns 20 once
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockUnreachableEntry)),
            Is.True,
            "expected MockUnreachableEntry; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void ClearMock_SingleCommand_Parses()
    {
        var src = @"
test foo
    clear mock screen width
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "no parse errors expected; got: " + string.Join(", ", errs.Select(e => e.Display)));
        var clear = FindFirstClearMock(prog);
        Assert.That(clear, Is.Not.Null);
        Assert.That(clear.commandName, Is.EqualTo("screen width"));
    }

    [Test]
    public void ClearMocks_All_Parses()
    {
        var src = @"
test foo
    clear mocks
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty);
        var clear = FindFirstClearMock(prog);
        Assert.That(clear, Is.Not.Null);
        Assert.That(clear.commandName, Is.Null, "clear mocks should have null commandName (= clear all)");
    }

    [Test]
    public void Clear_WithoutMockOrMocks_Errors()
    {
        var src = @"
test foo
    clear something
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.ClearMockMissingTarget)),
            Is.True,
            "expected ClearMockMissingTarget; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_BlockForm_MixedReturnsAndForbid_Parses()
    {
        var src = @"
test foo
    mock screen width
        returns 10 once
        forbid always
    endmock
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "no parse errors expected; got: " + string.Join(", ", errs.Select(e => e.Display)));
        var mock = FindFirstMock(prog);
        Assert.That(mock.entries.Count, Is.EqualTo(2));
        Assert.That(mock.entries[0].kind, Is.EqualTo(MockEntryKind.Returns));
        Assert.That(mock.entries[1].kind, Is.EqualTo(MockEntryKind.Forbid));
    }

    [Test]
    public void Mock_StackedInline_StopsAtNewline_NoEndMockNeeded()
    {
        // Stacked inline form via colon — DEFER-style. No endmock required;
        // the statement ends at the first newline.
        var src = @"
test foo
    mock screen width returns 10 once: returns 20 once
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "no parse errors expected; got: " + string.Join(", ", errs.Select(e => e.Display)));
        var mock = FindFirstMock(prog);
        Assert.That(mock.entries.Count, Is.EqualTo(2));
        Assert.That(mock.entries[0].frequency, Is.EqualTo(MockFrequencyKind.Once));
        Assert.That(mock.entries[1].frequency, Is.EqualTo(MockFrequencyKind.Once));
    }

    [Test]
    public void Mock_BlockForm_MissingEndMock_DoesNotConsumeEndTest()
    {
        // When `endmock` is missing, the mock parser must NOT consume the
        // surrounding `endtest`; the missing-endmock error is reported and
        // the test parser still terminates correctly.
        var src = @"
test foo
    mock screen width
        returns 10 once
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockMissingEndMock)),
            Is.True,
            "expected MockMissingEndMock; got: " + string.Join(", ", errs.Select(e => e.Display)));
        // The test should still be properly closed (the test node exists with
        // the mock as its only top-level statement).
        Assert.That(prog.tests.Count, Is.EqualTo(1));
    }

    [Test]
    public void Mock_OutsideTest_Errors()
    {
        var src = @"
mock screen width returns 10
";
        var prog = Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockOutsideTest)),
            Is.True,
            "expected MockOutsideTest; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void ClearMock_OutsideTest_Errors()
    {
        var src = @"
clear mocks
";
        var prog = Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.ClearMockOutsideTest)),
            Is.True,
            "expected ClearMockOutsideTest; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Assert_OutsideTest_Errors()
    {
        var src = @"
assert 1 = 1
";
        var prog = Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.AssertOutsideTest)),
            Is.True,
            "expected AssertOutsideTest; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }
}
