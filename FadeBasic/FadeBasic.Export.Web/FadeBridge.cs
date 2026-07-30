using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using FadeBasic;
using FadeBasic.Ast.Visitors;
using FadeBasic.Json;
using FadeBasic.Launch;
using FadeBasic.Lib.Standard;
using FadeBasic.Sdk;
using FadeBasic.Virtual;
using FadeSdk = FadeBasic.Sdk.Fade;
using FadeBasic.LSP.Core;
using FadeBasic.LSP.Core.Handlers;

namespace FadeBasic.Export.Web;

// FadeBridge is the browser-side adapter between the worker's postMessage
// surface and the cross-platform LSP logic in FadeBasic.LSP.Core. The native
// LSP server in FadeBasic/LSP/ will get the same Core handlers behind its
// OmniSharp transport once it's refactored.
[SupportedOSPlatform("browser")]
public static partial class FadeBridge
{
    // Dynamically-registered command sources. Cleared by ClearCommandAssemblies
    // and rebuilt from fade.json's commandDlls on every project-type change.
    // MUST be declared before _workspace — static field initializers run in
    // declaration order and CreateWorkspace reads this list.
    private static readonly List<IMethodSource> _registeredSources = new();

    // Source-generated metadata blobs (`<ClassName>MetaData.COMMANDS_JSON`)
    // pulled out of each registered assembly. Feeds the workspace's docs
    // provider so hover/help can render rich markdown for commands from
    // dynamically-loaded libraries, not just StandardCommands.
    private static readonly List<string> _registeredCommandJsonBlobs = new();

    // Dynamically-loaded assemblies keyed by simple name. WASM's default
    // AssemblyLoadContext doesn't fall back to "scan loaded assemblies by
    // simple name" when binding type references the way desktop CLR does —
    // so when the entry assembly's static cctor does `new SomeLib.Foo()`,
    // resolution fails unless we hand the assembly back through Resolving.
    private static readonly Dictionary<string, Assembly> _dynamicAssemblies = new();
    private static bool _resolverHooked;

    private static void EnsureResolverHooked()
    {
        if (_resolverHooked) return;
        _resolverHooked = true;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
            _dynamicAssemblies.TryGetValue(name.Name ?? "", out var asm) ? asm : null;
    }

    // Load `bytes` into the default ALC and register the loaded assembly so
    // the Resolving handler can hand it back when the entry's type binder
    // looks it up by simple name. Duplicates with _framework/ are harmless:
    // default resolution finds those first, and Resolving only fires when
    // default resolution fails — so the byte-loaded copy is reachable only
    // for the assemblies that aren't already in _framework/.
    private static Assembly LoadAndRegister(byte[] bytes)
    {
        EnsureResolverHooked();
        var asm = Assembly.Load(bytes);
        var simpleName = asm.GetName().Name;
        if (!string.IsNullOrEmpty(simpleName))
            _dynamicAssemblies[simpleName] = asm;
        return asm;
    }

    // Active workspace — rebuilt by SetProjectType and RegisterCommandAssembly.
    private static FadeWorkspace _workspace = CreateWorkspace("web");
    private static string _activeProjectType = "web";

    private static FadeWorkspace CreateWorkspace(string projectType)
    {
        var sources = new List<IMethodSource>(_registeredSources) { new StandardCommands() };
        var commands = new CommandCollection(sources.ToArray());

        // Docs follow whatever's registered: StandardCommands is always
        // there, plus one COMMANDS_JSON blob per dynamically-loaded
        // assembly (collected in RegisterCommandAssembly). projectType
        // doesn't pick the docs anymore — the assemblies themselves do.
        var blobs = new List<string>(_registeredCommandJsonBlobs.Count + 1)
        {
            StandardCommandsMetaData.COMMANDS_JSON,
        };
        blobs.AddRange(_registeredCommandJsonBlobs);
        ICommandDocsProvider docs = StandardCommandDocs.Build(blobs.ToArray());
        _ = projectType;

        var ws = new FadeWorkspace(commands);
        ws.Docs = docs;
        return ws;
    }

    // Called by the worker (main.ts → worker.js) when the active fade.json
    // type changes. Rebuilds the workspace with the right CommandCollection
    // so the LSP picks up the new command surface. Returns the new type so
    // the page can log/confirm. Idempotent.
    [JSExport]
    public static string SetProjectType(string projectType)
    {
        var t = (projectType ?? "web").ToLowerInvariant();
        if (t == _activeProjectType) return t;
        _activeProjectType = t;
        _workspace = CreateWorkspace(t);
        return t;
    }

    // Load a command DLL from raw bytes, instantiate the named class, and
    // merge it into the workspace. Both workers (LSP + VM) receive this call
    // so hover/completion and execution see the same command surface.
    // dllBytes is a Uint8Array on the JS side; className is fully-qualified.
    [JSExport]
    public static string RegisterCommandAssembly(byte[] dllBytes, string className)
    {
        try
        {
            var asm = LoadAndRegister(dllBytes);
            var type = asm.GetType(className)
                ?? throw new Exception($"Type '{className}' not found in assembly");
            var instance = Activator.CreateInstance(type) as IMethodSource
                ?? throw new Exception($"'{className}' does not implement IMethodSource");
            _registeredSources.Add(instance);

            // The command source generator emits a sibling `<ClassName>MetaData`
            // type with a `public const string COMMANDS_JSON` carrying the
            // XML doc strings for every command. Pull it out so hover/help
            // can render rich markdown — without this, registered libraries
            // show just the signature shape.
            var metaType = asm.GetType(className + "MetaData");
            var jsonField = metaType?.GetField("COMMANDS_JSON",
                BindingFlags.Public | BindingFlags.Static);
            if (jsonField?.GetRawConstantValue() is string json && !string.IsNullOrEmpty(json))
                _registeredCommandJsonBlobs.Add(json);

            _workspace = CreateWorkspace(_activeProjectType);
            return StatusOk();
        }
        catch (Exception ex)
        {
            return StatusErr(ex);
        }
    }

    // Remove all dynamically-registered command sources and rebuild the workspace.
    // Called by the page before re-registering whenever fade.json's commandDlls changes.
    [JSExport]
    public static string ClearCommandAssemblies()
    {
        _registeredSources.Clear();
        _registeredCommandJsonBlobs.Clear();
        _workspace = CreateWorkspace(_activeProjectType);
        return "true";
    }

