using System;

namespace FadeBasic.Testing
{
    /// <summary>
    /// Tag a class implementing <see cref="IFadeTestHost"/> with this attribute
    /// to have <see cref="FadeTestApplicationBuilder"/> discover and use it
    /// automatically when running under <c>dotnet test</c>. If multiple classes
    /// in the entry assembly carry this attribute, the test app will fail with
    /// a clear error listing all candidates — exactly one is permitted.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class FadeTestHostAttribute : Attribute
    {
    }
}
