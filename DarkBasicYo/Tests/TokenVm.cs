using DarkBasicYo;
using DarkBasicYo.Virtual;

namespace Tests;

public class TokenVm
{
    void Setup(string src, out Compiler compiler, out List<byte> progam)
    {
        var lexer = new Lexer();
        var tokens = lexer.Tokenize(src);
        var parser = new Parser(new TokenStream(tokens), StandardCommands.LimitedCommands);
        var exprAst = parser.ParseWikiExpression();

        compiler = new Compiler();
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
        var src = "3.2 * 1.5";
        Setup(src, out _, out var prog);
        prog.Add(OpCodes.DBG_PRINT);
        var vm = new VirtualMachine(prog);
        vm.Execute().MoveNext();
        var output = vm.ReadStdOut();
        Assert.That(output, Is.EqualTo($"{TypeCodes.REAL} - 4.8\n"));
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
}