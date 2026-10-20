using System;
using System.Runtime.CompilerServices;
using FadeBasic.Virtual;

namespace FadeBasic.Compiled
{
    /// <summary>
    /// A runtime error raised by compiled Fade code. It is a <see cref="VirtualRuntimeException"/>
    /// so hosts can treat both backends the same way. There is no instruction index; instead
    /// <see cref="SourceLine"/> is the (0-based, concatenated-source) line of the Fade statement.
    /// </summary>
    public class CompiledFadeRuntimeException : VirtualRuntimeException
    {
        public int SourceLine { get; }

        public CompiledFadeRuntimeException(VirtualRuntimeErrorType type, string message, int sourceLine)
            : base(new VirtualRuntimeError
            {
                type = type,
                insIndex = -1,
                message = message,
                callStack = Array.Empty<JumpHistoryData>()
            })
        {
            SourceLine = sourceLine;
        }
    }

    /// <summary>
    /// Thrown by `end`, so a compiled program can stop from any call depth. The driver
    /// in <see cref="CompiledFadeProgram"/> catches it.
    /// </summary>
    public sealed class FadeEndProgramException : Exception
    {
        public static readonly FadeEndProgramException Instance = new FadeEndProgramException();
        FadeEndProgramException() : base("fade program ended") { }
    }

    /// <summary>
    /// Helpers for generated code. Each one reproduces a VM operation exactly (see VmUtil),
    /// including its quirks. The simple numeric cases are emitted inline by the transpiler;
    /// only the ones with branches, errors or odd semantics live here.
    /// </summary>
    public static class FadeRt
    {
        // ---- errors -------------------------------------------------------------------

        public static Exception InvalidAddress(int index, int size, int site) =>
            new CompiledFadeRuntimeException(VirtualRuntimeErrorType.INVALID_ADDRESS,
                $"invalid-address. index=[{index}] min=[0] max=[{size}]", site);

        static Exception DivideByZero(int site) =>
            new CompiledFadeRuntimeException(VirtualRuntimeErrorType.DIVIDE_BY_ZERO, "divide-by-zero.", site);

        public static Exception Unsupported(string what) =>
            new NotImplementedException(what);

        /// <summary>A cast the VM has no conversion for. The VM throws at runtime, so do we.</summary>
        public static TOut BadCast<TIn, TOut>(TIn value, byte from, byte to) =>
            throw new NotImplementedException($"casts from typeCode=[{from}] types are not supported. target=[{to}]");

        /// <summary>An operation the VM has no case for (e.g. `not` on a string).</summary>
        public static T BadOp<T>(string op, byte typeCode) =>
            throw new Exception($"Unsupported {op} operation typecode=[{typeCode}]");

        public static T BadOp<TA, T>(TA operand, string op, byte typeCode) =>
            throw new Exception($"Unsupported {op} operation typecode=[{typeCode}]");

        public static T BadOp<TA, TB, T>(TA a, TB b, string op, byte typeCode) =>
            throw new Exception($"Unsupported {op} operation typecode=[{typeCode}]");

        public static void AssertFailed(string reason, string source, int site) =>
            throw new CompiledFadeRuntimeException(VirtualRuntimeErrorType.ASSERT_FAILED,
                string.IsNullOrEmpty(reason) ? $"assert failed: {source}" : $"assert failed: {source} ({reason})", site);

        // ---- division (zero checked, even for floats) -----------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Div(int a, int b, int site) { if (b == 0) throw DivideByZero(site); return a / b; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Div(long a, long b, int site) { if (b == 0) throw DivideByZero(site); return a / b; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Div(uint a, uint b, int site) { if (b == 0) throw DivideByZero(site); return a / b; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Div(ushort a, ushort b, int site) { if (b == 0) throw DivideByZero(site); return (ushort)(a / b); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Div(byte a, byte b, int site) { if (b == 0) throw DivideByZero(site); return (byte)(a / b); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Div(float a, float b, int site) { if (b == 0) throw DivideByZero(site); return a / b; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Div(double a, double b, int site) { if (b == 0) throw DivideByZero(site); return a / b; }

        // ---- mod (not zero checked: integer mod by zero throws DivideByZeroException) -------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Mod(int a, int b) => a % b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Mod(long a, long b) => a % b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Mod(uint a, uint b) => a % b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Mod(ushort a, ushort b) => (ushort)(a % b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Mod(byte a, byte b) => (byte)(a % b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Mod(float a, float b) => a % b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Mod(double a, double b) => a % b;

        // ---- power ----------------------------------------------------------------------

