using System.Diagnostics;
using DarkBasicYo;
using DarkBasicYo.Ast;
using DarkBasicYo.Virtual;

namespace Tests;

public class TokenVm
{
    private ProgramNode? _exprAst;

    void Setup(string src, out Compiler compiler, out List<byte> progam)
    {
        var collection = TestCommands.Commands;
        var lexer = new Lexer();
        var tokens = lexer.Tokenize(src, collection);
        var parser = new Parser(new TokenStream(tokens), collection);
        _exprAst = parser.ParseProgram();

        compiler = new Compiler(collection);
        compiler.Compile(_exprAst);
        progam = compiler.Program;
    }
    
    
    
    [Test]
    public void TestDeclare_Registers()
    {
        var src = "x AS WORD; x = 12";
        Setup(src, out _, out var prog);
        
        // Assert.That(prog.Count, Is.EqualTo(6)); // type code and 4 bytes for the int
        Assert.That(prog[0], Is.EqualTo(OpCodes.PUSH));
        Assert.That(prog[1], Is.EqualTo(TypeCodes.INT));
        Assert.That(prog[2], Is.EqualTo(0));
        Assert.That(prog[3], Is.EqualTo(0));
        Assert.That(prog[4], Is.EqualTo(0));
        Assert.That(prog[5], Is.EqualTo(12));
        Assert.That(prog[6], Is.EqualTo(OpCodes.CAST));
        Assert.That(prog[7], Is.EqualTo(TypeCodes.WORD));

        Assert.That(prog[8], Is.EqualTo(OpCodes.STORE));
        Assert.That(prog[9], Is.EqualTo(0));
    }

    
    [Test]
    public void String_Concat()
    {
        var src = @"
x$ = ""hello""
y$ = ""world""
z$ = x$ + y$
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0)); // the ptr to the string in memory
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        
        vm.heap.Read((int)vm.dataRegisters[0], "hello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("hello"));
        
        
        vm.heap.Read((int)vm.dataRegisters[1], "world".Length * 4, out memory);
        str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("world"));
        
        vm.heap.Read((int)vm.dataRegisters[2], "helloworld".Length * 4, out memory);
        str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("helloworld"));
    }

    
    [Test]
    public void String_Concat_VariableAndLiteral()
    {
        var src = @"
x$ = ""hello""
y$ = x$ + "" world""
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0)); // the ptr to the string in memory
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        
        vm.heap.Read((int)vm.dataRegisters[0], "hello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("hello"));

        vm.heap.Read((int)vm.dataRegisters[1], "hello world".Length * 4, out memory);
        str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("hello world"));
        
    }

    
    [Test]
    public void String_Concat_LiteralAndVariable()
    {
        var src = @"
x$ = ""hello""
y$ = ""world"" + x$
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0)); // the ptr to the string in memory
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        
        vm.heap.Read((int)vm.dataRegisters[0], "hello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("hello"));

        vm.heap.Read((int)vm.dataRegisters[1], "helloworld".Length * 4, out memory);
        str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("worldhello"));
        
    }

    
    [Test]
    public void String_Declare()
    {
        var src = "x as string; x = \"hello\" ";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0)); // the ptr to the string in memory
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        
        vm.heap.Read(0, "hello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        
        Assert.That(str, Is.EqualTo("hello"));
    }

    
    [Test]
    public void String_Declare_Anon()
    {
        var src = "x$ = \"hello\" ";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0)); // the ptr to the string in memory
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        
        vm.heap.Read(0, "hello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        
        Assert.That(str, Is.EqualTo("hello"));
    }
    [Test]
    public void String_Declare3()
    {
        var src = "x$ = \"hello\"; y$ = \"world\" ";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0)); // the ptr to the string in memory
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        Assert.That(vm.dataRegisters[1], Is.EqualTo("hello".Length*4)); // the ptr to the string in memory
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.STRING));

        vm.heap.Read(0, "hello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        
        Assert.That(str, Is.EqualTo("hello"));
        
        
        vm.heap.Read("hello".Length * 4, "world".Length * 4, out memory);
        str = VmConverter.ToString(memory);
        
        Assert.That(str, Is.EqualTo("world"));
    }

    
    
    [Test]
    public void TestDeclareAndAssign()
    {
        var src = "x as word; x = 3;";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.WORD));
    }

    
    [Test]
    public void TestDeclareAndAssignToExpression()
    {
        var src = "x as word; x = 3 + 3;";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(6));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.WORD));
    }
    
    
    
    [Test]
    public void Array_AssignSimple()
    {
        var src = @"
dim x(4) as byte
x(1) = 53
y as byte
y = x(1)
";
        // y as byte
        // y = x(1)
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        Assert.That(vm.heap.Cursor, Is.EqualTo(4));

        var heap = vm.heap;
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[3 /* dim register offset */], Is.EqualTo(53));
        Assert.That(vm.typeRegisters[3 /* dim register offset */], Is.EqualTo(TypeCodes.BYTE));

    }

    [Test]
    public void Array_Math()
    {
        var src = @"
dim x(4) as byte
x(1) = 53
y as byte
y = x(1)
";
        // y as byte
        // y = x(1)
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        Assert.That(vm.heap.Cursor, Is.EqualTo(4));

        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[3 /* dim register offset */], Is.EqualTo(53));
        Assert.That(vm.typeRegisters[3 /* dim register offset */], Is.EqualTo(TypeCodes.BYTE));

    }

    
    [Test]
    public void Array_Math2()
    {
        var src = @"
dim x(4) as word
x(0) = 1
x(1) = 3
x(2) = 5
x(3) = 7
y = (x(1) + x(0)) * x(2) * x(3) 
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        // Assert.That(vm.heap.Cursor, Is.EqualTo(4));

        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[3 /* dim register offset */], Is.EqualTo(140));
        Assert.That(vm.typeRegisters[3 /* dim register offset */], Is.EqualTo(TypeCodes.INT)); // int, because y is not declared as a byte, so it is an int by default

    }

    
    [Test]
    public void Array_Math3()
    {
        var src = @"
dim x(4)
x(0) = 2
x(1) = x(0) * 2
x(2) = x(1) * x(0)
x(3) = x(2) * x(1) * x(0)
y = x(3)
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        Assert.That(vm.heap.Cursor, Is.EqualTo(16));

        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[3 /* dim register offset */], Is.EqualTo(64));
        Assert.That(vm.typeRegisters[3 /* dim register offset */], Is.EqualTo(TypeCodes.INT)); // int, because y is not declared as a byte, so it is an int by default

    }
    
    [Test]
    public void Array_Math_Floats()
    {
        var src = @"
dim x#(4)
x#(0) = 1.2
y# = x#(0)
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        Assert.That(vm.heap.Cursor, Is.EqualTo(16));

        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        var outputRegisterValue = vm.dataRegisters[3 /* dim register offset */];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        float output = BitConverter.ToSingle(outputRegisterBytes, 0);
        Assert.That(output, Is.EqualTo(1.2f));

        Assert.That(vm.typeRegisters[3 /* dim register offset */], Is.EqualTo(TypeCodes.REAL)); // int, because y is not declared as a byte, so it is an int by default

    }




    
    [Test]
    public void Array_Create()
    {
        var src = @"
dim x(4)
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(16));
    }

    
    [Test]
    public void Array_Create_MultiDim()
    {
        var src = @"
dim x(4,2)
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(32));
    }

    
    [Test]
    public void Array_Create_MultiDim_Assign()
    {
        var src = @"
dim x(4,2) as word
x(1,1) = 42
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(16));
        
               
        vm.heap.Read((1*2 + 1) * 2, 2, out var bytes);
        var value = BitConverter.ToInt16(bytes, 0);
        Assert.That(value, Is.EqualTo(42));
    }

    [Test]
    public void Array_Create_MultiDim_Assign2()
    {
        var src = @"
dim x(4,5,3) as word
x(2,3,2) = 42
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(120));
        
               
        vm.heap.Read(( (2*5*3) + (3*3) + 2) * 2, 2, out var bytes);
        var value = BitConverter.ToInt16(bytes, 0);
        Assert.That(value, Is.EqualTo(42));
    }
    
    
    [Test]
    public void Array_Create_MultiDim_AssignRead()
    {
        var src = @"
dim x(4,5,3) as word
x(2,3,2) = 42
y as word
y = x(2,3,2)
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(120));
        
        Assert.That(vm.dataRegisters[1 + (3*2) /* 3 ranks, 2 registers per rank. */], Is.EqualTo(42));
        Assert.That(vm.typeRegisters[1 + (3*2)], Is.EqualTo(TypeCodes.WORD));
    }

    
    [Test]
    public void Array_Create_MultiDim_AssignRead2()
    {
        var src = @"
dim x(4,5,3) as byte
x(2,3,2) = 42
x(2,3,1) = 11
y = x(2,3,2) + x(2,3,1)
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(60));
        
        Assert.That(vm.dataRegisters[1 + (3*2) /* 3 ranks, 2 registers per rank. */], Is.EqualTo(53));
        Assert.That(vm.typeRegisters[1 + (3*2)], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Array_Create2()
    {
        var src = @"
dim x(4) as word
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(8));
    }

    