    // Load a side-by-side dependency DLL into the AppDomain without
    // registering it as a command source. Used by the export loader to pull
    // in the game's transitive deps (e.g. FadeBasic.Lib.Web.dll) BEFORE the
    // entry assembly is loaded — otherwise resolving the entry's ILaunchable
    // type would fail because referenced assemblies aren't yet present.
    [JSExport]
    public static string LoadAssembly(byte[] dllBytes)
    {
        try
        {
            LoadAndRegister(dllBytes);
            return StatusOk();
        }
        catch (Exception ex)
        {
            return StatusErr(ex);
        }
    }

    // ─── Cooperative pump model ───────────────────────────────────────
    // The web runtime can't run the VM to completion in one synchronous
    // call: while C# is executing, the worker's JS event loop is blocked,
    // so any postMessage from the page (prompt answers, pause/stop) sits
    // undelivered in the queue. Instead, we hold the VM as static state
    // and the worker pumps it in small budgeted batches, yielding to its
    // event loop between batches. `prompt$` and `wait ms` cooperate by
    // calling vm.Suspend() and stashing wake-up state in WebRuntimeBridge;
    // the JS pump reads that state to decide how (and when) to schedule
    // the next tick. See worker.js for the pump driver, WebCommands.cs
    // for the prompt$/wait ms command implementations.

    // All cooperative-pump state + methods live in FadeBasic.Sdk.CooperativePump.
    // FadeBridge delegates its JSExports to CooperativePump and
    // keeps only the things that are genuinely Export.Web-specific:
    // assembly loading (LoadAndRegister), workspace + LSP state, the
    // debug session, and the JS-interop wiring (JSImport / JSExport).

    // Routes C# → JS for HostBridge.PostMessage. runtime.js binds the
    // 'fade-runtime' module to a fan-out that posts `host-message` to
    // the page; the page dispatches by `channel` and replies with a
    // typed `host-reply` that flows back into DepositResultString etc.
    [JSImport("postHostMessage", "fade-runtime")]
    internal static partial void PostHostMessage(string channel, string payload);

    // Static wire-up: hook the cooperative pump into this host. Runs
    // once on first touch of FadeBridge (at runtime boot, when JS
    // resolves the assembly's exports).
    //
    // The pump itself lives in FadeBasic.Sdk.CooperativePump —
    // we just wire its delegate slots so it can fetch our active
    // command set, our WaitImpl override redirects to its cooperative
    // path, and HostBridge.PostMessage / SuspendVm route through it.
    // MonoGame will do an identical wire-up in its own startup.
    static FadeBridge()
    {
        CooperativePump.CommandsAccessor = () => _workspace.Commands;

        StandardCommands.WaitImpl = ms =>
        {
            // Three paths, picked by which driver is in flight:
            //  - Cooperative pump (Run / tests): RunVm non-null → set
            //    pending wait + suspend; JS pump schedules next tick.
            //  - Debug session: _debugSession non-null → same, but on
            //    the debug session's VM; pumpDebugTick handles the
            //    setTimeout cadence.
            //  - Fallback: Thread.Sleep. Should not happen in normal use.
            if (CooperativePump.RunVm != null)
            {
                CooperativePump.OnCooperativeWait(ms);
            }
            else if (_debugSession != null)
            {
                // DebugTick reads _pendingWaitMs out of CooperativePump
                // — sharing the field keeps the JS pump on one source
                // of truth. Suspend the session's VM directly here
                // since CooperativePump only knows about RunVm.
                CooperativePump.OnCooperativeWait(ms);
                _debugSession._vm?.Suspend();
            }
            else
            {
                System.Threading.Thread.Sleep(ms);
            }
        };
        HostBridge.PostMessage = (channel, payload) =>
            PostHostMessage(channel, payload);
        HostBridge.SuspendVm = () => CooperativePump.OnHostReplyWait();
    }

    // Begin a run from an entry assembly's bytes. Host-specific because
    // it loads the consumer's DLL into our AssemblyLoadContext via
    // LoadAndRegister; once we've got the ILaunchable, we hand the VM
    // to the cooperative pump and the rest of the flow is identical to
    // RunStartFromSource / RunStartFromBytecode.
    [JSExport]
    public static string RunStart(byte[] entryDllBytes)
    {
        try
        {
            var asm = LoadAndRegister(entryDllBytes);
            Type launchableType = null;
            foreach (var t in asm.GetTypes())
            {
                if (!t.IsClass || t.IsAbstract) continue;
                if (typeof(ILaunchable).IsAssignableFrom(t)) { launchableType = t; break; }
            }
            if (launchableType == null)
                throw new Exception("No ILaunchable implementation found in entry assembly");
            var instance = (ILaunchable)Activator.CreateInstance(launchableType);
            var vm = new VirtualMachine(instance.Bytecode)
            {
                hostMethods = HostMethodTable.FromCommandCollection(instance.CommandCollection),
            };
            CooperativePump.RunStartWithVm(vm);
            return StatusOk();
        }
        catch (Exception ex)
        {
            return StatusErr(ex);
        }
    }

    // Compile-from-source / bytecode entry points and the compile-only
    // helpers all delegate to CooperativePump. The JSExport wrappers are
    // the only host-specific bit — they expose the pump's static methods
    // through Mono WASM's [JSExport] surface. WebRuntime.MonoGame will
    // expose the same pump via [JSInvokable] wrappers (different surface,
    // same shared logic).
    [JSExport]
    public static string RunStartFromSource(string source) =>
        CooperativePump.RunStartFromSource(source);

    // Hot reload: arm a new source against the running program (returns a verdict
    // JSON) and query the latest verdict. When a DEBUG session is active, target
    // its reload session (DebugTick applies it while keeping the debugger
    // attached); otherwise the run-mode session (RunTick applies it). Both apply
    // at the next clean statement boundary, preserving state.
    [JSExport]
    public static string ReloadArm(string source) =>
        _debugSession?.HotReload != null
            ? CooperativePump.ReloadArmSession(_debugSession.HotReload, source)
            : CooperativePump.ReloadArm(source);

    [JSExport]
    public static string ReloadStatus() =>
        _debugSession?.HotReload != null
            ? CooperativePump.ReloadStatusSession(_debugSession.HotReload)
            : CooperativePump.ReloadStatus();

    [JSExport]
    public static byte[] CompileToBytecode(string source) =>
        CooperativePump.CompileToBytecode(source);

    [JSExport]
    public static string CompileToBytecodeStatus(string source) =>
        CooperativePump.CompileToBytecodeStatus(source);

    [JSExport]
    public static string RunStartFromBytecode(byte[] bytecode) =>
        CooperativePump.RunStartFromBytecode(bytecode);

    // RunTick + StopRun + all Deposit* methods are pump-internal —
    // identical across hosts. Delegate to CooperativePump.

    [JSExport]
    public static string RunTick(int budget) => CooperativePump.RunTick(budget);

    [JSExport]
    public static string StopRun() => CooperativePump.StopRun();

