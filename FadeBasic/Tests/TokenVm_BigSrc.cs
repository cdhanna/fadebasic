using System.Text;
using FadeBasic.Virtual;

namespace Tests;

public partial class TokenVm
{

    [Test]
    public void BigSrc_GiantHeap()
    {
        var src = @"
TYPE egg
    x#, y#
ENDTYPE
e as egg
e.x# = 1.2
e.y# = 3.4

";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        
        // allocation a bunch of memory
        vm.heap.Allocate(ref HeapTypeFormat.STRING_FORMAT, (int)VmPtr.MAX_ARRAY_SIZE - 1, out var dumbPtr);
        
        vm.Execute2(0);

        var expectedPtr = new VmPtr
        {
            bucketPtr = 1,
            memoryPtr = 0
        };
        if (!vm.heap.TryGetAllocation(expectedPtr, out var allocation))
        {
            Assert.Fail("pointer is not at expected location");
        }
        
        Assert.That(allocation.length, Is.EqualTo(8)); // 2 floats
    }

    
    [Test]
    public void BigSrc_LotsOfVariables()
    {
        var sb = new StringBuilder();
        var max = short.MaxValue / 2; // this number cannot get to big, because the string-builder explodes. 
        for (int i = 0; i < max; i++)
        {
            sb.AppendLine($"x_{i} = {i}");
        }

        var src = sb.ToString();
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        for (int i = 0; i < max; i++)
        {
            Assert.That(vm.dataRegisters[i], Is.EqualTo(i), $"variable at reg {i} is wrong.");

        }
    }
    
}