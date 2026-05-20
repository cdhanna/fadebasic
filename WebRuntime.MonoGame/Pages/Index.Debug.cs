// Debug bridge for monogame projects. Mirrors WebRuntime/FadeBridge.cs's
// DAP-style surface, but the session lives inside Game1 (driven by
// Game1.Update calling _debugSession.StartDebugging in a tight loop per
// tick) rather than being driven directly from a worker. The JSInvokable
// methods here are control-plane only — set breakpoints, request step or
// pause, drain queued events. The actual VM ticking happens via the
// rAF → TickDotNet → Game1.Update → DebugSession.StartDebugging path.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fade.MonoGame.Core;
using FadeBasic;
using FadeBasic.Json;        // for Jsonify() extension on DebugMessage etc.
using FadeBasic.Launch;
using FadeBasic.Lib.Standard;
using FadeBasic.Sdk;
using FadeBasic.Testing;     // FadeTestRunContext, FadeTestSessionContext, FadeTestResult
using FadeBasic.Virtual;
using Microsoft.JSInterop;
// `using` aliases are file-scoped — Index.razor.cs aliases Fade as FadeSdk
// for unambiguous resolution against Fade.MonoGame.* namespaces; mirror
// that here so DebugStartTest can call FadeSdk.TryCreateFromString.
using FadeSdk = FadeBasic.Sdk.Fade;

namespace WebRuntime.MonoGame.Pages
{
    public partial class Index
    {
        // Tracks message ids we issue for synthesized DebugMessages. The
        // base DebugSession also issues ids; the union keeps them unique
        // enough for the page-side adapter to correlate ACKs.
        private int _debugMessageIdCounter;

        private static readonly JsonSerializerOptions _debugJsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            IncludeFields = true,
        };

        private int NextDebugId() => ++_debugMessageIdCounter;

        // Drains DebugSession.outboundMessages and serializes them in the
        // shape WebRuntime/worker.js posts for `debug-event` messages —
        // `{ id, type, json }`. The JS rAF tick calls this every frame
        // and forwards each event to the editor's debug control bar.
        private string DrainDebugEvents()
        {
            if (_game?.BrowserDebugSession == null) return "[]";
            var drained = _game.BrowserDebugSession.DrainOutbound();
            if (drained.Count == 0) return "[]";
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
            return JsonSerializer.Serialize(msgs, _debugJsonOpts);
        }

        // Send a basic (no-payload) message to the session's inbox. Wrapped
        // so each call assigns a unique id and pre-jsons the payload (the
        // base DebugSession re-parses RawJson when consuming typed messages).
        private void EnqueueBasic(DebugMessageType type)
        {
            if (_game?.BrowserDebugSession == null) return;
            var msg = new DebugMessage { id = NextDebugId(), type = type };
            msg.RawJson = msg.Jsonify();
            _game.BrowserDebugSession.Enqueue(msg);
        }

        // ─── Lifecycle ─────────────────────────────────────────────

