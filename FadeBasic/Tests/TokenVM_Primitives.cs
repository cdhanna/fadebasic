using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace Tests;

public partial class TokenVm
{
    
    [TestCase(4, 4, (ushort)4, (uint)4, 4, 4, 4, 4)]
    [TestCase(300, 300, (ushort)300, (uint)300, 300, 300, 44, 44)]
    public void Primitives_Casting_FromDFloat(double value, long castDInt, ushort castWord, uint castDword, float castFloat, double castInt, byte castByte, byte castBool)
    {
        var src = $@"
x as double float = {value}
x_di as double integer = x
x_w as word = x
x_dw as dword = x
x_f as float = x
x_i as integer = x
x_b as byte = x
x_b2 as bool = x

";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[1]), Is.EqualTo(castDInt));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[2]), Is.EqualTo(castWord));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[3]), Is.EqualTo(castDword));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[4]), Is.EqualTo(castFloat).Within(0.0001f));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[5]), Is.EqualTo(castInt).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[6]), Is.EqualTo(castByte).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[7]), Is.EqualTo(castBool).Within(0.0001f));
    }

    
    [TestCase(4, 4, (ushort)4, (uint)4, 4, 4, 4, 4)]
    [TestCase(300, 300, (ushort)300, (uint)300, 300, 300, 44, 44)]
    public void Primitives_Casting_FromFloat(float value, long castDInt, ushort castWord, uint castDword, int castInt, double castDbl, byte castByte, byte castBool)
    {
        var src = $@"
x as float = {value}
x_di as double integer = x
x_w as word = x
x_dw as dword = x
x_i as int = x
x_df as double float = x
x_b as byte = x
x_b2 as bool = x

";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[1]), Is.EqualTo(castDInt));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[2]), Is.EqualTo(castWord));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[3]), Is.EqualTo(castDword));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[4]), Is.EqualTo(castInt).Within(0.0001f));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[5]), Is.EqualTo(castDbl).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[6]), Is.EqualTo(castByte).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[7]), Is.EqualTo(castBool).Within(0.0001f));
    }
    
    [TestCase((uint)4, 4, (ushort)4, 4, 4, 4, 4, 4)]
    [TestCase((uint)300, 300, (ushort)300, 300, 300, 300, 44, 44)]
    public void Primitives_Casting_FromDWord(uint value, long castDInt, ushort castWord, int castInt, float castFloat, double castDbl, byte castByte, byte castBool)
    {
        var src = $@"
x as dword = {value}
x_di as double integer = x
x_w as word = x
x_i as integer = x
x_f as float = x
x_df as double float = x
x_b as byte = x
x_b2 as bool = x

";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[1]), Is.EqualTo(castDInt));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[2]), Is.EqualTo(castWord));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[3]), Is.EqualTo(castInt));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[4]), Is.EqualTo(castFloat).Within(0.0001f));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[5]), Is.EqualTo(castDbl).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[6]), Is.EqualTo(castByte).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[7]), Is.EqualTo(castBool).Within(0.0001f));
    }
    
    [TestCase((ushort)4, 4, 4, (uint)4, 4, 4, 4, 4)]
    [TestCase((ushort) 300, 300, 300, (uint)300, 300, 300, 44, 44)]
    public void Primitives_Casting_FromWord(ushort value, long castDInt, int castInt, uint castDword, float castFloat, double castDbl, byte castByte, byte castBool)
    {
        var src = $@"
x as word = {value}
x_di as double integer = x
x_i as integer = x
x_dw as dword = x
x_f as float = x
x_df as double float = x
x_b as byte = x
x_b2 as bool = x

";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[1]), Is.EqualTo(castDInt));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[2]), Is.EqualTo(castInt));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[3]), Is.EqualTo(castDword));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[4]), Is.EqualTo(castFloat).Within(0.0001f));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[5]), Is.EqualTo(castDbl).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[6]), Is.EqualTo(castByte).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[7]), Is.EqualTo(castBool).Within(0.0001f));
    }

    
    [TestCase(4, 4, (ushort)4, (uint)4, 4, 4, 4, 4)] 
    public void Primitives_Casting_FromByte(byte value, long castDInt, ushort castWord, uint castDword, float castFloat, double castDbl, int castInt, byte castBool)
    {
        var src = $@"
x as byte = {value}
x_di as double integer = x
x_w as word = x
x_dw as dword = x
x_f as float = x
x_df as double float = x
x_i as int = x
x_b2 as bool = x

";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[1]), Is.EqualTo(castDInt));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[2]), Is.EqualTo(castWord));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[3]), Is.EqualTo(castDword));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[4]), Is.EqualTo(castFloat).Within(0.0001f));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[5]), Is.EqualTo(castDbl).Within(0.0001f));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[6]), Is.EqualTo(castInt).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[7]), Is.EqualTo(castBool).Within(0.0001f));
    }
    
