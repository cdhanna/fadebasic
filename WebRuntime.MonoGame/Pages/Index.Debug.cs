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
using FadeBasic;
using FadeBasic.Json;        // for Jsonify() extension on DebugMessage etc.
using FadeBasic.Launch;
using FadeBasic.Virtual;
using Microsoft.JSInterop;

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
