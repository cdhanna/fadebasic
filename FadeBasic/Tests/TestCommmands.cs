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

        [FadeBasicCommand("macroFuncDemo", FadeBasicCommandUsage.Macro)]
        public static void Example(int x, ref int id)
        {
            id = x * 2;
        }
        [FadeBasicCommand("macro return demo", FadeBasicCommandUsage.Macro)]
        public static int Example()
        {
            return 42;
        }
        [FadeBasicCommand("macroReturnDemo", FadeBasicCommandUsage.Macro)]
        public static int Example2()
        {
            return 42;
        }
        
        [FadeBasicCommand("rnd")]
        public static int Random(int max=10)
        {
            var r = new Random();
            return r.Next(max);
        }
        
        
        [FadeBasicCommand("now", FadeBasicCommandUsage.Both)]
        public static long Now()
        {
            var now = DateTimeOffset.Now;
            return (long)now.ToUnixTimeMilliseconds();
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
            waitMsCallCount++;
        }
        //
        [FadeBasicCommand("callDemo")]
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

        // --- Overload-resolution fixtures ---------------------------------
        // `bump` is a by-ref command overloaded on element type. The type pass
        // must pick the int or real variant based on the argument's type.
        [FadeBasicCommand("ovrbump")]
        public static void BumpInt(ref int value, int amount)
        {
            value += amount;
        }
        [FadeBasicCommand("ovrbump")]
        public static void BumpReal(ref float value, float amount)
        {
            value += amount;
        }

        // Mirrors the real stdlib `inc` ref-float overload EXACTLY, including
        // the optional `amount = 1` default — used to test that the default
        // value is injected as a float (not raw int bytes read as a float).
        [FadeBasicCommand("incf")]
        public static void IncF(ref float value, float amount = 1)
        {
            value += amount;
        }

        // Mirrors the FULL stdlib `inc` overload set: ref-int and ref-float,
        // both with an optional `amount = 1`. Reproduces overload selection for
        // a float target when both overloads carry defaults.
        [FadeBasicCommand("incx")]
        public static void IncxInt(ref int value, int amount = 1)
        {
            value += amount;
        }
        [FadeBasicCommand("incx")]
        public static void IncxReal(ref float value, float amount = 1)
        {
            value += amount;
        }

        // `twice` is a value-arg command overloaded on element type, returning
        // the same type from every overload — exercises overload selection in
        // expression context.
        [FadeBasicCommand("ovrtwice")]
        public static int TwiceInt(int x)
        {
            return x * 2;
        }
        [FadeBasicCommand("ovrtwice")]
        public static int TwiceReal(float x)
        {
            return (int)(x * 2);
        }

        // `ovrret` overloads differ in BOTH parameter type and return type —
        // a perfectly legal overload set. Selection is driven by the argument
        // type, and the expression's type is whatever the winning overload
        // returns (int for an int arg, string for a string arg).
        [FadeBasicCommand("ovrret")]
        public static int RetInt(int x)
        {
            return x;
        }
        [FadeBasicCommand("ovrret")]
        public static string RetStr(string x)
        {
            return x;
        }

        // `pairf` overloads on float vs double (same return type). An integer
        // argument widens equally well to either, so `pairf(3)` is genuinely
        // ambiguous and must report CommandOverloadAmbiguous.
        [FadeBasicCommand("ovrpair")]
        public static int PairF(float x)
        {
            return 1;
        }
        [FadeBasicCommand("ovrpair")]
        public static int PairD(double x)
        {
            return 2;
        }

        // `ovrarity` overloads purely on arity (both return int). Selection is
        // by argument COUNT — resolved at parse time, before types are known.
        [FadeBasicCommand("ovrarity")]
        public static int ArityOne(int a)
        {
            return a;
        }
        [FadeBasicCommand("ovrarity")]
        public static int ArityTwo(int a, int b)
        {
            return a + b;
        }

        // `ovrv` is the void/statement-context version of arity overloading, and
        // the first parameter is by-ref.
        [FadeBasicCommand("ovrv")]
        public static void VOne(ref int a)
        {
            a += 1;
        }
        [FadeBasicCommand("ovrv")]
        public static void VTwo(ref int a, int b)
        {
            a += b;
        }

        // `ovrmix` combines both axes: two same-arity overloads that differ by
        // type, plus a higher-arity overload. Arity narrows first (parse), then
        // type resolves within the winning arity (type pass).
        [FadeBasicCommand("ovrmix")]
        public static int MixInt(int a)
        {
            return a;
        }
        [FadeBasicCommand("ovrmix")]
        public static int MixReal(float a)
        {
            return (int)a;
        }
        [FadeBasicCommand("ovrmix")]
        public static int MixTwo(int a, int b)
        {
            return a + b;
        }
        //
        //
        [FadeBasicCommand("print")]
        public static void Tuna(params object[] variable)
        {
            Console.WriteLine(string.Join("\n", variable));
        }

        // Counter incremented every time `wait ms` is invoked (real path only,
        // mocks bypass the executor). Mock execution tests reset and inspect
        // this to confirm the host method was/wasn't actually called.
        public static int waitMsCallCount = 0;

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
        
        [FadeBasicCommand("refFloat")]
        public static void Tuna(ref float x)
        {
            x *= 2;
        }

        
        [FadeBasicCommand("tuna_echo")]
        public static void TunaEcho(int a, ref string x)
        {
            x = "t" + a;
        }
        [FadeBasicCommand("tuna_echo2")]
        public static void TunaEcho2(int a, ref string x)
        {
            if (a >= 1)
            {
                x = x + "more";
            }
            // otherwise, x is left un-assigned. 
        }
        
        [FadeBasicCommand("tuna_opt_string")]
        public static void TunaEcho(int a, string x="")
        {
            // do nothing :shrug:
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
        
        [FadeBasicCommand("str$", FadeBasicCommandUsage.Both)]
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