using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using FadeBasic.SourceGenerators;
using FadeBasic.Virtual;

namespace FadeBasic.Lib.Web;

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

    /// <summary>Displays a browser alert.</summary>
    [FadeBasicCommand("alert")]
    public static void Alert(string msg) => WebInterop.Alert(msg);

    /// <summary>Synchronous user-input prompt. Returns the entered string, or empty if cancelled.</summary>
    // ─── Cooperative suspend model ────────────────────────────────────
    // The C# call doesn't actually wait — it asks the host runtime to do
    // two things and then returns an empty placeholder:
    //
    //   1. PostMessage("fade-web/prompt", message) — the host forwards
    //      this to whatever UI is consuming runtime events. In the WASM
    //      bundle that's a postMessage from worker → page; the page's
    //      hostHandlers map dispatches by channel name.
    //
    //   2. SuspendVm() — pauses the current VM. The host knows which VM
    //      is currently being pumped.
    //
    // The placeholder pushed by the source-generated executor stays on
    // the operand stack until the page replies with a `host-reply`; the
    // runtime's DepositResultString JSExport swaps it for the real answer
    // before the next opcode runs. From Fade source's POV `y$ = prompt$("?")`
    // is still one synchronous expression.
    //
    // Lib.Web does not know who the host is and does not need to. Any
    // other plugin library can use the same primitives with a different
    // channel name and the consumer wires up the page-side handler in
    // their own index.html — no changes to FadeBasic, FadeBasic.Export.Web,
    // or worker.js required.
    [FadeBasicCommand("prompt$")]
    public static string Prompt(string message)
    {
        HostBridge.PostMessage?.Invoke("fade-web/prompt", message);
        HostBridge.SuspendVm?.Invoke();
        return "";
    }
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
