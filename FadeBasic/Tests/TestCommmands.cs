using System.Runtime.InteropServices;
using System.Text;
using FadeBasic;
using FadeBasic.SourceGenerators;
using FadeBasic.Virtual;

namespace Tests
{

    public partial class TestCommands
    {
        public static readonly CommandCollection CommandsForTesting = new CommandCollection(new TestCommands());

        [FadeBasicCommand("rnd")]
        public static int Random(int max=10)
        {
            var r = new Random();
            return r.Next(max);
        }
        
        [FadeBasicCommand("Jerk")]
        public static void Dbjerkl(int dbl)
        {
            dbl *= 2;
        }
        //
        [FadeBasicCommand("refDbl")]
        public static void Dbl(ref int dbl)
        {
            dbl *= 2;
        }
        //
        [FadeBasicCommand("getVm")]
        public static void GetVm([FromVm]VirtualMachine vm)
        {
            
        }
        
        [FadeBasicCommand("any input")]
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
        [FadeBasicCommand("sum")]
        public static int Sum(params int[] numbers)
        {
            var sum = 0;
            for (var i = 0; i < numbers.Length; i++)
            {
                sum += numbers[i];
            }
        
            return sum;
        }
        [FadeBasicCommand("sum2")]
        public static int Sum2([FromVm]VirtualMachine x, params int[] numbers)
        {
            var sum = 0;
            for (var i = 0; i < numbers.Length; i++)
            {
                sum += numbers[i];
            }
        
            return sum;
        }
        
        [FadeBasicCommand("cls")]
        public static void ClearScreen([FromVm] VirtualMachine vm, int color=0)
        {
            
        }

        
        
        [FadeBasicCommand("get last")]
        public static int SillyLast(params int[] numbers)
        {
            return numbers[^1];
        }
        
        //
        [FadeBasicCommand("wait key")]
        public static void WaitKey()
        {
            
        }
        
        [FadeBasicCommand("wait ms")]
        public static void WiatMs(int amount)
        {
            
        }
        //
        [FadeBasicCommand("callTest")]
        public static void CallTest()
        {
            
        }
        [FadeBasicCommand("add")]
        public static int AddTest(int a, int b)
        {
            return a + b;
        }
        
        //
        [FadeBasicCommand("screen width")]
        public static int ScreenWidth()
        {
            return 5;
        }
        
        //
        [FadeBasicCommand("min")]
        public static int Min(int a, int b)
        {
            return Math.Min(a, b);
        }
        //
        //
        [FadeBasicCommand("inc")]
        public static void Inc(ref int variable, int amount = 1)
        {
            variable += amount;
        }
        //
        //
        [FadeBasicCommand("print")]
        public static void Tuna(params object[] variable)
        {
            Console.WriteLine(string.Join("\n", variable));
        }

        public static List<string> staticPrintBuffer = new List<string>();
        [FadeBasicCommand("static print")]
        public static void StaticPrint(params object[] variable)
        {
            staticPrintBuffer.AddRange(variable.Select(x => x.ToString()));
        }

        [FadeBasicCommand("all the prims")]
        public static string Todos(int integer, ushort word, uint dword, long dint, float real, double dFloat, byte b, bool b2)
        {
            return $"{integer},{word},{dword},{dint},{real},{dFloat},{b},{b2}";
        }

        [FadeBasicCommand("prim test di")]
        public static long PrimTest_ReturnLong(long x) => x * 2;
        
        
        [FadeBasicCommand("prim test w")]
        public static ushort PrimTest_ReturnWord(ushort x) => (ushort)(x * 2);
        
        
        [FadeBasicCommand("prim test dw")]
        public static uint PrimTest_ReturnDWord(uint x) => x * 2;
        
        
        [FadeBasicCommand("prim test f")]
        public static float PrimTest_ReturnFloat(float x) => x * 2;
        
        
        [FadeBasicCommand("prim test df")]
        public static double PrimTest_ReturnDouble(double x) => x * 2;
        
        
        [FadeBasicCommand("prim test b")]
        public static byte PrimTest_ReturnByte(byte x) => (byte)(x * 2);
        
        
        [FadeBasicCommand("prim test b2")]
        public static bool PrimTest_ReturnBool(bool x) => !x;

        
        [FadeBasicCommand("concat")]
        public static string Concat(params object[] variable)
        {
            return string.Join(";", variable);
        }

        [FadeBasicCommand("retandref")]
        public static int ReturnAndRef(ref int a)
        {
            // this command is interesting because it can declare a variable via the ref param, and also as a return.
            a = 3;
            return a;
        }
        
        //
        [FadeBasicCommand("len")]
        public static int Length(string x)
        {
            return x.Length;
        }
        //
        [FadeBasicCommand("reverse")]
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
        [FadeBasicCommand("overloadA")]
        public static int OverloadA(int a)
        {
            return a * 2;
        }
            
        [FadeBasicCommand("overloadA")]
        public static int OverloadA(int x, int b)
        {
            return x + b;
        }
        //
        [FadeBasicCommand("tuna")]
        public static void Tuna(ref string x)
        {
            x = "tuna";
        }
        
        
        //
        [FadeBasicCommand("upper$")]
        public static string Upper(string x)
        {
            return x.ToUpperInvariant();
        }
        
        [FadeBasicCommand("complexArg")]
        public static void ComplexArg([FromVm] VirtualMachine vm, RawArg<int> arg)
        {
            VmUtil.HandleValue(vm, arg.value * 2, TypeCodes.INT, arg.state, arg.address);
        }
        
        
        [FadeBasicCommand("rgb")]
        public static int RgbToHex(byte r, byte g, byte b)
        {
            var color = 0;
            color = r;
            color = color + g << 4;
            color = color + b << 8;
            return color;
        }

        [FadeBasicCommand("ink")]
        public static void Ink([FromVm] VirtualMachine vm, int foreground, int background)
        {
            
        }
        
        
        
        [FadeBasicCommand("write byte")]
        public static string WriteData(int fileNumber, byte data)
        {
            throw new NotImplementedException("");
        }
        [FadeBasicCommand("write float")]
        public static string WriteData(int fileNumber, float data)
        {
            throw new NotImplementedException("");
        }
        [FadeBasicCommand("write long")]
        public static string WriteData(int fileNumber, long data)
        {
            throw new NotImplementedException("");
        }
        [FadeBasicCommand("write string")]
        public static string WriteData(int fileNumber, string data)
        {
            throw new NotImplementedException("");
        }
        [FadeBasicCommand("write word")]
        public static string WriteData(int fileNumber, short data)
        {
            throw new NotImplementedException("");
        }
        
        [FadeBasicCommand("get dir$")]
        public static string GetWorkingDirectory()
        {
            throw new NotImplementedException("get the working directory");
        }
        
        [FadeBasicCommand("str$")]
        public static string Str(object x)
        {
            return x?.ToString() ?? "";
        }
        
                
        [FadeBasicCommand("file end")]
        public static int IsFileEnd([FromVm] VirtualMachine vm, int fileNumber)
        {
            return fileNumber; // eh?
        }
    }
}