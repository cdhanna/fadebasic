using BenchmarkDotNet.Attributes;
using DarkBasicYo;
using DarkBasicYo.Virtual;
using MoonSharp.Interpreter;

namespace Benchmarks;

[MemoryDiagnoser]
public class Vms
{
    private List<byte> _compilerProgram;
    private VirtualMachine _vm;

    private Script _lua;

    // [Params(
    //     // "3 + 2", 
    //     // "(1 + 2 * 4) * (5+2+1) * 2",
    //     ""
    // )]
    public string Source { get; set; } =
        "dim x(4);x(0) = 2;x(1) = x(0) * 2;x(2) = x(1) * x(0);x(3) = x(2) * x(1) * x(0);y = x(3)";
    
    [GlobalSetup]
    public void Setup()
    {
        // var src = Source;
        // var lexer = new Lexer();
        // var tokens = lexer.Tokenize(src);
        // var parser = new Parser(new TokenStream(tokens), StandardCommands.LimitedCommands);
        // var exprAst = parser.ParseProgram();
        //
        // var compiler = new Compiler(StandardCommands.LimitedCommands);
        // compiler.Compile(exprAst);
        // _compilerProgram = compiler.Program;
        // _vm = new VirtualMachine(_compilerProgram);
        //
        Script.WarmUp();
        _lua = new Script();
    }
//     
//     [Benchmark()]
//     public void Dbp()
//     {
//         var src = Source;
//         var lexer = new Lexer();
//         var tokens = lexer.Tokenize(src);
//         var parser = new Parser(new TokenStream(tokens), StandardCommands.LimitedCommands);
//         var exprAst = parser.ParseProgram();
//
//         var compiler = new Compiler(StandardCommands.LimitedCommands);
//         compiler.Compile(exprAst);
//         var _compilerProgram = compiler.Program;
//         var _vm = new VirtualMachine(_compilerProgram);
//         _vm.Execute2();
//
//     }
//
//     [Benchmark()]
//     public void Dbp_Cached()
//     {
//         _vm.Execute2();
//     }
//     
//     [Benchmark()]
//     public void Lua()
//     {
//         Script.RunString(@"
// x = { 2, 1, 3, 4}
// y = x[1] * x[2]
// ");
//     }


//     [Benchmark()]
//     public void Lua_Cached()
//     {
//         _lua.DoString(@"
// x = { 2, 1, 3, 4}
// y = x[1] * x[2]
// ");
//     }
//     
}