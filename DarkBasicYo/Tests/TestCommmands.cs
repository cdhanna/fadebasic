using System.Runtime.InteropServices;
using System.Text;
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
    public static void Dbl(ref int dbl)
    {
        dbl *= 2;
    }

    [CommandName("inc")]
    public static void Inc(ref int variable, int amount = 1)
    {
        variable += amount;
    }
    
    
    // [CommandName("setTuna")]
    // public static void Tuna(ref string variable)
    // {
    //     variable = "tuna";
    // }

    [CommandName("len")]
    public static int Length(string x)
    {
        return x.Length;
    }
    
    [CommandName("reverse")]
    public static string Reverse(string x)
    {
        var sb = new StringBuilder();
        for (var i = x.Length - 1; i >= 0; i--)
        {
            sb.Append(x[i]);
        }
        return sb.ToString();
    }
    
    
    [CommandName("tuna")]
    public static void Tuna(ref string x)
    {
        x = "tuna";
    }
}