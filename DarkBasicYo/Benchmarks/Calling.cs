using BenchmarkDotNet.Attributes;
using DarkBasicYo;
using DarkBasicYo.Virtual;

namespace Benchmarks;

[MemoryDiagnoser()]
public class Calling
{
    (byte[], HostMethodTable) Compile(string src)
    {
        var lexer = new Lexer();
        var tokens = lexer.Tokenize(src, StandardCommands.LimitedCommands);

        Console.WriteLine("----- COMMANDS");
        foreach (var x in StandardCommands.LimitedCommands.Commands)
        {
            Console.WriteLine("COMMAND : " + x.name);
        }
        
        var parser = new Parser(new TokenStream(tokens), StandardCommands.LimitedCommands);
        var exprAst = parser.ParseProgram();
        
        var compiler = new Compiler(StandardCommands.LimitedCommands);
        
        compiler.Compile(exprAst);
        
        return (compiler.Program.ToArray(), compiler.methodTable);
    }

    private (byte[], HostMethodTable) _incProg;
    private (byte[], HostMethodTable) _baseline;
    [GlobalSetup]
    public void Setup()
    {
        var incSrc = @"
x = 1
standard double x
";
        
        _incProg = Compile(incSrc);
        
        var baseSrc = @"
x = 1
y = 2
z = 3
";
        
        _baseline = Compile(baseSrc);
    }
    [Benchmark]
    public void ReflectionV1()
    {
        var vm = new VirtualMachine(_incProg.Item1);
        vm.hostMethods = _incProg.Item2;
        // vm.commands = StandardCommands.LimitedCommands;
        vm.Execute2();
    }

    [Benchmark()]
    public void Baseline()
    {
        var vm = new VirtualMachine(_baseline.Item1);
        vm.hostMethods = _baseline.Item2;
        // vm.commands = StandardCommands.LimitedCommands;
        vm.Execute2();
    }
}