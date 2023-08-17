using System.Linq;
using DarkBasicYo.SourceGenerators;
using DarkBasicYo.Virtual;

namespace DarkBasicYo
{
    public partial class DarkBasicCommands
    {
        
        
        [DarkBasicCommand("len")]
        public static int Length(string x)
        {
            return x.Length;
        }
        [DarkBasicCommand("flip")]
        public static string Flip(string x)
        {
            return x.Reverse().ToString();
        }
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
        // [DarkBasicCommand("Mult")]
        // public static int Mult(int x, int y)
        // {
        //     return x * y;
        // }
        //
        //
        //
        // [DarkBasicCommand("standard double")]
        //  public static int StandardDouble(int x)
        //  {
        //      return x * 2;
        //  }
    }
}