        public static float Pow(float a, float b, int site)
        {
            var r = (float)Math.Pow((double)a, (double)b);
            if (float.IsNaN(r))
            {
                throw new CompiledFadeRuntimeException(VirtualRuntimeErrorType.INVALID_POWER,
                    "invalid-power-expression.", site);
            }
            return r;
        }

        // ---- equality -------------------------------------------------------------------

        /// <summary>REAL `=`: |a-b| &lt;= float.Epsilon, i.e. effectively exact.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Eq(float a, float b) => Math.Abs(a - b) <= float.Epsilon ? 1 : 0;

        /// <summary>
        /// DFLOAT `=`: the VM computes a double 1.0/0.0 but pushes it as a 4-byte INT, and
        /// the low four bytes of both are zero. So this is always 0.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Eq(double a, double b) => 0;

        public static int Eq(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal) ? 1 : 0;

        // ---- string ordering (GT/GTE use CompareOrdinal == 1 and != -1) ------------------

        public static int Gt(string a, string b) => string.CompareOrdinal(a ?? "", b ?? "") == 1 ? 1 : 0;
        public static int Gte(string a, string b) => string.CompareOrdinal(a ?? "", b ?? "") == -1 ? 0 : 1;

        public static string Concat(string a, string b) => string.Concat(a, b);

        public static int Len(string s) => s?.Length ?? 0;

        // ---- select/case keys: VmUtil.ReadSpanAsUInt -------------------------------------
        // 4-byte values are read as int and sign-extended; other sizes are zero-extended.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Key(int v) => (ulong)v;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Key(uint v) => (ulong)unchecked((int)v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Key(float v) => (ulong)BitConverter.ToInt32(BitConverter.GetBytes(v), 0);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Key(long v) => unchecked((ulong)v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Key(double v) => unchecked((ulong)BitConverter.DoubleToInt64Bits(v));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Key(ushort v) => v;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Key(byte v) => v;

        // ---- `params` arguments are not cast by the compiler; the executor reads raw bytes

        public static T Reinterpret<TFrom, T>(TFrom value) where TFrom : struct where T : struct
        {
            // MemoryMarshal.Read over a too-short span throws, in the VM as well.
            var box = new TFrom[] { value };
            return System.Runtime.InteropServices.MemoryMarshal.Read<T>(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(box.AsSpan()));
        }

        // ---- `ref` command arguments on registers ------------------------------------------
        // A register is a ulong. STORE puts a 4-byte value in sign-extended, anything else
        // zero-extended. A command reads its ref argument with MemoryMarshal.Read<T> over the
        // register's 8 bytes, and writes it back as BitConverter.ToUInt32 of the value's bytes:
        // only the low 32 bits survive, and values shorter than 4 bytes throw.

        public static TParam RegRead<TReg, TParam>(TReg value) where TReg : struct where TParam : struct
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new[] { value }.AsSpan());
            ulong raw;
            if (bytes.Length == 4) raw = (ulong)BitConverter.ToInt32(bytes.ToArray(), 0);
            else
            {
                var wide = new byte[8];
                bytes.CopyTo(wide);
                raw = BitConverter.ToUInt64(wide, 0);
            }
            return System.Runtime.InteropServices.MemoryMarshal.Read<TParam>(BitConverter.GetBytes(raw));
        }

        public static TReg RegWrite<TParam, TReg>(TParam value) where TReg : struct where TParam : struct
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new[] { value }.AsSpan()).ToArray();
            ulong raw = BitConverter.ToUInt32(bytes, 0);
            return System.Runtime.InteropServices.MemoryMarshal.Read<TReg>(BitConverter.GetBytes(raw));
        }

        // ---- len / bytes -----------------------------------------------------------------

        /// <summary>`len(arr, d)` with a runtime dimension: d is bounds-checked against the rank.</summary>
        public static int LenDim<T>(FadeArray<T> array, int dimension, int rank, int site)
        {
            if (dimension < 0 || dimension >= rank) throw InvalidAddress(dimension, rank, site);
            return array == null ? 0 : array.GetSize(dimension);
        }

        /// <summary>Evaluates a value for its side effects and returns a constant (`bytes(expr)`).</summary>
        public static int Discard<T>(T value, int result) => result;

        // ---- command bridging ------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte FromBool(bool b) => b ? (byte)1 : (byte)0;

        /// <summary>
        /// Hands the host-context VM to a command, first setting its instruction index to a
        /// unique call-site id (commands such as debug UI widgets use it as a stable identity).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static VirtualMachine At(VirtualMachine vm, int site)
        {
            vm.instructionIndex = site;
            return vm;
        }

        public static object[] Box<T>(T[] values)
        {
            var result = new object[values.Length];
            for (var i = 0; i < values.Length; i++) result[i] = values[i];
            return result;
        }
    }
}