    [JSExport]
    public static string DepositResultString(string value) =>
        CooperativePump.DepositResultString(value);

    [JSExport]
    public static string DepositResultInt(int value) =>
        CooperativePump.DepositResultInt(value);

    [JSExport]
    public static string DepositResultReal(float value) =>
        CooperativePump.DepositResultReal(value);

    [JSExport]
    public static string DepositResultBool(bool value) =>
        CooperativePump.DepositResultBool(value);

    [JSExport]
    public static string DepositResultByte(byte value) =>
        CooperativePump.DepositResultByte(value);

    [JSExport]
    public static string DepositResultWord(int value) =>
        CooperativePump.DepositResultWord(value);

    [JSExport]
    public static string DepositResultDword(int value) =>
        CooperativePump.DepositResultDword(value);

    // int64. The JSMarshalAs annotation tells the JS generator to use
    // BigInt on the JS side — without it the generator refuses to
    // marshal `long` (SYSLIB1072). The page handler should return
    // `{ resultType: 'dint', value: BigInt(...) }` to preserve values
    // past 2^53.
    [JSExport]
    public static string DepositResultDint(
        [JSMarshalAs<JSType.BigInt>] long value) =>
        CooperativePump.DepositResultDint(value);

    [JSExport]
    public static string DepositResultDfloat(double value) =>
        CooperativePump.DepositResultDfloat(value);

    [JSExport]
    public static string DepositResultVoid() =>
        CooperativePump.DepositResultVoid();

