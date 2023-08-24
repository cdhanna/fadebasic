using System.Linq;
using DarkBasicYo.SourceGenerators;
using DarkBasicYo.Virtual;

namespace DarkBasicYo
{
    public partial class DarkBasicCommands
    {
        
        
        // [DarkBasicCommand("len")]
        // public static int Length(string x)
        // {
        //     return x.Length;
        // }
        // [DarkBasicCommand("flip")]
        // public static string Flip(string x)
        // {
        //     return x.Reverse().ToString();
        // }
        //
        // [DarkBasicCommand("Ana2")]
        // public static int Ana3(int y)
        // {
        //     return y * 2;
        // }
        //

        //
        // [DarkBasicCommand("any input")]
        // public static int Ana()
        // {
        //     // return y * 2;
        //     return 0;
        // }
        //
        // [DarkBasicCommand("Many")]
        // public static int Many(params int[] many)
        // {
        //     return many.Length;
        // }
        //
        [DarkBasicCommand("complexArg")]
        public static void ComplexArg([FromVm] VirtualMachine vm, RawArg<int> arg)
        {
            VmUtil.HandleValue(vm, 44, TypeCodes.INT, arg.state, arg.address);
        }

        //
        //
        //
        // [DarkBasicCommand("standard double2")]
        //  public static int StandardDoubleffff(int x, int y=3)
        //  {
        //      return x * 2;
        //  }
        //  
        //  [DarkBasicCommand("refTest")]
        //  public static int RefTest(ref int x)
        //  {
        //      return x * 2;
        //  }
    }
}