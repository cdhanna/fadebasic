using System.Numerics;
using DarkBasicYo.Virtual;
using RosylnExample;

namespace Tests;

public class VMTests
{
    [SetUp]
    public void Setup()
    {
        TestMethods.wasCalled = false;
        TestMethods.someInt = 0;
    }
    
    [Test]
    public void SimplePush()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.WORD, 0b01, 0b01, // a 1 in the 9th's place is 512
            OpCodes.DBG_PRINT, 
        });
        
        var state = vm.Execute();
        var res = state.MoveNext();

        var str = vm.ReadStdOut();
        Assert.That(str, Is.EqualTo("4 - 257\n"));
        Assert.IsTrue(state.Current.isComplete);
        // Assert.That(vm.stack.Peek(), Is.EqualTo(512));
    }

    [Test]
    public void SimpleAdding()
    {
        
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.WORD, 1, 0,
            OpCodes.PUSH, TypeCodes.WORD, 0, 5,
            OpCodes.ADD, 
            OpCodes.DBG_PRINT
        });
        
        var state = vm.Execute();
        var res = state.MoveNext();
        
        Assert.IsTrue(state.Current.isComplete);
        
        var str = vm.ReadStdOut();
        Assert.That(str, Is.EqualTo("4 - 261\n"));
    }
    
    [Test]
    public void SimpleMultiplication()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.WORD, 0, 2, // 256 * 0 + 2
            OpCodes.PUSH, TypeCodes.WORD, 1, 2, // 256 * 1 + 2
            OpCodes.MUL, 
            OpCodes.DBG_PRINT
        });
        
        var state = vm.Execute();
        var res = state.MoveNext();
        
        Assert.IsTrue(state.Current.isComplete);
        var str = vm.ReadStdOut();
        Assert.That(str, Is.EqualTo($"4 - {(2) * (256 + 2)}\n"));
    }
    
    
    [Test]
    public void SimpleMultiplication_WordOverflow()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.WORD, 1, 2, // 256 * 1 + 2
            OpCodes.PUSH, TypeCodes.WORD, 1, 2, // 256 * 1 + 2

            OpCodes.MUL, 
            OpCodes.DBG_PRINT
        });
        
        var state = vm.Execute();
        var res = state.MoveNext();
        
        Assert.IsTrue(state.Current.isComplete);
        var str = vm.ReadStdOut();
        
        // the actual product is 66,564
        // but the value of Word is 65,536,
        // so if it wraps correctly, we should see 66,564 - 65,536 = 1028
        Assert.That(str, Is.EqualTo($"4 - 1028\n"));
    }
    
    
    [Test]
    public void Store()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.WORD, 0, 1, // 1
            OpCodes.STORE, Registers.R0, // store in register 1
        });
        
        var state = vm.Execute();
        var res = state.MoveNext();
        
        Assert.IsTrue(state.Current.isComplete);
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.WORD));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1));
    }
    
    
    [Test]
    public void StoreLoadAdd()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.WORD, 0, 1, // 1
            OpCodes.STORE, Registers.R0, // store in register 1
            OpCodes.LOAD, Registers.R0, // pushes 1 onto the stack
            OpCodes.LOAD, Registers.R0, // pushes 1 onto the stack again
            OpCodes.ADD,
            OpCodes.STORE, Registers.R1
        });
        
        var state = vm.Execute();
        var res = state.MoveNext();
        
        Assert.IsTrue(state.Current.isComplete);
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.WORD));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1));
        
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.WORD));
        Assert.That(vm.dataRegisters[1], Is.EqualTo(2));
    }

    
    [Test]
    public void StoreLoadAdd2()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.WORD, 0, 3, // 2
            OpCodes.STORE, Registers.R0, // store in register 1
            OpCodes.LOAD, Registers.R0, // pushes 3 onto the stack
            OpCodes.LOAD, Registers.R0, // pushes 3 onto the stack again
            OpCodes.ADD,
            OpCodes.STORE, Registers.R1,
            OpCodes.LOAD, Registers.R0, // pushes 3 onto the stack again
            OpCodes.LOAD, Registers.R1, // pushes 6 onto the stack again
            OpCodes.MUL,
            OpCodes.STORE, Registers.R0, // stores the result in r0
        });
        
        var state = vm.Execute();
        var res = state.MoveNext();
        
        Assert.IsTrue(state.Current.isComplete);
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.WORD));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(18));
        
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.WORD));
        Assert.That(vm.dataRegisters[1], Is.EqualTo(6));
    }

    
    [Test]
    public void CastIntToWord()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 0, 3, // 3
            OpCodes.CAST, TypeCodes.WORD,
            OpCodes.STORE, Registers.R0
        });
        
        var state = vm.Execute();
        var res = state.MoveNext();
        
        Assert.IsTrue(state.Current.isComplete);
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.WORD));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3));
        
    }
    
    [Test]
    public void Simple_TypeSize_Expansion()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.BYTE, 1, // 1
            OpCodes.PUSH, TypeCodes.WORD, 120, 10, // 256 * 254 + 10

            OpCodes.ADD, 
            OpCodes.DBG_PRINT
        });
        
        var state = vm.Execute();
        var res = state.MoveNext();
        
        Assert.IsTrue(state.Current.isComplete);
        var str = vm.ReadStdOut();
        
        // adding a byte and a word together, the resulting math should take on the larger size
        Assert.That(str, Is.EqualTo($"4 - 30731\n"));
    }
    
    [Test]
    public void Simple_TypeSize_Expansion_OrderFlipped()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.WORD, 120, 10, // 256 * 254 + 10
            OpCodes.PUSH, TypeCodes.BYTE, 1, // 1

            OpCodes.ADD, 
            OpCodes.DBG_PRINT
        });
        
        var state = vm.Execute();
        var res = state.MoveNext();
        
        Assert.IsTrue(state.Current.isComplete);
        var str = vm.ReadStdOut();
        
        // adding a byte and a word together, the resulting math should take on the larger size
        Assert.That(str, Is.EqualTo($"4 - 30731\n"));
    }

    [Test]
    public void Bn_Float_Add()
    {
        float a = 1.2f;
        float b = 3.5f;

        var aBytes = BitConverter.GetBytes(a);
        var bBytes = BitConverter.GetBytes(b);

        var ai = new BigInteger(aBytes);
        var bi = new BigInteger(bBytes);

        var i = ai + bi;
        var iBytes = i.ToByteArray();

        var c = BitConverter.ToSingle(aBytes, 0);
        var d = BitConverter.ToSingle(bBytes, 0);
        
        Assert.That(c+d, Is.EqualTo(a + b));
    }
    
    
    [Test]
    public void SimpleAlloc()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 0, 4, 
            OpCodes.ALLOC, 
            OpCodes.STORE, Registers.R0,
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 0, 4, 
            OpCodes.ALLOC, 
            OpCodes.STORE, Registers.R1,
        });

        vm.Execute2();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[1], Is.EqualTo(4));
    }
    
    
    [Test]
    public void SimpleAlloc_AndWrite()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.WORD, 0, 3, // push a word onto the stack (no type code)
            OpCodes.PUSH, TypeCodes.WORD, 0, 9, // push a second word onto the stack (no type code)
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 0, 6, // push a length of 4 (bytes) onto the stack
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 0, 6, // push a length of 4 (bytes) onto the stack
            OpCodes.ALLOC, // allocate 6 bytes (pop)
            OpCodes.STORE, Registers.R0,
            OpCodes.LOAD, Registers.R0,
            OpCodes.WRITE, // write the next 6 bytes (pop, pop-pop-pop-pop)
            
        });

        vm.Execute2();

        var mem = vm.heap.memory;
        Assert.That(mem[0], Is.EqualTo(4));
        Assert.That(mem[1], Is.EqualTo(9));
        Assert.That(mem[2], Is.EqualTo(0));
        
        Assert.That(mem[3], Is.EqualTo(4));
        Assert.That(mem[4], Is.EqualTo(3));
        Assert.That(mem[5], Is.EqualTo(0));
    }

    
    [Test]
    public void SimpleAlloc_AndWrite_AndRead()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.WORD, 0, 3, // push a word onto the stack (no type code)
            OpCodes.PUSH, TypeCodes.WORD, 0, 9, // push a second word onto the stack (no type code)
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 0, 6, // push a length of 4 (bytes) onto the stack
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 0, 6, // push a length of 4 (bytes) onto the stack
            OpCodes.ALLOC, // allocate 6 bytes (pop)
            OpCodes.STORE, Registers.R0, // save the address of the data to r0
            OpCodes.LOAD, Registers.R0, // load the address back up
            OpCodes.WRITE, // write the next 6 bytes (pop, pop-pop-pop-pop)
  
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 0, 6, // push a length of 4 (bytes) onto the stack
            OpCodes.LOAD, Registers.R0, // load the address back up
            OpCodes.READ, // read the data out of the heap
            OpCodes.ADD, // add the stack values
            OpCodes.STORE, Registers.R1 // store the sum in register 1
            
        });

        vm.Execute2();
        
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.WORD));
        Assert.That(vm.dataRegisters[1], Is.EqualTo(12));
        
        var mem = vm.heap.memory;
        Assert.That(mem[0], Is.EqualTo(4));
        Assert.That(mem[1], Is.EqualTo(9));
        Assert.That(mem[2], Is.EqualTo(0));
        
        Assert.That(mem[3], Is.EqualTo(4));
        Assert.That(mem[4], Is.EqualTo(3));
        Assert.That(mem[5], Is.EqualTo(0));
    }

    
    [Test]
    public void SimpleHost_Call_NoArgsNoNothing()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 0, 1, // push the address onto the stack
            OpCodes.CALL_HOST, 
        });

        var method = HostMethodUtil.BuildHostMethodViaReflection(typeof(TestMethods), nameof(TestMethods.Test));
        vm.hostMethods.methods = new HostMethod[]
        {
            null, // fake out
            method
        };
        vm.Execute2();
        
        Assert.IsTrue(TestMethods.wasCalled);
    }

    
    [Test]
    public void SimpleHost_Call_WithInt()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 1, 10, // push the value for the int arg
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 0, 1, // push the address onto the stack
            OpCodes.CALL_HOST, 
        });

        var method = HostMethodUtil.BuildHostMethodViaReflection(typeof(TestMethods), nameof(TestMethods.TestWithInt));
        vm.hostMethods.methods = new HostMethod[]
        {
            null, // fake out
            method
        };
        vm.Execute2();
        
        Assert.IsTrue(TestMethods.wasCalled);
        Assert.That(TestMethods.someInt, Is.EqualTo(266));
    }
    
    
    [Test]
    public void SimpleHost_Call_WithReturnInt()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 1, 10, // push the value for the int arg
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 0, 1, // push the address onto the stack
            OpCodes.CALL_HOST, 
            OpCodes.STORE, Registers.R0
        });

        var method = HostMethodUtil.BuildHostMethodViaReflection(typeof(TestMethods), nameof(TestMethods.TestWithReturnValue));
        vm.hostMethods.methods = new HostMethod[]
        {
            null, // fake out
            method
        };
        vm.Execute2();
        
        Assert.IsTrue(TestMethods.wasCalled);
       
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3));
    }
    
    
    [Test]
    public void SimpleHost_Call_WithIntAndReturnInt()
    {
        var vm = new VirtualMachine(new List<byte>
        {
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 1, 10, // push the value for the int arg
            OpCodes.PUSH, TypeCodes.INT, 0, 0, 0, 0, // push the address onto the stack
            OpCodes.CALL_HOST, 
            OpCodes.STORE, Registers.R0
        });

        var method = HostMethodUtil.BuildHostMethodViaReflection(typeof(TestMethods), nameof(TestMethods.TestWithDoublesTheInt));
        vm.hostMethods.methods = new HostMethod[]
        {
            method
        };
        vm.Execute2();
        
        Assert.IsTrue(TestMethods.wasCalled);
       
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(266*2));
    }
}

public static class TestMethods
{
    public static bool wasCalled;
    public static int someInt;
    public static void Test()
    {
        wasCalled = true;
    }

    public static void TestWithInt(int a)
    {
        someInt = a;
        wasCalled = true;
    }
    
    public static int TestWithReturnValue()
    {
        wasCalled = true;
        return 3;
    }
    
    public static int TestWithDoublesTheInt(int a)
    {
        wasCalled = true;
        return a * 2;
    }
}