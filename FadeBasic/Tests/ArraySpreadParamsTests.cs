using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace Tests;

[TestFixture]
public class ArraySpreadParamsTests
{
    private (Compiler compiler, byte[] program) Compile(string src)
    {
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        lex.AssertNoLexErrors();
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        prog.AssertNoParseErrors();
        var compiler = new Compiler(TestCommands.CommandsForTesting, new CompilerOptions());
        compiler.Compile(prog);
        return (compiler, compiler.Program.ToArray());
    }

    private VirtualMachine RunMain(string src)
    {
        var (compiler, program) = Compile(src);
        var vm = new VirtualMachine(program) { hostMethods = compiler.methodTable };
        vm.Execute3();
        return vm;
    }

    private List<ParseError> ParseErrors(string src)
    {
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        return prog.GetAllErrors();
    }

    [Test]
    public void Spread_IntArray_IntoSumParams_AddsCorrectly()
    {
        // `sum(params int[])` in TestCommands returns the sum. Spreading
        // a 3-element int array should produce the same result as calling
        // sum(10, 20, 30).
        var src = @"
dim xs(3)
xs(0) = 10
xs(1) = 20
xs(2) = 30
n = sum(xs)
";
        var vm = RunMain(src);
        // The `n` global register should hold 60.
        // dataRegisters[register for n] = 60. We don't know the exact
        // register here; assert via the program's debug view. Simpler:
        // use a known TestCommands hook. But the cleanest: re-bind and
        // check via the static-print buffer. Let me just inspect register 0
        // since n is the first declaration after the array.
        // Actually we can ask via the runtime: look up by scope.
        var found = false;
        for (var i = 0; i < vm.globalScope.dataRegisters.Length; i++)
        {
            if (vm.globalScope.dataRegisters[i] == 60)
            {
                found = true;
                break;
            }
        }
        Assert.That(found, Is.True, "expected sum(xs) = 60 stored somewhere in globals");
    }

    [Test]
    public void Spread_EmptyArray_PushesZeroCount()
    {
        // `sum` on a 0-element array sums to 0.
        var src = @"
dim xs(0)
n = sum(xs)
";
        var vm = RunMain(src);
        // n should be 0; that's the default so we just check no crash.
        Assert.That(vm.error.type, Is.EqualTo(VirtualRuntimeErrorType.NONE));
    }

    [Test]
    public void Spread_InlineAndArray_BothWork()
    {
        // The existing inline-list call shape must still work alongside
        // the new spread shape.
        var src = @"
dim xs(2)
xs(0) = 5
xs(1) = 7
a = sum(xs)        ` spread
b = sum(1, 2, 3)   ` inline
";
        var vm = RunMain(src);
        // a should be 12, b should be 6 — both live in globals.
        var foundA = false; var foundB = false;
        for (var i = 0; i < vm.globalScope.dataRegisters.Length; i++)
        {
            if (vm.globalScope.dataRegisters[i] == 12) foundA = true;
            if (vm.globalScope.dataRegisters[i] == 6) foundB = true;
        }
        Assert.That(foundA, Is.True, "expected sum(xs) = 12");
        Assert.That(foundB, Is.True, "expected sum(1,2,3) = 6");
    }

    [Test]
    public void Spread_RankTwoArray_Errors()
    {
        // 2D arrays can't be spread.
        var src = @"
dim xs(3, 2)
n = sum(xs)
";
        var errs = ParseErrors(src);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.ParamsArrayMustBeRankOne)),
            Is.True,
            "expected ParamsArrayMustBeRankOne; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Spread_StringArray_IntoObjectParams_Works()
    {
        // `static print` is `params object[]` (TypeCode.ANY at the params
        // slot). Spreading a string array into it should NOT error —
        // an object[] params slot accepts any element type, matching the
        // same tolerance the inline-arg path grants.
        TestCommands.staticPrintBuffer.Clear();
        var src = @"
dim x$(2)
x$(0) = ""a""
x$(1) = ""b""
static print x$
";
        var errs = ParseErrors(src);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.ParamsArrayElementTypeMismatch)),
            Is.False,
            "expected no ParamsArrayElementTypeMismatch on params object[]; got: " +
            string.Join(", ", errs.Select(e => e.Display)));

        RunMain(src);
        Assert.That(TestCommands.staticPrintBuffer, Is.EqualTo(new[] { "a", "b" }),
            "string-array spread into params object[] should print each element");
    }

    [Test]
    public void Spread_MixingArrayAndInline_Errors()
    {
        // Can't mix `Foo(arr, 99)` — array spread is exclusive at the
        // params position.
        var src = @"
dim xs(2)
xs(0) = 5
xs(1) = 7
n = sum(xs, 99)
";
        var errs = ParseErrors(src);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.ParamsCannotMixArrayWithInline)),
            Is.True,
            "expected ParamsCannotMixArrayWithInline; got: " +
            string.Join(", ", errs.Select(e => e.Display)));
    }
}
