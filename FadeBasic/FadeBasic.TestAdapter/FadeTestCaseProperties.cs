using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace FadeBasic.TestAdapter
{
    /// <summary>
    /// Custom <see cref="TestProperty"/> registrations carried on each emitted
    /// <see cref="TestCase"/>. These survive the round-trip from discoverer to
    /// executor, letting the executor look up the matching
    /// <c>TestManifestEntry</c> by stable ID rather than by display name (which
    /// can collide across abstract parents and concrete children).
    /// </summary>
    internal static class FadeTestCaseProperties
    {
        public static readonly TestProperty EntryPointAddress = TestProperty.Register(
            id: "FadeBasic.EntryPointAddress",
            label: "Fade Entry Point Address",
            valueType: typeof(int),
            owner: typeof(FadeTestDiscoverer));

        public static readonly TestProperty FromParent = TestProperty.Register(
            id: "FadeBasic.FromParent",
            label: "Fade From-Parent",
            valueType: typeof(string),
            owner: typeof(FadeTestDiscoverer));

        public static readonly TestProperty FbasicSourceFile = TestProperty.Register(
            id: "FadeBasic.SourceFile",
            label: "Fade Source File",
            valueType: typeof(string),
            owner: typeof(FadeTestDiscoverer));

        // ManagedType / ManagedMethod are how IDE Test Explorers (Rider,
        // VS Code C# Dev Kit, Visual Studio) split a TestCase into its
        // namespace.class.method tree path. ObjectModel registers these as
        // PRIVATE static fields on TestCase, so we can't reference them
        // directly. TestProperty.Register is idempotent on the `id` —
        // calling it with the same canonical IDs returns the framework's
        // own internal instance, so SetPropertyValue against ours is the
        // same write the framework would do internally.
        public static readonly TestProperty ManagedType = TestProperty.Register(
            id: "TestCase.ManagedType",
            label: "ManagedType",
            valueType: typeof(string),
            owner: typeof(TestCase));

        public static readonly TestProperty ManagedMethod = TestProperty.Register(
            id: "TestCase.ManagedMethod",
            label: "ManagedMethod",
            valueType: typeof(string),
            owner: typeof(TestCase));
    }
}