    // Unwrap TargetInvocationException / TypeInitializationException so the
    // page sees the real cause instead of "Arg_TargetInvocationException".
    // Resource-key messages are common under WASM trimming — strings like
    // ArgumentNull_Generic land in the report; the chain plus type name
    // usually narrows things down.
    private static string DescribeException(Exception ex)
    {
        var sb = new StringBuilder();
        var current = ex;
        var depth = 0;
        while (current != null && depth < 6)
        {
            if (sb.Length > 0) sb.Append("\n  → ");
            sb.Append(current.GetType().FullName).Append(": ").Append(current.Message);
            if (!string.IsNullOrEmpty(current.StackTrace) && depth == 0)
                sb.Append('\n').Append(current.StackTrace);
            current = current.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    // camelCase JSON to match LSP wire-protocol convention; TS interfaces in
    // Playground use lowercase field names. IncludeFields is critical — Core
    // DTOs use public FIELDS (not properties), which System.Text.Json ignores
    // by default. Without this every diagnostic serializes as {} and the
    // Playground throws "d.range is undefined".
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    // Hand-rolled JSON for the {ok, error?} status shape used by every
    // assembly-loading JSExport. `JsonSerializer.Serialize(new { ok=true })`
    // is anonymous-type-based; in this project's Release/trimmed publish,
    // the trimmer strips parameter names from `<>f__AnonymousType*` even
    // with TrimMode=copy, and System.Text.Json then throws "deserialization
    // constructor ... contains parameters with null names". A literal JSON
    // string sidesteps the metadata reflection entirely.
    private static string StatusOk() => "{\"ok\":true}";
    private static string StatusErr(Exception ex)
    {
        var sb = new StringBuilder("{\"ok\":false,\"error\":");
        AppendJsonString(sb, DescribeException(ex));
        sb.Append('}');
        return sb.ToString();
    }

    // JSON-safe string emit: quotes the value and escapes the characters
    // RFC 8259 requires (`"`, `\`, and U+0000–U+001F). FadeBasic.Json's
    // built-in JsonWriteOp.AppendEscaped only handles `"` and `\`, which
    // is fine for the short identifiers the rest of the project emits but
    // produces invalid JSON when the value contains markdown newlines —
    // exactly what ListCommandDocs hits when it serializes hover markdown.
    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        if (value != null)
        {
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
        }
        sb.Append('"');
    }

    // ─── Run ──────────────────────────────────────────────────────────────
    // NOTE: reached only through the [JSExport] mono-interop surface
    // (runtime.js calls FB.CompileAndRun). It formerly also carried
    // [JSInvokable] (Blazor DotNet.invokeMethod), but nothing calls that path
    // anymore, and the lone [JSInvokable] was the project's ONLY dependency on
    // Microsoft.JSInterop — which broke WASM AOT (the trimmer drops the assembly,
    // then Mono AOT can't resolve the attribute). Removed so AOT links cleanly.
    [JSExport]
    // Returns a JSON envelope so the page can format different kinds of
    // output (compile errors / runtime errors / printed stdout) with their
    // own styling. Shape: { compileError, runtimeError, printed }. Any
    // field may be null/empty. Print output also streams through `onPrint`
    // during execution; we drain anything that wasn't flushed yet.
    public static string CompileAndRun(string source)
    {
        var commands = _workspace.Commands;
        if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
        {
            return JsonSerializer.Serialize(new
            {
                compileError = errors.ToDisplay(),
                runtimeError = (string)null,
                printed = "",
            }, _jsonOpts);
        }

        string runtimeError = null;
        try { ctx.Run(); }
        catch (Exception ex) { runtimeError = ex.GetType().Name + ": " + ex.Message; }

        return JsonSerializer.Serialize(new
        {
            compileError = (string)null,
            runtimeError,
            printed = "",
        }, _jsonOpts);
    }

    // ─── LSP entry points — thin adapters over Core ───────────────────────

    [JSExport]
    public static string LspSetDocument(string uri, string text)
    {
        try
        {
            var doc = _workspace.SetDocument(uri, text);
            return SerializeDiagnostics(DiagnosticsHandler.Compute(doc));
        }
        catch (Exception ex)
        {
            return SerializeDiagnostics(new List<LspDiagnostic>
            {
                new LspDiagnostic
                {
                    Severity = LspDiagnosticSeverity.Error,
                    Range = new LspRange
                    {
                        Start = new LspPosition { Line = 0, Character = 0 },
                        End = new LspPosition { Line = 0, Character = 1 },
                    },
                    Message = $"LSP internal error: {ex.GetType().Name}: {ex.Message}",
                    Code = "INT-001",
                    Source = "fade",
                },
            });
        }
    }

    // Reflection-free array serialization via FadeBasic.Json (IJsonable). This
    // is the per-keystroke diagnostics path — System.Text.Json's reflection
    // serializer cost ~8ms/reparse in WASM here.
    static string SerializeDiagnostics(List<LspDiagnostic> diags)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < diags.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(diags[i].Jsonify());
        }
        sb.Append(']');
        return sb.ToString();
    }

    [JSExport]
    public static string LspGetSemanticTokens(string uri)
    {
        var doc = _workspace.Get(uri);
        return JsonSerializer.Serialize(SemanticTokensHandler.Compute(doc), _jsonOpts);
    }

    // Range-scoped variant: serialize only tokens whose (0-based) line is in
    // [startLine, endLine). On a large multi-file project the joined doc is
    // thousands of lines and serializing the whole token stream per keystroke
    // costs seconds — the caller (Playground) requests just the editor viewport
    // so the payload stays tiny. endLine <= 0 means "to end of document".
    [JSExport]
    public static string LspGetSemanticTokensRange(string uri, int startLine, int endLine)
    {
        var doc = _workspace.Get(uri);
        if (endLine <= 0) endLine = int.MaxValue;
        return JsonSerializer.Serialize(SemanticTokensHandler.Compute(doc, startLine, endLine), _jsonOpts);
    }

    // Tokenize a free-floating snippet of Fade source — no workspace doc,
    // no diagnostics published — and return a flat list of `{line, col,
    // length, type}` entries. The Help tab uses this to syntax-highlight
    // ```fade``` code blocks in command/language docs by piggybacking on
    // the same lexer + ClassifyToken pass the LSP semantic-tokens handler
    // uses for the editor. The type field is the legend index from
    // SemanticTokensHandler.Legend (0=comment, 1=keyword, …).
    [JSExport]
    public static string LspTokenizeSnippet(string source)
    {
        if (string.IsNullOrEmpty(source)) return "[]";
        var commands = _workspace.Commands;
        var lex = new FadeBasic.Lexer().TokenizeWithErrors(source, commands);
        var doc = new FadeBasic.LSP.Core.FadeDocument
        {
            Uri = "fade://help-snippet",
            Text = source,
            LexResults = lex,
            Commands = commands,
        };
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var ct in SemanticTokensHandler.Classify(doc))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"line\":").Append(ct.Token.lineNumber);
            sb.Append(",\"col\":").Append(ct.Token.charNumber);
            sb.Append(",\"length\":").Append(ct.Token.Length);
            sb.Append(",\"type\":").Append(SemanticTokensHandler.LegendIndex(ct.Type));
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    [JSExport]
    public static string LspHover(string uri, int line, int character)
    {
        var doc = _workspace.Get(uri);
        var hover = HoverHandler.Compute(doc, line, character);
        return hover == null ? "null" : JsonSerializer.Serialize(hover, _jsonOpts);
    }

    [JSExport]
    public static string LspCompletion(string uri, int line, int character)
    {
        var doc = _workspace.Get(uri);
        var items = CompletionHandler.Compute(doc, line, character);
        return JsonSerializer.Serialize(items, _jsonOpts);
    }

    [JSExport]
    public static string LspGetAllDiagnostics()
    {
        var all = new Dictionary<string, List<LspDiagnostic>>();
        foreach (var doc in _workspace.AllDocuments)
            all[doc.Uri] = DiagnosticsHandler.Compute(doc);
        return JsonSerializer.Serialize(all, _jsonOpts);
    }

    [JSExport]
    public static string LspSignatureHelp(string uri, int line, int character)
    {
        var doc = _workspace.Get(uri);
        var sig = SignatureHelpHandler.Compute(doc, line, character);
        return sig == null ? "null" : JsonSerializer.Serialize(sig, _jsonOpts);
    }

    [JSExport]
    public static string LspReferences(string uri, int line, int character)
    {
        var doc = _workspace.Get(uri);
        var refs = ReferencesHandler.Compute(doc, line, character);
        return JsonSerializer.Serialize(refs ?? new List<LspLocation>(), _jsonOpts);
    }

    [JSExport]
    public static string LspDefinition(string uri, int line, int character)
    {
        var doc = _workspace.Get(uri);
        var def = DefinitionHandler.Compute(doc, line, character);
        return def == null ? "null" : JsonSerializer.Serialize(def, _jsonOpts);
    }

    [JSExport]
    public static string LspDocumentSymbols(string uri)
    {
        var doc = _workspace.Get(uri);
        var syms = DocumentSymbolHandler.Compute(doc);
        return JsonSerializer.Serialize(syms ?? new List<LspDocumentSymbol>(), _jsonOpts);
    }

    [JSExport]
    public static string LspFoldingRanges(string uri)
    {
        var doc = _workspace.Get(uri);
        var ranges = FoldingRangeHandler.Compute(doc);
        return JsonSerializer.Serialize(ranges ?? new List<LspFoldingRange>(), _jsonOpts);
    }

    // optionsJson is an LspFormattingOptions in camelCase JSON.
    [JSExport]
    public static string LspFormat(string uri, string optionsJson)
    {
        var doc = _workspace.Get(uri);
        var opts = string.IsNullOrEmpty(optionsJson)
            ? new LspFormattingOptions()
            : JsonSerializer.Deserialize<LspFormattingOptions>(optionsJson, _jsonOpts) ?? new LspFormattingOptions();
        var edits = FormattingHandler.Compute(doc, opts);
        return JsonSerializer.Serialize(edits, _jsonOpts);
    }

    [JSExport]
    public static string LspFormatRange(string uri, string optionsJson, int startLine, int startCh, int endLine, int endCh)
    {
        var doc = _workspace.Get(uri);
        var opts = string.IsNullOrEmpty(optionsJson)
            ? new LspFormattingOptions()
            : JsonSerializer.Deserialize<LspFormattingOptions>(optionsJson, _jsonOpts) ?? new LspFormattingOptions();
        var range = new LspRange
        {
            Start = new LspPosition { Line = startLine, Character = startCh },
            End = new LspPosition { Line = endLine, Character = endCh },
        };
        var edits = FormattingHandler.ComputeRange(doc, opts, range);
        return JsonSerializer.Serialize(edits, _jsonOpts);
    }

    [JSExport]
    public static string LspFormatOnType(string uri, string optionsJson, int line, int character)
    {
        var doc = _workspace.Get(uri);
        var opts = string.IsNullOrEmpty(optionsJson)
            ? new LspFormattingOptions()
            : JsonSerializer.Deserialize<LspFormattingOptions>(optionsJson, _jsonOpts) ?? new LspFormattingOptions();
        var edits = FormattingHandler.ComputeOnType(doc, opts, new LspPosition { Line = line, Character = character });
        return JsonSerializer.Serialize(edits, _jsonOpts);
    }

    [JSExport]
    public static string LspRename(string uri, int line, int character, string newName)
    {
        var doc = _workspace.Get(uri);
        var edit = RenameHandler.Compute(doc, line, character, newName);
        return edit == null ? "null" : JsonSerializer.Serialize(edit, _jsonOpts);
    }

    // ─── Help / command docs ──────────────────────────────────────────────
    // Returns a JSON array of every command currently loaded in the
    // workspace's CommandCollection, with the same markdown the hover
    // provider renders. Used by the page's Help tab to build a TOC +
    // per-command reader. One row per UNIQUE command name (overloads
    // collapse — the first signature wins). Sorted alphabetically.
    [JSExport]
    public static string ListCommandDocs()
    {
        try
        {
            var commands = _workspace.Commands?.Commands;
            if (commands == null) return "[]";

            // Map command name → owning class label, derived from each
            // IMethodSource's CommandGroupName (e.g.
            // "Fade.MonoGame.Lib.FadeMonoGameCommands"). FIRST source wins
            // on name collisions, matching the dedupe ordering applied to
            // workspace.Commands below — otherwise a command's body and
            // group label could come from different sources. We shorten
            // FQNs to "<TypeName minus 'Commands' suffix>" so the TOC reads
            // "Standard" / "FadeMonoGame" rather than the full namespace.
            var nameToGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sources = _workspace.Commands?.Sources;
            if (sources != null)
            {
                foreach (var source in sources)
                {
                    var groupLabel = ShortenGroupName(source.CommandGroupName);
                    foreach (var cmd in source.Commands)
                    {
                        if (string.IsNullOrEmpty(cmd.name)) continue;
                        if (!nameToGroup.ContainsKey(cmd.name)) nameToGroup[cmd.name] = groupLabel;
                    }
                }
            }

            // Dedupe by command.name. Overloads (e.g. `rgb` with 3 vs 4
            // args) share a name; we surface one row per name and use the
            // first CommandInfo we find — BuildCommandMarkdown already
            // describes all parameter slots from that signature.
            var seen = new HashSet<string>();
            var rows = new List<(string name, string sig, string group, string markdown)>();
            foreach (var c in commands)
            {
                if (string.IsNullOrEmpty(c.name)) continue;
                if (!seen.Add(c.name)) continue;
                string markdown;
                try
                {
                    markdown = FadeBasic.LSP.Core.Handlers.HoverHandler.BuildCommandMarkdown(
                        c, _workspace.Docs);
                }
                catch (Exception ex)
                {
                    markdown = $"### {c.name}\n\n_Failed to render docs: {ex.Message}_";
                }
                // group: the IMethodSource the command came from, so the
                // TOC reflects actual library origin. GuessGroup is the
                // backstop for commands that somehow have no source map
                // entry (shouldn't happen — every Command was iterated
                // off some Source above — but defensive).
                var group = nameToGroup.TryGetValue(c.name, out var g) ? g : GuessGroup(c.name);
                rows.Add((c.name, c.sig, group, markdown));
            }
            // Stable alphabetical order so the TOC is deterministic.
            rows.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

            // Hand-rolled output instead of `JsonSerializer.Serialize(new { ... })`:
            // anonymous types are unreliable under the project's trimmed
            // Release publish (see the note on StatusOk/StatusErr above).
            var sb = new StringBuilder();
            sb.Append('[');
            for (var i = 0; i < rows.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":");      AppendJsonString(sb, rows[i].name);
                sb.Append(",\"signature\":"); AppendJsonString(sb, rows[i].sig);
                sb.Append(",\"group\":");     AppendJsonString(sb, rows[i].group);
                sb.Append(",\"markdown\":");  AppendJsonString(sb, rows[i].markdown);
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }
        catch (Exception ex)
        {
            var sb = new StringBuilder("{\"error\":");
            AppendJsonString(sb, "Failed to enumerate command docs: " + ex.Message);
            sb.Append('}');
            return sb.ToString();
        }
    }

    // Cheap heuristic: cluster commands by their first word so the TOC
    // gets meaningful section headings (e.g. "print", "string", "wait").
    // For multi-word commands ("wait ms", "wait key") this also yields a
    // shared bucket. Single-word commands get their own bucket named
    // after themselves only when no peers share the prefix — to avoid
    // a 200-bucket TOC, single-words fall back to a generic "Core" group.
    private static string GuessGroup(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Core";
        var idx = name.IndexOf(' ');
        return idx > 0 ? name.Substring(0, idx) : "Core";
    }

    // Turn an IMethodSource.CommandGroupName (a fully-qualified type name like
    // "Fade.MonoGame.Lib.FadeMonoGameCommands") into a human-friendly TOC
    // section label ("FadeMonoGame"). Strips the namespace and the
    // conventional "Commands" suffix on the type name.
    private static string ShortenGroupName(string fqn)
    {
        if (string.IsNullOrEmpty(fqn)) return "Core";
        var dot = fqn.LastIndexOf('.');
        var typeName = dot >= 0 ? fqn.Substring(dot + 1) : fqn;
        const string suffix = "Commands";
        if (typeName.EndsWith(suffix, StringComparison.Ordinal) && typeName.Length > suffix.Length)
            typeName = typeName.Substring(0, typeName.Length - suffix.Length);
        return string.IsNullOrEmpty(typeName) ? "Core" : typeName;
    }

    // ─── Tests ────────────────────────────────────────────────────────────
    // Compile the source and list the test entry points. Returns a JSON
    // array of { name, isAbstract, fromParent, sourceLine }. On compile
    // failure returns an empty array (errors surface via LspSetDocument).
    [JSExport]
    public static string ListTests(string source)
    {
        try
        {
            var commands = _workspace.Commands;
            if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out _))
                return "[]";
            // Hand-rolled JSON: PublishTrimmed strips anonymous-type parameter
            // names, so JsonSerializer.Serialize(new { … }) throws at runtime and
            // the catch below would silently return "[]" (no tests ever surface).
            // Same reasoning as DebugStartOk/DebugStartErr.
            var sb = new StringBuilder("[");
            var first = true;
            foreach (var t in ctx.Compiler.TestManifest)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":");
                AppendJsonString(sb, t.name);
                sb.Append(",\"isAbstract\":").Append(t.isAbstract ? "true" : "false");
                sb.Append(",\"fromParent\":");
                if (t.fromParent == null) sb.Append("null"); else AppendJsonString(sb, t.fromParent);
                sb.Append(",\"sourceLine\":").Append(t.sourceLine);
                sb.Append(",\"sourceChar\":").Append(t.sourceChar);
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }
        catch
        {
            return "[]";
        }
    }

    // Begin a cooperative test run — delegates to CooperativePump.
    // The JS pump drives RunTick repeatedly and CooperativePump.RunTick
    // handles the per-test transitions internally.
    [JSExport]
    public static string RunTestsStart(string source, string testName) =>
        CooperativePump.RunTestsStart(source, testName);

    // ─── Debug session (DAP) ────────────────────────────────────────────
    // One active session at a time. The worker calls DebugStart() to
    // compile + boot a session, then DebugTick() in a loop to make
    // forward progress, draining outbound messages between ticks.

    private static FadeRuntimeContext _debugContext;
    private static WebDebugSession _debugSession;
    private static int _debugMessageIdCounter;
    // Tracks the pause state across ticks so we can emit a synthetic stop
    // event on running→paused transitions (e.g. step landings, which the
    // base DebugSession only signals via a PROTO_ACK on the step request).
    private static bool _debugWasPaused;
    // Set when DebugStartTest boots a session targeting a specific test.
    // GetDebugTestResult uses this to know which test name to report, and
    // (via _debugSession._vm.assertionFailure) whether it passed. Cleared
    // by DebugStart (non-test) and DebugTerminate so subsequent debug
    // queries return null instead of stale data.
    private static FadeBasic.Virtual.TestManifestEntry _debugTestEntry;

    private static int NextDebugId() => ++_debugMessageIdCounter;

    // Compile + boot a debug session that targets a specific test entry
    // point. Mirrors FadeTestExecutor.RunTest's setup — a fresh VM at the
    // test's entry address with isTestExecution=true — but wraps it in a
    // WebDebugSession so we can pause, step, and inspect normally.
    [JSExport]
    public static string DebugStartTest(string source, string testName)
    {
        try
        {
            DebugTerminate();
            var commands = _workspace.Commands;
            if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
            {
                return DebugStartErr("Compile failed:\n" + errors.ToDisplay());
            }
            FadeBasic.Virtual.TestManifestEntry foundEntry = null;
            foreach (var t in ctx.Compiler.TestManifest)
            {
                if (string.Equals(t.name, testName, StringComparison.OrdinalIgnoreCase))
                {
                    foundEntry = t;
                    break;
                }
            }
            if (foundEntry == null || foundEntry.isAbstract)
            {
                return DebugStartErr(foundEntry == null
                    ? $"No test named '{testName}' found"
                    : $"Test '{testName}' is abstract and cannot be debugged");
            }

            // Fresh VM at the test's entry address (matches
            // FadeTestExecutor.RunTest's bootstrap so the test runs the same
            // way it would in normal test execution).
            var vm = new FadeBasic.Virtual.VirtualMachine(ctx.Machine.program, foundEntry.entryPointAddress)
            {
                hostMethods = ctx.Compiler.methodTable,
                isTestExecution = true,
            };
            _debugContext = ctx;
            _debugSession = new WebDebugSession(vm, ctx.Compiler.DebugData, commands);
            _debugTestEntry = foundEntry;
            _debugWasPaused = true;
            EnqueueBasic(DebugMessageType.REQUEST_PAUSE);

            var lines = new SortedSet<int>();
            foreach (var t in ctx.Compiler.DebugData.statementTokens)
                if (t?.token != null) lines.Add(t.token.lineNumber);
            return DebugStartTestOk(lines, foundEntry.name, foundEntry.sourceLine);
        }
        catch (Exception ex)
        {
            return DebugStartErr("Debug-test start failed: " + ex.Message);
        }
    }

    // Hand-rolled JSON for DebugStartTest's success shape. Anonymous types +
    // JsonSerializer break under PublishTrimmed (parameter names are stripped),
    // so — like DebugStartOk/DebugStartErr — build the literal directly.
    private static string DebugStartTestOk(SortedSet<int> statementLines, string testName, int testLine)
    {
        var sb = new StringBuilder("{\"ok\":true,\"statementLines\":[");
        var first = true;
        foreach (var line in statementLines)
        {
            if (!first) sb.Append(',');
            sb.Append(line);
            first = false;
        }
        sb.Append("],\"testName\":");
        AppendJsonString(sb, testName);
        sb.Append(",\"testLine\":").Append(testLine).Append('}');
        return sb.ToString();
    }

    // Compile + boot a debug session. Returns JSON with { ok, error?,
    // statementTokens[] } so the page can render gutter glyphs at valid
    // breakpoint lines.
    [JSExport]
    public static string DebugStart(string source)
    {
        try
        {
            DebugTerminate(); // reset any prior session.
            var commands = _workspace.Commands;
            if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
            {
                return DebugStartErr("Compile failed:\n" + errors.ToDisplay());
            }
            _debugContext = ctx;
            _debugSession = new WebDebugSession(ctx.Machine, ctx.Compiler.DebugData, commands);
            // Hot reload while debugging: arm a session over the SAME VM +
            // compiler that produced the running bytecode (Gotcha #1). DebugTick's
            // StartDebugging hook applies armed edits at a safepoint and rebinds
            // via RestartAfterReload — keeping the debugger attached + breakpoints.
            _debugSession.HotReload = CooperativePump.BuildReloadSession(ctx.Machine, ctx.Compiler, commands);
            _debugTestEntry = null; // non-test debug; clear any prior test marker
            // Pre-mark as paused so the first tick's running→paused detection
            // doesn't fire a synthetic stop event for our internal start-
            // pause. Real pauses (breakpoints, steps) flip from false→true
            // and emit normally.
            _debugWasPaused = true;

            // Start the session in a paused state. The page must set its
            // breakpoints and then call DebugContinue() to begin running.
            // Without this, the tick loop in worker.js would race the page
            // and execute past any breakpoints before they're installed.
            EnqueueBasic(DebugMessageType.REQUEST_PAUSE);

            // Surface valid statement lines so the editor can show breakpoint
            // hints. statementTokens have 1-based lineNumber.
            var lines = new SortedSet<int>();
            foreach (var t in ctx.Compiler.DebugData.statementTokens)
                if (t?.token != null) lines.Add(t.token.lineNumber);
            return DebugStartOk(lines);
        }
        catch (Exception ex)
        {
            return DebugStartErr("Debug start failed: " + ex.Message);
        }
    }

    // Hand-rolled JSON for DebugStart's return shape. Same reasoning as
    // StatusOk above: PublishTrimmed=true strips parameter names from
    // anonymous types, breaking System.Text.Json's deserialization
    // metadata. A literal JSON string is trim-safe.
    private static string DebugStartOk(SortedSet<int> statementLines)
    {
        var sb = new StringBuilder("{\"ok\":true,\"statementLines\":[");
        var first = true;
        foreach (var line in statementLines)
        {
            if (!first) sb.Append(',');
            sb.Append(line);
            first = false;
        }
        sb.Append("]}");
        return sb.ToString();
    }
    private static string DebugStartErr(string error)
    {
        var sb = new StringBuilder("{\"ok\":false,\"error\":");
        AppendJsonString(sb, error);
        sb.Append(",\"statementLines\":[]}");
        return sb.ToString();
    }

    // Run a budget of VM instructions. Returns drained outbound messages
    // (as a JSON array of DebugMessage) + a small status object. The
    // worker loops over this until either a stop message arrives, a
    // terminate request comes in, or the program completes.
    [JSExport]
    public static string DebugTick(int ops)
    {
        if (_debugSession == null)
            return DebugTickComplete();

        // Per-tick reset for the cooperative-wait hint. WaitImpl writes
        // this when `wait ms` fires inside the debug session; pumpDebugTick
        // reads it from the response and uses it as the next setTimeout
        // delay. Without the reset, a stale value from a previous tick
        // would re-trigger the wait. The state lives in CooperativePump
        // so RunTick and DebugTick share the same source of truth.
        CooperativePump.PendingWaitMs = 0;
        try { _debugSession.StartDebugging(ops); }
        catch (Exception ex) { /* never fail the worker — surface as a message */
            _debugSession.Enqueue(new DebugMessage { id = NextDebugId(), type = DebugMessageType.NOOP });
            return DebugTickFailed("Runtime exception: " + ex.Message);
        }
        // If WaitImpl flipped requestedExit to unwind early (kind=3 yield
        // for breakpoint updates etc., or kind=2 terminate before the
        // page's debug-terminate has landed), clear the flag now so the
        // NEXT tick can resume normally. For genuine kind=2 terminate
        // the debug-terminate message will null _debugSession on the
        // next worker tick anyway, so the reset is harmless there.
        _debugSession.ClearYieldRequest();

        var drained = _debugSession.DrainOutbound();

        // No synthetic events. The page acts as its own DAP adapter — it
        // listens for PROTO_ACK with status=1 on its own step requests and
        // treats those as "stopped after step", same way a real DAP adapter
        // translates the ACK into a DAP Stopped event for VSCode.
        return DebugTickRunning(
            running: !_debugSession.IsPaused,
            paused: _debugSession.IsPaused,
            complete: _debugSession.ProgramComplete,
            instructionPointer: _debugSession.InstructionPointer,
            waitMs: CooperativePump.PendingWaitMs,
            messages: drained);
    }

    // Hand-rolled JSON for DebugTick's return shapes. Anonymous types
    // (the old code) trip System.Text.Json's deserialization-metadata
    // reflection under PublishTrimmed=true. See StatusOk above.
    private static string DebugTickComplete() =>
        "{\"running\":false,\"paused\":false,\"complete\":true,\"messages\":[]}";
    private static string DebugTickFailed(string error)
    {
        var sb = new StringBuilder("{\"running\":false,\"paused\":false,\"complete\":true,\"error\":");
        AppendJsonString(sb, error);
        sb.Append(",\"messages\":[]}");
        return sb.ToString();
    }
    private static string DebugTickRunning(
        bool running, bool paused, bool complete, int instructionPointer, int waitMs,
        List<DebugMessage> messages)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"running\":").Append(running ? "true" : "false");
        sb.Append(",\"paused\":").Append(paused ? "true" : "false");
        sb.Append(",\"complete\":").Append(complete ? "true" : "false");
        sb.Append(",\"instructionPointer\":").Append(instructionPointer);
        sb.Append(",\"waitMs\":").Append(waitMs);
        sb.Append(",\"printed\":\"\"");
        sb.Append(",\"messages\":[");
        var first = true;
        foreach (var m in messages)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"id\":").Append(m.id);
            sb.Append(",\"type\":");
            AppendJsonString(sb, m.type.ToString());
            sb.Append(",\"json\":");
            AppendJsonString(sb, m.RawJson ?? m.Jsonify());
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // Replace the active breakpoint set. linesJson is a JSON array of
    // { lineNumber, colNumber? } pairs in the source's coordinate space.
    //
    // Reflection-based JsonSerializer.Deserialize below needs BreakpointRequestDto's
    // parameterless ctor + properties to survive the WASM trimmer. Without this,
    // a trimmed publish (e.g. built without the wasm-tools workload) throws
    // "Deserialization of types without a parameterless constructor ... is not
    // supported" at runtime and breakpoints never register. This keeps the type's
    // members regardless of build/trim settings.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.PublicProperties, typeof(BreakpointRequestDto))]
    [JSExport]
    public static string DebugSetBreakpoints(string linesJson)
    {
        if (_debugSession == null) return "false";
        var input = JsonSerializer.Deserialize<List<BreakpointRequestDto>>(linesJson, _jsonOpts)
                    ?? new List<BreakpointRequestDto>();
        var msg = new RequestBreakpointMessage
        {
            id = NextDebugId(),
            type = DebugMessageType.REQUEST_BREAKPOINTS,
            breakpoints = input.Select(b => new Breakpoint
            {
                lineNumber = b.Line,
                colNumber = b.Column,
            }).ToList(),
        };
        // RawJson is what the session uses when re-parsing typed payloads.
        msg.RawJson = msg.Jsonify();
        _debugSession.Enqueue(msg);
        return "true";
    }

    [JSExport]
    public static string DebugStep(string kind)
    {
        if (_debugSession == null) return "false";
        var type = kind switch
        {
            "over" => DebugMessageType.REQUEST_STEP_OVER,
            "in"   => DebugMessageType.REQUEST_STEP_IN,
            "out"  => DebugMessageType.REQUEST_STEP_OUT,
            _ => DebugMessageType.NOOP,
        };
        if (type == DebugMessageType.NOOP) return "false";
        EnqueueBasic(type);
        return "true";
    }

    [JSExport]
    public static string DebugContinue()
    {
        if (_debugSession == null) return "false";
        EnqueueBasic(DebugMessageType.REQUEST_PLAY);
        return "true";
    }

    [JSExport]
    public static string DebugPause()
    {
        if (_debugSession == null) return "false";
        EnqueueBasic(DebugMessageType.REQUEST_PAUSE);
        return "true";
    }

    [JSExport]
    public static string DebugTerminate()
    {
        // Do NOT enqueue REQUEST_TERMINATE — DebugSession's handler calls
        // Environment.Exit(0) which would kill the entire WASM runtime.
        // Just drop our references; the session is GC'd naturally and the
        // tick loop sees `session == null` on its next call.
        _debugSession = null;
        _debugContext = null;
        _debugTestEntry = null;
        return "true";
    }

    // Extract a FadeTestResult from the currently-debugging test's VM.
    // Returns "null" (JSON) when the session isn't a test debug or when
    // there's no live session to inspect.
    //
    // Callable at any point during a debug-test session — the Playground
    // typically calls it once the session emits 'complete' so it can
    // flip the test row from 'running' to 'pass'/'fail'. Calling
    // mid-execution returns a partial snapshot (assertionFailure may
    // not be set yet); the result is most meaningful when the VM has
    // run past program.Length, which is exactly the 'complete' signal
    // the Playground listens for.
    [JSExport]
    public static string GetDebugTestResult()
    {
        if (_debugSession == null || _debugTestEntry == null) return "null";
        var vm = _debugSession._vm;
        if (vm == null) return "null";
        var elapsed = System.TimeSpan.Zero; // debug sessions don't time tests
        var result = FadeBasic.Sdk.FadeTestExecutor.BuildResultFromVm(
            vm,
            _debugTestEntry,
            elapsed,
            _debugContext?.Compiler.DebugData,
            runtimeException: null);
        return CooperativePump.SerializeTestResult(result);
    }

    [JSExport]
    public static string DebugStackFrames()
    {
        if (_debugSession == null) return "[]";
        var frames = _debugSession.GetFrames2();
        return JsonSerializer.Serialize(frames, _jsonOpts);
    }

    // Resolve a VM instruction index to its originating source location in
    // joined-source coordinates (0-based line + char). Used by the crash
    // overlay on REV_REQUEST_EXPLODE: the runtime-error message embeds the
    // failing `insIndex`, but the line/char only live in the DebugData this
    // session built at compile time. Wraps IndexCollection's binary search;
    // the caller translates joined coords to per-file via ProjectSourceMap.
    // Returns "null" when no session is active or the index is past the
    // last statement token.
    [JSExport]
    public static string DebugResolveInstruction(int insIndex)
    {
        if (_debugSession?.instructionMap == null) return "null";
        if (!_debugSession.instructionMap.TryFindClosestTokenBeforeIndex(insIndex, out var debugToken)) return "null";
        if (debugToken?.token == null) return "null";
        return JsonSerializer.Serialize(new
        {
            insIndex,
            lineNumber = debugToken.token.lineNumber,
            charNumber = debugToken.token.charNumber,
        }, _jsonOpts);
    }

    [JSExport]
    public static string DebugScopes(int frameId)
    {
        if (_debugSession == null) return "{\"scopes\":[]}";
        var resp = _debugSession.GetScopes(new DebugScopeRequest { frameIndex = frameId });
        StripRuntimeRefs(resp);
        return JsonSerializer.Serialize(resp, _jsonOpts);
    }

    [JSExport]
    public static string DebugVariableExpansion(int variableId)
    {
        if (_debugSession == null) return "{\"scopes\":[]}";
        var sub = _debugSession.variableDb.Expand(variableId);
        var msg = new ScopesMessage { scopes = new List<DebugScope> { sub } };
        StripRuntimeRefs(msg);
        return JsonSerializer.Serialize(msg, _jsonOpts);
    }

    // DebugVariable carries a `runtimeVariable` field that holds live VM
    // internals (delegates, byref data) — System.Text.Json can't serialize
    // them. The native LSP/DAP serializer skips this via IJsonable's
    // ProcessJson, but our STJ-based path here doesn't honor that. Null
    // the field before serializing so the response is clean.
    // Null out runtimeVariable for serialization WITHOUT mutating the
    // original Launch.DebugVariable objects — those live in the
    // variableDb's idToVariable map and subsequent setVariable calls
    // need their runtimeVariable to still point at the live VM data.
    // (Mutating in place worked back when variables didn't carry a
    // runtimeVariable, but the array-element fix in DebugUtil.Expand
    // now attaches one so TrySetValue's heap-write branch fires.
    // Stripping in place broke that — the element id was in
    // idToVariable but its runtimeVariable came back null, so
    // TrySetValue's null-check threw "no variable for given id".)
    private static void StripRuntimeRefs(ScopesMessage msg)
    {
        if (msg?.scopes == null) return;
        for (var si = 0; si < msg.scopes.Count; si++)
        {
            var scope = msg.scopes[si];
            if (scope?.variables == null) continue;
            for (var vi = 0; vi < scope.variables.Count; vi++)
            {
                var v = scope.variables[vi];
                if (v?.runtimeVariable == null) continue;
                scope.variables[vi] = new Launch.DebugVariable
                {
                    id = v.id,
                    name = v.name,
                    type = v.type,
                    value = v.value,
                    evalName = v.evalName,
                    fieldCount = v.fieldCount,
                    elementCount = v.elementCount,
                    // runtimeVariable intentionally left null — STJ-safe.
                };
            }
        }
    }

    [JSExport]
    public static string DebugEval(int frameId, string expression)
    {
        if (_debugSession == null) return "null";
        var result = _debugSession.Eval(frameId, expression);
        return JsonSerializer.Serialize(result, _jsonOpts);
    }

    [JSExport]
    public static string DebugRepl(int frameId, string code)
    {
        if (_debugSession == null) return "null";
        var result = _debugSession.ReplExec(frameId, code);
        return JsonSerializer.Serialize(result, _jsonOpts);
    }

    [JSExport]
    public static string DebugSetVariable(int frameId, int variableId, string rhs)
    {
        if (_debugSession == null) return "null";
        var result = _debugSession.Eval(frameId, rhs, variableId);
        // DebugVariableDatabase caches the local/global scope on first read
        // and returns the cached object on subsequent calls. After a
        // successful set the underlying VM memory is updated but the cached
        // DebugVariable.value strings still show the old display value.
        // Bust the cache so the next GetScopes call rebuilds with fresh
        // values. (ClearLifetime resets variable IDs too — the page must
        // re-request scopes; expandedVars by-id state on the client
        // intentionally resets per pause anyway.)
        if (result != null && result.id != -1)
        {
            try { _debugSession.variableDb.ClearLifetime(); } catch { /* best effort */ }
        }
        return JsonSerializer.Serialize(result, _jsonOpts);
    }

    private static void EnqueueBasic(DebugMessageType type)
    {
        if (_debugSession == null) return;
        var msg = new DebugMessage { id = NextDebugId(), type = type };
        msg.RawJson = msg.Jsonify();
        _debugSession.Enqueue(msg);
    }

    private sealed class BreakpointRequestDto
    {
        public int Line { get; set; }
        public int Column { get; set; }
    }

    // Returns a JSON object with FadeBasic + .NET runtime version strings
    // for display in the browser's Diagnostics panel.
    [JSExport]
    public static string GetVersionInfo()
    {
        var asm = typeof(FadeBasic.Virtual.VirtualMachine).Assembly;
        var attrs = (System.Reflection.AssemblyInformationalVersionAttribute[])
            asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
        var fadeVersion = attrs.Length > 0 ? attrs[0].InformationalVersion : asm.GetName().Version?.ToString() ?? "unknown";
        var dotnetVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        return JsonSerializer.Serialize(new { fadeBasic = fadeVersion, dotnet = dotnetVersion });
    }
}

