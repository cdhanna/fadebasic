using DarkBasicYo;

namespace Tests;

public class TestCommands
{
    public static readonly CommandCollection Commands = new(typeof(TestCommands));

    [CommandName("callTest")]
    public static void CallTest()
    {
        
    }
    
    
    [CommandName("add")]
    public static int AddTest(int a, int b)
    {
        return a + b;
    }
    
    [CommandName("min")]
    public static int Min(int a, int b)
    {
        return Math.Min(a, b);
    }

    [CommandName("refDbl")]
    public static void Dbl(int a)
    {
        a *= 2;
    }
}