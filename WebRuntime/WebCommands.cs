using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using FadeBasic.SourceGenerators;

namespace WebRuntime;

public partial class WebCommands
{
    private static readonly StringBuilder _printBuffer = new();

    public static string DrainPrintBuffer()
    {
        var s = _printBuffer.ToString();
        _printBuffer.Clear();
        return s;
    }

    [FadeBasicCommand("print", FadeBasicCommandUsage.Runtime)]
    public static void Print(params object[] elements)
    {
        foreach (var el in elements)
        {
            var line = el?.ToString() ?? "";
            _printBuffer.AppendLine(line);
            Console.WriteLine(line);
            // Live stream — main thread's web-commands.js exports a no-op;
            // worker's setModuleImports overrides it to postMessage back to the page.
            try { WebInterop.OnPrint(line); } catch { /* module not yet registered */ }
        }
    }

    [FadeBasicCommand("location")]
    public static string Location() => WebInterop.GetLocation();

    [FadeBasicCommand("user agent")]
    public static string UserAgent() => WebInterop.GetUserAgent();

    [FadeBasicCommand("time ms")]
    public static int TimeMs() =>
        (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0x7FFFFFFF);

    [FadeBasicCommand("alert")]
    public static void Alert(string msg) => WebInterop.Alert(msg);
}

[SupportedOSPlatform("browser")]
internal static partial class WebInterop
{
    [JSImport("getLocation", "web-commands")]
    internal static partial string GetLocation();

    [JSImport("getUserAgent", "web-commands")]
    internal static partial string GetUserAgent();

    [JSImport("alert", "web-commands")]
    internal static partial void Alert(string msg);

    [JSImport("onPrint", "web-commands")]
    internal static partial void OnPrint(string line);
}
