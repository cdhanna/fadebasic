using System.Numerics;
using DarkBasicYo.Virtual;
using RosylnExample;

namespace Tests;

public class VMTests
{
    
    
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
}