        // Browser-side "start debugging" — equivalent to FadeBridge.DebugStart
        // but for an already-running game. The Game1 VM is always running
        // through DebugSession.StartDebugging (debug mode is enabled in
        // ResetFade); this method just enqueues a pause so the page can
        // set its breakpoints before any user code runs further. Returns
        // a JSON envelope with statementLines so the editor can paint
        // breakpoint hint glyphs in the gutter.
        [JSInvokable]
        public string DebugStart()
        {
            if (_game?.DebugSession == null)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "No game or debug session — Run a monogame program first.",
                    statementLines = Array.Empty<int>(),
                }, _debugJsonOpts);
            }

            EnqueueBasic(DebugMessageType.REQUEST_PAUSE);

            var lines = new SortedSet<int>();
            var dbgData = _game.BrowserDebugSession.DebugDataAccess;
            if (dbgData != null)
            {
                foreach (var t in dbgData.statementTokens)
                {
                    if (t?.token != null) lines.Add(t.token.lineNumber);
                }
            }
            return JsonSerializer.Serialize(new
            {
                ok = true,
                statementLines = lines,
            }, _debugJsonOpts);
        }

        // Browser-side "debug a single test" — enters test mode WITH
        // debug enabled, then enqueues the named test through the same
        // MonoGameTestHost the run-test path uses. Returns immediately
        // with a `{ok, statementLines}` envelope so the editor can paint
        // breakpoint hint glyphs; the test runs concurrently via Game1's
        // tick loop, driving _debugSession just like the regular Debug
        // button does. When the test VM completes, _debugSession is kept
        // alive (suppressExitOnProgramEnd) so the user can subsequently
        // debug another test or resume Run mode.
        //
        // Why this isn't async like RunTests: the page wants the
        // `statementLines` payload synchronously so breakpoints can land
        // before the test body executes. The test itself is fire-and-
        // forget from this method's perspective — its outcome surfaces
        // through the debug event drain (REV_REQUEST_EXPLODE for an
        // assertion failure, REV_REQUEST_EXIT for clean completion).
        [JSInvokable]
        public string DebugStartTest(string source, string testName)
        {
            try
            {
                if (_game == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = "Game not booted — Run a monogame program first to warm up the runtime.",
                        statementLines = Array.Empty<int>(),
                    }, _debugJsonOpts);
                }

                var ctx = CompileForTests(source, out var compileError);
                if (ctx == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = "Compile failed:\n" + compileError,
                        statementLines = Array.Empty<int>(),
                    }, _debugJsonOpts);
                }

                TestManifestEntry foundEntry = null;
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
                            ? $"No test named '{testName}' found in the source."
                            : $"Test '{testName}' is abstract and cannot be debugged.",
                        statementLines = Array.Empty<int>(),
                    }, _debugJsonOpts);
                }

                // Enter test mode with debug armed. _debugSession's
                // suppressExitOnProgramEnd is flipped inside SetTestMode
                // so the session survives the test VM hitting end-of-
                // program — letting the user pause/step into a second
                // test without re-booting Game1.
                _game.SetTestMode(true, withDebug: true);
                _paused = false;

                // Sync GameReloader.LatestRuntime to the test ctx so
                // Game1.Update's assertion-failure source-map lookup
                // reads the right source, not a stale boot stub.
                GameReloader.SetBuild(ctx);

                // The host lifecycle (Initialize / BeforeAll) is normally
                // awaited inside RunTests's async flow. For debug we
                // start the test "fire and forget" so we can return
                // statementLines synchronously — kick the lifecycle on
                // a background continuation that the rAF tick can drive
                // forward while the JS side sets breakpoints + resumes.
                EnqueueBasic(DebugMessageType.REQUEST_PAUSE);
                _ = QueueTestForDebugAsync(ctx, foundEntry);

                var lines = new SortedSet<int>();
                var dbgData = _game.BrowserDebugSession?.DebugDataAccess;
                if (dbgData != null)
                {
                    foreach (var t in dbgData.statementTokens)
                    {
                        if (t?.token != null) lines.Add(t.token.lineNumber);
                    }
                }
                _status = $"debug test: {testName}";
                StateHasChanged();
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    statementLines = lines,
                }, _debugJsonOpts);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("DebugStartTest threw: " + ex);
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "DebugStartTest exception: " + ex.Message,
                    statementLines = Array.Empty<int>(),
                }, _debugJsonOpts);
            }
        }

        // Fire-and-forget continuation for DebugStartTest. Awaits the
        // host lifecycle + the test's QueueTest TCS in the background;
        // any exception is logged but not surfaced (the test outcome
        // flows through the debug-event drain). Cancellation is not
        // currently wired through — clicking Stop in the debug toolbar
        // sends REQUEST_TERMINATE through Index.Debug.cs DebugTerminate
        // (which un-pauses the session); the test continues until end of
        // program or hits another breakpoint.
        private async Task QueueTestForDebugAsync(FadeRuntimeContext launchable, TestManifestEntry entry)
        {
            try
            {
                await EnsureHostInitializedAsync(launchable).ConfigureAwait(false);
                var hostMethods = HostMethodTable.FromCommandCollection(launchable.CommandCollection);
                var runCtx = new FadeTestRunContext(launchable, entry, hostMethods);
                _testCts?.Dispose();
                _testCts = new CancellationTokenSource();
                var result = await _testHost.RunTestAsync(runCtx, _testCts.Token).ConfigureAwait(false);
                if (result.passed)
                    Console.WriteLine($"[fade test] {entry.name} passed in {result.duration.TotalMilliseconds:F0}ms");
                else
                    Console.Error.WriteLine($"[fade test] {entry.name} FAILED: {result.failureMessage ?? result.failureReason ?? "test failed"}");

                // Test VM has finished. Send REV_REQUEST_EXITED so the
                // editor clears its debug UI. The auto-emit is suppressed
                // by suppressExitOnProgramEnd=true, so we fire it manually.
                // We do NOT pause here — TickDotNet skips the drain when
                // _paused=true, so the EXITED message must be left to drain
                // on the next tick. The game idles safely (Quit() is a no-op
                // in browser) until the user clicks Run to reload.
                try { _game?.BrowserDebugSession?.SendExitedMessage(); }
                catch (Exception e) { Console.Error.WriteLine("QueueTestForDebugAsync SendExited: " + e); }

                // Stop audio and exit test mode so the next LoadProgram
                // (Run button) reloads normally.
                try { AudioInstanceSystem.StopAll(); }
                catch (Exception e) { Console.Error.WriteLine("QueueTestForDebugAsync StopAll: " + e); }
                _game?.SetTestMode(false);
                _status = "debug test done";
                StateHasChanged();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"DebugStartTest background: {ex}");
                _game?.SetTestMode(false);
            }
        }

        [JSInvokable]
        public string DebugTerminate()
        {
            // Don't dispatch REQUEST_TERMINATE — its handler in DebugSession
            // calls Environment.Exit which would brick the runtime. Browser
            // terminate just means "stop debugging" — let the program keep
            // running (or have the user hit Stop separately). Re-mark as
            // un-paused so the tick loop drains naturally.
            EnqueueBasic(DebugMessageType.REQUEST_PLAY);
            return "true";
        }

        // ─── Control plane ─────────────────────────────────────────

        [JSInvokable]
        public string DebugSetBreakpoints(string linesJson)
        {
            if (_game?.BrowserDebugSession == null) return "false";
            var input = JsonSerializer.Deserialize<List<BreakpointRequestDto>>(linesJson, _debugJsonOpts)
                        ?? new List<BreakpointRequestDto>();
            var msg = new RequestBreakpointMessage
            {
                id = NextDebugId(),
                type = DebugMessageType.REQUEST_BREAKPOINTS,
                breakpoints = input.ConvertAll(b => new Breakpoint
                {
                    lineNumber = b.Line,
                    colNumber = b.Column,
                }),
            };
            msg.RawJson = msg.Jsonify();
            _game.BrowserDebugSession.Enqueue(msg);
            return "true";
        }

        [JSInvokable]
        public string DebugStep(string kind)
        {
            if (_game?.DebugSession == null) return "false";
            var type = kind switch
            {
                "over" => DebugMessageType.REQUEST_STEP_OVER,
                "in"   => DebugMessageType.REQUEST_STEP_IN,
                "out"  => DebugMessageType.REQUEST_STEP_OUT,
                _      => DebugMessageType.NOOP,
            };
            if (type == DebugMessageType.NOOP) return "false";
            EnqueueBasic(type);
            return "true";
        }

        [JSInvokable]
        public string DebugContinue()
        {
            if (_game?.DebugSession == null) return "false";
            EnqueueBasic(DebugMessageType.REQUEST_PLAY);
            return "true";
        }

        [JSInvokable]
        public string DebugPause()
        {
            if (_game?.DebugSession == null) return "false";
            EnqueueBasic(DebugMessageType.REQUEST_PAUSE);
            return "true";
        }

        // ─── Introspection ─────────────────────────────────────────

        [JSInvokable]
        public string DebugStackFrames()
        {
            if (_game?.DebugSession == null) return "[]";
            var frames = _game.DebugSession.GetFrames2();
            return JsonSerializer.Serialize(frames, _debugJsonOpts);
        }

        [JSInvokable]
        public string DebugScopes(int frameId)
        {
            if (_game?.DebugSession == null) return "{\"scopes\":[]}";
            // [DEBUG-LOGGING — remove once scope-after-step issue is resolved]
            try
            {
                var vm = _game.DebugSession._vm;
                var dbg = _game.BrowserDebugSession.DebugDataAccess;
                var insCount = dbg?.insToVariable?.Count ?? 0;
                Console.WriteLine($"[WASM-DBG] DebugScopes(frame={frameId}) ins={vm.instructionIndex} scopeStack={vm.scopeStack.Count} dbg.insToVariable.count={insCount}");
                if (vm.scopeStack.Count > 0 && dbg != null)
                {
                    var s = vm.scopeStack.buffer[0];
                    var registered = new List<string>();
                    for (ulong k = 0; k < (ulong)s.insIndexes.LongLength; k++)
                    {
                        var insIdx = s.insIndexes[k];
                        if (insIdx <= 0) continue;
                        var hit = dbg.insToVariable.TryGetValue(insIdx, out var dv);
                        registered.Add($"reg{k}@ins{insIdx}({(hit ? dv.name : "<no-map>")}={s.dataRegisters[k]} flag={s.flags[k]})");
                    }
                    Console.WriteLine($"[WASM-DBG] scope0 registers: {string.Join(", ", registered)}");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("[WASM-DBG] DebugScopes log threw: " + ex.Message); }
            var resp = _game.DebugSession.GetScopes(new DebugScopeRequest { frameIndex = frameId });
            StripRuntimeRefs(resp);
            return JsonSerializer.Serialize(resp, _debugJsonOpts);
        }

        [JSInvokable]
        public string DebugVariableExpansion(int variableId)
        {
            if (_game?.DebugSession == null) return "{\"scopes\":[]}";
            var sub = _game.DebugSession.variableDb.Expand(variableId);
            var msg = new ScopesMessage { scopes = new List<DebugScope> { sub } };
            StripRuntimeRefs(msg);
            return JsonSerializer.Serialize(msg, _debugJsonOpts);
        }

        [JSInvokable]
        public string DebugEval(int frameId, string expression)
        {
            if (_game?.DebugSession == null) return "null";
            var result = _game.DebugSession.Eval(frameId, expression);
            return JsonSerializer.Serialize(result, _debugJsonOpts);
        }

        [JSInvokable]
        public string DebugRepl(int frameId, string code)
        {
            if (_game?.DebugSession == null) return "null";
            var result = _game.DebugSession.ReplExec(frameId, code);
            return JsonSerializer.Serialize(result, _debugJsonOpts);
        }

        [JSInvokable]
        public string DebugSetVariable(int frameId, int variableId, string rhs)
        {
            if (_game?.DebugSession == null) return "null";
            var result = _game.DebugSession.Eval(frameId, rhs, variableId);
            // The variable cache becomes stale after a successful set; clear
            // so the next GetScopes rebuilds with fresh values. Mirrors what
            // WebRuntime/FadeBridge.cs does.
            if (result != null && result.id != -1)
            {
                try { _game.DebugSession.variableDb.ClearLifetime(); }
                catch { /* best effort */ }
            }
            return JsonSerializer.Serialize(result, _debugJsonOpts);
        }

        // DebugVariable carries a `runtimeVariable` field that holds live
        // VM internals (delegates, byref data) — System.Text.Json can't
        // serialize them. Null the field before serializing so the response
        // is clean. Matches the helper in WebRuntime/FadeBridge.cs.
        private static void StripRuntimeRefs(ScopesMessage msg)
        {
            if (msg?.scopes == null) return;
            foreach (var scope in msg.scopes)
            {
                if (scope?.variables == null) continue;
                foreach (var v in scope.variables) v.runtimeVariable = null;
            }
        }

        private sealed class BreakpointRequestDto
        {
            public int Line { get; set; }
            public int Column { get; set; }
        }
    }
}
