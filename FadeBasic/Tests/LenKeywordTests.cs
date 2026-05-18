using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Sdk;
using FadeBasic.Virtual;

namespace Tests;

[TestFixture]
public class LenKeywordTests
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

    private bool TryFindGlobalValue(VirtualMachine vm, ulong value)
    {
        for (var i = 0; i < vm.globalScope.dataRegisters.Length; i++)
        {
            if (vm.globalScope.dataRegisters[i] == value) return true;
        }
        return false;
    }

    [Test]
    public void Len_IntArray_ReturnsElementCount()
    {
        var src = @"
dim xs(3)
n = len(xs)
";
        var vm = RunMain(src);
        Assert.That(TryFindGlobalValue(vm, 3), Is.True, "expected len(xs) = 3");
    }

    [Test]
    public void Len_String_ReturnsCharCount()
    {
        var src = @"
n = len(""hello"")
";
        var vm = RunMain(src);
        Assert.That(TryFindGlobalValue(vm, 5), Is.True, "expected len(\"hello\") = 5");
    }

    [Test]
    public void Len_EmptyString_ReturnsZero()
    {
        var src = @"
n = len("""")
";
        var (compiler, program) = Compile(src);
        var vm = new VirtualMachine(program) { hostMethods = compiler.methodTable };
        vm.Execute3();

        Assert.That(vm.error.type, Is.EqualTo(VirtualRuntimeErrorType.NONE));
        Assert.That(compiler.globalScope.TryGetVariable("n", out var nVar), Is.True,
            "compiler should have allocated a register for `n`");
        Assert.That(vm.globalScope.dataRegisters[nVar.registerAddress], Is.EqualTo(0UL),
            "expected len(\"\") to write 0 into n's register");
    }

    [Test]
    public void Len_InAssignmentToLong_Works()
    {
        var src = @"
dim xs(7)
m as long = len(xs)
";
        var vm = RunMain(src);
        Assert.That(TryFindGlobalValue(vm, 7), Is.True, "expected len(xs) = 7 stored as long");
    }

    [Test]
    public void Len_OnNonArrayNonString_Errors()
    {
        // `len` on an int variable should error at validation time.
        var src = @"
x = 5
n = len(x)
";
        var errs = ParseErrors(src);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.LenInvalidType)),
            Is.True,
            "expected LenInvalidType; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }

    [Test]
    public void Len_MissingParens_Errors()
    {
        // `len xs` (no parens) should fail to parse.
        var src = @"
dim xs(3)
n = len xs
";
        var errs = ParseErrors(src);
        Assert.That(errs.Any(e => e.errorCode.Equals(ErrorCodes.LenMissingParens)),
            Is.True,
            "expected LenMissingParens; got: " + string.Join(", ", errs.Select(e => e.Display)));
    }
}
