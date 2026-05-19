using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Fade.MonoGame.Core;
using Fade.MonoGame.Lib;
using FadeBasic;
using FadeBasic.Lib.Standard;
using FadeBasic.Sdk;
// FadeBasic.Sdk.Fade collides with the Fade.* MonoGame namespaces in name
// resolution. Alias it so `FadeSdk.TryCreateFromString(...)` is unambiguous.
using FadeSdk = FadeBasic.Sdk.Fade;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Xna.Framework;

namespace WebRuntime.MonoGame.Pages
{
    // Bridge between the JS-driven rAF tick loop and a KNI-backed Fade Game1.
    //
    // Lifecycle:
    //   1. OnAfterRender(firstRender=true) compiles a tiny idle fbasic stub
    //      via FadeMonoGameCommands+StandardCommands, constructs Game1 with
    //      it, and calls game.Run() (non-blocking on KNI BlazorGL). Then it
    //      hands a DotNetObjectReference to JS so the rAF loop can call back.
    //   2. JS calls TickDotNet() once per requestAnimationFrame; that calls
    //      _game.Tick() which drives Game1.Update + Draw once.
    //   3. JS calls OnCanvasResized() on element resize → updates the back
    //      buffer + viewport via GraphicsDeviceManager.
    //   4. JS calls OnGameTimedOut() if a single tick blocked > the watchdog
    //      threshold (runaway fbasic loop) — we null the reference; future
    //      ticks become no-ops.
    //   5. Editor (or page boot) calls LoadProgram(source) to compile + swap
    //      a new fbasic source into the running game via Game1.LoadProgram.
    //      This covers both first-load and hot-reload — Game1's existing
    //      reload-on-flag path handles the swap on the next Update tick.
    public partial class Index : IDisposable
    {
        // Stub source the boot path compiles so we have a valid Game1 to
        // construct before the editor sends any user source. The do/loop
        // keeps the VM alive (without it, instructionIndex >= program.Length
        // immediately, which on desktop calls Quit()).
        private const string BootStubSource = @"do
  sync
loop
";

        private Game1 _game;
        private DotNetObjectReference<Index> _pageDotNetRef;
        private string _status = "booting…";
        // Pause flag toggled by Stop / LoadProgram. The JS rAF still fires
        // TickDotNet every frame; this just makes the call a no-op so the
        // game freezes in place (canvas keeps whatever the last frame
        // rendered). Keeping the runtime warm means the next LoadProgram
        // is an instant reload, not a full KNI re-boot.
        private bool _paused;

        protected override async void OnAfterRender(bool firstRender)
        {
            base.OnAfterRender(firstRender);
            if (!firstRender) return;

            try
            {
                LoadProgramInternal(BootStubSource, initialBoot: true);
                _pageDotNetRef = DotNetObjectReference.Create(this);
                StateHasChanged();
                await JsRuntime.InvokeVoidAsync("initRenderJS", _pageDotNetRef);
            }
            catch (Exception ex)
            {
                _status = "boot error: " + ex.Message;
                StateHasChanged();
                Console.Error.WriteLine("Index.OnAfterRender boot error: " + ex);
            }
        }

        [JSInvokable]
        public string TickDotNet()
        {
            if (_paused) return "[]";
            if (_game == null) return "[]";
            try
            {
                _game.Tick();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Game tick error: " + e);
                _game = null;
                _status = "runtime error: " + e.Message;
                StateHasChanged();
                return "[]";
            }
            // Drain any debug-session events the tick produced (stopped at
            // breakpoint, step-landed-here, scope changed, etc.). Returned
            // as JSON; the JS rAF loop dispatches each event to the
            // editor's debug control bar via monoGameHost.onDebugEvent.
            return DrainDebugEvents();
        }

        [JSInvokable]
        public void OnCanvasResized(int width, int height)
        {
            if (_game == null) return;
            var service = _game.Services.GetService(typeof(IGraphicsDeviceManager));
            if (service is GraphicsDeviceManager gdm)
            {
                gdm.PreferredBackBufferWidth = width;
                gdm.PreferredBackBufferHeight = height;
                gdm.ApplyChanges();
            }
        }

        [JSInvokable]
        public void OnGameTimedOut(double frameMs)
        {
            _game = null;
            _status = $"stopped: frame blocked for {frameMs:F0}ms (watchdog)";
            StateHasChanged();
        }

        // Editor-driven Stop button. Pauses the VM (no further ticks) but
        // keeps Game1 + GraphicsDevice alive so the next LoadProgram reloads
        // instantly. The canvas keeps showing the last frame; we don't
        // black it out so the user can see what they last saw.
        [JSInvokable]
        public void StopGame()
        {
            _paused = true;
            _status = "stopped";
            StateHasChanged();
        }

        // The main editor entry point — compile a new fbasic source against
        // the Fade.MonoGame command surface and either construct or
        // hot-reload the game. Returns true on success; surface compile
        // errors via the page status and console.
        [JSInvokable]
        public bool LoadProgram(string source)
        {
            return LoadProgramInternal(source, initialBoot: false);
        }

