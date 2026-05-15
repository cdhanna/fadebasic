using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace Tests;

[TestFixture]
public class RuntoCompilerTests
{
    private Compiler Compile(string src, out ProgramNode prog)
    {
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        lex.AssertNoLexErrors();
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        prog = parser.ParseProgram(new ParseOptions { ignoreChecks = true });
        prog.AssertNoParseErrors();
        var compiler = new Compiler(TestCommands.CommandsForTesting, new CompilerOptions());
        compiler.Compile(prog);
        return compiler;
    }

    [Test]
    public void Runto_Statement_Parses()
    {
        var src = @"
test foo
    runto someLabel
endtest
";
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram(new ParseOptions { ignoreChecks = true });
        prog.AssertNoParseErrors();
        Assert.That(prog.tests.Count, Is.EqualTo(1));
        Assert.That(prog.tests[0].testProgram.statements.Count, Is.EqualTo(1));
        var rt = prog.tests[0].testProgram.statements[0] as RuntoStatement;
        Assert.That(rt, Is.Not.Null);
        Assert.That(rt.targetLabel, Is.EqualTo("somelabel"));
    }

    [Test]
    public void Runto_BlockForm_WithMaxCycles_Parses()
    {
        var src = @"
test foo
    runto someLabel
        max cycles 1000
    endrunto
endtest
";
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram(new ParseOptions { ignoreChecks = true });
        prog.AssertNoParseErrors();
        var rt = prog.tests[0].testProgram.statements[0] as RuntoStatement;
        Assert.That(rt, Is.Not.Null);
        Assert.That(rt.targetLabel, Is.EqualTo("somelabel"));
        Assert.That(rt.maxCyclesExpression, Is.Not.Null);
    }

