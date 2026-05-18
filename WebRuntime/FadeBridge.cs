using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using FadeBasic;
using FadeBasic.Json;
using FadeBasic.Launch;
using FadeBasic.Lib.Standard;
using FadeBasic.Sdk;
using FadeBasic.LSP.Core;
using FadeBasic.LSP.Core.Handlers;

namespace WebRuntime;

// FadeBridge is the browser-side adapter between the worker's postMessage
// surface and the cross-platform LSP logic in FadeBasic.LSP.Core. The native
// LSP server in FadeBasic/LSP/ will get the same Core handlers behind its
// OmniSharp transport once it's refactored.
[SupportedOSPlatform("browser")]
public static partial class FadeBridge
{
    private static readonly FadeWorkspace _workspace = CreateWorkspace();

    private static FadeWorkspace CreateWorkspace()
    {
        var ws = new FadeWorkspace(
            new CommandCollection(new WebCommands(), new StandardCommands()));
        // Surface rich command markdown on hover (parsed from the XML doc
        // comments baked into StandardCommandsMetaData.COMMANDS_JSON).
        ws.Docs = StandardCommandDocs.Build();
        return ws;
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

    // ─── Run ──────────────────────────────────────────────────────────────
    [JSInvokable]
    [JSExport]
    public static string CompileAndRun(string source)
    {
        var sb = new StringBuilder();
        var commands = _workspace.Commands;
        if (!Fade.TryCreateFromString(source, commands, out var ctx, out var errors))
        {
            sb.AppendLine("Compile failed:");
            sb.Append(errors.ToDisplay());
            return sb.ToString();
        }

        try { ctx.Run(); }
        catch (Exception ex) { sb.AppendLine($"Runtime error: {ex.GetType().Name}: {ex.Message}"); }

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

    // ─── LSP entry points — thin adapters over Core ───────────────────────

    [JSExport]
    public static string LspSetDocument(string uri, string text)
    {
        try
        {
            var doc = _workspace.SetDocument(uri, text);
            return JsonSerializer.Serialize(DiagnosticsHandler.Compute(doc), _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new LspDiagnostic[]
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
            }, _jsonOpts);
        }
    }

    [JSExport]
    public static string LspGetSemanticTokens(string uri)
    {
        var doc = _workspace.Get(uri);
        return JsonSerializer.Serialize(SemanticTokensHandler.Compute(doc), _jsonOpts);
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
            if (!Fade.TryCreateFromString(source, commands, out var ctx, out _))
                return "[]";
            var tests = new List<object>();
            foreach (var t in ctx.Compiler.TestManifest)
            {
                tests.Add(new
                {
                    name = t.name,
                    isAbstract = t.isAbstract,
                    fromParent = t.fromParent,
                    sourceLine = t.sourceLine,
                    sourceChar = t.sourceChar,
                });
            }
            return JsonSerializer.Serialize(tests, _jsonOpts);
        }
        catch
        {
            return "[]";
        }
    }

