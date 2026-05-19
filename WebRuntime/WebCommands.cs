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

    /// <summary>
    /// Displays a web based alert to the user
    /// </summary>
    /// <param name="str">the message</param>
    [FadeBasicCommand("alert")]
    public static void Alert(string msg) => WebInterop.Alert(msg);

    // Synchronous user-input prompt for scripting and tests. Returns the
    // entered string (or an empty string if the user cancels). The host JS
    // bridge implements the actual prompt — main-thread mode uses window.prompt,
    // worker mode posts a request and blocks on the response.
    [FadeBasicCommand("prompt$")]
    public static string Prompt(string message) => WebInterop.Prompt(message);
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

    [JSImport("prompt", "web-commands")]
    internal static partial string Prompt(string msg);

    // Cooperative `wait ms` for WASM. The JS-side impl blocks on
    // Atomics.wait(timeout=ms) over a shared buffer; the main thread can
    // Atomics.notify the buffer to wake the wait early (e.g. when the
    // user clicks Pause / Stop). Returns the milliseconds actually waited
    // — call sites can ignore it.
    [JSImport("waitMsInterruptible", "web-commands")]
    internal static partial int WaitMsInterruptible(int milliseconds);
}
