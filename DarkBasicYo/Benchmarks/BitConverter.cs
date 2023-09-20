using BenchmarkDotNet.Attributes;

namespace Benchmarks;

[MemoryDiagnoser()]
public class BitConverterTests
{
    private byte[] bytes;
    private int size;
    [GlobalSetup]
    public void Setup()
    {
        bytes = new byte[8];
        size = 4;
        for (var i = 0; i < size; i++)
        {
            bytes[i] = (byte)i;
        }
    }

    [Benchmark()]
    public void Baseline()
    {
        ulong x = BitConverter.ToUInt64(bytes, 0);
    }

    [Benchmark()]
    public void MyDumbWay()
    {
        ulong x = 0;
        switch (size)
        {
            case 4:
                x = (x + bytes[0]) + ((x + bytes[1]) << 8) + ((x + bytes[2]) << 16) + ((x + bytes[3]) << 24);
                break;
        }
        // for (var i = 0; i < bytes.Length; i++)
        // {
        //     x = (x + (ulong)i) << (i * 8);
        // }
        // x = (x + 1) + ((2 + x) << 8)+ ((3 + x) << 16)+ ((4 + x) << 24);
    }
}