    // Compile + run either all tests (testName empty / null) or a single
    // named test. Returns JSON with { passed, failed, duration, results[],
    // printed, error? }. `printed` is the captured stdout from any
    // `print` statements run during testing.
    [JSExport]
    public static string RunTests(string source, string testName)
    {
        var sb = new StringBuilder();
        var commands = _workspace.Commands;
        if (!Fade.TryCreateFromString(source, commands, out var ctx, out var errors))
        {
            return JsonSerializer.Serialize(new
            {
                passed = 0,
                failed = 0,
                error = "Compile failed:\n" + errors.ToDisplay(),
                results = Array.Empty<object>(),
                printed = "",
            }, _jsonOpts);
        }

        // Drain any output from previous runs so the captured output is
        // attributable to this invocation only.
        WebCommands.DrainPrintBuffer();

        try
        {
            object payload;
            if (string.IsNullOrWhiteSpace(testName))
            {
                var run = ctx.RunAllTests();
                payload = new
                {
                    passed = run.passedCount,
                    failed = run.failedCount,
                    duration = run.duration.TotalMilliseconds,
                    results = ResultsToObjects(run.tests),
                };
            }
            else
            {
                var r = ctx.RunTest(testName);
                payload = new
                {
                    passed = r.passed ? 1 : 0,
                    failed = r.passed ? 0 : 1,
                    duration = r.duration.TotalMilliseconds,
                    results = ResultsToObjects(new List<FadeBasic.Sdk.FadeTestResult> { r }),
                };
            }
            var printed = WebCommands.DrainPrintBuffer();
            return JsonSerializer.Serialize(new
            {
                passed = ((dynamic)payload).passed,
                failed = ((dynamic)payload).failed,
                duration = ((dynamic)payload).duration,
                results = ((dynamic)payload).results,
                printed,
            }, _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                passed = 0,
                failed = 0,
                error = "Runtime error: " + ex.GetType().Name + ": " + ex.Message,
                results = Array.Empty<object>(),
                printed = WebCommands.DrainPrintBuffer(),
            }, _jsonOpts);
        }
    }

    private static List<object> ResultsToObjects(List<FadeBasic.Sdk.FadeTestResult> results)
    {
        var list = new List<object>(results.Count);
        foreach (var r in results)
        {
            var frames = new List<object>();
            if (r.failureFrames != null)
            {
                foreach (var f in r.failureFrames)
                {
                    frames.Add(new
                    {
                        functionName = f.functionName,
                        lineNumber = f.lineNumber,
                        charNumber = f.charNumber,
                        instructionIndex = f.instructionIndex,
                    });
                }
            }
            list.Add(new
            {
                name = r.testName,
                passed = r.passed,
                duration = r.duration.TotalMilliseconds,
                failureMessage = r.failureMessage,
                failureReason = r.failureReason,
                failureSourceText = r.failureSourceText,
                failureInstructionIndex = r.failureInstructionIndex,
                failureFrames = frames,
            });
        }
        return list;
    }

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
            if (!Fade.TryCreateFromString(source, commands, out var ctx, out var errors))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "Compile failed:\n" + errors.ToDisplay(),
                    statementLines = Array.Empty<int>(),
                }, _jsonOpts);
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
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = foundEntry == null
                        ? $"No test named '{testName}' found"
                        : $"Test '{testName}' is abstract and cannot be debugged",
                }, _jsonOpts);
            }

            WebCommands.DrainPrintBuffer();
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
            _debugWasPaused = true;
            EnqueueBasic(DebugMessageType.REQUEST_PAUSE);

            var lines = new SortedSet<int>();
            foreach (var t in ctx.Compiler.DebugData.statementTokens)
                if (t?.token != null) lines.Add(t.token.lineNumber);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                statementLines = lines,
                testName = foundEntry.name,
                testLine = foundEntry.sourceLine,
            }, _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Debug-test start failed: " + ex.Message,
            }, _jsonOpts);
        }
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
            if (!Fade.TryCreateFromString(source, commands, out var ctx, out var errors))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "Compile failed:\n" + errors.ToDisplay(),
                    statementLines = Array.Empty<int>(),
                }, _jsonOpts);
            }
            WebCommands.DrainPrintBuffer();
            _debugContext = ctx;
            _debugSession = new WebDebugSession(ctx.Machine, ctx.Compiler.DebugData, commands);
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
            return JsonSerializer.Serialize(new
            {
                ok = true,
                statementLines = lines,
            }, _jsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "Debug start failed: " + ex.Message,
            }, _jsonOpts);
        }
    }

    // Run a budget of VM instructions. Returns drained outbound messages
    // (as a JSON array of DebugMessage) + a small status object. The
    // worker loops over this until either a stop message arrives, a
    // terminate request comes in, or the program completes.
    [JSExport]
    public static string DebugTick(int ops)
    {
        if (_debugSession == null)
            return JsonSerializer.Serialize(new { running = false, complete = true, messages = Array.Empty<object>() }, _jsonOpts);

        try { _debugSession.StartDebugging(ops); }
        catch (Exception ex) { /* never fail the worker — surface as a message */
            _debugSession.Enqueue(new DebugMessage { id = NextDebugId(), type = DebugMessageType.NOOP });
            return JsonSerializer.Serialize(new
            {
                running = false,
                complete = true,
                error = "Runtime exception: " + ex.Message,
                messages = Array.Empty<object>(),
            }, _jsonOpts);
        }

        var drained = _debugSession.DrainOutbound();
        var msgs = new List<object>(drained.Count);
        foreach (var m in drained)
        {
            msgs.Add(new
            {
                id = m.id,
                type = m.type.ToString(),
                json = m.RawJson ?? m.Jsonify(),
            });
        }

        // No synthetic events. The page acts as its own DAP adapter — it
        // listens for PROTO_ACK with status=1 on its own step requests and
        // treats those as "stopped after step", same way a real DAP adapter
        // translates the ACK into a DAP Stopped event for VSCode.

        var printed = WebCommands.DrainPrintBuffer();
        return JsonSerializer.Serialize(new
        {
            running = !_debugSession.IsPaused,
            paused = _debugSession.IsPaused,
            complete = _debugSession.ProgramComplete,
            instructionPointer = _debugSession.InstructionPointer,
            messages = msgs,
            printed,
        }, _jsonOpts);
    }

    // Replace the active breakpoint set. linesJson is a JSON array of
    // { lineNumber, colNumber? } pairs in the source's coordinate space.
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
        return "true";
    }

    [JSExport]
    public static string DebugStackFrames()
    {
        if (_debugSession == null) return "[]";
        var frames = _debugSession.GetFrames2();
        return JsonSerializer.Serialize(frames, _jsonOpts);
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
    private static void StripRuntimeRefs(ScopesMessage msg)
    {
        if (msg?.scopes == null) return;
        foreach (var scope in msg.scopes)
        {
            if (scope?.variables == null) continue;
            foreach (var v in scope.variables) v.runtimeVariable = null;
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
}
