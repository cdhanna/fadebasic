using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;

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
        Setup(src, out _, out var prog, ignoreParseCheck: true);
        _exprAst.AssertParseErrors(1);
        // var vm = new VirtualMachine(prog);
        // vm.Execute().MoveNext();
        //
        // Assert.That(vm.dataRegisters[1], Is.EqualTo(0));
        // Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Function_GotoHell()
    {
        // TODO: how do we stop people from jumping INTO a function? 
        //  probably by adding a restrictino that you cannot goto between function scopes ? 
        var src = @"
x = Test()
goto truck ` this line causes execution to jump into a function, which is bad bad bad
` the important part is that no END expression exists, but the compiler should auto-end
Function Test()
    a = 1 + 2
    truck:
EndFunction a
Function Death()
Endfunction
";
        Setup(src, out _, out var prog, expectedParseErrors:1);
        Assert.That(_exprAst.GetAllErrors()[0].errorCode, Is.EqualTo(ErrorCodes.TraverseLabelBetweenScopes));
    }
    
    [Test]
    public void Function_AutoEnd()
    {
        var src = @"
x = Test()
` the important part is that no END expression exists, but the compiler should auto-end
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
        
        Assert.That(vm.internedData.functions["test"].typeId, Is.EqualTo(0));
        Assert.That(vm.internedData.functions["test"].typeCode, Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Function_Global()
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
    public void Function_Global_AccessStruct()
    {
        var src = @"
TYPE egg
    x
ENDTYPE

GLOBAL albert AS egg ` declare as global, so it can be used in function
albert.x = 42

z = Test() ` put the result onto a variable so we can validate it

END
Function Test()
EndFunction albert.x ` just access the global value
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(42)); 
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }
    
    
    [Test]
    public void Function_Global_AccessStruct_WithRandoArray()
    {
        var src = @"
TYPE egg
    x
ENDTYPE
DIM randoArr(3) as egg ` for some reason this messes things up maybe?
GLOBAL albert AS egg ` declare as global, so it can be used in function
albert.x = 42
z = 1
z = Test() ` put the result onto a variable so we can validate it

END
Function Test()
EndFunction albert.x ` just access the global value
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[4], Is.EqualTo(42)); 
        Assert.That(vm.typeRegisters[4], Is.EqualTo(TypeCodes.INT));
    }
    
    [Test]
    public void Function_Local()
    {
        var src = @"
local y as integer
y = 1
x = Test()

END
Function Test()
a = y
EndFunction a
";
        Setup(src, out _, out var prog, ignoreParseCheck: true);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(0)); 
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
        vm.Execute2();
        
        vm.heap.Read(vm.dataRegisters[0].ToPtr(), "worldhello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("worldhello"));

        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
    }
    
    
    [Test]
    public void Function_Return_String()
    {
        var src = @"
x$ = """"
x$ = Test()
END
Function Test()
a$ = ""hello""
EndFunction a$
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        vm.heap.Read(vm.dataRegisters[0].ToPtr(), "hello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("hello"));

        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        
        
        Assert.That(vm.internedData.functions["test"].typeId, Is.EqualTo(0));
        Assert.That(vm.internedData.functions["test"].typeCode, Is.EqualTo(TypeCodes.STRING));
    }

    
    [Test]
    public void Function_Return_Struct()
    {
        var src = @"
TYPE egg
    x
ENDTYPE

e1 as egg
e1.x = 1

e2 = Test(e1)
e2.x = e2.x * 2

END
Function Test(e as egg)
e.x = e.x + 1
e.x = e.x + 1
EndFunction e
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.heap.Cursor, Is.EqualTo((4 * 3).ToPtr())); // size of the only field in egg, int, 4. And there are 3 copies; one in global scope, one passed to the function, and one returned from the function
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        vm.heap.Read(vm.dataRegisters[0].ToPtr(), 4, out var memory);
        var data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(1));
        
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.STRUCT));
        vm.heap.Read(vm.dataRegisters[1].ToPtr(), 4, out memory);
        data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(6));
    }

    
    [Test]
    public void Type_Array_Math()
    {
        var src = @"
x = 0
TYPE cardType
    suit
    value
ENDTYPE
dim cards(3) as cardType
cards(2).suit = 5
cards(2).value = 8
ct as cardType
ct = cards(2)

x = ct.value + ct.suit

";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        Assert.That(vm.dataRegisters[0], Is.EqualTo(13));
        // Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
    }


    [Test]
    public void Function_Reference_GlobalArray()
    {
        var src = @"
x = 0
dim cards(3) as integer
cards(0) = 100
cards(1) = 200
cards(2) = 300

x = Test()

END
Function Test()
    g = cards(0) + cards(1) + cards(2)
EndFunction g
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();

        // var expected = "the eight of pie";
        // vm.heap.GetAllocationSize((int)vm.dataRegisters[0], out var allocSize);
        // vm.heap.Read((int)vm.dataRegisters[0], allocSize, out var memory);
        // var str = VmConverter.ToString(memory);
        // Assert.That(str, Is.EqualTo(expected));

        Assert.That(vm.dataRegisters[0], Is.EqualTo(600));
        // Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
    }
    
    [Test]
    public void Function_Return_CardGameUseCase_Simple()
    {
        var src = @"
x = 0
TYPE cardType
    suit as integer
    value as integer
ENDTYPE
dim cards(3) as cardType
cards(2).suit = 5
cards(2).value = 8

x = Test(2)

END
Function Test(index)
    ct as cardType
    ct = cards(index)
    `IF ct.suit = 5 then returnValue$ = ""of pie""
    `IF ct.value = 8 then returnValue$ = ""the eight "" + returnValue$
    `returnValue$ = ""of pie""
    `returnValue$ = ""the eight"" + returnValue$
    
EndFunction ct.suit + ct.value
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();

        // var expected = "the eight of pie";
        // vm.heap.GetAllocationSize((int)vm.dataRegisters[0], out var allocSize);
        // vm.heap.Read((int)vm.dataRegisters[0], allocSize, out var memory);
        // var str = VmConverter.ToString(memory);
        // Assert.That(str, Is.EqualTo(expected));

        Assert.That(vm.dataRegisters[0], Is.EqualTo(13));
        // Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
    }

    [Test]
    public void Function_Return_CardGameUseCase()
    {
        var src = @"
x$ = """"
TYPE cardType
    suit as integer
    value as integer
ENDTYPE
dim cards(3) as cardType
cards(2).suit = 5
cards(2).value = 8

x$ = Test(2)
print x$
END
Function Test(index)
    ct as cardType
    ct = cards(index)
    IF ct.suit = 5 then returnValue$ = ""of pie""
    IF ct.value = 8 then returnValue$ = ""the eight "" + returnValue$
    
EndFunction returnValue$
";
        Setup(src, out var compiler, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;

        vm.Execute().MoveNext();

        // at this point, the pointer to the right heap is getting lost???
        
        var expected = "the eight of pie";
        vm.heap.GetAllocationSize(vm.dataRegisters[0].ToPtr(), out var allocSize);
        vm.heap.Read(vm.dataRegisters[0].ToPtr(), allocSize, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo(expected));

        // Assert.That(vm.dataRegisters[0], Is.EqualTo(13));
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

derp = Test(e)

END
Function Test(a as egg)
EndFunction a.x + a.y
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        vm.heap.Read(vm.dataRegisters[0].ToPtr(), 4, out var memory);
        var data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(32));
        
        vm.heap.Read(vm.dataRegisters[0].ToPtr() + 4, 4, out memory); 
        data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(66));

        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(32 + 66));
    }

    
    [Test]
    public void Function_CustomTypedArg_ReferenceMutation_DoesNotChangeOriginal()
    {
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
        
        vm.heap.Read(vm.dataRegisters[0].ToPtr(), 4, out var memory);
        var data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(32));
        
        vm.heap.Read(vm.dataRegisters[0].ToPtr() + 4, 4, out memory); 
        data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(66));

        
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
        Setup(src, out var compiler, out var prog);
        
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
        
        this._exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        
        // parameters are in reverse order of index
        Assert.That(vm.internedData.functions["test"].parameters[1].index, Is.EqualTo(0));
        Assert.That(vm.internedData.functions["test"].parameters[1].name, Is.EqualTo("a"));
        
        Assert.That(vm.internedData.functions["test"].parameters[0].index, Is.EqualTo(1));
        Assert.That(vm.internedData.functions["test"].parameters[0].name, Is.EqualTo("b"));
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
        Assert.That(vm.stack.ptr, Is.EqualTo(0));

    }
    
   
    [Test]
    public void Function_UnusedReturnValue()
    {
        var src = @"
Test(1)

Function Test(a)
    ` the value a will be put onto the stack due to the return, but nothing takes it off?
EndFunction a
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.stack.ptr, Is.EqualTo(0));
    }

    
    [TestCase(0, 0)]
    [TestCase(1, 1)]
    [TestCase(2, 1)]
    [TestCase(3, 2)]
    [TestCase(4, 3)]
    [TestCase(5, 5)]
    [TestCase(6, 8)]
    [TestCase(7, 13)]
    [TestCase(8, 21)]
    [TestCase(9, 34)]
    [TestCase(10, 55)]
    [TestCase(11, 89)]
    [TestCase(12, 55+89)]
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
EndFunction 0
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2(25000); // YO, the instruction count needs to be high
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(expected)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

}