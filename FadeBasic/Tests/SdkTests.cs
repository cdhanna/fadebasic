using FadeBasic;
using FadeBasic.Launch;
using FadeBasic.Lib.Standard;
using FadeBasic.Sdk;

namespace Tests;

public class SdkTests
{

    [Test]
    public void Debug()
    {
        var commands = new CommandCollection(
            new ConsoleCommands(), 
            new StandardCommands()
        );
        // var path = "/Users/chrishanna/Documents/Github/DarkBasicVsCode/sample/example.csproj";
        var path = "Fixtures/Projects/SpinFor4Seconds/prim.csproj";
        if (!Fade.TryFromProject(path, commands, out var ctx, out _))
        {
            Assert.Fail("no file");
        }

        var called = false;
        Task.Factory.StartNew(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            
            var servers = DebugSession.DiscoverServers();
            var port = servers[0].port;
            called = true;
        });
        
        ctx.Debug(waitForConnection: false);//, port: 57428);

        Assert.IsTrue(called);
    }
    
    
    [Test]
    public void FromProject()
    {
        var commands = new CommandCollection(
            new ConsoleCommands(), new StandardCommands()
            );
        if (!Fade.TryFromProject("Fixtures/Projects/Primitive/prim.csproj", commands, out var ctx, out _))
        {
            Assert.Fail("no file");
        }
        ctx.Run();

        ctx.TryGetInteger("i", out var i);
        Assert.That(i, Is.EqualTo(2));
    }
    
    [Test]
    public void Doop()
    {
        var commands = new CommandCollection(new FadeBasic.Lib.Standard.StandardCommands());
        var src = "x = rnd(5) + 3";
        var ran = Fade.TryRun(src, commands, out var ctx, out _);
        if (!ctx.TryGetInteger("x", out var x))
        {
            Assert.Fail();
        }
        Assert.That(x, Is.GreaterThanOrEqualTo(3));


    }
    
    
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
    i as integer
    di as double integer
    w as word
    dw as dword
    b as byte
    b2 as boolean
    f as float
    df as double float
    s as string
    c as chicken
ENDTYPE
TYPE chicken
    i as integer
    di as double integer
    w as word
    dw as dword
    b as byte
    b2 as boolean
    f as float
    df as double float
    s as string
ENDTYPE
e as egg
e.i = 32
e.di = 901
e.w = 4
e.dw = 8
e.b = 255
e.b2 = 4
e.f = 32.1
e.df = 55.5
e.s = ""tunafish""

