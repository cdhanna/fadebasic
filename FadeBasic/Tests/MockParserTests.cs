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
            foreach (var stmt in t.testProgram.statements)
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
            foreach (var stmt in t.testProgram.statements)
            {
                if (stmt is ClearMockStatement c) return c;
            }
        }
        return null;
    }

    // ── Block-form shape ───────────────────────────────────────────────────

    [Test]
    public void Mock_Empty_Body_ParsesAsVoidMock()
    {
        // Empty block = suppress the call. No inline form: `endmock` is
        // required even for void mocks.
        var src = @"
test foo
    mock wait ms
    endmock
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "no parse errors expected; got: " + string.Join(", ", errs.Select(e => e.Display)));
        var mock = FindFirstMock(prog);
        Assert.That(mock, Is.Not.Null);
        Assert.That(mock.commandName, Is.EqualTo("wait ms"));
        Assert.That(mock.body, Is.Empty);
    }

    [Test]
    public void Mock_Returns_ParsesAsReturnsStatement()
    {
        var src = @"
test foo
    mock screen width
        returns 10
    endmock
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "no parse errors expected; got: " + string.Join(", ", errs.Select(e => e.Display)));
        var mock = FindFirstMock(prog);
        Assert.That(mock.body.Count, Is.EqualTo(1));
        Assert.That(mock.body[0], Is.TypeOf<MockReturnsStatement>());
        var rs = (MockReturnsStatement)mock.body[0];
        Assert.That(rs.expression, Is.Not.Null);
    }

    [Test]
    public void Mock_Forbid_ParsesAsForbidStatement()
    {
        var src = @"
test foo
    mock screen width
        forbid
    endmock
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty);
        var mock = FindFirstMock(prog);
        Assert.That(mock.body.Count, Is.EqualTo(1));
        Assert.That(mock.body[0], Is.TypeOf<MockForbidStatement>());
        var fs = (MockForbidStatement)mock.body[0];
        Assert.That(fs.reason, Is.Null);
    }

    [Test]
    public void Mock_ForbidWithReason_ParsesReason()
    {
        var src = @"
test foo
    mock wait ms
        forbid ""no waiting in tests""
    endmock
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "no parse errors expected; got: " + string.Join(", ", errs.Select(e => e.Display)));
        var mock = FindFirstMock(prog);
        var fs = (MockForbidStatement)mock.body[0];
        Assert.That(fs.reason, Is.Not.Null);
    }

    // ── Error paths ────────────────────────────────────────────────────────

    [Test]
    public void Mock_InlineForm_NoLongerSupported_Errors()
    {
        // `mock cmd returns X` on one line is no longer valid — the parser
        // expects a newline and `endmock`. The `returns 10` token sequence
        // now sits in an empty mock body, awaiting `endmock`; eventually
        // the surrounding `endtest` is hit and MockMissingEndMock fires.
        var src = @"
test foo
    mock screen width returns 10
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockMissingEndMock)),
            Is.True,
            "inline mock should now require endmock; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_BareForm_NoLongerSupported_Errors()
    {
        // `mock cmd` with no `endmock` used to install a void mock. Now
        // every mock requires `endmock`.
        var src = @"
test foo
    mock wait ms
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockMissingEndMock)),
            Is.True,
            "bare mock should now require endmock; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_MissingEndMock_Errors()
    {
        var src = @"
test foo
    mock screen width
        returns 10
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
    mock not_a_real_command
        returns 10
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockMissingCommandName)),
            Is.True,
            "expected MockMissingCommandName; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_MultipleReturns_Errors()
    {
        // A body may have at most one `returns`. (Frequency is gone, so the
        // old "stacked returns with different frequencies" use case is too.)
        var src = @"
test foo
    mock screen width
        returns 10
        returns 20
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockMultipleReturns)),
            Is.True,
            "expected MockMultipleReturns; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_MultipleForbid_Errors()
    {
        var src = @"
test foo
    mock screen width
        forbid
        forbid
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockMultipleForbid)),
            Is.True,
            "expected MockMultipleForbid; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_ReturnsAndForbid_Errors()
    {
        // `returns` + `forbid` together is nonsensical: forbid prevents the
        // return path from ever running.
        var src = @"
test foo
    mock screen width
        returns 10
        forbid
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockReturnsAndForbid)),
            Is.True,
            "expected MockReturnsAndForbid; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_ForbidReasonMustBeString()
    {
        var src = @"
test foo
    mock wait ms
        forbid 42
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockForbidReasonMustBeString)),
            Is.True,
            "expected MockForbidReasonMustBeString; got: " + string.Join(", ", errs.Select(e => e.Display)));
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
        returns 10
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockMissingEndMock)),
            Is.True,
            "expected MockMissingEndMock; got: " + string.Join(", ", errs.Select(e => e.Display)));
        // The test should still be properly closed.
        Assert.That(prog.tests.Count, Is.EqualTo(1));
    }

    // ── ClearMock ──────────────────────────────────────────────────────────

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

    // ── Scope enforcement ──────────────────────────────────────────────────

    [Test]
    public void Mock_OutsideTest_Errors()
    {
        var src = @"
mock screen width
    returns 10
endmock
";
        Parse(src, out var errs);
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
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.ClearMockOutsideTest)),
            Is.True,
            "expected ClearMockOutsideTest; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    // ── Type validation (Phase C) ──────────────────────────────────────────

    [Test]
    public void Mock_ReturnsOnVoidCommand_Errors()
    {
        // `wait ms` is void — `returns 0` against it must error rather than
        // silently degrade (the old behavior).
        var src = @"
test foo
    mock wait ms
        returns 0
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockReturnsOnVoidCommand)),
            Is.True,
            "expected MockReturnsOnVoidCommand; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_ReturnsTypeMismatch_Errors()
    {
        // `screen width` returns an int — returning a string should error.
        var src = @"
test foo
    mock screen width
        returns ""nope""
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockReturnsTypeMismatch)),
            Is.True,
            "expected MockReturnsTypeMismatch; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_ReturnsNumericCoercion_Ok()
    {
        // `now` returns a long. `returns 5` (int literal) should coerce
        // cleanly — same rule that lets `local n as long = 5` work. We use
        // EnforceTypeAssignment so the coercion semantics stay consistent
        // with the rest of the language.
        var src = @"
test foo
    mock now
        returns 5
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockReturnsTypeMismatch)),
            Is.False,
            "int → long coercion should be allowed in mock returns; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_ReturnsMatchingType_Ok()
    {
        // `screen width` returns int; `returns 42` should be fine.
        var src = @"
test foo
    mock screen width
        returns 42
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e =>
                e.errorCode.Equals(ErrorCodes.MockReturnsOnVoidCommand)
             || e.errorCode.Equals(ErrorCodes.MockReturnsTypeMismatch)),
            Is.False,
            "no type errors expected; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_EmptyBodyOnAnyCommand_Ok()
    {
        // Empty mock body = suppress the call. Always valid regardless of
        // whether the command returns a value (the caller of a value-
        // returning command gets a stack-leak if it reads the return — but
        // that's a separate runtime concern; the parser accepts it).
        var src = @"
test foo
    mock screen width
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "empty mock body should parse cleanly; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Assert_OutsideTest_IsAllowed()
    {
        // Unrelated to mock but lives in this fixture historically.
        // `assert` is legal in the main program; the VM crashes at runtime
        // when one fails outside a test.
        var src = @"
assert 1 = 1
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.AssertOutsideTest)),
            Is.False,
            "AssertOutsideTest should no longer be raised; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }
}
