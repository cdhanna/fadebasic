using FadeBasic.Virtual;

namespace Tests;

public partial class TokenVm
{
    
    [TestCase(
        @"
x$ = ""toast""
for n = 1 to 5
    x$ = x$ + x$
next n
", 1)]
    [TestCase(
        @"
x$ = ""toast""
x$ = ""frank""
", 1)]
    [TestCase(
        @"
x$ = ""toast""
x$ = ""frank""
x$ = ""billy""
", 1)]
    [TestCase(
        @"
type vec
    x
    y
endtype
v as vec
v2 = v
v3 = v2
", 3)]
    [TestCase(@"
x$ = test()
function test()
endfunction ""igloo""
", 1)]
    [TestCase(@"
x$ = test(1)
x$ = test(2)
function test(n)
endfunction str$(n)
", 1)]
    [TestCase(@"
test()
function test()
    z$ = ""toast""
endfunction
", 0)]
    [TestCase(@"
type vec
    x
    y
endtype
v = test() ` 1 allocation to assign
function test()
    v2 as vec 
    v3 as vec
endfunction v2 
", 1)]
    [TestCase(@"
LOCAL DIM x(10)
x(1) = 4
", 1)]
    [TestCase(@"
localArr()
FUNCTION localArr()
    LOCAL DIM x(10)
ENDFUNCTION
", 0)]
    public void GC_Simple(string src, int allocationCount)
    {
        Setup(src, out var compiler, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;

        vm.Execute2(0);
        vm.heap.Sweep();

        Assert.That(vm.heap.Allocations, Is.EqualTo(allocationCount));
    }


    [Test]
    public void String_Interning()
    {
        var src = @"
x$ = ""toast""
y$ = ""toast""
";
        Setup(src, out var compiler, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;

        vm.Execute2(0);

        var xPtr = vm.dataRegisters[0];
        var yPtr = vm.dataRegisters[1];
        Assert.That(xPtr, Is.EqualTo(0));
        Assert.That(yPtr, Is.EqualTo(xPtr));
        // vm.heap.Sweep();
    }

}