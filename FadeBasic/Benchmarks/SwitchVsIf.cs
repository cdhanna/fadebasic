using BenchmarkDotNet.Attributes;
using FadeBasic;
using FadeBasic.Virtual;

namespace Benchmarks;

[MemoryDiagnoser]
public class SwitchVsIf
{
    private List<byte> _switchProgram;
    private List<byte> _ifProgram;
    private List<byte> _ifBinProgram;

    List<byte> Compile(string src)
    {
        var lexer = new Lexer();
        var tokens = lexer.Tokenize(src);
        var parser = new Parser(new TokenStream(tokens), StandardCommands.LimitedCommands);
        var exprAst = parser.ParseProgram();
        
        var compiler = new Compiler(StandardCommands.LimitedCommands);
        compiler.Compile(exprAst);
        return compiler.Program;
    }
    
    [GlobalSetup]
    public void Setup()
    {
        var switchSrc = @"
x = 5
y = 0
SELECT x
    CASE 1 
        y = 1
    ENDCASE
    CASE 2
        y = 2
    ENDCASE
    CASE 3
        y = 3
    ENDCASE
    CASE 4
        y = 4
    ENDCASE
    CASE 5
        y = 5
    ENDCASE
    CASE 6
        y = 6
    ENDCASE
    CASE 7
        y = 7
    ENDCASE
    CASE 8
        y = 8
    ENDCASE
ENDSELECT
";

        var ifSrc = @"
x = 5
y = 0
IF x = 1 THEN y = 1
IF x = 2 THEN y = 2
IF x = 3 THEN y = 3
IF x = 4 THEN y = 4
IF x = 5 THEN y = 5
IF x = 6 THEN y = 6
IF x = 7 THEN y = 7
IF x = 8 THEN y = 8
";

        var ifBinSrc = @"
x = 5
y = 0
IF X < 4
    IF X < 2
        Y = 1
    ELSE
        IF X = 2
            Y = 2
        ELSE
            Y = 3
        ENDIF
    ENDIF
ELSE
    IF X = 4
        y = 4
    ELSE
        IF X < 7
            IF X = 5
                y = 5
            ELSE
                y = 6
            ENDIF
        ELSE
            IF X = 7
                y = 7
            ELSE
                y = 8
            ENDIF
        ENDIF
    ENDIF
ENDIF
";

        _switchProgram = Compile(switchSrc);
        _ifProgram = Compile(ifSrc);
        _ifBinProgram = Compile(ifBinSrc);
    }

    [Benchmark]
    public void If()
    {
        var vm = new VirtualMachine(_ifProgram);
        vm.Execute2();
    }
    
    
    [Benchmark]
    public void IfBinary()
    {
        var vm = new VirtualMachine(_ifBinProgram);
        vm.Execute2();
    }

    
    [Benchmark]
    public void Switch()
    {
        var vm = new VirtualMachine(_switchProgram);
        vm.Execute2();
    }
}