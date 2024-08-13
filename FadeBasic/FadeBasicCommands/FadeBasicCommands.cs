using System;
using System.Linq;
using FadeBasic.SourceGenerators;
using FadeBasic.Virtual;

namespace FadeBasic
{
    /// <summary>
    /// More Docs!
    /// </summary>
    public partial class FadeBasicCommands
    {
        [FadeBasicCommand( "tuna")]
        public static void Secondary()
        {
            
        }
    }
    
    
    /// <summary>
    /// Docs
    /// </summary>
    public partial class FadeBasicCommands
    {
        
    /// <summary>
    /// hello world
    ///
    /// bv
    /// <para>
    /// Goofs
    /// </para>
    /// </summary>
    /// <param name="x">args</param>
    [FadeBasicCommand("print")]
    public static void Print(object x)
    {
        Console.WriteLine(x);
    }

    /// <summary>
    /// tuna 
    /// </summary>
    /// <param name="max"></param>
    /// <returns></returns>
    [FadeBasicCommand("rnd")]
    public static int Rnd(int max = 10)
    {
        var r = new Random();
        return r.Next(max);
    }
        
        [FadeBasicCommand("input")]
        public static void Input(ref string result)
        {
            result = Console.ReadLine();
        }
             
        [FadeBasicCommand("double")]
        public static int Double(int x)
        {
            return x * 2;
        }
        
        [FadeBasicCommand("reflect count")]
        public static int ReflectCount([FromVm] VirtualMachine vm)
        {
            return vm.hostMethods.methods.Length;
        }
        
        [FadeBasicCommand("toast")]
        public static void Toast(ref int n)
        {
            n *= 2;
            Console.Write(n);
        }
        
        [FadeBasicCommand("farts2")]
        public static void farts2(int barf, int n)
        {
            var x = barf * n;
            Console.Write(x);
        }
   
        
        [FadeBasicCommand("overload")]
        public static void Overload_1(int a)
        {
        }
        
        [FadeBasicCommand("overload")]
        public static void Overload_1(int a, int b)
        {
        }
        
        // [FadeBasicCommand("flip")]
        // public static string Flip(string x)
        // {
        //     return x.Reverse().ToString();
        // }
        //
        // [FadeBasicCommand("Ana2")]
        // public static int Ana3(int y)
        // {
        //     return y * 2;
        // }
        //

        //
        // [FadeBasicCommand("any input")]
        // public static int Ana()
        // {
        //     // return y * 2;
        //     return 0;
        // }
        //
        // [FadeBasicCommand("Many")]
        // public static int Many(params int[] many)
        // {
        //     return many.Length;
        // }
        //
        [FadeBasicCommand("complexArg")]
        public static void ComplexArg([FromVm] VirtualMachine vm, RawArg<int> arg)
        {
            VmUtil.HandleValue(vm, 44, TypeCodes.INT, arg.state, arg.address);
        }
        

        //
        //
        //
        // [FadeBasicCommand("standard double2")]
        //  public static int StandardDoubleffff(int x, int y=3)
        //  {
        //      return x * 2;
        //  }
        //  
        //  [FadeBasicCommand("refTest")]
        //  public static int RefTest(ref int x)
        //  {
        //      return x * 2;
        //  }
    }
}