        // Register a single XNB asset's bytes with the running Game1's
        // BrowserContentManager. The page calls this once per .xnb in the
        // project before invoking LoadProgram, so any `texture`/`load sfx
        // clip`/`font` commands fbasic runs can resolve via stock
        // Content.Load<T>(name).
        //
        // `name` should be the bare asset name (no extension), matching the
        // string fbasic passes to `texture`/`load sfx clip`. The page
        // strips `.xnb` before calling, but BrowserContentManager also
        // tolerates a trailing `.xnb` defensively.
        [JSInvokable]
        public void RegisterAsset(string name, byte[] bytes)
        {
            if (_game == null) return;
            _game.RegisterAsset(name, bytes);
        }

        // Wipe the registered asset dict — used when the editor switches
        // projects so stale assets from the previous project don't bleed
        // into the new run.
        [JSInvokable]
        public void ClearAssets()
        {
            _game?.BrowserContent?.ClearAssets();
        }

        private bool LoadProgramInternal(string source, bool initialBoot)
        {
            try
            {
                // CommandCollection: Fade.MonoGame.Lib first so its `print`
                // (which writes to MonoGame Console) wins over Standard's,
                // matching desktop precedence.
                var commands = new CommandCollection(
                    new FadeMonoGameCommands(),
                    new StandardCommands());

                if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
                {
                    var msg = "compile error:\n" + errors.ToDisplay();
                    _status = "compile error";
                    Console.Error.WriteLine(msg);
                    if (!initialBoot) StateHasChanged();
                    return false;
                }

                if (_game == null)
                {
                    _game = new Game1(ctx);
                    _game.Run();
                    _status = initialBoot ? "running (boot stub)" : "running";
                }
                else
                {
                    _game.LoadProgram(ctx);
                    _status = "reloaded";
                }
                // Un-pause so subsequent ticks resume rendering. A user
                // can Stop → edit → Run flow and we pick up smoothly.
                _paused = false;

                if (!initialBoot) StateHasChanged();
                return true;
            }
            catch (Exception ex)
            {
                _status = "load error: " + ex.Message;
                Console.Error.WriteLine("LoadProgram error: " + ex);
                if (!initialBoot) StateHasChanged();
                return false;
            }
        }

        // ─── Testing bridge ────────────────────────────────────────────
        // Mirrors WebRuntime/FadeBridge.cs ListTests/RunTests but compiles
        // against FadeMonoGameCommands so graphics-touching tests can call
        // sprite/texture/etc. Tests run via FadeRuntimeContext.RunAllTests
        // (a fresh VM per test) — they don't disturb the main game's VM.
        // GameSystem.game stays set, so commands that read graphics state
        // (e.g., `texture` which loads via ContentWatcher) will *attempt*
        // their work; commands that need Game1 lifecycle (e.g., `sync`)
        // run unbatched against the main GraphicsDevice. Good enough for
        // logic tests; graphics tests will land properly once Game1's
        // testMode + QueueTest flow is wired into the JS rAF loop.

        private static readonly JsonSerializerOptions _testJsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            IncludeFields = true,
        };

        [JSInvokable]
        public string ListTests(string source)
        {
            try
            {
                var commands = new CommandCollection(
                    new FadeMonoGameCommands(),
                    new StandardCommands());
                if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out _))
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
                return JsonSerializer.Serialize(tests, _testJsonOpts);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ListTests failed: " + ex);
                return "[]";
            }
        }

        [JSInvokable]
        public string RunTests(string source, string testName)
        {
            try
            {
                var commands = new CommandCollection(
                    new FadeMonoGameCommands(),
                    new StandardCommands());

                if (!FadeSdk.TryCreateFromString(source, commands, out var ctx, out var errors))
                {
                    return JsonSerializer.Serialize(new
                    {
                        passed = 0,
                        failed = 0,
                        error = "Compile failed:\n" + errors.ToDisplay(),
                        results = Array.Empty<object>(),
                    }, _testJsonOpts);
                }

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
                        results = ResultsToObjects(new List<FadeTestResult> { r }),
                    };
                }
                return JsonSerializer.Serialize(payload, _testJsonOpts);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    passed = 0,
                    failed = 0,
                    error = "Runtime error: " + ex.GetType().Name + ": " + ex.Message,
                    results = Array.Empty<object>(),
                }, _testJsonOpts);
            }
        }

        private static List<object> ResultsToObjects(List<FadeTestResult> results)
        {
            var list = new List<object>(results.Count);
            foreach (var r in results)
            {
                list.Add(new
                {
                    name = r.testName,
                    passed = r.passed,
                    duration = r.duration.TotalMilliseconds,
                    failureMessage = r.failureMessage,
                    failureReason = r.failureReason,
                    failureSourceText = r.failureSourceText,
                });
            }
            return list;
        }

        public void Dispose()
        {
            _pageDotNetRef?.Dispose();
            _pageDotNetRef = null;
            _game = null;
        }
    }
}
