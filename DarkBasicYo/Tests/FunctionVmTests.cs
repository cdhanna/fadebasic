using DarkBasicYo.Virtual;

namespace Tests;

public partial class TokenVm
{
     
    [Test]
    public void Function_NoReturn()
    {
        var src = @"
x = Test()
y = x
END
Function Test()

EndFunction

";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Function_Simple()
    {
        var src = @"
x = Test()

END
Function Test()
a = 1 + 2
EndFunction a
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Function_InvalidAccess()
    {
        var src = @"
global y as integer
y = 1
x = Test()

END
Function Test()
a = y
EndFunction a
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(1)); 
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }
    
    [Test]
    public void Function_ExplicitTypedArg()
    {
        var src = @"
x as byte
x = Test(2)

END
Function Test(a as byte)
EndFunction a * 2
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(4)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.BYTE));
    }
    
    
    [Test]
    public void Function_String()
    {
        var src = @"
x$ = Test(""world"")

END
Function Test(a as string)
EndFunction a + ""hello""
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        vm.heap.Read((int)vm.dataRegisters[0], "worldhello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("worldhello"));

        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
    }

    
    [Test]
    public void Function_CustomTypedArg()
    {
        var src = @"
TYPE egg
    x
    y
ENDTYPE


e as egg
e.x = 32
e.y = 66

sum = Test(e)

END
Function Test(a as egg)
EndFunction a.x + a.y
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        vm.heap.Read((int)vm.dataRegisters[0], 4, out var memory);
        var data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(32));
        
        vm.heap.Read((int)vm.dataRegisters[0] + 4, 4, out memory); 
        data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(66));

        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(32 + 66));
    }

    
    [Test]
    public void Function_CustomTypedArg_ReferenceMutation()
    {
        throw new NotImplementedException("is this right?");
        var src = @"
TYPE egg
    x
    y
ENDTYPE

e as egg
e.x = 32
e.y = 66
Test(e)

END
Function Test(a as egg)
a.x = a.x * 2
a.y = a.y - 10
EndFunction
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        vm.heap.Read((int)vm.dataRegisters[0], 4, out var memory);
        var data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(32*2));
        
        vm.heap.Read((int)vm.dataRegisters[0] + 4, 4, out memory); 
        data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(66-10));

        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        
    }

    
    [Test]
    public void Function_ArgOrder_1()
    {
        var src = @"
x = Test(1, 2)

END
Function Test(a, b)
EndFunction a
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Function_ArgOrder_2()
    {
        var src = @"
x = Test(1, 2)

END
Function Test(a, b)
EndFunction b
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(2));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Function_Args()
    {
        var src = @"
x = Test(1, 2)

END
Function Test(a, b)
EndFunction a + b
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Function_Args_Cast()
    {
        var src = @"
x = Test(1.2)

END
Function Test(a)
EndFunction a + 1
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);

        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(2)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Function_Args_TypeCast()
    {
        var src = @"
x = Test(1.2)

END
Function Test(a#)
EndFunction a# + 1
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);

        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(2));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }
    
    
    [Test]
    public void Function_Args_TypeCast_IntToFloat()
    {
        var src = @"
x# = Test(1)

END
Function Test(a#)
EndFunction a# + 1
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);

        vm.Execute2();
        
        var outputRegisterValue = vm.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        float output = BitConverter.ToSingle(outputRegisterBytes, 0);
        Assert.That(Math.Abs(output - 2f), Is.LessThan(.0001)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.REAL));
    }

    
    [Test]
    public void Function_Args_TypeCast_OrderFlip()
    {
        var src = @"
x = Test(5.2)

END
Function Test(a#)
EndFunction 1 + a# 
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);

        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(6));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Function_Args_TypeCast_NoCast()
    {
        var src = @"
x# = Test(1.2)

END
Function Test(a#)
EndFunction a# + 1
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        var outputRegisterValue = vm.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        float output = BitConverter.ToSingle(outputRegisterBytes, 0);
        Assert.That(Math.Abs(output - 2.2f), Is.LessThan(.0001)); 

        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.REAL));
    }
    
    [Test]
    public void Function_Scoping()
    {
        var src = @"
y = 1
Test()

END
Function Test()
y = 2
EndFunction
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Function_Recursion()
    {
        var src = @"
x = Test(1)

END
Function Test(a)
    IF a < 10
        a = Test(a + 1)
    ENDIF
EndFunction a
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(10)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [TestCase(0, 0)]
    [TestCase(1, 1)]
    [TestCase(2, 1)]
    [TestCase(3, 2)]
    [TestCase(4, 3)]
    [TestCase(5, 5)]
    [TestCase(6, 8)]
    public void Function_Fib(int input, int expected)
    {
        var src = @$"
x = Fib({input})

END
Function Fib(a)
    IF a <= 0
        ExitFunction 0
    ENDIF
    IF a = 1
        ExitFunction 1
    ENDIF
    ExitFunction Fib(a - 1) + Fib(a - 2)
EndFunction a
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(expected)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

}