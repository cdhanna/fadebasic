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
        exitmock 10
    endmock
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "no parse errors expected; got: " + string.Join(", ", errs.Select(e => e.Display)));
        var mock = FindFirstMock(prog);
        Assert.That(mock.body.Count, Is.EqualTo(1));
        Assert.That(mock.body[0], Is.TypeOf<MockExitMockStatement>());
        var rs = (MockExitMockStatement)mock.body[0];
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
        // Every mock requires its own `endmock`. Even when a body has a
        // single `exitmock <expr>` on the same line as the mock header,
        // the parser will still keep looking for `endmock` and eventually
        // run into the surrounding `endtest`, surfacing MockMissingEndMock.
        var src = @"
test foo
    mock screen width exitmock 10
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockMissingEndMock)),
            Is.True,
            "inline mock should still require endmock; got: " +
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
        exitmock 10
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
        exitmock 10
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
        exitmock 10
        exitmock 20
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
        exitmock 10
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
        exitmock 10
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
    exitmock 10
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
        exitmock 0
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
        exitmock ""nope""
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
        exitmock 5
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
    public void Mock_EndmockExprTypeMismatch_Errors()
    {
        // `screen width` returns int. `endmock ""3""` (string literal) is
        // not assignable to int — should error like `exitmock` does.
        var src = @"
test foo
    mock screen width
    endmock ""3""
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockReturnsTypeMismatch)),
            Is.True,
            "expected MockReturnsTypeMismatch on endmock string→int; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_ReturnsMatchingType_Ok()
    {
        // `screen width` returns int; `returns 42` should be fine.
        var src = @"
test foo
    mock screen width
        exitmock 42
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
    public void Mock_EmptyBodyOnVoidCommand_Ok()
    {
        // Empty mock body = suppress the call. Legal for void commands —
        // the caller doesn't read a return, so there's nothing to leak.
        var src = @"
test foo
    mock wait ms
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "empty mock body on void command should parse cleanly; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_EmptyBodyOnValueCommand_Errors()
    {
        // A value-returning command's mock body must contain `returns` or
        // `forbid`. An empty body would leave the caller's expected return
        // value missing on the stack — that's now a compile-time error.
        var src = @"
test foo
    mock screen width
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockValueCommandMissingReturns)),
            Is.True,
            "expected MockValueCommandMissingReturns; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_RefParamNotAssigned_Errors()
    {
        // A ref parameter must be assigned in the mock body — otherwise the
        // caller's variable is left undefined. `forbid` short-circuits the
        // check, but otherwise every ref param needs at least one top-level
        // assignment.
        var src = @"
test foo
    mock inc target, amount
        ` target (ref) never assigned
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockRefParamNotAssigned)),
            Is.True,
            "expected MockRefParamNotAssigned; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_RefParamAssigned_Ok()
    {
        // `inc` takes `(ref int variable, int amount = 1)`. Assigning to
        // `target` (the ref param) inside the body is the happy path and
        // should produce no validation errors.
        var src = @"
test foo
    mock inc target, amount
        target = 99
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockRefParamNotAssigned)),
            Is.False,
            "no ref errors expected; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_RefParamUnassigned_WithForbid_Ok()
    {
        // `forbid` halts the test before the caller observes any output,
        // so unassigned ref params are fine when forbid is present.
        var src = @"
test foo
    mock inc target, amount
        forbid ""nope""
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockRefParamNotAssigned)),
            Is.False,
            "forbid should suppress the ref-assignment requirement; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_RuntoInBody_Errors()
    {
        // `runto` is a test-control primitive and must not appear inside a
        // mock body. The body is mini-function bytecode run on dispatch,
        // not a test-navigation context.
        var src = @"
checkpoint:
end

test foo
    mock screen width
        runto checkpoint
    endmock 0
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.RuntoInsideMockBody)),
            Is.True,
            "expected RuntoInsideMockBody; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_RuntoNestedInBody_Errors()
    {
        // Even wrapped in an `if`, runto inside a mock body is illegal —
        // we walk the body tree recursively.
        var src = @"
checkpoint:
end

test foo
    mock screen width
        if 1 then runto checkpoint
    endmock 0
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.RuntoInsideMockBody)),
            Is.True,
            "expected RuntoInsideMockBody (nested in if); got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_ParamsInParens_ParseOk()
    {
        // `mock inc(target, amount)` should parse identically to the bare
        // `mock inc target, amount` form.
        var src = @"
test foo
    mock inc(target, amount)
        target = 99
    endmock
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "no parse errors expected; got: " + string.Join(", ", errs.Select(e => e.Display)));
        var mock = FindFirstMock(prog);
        Assert.That(mock.parameters.Count, Is.EqualTo(2));
        Assert.That(mock.parameters[0].variableName, Is.EqualTo("target"));
        Assert.That(mock.parameters[1].variableName, Is.EqualTo("amount"));
    }

    [Test]
    public void Mock_ParamsInParens_MissingClose_Errors()
    {
        var src = @"
test foo
    mock inc(target, amount
        target = 99
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockParamsMissingCloseParen)),
            Is.True,
            "expected MockParamsMissingCloseParen; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_ParamCountNoMatchingOverload_Errors()
    {
        // `inc` has one overload: `(ref int variable, int amount = 1)` — 2
        // args (the optional one still counts). A mock with 3 named params
        // matches no overload and should error.
        var src = @"
test foo
    mock inc(a, b, c)
        a = 1
    endmock
endtest
";
        Parse(src, out var errs);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.MockParamCountNoMatchingOverload)),
            Is.True,
            "expected MockParamCountNoMatchingOverload; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Mock_StringParamName_ParseOk()
    {
        // String-suffixed param names (`s$`) must be accepted as identifiers.
        // The actual type comes from the command metadata.
        var src = @"
test foo
    mock tuna_echo a, x$
        x$ = ""hello""
    endmock
endtest
";
        var prog = Parse(src, out var errs);
        Assert.That(errs, Is.Empty,
            "no parse errors expected; got: " + string.Join(", ", errs.Select(e => e.Display)));
        var mock = FindFirstMock(prog);
        Assert.That(mock.parameters.Count, Is.EqualTo(2));
        Assert.That(mock.parameters[1].variableName.ToLowerInvariant(), Does.Contain("x"));
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