//     [Test]
//     public void Array_CreateAssign_ExpectIndexException()
//     {
//         var src = @"
// dim x(5) as word
// x(6) = 12
// ";
//         Setup(src, out _, out var prog);
//         
//         var vm = new VirtualMachine(prog);
//         var expectedMsg = "toast";
//         var called = false;
//         try
//         {
//             vm.Execute2();
//         }
//         catch (Exception ex)
//         {
//             called = true;
//             Assert.That(expectedMsg, Is.EqualTo(ex.Message));
//         }
//         Assert.IsTrue(called, "exception not called");
//     }

    
    [Test]
    public void Array_CreateAssign()
    {
        var src = @"
dim x(5) as word
x(1) = 12
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(10));
         
        vm.heap.Read(2, 2, out var bytes);
        var value = BitConverter.ToInt16(bytes, 0);
        Assert.That(value, Is.EqualTo(12));
    }

    
    [Test]
    public void Array_CreateAssign2()
    {
        var src = @"
dim x(5) as word
x(1) = 12
x(2) = 3 + 2
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(10));
         
        vm.heap.Read(2, 2, out var bytes);
        var value = BitConverter.ToInt16(bytes, 0);
        Assert.That(value, Is.EqualTo(12));
        
        vm.heap.Read(4, 2, out bytes);
        value = BitConverter.ToInt16(bytes, 0);
        Assert.That(value, Is.EqualTo(5));
    }

    
    [Test]
    public void Array_CreateAssignRead()
    {
        var src = @"
dim x(5) as word
x(1) = 12
x(2) = x(1) * 2
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(10));
         
        vm.heap.Read(2, 2, out var bytes);
        var value = BitConverter.ToInt16(bytes, 0);
        Assert.That(value, Is.EqualTo(12));
        
        vm.heap.Read(4, 2, out bytes);
        value = BitConverter.ToInt16(bytes, 0);
        Assert.That(value, Is.EqualTo(24));
    }

    
    [Test]
    public void TestAutoDeclareAndAssignToExpression()
    {
        var src = "x = 3 * 2";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(6));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }
    
    
    [Test]
    public void MultipleVariablesAndSuch()
    {
        var src = @"
x as word
x = 4.2
y = 2
x = x + y * 3
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(10));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.WORD));
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(2));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void MultipleAutoVariablesAndSuch()
    {
        var src = @"
x = 4
y = 2
x = x + y * 3
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(10));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(2));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }

    
    
    [Test]
    public void ExplicitFloat()
    {
        var src = @"x as float; x = 1.2";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        
        vm.Execute2();

        var outputRegisterValue = vm.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        float output = BitConverter.ToSingle(outputRegisterBytes, 0);
        
        Assert.That(output, Is.EqualTo(1.2f));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.REAL));
    }

    
    [Test]
    public void AutoFloat()
    {
        var src = @"x# = 1.2";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();

        var outputRegisterValue = vm.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        float output = BitConverter.ToSingle(outputRegisterBytes, 0);
        
        Assert.That(output, Is.EqualTo(1.2f));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.REAL));
    }

    
    [Test]
    public void AutoFloatMath()
    {
        var src = @"
x# = 5
y# = 1
z# = y# / x#
x# = z#
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();

        var outputRegisterValue = vm.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        float output = BitConverter.ToSingle(outputRegisterBytes, 0);
        
        Assert.That(output, Is.EqualTo(.2f));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.REAL));
    }

    
    [Test]
    public void TestDeclareAndAssignToExpressionToOverflow()
    {
        var src = "x as byte; x = 3 + 254;"; 
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.BYTE));
        Assert.That(vm.dataRegisters[0], Is.EqualTo((3 + 254) % 256));
    }
    
    [Test]
    public void Float_Mult()
    {
        var src = "x# = 3.2 * 1.5";
        Setup(src, out _, out var prog);
        // prog.Add(OpCodes.DBG_PRINT);
        var vm = new VirtualMachine(prog);
        vm.Execute2();

        var outputRegisterValue = vm.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        float output = BitConverter.ToSingle(outputRegisterBytes, 0);
        
        Assert.That(output, Is.EqualTo(3.2f * 1.5f));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.REAL));
    }
    
    
    [Test]
    public void Type_Instantiate()
    {
        var src = @"
type egg 
x
endtype

y as egg
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(4)); // size of the only field in egg, int, 4.
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
    }

    
    [Test]
    public void Type_Instantiate_Assign()
    {
        var src = @"
type egg 
x
endtype

y as egg
y.x = 53
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(4)); // size of the only field in egg, int, 4.
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        vm.heap.Read((int)vm.dataRegisters[0], 4, out var memory);
        var data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(53));
    }
    
    
    [Test]
    public void Type_Instantiate_AssignExpression()
    {
        var src = @"
type egg 
x
endtype

y as egg
y.x = 53
y.x = y.x + y.x
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(4)); // size of the only field in egg, int, 4.
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        vm.heap.Read((int)vm.dataRegisters[0], 4, out var memory);
        var data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(106));
    }

    
    [Test]
    public void Type_Instantiate_MultipleFields()
    {
        var src = @"
type egg 
x
z
endtype

y as egg
y.x = 53
y.z = 10
w = y.x + y.z
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(8)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[1], Is.EqualTo(63));
    }
     
    [Test]
    public void Type_Instantiate_StringField2()
    {
        var src = @"
type egg 
x
z$
endtype

y as egg
y.x = 50
y.z$ = ""hello""
w = y.x + len y.z$
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(4 + 4 + "hello".Length*4)); // a '4' comes from y.x, and the other '4' is the ptr to z$
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[1], Is.EqualTo(55));
    }
    
    
    [Test]
    public void Type_Instantiate_Nested()
    {
        var src = @"
type egg 
color
endtype

type chicken
e as egg
n 
endtype

albert as chicken
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(8));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        // Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        // Assert.That(vm.dataRegisters[1], Is.EqualTo(55));
    }

    
    [Test]
    public void Type_Instantiate_Nested_Assign()
    {
        var src = @"
type egg 
color
endtype

type chicken
e as egg
n 
endtype

albert as chicken
albert.e.color = 3
albert.n = 4
test = albert.e.color * albert.n
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(8));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[1], Is.EqualTo(12));
    }
    
    
    [Test]
    public void Type_Instantiate_Array()
    {
        var src = @"
TYPE egg 
    color
ENDTYPE

DIM x(3) AS egg
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(12));
        // Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
    }

    
    [Test]
    public void Type_Instantiate_Array_HalfSize()
    {
        var src = @"
TYPE egg 
    color AS WORD
ENDTYPE

DIM x(3) AS egg
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(6));
        // Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
    }
    
    [Test]
    public void Type_Instantiate_Array_Assign()
    {
        var src = @"
TYPE egg 
    color AS WORD
ENDTYPE

DIM x(3) AS egg
x(1).color = 3
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(6));
        
        vm.heap.Read(1 * 2, 2, out var mem);
        var data = BitConverter.ToInt16(mem, 0);
        
        Assert.That(data, Is.EqualTo(3));

    }
    
    
    [Test]
    public void Type_Instantiate_MultiArray_Assign()
    {
        var src = @"
TYPE egg 
    color AS WORD
ENDTYPE

DIM x(3,2) AS egg
x(1,1).color = 3
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(12));
        
        vm.heap.Read((1 + 2 * 1) * 2, 2, out var mem);
        var data = BitConverter.ToInt16(mem, 0);
        
        Assert.That(data, Is.EqualTo(3));

    }

    
    [Test]
    public void Type_Instantiate_ArrayMultiField_Assign()
    {
        var src = @"
TYPE egg 
    color AS WORD
    derp
ENDTYPE

DIM x(3) AS egg
x(2).derp = 3
y = x(2).derp
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(18));
        
        Assert.That(vm.dataRegisters[3], Is.EqualTo(3));

    }

    
    [Test]
    public void Type_Instantiate_MultiArray_MultiField_Assign()
    {
        var src = @"
TYPE egg 
    color AS WORD
    derp
ENDTYPE

DIM x(3,4) AS egg
x(2,3).derp = 3
y = x(2,3).derp
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(6*12));
        Assert.That(vm.dataRegisters[3], Is.EqualTo(3));
    }

    
    [Test]
    public void Type_Instantiate_ArrayMultiField_Assign2()
    {
        var src = @"
TYPE egg 
    color AS WORD
    derp
ENDTYPE

DIM x(3) AS egg
x(2).derp = 3
x(1).color = 2
y = x(2).derp * x(1).color
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(18));
        
        Assert.That(vm.dataRegisters[3], Is.EqualTo(6));

    }

    
    [Test]
    public void CallHost()
    {
        var src = "callTest";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
    }
    
    
    [Test]
    public void CallHost_RefType()
    {
        var src = "x = 7; refDbl x; x = x * 2";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(28));
    }
    
    
    [Test]
    public void CallHost_RefType_String()
    {
        var src = "x$ = \"a\"; tuna x$ ";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        vm.heap.Read((int)vm.dataRegisters[0], "tuna".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("tuna"));
    }

    
    [Test]
    public void CallHost_RefType_Int_FromArrayMulti()
    {
        var src = @"
dim x(3,2)
inc x(1,1)
y = x(1,1)   
";
  
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[5], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[5], Is.EqualTo(1));
    }

    
    [Test]
    public void CallHost_RefType_Int_FromArray()
    {
        var src = @"
dim x(3)
inc x(1)
y = x(1)   
";
  
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[3], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[3], Is.EqualTo(1));
    }

    
    [Test]
    public void CallHost_RefType_Int_FromArray2()
    {
        var src = @"
dim x(3)
inc x(1), 2
inc x(2), 3
refDbl x(1)
y = x(1) + x(2)
";
  
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[3], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[3], Is.EqualTo(7));
    }

    
    [Test]
    public void CallHost_RefType_String_FromArray()
    {
        var src = @"
dim x$(3)
tuna x$(1)
y$ = x$(1)      
";
  
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[3], Is.EqualTo(TypeCodes.STRING));
        vm.heap.Read((int)vm.dataRegisters[3], "tuna".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("tuna"));
    }

    
    [Test]
    public void CallHost_StringArg()
    {
        var src = "x = len \"hello\"";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo("hello".Length));
    }
    
    
    [Test]
    public void CallHost_StringArg_FromArray()
    {
        var src = @"
dim x$(3)
x$(1) = ""hello""
y = len x$(1)
z$ = reverse x$(1)
x$(2) = reverse x$(1)
w$ = x$(2)
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[3], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[3], Is.EqualTo("hello".Length));
        Assert.That(vm.typeRegisters[4], Is.EqualTo(TypeCodes.STRING));
        vm.heap.Read((int)vm.dataRegisters[4], "hello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("olleh"));
        
        Assert.That(vm.typeRegisters[5], Is.EqualTo(TypeCodes.STRING));
        vm.heap.Read((int)vm.dataRegisters[5], "hello".Length * 4, out memory);
        str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("olleh"));
    }

    
    [Test]
    public void CallHost_StringReturn()
    {
        var src = "x$ = reverse \"hello\"";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        vm.heap.Read((int)vm.dataRegisters[0], "hello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("olleh"));
    }
    
    [Test]
    public void CallHost_StringReturn_Assignment()
    {
        var src = "x$ = \"hello\"; y$ = reverse x$";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        vm.heap.Read((int)vm.dataRegisters[0], "hello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("hello"));
        
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.STRING));
        vm.heap.Read((int)vm.dataRegisters[1], "hello".Length * 4, out memory);
        str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("olleh"));
    }
    
    
    [Test]
    public void CallHost_StringReturn_Expression()
    {
        var src = "x$ = \"hello\"; y$ = \"world\" + reverse x$";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        vm.heap.Read((int)vm.dataRegisters[0], "hello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("hello"));
        
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.STRING));
        vm.heap.Read((int)vm.dataRegisters[1], "helloworld".Length * 4, out memory);
        str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("worldolleh"));
    }

    
    [Test]
    public void CallHost_RefType_AutoDeclare()
    {
        var src = "inc x;";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1));
    }
    
    [Test]
    public void CallHost_RefType_Optional_ButHasValue()
    {
        var src = "x = 7; inc x, 2";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(9));
    }
    
    [Test]
    public void CallHost_RefType_Optional()
    {
        var src = "x = 7; inc x";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(8));
    }

    [Test]
    public void CallHostAdd()
    {
        var src = "x = add 1,2";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3));
    }
    
    [Test]
    public void CallHost_OpOrder()
    {
        var src = "x = min 5,8 + 1";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(5));
    }
    
    
    [Test]
    public void CallHost_OpOrder2()
    {
        var src = "x = min 5, 8 * 2";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(5));
    }
    
    [Test]
    public void CallHost_OpOrder3()
    {
        var src = "x = (min 5, 8) * 2";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog)
        {
            hostMethods = compiler.methodTable
        };
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(10));
    }
    
    [Test]
    public void CallHost_OpOrder4()
    {
        var src = "x = 1 + min 5 8";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(6));
    }
}