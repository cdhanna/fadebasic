using System;
using System.IO;
using System.Linq;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Lib.Standard;
using FadeBasic.Virtual;
using NUnit.Framework;

namespace Tests;

// Runs the ACTUAL GMTK "killcode" project headless in the core VM, against
// no-op stubs of the Fade.Mono host commands (MonoStubs), to reproduce the
// reported heap corruption. The point is to exercise the real program's
// memory behaviour (arrays / strings / structs / GC) under an aggressive GC.
[TestFixture]
public class KillcodeReproTests
{
    // fade.json source order.
    static readonly string[] Order =
    {
        "entry", "snippets", "assets", "postits", "setups",
        "animatics", "main", "menu", "countdown", "animatics_Intro_BinSkull",
    };

    static string FixtureDir()
    {
        // walk up from the test bin dir to the Tests/Fixtures/killcode folder
        var dir = TestContext.CurrentContext.TestDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "Fixtures", "killcode", "code");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        // fallback: search the repo
        var root = TestContext.CurrentContext.TestDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "Tests")))
            root = Path.GetDirectoryName(root);
        return Path.Combine(root!, "Tests", "Fixtures", "killcode", "code");
    }

    static string JoinedSource()
    {
        var dir = FixtureDir();
        return string.Join("\n", Order.Select(n => File.ReadAllText(Path.Combine(dir, n + ".fbasic"))));
    }

    [TestCase(1, false)]
    [TestCase(64, false)]
    [TestCase(1, true)]   // paranoid: poison freed memory + never reuse it
    [TestCase(64, true)]
    public void Killcode_RunsHeadless(int sweep, bool paranoid)
    {
        var src = JoinedSource();
        var commands = new CommandCollection(new StandardCommands(), new ConsoleCommands(), new MonoStubs());

        var lexer = new Lexer();
        var tokens = lexer.Tokenize(src, commands);
        var parser = new Parser(new TokenStream(tokens), commands);
        var ast = parser.ParseProgram();
        var parseErrs = ast.GetAllErrors();
        TestContext.Out.WriteLine($"parse errors: {parseErrs.Count}");
        foreach (var e in parseErrs.Take(15)) TestContext.Out.WriteLine("  " + e.Display);
        Assert.That(parseErrs.Count, Is.EqualTo(0), "killcode did not parse against stubs");

        var compiler = new Compiler(commands, new CompilerOptions());
        compiler.Compile(ast);
        var prog = compiler.Program;

        var vm = new VirtualMachine(prog) { hostMethods = compiler.methodTable, sweepInterval = sweep };
        vm.heap.paranoid = paranoid;

        // Drive the game's do..loop in bounded batches. Watch for the reported
        // corruption: an INVALID_ADDRESS error, or a thrown VM exception.
        var maxBatches = 12000;
        VirtualRuntimeException thrown = null;
        var batches = 0;
        try
        {
            for (; batches < maxBatches; batches++)
            {
                if (vm.instructionIndex >= vm.program.Length) break;
                if (vm.error.type != VirtualRuntimeErrorType.NONE) break;
                vm.Execute2(2000);
            }
        }
        catch (VirtualRuntimeException e) { thrown = e; }

        var tag = $"sweep={sweep} paranoid={paranoid}";
        TestContext.Out.WriteLine($"[{tag}] batches={batches} ins={vm.instructionIndex}/{vm.program.Length} " +
                                  $"err={vm.error.type} allocs={vm.heap.Allocations}");
        if (thrown != null) TestContext.Out.WriteLine($"[{tag}] THREW: {thrown.Message}");
        if (vm.error.type != VirtualRuntimeErrorType.NONE)
            TestContext.Out.WriteLine($"[{tag}] VM ERROR: {vm.error.type} @ins={vm.error.insIndex} : {vm.error.message}");
    }
}
