using BenchmarkDotNet.Attributes;

namespace Benchmarks;

[MemoryDiagnoser()]
public class BytesVsInts
{
    private byte[] bytes;
    private uint[] ints;

    [GlobalSetup]
    public void Setup()
    {
        bytes = new byte[1024];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 255);
        }

        ints = new uint[256];
        for (var i = 0; i < ints.Length; i++)
        {
            ints[i] = ints[i] << 3;
        }
    }

    [Benchmark()]
    public void ByteCheck()
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
        }
    }

    [Benchmark()]
    public unsafe void ByteCheckUnsafe()
    {
        fixed (byte* ptr = bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                var b = ptr[i];
            }
        }
        
    }
    
    // [Benchmark()]
    public unsafe void IntCheck()
    {
        var subIndex = 0;
        var insIndex = 0;
        for (var i = 0; i < ints.Length * 4; i++)
        {
            var x = ints[insIndex];
            byte* pi = (byte*)&x;
            var b = pi[ subIndex ++];
            if (subIndex == 4)
            {
                subIndex = 0;
                insIndex++;
            }
        }
    }
    
    
}