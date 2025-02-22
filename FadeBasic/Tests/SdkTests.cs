using FadeBasic.Sdk;

namespace Tests;

public class SdkTests
{
    [Test]
    public void Simple()
    {
        var src = "print 42";
        var ran = Fade.TryRun(src, TestCommands.CommandsForTesting, out _);
        Assert.IsTrue(ran);
    }
    
    [Test]
    public void Simple_GetErrors()
    {
        var src = "print 42x";
        var ran = Fade.TryRun(src, TestCommands.CommandsForTesting, out var errors);
        Assert.IsFalse(ran);
        Assert.That(errors.ParserErrors.Count, Is.EqualTo(1));
    }
    
    [Test]
    public void Read_Integer()
    {
        var src = @"
x = 5
y = x + 12
w# = 1
";
        var created = Fade.TryCreate(src, TestCommands.CommandsForTesting, out var ctx, out _);
        Assert.IsTrue(created);
        
        ctx.Run();

        var found = ctx.TryGetInteger("y", out var y);
        Assert.IsTrue(found);
        Assert.AreEqual(y, 17);
        
        found = ctx.TryGetInteger("x", out var x);
        Assert.IsTrue(found);
        Assert.AreEqual(x, 5);

        found = ctx.TryGetInteger("z", out _, out var err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
        
        found = ctx.TryGetInteger("w#", out _, out err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
    }
    
    
    [Test]
    public void Read_DoubleInteger()
    {
        var src = @"
x as double integer = 5
y as double integer = x + 12
w# = 1
";
        var created = Fade.TryCreate(src, TestCommands.CommandsForTesting, out var ctx, out _);
        Assert.IsTrue(created);
        
        ctx.Run();

        var found = ctx.TryGetDoubleInteger("y", out var y);
        Assert.IsTrue(found);
        Assert.AreEqual(y, 17);
        
        found = ctx.TryGetDoubleInteger("x", out var x);
        Assert.IsTrue(found);
        Assert.AreEqual(x, 5);

        found = ctx.TryGetDoubleInteger("z", out _, out var err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
        
        found = ctx.TryGetDoubleInteger("w#", out _, out err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
    }
    
    
    [Test]
    public void Read_Word()
    {
        var src = @"
x as word = 5
y as word = x + 12
w# = 1
";
        var created = Fade.TryCreate(src, TestCommands.CommandsForTesting, out var ctx, out _);
        Assert.IsTrue(created);
        
        ctx.Run();

        var found = ctx.TryGetWord("y", out var y);
        Assert.IsTrue(found);
        Assert.AreEqual(y, 17);
        
        found = ctx.TryGetWord("x", out var x);
        Assert.IsTrue(found);
        Assert.AreEqual(x, 5);

        found = ctx.TryGetWord("z", out _, out var err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
        
        found = ctx.TryGetWord("w#", out _, out err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
    }
    
    
    [Test]
    public void Read_DWord()
    {
        var src = @"
x as dword = 5
y as dword = x + 12
w# = 1
";
        var created = Fade.TryCreate(src, TestCommands.CommandsForTesting, out var ctx, out _);
        Assert.IsTrue(created);
        
        ctx.Run();

        var found = ctx.TryGetDWord("y", out var y);
        Assert.IsTrue(found);
        Assert.AreEqual(y, 17);
        
        found = ctx.TryGetDWord("x", out var x);
        Assert.IsTrue(found);
        Assert.AreEqual(x, 5);

        found = ctx.TryGetDWord("z", out _, out var err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
        
        found = ctx.TryGetDWord("w#", out _, out err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
    }
    
    
    [Test]
    public void Read_Float()
    {
        var src = @"
x as float = 5
y as float = x + 12
w = 1
";
        var created = Fade.TryCreate(src, TestCommands.CommandsForTesting, out var ctx, out _);
        Assert.IsTrue(created);
        
        ctx.Run();

        var found = ctx.TryGetFloat("y", out var y);
        Assert.IsTrue(found);
        Assert.AreEqual(y, 17);
        
        found = ctx.TryGetFloat("x", out var x);
        Assert.IsTrue(found);
        Assert.AreEqual(x, 5);

        found = ctx.TryGetFloat("z", out _, out var err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
        
        found = ctx.TryGetFloat("w#", out _, out err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
    }
    
    
    [Test]
    public void Read_DFloat()
    {
        var src = @"
x as double float = 5
y as double float = x + 12
w = 1
";
        var created = Fade.TryCreate(src, TestCommands.CommandsForTesting, out var ctx, out _);
        Assert.IsTrue(created);
        
        ctx.Run();

        var found = ctx.TryGetDoubleFloat("y", out var y);
        Assert.IsTrue(found);
        Assert.AreEqual(y, 17);
        
        found = ctx.TryGetDoubleFloat("x", out var x);
        Assert.IsTrue(found);
        Assert.AreEqual(x, 5);

        found = ctx.TryGetDoubleFloat("z", out _, out var err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
        
        found = ctx.TryGetDoubleFloat("w#", out _, out err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
    }
    
    [Test]
    public void Read_Byte()
    {
        var src = @"
x as byte = 5
y as byte = x + 12
w# = 1
";
        var created = Fade.TryCreate(src, TestCommands.CommandsForTesting, out var ctx, out _);
        Assert.IsTrue(created);
        
        ctx.Run();

        var found = ctx.TryGetByte("y", out var y);
        Assert.IsTrue(found);
        Assert.AreEqual(y, 17);
        
        found = ctx.TryGetByte("x", out var x);
        Assert.IsTrue(found);
        Assert.AreEqual(x, 5);

        found = ctx.TryGetByte("z", out _, out var err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
        
        found = ctx.TryGetByte("w#", out _, out err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
    }
    
    
    [Test]
    public void Read_Bool()
    {
        var src = @"
x as boolean = 0
y as boolean = 54
w# = 1
";
        var created = Fade.TryCreate(src, TestCommands.CommandsForTesting, out var ctx, out _);
        Assert.IsTrue(created);
        
        ctx.Run();

        var found = ctx.TryGetBool("y", out var y);
        Assert.IsTrue(found);
        Assert.AreEqual(y, true);
        
        found = ctx.TryGetBool("x", out var x);
        Assert.IsTrue(found);
        Assert.AreEqual(x, false);

        found = ctx.TryGetBool("z", out _, out var err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
        
        found = ctx.TryGetBool("w#", out _, out err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
    }

    [Test]
    public void Read_Type()
    {
        var src = @"
TYPE egg
    x
    y#
ENDTYPE
e as egg
e.x = 4
e.y# = 2.1
";
        Assert.Fail("should be able to read e, and get back SOMETHING");
    }
    
    
    [Test]
    public void Read_Array()
    {
        var src = @"
DIM x(4)
x(1) = 5
";
        Assert.Fail("should be able to read x, and get back SOMETHING");
    }
    
    [Test]
    public void Read_String()
    {
        var src = @"
x as string = ""tuna""
y as string = ""hello""
w# = 1
";
        var created = Fade.TryCreate(src, TestCommands.CommandsForTesting, out var ctx, out _);
        Assert.IsTrue(created);
        
        ctx.Run();

        var found = ctx.TryGetString("y", out var y);
        Assert.IsTrue(found);
        Assert.AreEqual(y, "hello");
        
        found = ctx.TryGetString("x", out var x);
        Assert.IsTrue(found);
        Assert.AreEqual(x, "tuna");

        found = ctx.TryGetString("z", out _, out var err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
        
        found = ctx.TryGetString("w#", out _, out err);
        Assert.IsFalse(found);
        Assert.IsNotNull(err);
    }
}