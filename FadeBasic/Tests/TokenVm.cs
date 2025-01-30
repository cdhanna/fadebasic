using System.Diagnostics;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace Tests;

public partial class TokenVm
{
    private ProgramNode? _exprAst;
    public static DebugData SingletonDebugData;

    void Setup(string src, out Compiler compiler, out List<byte> progam, int? expectedParseErrors=null, bool generaeteDebug=false, bool ignoreParseCheck=false)
    {
        var collection = TestCommands.CommandsForTesting;
        var lexer = new Lexer();
        var tokens = lexer.Tokenize(src, collection);
        var parser = new Parser(new TokenStream(tokens), collection);
        _exprAst = parser.ParseProgram();
        if (expectedParseErrors.HasValue)
        {
            _exprAst.AssertParseErrors(expectedParseErrors.Value);
            if (expectedParseErrors > 0)
            {
                compiler = null;
                progam = new List<byte>();
                return;
            }
        }
        else if (!ignoreParseCheck)
        {
            _exprAst.AssertNoParseErrors();
        }
        
        compiler = new Compiler(collection, new CompilerOptions
        {
            GenerateDebugData = generaeteDebug
        });
        
        compiler.Compile(_exprAst);
        progam = compiler.Program;
        SingletonDebugData = compiler.DebugData;
    }
    
    
    [Test]
    public void ActualProgram_QuickSort_1()
    {
        var src = @"
global size as integer
size = 100
maxValue = 100
DIM arr(size) as integer ` make an array

for x = 0 to size - 1
    arr(x) = rnd(maxValue) ` fill it up with random junk
next

print ""unsorted""
show() ` and print it out

quickSort(0, size - 1) `sort it

print ""sorted""
show() ` and print it out again

end `end the program before the functions, otherwise ""kaboom""

function partition(low, high)
    pivot = arr(high)
    i = low - 1
    for j = low to high-1
        if (arr(j) < pivot)
            i = i + 1
            temp = arr(i)
            arr(i) = arr(j)
            arr(j) = temp
        endif
    next
    `swap(arr[i+1],arr[high]);
    temp = arr(i + 1)
    arr(i + 1) = arr(high)
    arr(high) = temp

endfunction i + 1


function quickSort(low, high)
    if (low < high)
        pi = partition(low, high)
        quickSort(low, pi - 1)
        quickSort(pi + 1, high)
    endif
endfunction

function show()
    for x = 0 to size - 1
    print arr(x)
    next
endfunction

";
        Setup(src, out var compiler, out var prog);

        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;

        vm.Execute2(0); // just don't explode.
    }


    [Test]
    public void TestDeclare_Registers()
    {
        var src = "x AS WORD: x = 12";
        Setup(src, out _, out var prog);

        // Assert.That(prog.Count, Is.EqualTo(6)); // type code and 4 bytes for the int
        var offset = 4; // the size of the ptr representing the interned data
        Assert.That(prog[0 + offset], Is.EqualTo(OpCodes.PUSH));
        Assert.That(prog[1 + offset], Is.EqualTo(TypeCodes.INT));
        Assert.That(prog[2 + offset], Is.EqualTo(12));
        Assert.That(prog[3 + offset], Is.EqualTo(0));
        Assert.That(prog[4 + offset], Is.EqualTo(0));
        Assert.That(prog[5 + offset], Is.EqualTo(0));
        Assert.That(prog[6 + offset], Is.EqualTo(OpCodes.CAST));
        Assert.That(prog[7 + offset], Is.EqualTo(TypeCodes.WORD));

        Assert.That(prog[8 + offset], Is.EqualTo(OpCodes.STORE));
        Assert.That(prog[9 + offset], Is.EqualTo(0));
    }


    [Test]
    public void String_Init_StartEmpty()
    {
        var src = @"
buffer$=""""
toast$=""a""
x=len(buffer$)
";
        Setup(src, out var compiler, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;

        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0)); // the ptr to the string in memory
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(1)); // the ptr to the string in memory
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.STRING));
        
        Assert.That(vm.typeRegisters[2], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[2], Is.EqualTo(0)); // zero length string

        
        vm.heap.Read((int)vm.dataRegisters[0], "".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo(""));
        
        vm.heap.Read((int)vm.dataRegisters[1], "a".Length * 4, out memory);
        str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("a"));
        
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
    public void String_Concat_SelfReference()
    {
        var src = @"
x$ = ""world""
x$ = ""hello "" + x$
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo("world".Length * 4 + "hello ".Length * 4)); // the ptr to the string in memory
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        
        vm.heap.Read((int)vm.dataRegisters[0], "hello world".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("hello world"));
        
        
        // vm.heap.Read((int)vm.dataRegisters[1], "world".Length * 4, out memory);
        // str = VmConverter.ToString(memory);
        // Assert.That(str, Is.EqualTo("world"));
        //
        // vm.heap.Read((int)vm.dataRegisters[2], "helloworld".Length * 4, out memory);
        // str = VmConverter.ToString(memory);
        // Assert.That(str, Is.EqualTo("helloworld"));
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
    public void String_Declare2_WithIncrementalEval()
    {
        var src = "y=4\nx$ = \"hello\" ";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        for (var i = 0 ; i < vm.program.Length; i ++)
            vm.Execute2(1);
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(0)); // the ptr to the string in memory
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.STRING));
        
        vm.heap.Read(0, "hello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        
        Assert.That(str, Is.EqualTo("hello"));
    }
    
    [Test]
    public void String_Declare()
    {
        var src = "x as string: x = \"hello\" ";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0)); // the ptr to the string in memory
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        
        vm.heap.Read(0, "hello".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        
        Assert.That(str, Is.EqualTo("hello"));
    }

    
    // [Test]
    // public void String_Declare_Backslash()
    // {
    //     var src = "x as string: x = \"hel\\lo\" ";
    //     Setup(src, out _, out var prog);
    //     
    //     var vm = new VirtualMachine(prog);
    //     vm.Execute2();
    //     
    //     Assert.That(vm.dataRegisters[0], Is.EqualTo(0)); // the ptr to the string in memory
    //     Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
    //     
    //     vm.heap.Read(0, "hel\\lo".Length * 4, out var memory);
    //     var str = VmConverter.ToString(memory);
    //     
    //     Assert.That(str, Is.EqualTo("hel\\lo"));
    // }
    
    [Test]
    public void String_Declare_HugeHeap()
    {
        var initialStr =
            "okayokayokayokayokayokayokayokayokayokayokayokayokayokayokayokayokayokayokayokay";
        var src = "x$ = \""+ initialStr + "\" ";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0)); // the ptr to the string in memory
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        
        vm.heap.Read(0, initialStr.Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        
        Assert.That(str, Is.EqualTo(initialStr));
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
        var src = "x$ = \"hello\": y$ = \"world\" ";
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

    
    
    [TestCase("x = 3 > 2", 1)]
    [TestCase("x = 3 > 4", 0)]
    [TestCase("x = 3 > 3", 0)]
    [TestCase("x = 3 < 3", 0)]
    [TestCase("x = 3 < 4", 1)]
    [TestCase("x = 3 < 2", 0)]
    [TestCase("x = 3 >= 2", 1)]
    [TestCase("x = 3 >= 4", 0)]
    [TestCase("x = 3 >= 3", 1)]
    [TestCase("x = 3 <= 2", 0)]
    [TestCase("x = 3 <= 3", 1)]
    [TestCase("x = 3 <= 4", 1)]
    [TestCase("x = 3 = 4", 0)]
    [TestCase("x = 3 = 3", 1)]
    [TestCase("x = NOT 1", 0)]
    [TestCase("x = NOT 0", 1)]
    [TestCase("x = 3 <> 3", 0)]
    [TestCase("x = 3 <> 2", 1)]
    [TestCase("x = 3 <> 4", 1)]
    [TestCase("x = NOT 3 > 2", 0)]
    [TestCase("x = NOT NOT 3 > 2", 1)]
    [TestCase("x = NOT(2*2>2+3)", 1)]
    [TestCase("x = 3>2 AND 3>1", 1)]
    [TestCase("x = 3>2 AND 3>5", 0)]
    [TestCase("x = 3>2 AND NOT 3>5", 1)]
    [TestCase("x = NOT 3>2 AND 0", 0)]
    [TestCase("x = NOT (3>2 AND 0)", 1)]
    [TestCase("x = NOT 3>2 AND 1", 0)]
    [TestCase("x = NOT (3>2 AND 1)", 0)]
    [TestCase("x = (NOT 3>2) AND 1", 0)]
    [TestCase("x = NOT (NOT 1 AND 0)", 1)]
    [TestCase("x = NOT (NOT 3>2 AND 3>5)", 1)]
    [TestCase("x = (NOT 3>2) OR 1", 1)]
    public void Expression_Conditionals_Literal_Ints(string src, int expected)
    {
        Setup(src, out _, out var prog);
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        Assert.That(vm.dataRegisters[0], Is.EqualTo(expected));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    [TestCase("x# = 3 > 2", 1)]
    [TestCase("x# = 3 > 4", 0)]
    [TestCase("x# = 3 > 3", 0)]
    [TestCase("x# = 3 < 3", 0)]
    [TestCase("x# = 3 < 4", 1)]
    [TestCase("x# = 3 < 2", 0)]
    [TestCase("x# = 3 >= 2", 1)]
    [TestCase("x# = 3 >= 4", 0)]
    [TestCase("x# = 3 >= 3", 1)]
    [TestCase("x# = 3 <= 2", 0)]
    [TestCase("x# = 3 <= 3", 1)]
    [TestCase("x# = 3 <= 4", 1)]
    [TestCase("x# = 3 = 4", 0)]
    [TestCase("x# = 3 = 3", 1)]
    [TestCase("x# = NOT 1", 0)]
    [TestCase("x# = NOT 0", 1)]
    [TestCase("x# = 3 <> 3", 0)]
    [TestCase("x# = 3 <> 2", 1)]
    [TestCase("x# = 3 <> 4", 1)]
    [TestCase("x# = NOT 3 > 2", 0)]
    [TestCase("x# = NOT NOT 3 > 2", 1)]
    [TestCase("x# = NOT(2*2>2+3)", 1)]
    public void Expression_Conditionals_Literal_Floats(string src, int expected)
    {
        Setup(src, out _, out var prog);
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        var outputRegisterValue = vm.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        float output = BitConverter.ToSingle(outputRegisterBytes, 0);
        
        Assert.That(output, Is.EqualTo(expected));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.REAL));
    }
    
    
    [Test]
    public void Comments()
    {
        var src = @"
x = 1
` x = 2
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Comments_InTypeDef()
    {
        var src = @"
TYPE egg
    x `derp
ENDTYPE
x = 1
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    
    [Test]
    public void IfStatement_Simple()
    {
        var src = @"
x = 0
IF 1
x = 1
ENDIF
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void IfStatement_Simple_NoEntrance()
    {
        var src = @"
x = 1
IF 0
x = 0
ENDIF
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void IfStatement_StringEquality()
    {
        var src = @"
a = 0
x$ = ""hello""
IF x$ = ""hello""
a = 1
ENDIF
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void IfStatement_StringEquality_NotEqual()
    {
        var src = @"
a = 0
x$ = ""hello""
IF x$ = ""hello2""
a = 1
ENDIF
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void IfStatement_StringEquality_NotEqual2()
    {
        var src = @"
a = 0
x$ = ""hello""
IF x$ = ""world""
a = 1
ENDIF
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void IfStatement_ConditionalExpression()
    {
        var src = @"
x = 1
IF x = 1
x = 2
ENDIF
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(2)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    [Test]
    public void IfStatement_ConditionalExpression2()
    {
        var src = @"
x = 1
IF x = 1 AND 2 > 3
x = 2
ENDIF
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    
    [Test]
    public void IfStatement_Else()
    {
        var src = @"
x = 1
IF x > 1
    x = 2
ELSE
    x = 3
ENDIF
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void IfStatement_ElseEmpty()
    {
        var src = @"
x = 1
IF x > 1
    x = 2
ELSE
ENDIF
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    [Test]
    public void IfStatement_EmptyIf()
    {
        var src = @"
x = 1
IF x > 1
ELSE
    x = 2
ENDIF
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(2)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void IfStatement_NoFallThrough()
    {
        var src = @"
x = 1
IF x = 1
    x = 2
ELSE
    x = 3
ENDIF
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(2)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }
    
    
    [Test]
    public void IfStatement_OneLinger()
    {
        var src = @"
x = 1
IF x = 1 THEN x = 2
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(2)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void IfStatement_Nested()
    {
        var src = @"
x = 1
y = 0
IF x = 1
    x = 2
    IF x > 1
        x = 3
        y = y + 1
    ENDIF
    IF x > 1
        x = 4
        y = y + 1
    ENDIF
ENDIF
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(4)); 
        Assert.That(vm.dataRegisters[1], Is.EqualTo(2)); 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }
    
    [Test]
    public void RepeatUntil_Simple()
    {
        var src = @"
x = 1
REPEAT
    x = x + 1
UNTIL x > 4
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(5));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    [Test]
    public void RepeatUntil_ConditionIsAlreadyTrue()
    {
        var src = @"
x = 1
REPEAT
    x = x + 1
UNTIL x > 0
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(2));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void RepeatUntil_Exit()
    {
        var src = @"
x = 1
REPEAT
    x = x + 1
    IF x > 5 THEN EXIT
UNTIL x > 100
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(6));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void DoLoop_Simple_Exit()
    {
        var src = @"
x = 0
DO
    x = x + 1
    IF x = 10 THEN EXIT
LOOP
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(10));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    [Test]
    public void DoLoop_Simple_GoTo()
    {
        var src = @"
x = 0
DO
    x = x + 1
    IF x = 10 THEN GOTO Derp
LOOP

Derp:
x = x * 2
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(20));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }
    
    
    [Test]
    public void DoLoop_NeverExit()
    {
        var src = @"
x = 0
y = 0
DO
    x = x + 1
LOOP
y = 1
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(142)); // on July 30th 2023, it happened to be that it got to 142, but that is entirely by instructions run... 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(0)); // the point is that the loop never exits
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }


    [Test]
    public void Select_Simple()
    {
        var src = @"
x = 32
SELECT x
    CASE 32
        y = 1
    ENDCASE
ENDSELECT
";
        Setup(src, out _, out var prog);

        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(1));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }
    
    
    [Test]
    public void Select_Simple_2Cases_SkipTop()
    {
        var src = @"
x = 15
y = 0
z = 0
SELECT x
    CASE 32
        y = 1
        z = 1
    ENDCASE
    CASE 15
        y = 2
    ENDCASE
ENDSELECT
";
        Setup(src, out _, out var prog);

        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(2));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[2], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[2], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Select_Simple_2Cases_SkipBottom()
    {
        var src = @"
x = 32
y = 0
z = 0
SELECT x
    CASE 32
        y = 1
    ENDCASE
    CASE 15
        y = 2
        z = 1
    ENDCASE
ENDSELECT
";
        Setup(src, out _, out var prog);

        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(1));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[2], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[2], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Select_Simple_2Cases_MultipleValues()
    {
        var src = @"
x = 6
y = 0
z = 0
SELECT x
    CASE 5,6,7
        y = 1
    ENDCASE
    CASE 15
        y = 2
        z = 1
    ENDCASE
ENDSELECT
";
        Setup(src, out _, out var prog);

        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(1));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[2], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[2], Is.EqualTo(TypeCodes.INT));
    }
    
    
    [Test]
    public void Select_Simple_Default()
    {
        var src = @"
x = 6
y = 0
z = 0
SELECT x
    CASE 10
        y = 1
        z = 1
    ENDCASE
    CASE 15
        y = 2
        z = 1
    ENDCASE
    CASE DEFAULT
        y = 3
    ENDCASE
ENDSELECT
";
        Setup(src, out _, out var prog);

        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(3));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[2], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[2], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Select_Simple_Expression()
    {
        var src = @"
x = 0
y = 0
z = 0
SELECT 3 + 6
    CASE 10
        y = 1
        z = 1
    ENDCASE
    CASE 9
        y = 2
    ENDCASE
    CASE DEFAULT
        y = 3
        z = 1
    ENDCASE
ENDSELECT
";
        Setup(src, out _, out var prog);

        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(2));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[2], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[2], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Select_Floats()
    {
        var src = @"
x# = 1.2
SELECT x#
    CASE 1.2
        y = 1
    ENDCASE
    CASE 4
        y = 2
        z = 1
    ENDCASE
ENDSELECT
";
        Setup(src, out _, out var prog);

        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(1));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[2], Is.EqualTo(0));
        Assert.That(vm.typeRegisters[2], Is.EqualTo(TypeCodes.INT));
    }
    
    [Test]
    public void For_Simple()
    {
        var src = @"
x = 0
y = 0
FOR x = 1 TO 3
 y = y + x
NEXT
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(4));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(6));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void For_Simple_ChangingStep()
    {
        var src = @"
x = 0
y = 0
z = 1
FOR x = 1 TO 100 STEP z
    y = x
    z = x * 2
NEXT
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();

        var z = 1;
        var y = 0;
        var x = 0;
        for (x = 1; x <= 100; x += z)
        {
            y = x;
            z = x * 2;
        }
        
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(x));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(y));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[2], Is.EqualTo(z));
        Assert.That(vm.typeRegisters[2], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void For_Simple_Expression()
    {
        var src = @"
x = 0
y = 0
a = 10
FOR x = 1 TO a
 y = y + 1
NEXT
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(11));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(10));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }

    [Test]
    public void For_Simple_Exit()
    {
        var src = @"x = 0
y = 1
FOR x = 1 TO 100
    y = y * x
    IF y > 20
        EXIT
    ENDIF
NEXT
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(4));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(24));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }
    
    
    [Test]
    public void For_Simple_Reg()
    {
        var src = @"x = 0

FOR x = 1 TO 4 `x is reg 0, lc is reg 1
    n = 10 `n is reg 2
NEXT
y = 20
f = y * n
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(5));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        // Assert.That(vm.dataRegisters[1], Is.EqualTo(4)); // loop counter
        // Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[1], Is.EqualTo(10));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        // Assert.That(vm.dataRegisters[3], Is.EqualTo(20));
        // Assert.That(vm.typeRegisters[3], Is.EqualTo(TypeCodes.INT));
        // Assert.That(vm.dataRegisters[4], Is.EqualTo(200));
        // Assert.That(vm.typeRegisters[4], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[2], Is.EqualTo(20));
        Assert.That(vm.typeRegisters[2], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[3], Is.EqualTo(200));
        Assert.That(vm.typeRegisters[3], Is.EqualTo(TypeCodes.INT));
    }

    
        
    [Test]
    public void For_Sequences()
    {
        var src = @"
x = 0
y = 0
FOR x = 1 TO 3
 y = y + 1
NEXT
FOR x = 1 TO 3
 y = y + 1
NEXT
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(4));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(6));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void For_Simple_Negative()
    {
        var src = @"
x = 0
y = 0
FOR x = 10 TO 0 STEP -3
 y = y + x
NEXT
";
        Setup(src, out _, out var prog);

        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        var outputRegisterValue = vm.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        int output = BitConverter.ToInt32(outputRegisterBytes, 0);

        Assert.That(output, Is.EqualTo(-2));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(22));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void For_Nested()
    {
        var src = @"
c = 0
FOR x = 0 TO 4
    FOR y = 0 TO 2
        c = c + 1
        n = 1
    NEXT
    c = c * 4
NEXT
";
        Setup(src, out _, out var prog);

        var ast = _exprAst.ToString();
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();

        var c = 0;
        for (var x = 0; x <= 4; x++)
        {
            for (var y = 0; y <= 2; y ++)
            {
                c++;
            }

            c *= 4;
        }

        Assert.That(vm.dataRegisters[0], Is.EqualTo(c));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
    }
    
    
    [Test]
    public void For_Nested_LotsOfOtherVariablesAround()
    {
        var src = @"
c = 0
f = 1
FOR x = 0 TO 4
    l = 1
    FOR y = 0 TO 2
        c = c + 1
        n = 1
    NEXT
    k = 1
    c = c * 4
NEXT
";
        Setup(src, out _, out var prog);

        var ast = _exprAst.ToString();
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();

        var c = 0;
        for (var x = 0; x <= 4; x++)
        {
            for (var y = 0; y <= 2; y ++)
            {
                c++;
            }
            c *= 4;
        }

        Assert.That(vm.dataRegisters[0], Is.EqualTo(c));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
    }

    
    
    [Test]
    public void While_Simple()
    {
        var src = @"
x = 1
WHILE x < 3
    x = x + 1
ENDWHILE
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }


    [Test]
    public void While_Simple2()
    {
        var src = @"
x = 1
y = 0
WHILE x <= 5
    x = x + 1
    y = y + x
ENDWHILE
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(6));
        Assert.That(vm.dataRegisters[1], Is.EqualTo(20));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    

    [Test]
    public void While_Exit()
    {
        var src = @"
x = 1
WHILE x < 50
    x = x + 1
    IF x = 3
        EXIT
    ENDIF
ENDWHILE
x = x * 2
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(6));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }
    
    
    [Test]
    public void While_Nested()
    {
        var src = @"
x = 1
WHILE x < 50
    x = x + 1
    IF x = 3
        WHILE x < 10
            x = x + 2
            EXIT
            x = x + 100 `won't hit
        ENDWHILE
        x = x + 1
        EXIT
        x = x + 100 `won't hit
    ENDIF
ENDWHILE
x = x * 2
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(12));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Goto_Simple()
    {
        var src = @"
x = 1
GOTO Skip
x = 2
Skip:
x = 3

";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    
    [Test]
    public void GoSub_Simple()
    {
        var src = @"
x = 1

Gosub Sub
Gosub Sub
Gosub Sub

Sub:
x = x + 1
Return

";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(5)); // starts as 1, add 3 times, then hit the definition itself.
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void GoSub_End()
    {
        var src = @"
x = 1

Gosub Sub
Gosub Sub
Gosub Sub
END

Sub:
x = x + 1
Return

";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(4)); // starts as 1, add 3 times, then hit the definition itself.
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void GoSub_Example()
    {
        var src = @"
x = 1
y = 0
GOSUB Double
GOSUB Double
END

Double:
x = x * 2
GOSUB Trace
Return

Trace:
y = y + 1
Return
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(4)); // starts as 1, add 3 times, then hit the definition itself.
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(2)); // starts as 1, add 3 times, then hit the definition itself.
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
    }

    
    
    [Test]
    public void TestDeclareAndAssign()
    {
        var src = "x as word: x = 3";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.WORD));
    }

    [Test]
    public void TestGlobalDeclareAndAssign()
    {
        var src = "GLOBAL x as word: x = 3";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.globalScope.dataRegisters[0], Is.EqualTo(3));
        Assert.That(vm.globalScope.typeRegisters[0], Is.EqualTo(TypeCodes.WORD));
    }

    
    [Test]
    public void TestDeclareAndAssignToExpression()
    {
        var src = "x as word: x = 3 + 3";
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
    public void Array_LengthCheck()
    {
        var src = @"
dim x(12,3,50) as word
x(0) = 4
";
        Setup(src, out var compiler, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        // Assert.That(vm.heap.Cursor, Is.EqualTo(4));

        // Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        // Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        // Assert.That(vm.dataRegisters[3 /* dim register offset */], Is.EqualTo(140));
        // Assert.That(vm.typeRegisters[3 /* dim register offset */], Is.EqualTo(TypeCodes.INT)); // int, because y is not declared as a byte, so it is an int by default

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
        
        Assert.That(vm.dataRegisters[1], Is.EqualTo(2));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(10));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));

    }

    
    
    [Test]
    public void NegativeNumber()
    {
        var src = @"
x = -4
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        var outputRegisterValue = vm.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        int output = BitConverter.ToInt32(outputRegisterBytes, 0);

        Assert.That(output, Is.EqualTo(-4));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void NegativeNumber_Float()
    {
        var src = @"
x as float
x = -4
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        var outputRegisterValue = vm.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        float output = BitConverter.ToSingle(outputRegisterBytes, 0);

        Assert.That(output, Is.EqualTo(-4));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.REAL));
    }
    
    [Test]
    public void Subtraction()
    {
        var src = @"
x = 3 - 1
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(2));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }
    
    
    [Test]
    public void Subtraction_Neg()
    {
        var src = @"
x = 3 - -1
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        Assert.That(vm.dataRegisters[0], Is.EqualTo(4));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }
    
    
    [Test]
    public void Subtraction_Parens()
    {
        var src = @"
x = (4-5) - (2 - 1)
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        
        var outputRegisterValue = vm.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        int output = BitConverter.ToInt32(outputRegisterBytes, 0);

        Assert.That(output, Is.EqualTo(-2));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void ExplicitFloat()
    {
        var src = @"x as float: x = 1.2";
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
    
    [TestCase("1.2", "+", "2", 3.2f)]
    [TestCase("1.2", "-", "2", -.8f)]
    [TestCase("1.2", "/", "2", .6f)]
    [TestCase("1.2", "*", "2", 2.4f)]
    public void Float_OpShortcuts(string init, string op, string second, float result)
    {
        var src = @$"x# = {init}
x# {op}= {second}";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();

        var outputRegisterValue = vm.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        float output = float.Round( BitConverter.ToSingle(outputRegisterBytes, 0), 4);
        
        Assert.That(output, Is.EqualTo(result));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.REAL));
    }
    
    
    [Test]
    public void Declare_Implicit()
    {
        var src = @"global x# = 3";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();

        var outputRegisterValue = vm.globalScope.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        float output = BitConverter.ToSingle(outputRegisterBytes, 0);
        
        Assert.That(output, Is.EqualTo(3));
        Assert.That(vm.globalScope.typeRegisters[0], Is.EqualTo(TypeCodes.REAL));
    }

    
    [Test]
    public void IfStatement_ShortCircuit_Or_BothPathsRun()
    {
        var src = @$"
dim x(3)
y = 0
if 0 OR 1 > 0
    y = 12
endif
";
        Setup(src, out _, out var prog);

        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.Execute2();

        // register 2, because the first 3 are used for the array
        Assert.That(vm.typeRegisters[3], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[3], Is.EqualTo(12)); // register 0 is x, 
    }

    
    [Test]
    public void IfStatement_ShortCircuit_Or_PreventsExecutions()
    {
        var src = @$"
dim x(3)
y = 0
if 3 > 0 OR x(-1)
    y = 12
endif
";
        Setup(src, out _, out var prog);

        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.Execute2();

        // register 2, because the first 3 are used for the array
        Assert.That(vm.typeRegisters[3], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[3], Is.EqualTo(12)); // register 0 is x, 
    }

    
    [Test]
    public void IfStatement_ShortCircuit_Chain()
    {
        var src = @$"
dim x(3)
y = 0
if 0 OR (0 AND (x-1)) OR 1 OR x(-1)
    y = 12
endif
";
        Setup(src, out _, out var prog);

        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.Execute2();

        // register 2, because the first 3 are used for the array
        Assert.That(vm.typeRegisters[3], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[3], Is.EqualTo(12)); // register 0 is x, 
    }

    
    [Test]
    public void IfStatement_ShortCircuit_And_PreventsExecutions()
    {
        var src = @$"
dim x(3)
y = 12
if 0 AND x(-1)
    y = 0
endif
";
        Setup(src, out _, out var prog);

        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.Execute2();

        // register 2, because the first 3 are used for the array
        Assert.That(vm.typeRegisters[3], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[3], Is.EqualTo(12)); // register 0 is x, 
    }

    
    [Test]
    public void IfStatement_ShortCircuit_And_BothPathsRun()
    {
        var src = @$"
dim x(3)
y = 0
if 1 AND 2
    y = 12
endif
";
        Setup(src, out _, out var prog);

        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.Execute2();

        // register 2, because the first 3 are used for the array
        Assert.That(vm.typeRegisters[3], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[3], Is.EqualTo(12)); // register 0 is x, 
    }

    
    [TestCase("x#", "3", null, 3)]
    [TestCase("x#", "3.2", null, 3.2f)]
    [TestCase("x as float", "3.2", null, 3.2f)]
    [TestCase("x#", "\"toast\"", "invalid conversion", 0)]
    public void TypeConversion_Float_Simple(string leftSide, string rightSide, string error, float value)
    {
        var src = @$"
{leftSide} = {rightSide}
";
        Setup(src, out _, out var prog, ignoreParseCheck: true);

        if (error != null)
        {
            _exprAst.AssertParseErrors(1);
            return;
        }
        
        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.Execute2();

        var outputRegisterValue = vm.dataRegisters[0];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        float output = BitConverter.ToSingle(outputRegisterBytes, 0);
        
        Assert.That(output, Is.EqualTo(value));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.REAL));
    }
    
    
    [Test]
    public void Declare_Global_ReferencedFromLocal()
    {
        var src = @"
global x
x = 4
y = x
";
        Setup(src, out var compiler, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();

        Assert.That(vm.dataRegisters[0], Is.EqualTo(4)); // register 0 is x, 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.globalScope.dataRegisters[1], Is.EqualTo(4)); // register 1 is y, 
        Assert.That(vm.globalScope.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[2], Is.EqualTo(0)); // register 2 should have nothing in it
        Assert.That(vm.typeRegisters[2], Is.EqualTo(0));
    }

    
    
    [Test]
    public void Declare_InLoop()
    {
        var src = @"
for x = 0 to 3
    global y as integer = x
next
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();

        Assert.That(vm.dataRegisters[0], Is.EqualTo(4)); // register 0 is x, 
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.globalScope.dataRegisters[1], Is.EqualTo(3)); // register 1 is y, 
        Assert.That(vm.globalScope.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        
        Assert.That(vm.dataRegisters[2], Is.EqualTo(0)); // register 2 should have nothing in it
        Assert.That(vm.typeRegisters[2], Is.EqualTo(0));
    }
    
    
    [Test]
    public void Math_Ints_Add()
    {
        var src = @"x = 1 + 2";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }
    
    [Test]
    public void Math_IntFloats_And()
    {
        var src = @"x = 1.0 AND 2.0";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }
    
    [Test]
    public void Math_Ints_Divide()
    {
        var src = @"x = 50 / 5";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        Assert.That(vm.dataRegisters[0], Is.EqualTo(10));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }
    
    
    [Test]
    public void Math_Ints_Mod()
    {
        var src = @"x = 50 mod 3";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        Assert.That(vm.dataRegisters[0], Is.EqualTo(2));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }

    
    [Test]
    public void Math_Ints_Multiply()
    {
        var src = @"x = 3 * 2";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        Assert.That(vm.dataRegisters[0], Is.EqualTo(6));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
    }
    
    [TestCase("3", ">", "2", true)]
    [TestCase("2", ">", "3", false)]
    [TestCase("2", "<", "3", true)]
    [TestCase("3", "<", "2", false)]
    [TestCase("3", ">=", "2", true)]
    [TestCase("2", ">=", "3", false)]
    [TestCase("2", "<=", "3", true)]
    [TestCase("3", "<=", "2", false)]
    public void Math_Ints_OpTesting(string l, string op, string r, bool expected)
    {
        var src = @$"x = {l} {op} {r}";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        Assert.That(vm.dataRegisters[0], Is.EqualTo(expected ? 1 : 0));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
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
    public void Math_FloatInt_GreaterThan()
    {
        var src = @"
x# = 3.2
width = 109
n = x# > width
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[2], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[2], Is.EqualTo(0));
    }
    
    
    [Test]
    public void Math_FloatInt_Multiply()
    {
        var src = @"
x# = 3.0
width = 3
n# = x# * width
";
        Setup(src, out _, out var prog);
        
        var vm = new VirtualMachine(prog);
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[2], Is.EqualTo(TypeCodes.REAL));
        var outputRegisterValue = vm.dataRegisters[2];
        var outputRegisterBytes = BitConverter.GetBytes(outputRegisterValue);
        float output = BitConverter.ToSingle(outputRegisterBytes, 0);
        Assert.That(output, Is.EqualTo(9.0f));
        
        // Assert.That(vm.dataRegisters[2], Is.EqualTo(0));
    }
    
    [Test]
    public void TestDeclareAndAssignToExpressionToOverflow()
    {
        var src = "x as byte: x = 3 + 254"; 
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
    public void Type_ImplicitAssign()
    {
        var src = @"
TYPE egg
    x
ENDTYPE
a as egg
b = a
b.x = 1
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(8)); // size of the only field in egg, int, 4. (times 2, because there are 2 copies)
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.STRUCT));
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
w = y.x + len(y.z$)
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
    public void Type_Instantiate_Nested_DeclareOrderDoesNotMatter()
    {
        var src = @"
type chicken
e as egg
n 
endtype

type egg 
color
endtype

albert as chicken
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(8));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        // it shouldn't matter the order we define the types, because we are parse-sure they don't have loops.
    }

    
    [Test]
    public void Type_Instantiate_Nested_Assign2()
    {
        var src = @"
TYPE vector
    x as integer
    y as integer
ENDTYPE

TYPE object
    pos as vector
    vel as vector
ENDTYPE

player as object
player.vel.x = 1
player.pos.x = 2
player.pos.x = player.pos.x + player.vel.x

x = player.pos.x
";
        Setup(src, out var compiler, out var prog);
        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(16));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[1], Is.EqualTo(3));
    }

    
    [Test]
    public void Type_Instantiate_Nested_Assign_FloatConversion()
    {
        // the vector types are defined as floats, and when they get math'd, it should convert fine into an int
        var src = @"
TYPE vector
    x as float
    y as float
ENDTYPE

TYPE object
    pos as vector
    vel as vector
ENDTYPE

player as object
player.vel.x = 1 `here, its taking an INTEGER and jamming it into a float, but the bytes are getting confused, and its being treated as integer bytes within the float variable.
player.pos.x = 2
player.pos.x = player.pos.x + player.vel.x

` there is an implicit conversion here from float (on the rhs), to int (on the lhs)
x = player.pos.x
";
        Setup(src, out var compiler, out var prog);
        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(16));
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[1], Is.EqualTo(3));
    }

    
    [Test]
    public void Type_Instantiate_Nested_Assign()
    {
        var src = @"
TYPE egg 
    color
ENDTYPE

TYPE chicken
    e AS egg
    n 
ENDTYPE

albert AS chicken
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
        Assert.That(vm.globalScope.dataRegisters[3], Is.EqualTo(3));
    }
    
    
    [Test]
    public void Type_Instantiate_MultiArray_MultiField_Assign_WithExtraVar()
    {
        var src = @"
TYPE egg 
    color AS WORD
    derp 
ENDTYPE
y = 0
DIM x(3) AS egg `3*4 is 12 elements- 

n as egg
x(2).derp = 122
n = x(2)
y = n.derp
";
        /*
         * the problem is that n is being assigned to the pointer of x(2,3).
         * well, x(2,3) evals to be a pointer to that spot in memory
         *
         * the addr is 66; but the data is at 68
         */
        
        
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.heap.Cursor, Is.EqualTo(6*(3 + 1))); // 1 extra for the original assignment of n as egg
        Assert.That(vm.dataRegisters[0], Is.EqualTo(122));
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
        var x = new TestCommands();
        
        var src = "callTest";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
    }
    
    
    [Test]
    public void CallHost_MultiWord()
    {
        var src = "wait key";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
    }
    
