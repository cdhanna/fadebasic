using System.Diagnostics;
using FadeBasic.Virtual;

namespace Tests.CSharpBackend;

/// <summary>
/// Rough timings of the VM against the C# backend on CPU-bound Fade programs.
/// Explicit: run with --filter "FullyQualifiedName~CSharpBackendPerf".
/// </summary>
[TestFixture, Explicit("benchmark")]
public class CSharpBackendPerf
{
    static readonly (string name, string src)[] Programs =
    {
        ("loops_int", @"
total = 0
for i = 1 to 3000000
  total = total + (i mod 7) * 3
next
static print total"),
        ("float_math", @"
x# = 0
for i = 1 to 1000000
  x# = x# + (i * 0.5) / 3.0 - x# * 0.001
next
static print x#"),
        ("function_calls", @"
static print fib(24)
function fib(n)
  if n < 2 then exitfunction n
endfunction fib(n - 1) + fib(n - 2)"),
        ("arrays_and_types", @"
type cell
  kind
  score#
endtype
dim cells(1000) as cell
for pass = 1 to 300
  for i = 0 to 999
    cells(i).kind = (cells(i).kind + i + pass) mod 13
    cells(i).score# = cells(i).score# * 0.5 + cells(i).kind
  next
next
static print cells(500).kind, cells(500).score#"),
        ("strings", @"
s$ = """"
for i = 1 to 20000
  s$ = s$ + ""x""
  if len(s$) > 50 then s$ = """"
next
static print len(s$)"),
    };

    [Test]
    public void VmVersusCompiled()
    {
        var cases = Programs.Select(p => new CSharpBackendHarness.Case { Name = p.name, Source = p.src, VmBudget = long.MaxValue }).ToList();
        foreach (var c in cases) CSharpBackendHarness.Prepare(c);
        CSharpBackendHarness.CompileAll(cases);

        TestContext.WriteLine($"{"program",-18} {"vm ms",10} {"c# ms",10} {"speedup",8}");
        foreach (var c in cases)
        {
            Assert.That(c.SkipReason, Is.Null, c.Name);
            Assert.That(c.TranspileError, Is.Null, c.Name);
            Assert.That(c.CompiledType, Is.Not.Null, c.CSharpCompileError);

            var vmMs = Best(() =>
            {
                var ast = CSharpBackendHarness.Parse(c.Source, c.Commands, out _)!;
                var compiler = new Compiler(c.Commands);
                compiler.Compile(ast);
                var vm = new VirtualMachine(compiler.Program) { hostMethods = compiler.methodTable };
                var sw = Stopwatch.StartNew();
                vm.Execute2(0);
                return sw.Elapsed.TotalMilliseconds;
            });
            var csMs = Best(() =>
            {
                var program = (FadeBasic.Compiled.CompiledFadeProgram)Activator.CreateInstance(c.CompiledType!)!;
                var sw = Stopwatch.StartNew();
                program.RunToCompletion();
                return sw.Elapsed.TotalMilliseconds;
            });

            var vmRun = c.Vm!;
            var csRun = CSharpBackendHarness.RunCompiled(c.CompiledType!);
            Assert.That(csRun.Output, Is.EqualTo(vmRun.Output), c.Name);

            TestContext.WriteLine($"{c.Name,-18} {vmMs,10:F1} {csMs,10:F2} {vmMs / csMs,7:F0}x");
        }
    }

    static double Best(Func<double> run)
    {
        var best = double.MaxValue;
        for (var i = 0; i < 5; i++)
        {
            TestCommands.staticPrintBuffer.Clear();
            best = Math.Min(best, run());
        }
        return best;
    }
}
