using FadeBasic.Virtual;

namespace Tests;

public static class TestExtensions
{
    public static VmPtr ToPtr(this ulong raw) => VmPtr.FromRaw(raw);

    public static VmPtr ToPtr(this int raw) => new VmPtr
    {
        bucketPtr = 0,
        memoryPtr = raw
    };
}