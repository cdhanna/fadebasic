using FadeBasic.Virtual;
using Microsoft.VisualStudio.TestPlatform.Utilities;

namespace Tests;

public partial class TokenVm
{
    
    [TestCase(
        @"
x$ = ""toast""
for n = 1 to 5
    x$ = x$ + x$
next n
", 2)]
    [TestCase(
        @"
x$ = ""toast""
x$ = ""frank""
", 2)]
    [TestCase(
        @"
x$ = ""toast""
x$ = ""frank""
x$ = ""billy""
", 3)]
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
", 1)]
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
    [TestCase(@"
blargh(""toast"")
FUNCTION blargh(x$)
    print x$
ENDFUNCTION
", 1)]
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
    public void GC_ArraysDontRuinCopiedOutData()
    {
        var src = @"
type egg
 z
endtype

global x as egg
global x2 as egg
for n = 0 to 2
    dim a(5) as egg
    a(3) = { z = 50+n }
    if (n = 0)
        x = a(3)
    endif
    if (n = 2)
        x2 = a(3)
    endif
next

q = x.z
q2 = x2.z

x3 = a(3)
q3 = x3.z
";
        Setup(src, out var compiler, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;

        vm.Execute2(0);
        vm.heap.Sweep();

        var v = vm.dataRegisters[6];
        var v2 = vm.dataRegisters[7];
        var v3 = vm.dataRegisters[9];
        
        Assert.That(v, Is.EqualTo(50));
        Assert.That(v2, Is.EqualTo(52));
        Assert.That(v3, Is.EqualTo(52));
        
        Assert.That(vm.heap.Allocations, Is.EqualTo(4)); // left over array, 3 eggs
    }

    [TestCase(@"
for n = 1 to 100
    ` concat of literals is fine
    x$ = ""a"" + ""b""
next 
", 3)]
    [TestCase(@"
for n = 1 to 100
    ` just accessing string is fine
    x$ = str$(1)
next 
", 1)]
    [TestCase(@"
for n = 1 to 100
    ` concat of returned is not?
    x$ = str$(1) + str$(2)
next 
", 1)]
    public void GC_Simple_StringConcatIssues(string src, int allocationCount)
    {
        Setup(src, out var compiler, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;

        vm.Execute2(0);
        vm.heap.Sweep();

        Assert.That(vm.heap.Allocations, Is.EqualTo(allocationCount));
    }

    [TestCase("toast", "toast")]
    [TestCase("/", "/")]
    [TestCase("\\\\", "\\")]
    public void String_Interning(string str, string expected)
    {
        var src = $@"
x$ = ""{str}""
y$ = ""{str}""
";
        Setup(src, out var compiler, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;

        vm.Execute2(0);

        var xPtr = vm.dataRegisters[0];
        var yPtr = vm.dataRegisters[1];
        Assert.That(xPtr, Is.EqualTo(0));
        Assert.That(yPtr, Is.EqualTo(xPtr));
        
        vm.heap.Read(xPtr.ToPtr(), expected.Length * 4, out var memory);
        var actual = VmConverter.ToString(memory);
        Assert.That(actual, Is.EqualTo(expected));

    }

}