using BenchmarkDotNet.Attributes;
using DarkBasicYo;
using DarkBasicYo.Virtual;

namespace Benchmarks;

[MemoryDiagnoser]
public class Vms
{
    private List<byte> _compilerProgram;
    private VirtualMachine _vm;

    [Params("3 + 2", "(1 + 2 * 4) * (5+2+1) * 2")]
    public string Source { get; set; }
    
    [GlobalSetup]
    public void Setup()
    {
        var src = Source;
        var lexer = new Lexer();
        var tokens = lexer.Tokenize(src);
        var parser = new Parser(new TokenStream(tokens), StandardCommands.LimitedCommands);
        var exprAst = parser.ParseWikiExpression();

        var compiler = new Compiler();
        compiler.Compile(exprAst);
        _compilerProgram = compiler.Program;
        _vm = new VirtualMachine(_compilerProgram);
    }

    [Benchmark()]
    public void Execute()
    {
        _vm.Execute2();
    }
    
    [Benchmark()]
    public void ExecuteSplit()
    {
        for (var i = 0; i < 15; i++)
        {
            _vm.Execute2(1);
        }
    }
    
    [Benchmark()]
    public void ExecuteSplit2()
    {
        for (var i = 0; i < 5; i++)
        {
            _vm.Execute2(3);
        }
    }
    
}