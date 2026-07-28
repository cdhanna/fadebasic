using System.Linq;
using System.Text;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Lib.Standard;
using FadeBasic.Virtual;
using NUnit.Framework;

namespace Tests;

// Regression for the killcode "cannot add pointer buckets" crash: an array
// declared after >256 registers had its POINTER register address truncated to a
// byte (CreateArray used (byte)(registerCount++)), wrapping mod 256 onto an
// early scalar. Writing that scalar clobbered the array's base pointer with a
// small int, so the next index did pointer-add on {bucket=1,mem=0} and threw.
// len() still worked because it reads a separate size register.
[TestFixture]
public class RegisterWrapRepro
{
    [Test]
    public void ArrayPointerRegister_NotTruncated_Above256Registers()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 300; i++) sb.AppendLine($"v{i} = {100 + i}");
        sb.AppendLine("dim arr(8)");
        sb.AppendLine("arr(0) = 777");
        // clobber every early scalar — one of them is whatever low register the
        // array pointer would have wrapped onto if truncated.
        for (var i = 0; i < 300; i++) sb.AppendLine($"v{i} = 1");
        sb.AppendLine("global result0");
        sb.AppendLine("result0 = arr(0)");

        var commands = new CommandCollection(new StandardCommands(), new ConsoleCommands());
        var lexer = new Lexer();
        var tokens = lexer.Tokenize(sb.ToString(), commands);
        var parser = new Parser(new TokenStream(tokens), commands);
        var ast = parser.ParseProgram();
        Assert.That(ast.GetAllErrors().Count, Is.EqualTo(0));
        var compiler = new Compiler(commands, new CompilerOptions());
        compiler.Compile(ast);
        var vm = new VirtualMachine(compiler.Program) { hostMethods = compiler.methodTable };
        string err = null;
        try { vm.Execute2(0); } catch (System.Exception e) { err = e.Message; }

        // The array pointer register must not have been clobbered → no pointer-bucket throw.
        Assert.That(err, Is.Null, "register truncation/aliasing: " + err);
    }
}