e.c.i = 32
e.c.di = 901
e.c.w = 4
e.c.dw = 8
e.c.b = 255
e.c.b2 = 4
e.c.f = 32.1
e.c.df = 55.5
e.c.s = ""igloo""
";
        if (!Fade.TryCreate(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }
        
        ctx.Run();

        var found = ctx.TryGetObject("e", out FadeObject e, out _);
        Assert.IsTrue(found);
        Assert.That(e.objects["c"].wordFields["w"], Is.EqualTo(4));
    }
    
    
    [Test]
    public void Read_Array_Object_Rank1()
    {
        var src = @"
TYPE egg
    a, b
ENDTYPE
DIM x(4) as egg
x(1).a = 5
x(1).b = 2
";
        if (!Fade.TryRun(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }

        ctx.TryGetObjectArray("x", out var a, out var _);
        
        var obj = a.GetElement(1);
        Assert.That(obj.integerFields["a"], Is.EqualTo(5));
        Assert.That(obj.integerFields["b"], Is.EqualTo(2));
    }
    
    
    [Test]
    public void Read_Array_Object_Rank2()
    {
        var src = @"
TYPE egg
    a, b
ENDTYPE
DIM x(4, 3) as egg
x(3, 2).a = 5
x(3, 2).b = 2
";
        if (!Fade.TryRun(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }

        ctx.TryGetObjectArray("x", out var a, out var _);
        
        var obj = a.GetElement(3,2);
        Assert.That(obj.integerFields["a"], Is.EqualTo(5));
        Assert.That(obj.integerFields["b"], Is.EqualTo(2));
    }
    
    
    [Test]
    public void Read_Array_ObjectNested_Rank2()
    {
        var src = @"
TYPE egg
    a, b
    c as chicken
ENDTYPE
TYPE chicken
    name$
ENDTYPE
DIM x(4, 3) as egg
x(3, 2).a = 5
x(3, 2).b = 2
x(3, 2).c.name$ = ""hamlet""

";
        if (!Fade.TryRun(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }

        ctx.TryGetObjectArray("x", out var a, out var _);
        
        var obj = a.GetElement(3,2);
        Assert.That(obj.integerFields["a"], Is.EqualTo(5));
        Assert.That(obj.integerFields["b"], Is.EqualTo(2));
    }
    
    [Test]
    public void Read_Array_Integer_Rank1()
    {
        var src = @"
DIM x(4)
x(1) = 5
";
        if (!Fade.TryRun(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }

        if (!ctx.TryGetIntegerArray("x", out var a, out var _))
        {
            Assert.Fail();
        }
        
        var number = a.GetElement(1);
        Assert.That(number, Is.EqualTo(5));
    }
    
    
    [Test]
    public void Read_Array_Integer_Rank2()
    {
        var src = @"
DIM x(4, 9)
x(1, 7) = 5
";
        if (!Fade.TryRun(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }

        ctx.TryGetIntegerArray("x", out var a, out var _);

        var number = a.GetElement(1, 7);
        Assert.That(number, Is.EqualTo(5));
        
    }
    
    
    [Test]
    public void Read_Array_Integer_Rank3()
    {
        var src = @"
DIM x(4, 9, 2)
x(3, 7, 0) = 5
";
        if (!Fade.TryRun(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }

        ctx.TryGetIntegerArray("x", out var a, out var _);
        
        var number = a.GetElement(3, 7, 0);
        Assert.That(number, Is.EqualTo(5));
        
    }
    
    
    [Test]
    public void Read_Array_DoubleInteger_Rank2()
    {
        var src = @"
DIM x(4, 9) as double integer
x(1, 7) = 5
";
        if (!Fade.TryRun(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }

        if (!ctx.TryGetDoubleIntegerArray("x", out var a, out var _))
        {
            Assert.Fail();
        }

        var number = a.GetElement(1, 7);
        Assert.That(number, Is.EqualTo(5));
        
    }

    
    [Test]
    public void Read_Array_Word_Rank2()
    {
        var src = @"
DIM x(4, 9) as word
x(1, 7) = 5
";
        if (!Fade.TryRun(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }

        if (!ctx.TryGetWordArray("x", out var a, out var _))
        {
            Assert.Fail();
        }

        var number = a.GetElement(1, 7);
        Assert.That(number, Is.EqualTo(5));
        
    }

    
    [Test]
    public void Read_Array_DWord_Rank2()
    {
        var src = @"
DIM x(4, 9) as dword
x(1, 7) = 5
";
        if (!Fade.TryRun(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }

        if (!ctx.TryGetDWordArray("x", out var a, out var _))
        {
            Assert.Fail();
        }

        var number = a.GetElement(1, 7);
        Assert.That(number, Is.EqualTo(5));
        
    }

    
    [Test]
    public void Read_Array_Byte_Rank2()
    {
        var src = @"
DIM x(4, 9) as byte
x(1, 7) = 5
";
        if (!Fade.TryRun(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }

        if (!ctx.TryGetByteArray("x", out var a, out var _))
        {
            Assert.Fail();
        }

        var number = a.GetElement(1, 7);
        Assert.That(number, Is.EqualTo(5));
        
    }
    
    [Test]
    public void Read_Array_Bool_Rank2()
    {
        var src = @"
DIM x(4, 9) as bool
x(1, 7) = 5
";
        if (!Fade.TryRun(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }

        if (!ctx.TryGetBoolArray("x", out var a, out var _))
        {
            Assert.Fail();
        }

        var number = a.GetElement(1, 7);
        Assert.That(number, Is.EqualTo(true));
        
    }

    [Test]
    public void Read_Array_Float_Rank2()
    {
        var src = @"
DIM x(4, 9) as float
x(1, 7) = 5
";
        if (!Fade.TryRun(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }

        if (!ctx.TryGetFloatArray("x", out var a, out var _))
        {
            Assert.Fail();
        }

        var number = a.GetElement(1, 7);
        Assert.That(number, Is.EqualTo(5));
        
    }
    
    
    [Test]
    public void Read_Array_DoubleFloat_Rank2()
    {
        var src = @"
DIM x(4, 9) as double float
x(1, 7) = 5
";
        if (!Fade.TryRun(src, TestCommands.CommandsForTesting, out var ctx, out _))
        {
            Assert.Fail();
        }

        if (!ctx.TryGetDoubleFloatArray("x", out var a, out var _))
        {
            Assert.Fail();
        }

        var number = a.GetElement(1, 7);
        Assert.That(number, Is.EqualTo(5));
        
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