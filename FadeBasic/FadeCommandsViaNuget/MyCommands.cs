using FadeBasic.SourceGenerators;

namespace FadeCommandsViaNuget;

public partial class MyCommands
{
    [FadeBasicCommand("print")]
    public static void Print(int n)
    {
        Console.WriteLine(n);
    }
    
    [FadeBasicCommand("toast")]
    public static void Toast(ref int n)
    {
        n = n * 3;
    }
}