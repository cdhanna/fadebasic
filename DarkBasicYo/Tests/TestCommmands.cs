using System.Runtime.InteropServices;
using System.Text;
using DarkBasicYo;
using DarkBasicYo.SourceGenerators;
using DarkBasicYo.Virtual;

namespace Tests
{

    public partial class TestCommands
    {
        public static readonly CommandCollection CommandsForTesting = new CommandCollection(new TestCommands());

        [DarkBasicCommand("Jerk")]
        public static void Dbjerkl(int dbl)
        {
            dbl *= 2;
        }
        //
        [DarkBasicCommand("refDbl")]
        public static void Dbl(ref int dbl)
        {
            dbl *= 2;
        }
        //
        [DarkBasicCommand("getVm")]
        public static void GetVm([FromVm]VirtualMachine vm)
        {
            
        }
        
        [DarkBasicCommand("any input")]
        public static void InputAnyType(object x, ref int tc)
        {
            switch (x)
            {
                case int:
                    tc = TypeCodes.INT;
                    break;
                case string:
                    tc = TypeCodes.STRING;
                    break;
                default:
                    tc = -1;
                    break;
            }
        }
        //
        //
        [DarkBasicCommand("sum")]
        public static int Sum(params int[] numbers)
        {
            var sum = 0;
            for (var i = 0; i < numbers.Length; i++)
            {
                sum += numbers[i];
            }
        
            return sum;
        }
        
        
        [DarkBasicCommand("get last")]
        public static int SillyLast(params int[] numbers)
        {
            return numbers[^1];
        }
        
        //
        [DarkBasicCommand("wait key")]
        public static void WaitKey()
        {
            
        }
        //
        [DarkBasicCommand("callTest")]
        public static void CallTest()
        {
            
        }
        [DarkBasicCommand("add")]
        public static int AddTest(int a, int b)
        {
            return a + b;
        }
        //
        [DarkBasicCommand("min")]
        public static int Min(int a, int b)
        {
            return Math.Min(a, b);
        }
        //
        //
        [DarkBasicCommand("inc")]
        public static void Inc(ref int variable, int amount = 1)
        {
            variable += amount;
        }
        //
        //
        [DarkBasicCommand("print")]
        public static void Tuna(params object[] variable)
        {
            Console.WriteLine(string.Join("\n", variable));
        }
        
        [DarkBasicCommand("concat")]
        public static string Concat(params object[] variable)
        {
            return string.Join(";", variable);
        }
        
        //
        [DarkBasicCommand("len")]
        public static int Length(string x)
        {
            return x.Length;
        }
        //
        [DarkBasicCommand("reverse")]
        public static string Reverse(string x)
        {
            var sb = new StringBuilder();
            for (var i = x.Length - 1; i >= 0; i--)
            {
                sb.Append(x[i]);
            }
            return sb.ToString();
        }
        //
        // [DarkBasicCommand("overloadA")]
        // public static int OverloadA(string x)
        // {
        //     return x.Length;
        // }
        //     
        // // [CommandName("overloadA")]
        // // public static int OverloadA(int x)
        // // {
        // //     return x * 2;
        // // }
        //
        [DarkBasicCommand("tuna")]
        public static void Tuna(ref string x)
        {
            x = "tuna";
        }
    }
}