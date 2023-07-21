using System.Diagnostics;
using DarkBasicYo;
using DarkBasicYo.Virtual;

namespace Tests;

public class TokenVm
{
    void Setup(string src, out Compiler compiler, out List<byte> progam)
    {
        var collection = TestCommands.Commands;
        var lexer = new Lexer();
        var tokens = lexer.Tokenize(src, collection);
        var parser = new Parser(new TokenStream(tokens), collection);
        var exprAst = parser.ParseProgram();

        compiler = new Compiler(collection);
        compiler.Compile(exprAst);
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