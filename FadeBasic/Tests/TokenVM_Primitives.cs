using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace Tests;

public partial class TokenVm
{
    [TestCase(-1, 0, 255)]
    [TestCase(0, 0, 0)]
    [TestCase(10, 0, 10)]
    [TestCase(255, 0, 255)]
    [TestCase(256, 0, 0)]
    [TestCase(257, 0, 1)]
    [TestCase(512, 0, 0)]
    [TestCase(-4, 4, 0)]
    [TestCase(-4, 2, 256 - 2)]
    public void Primitive_Range_Byte(int source, int add, int expected)
    {
        var src = @$"
GLOBAL x as byte
y = func({source})

s$ = str$(x)

FUNCTION func(y as byte)
    x = y
    x = x + {add}
ENDFUNCTION x
";
        Setup(src, out var compiler, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;

        vm.Execute2(0);

        var raw = vm.dataRegisters[0];
        var raw2 = vm.dataRegisters[1];
        Assert.That(raw, Is.EqualTo(raw2));
        var value = VmUtil.ConvertToByte(raw);
        var value2 = VmUtil.ConvertToByte(raw);
        
        Assert.That(value, Is.EqualTo(expected));
        Assert.That(value2, Is.EqualTo(value));

        var span = new ReadOnlySpan<byte>(BitConverter.GetBytes(vm.dataRegisters[2]));
        var str = VmUtil.ConvertValueToDisplayString(TypeCodes.STRING, vm, ref span);
        Assert.That(str, Is.EqualTo(expected.ToString()));
    }
    
    
    [TestCase(-1, 0, 65535)]
    [TestCase(-1, 1, 0)]
    [TestCase(65535, 1, 0)]
    [TestCase(42, 42, 2*42)]
    public void Primitive_Range_Word(int source, int add, int expected)
    {
        var src = @$"
GLOBAL x as word = {source}
func({source})
y as word = x
s$ = str$(x)

FUNCTION func(y as word)
    x = y
    x = x + {add}
ENDFUNCTION x
";
        Setup(src, out var compiler, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;

        // var ft = _exprAst.functions[0].ParsedType;
        // Assert.That(ft.type, Is.EqualTo(VariableType.Word));
        
        vm.Execute2(0);

        var raw = vm.dataRegisters[0];
        var raw2 = vm.dataRegisters[1];
        Assert.That(raw, Is.EqualTo(raw2));
        var value = VmUtil.ConvertToWord(raw);
        var value2 = VmUtil.ConvertToWord(raw);
        
        Assert.That(value, Is.EqualTo(expected));
        Assert.That(value2, Is.EqualTo(value));

        var span = new ReadOnlySpan<byte>(BitConverter.GetBytes(vm.dataRegisters[2]));
        var str = VmUtil.ConvertValueToDisplayString(TypeCodes.STRING, vm, ref span);
        Assert.That(str, Is.EqualTo(expected.ToString()));
    }
}