    [Test]
    public void Runto_MissingLabel_Errors()
    {
        var src = @"
test foo
    runto
endtest
";
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram(new ParseOptions { ignoreChecks = true });
        var errs = prog.GetAllErrors();
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.RuntoMissingLabel)), Is.True);
    }

    [Test]
    public void Compiler_TestEntryPointsRecorded()
    {
        var src = @"
test alpha
endtest

test beta
endtest
";
        var compiler = Compile(src, out _);
        Assert.That(compiler.TestManifest.Count, Is.EqualTo(2));
        Assert.That(compiler.TestManifest[0].name, Is.EqualTo("alpha"));
        Assert.That(compiler.TestManifest[1].name, Is.EqualTo("beta"));
        // Different tests have distinct entry points.
        Assert.That(compiler.TestManifest[0].entryPointAddress,
            Is.Not.EqualTo(compiler.TestManifest[1].entryPointAddress));
    }

    [Test]
    public void Compiler_AbstractTestRecorded()
    {
        var src = @"
abstract test root
endtest

test child from root
endtest
";
        var compiler = Compile(src, out _);
        Assert.That(compiler.TestManifest.Count, Is.EqualTo(2));
        Assert.That(compiler.TestManifest[0].name, Is.EqualTo("root"));
        Assert.That(compiler.TestManifest[0].isAbstract, Is.True);
        Assert.That(compiler.TestManifest[1].name, Is.EqualTo("child"));
        Assert.That(compiler.TestManifest[1].isAbstract, Is.False);
        Assert.That(compiler.TestManifest[1].fromParent, Is.EqualTo("root"));
    }

    [Test]
    public void Compiler_RuntoTargetLabel_EmitsYield()
    {
        // A label referenced by runto should have RUNTO_YIELD emitted right after
        // its NOOP. We verify by inspecting the bytecode.
        var src = @"
mylabel:
end

test foo
    runto mylabel
endtest
";
        var compiler = Compile(src, out var prog);
        var program = compiler.Program.ToArray();

        // Find the NOOP for 'mylabel' — search the bytecode for OpCodes.NOOP
        // followed by OpCodes.RUNTO_YIELD. There should be exactly one such pair.
        var pairs = 0;
        for (var i = 0; i < program.Length - 1; i++)
        {
            if (program[i] == OpCodes.NOOP && program[i + 1] == OpCodes.RUNTO_YIELD)
            {
                pairs++;
            }
        }
        Assert.That(pairs, Is.GreaterThanOrEqualTo(1),
            "expected at least one NOOP+RUNTO_YIELD pair for the runto target label");
    }

    [Test]
    public void Compiler_NonRuntoLabel_NoYield()
    {
        // Without any runto referencing it, the label should NOT have a RUNTO_YIELD
        // following its NOOP.
        var src = @"
mylabel:
end
";
        var compiler = Compile(src, out var prog);
        var program = compiler.Program.ToArray();

        for (var i = 0; i < program.Length - 1; i++)
        {
            if (program[i] == OpCodes.NOOP && program[i + 1] == OpCodes.RUNTO_YIELD)
            {
                Assert.Fail("found unexpected NOOP+RUNTO_YIELD pair (no test references this label)");
            }
        }
    }

    [Test]
    public void Compiler_RunBuild_NoTests_NoYieldOpcodes()
    {
        // Programs with no tests should never emit RUNTO_YIELD opcodes anywhere.
        // Zero production cost in `dotnet run` builds.
        var src = @"
mylabel:
goto mylabel
end
";
        var compiler = Compile(src, out _);
        var program = compiler.Program.ToArray();

        // Need to bound the search to just the code section (before interned data),
        // otherwise we'd find raw byte 65 inside the JSON tail.
        var internedStart = System.BitConverter.ToInt32(program, 0);
        for (var i = 0; i < internedStart; i++)
        {
            Assert.That(program[i], Is.Not.EqualTo(OpCodes.RUNTO_YIELD),
                "no RUNTO_YIELD should be emitted when no tests reference labels");
        }
    }

    [Test]
    public void Runto_SingleLine_WithMaxCycles_NoEndRuntoNeeded()
    {
        // DEFER-style single-line form: `runto label max cycles N` on one line,
        // no endrunto required.
        var src = @"
mylabel:
end

test foo
    runto mylabel max cycles 1000
endtest
";
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram(new ParseOptions { ignoreChecks = true });
        prog.AssertNoParseErrors();
        var rt = prog.tests[0].testProgram.statements[0] as RuntoStatement;
        Assert.That(rt, Is.Not.Null);
        Assert.That(rt.targetLabel, Is.EqualTo("mylabel"));
        Assert.That(rt.maxCyclesExpression, Is.Not.Null);
    }

    [Test]
    public void Runto_OutsideTest_Errors()
    {
        var src = @"
mylabel:
end

runto mylabel
";
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        var errs = prog.GetAllErrors();
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.RuntoOutsideTest)),
            Is.True,
            "expected RuntoOutsideTest; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Runto_MaxCycles_IsRealKeyword_NotSoftStringMatch()
    {
        // `max` and `cycles` should NOT be treated as bare identifiers; the
        // lexer recognizes `max cycles` as a single multi-word keyword token.
        var src = @"
mylabel:
end

test foo
    runto mylabel
        max cycles 1000
    endrunto
endtest
";
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        lex.AssertNoLexErrors();
        // A `KeywordMaxCycles` token should appear among the lexed tokens.
        Assert.That(lex.tokens.Any(t => t.type == LexemType.KeywordMaxCycles),
            Is.True,
            "expected the lexer to emit a KeywordMaxCycles token");
    }

    [Test]
    public void Compiler_RuntoStatement_EmitsRuntoOpCode()
    {
        var src = @"
mylabel:
end

test foo
    runto mylabel
endtest
";
        var compiler = Compile(src, out _);
        var program = compiler.Program.ToArray();

        // Look for at least one RUNTO opcode in the test region.
        var found = false;
        var internedStart = System.BitConverter.ToInt32(program, 0);
        for (var i = 0; i < internedStart; i++)
        {
            if (program[i] == OpCodes.RUNTO) { found = true; break; }
        }
        Assert.That(found, Is.True, "expected a RUNTO opcode in the compiled output");
    }
}
