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
x$ = demo()
function demo()
endfunction ""igloo""
", 1)]
    [TestCase(@"
x$ = demo(1)
x$ = demo(2)
function demo(n)
endfunction str$(n)
", 1)]
    [TestCase(@"
demo()
function demo()
    z$ = ""toast""
endfunction
", 1)]
    [TestCase(@"
type vec
    x
    y
endtype
v = demo() ` 1 allocation to assign
function demo()
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
    [TestCase(@"
tuna_echo 1, a$
", 2)]
    [TestCase(@"
tuna_echo 1, a$
a$ = ""toast""
", 2)]
    [TestCase(@"
a$ = ""toast""
tuna_echo2 0, a$ `no assignment needed
tuna_echo2 1, a$ `1 assignment needed
", 2)]
    [TestCase(@"
len(""toast"")
", 1)]
    [TestCase(@"
len(""toast"")
len(""toast"")
len(""toast"")
", 1)]
    [TestCase(@"
len(""toast"")
len(""toast"")
len(""toast"")
", 1)]
    [TestCase(@"
tuna_opt_string 3 `an empty optional does not need to allocate
", 0)]
    public void GC_Simple(string src, int allocationCount)
    {
        Setup(src, out var compiler, out var prog);

        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.sweepInterval = 1; // GC tests exercise the sweep-on-every-store path

        vm.Execute2(0);
        vm.CollectGarbage();

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
        vm.sweepInterval = 1; // this test guards against stale copies of freed memory

        vm.Execute2(0);
        vm.CollectGarbage();

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
        vm.sweepInterval = 1; // GC tests exercise the sweep-on-every-store path

        vm.Execute2(0);
        vm.CollectGarbage();

        Assert.That(vm.heap.Allocations, Is.EqualTo(allocationCount));
    }

    // Invariant guard: a global heap variable keeps its pointer flag after a
    // function with local scalar stores runs. (The STORE opcode was writing
    // globalScope.flags[addr] instead of scope.flags[addr]; harmless in
    // practice because register addresses are globally unique so a local's addr
    // never aliases a live global's, but the assignment is now to the correct
    // scope. This test documents the invariant it must not regress.)
    [Test]
    public void Store_GlobalPointerFlag_SurvivesFunctionLocals()
    {
        var src = @"
g$ = ""hello""
clobber()
function clobber()
    junk = 42
endfunction
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog) { hostMethods = compiler.methodTable };
        vm.Execute2(0);
        Assert.That(VirtualScope.IsPtr(vm.globalScope.flags[0]), Is.True,
            $"global string lost its pointer flag (globalScope.flags[0]={vm.globalScope.flags[0]})");
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