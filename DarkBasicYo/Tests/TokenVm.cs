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
    public void TestLiteralInt()
    {
        var src = "4293";
        Setup(src, out _, out var prog);
        
        Assert.That(prog.Count, Is.EqualTo(6)); // type code and 4 bytes for the int
        Assert.That(prog[0], Is.EqualTo(OpCodes.PUSH));
        Assert.That(prog[1], Is.EqualTo(TypeCodes.INT));
        Assert.That(prog[2], Is.EqualTo(0));
        Assert.That(prog[3], Is.EqualTo(0));
        Assert.That(prog[4], Is.EqualTo(16));
        Assert.That(prog[5], Is.EqualTo(197));
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

    
    [Test]
    public void Array_CreateAssign_ExpectIndexException()
    {
        var src = @"
dim x(5) as word
x(6) = 12
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        var expectedMsg = "toast";
        var called = false;
        try
        {
            vm.Execute2();
        }
        catch (Exception ex)
        {
            called = true;
            Assert.That(expectedMsg, Is.EqualTo(ex.Message));
        }
        Assert.IsTrue(called, "exception not called");
        //
        // Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        // Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        //
        // Assert.That(vm.heap.Cursor, Is.EqualTo(10));
        //  
        // vm.heap.Read(2, 2, out var bytes);
        // var value = BitConverter.ToInt16(bytes, 0);
        // Assert.That(value, Is.EqualTo(12));
    }

    
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
    public void TestIntAdd()
    {
        var src = "4 + 9";
        Setup(src, out _, out var prog);
        
        Assert.That(prog.Count, Is.EqualTo(13)); // type code and 4 bytes for the int
        Assert.That(prog[0], Is.EqualTo(OpCodes.PUSH));
        Assert.That(prog[1], Is.EqualTo(TypeCodes.INT));
        Assert.That(prog[2], Is.EqualTo(0));
        Assert.That(prog[3], Is.EqualTo(0));
        Assert.That(prog[4], Is.EqualTo(0));
        Assert.That(prog[5], Is.EqualTo(4));
        Assert.That(prog[6], Is.EqualTo(OpCodes.PUSH));
        Assert.That(prog[7], Is.EqualTo(TypeCodes.INT));
        Assert.That(prog[8], Is.EqualTo(0));
        Assert.That(prog[9], Is.EqualTo(0));
        Assert.That(prog[10], Is.EqualTo(0));
        Assert.That(prog[11], Is.EqualTo(9));
        Assert.That(prog[12], Is.EqualTo(OpCodes.ADD));
    }
    
    
    [Test]
    public void TestIntMul()
    {
        var src = "4 * 9";
        Setup(src, out _, out var prog);

        Assert.That(prog.Count, Is.EqualTo(13)); // type code and 4 bytes for the int
        Assert.That(prog[0], Is.EqualTo(OpCodes.PUSH));
        Assert.That(prog[1], Is.EqualTo(TypeCodes.INT));
        Assert.That(prog[2], Is.EqualTo(0));
        Assert.That(prog[3], Is.EqualTo(0));
        Assert.That(prog[4], Is.EqualTo(0));
        Assert.That(prog[5], Is.EqualTo(4));
        Assert.That(prog[6], Is.EqualTo(OpCodes.PUSH));
        Assert.That(prog[7], Is.EqualTo(TypeCodes.INT));
        Assert.That(prog[8], Is.EqualTo(0));
        Assert.That(prog[9], Is.EqualTo(0));
        Assert.That(prog[10], Is.EqualTo(0));
        Assert.That(prog[11], Is.EqualTo(9));
        Assert.That(prog[12], Is.EqualTo(OpCodes.MUL));

        prog.Add(OpCodes.DBG_PRINT);
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        var output = vm.ReadStdOut();
        Assert.That(output, Is.EqualTo($"{TypeCodes.INT} - 36\n"));
    }
    
    
    [Test]
    public void OperatorOrder_1()
    {
        var src = "1 + 2 * 3";
        Setup(src, out _, out var prog);
        prog.Add(OpCodes.DBG_PRINT);
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        var output = vm.ReadStdOut();
        Assert.That(output, Is.EqualTo($"{TypeCodes.INT} - 7\n"));
    }
    
    [Test]
    public void OperatorOrder_2()
    {
        var src = "1 * 2 + 3";
        Setup(src, out _, out var prog);
        prog.Add(OpCodes.DBG_PRINT);
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        var output = vm.ReadStdOut();
        Assert.That(output, Is.EqualTo($"{TypeCodes.INT} - 5\n"));
    }
    
    [Test]
    public void OperatorOrder_3()
    {
        var src = "5 * ((2 + 3) * 3)";
        Setup(src, out _, out var prog);
        prog.Add(OpCodes.DBG_PRINT);
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        var output = vm.ReadStdOut();
        Assert.That(output, Is.EqualTo($"{TypeCodes.INT} - 75\n"));
    }
    
    
    [Test]
    public void Float()
    {
        var src = "3.2";
        Setup(src, out _, out var prog);
        prog.Add(OpCodes.DBG_PRINT);
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        var output = vm.ReadStdOut();
        Assert.That(output, Is.EqualTo($"{TypeCodes.REAL} - 3.2\n"));
    }
    
    
    [Test]
    public void Float_Addition()
    {
        var src = "3.2 + 1.5";
        Setup(src, out _, out var prog);
        prog.Add(OpCodes.DBG_PRINT);
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        var output = vm.ReadStdOut();
        Assert.That(output, Is.EqualTo($"{TypeCodes.REAL} - 4.7\n"));
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
    public void FloatIntAddition()
    {
        var src = "3.2 + 1";
        Setup(src, out _, out var prog);
        prog.Add(OpCodes.DBG_PRINT);
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        var output = vm.ReadStdOut();
        Assert.That(output, Is.EqualTo($"{TypeCodes.REAL} - 4.2\n"));
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
    public void CallHostAdd()
    {
        var src = "x = add 1 2";
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
        var src = "x = min 5 8 + 1";
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
        var src = "x = min 5 8 * 2";
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
        var src = "x = (min 5 8) * 2";
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