    [TestCase(4, 4, (ushort)4, (uint)4, 4, 4, 4, 4)] 
    public void Primitives_Casting_FromBool(byte value, long castDInt, ushort castWord, uint castDword, float castFloat, double castDbl, int castInt, byte castBool)
    {
        var src = $@"
x as bool = {value}
x_di as double integer = x
x_w as word = x
x_dw as dword = x
x_f as float = x
x_df as double float = x
x_i as int = x
x_b as byte = x

";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[1]), Is.EqualTo(castDInt));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[2]), Is.EqualTo(castWord));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[3]), Is.EqualTo(castDword));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[4]), Is.EqualTo(castFloat).Within(0.0001f));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[5]), Is.EqualTo(castDbl).Within(0.0001f));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[6]), Is.EqualTo(castInt).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[7]), Is.EqualTo(castBool).Within(0.0001f));
    }
    
    [TestCase(4, 4, (ushort)4, (uint)4, 4, 4, 4, 4)]
    [TestCase(300, 300, (ushort)300, (uint)300, 300, 300, 44, 44)]
    public void Primitives_Casting_FromInt(int value, long castDInt, ushort castWord, uint castDword, float castFloat, double castDbl, byte castByte, byte castBool)
    {
        var src = $@"
x as integer = {value}
x_di as double integer = x
x_w as word = x
x_dw as dword = x
x_f as float = x
x_df as double float = x
x_b as byte = x
x_b2 as bool = x

";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[1]), Is.EqualTo(castDInt));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[2]), Is.EqualTo(castWord));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[3]), Is.EqualTo(castDword));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[4]), Is.EqualTo(castFloat).Within(0.0001f));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[5]), Is.EqualTo(castDbl).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[6]), Is.EqualTo(castByte).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[7]), Is.EqualTo(castBool).Within(0.0001f));
    }
    
    [TestCase(4, 4, (ushort)4, (uint)4, 4, 4, 4, 4)]
    [TestCase(300, 300, (ushort)300, (uint)300, 300, 300, 44, 44)]
    public void Primitives_Casting_FromDInt(long value, int castInt, ushort castWord, uint castDword, float castFloat, double castDbl, byte castByte, byte castBool)
    {
        var src = $@"
x as double integer = {value}
x_i as integer = x
x_w as word = x
x_dw as dword = x
x_f as float = x
x_df as double float = x
x_b as byte = x
x_b2 as bool = x

";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[1]), Is.EqualTo(castInt));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[2]), Is.EqualTo(castWord));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[3]), Is.EqualTo(castDword));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[4]), Is.EqualTo(castFloat).Within(0.0001f));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[5]), Is.EqualTo(castDbl).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[6]), Is.EqualTo(castByte).Within(0.0001f));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[7]), Is.EqualTo(castBool).Within(0.0001f));
    }