    [Test]
    public void CallHost_PassVm()
    {
        var src = "getVm";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
    }

    [Test]
    public void CallHost_InkIssue()
    {
        var src = "ink 5,0";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
    }
    
    [Test]
    public void CallHost_Cast()
    {
        var src = "x = add(3.2, 1.1)";
        Setup(src, out var compiler, out var prog, expectedParseErrors: 0);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(4));
    }
    
    [Test]
    public void CallHost_ObjectInput()
    {
        var src = @$"
any input 3, x
any input ""darn"", y
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(0));
        
        Assert.That(vm.typeRegisters[1], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[1], Is.EqualTo(TypeCodes.STRING)); 
    }
    
    [Test]
    public void CallHost_RefType()
    {
        var src = "x = 7: refDbl x: x = x * 2";
        Setup(src, out var compiler, out var prog);
        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(28));
    }
    
    
    [Test]
    public void CallHost_RefType_ArrayStruct()
    {
        var src = @"
type toast
    x
endtype
dim bread(2) as toast
bread(1).x = 3
refDbl bread(1).x";
        Setup(src, out var compiler, out var prog);
        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.heap.Cursor, Is.EqualTo(8)); // size of the only field in toast, int, 4.
        // Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.PTR_HEAP));
        
        vm.heap.Read((int)vm.dataRegisters[0] + 4, 4, out var memory);
        var data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(6));
    }
    
    [Test]
    public void CallHost_RefType_FromStruct()
    {
        var src = @"
type toast
    x
endtype
bread as toast
bread.x = 3
refDbl bread.x";
        Setup(src, out var compiler, out var prog);
        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.heap.Cursor, Is.EqualTo(4)); // size of the only field in toast, int, 4.
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        vm.heap.Read((int)vm.dataRegisters[0], 4, out var memory);
        var data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(6));
    }
    
    
    [Test]
    public void CallHost_RefType_ArrayStruct_SecondValue()
    {
        var src = @"
type toast
    x
    y
endtype
dim bread(2) as toast
bread(1).y = 3
refDbl bread(1).y";
        Setup(src, out var compiler, out var prog);
        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.heap.Cursor, Is.EqualTo(16)); // size of the only field in toast, int, 4.
        // Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.PTR_HEAP));
        
        vm.heap.Read((int)vm.dataRegisters[0] + 12, 4, out var memory);
        var data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(6));
    }

    
    [Test]
    public void CallHost_RefType_ArrayStruct_Nested()
    {
        var src = @"
type jam
    z
    w
endtype
type toast
    x
    y as jam
endtype
dim bread(2) as toast
bread(1).y.w = 3
refDbl bread(1).y.w";
        Setup(src, out var compiler, out var prog);
        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.heap.Cursor, Is.EqualTo(24)); // size of the only field in toast, int, 4.
        // Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.PTR_HEAP));
        
        vm.heap.Read((int)vm.dataRegisters[0] + 20, 4, out var memory);
        var data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(6));
    }


    [Test]
    public void CallHost_RefType_FromStruct_SecondVariable()
    {
        var src = @"
type toast
    x
    y
endtype
bread as toast
bread.y = 3
refDbl bread.y";
        Setup(src, out var compiler, out var prog);
        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.heap.Cursor, Is.EqualTo(8)); // there are two ints, each are 4
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        vm.heap.Read((int)vm.dataRegisters[0] + 4, 4, out var memory);
        var data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(6));
    }
    
    
    [Test]
    public void CallHost_RefType_FromStruct_Nested()
    {
        var src = @"
type jam
    z
    w
endtype
type toast
    x
    y as jam
endtype

bread as toast
bread.y.w = 3
refDbl bread.y.w";
        
        Setup(src, out var compiler, out var prog);
        _exprAst.AssertNoParseErrors();
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.heap.Cursor, Is.EqualTo(12)); // there are three ints, each are 4
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
        
        vm.heap.Read((int)vm.dataRegisters[0] + 8, 4, out var memory);
        var data = BitConverter.ToInt32(memory);
        Assert.That(data, Is.EqualTo(6));
    }
    
    
    [Test]
    public void CallHost_RefType_FromStruct_Nested_InvalidCast()
    {
        var src = @"
type jam
    z
    w
endtype
type toast
    x
    y as jam
endtype

bread as toast
bread.y.z = 3
refDbl bread.y";
        
        Setup(src, out var compiler, out var prog, ignoreParseCheck: true);
        _exprAst.AssertParseErrors(1);
        var errs = _exprAst.GetAllErrors();
        Assert.That(errs[0].errorCode, Is.EqualTo(ErrorCodes.InvalidCast));
//         var vm = new VirtualMachine(prog);
//         vm.hostMethods = compiler.methodTable;
//         vm.Execute2();
//         Assert.That(vm.heap.Cursor, Is.EqualTo(12)); // there are three ints, each are 4
//         Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRUCT));
//         
//         vm.heap.Read((int)vm.dataRegisters[0] + 4, 4, out var memory);
//         var data = BitConverter.ToInt32(memory);
//         Assert.That(data, Is.EqualTo(6));
//         Assert.Fail(@"
// This test shouldn't be allowed to compile. 'bread.y' is not a valid numeric value, but
// it can be passed to the ref parameter because it happens to map to the first parameter.
// ");
    }

    
    [Test]
    public void CallHost_RefType_AsRaw()
    {
        var src = "x = 7: complexArg x:";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(14));
    }
    
    
    [Test]
    public void CallHost_FileEndIssue()
    {
        var src = @"
x = file end(10)=10
";
        Setup(src, out var compiler, out var prog, expectedParseErrors: 0);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1));
    }

    
    [Test]
    public void CallHost_Overload_1()
    {
        var src = @"
x = 1
x = overloadA(x)
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(2));
    }

    [Test]
    public void CallHost_Overload_2()
    {
        var src = @"
x = 1
x = overloadA(x,4)
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(5));
    }
    
