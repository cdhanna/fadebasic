using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.JSInterop;
using FadeBasic;
using FadeBasic.Lib.Standard;
using FadeBasic.Sdk;

namespace WebRuntime;

[SupportedOSPlatform("browser")]
public static partial class FadeBridge
{
    // [JSInvokable] is the Blazor-host bridge (main thread, DotNet.invokeMethodAsync).
    // [JSExport]   is the runtime-level bridge (worker, getAssemblyExports). Same body,
    // two front doors so both index.html (Blazor) and worker.html (raw runtime) work.
    [JSInvokable]
    [JSExport]
    public static string CompileAndRun(string source)
    {
        var sb = new StringBuilder();

        // WebCommands provides print (→ console.log + page buffer) and JS-interop
        // commands. StandardCommands provides rgb, wait ms, string ops (upper$,
        // lower$, str$, ...), rnd, timer, etc. ConsoleCommands is intentionally
        // omitted — its print would shadow ours and its inputs need a real TTY.
        var commands = new CommandCollection(new WebCommands(), new StandardCommands());
        if (!Fade.TryCreateFromString(source, commands, out var ctx, out var errors))
        {
            sb.AppendLine("Compile failed:");
            sb.Append(errors.ToDisplay());
            return sb.ToString();
        }

        try
        {
            ctx.Run();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Runtime error: {ex.GetType().Name}: {ex.Message}");
        }

        var printed = WebCommands.DrainPrintBuffer();
        if (!string.IsNullOrEmpty(printed))
        {
            sb.AppendLine("--- print output ---");
            sb.Append(printed);
        }

        sb.AppendLine("--- variables ---");
        if (ctx.TryGetInteger("x", out var x)) sb.AppendLine($"x = {x}");
        if (ctx.TryGetInteger("y", out var y)) sb.AppendLine($"y = {y}");
        if (ctx.TryGetString("s", out var s)) sb.AppendLine($"s = \"{s}\"");

        return sb.ToString();
    }
}
