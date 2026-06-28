using FadeBasic.Virtual;

namespace Tests;

public partial class TokenVm
{
    void RunSource(string src, out VirtualMachine vm)
    {
        Setup(src, out var compiler, out var prog);
        vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);
    }

    // ── len ──────────────────────────────────────────────────────────────

    [Test]
    public void Len_String_CharCount()
    {
        RunSource(@"
static print len(""toast"")
", out _);
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo("5"));
    }

    [Test]
    public void Len_Array_TopDimension()
    {
        RunSource(@"
dim x(10)
static print len(x)
", out _);
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo("10"));
    }

    [Test]
    public void Len_MultiDim_PerDimension()
    {
        // dimensions are zero-indexed, matching array indexing.
        RunSource(@"
dim g(7,3)
static print len(g)
static print len(g, 0)
static print len(g, 1)
", out _);
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo("7"));
        Assert.That(TestCommands.staticPrintBuffer[1], Is.EqualTo("7"));
        Assert.That(TestCommands.staticPrintBuffer[2], Is.EqualTo("3"));
    }

    [Test]
    public void Len_StructArray_ElementCount()
    {
        // rect is 16 bytes; the old allocation-derived len divided by the
        // ptr size (8) and reported 10 for a 5-element array. The
        // rank-register len is element-size independent.
        RunSource(@"
type rect
    a
    b
    c
    d
endtype
dim r(5) as rect
static print len(r)
", out _);
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo("5"));
    }

    [Test]
    public void Len_RuntimeDimension()
    {
        // `for k = 0 to dims(g)` is the canonical every-dimension loop.
        RunSource(@"
dim g(7,3)
for k = 0 to dims(g)
    static print len(g, k)
next k
", out _);
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo("7"));
        Assert.That(TestCommands.staticPrintBuffer[1], Is.EqualTo("3"));
    }

    [Test]
    public void Len_RuntimeDimension_DoesNotReevaluateExpression()
    {
        // the dimension expression must be spilled and evaluated exactly
        // once, even though the branchless select reads it per rank.
        RunSource(@"
dim g(7,3)
global k
k = 0
static print len(g, demo())
static print k
function demo()
    k = k + 1
endfunction 1
", out _);
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo("3"));
        Assert.That(TestCommands.staticPrintBuffer[1], Is.EqualTo("1"));
    }

    [TestCase(2)]
    [TestCase(-1)]
    public void Len_RuntimeDimension_OutOfRange_IsFatal(int dimension)
    {
        var src = $@"
dim g(7,3)
k = {dimension}
n = len(g, k)
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;

        var ex = Assert.Throws<VirtualRuntimeException>(() => { vm.Execute2(); });
        Assert.That(ex.Error.type, Is.EqualTo(VirtualRuntimeErrorType.INVALID_ADDRESS));
    }

    [TestCase(@"
dim g(7,3)
n = len(g, -1)
")] // constant dimension below range
    [TestCase(@"
dim g(7,3)
n = len(g, 2)
")] // constant dimension above range (zero-indexed: valid are 0 and 1)
    [TestCase(@"
x$ = ""toast""
n = len(x$, 2)
")] // strings have no dimensions
    [TestCase(@"
type vec
    x
    y
endtype
v as vec
n = len(v)
")] // len of a struct is meaningless; bytes() is the right tool
    public void Len_InvalidUsage_IsParseError(string src)
    {
        Setup(src, out _, out _, expectedParseErrors: 1);
    }

    // ── dims ─────────────────────────────────────────────────────────────

    [Test]
    public void Dims_HighestDimensionIndex()
    {
        // dims is the highest valid index for len(arr, k) — zero-indexed,
        // so a 1D array reports 0 and a 2D array reports 1.
        RunSource(@"
dim x(10)
dim g(7,3)
static print dims(x)
static print dims(g)
", out _);
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo("0"));
        Assert.That(TestCommands.staticPrintBuffer[1], Is.EqualTo("1"));
    }

    [TestCase(@"
x$ = ""toast""
n = dims(x$)
")]
    [TestCase(@"
n = dims(5)
")]
    public void Dims_InvalidUsage_IsParseError(string src)
    {
        Setup(src, out _, out _, expectedParseErrors: 1);
    }

    // ── bytes ────────────────────────────────────────────────────────────

    [Test]
    public void Bytes_Variables_And_TypeNames()
    {
        RunSource(@"
type vec
    x
    y
endtype
v as vec
n = 5
s$ = ""toast""
dim arr(10)
static print bytes(vec)
static print bytes(v)
static print bytes(n)
static print bytes(s$)
static print bytes(arr)
", out _);
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo("8"));  // type name: 2 int fields
        Assert.That(TestCommands.staticPrintBuffer[1], Is.EqualTo("8"));  // struct variable
        Assert.That(TestCommands.staticPrintBuffer[2], Is.EqualTo("4"));  // int variable
        Assert.That(TestCommands.staticPrintBuffer[3], Is.EqualTo("20")); // 5 chars x 4 bytes
        Assert.That(TestCommands.staticPrintBuffer[4], Is.EqualTo("40")); // 10 ints x 4 bytes
    }

    [Test]
    public void Bytes_StructArray_TotalBytes()
    {
        RunSource(@"
type rect
    a
    b
    c
    d
endtype
dim r(5) as rect
static print bytes(r)
static print bytes(rect)
static print len(r)
", out _);
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo("80")); // 5 x 16
        Assert.That(TestCommands.staticPrintBuffer[1], Is.EqualTo("16"));
        Assert.That(TestCommands.staticPrintBuffer[2], Is.EqualTo("5"));
    }

    [Test]
    public void Bytes_Expressions()
    {
        // arbitrary expressions are legal: they evaluate (side effects run
        // exactly once) and the size of the result type is reported.
        RunSource(@"
global counter
counter = 0
static print bytes(1 + 2)
static print bytes(demo())
static print counter
static print bytes(""ab"" + ""c"")
function demo()
    counter = counter + 1
endfunction 42
", out _);
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo("4"));  // int expression
        Assert.That(TestCommands.staticPrintBuffer[1], Is.EqualTo("4"));  // int-returning call
        Assert.That(TestCommands.staticPrintBuffer[2], Is.EqualTo("1"));  // ...evaluated exactly once
        Assert.That(TestCommands.staticPrintBuffer[3], Is.EqualTo("12")); // 3 chars x 4 bytes
    }

    [Test]
    public void Bytes_StructReturningCall()
    {
        RunSource(@"
type vec
    x
    y
endtype
static print bytes(makeVec())
function makeVec()
    v as vec
endfunction v
", out _);
        Assert.That(TestCommands.staticPrintBuffer[0], Is.EqualTo("8"));
    }

    [TestCase(@"
n = bytes(doesNotExist)
")] // neither a variable nor a type
    [TestCase(@"
n = bytes(demo())
function demo()
endfunction
")] // void expressions have no size
    public void Bytes_InvalidUsage_IsParseError(string src)
    {
        Setup(src, out _, out _, expectedParseErrors: 1);
    }
}