//     [Test]
//     public void CallStackCheck()
//     {
//         var src = @"n = 0
// igloo()
//
// function igloo()
//     print ""toast""
//     while 1 > 0
//         inc n
//         print ""hello"", n
//         wait ms 500
//         getVm
//     endwhile
// endfunction";
//         Setup(src, out var compiler, out var prog, generaeteDebug: true);
//         var vm = new VirtualMachine(prog);
//         vm.hostMethods = compiler.methodTable;
//         vm.Execute2();
//     }
    
    [Test]
    public void CallHost_Params()
    {
        var src = @"
x = 1
x = x + sum(2,3,6)
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(12));
    }

    
    [Test]
    public void CallHost_Params_Empty()
    {
        var src = @"
x = sum() + 12
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(12));
    }

    [Test]
    public void CallHost_Params_WithLeadingVm()
    {
        var src = @"
x = 1
x = x + sum2 (2,3,6)
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1 + 2 + 3 + 6));
    }

    
    [Test]
    public void CallHost_Params_Order()
    {
        var src = @"
x = get last( 1,2,3)
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3));
    }

    
    [Test]
    public void CallHost_Params_Object()
    {
        var src = @"
x$ = concat( 1, ""hello"", 2)
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        vm.heap.Read((int)vm.dataRegisters[0], "1;hello;2".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("1;hello;2"));
    }

    
    [Test]
    public void CallHost_RefType_String()
    {
        var src = "x$ = \"a\": tuna x$ ";
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
    public void CallHost_RefType_String_2()
    {
        var src = "tuna x$";
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
        var src = "x = len(\"hello\")";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo("hello".Length));
    }
    
    [Test]
    public void CallHost_StringArg_Backslash()
    {
        var src = "x = len(\"hel\\lo\")";
        Setup(src, out var compiler, out var prog, ignoreParseCheck: true);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo("hello".Length));
    }
    
    [Test]
    public void CallHost_WithDollarSign_Upper()
    {
        var src = "x$ = upper$(\"hello\")";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        // Assert.That(vm.dataRegisters[0], Is.EqualTo("HELLO".Length));
    }
    
    [Test]
    public void CallHost_StringArg_FromArray()
    {
        var src = @"
dim x$(3)
x$(1) = ""hello""
y = len(x$(1))
z$ = reverse(x$(1))
x$(2) = reverse(x$(1))
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
        var src = "x$ = reverse(\"hello\")";
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
    public void CallHost_StringReturn_2()
    {
        var src = "x$ = str$(32)";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.STRING));
        vm.heap.Read((int)vm.dataRegisters[0], "32".Length * 4, out var memory);
        var str = VmConverter.ToString(memory);
        Assert.That(str, Is.EqualTo("32"));
    }
    
    [Test]
    public void CallHost_StringReturn_Assignment()
    {
        var src = "x$ = \"hello\": y$ =reverse( x$)";
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
        var src = "x$ = \"hello\": y$ = \"world\" + reverse (x$)";
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
        var src = "inc x:";
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
        var src = "x = 7: inc x, 2";
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
        var src = "x = 7: inc x";
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
        var src = "x = add(1,2)";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3));
    }
    
    
    [Test]
    public void CallHost_WithVariableWithAPrefixOfCommand()
    {
        var src = @"minX = 0
minX = min(1,2)
";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(1));
    }
    
    [Test]
    public void CallHost_OpOrder()
    {
        var src = "x = min(5,8 + 1)";
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
        var src = "x = min(5, 8 * 2)";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(5));
    }
    [Test]
    public void CallHost_OpOrder_NewLine_CommaOnNewLine()
    {
        var src = @"
x = min (5
, 8 * 2)";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(5));
    }
    
    
    [Test]
    public void CallHost_OpOrder_NewLine_CommaOnSameLine()
    {
        var src = @"
x = min (5,
8 * 2)";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(5));
    }

    [Test]
    public void CallHost_OpOrder_NewLine_CommaBetweenManyLines()
    {
        var src = @"
x = min (5,


8 * 2)";
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
        var src = "x = min(5, 8) * 2";
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
        var src = "x = 1 + min(5, 8)";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(6));
    }
    
    [Test]
    public void CallHost_OpOrder_WidthCommand()
    {
        var src = "x = min( screen width(),3)";
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2();
        
        Assert.That(vm.typeRegisters[0], Is.EqualTo(TypeCodes.INT));
        Assert.That(vm.dataRegisters[0], Is.EqualTo(3));
    }
}