    [TestCase(5)]
    public void Primitive_CallHost_Return_DInt(int num)
    {
        var src = @$"
x as double integer = prim test di({num})
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[0]), Is.EqualTo(num * 2).Within(0.0001f));
    }
    
    
    [TestCase(5)]
    public void Primitive_CallHost_Return_Word(int num)
    {
        var src = @$"
x as word = prim test w({num})
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[0]), Is.EqualTo(num * 2).Within(0.0001f));
    }
    
    
    [TestCase(5)]
    public void Primitive_CallHost_Return_DWord(int num)
    {
        var src = @$"
x as dword = prim test dw({num})
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[0]), Is.EqualTo(num * 2).Within(0.0001f));
    }
    
    [TestCase(5)]
    public void Primitive_CallHost_Return_Float(int num)
    {
        var src = @$"
x as float = prim test f({num})
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[0]), Is.EqualTo(num * 2).Within(0.0001f));
    }
    
    [TestCase(5)]
    public void Primitive_CallHost_Return_DFloat(int num)
    {
        var src = @$"
x as double float = prim test df({num})
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[0]), Is.EqualTo(num * 2).Within(0.0001f));
    }

    
    [TestCase(5)]
    public void Primitive_CallHost_Return_Byte(int num)
    {
        var src = @$"
x as byte = prim test b({num})
";
        
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[0]), Is.EqualTo(num * 2).Within(0.0001f));
    }
    
    [TestCase(5)]
    [TestCase(0)]
    public void Primitive_CallHost_Return_Bool(int num)
    {
        var src = @$"
x as bool = prim test b2({num})
";
        
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[0]), Is.EqualTo(num > 0 ? 0 : 1).Within(0.0001f));
    }
    
    [Test]
    public void Primitive_CallHost_AllInput()
    {
        var src = @"
s$ = all the prims(1,1,1,1, 1,1,1,1)
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        var expected = TestCommands.Todos(1, 1, 1, 1, 1, 1, 1, true);

        var span = new ReadOnlySpan<byte>(BitConverter.GetBytes(vm.dataRegisters[0]));
        var str = VmUtil.ConvertValueToDisplayString(TypeCodes.STRING, vm, ref span);
        Assert.That(str, Is.EqualTo(expected));
    }
    
    
    [Test]
    public void Primitive_CallHost_AllInput_Casts()
    {
        var src = @"
x as double integer = 4
s$ = all the prims(x,1,1,1, 1,1,1,1)
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        var expected = TestCommands.Todos(4, 1, 1, 1, 1, 1, 1, true);

        var span = new ReadOnlySpan<byte>(BitConverter.GetBytes(vm.dataRegisters[1]));
        var str = VmUtil.ConvertValueToDisplayString(TypeCodes.STRING, vm, ref span);
        Assert.That(str, Is.EqualTo(expected));
    }

    
    
    [TestCase(5.5f, 2.2f, 7.7f, 3.3f, 12.1f, 2.5f, 1.1f, 30.25f, -5.5f, 1, 0, 0)]
    [TestCase(-3.4f, 1.7f, -1.7f, -5.1f, -5.78f, -2.0f, 0.0f, -8.007f, 3.4f, 0, 1, 0)]
    public void Primitive_Operations_Float(float a, float b,
        float expectedAdd, float expectedSub, float expectedMul,
        float expectedDiv, float expectedMod, float expectedExp, float expectedNeg,
        int expectedGt, int expectedLt, int expectedEq)
    {
        var src = @$"
add_result as float = {a} + {b}
sub_result as float = {a} - {b}
mul_result as float = {a} * {b}
div_result as float = {a} / {b}
mod_result as float = {a} MOD {b}
exp_result as float = 0
neg_result as float = -{a}
gt_result as float = {a} > {b}
lt_result as float = {a} < {b}
eq_result as float = {a} = {b}


STATIC PRINT add_result, sub_result
func_result# = GetExpResult(add_result, 1)

FUNCTION GetExpResult(x as float, y as float)
    result as float = x + y
ENDFUNCTION result
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);
        
        Assert.That(float.Parse(TestCommands.staticPrintBuffer[0]), Is.EqualTo(expectedAdd).Within(0.0001f));
        Assert.That(float.Parse(TestCommands.staticPrintBuffer[1]), Is.EqualTo(expectedSub).Within(0.0001f));
       
    
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[0]), Is.EqualTo(expectedAdd).Within(0.0001f));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[1]), Is.EqualTo(expectedSub).Within(0.0001f));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[2]), Is.EqualTo(expectedMul).Within(0.0001f));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[3]), Is.EqualTo(expectedDiv).Within(0.0001f));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[4]), Is.EqualTo(expectedMod).Within(0.0001f));
        //Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[5]), Is.EqualTo(expectedExp).Within(0.0001f));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[6]), Is.EqualTo(expectedNeg).Within(0.0001f));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[7]), Is.EqualTo(expectedGt));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[8]), Is.EqualTo(expectedLt));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[9]), Is.EqualTo(expectedEq));
        Assert.That(VmUtil.ConvertToFloat(vm.dataRegisters[10]), Is.EqualTo(expectedAdd + 1).Within(0.0001f));

    }
    
    [TestCase(5, 2, 7, 3, 10, 2, 1, 25, 65531, 1, 0, 0)] // Example: (5, 2)
    [TestCase(65535, 1, 0, 65534, 65535, 65535, 0, 65535, 1, 1, 0, 0)] // Example: (WORD wraparound)
    public void Primitive_Operations_Word(int a, int b, 
        int expectedAdd, int expectedSub, int expectedMul, 
        int expectedDiv, int expectedMod, int expectedExp, int expectedNeg, 
        int expectedGt, int expectedLt, int expectedEq)
    {
        var src = @$"
add_result as word = {a} + {b}
sub_result as word = {a} - {b}
mul_result as word = {a} * {b}
div_result as word = {a} / {b}
mod_result as word = {a} MOD {b}
exp_result as word = {a} ^ {b}
neg_result as word = -{a}
gt_result as word = {a} > {b}
lt_result as word = {a} < {b}
eq_result as word = {a} = {b}

STATIC PRINT add_result, sub_result
func_result = GetExpResult(add_result, 1)

FUNCTION GetExpResult(x as word, y as word)
    result = x ^ y
ENDFUNCTION result
";

        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo(expectedAdd.ToString()));
        Assert.That(TestCommands.staticPrintBuffer[1], Is.EqualTo(expectedSub.ToString()));
        
        
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[0]), Is.EqualTo(expectedAdd));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[1]), Is.EqualTo(expectedSub));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[2]), Is.EqualTo(expectedMul));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[3]), Is.EqualTo(expectedDiv));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[4]), Is.EqualTo(expectedMod));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[5]), Is.EqualTo(expectedExp));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[6]), Is.EqualTo(expectedNeg));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[7]), Is.EqualTo(expectedGt));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[8]), Is.EqualTo(expectedLt));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[9]), Is.EqualTo(expectedEq));
        Assert.That(VmUtil.ConvertToWord(vm.dataRegisters[10]), Is.EqualTo(expectedAdd));
    }
    
    /*
     * max - 4
     * max - 3
     * max - 2
     * max - 1
     * max
     * 0
     * 1
     * 2
     * 3
     * 4
     * 5
     * 
     */
    [TestCase(5, 2, 7, 3, 10, 2, 1, 25, uint.MaxValue-4, 1, 0, 0, 1, 0)] // Example: (5, 2)
    [TestCase(65535, 1, 65536, 65534, 65535, 65535, 0, 65535, uint.MaxValue - 65534, 1, 0, 0, 1, 0)] // Example: (WORD wraparound)
    public void Primitive_Operations_DWord(int a, int b, 
        int expectedAdd, int expectedSub, int expectedMul, 
        int expectedDiv, int expectedMod, int expectedExp, uint expectedNeg, 
        int expectedGt, int expectedLt, int expectedEq, int expectedGte, int expectedLte)
    {
        var src = @$"
a as dword = {a}
b as dword = {b}
add_result as dword = a + b
sub_result as dword = a - b
mul_result as dword = a * b
div_result as dword = a / b
mod_result as dword = a MOD b
exp_result as dword = a ^ b
neg_result as dword = -a
gt_result as dword = a > b
lt_result as dword = a < b
eq_result as dword = a = b

STATIC PRINT add_result, sub_result
func_result as dword = GetExpResult(add_result, 1)

gte_result as dword = a >= b
lte_result as dword = a <= b


FUNCTION GetExpResult(x as dword, y as dword)
    result as dword = x ^ y
ENDFUNCTION result
";

        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo(expectedAdd.ToString()));
        Assert.That(TestCommands.staticPrintBuffer[1], Is.EqualTo(expectedSub.ToString()));
        
        
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[2]), Is.EqualTo(expectedAdd));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[3]), Is.EqualTo(expectedSub));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[4]), Is.EqualTo(expectedMul));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[5]), Is.EqualTo(expectedDiv));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[6]), Is.EqualTo(expectedMod));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[7]), Is.EqualTo(expectedExp));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[8]), Is.EqualTo(expectedNeg));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[9]), Is.EqualTo(expectedGt));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[10]), Is.EqualTo(expectedLt));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[11]), Is.EqualTo(expectedEq));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[12]), Is.EqualTo(expectedAdd));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[13]), Is.EqualTo(expectedGte));
        Assert.That(VmUtil.ConvertToDWord(vm.dataRegisters[14]), Is.EqualTo(expectedLte));
    }
    
    
    [TestCase(5, 2, 
        7, 
        3, 
        10, 
        2, 
        1, 
        25, 
        byte.MaxValue - 4, 
        1, 
        0, 
        0, 
        1, 
        0)]
    public void Primitive_Operations_Bool(double a, double b, 
        int expectedAdd, int expectedSub, int expectedMul, 
        int expectedDiv, int expectedMod, int expectedExp, long expectedNeg, 
        int expectedGt, int expectedLt, int expectedEq, int expectedGte, int expectedLte)
    {
        var src = @$"
a as bool = {a}
b as bool = {b}
add_result as bool = a + b
sub_result as bool = a - b
mul_result as bool = a * b
div_result as bool = a / b
mod_result as bool = a MOD b
exp_result as bool = a ^ b
neg_result as bool = -a
gt_result as bool = a > b
lt_result as bool = a < b
eq_result as bool = a = b

STATIC PRINT add_result, sub_result
func_result as bool = GetExpResult(add_result, 1)

gte_result as bool = a >= b
lte_result as bool = a <= b


FUNCTION GetExpResult(x as bool, y as bool)
    result as bool = x ^ y
ENDFUNCTION result
";

        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[0]), Is.EqualTo(a));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[1]), Is.EqualTo(b));
        
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[2]), Is.EqualTo(expectedAdd));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[3]), Is.EqualTo(expectedSub));
        
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[4]), Is.EqualTo(expectedMul));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[5]), Is.EqualTo(expectedDiv));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[6]), Is.EqualTo(expectedMod));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[7]), Is.EqualTo(expectedExp));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[8]), Is.EqualTo(expectedNeg));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[9]), Is.EqualTo(expectedGt));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[10]), Is.EqualTo(expectedLt));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[11]), Is.EqualTo(expectedEq));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[12]), Is.EqualTo(expectedAdd));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[13]), Is.EqualTo(expectedGte));
        Assert.That(VmUtil.ConvertToByte(vm.dataRegisters[14]), Is.EqualTo(expectedLte));
        
        
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo(expectedAdd.ToString()));
        Assert.That(TestCommands.staticPrintBuffer[1], Is.EqualTo(expectedSub.ToString()));

    }
    
    [TestCase(5, 2, 
        7, 
        3, 
        10, 
        2.5d, 
        1, 
        25, 
        -5, 
        1, 
        0, 
        0, 
        1, 
        0)]
    public void Primitive_Operations_DFloat(double a, double b, 
        int expectedAdd, int expectedSub, int expectedMul, 
        double expectedDiv, int expectedMod, int expectedExp, long expectedNeg, 
        int expectedGt, int expectedLt, int expectedEq, int expectedGte, int expectedLte)
    {
        var src = @$"
a as double float = {a}
b as double float = {b}
add_result as double float = a + b
sub_result as double float = a - b
mul_result as double float = a * b
div_result as double float = a / b
mod_result as double float = a MOD b
exp_result as double float = a ^ b
neg_result as double float = -a
gt_result as double float = a > b
lt_result as double float = a < b
eq_result as double float = a = b

STATIC PRINT add_result, sub_result
func_result as double float = GetExpResult(add_result, 1)

gte_result as double float = a >= b
lte_result as double float = a <= b


FUNCTION GetExpResult(x as double float, y as double float)
    result as double float = x ^ y
ENDFUNCTION result
";

        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[0]), Is.EqualTo(a));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[1]), Is.EqualTo(b));
        
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[2]), Is.EqualTo(expectedAdd));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[3]), Is.EqualTo(expectedSub));
        
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[4]), Is.EqualTo(expectedMul));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[5]), Is.EqualTo(expectedDiv));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[6]), Is.EqualTo(expectedMod));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[7]), Is.EqualTo(expectedExp));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[8]), Is.EqualTo(expectedNeg));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[9]), Is.EqualTo(expectedGt));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[10]), Is.EqualTo(expectedLt));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[11]), Is.EqualTo(expectedEq));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[12]), Is.EqualTo(expectedAdd));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[13]), Is.EqualTo(expectedGte));
        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[14]), Is.EqualTo(expectedLte));
        
        
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo(expectedAdd.ToString()));
        Assert.That(TestCommands.staticPrintBuffer[1], Is.EqualTo(expectedSub.ToString()));

    }
    
    
    [TestCase(5)]
    [TestCase(-1)]
    public void Primitive_Operations_DFloat_InlineCastIssue(int num)
    {
        var src = @$"
x as double float = {num}
";

        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[0]), Is.EqualTo(num));
    }

    [Test]
    public void Primitive_Operations_DFloat_PassToFunction()
    {
        var src = @$"
z as double float = 4
y as double float = lateNightSnacking(z, 1)
FUNCTION lateNightSnacking(x as double float, w as double float)
    
ENDFUNCTION x + w
";

        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        Assert.That(VmUtil.ConvertToDFloat(vm.dataRegisters[1]), Is.EqualTo(5));
    }

    
    [TestCase(5, 2, 
        7, 
        3, 
        10, 
        2, 
        1, 
        25, 
        -5, 
        1, 
        0, 
        0, 
        1, 
        0)]
    public void Primitive_Operations_DInt(int a, int b, 
        int expectedAdd, int expectedSub, int expectedMul, 
        int expectedDiv, int expectedMod, int expectedExp, long expectedNeg, 
        int expectedGt, int expectedLt, int expectedEq, int expectedGte, int expectedLte)
    {
        var src = @$"
a as double integer = {a}
b as double integer = {b}
add_result as double integer = a + b
sub_result as double integer = a - b
mul_result as double integer = a * b
div_result as double integer = a / b
mod_result as double integer = a MOD b
exp_result as double integer = a ^ b
neg_result as double integer = -a
gt_result as double integer = a > b
lt_result as double integer = a < b
eq_result as double integer = a = b

STATIC PRINT add_result, sub_result
func_result as double integer = GetExpResult(add_result, 1)

gte_result as double integer = a >= b
lte_result as double integer = a <= b


FUNCTION GetExpResult(x as double integer, y as double integer)
    result as double integer = x ^ y
ENDFUNCTION result
";

        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[0]), Is.EqualTo(a));
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[1]), Is.EqualTo(b));
        
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[2]), Is.EqualTo(expectedAdd));
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[3]), Is.EqualTo(expectedSub));
        
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo(expectedAdd.ToString()));
        Assert.That(TestCommands.staticPrintBuffer[1], Is.EqualTo(expectedSub.ToString()));
        
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[4]), Is.EqualTo(expectedMul));
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[5]), Is.EqualTo(expectedDiv));
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[6]), Is.EqualTo(expectedMod));
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[7]), Is.EqualTo(expectedExp));
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[8]), Is.EqualTo(expectedNeg));
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[9]), Is.EqualTo(expectedGt));
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[10]), Is.EqualTo(expectedLt));
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[11]), Is.EqualTo(expectedEq));
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[12]), Is.EqualTo(expectedAdd));
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[13]), Is.EqualTo(expectedGte));
        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[14]), Is.EqualTo(expectedLte));
    }
    
    [TestCase(5)]
    [TestCase(-1)]
    public void Primitive_Operations_DInt_InlineCastIssue(int num)
    {
        var src = @$"
x as double integer = {num}
";

        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        Assert.That(VmUtil.ConvertToDInt(vm.dataRegisters[0]), Is.EqualTo(num));
    }
    
    
    [TestCase(5, 2, 7, 3, 10, 2, 1, 25, -5, 1, 0, 0)] // Example: (5, 2)
    [TestCase(10, 5, 15, 5, 50, 2, 0, 100_000, -10, 1, 0, 0)] // Example: (10, 5)
    public void Primitive_Operations_Integer(int a, int b, 
        int expectedAdd, int expectedSub, int expectedMul, 
        int expectedDiv, int expectedMod, int expectedExp, int expectedNeg, 
        int expectedGt, int expectedLt, int expectedEq)
    {
        var src = @$"

add_result = {a} + {b}      ' Addition
sub_result = {a} - {b}      ' Subtraction
mul_result = {a} * {b}      ' Multiplication
div_result = {a} / {b}      ' Division
mod_result = {a} MOD {b}    ' Modulus
exp_result = {a} ^ {b}      ' Exponentiation
neg_result = -{a}           ' Negation
gt_result = {a} > {b}       ' Greater Than
lt_result = {a} < {b}       ' Less Than
eq_result = {a} = {b}       ' Equal To

STATIC PRINT add_result, sub_result

' Using a function to return a value
func_result = GetExpResult(add_result, 1)

FUNCTION GetExpResult(x as integer, y as integer)
    result = x ^ y
ENDFUNCTION result
";

        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;

        vm.Execute2(0);

        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo(expectedAdd.ToString()));
        Assert.That(TestCommands.staticPrintBuffer[1], Is.EqualTo(expectedSub.ToString()));
        
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[0]), Is.EqualTo(expectedAdd));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[1]), Is.EqualTo(expectedSub));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[2]), Is.EqualTo(expectedMul));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[3]), Is.EqualTo(expectedDiv));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[4]), Is.EqualTo(expectedMod));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[5]), Is.EqualTo(expectedExp));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[6]), Is.EqualTo(expectedNeg));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[7]), Is.EqualTo(expectedGt));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[8]), Is.EqualTo(expectedLt));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[9]), Is.EqualTo(expectedEq));
        Assert.That(VmUtil.ConvertToInt(vm.dataRegisters[10]), Is.EqualTo(expectedAdd));
    }
    
    
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
    
    [TestCase(-1, 0, 4294967295)]
    [TestCase(0, 0, (uint)0)]
    [TestCase(10, 0, (uint)10)]
    // [TestCase(4294967293, 1, 0)]
    [TestCase(100000, 100000, (uint)200000)]
    public void Primitive_Range_DWord(int source, int add, uint expected)
    {
        var src = @$"
GLOBAL x as dword = {source}
func({source})
y as dword = x
s$ = str$(x)

FUNCTION func(y as dword)
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
        var value = VmUtil.ConvertToDWord(raw);
        var value2 = VmUtil.ConvertToDWord(raw);
        
        Assert.That(value, Is.EqualTo(expected));
        Assert.That(value2, Is.EqualTo(value));

        var span = new ReadOnlySpan<byte>(BitConverter.GetBytes(vm.dataRegisters[2]));
        var str = VmUtil.ConvertValueToDisplayString(TypeCodes.STRING, vm, ref span);
        Assert.That(str, Is.EqualTo(expected.ToString()));
    }
}