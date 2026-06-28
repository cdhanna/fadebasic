# MonoGame Project Type — Plan

Add a `'monogame'` project type to fade.json that runs `Fade.MonoGame` games
inside the web editor (and ships them as static bundles for itch.io). Goal: as
close to identical dev experience as possible between local desktop dev and
in-browser dev, so the same source survives both.

## References

- **Fade.MonoGame** (the desktop game framework we're porting):
  `/Users/chrishanna/Documents/Github/Fade.MonoGame/Fade.MonoGame/Fade.MonoGame`
  - `Fade.MonoGame.Lib/` — command library (sprites, audio, collision, render,
    texture, input, tween, transform, text, math).
  - `Fade.MonoGame.Game/` — `Game1` host, `GameReloader`, `DebugUISystem`
    (2520 lines of ImGui — user-authored dev UI + engine inspectors).
  - `Fade.MonoGame.csproj` — example user project; references the lib + game
    via `<FadeCommand>` items, lists `.fbasic` files via `<FadeSource>`.
- **XnaFiddle** — proves MonoGame-in-the-browser end-to-end; reference for
  iframe lifecycle, URL-fragment sharing, GitHub Pages-style static deploy:
  - <https://xnafiddle.net/>
  - <https://github.com/vchelaru/XnaFiddle>
- **KNI** — nkast's MonoGame fork with a Blazor WebGL host. The only viable
  browser-MonoGame today. XnaFiddle uses it; we will too.

## Architecture

Three pieces:

1. **Playground** — adds a `'monogame'` value to `FadeProjectType`, a new "Game"
   dockview panel containing a `<canvas>`, and a publish action that zips the
   runtime + project for itch.io.
2. **`WebRuntime.MonoGame`** — a second Blazor WASM project alongside the
   existing `WebRuntime/`. Loaded inline on the page (same pattern as the
   existing WebRuntime worker — `dotnet.create()` from a published `wwwroot/`,
   page calls `[JSExport]` methods directly). Hosts KNI on a canvas, loads
   `Fade.MonoGame.Lib` commands, runs the user's compiled bytecode in `Game1`.
3. **`Fade.MonoGame` (existing repo)** — multi-targeted via
   `<TargetFrameworks>net10.0;net10.0-browser</TargetFrameworks>` so a single
   source tree builds for both desktop and browser. Divergence handled by
   `#if BROWSER` and TFM-conditional `<PackageReference>`s. **No sibling
   `*.Web` projects.**

Two WASM runtimes co-exist in the same page:

- The existing `WebRuntime/` keeps owning LSP / compilation / test discovery /
  debug — runs in a Web Worker, off the main thread.
- The new `WebRuntime.MonoGame/` owns running the game — runs on the main
  thread because the WebGL canvas + MonoGame's `Game.Run()` requestAnimationFrame
  loop need main-thread access.

Editor JS holds references to both. To run a monogame project: worker compiles
→ returns bytecode bytes to the page → page calls `FadeMonoGameBridge.LoadProgram(bytes)`
on the main-thread runtime. Direct function call, no postMessage at the
runtime boundary.

```
┌───────────────────────────────────────────────────────────────────┐
│ Playground (Vite + Monaco + dockview) — single document           │
│                                                                   │
│  ┌─────────────┐  ┌─────────────┐  ┌──────────────────────────┐   │
│  │  Editor     │  │  Tests/etc  │  │  Game (canvas)           │   │
│  │             │  │             │  │  ┌────────────────────┐  │   │
│  │             │  │             │  │  │ WebRuntime.MonoGame│  │   │
│  │             │  │             │  │  │ (main-thread WASM) │  │   │
│  │             │  │             │  │  │  KNI + Game1       │  │   │
│  │             │  │             │  │  │  Fade.MonoGame.Lib │  │   │
│  │             │  │             │  │  │  user bytecode     │  │   │
│  │             │  │             │  │  └────────────────────┘  │   │
│  └──────┬──────┘  └──────┬──────┘  └────────────┬─────────────┘   │
│         └────────┬───────┘                      │                 │
│                  ▼                              │ JS calls        │
│         ┌────────────────┐                      │ LoadProgram(),  │
│         │ WebRuntime     │  worker postMessage  │ Reset(), etc.   │
│         │ (worker WASM)  │   compile + LSP      │                 │
│         │ LSP/compile/   │                      ▼                 │
│         │ tests/debug    │             ┌────────────────┐         │
│         └────────────────┘             │ Editor JS      │         │
│                  ▲                     │ (glue)         │         │
│                  └─────────────────────┴────────────────┘         │
└───────────────────────────────────────────────────────────────────┘
```

**Lifecycle:**
- Switch project within `'monogame'` type → call `Game1.ResetFade(newBytecode)`,
  reuse the same KNI runtime + canvas. Already supported by `Game1`.
- Switch to a non-`'monogame'` project → hide the canvas, leave the runtime
  warm. (Tearing KNI down cleanly is unverified; not worth the engineering
  cost for v1.)
- Hard reset → reload the page. Cheap and rare.

## Multi-target strategy for Fade.MonoGame

```xml
<TargetFrameworks>net10.0;net10.0-browser</TargetFrameworks>
```

- TFM-conditional package references:
  - `net10.0` → `MonoGame.Framework.DesktopGL`, `ImGui.NET`, content pipeline.
  - `net10.0-browser` → `nkast.Xna.Framework.*` (KNI WebGL host).
- `#if BROWSER` guards:
  - `DebugUISystem` (entire file) — no-op in browser for v1.
  - `ImGuiRenderer` and the ImGui calls in `Game1`.
  - `GameReloader.WatchFiles` (`FileSystemWatcher` doesn't exist in WASM).
  - `ContentWatcher` (filesystem-based).
  - `Microsoft.Xna.Framework.Content.Pipeline.Extra` references in `Game1`.
- User-facing dev-UI commands (`set ui slider`, etc.) keep their command
  signatures in browser builds so user fbasic compiles — they just become
  no-ops there. Documented as "in-game debug UI not yet available in web."

**Pre-commit check:** verify KNI exposes types under
`Microsoft.Xna.Framework.*` namespaces (historically true). If KNI lives under
`nkast.Xna.Framework.*`, add a `#if BROWSER` `using` shim file; the multi-target
story still holds.

## Phasing

### Phase 0 — Schema + UI placeholder (hours)

- [Playground/src/fade-config.ts:11](src/fade-config.ts#L11): add `'monogame'`
  to `FadeProjectType`. Update `ALLOWED_TYPES`, `validateFadeProject`,
  `defaultFadeProject`.
- Update `public/fade.schema.json` to mirror.
- Add a "Game" panel definition to dockview that renders a placeholder ("Game
  runtime not yet built") when `fade.json.type === 'monogame'`.
- Project-create flow: offer "Web" vs "MonoGame" when creating a new project.

### Phase 1 — KNI skeleton on the page (~1–2 weeks)

Patterns borrowed from XnaFiddle's `XnaFiddle.BlazorGL`:

- **JS-driven tick loop.** .NET does NOT own the game loop. JS owns
  `requestAnimationFrame` and calls `instance.invokeMethod('TickDotNet')`
  per frame. Wins: pause-when-hidden, FPS cap, 5s frame watchdog, hot-reload
  without runtime teardown — all from JS. Requires decomposing `Game1`
  (today: `game.Run()` is blocking) so per-tick work is a callable method.
- **KNI's nkast.Wasm.* JS shims** load as `<script>` tags **before** Blazor
  starts, from the NuGet static-web-assets pipeline:
  `_content/nkast.Wasm.JSInterop/...`, `Wasm.Dom`, `Wasm.Canvas`,
  `Wasm.Audio`, etc. Their `index.html` enumerates them.
- **Blazor Module shim.** KNI's JS expects `globalThis.Module` (Emscripten);
  Blazor stopped exposing it. Bridge with
  `globalThis.Module = Blazor.runtime.Module`, plus a `getArrayLength`
  polyfill. Fragile but necessary; treat as boilerplate.
- **Canvas-resize sync.** Each frame, JS reads container dims, resizes the
  `<canvas>`, calls `instance.invokeMethod('OnCanvasResized', w, h)` into
  .NET. KNI's GraphicsDeviceManager handles the back-buffer update.
- **`PublishTrimmed=false`** as the starting posture. We may be able to flip
  later (we don't have Roslyn-needs-full-metadata problem like XnaFiddle).

Concrete steps:

- New project: `WebRuntime.MonoGame/` (alongside `WebRuntime/`).
  - Blazor WASM, **TargetFramework=net8.0** (KNI's current TFM — vs
    WebRuntime's net10.0; the two projects publish independently, no conflict).
  - `<KniPlatform>BlazorGL</KniPlatform>`, `<DefineConstants>$(DefineConstants);BLAZORGL</DefineConstants>`.
  - References: `Kni.Platform.Blazor.GL` (KNI WebGL host) + Fade.MonoGame.Lib
    (browser TFM) + Fade.MonoGame.Game (browser TFM).
  - Force-load Fade.MonoGame.Lib's command-registration types in `Program.cs`
    via `_ = typeof(...)` / `RuntimeHelpers.RunClassConstructor(...)` so
    Blazor's lazy-load doesn't break command discovery (XnaFiddle does the
    same dance for its plugin assemblies).
  - JSExport surface, kept minimal:
    - `InitRenderJS(instance)` — receives the Blazor instance ref; starts the
      JS-side rAF loop.
    - `TickDotNet()` — one frame's `Update` + `Draw`.
    - `OnCanvasResized(w, h)` — back-buffer update.
    - `OnGameTimedOut(ms)` — JS watchdog tripped; stop ticking, surface error.
    - `LoadProgram(byte[] bytecode)` — calls `Game1.ResetFade(...)`.
- New build script: `Playground/scripts/build-monogame-runtime.mjs` mirroring
  the existing `build-runtime.mjs` pattern. Publishes WebRuntime.MonoGame into
  `Playground/public/monogame-runtime/`.
- New JS module: `Playground/src/monogame-host.ts`. Loads
  `_framework/blazor.webassembly.js` (NOT `dotnet.js` — Blazor WASM uses a
  different bootstrap than our existing worker), applies the Module shim,
  starts the rAF loop. Mounts KNI on a `<canvas id="mg-canvas">` inside the
  Game dockview panel.
- For Phase 1's "prove it works" milestone, hardcode an `ILaunchable` that
  draws a sprite — no FadeBASIC integration yet.

**Phase 1 done = a static sprite renders on a canvas inside a Playground tab.**

### Phase 2 — FadeBASIC integration (~1 week)

- Compilation stays in the existing worker (`WebRuntime`). It already knows how
  to register `Fade.MonoGame.Lib`'s commands — the workspace registration just
  needs to switch on `fade.json.type`:
  - `'web'` → `WebCommands + StandardCommands`.
  - `'monogame'` → `FadeMonoGameCommands + StandardCommands`.
- Editor's Run button (for monogame projects):
  1. Worker compiles → returns bytecode envelope (program bytes + debug data
     + command table reference) to the page via worker `postMessage`.
  2. Editor JS calls `FadeMonoGameBridge.LoadProgram(envelope)` on the
     main-thread runtime directly (no postMessage at this boundary).
  3. `LoadProgram` calls `Game1.ResetFade(...)` with the new bytecode.
- LSP/diagnostics for monogame projects: same worker, but `WebRuntime` needs to
  register Fade.MonoGame.Lib's commands for completion/hover/signature-help.
  Means publishing Fade.MonoGame.Lib's command metadata (the source-generator
  output) into the worker's WASM image. Concretely: WebRuntime gets a TFM-
  conditional reference to the same `Fade.MonoGame.Lib` so its
  `CommandCollection(new WebCommands(), new FadeMonoGameCommands(), ...)` is
  the union; the *runtime* command implementations only matter on the main-
  thread MonoGame runtime, the LSP side just needs the signatures.
- Debug session: defer to Phase 5+. Run-only for v1.

**Phase 2 done = a user can type a fbasic game that uses sprite/audio/input
commands, hit Run, and see it play in the Game tab.**

### Phase 3 — Shader editor + raw asset uploads (~2-3 weeks)

The piece that completes the inter-op story: same `.fx` source feeds both
local MGCB and the in-browser pipeline.

- **In-browser shader compilation** with the modern chain:
  ```
  .fx (HLSL) → dxc-wasm → SPIR-V → spirv-cross-wasm → GLSL ES
                                                       │
                                                       ▼
                                          custom Effect(glsl, paramManifest)
  ```
  - dxc and SPIRV-Cross both ship official WASM builds from Khronos.
  - The custom `Effect` subclass reads SPIRV-Cross's parameter reflection to
    populate `effect.Parameters[...]` so the runtime API matches MGCB output.
  - Errors normalized to MGCB's shape so dev experience matches local.
  - Document the supported HLSL subset (no geometry/compute shaders, etc.).
- **Asset uploads**: drag-and-drop PNG/OGG into the Assets tree in the editor.
  Steal XnaFiddle's `contentFileCache` pattern verbatim — intercept
  `XMLHttpRequest.open/send` at the JS level to serve in-memory bytes for
  registered virtual paths. KNI's `TitleContainer.OpenStream` does sync XHR,
  so this lets **stock `Content.Load<Texture2D>("foo")` work against
  drag-and-dropped files** without any API divergence from local dev. Bytes
  flow OPFS → JS register → satisfied XHR → KNI content pipeline. **No MGCB.**

### Phase 4 — Dev UI in HTML (~1-2 weeks)

Lift `DebugUISystem` out of `#if !BROWSER` and surface it as HTML dockview
panels. Same-document means this is direct C#-to-DOM, no cross-frame messaging.

- C# exposes `[JSExport] DescribeDevUI()` returning a JSON tree of the current
  dev UI graph (controls, current values, sprite/transform/effect inspectors).
  Editor calls it once per frame (or on-demand). Tiny payload.
- Editor renders the tree as DOM widgets — `vscode-text-field` for inputs,
  `vscode-button` for buttons, etc. Lookup table per control type.
- Events flow back through direct calls: `FadeMonoGameBridge.FireEvent(controlId, kind, payload)`.
  Applied on next VM tick.
- Engine inspectors (sprite, transform, effect) get their own dockview panels
  that read-only render the JSON tree, with slider-style edits round-tripping
  through the same RPC.
- Input ownership: borrow XnaFiddle's pattern. KNI registers its mouse
  listeners on `window` (bubbling phase). Listen on `document` (one level
  below) and call `stopPropagation()` when `event.target` is inside any
  dockview panel that owns dev UI. Cleaner than relying on focus state.
  No `io.WantCaptureKeyboard` polling needed.

### Phase 5+ — Polish

- Debug session for monogame projects. Mirrors the existing DAP-style tick
  loop in `WebRuntime/FadeBridge.cs` — but on the main thread, so the
  debug session ticks alongside `Game1.Update` rather than on its own
  schedule.
- Hot reload: editor watches OPFS, compiles on save, calls
  `LoadProgram(newBytecode)` directly. `Game1.ResetFade` swaps in the new
  program. Replaces the `FileSystemWatcher` path used on desktop.
- WASM ports of FreeType / DXT encoders if font baking or texture memory
  becomes a bottleneck.
- Shared-source story: dxc + spirv-cross becomes the canonical pipeline on
  desktop too once MonoGame mainline finishes moving in that direction.

### Eventual — Publish to itch (~days when we get to it)

Deferred. Not on the v1 critical path; sketched here so we don't paint
ourselves into a corner that would make this hard later. Phase 1 already
constrains us toward a static-bundle-shaped runtime, which is all itch
needs.

- "Publish" action in the editor that:
  1. Compiles the project to bytecode.
  2. Bundles `public/monogame-runtime/` (the published KNI WASM tree).
  3. Embeds the compiled bytecode and project content files into the bundle
     (alongside `wwwroot/`).
  4. Writes a stub `index.html` that loads the runtime the same way the
     editor does (Blazor.start + KNI shims) and calls `LoadProgram` against
     the embedded bytecode at boot.
  5. Zips the result.
- itch.io accepts a single .zip with an `index.html` at the root for HTML5
  games. This is exactly what we produce. itch wraps the page in its own
  iframe on display, giving consumer-side isolation for free.

## Open decisions

- **KNI namespace check.** XnaFiddle's `SampleGame.cs` uses
  `Microsoft.Xna.Framework`/`Microsoft.Xna.Framework.Graphics` directly —
  confirming KNI preserves the XNA namespaces under different package names
  (`Kni.Platform.Blazor.GL` etc.). The `#if BROWSER` plan holds without
  needing a using-shim file.
- **TFM choice — run a spike before committing.** KNI's
  `Kni.Platform.Blazor.GL.csproj` is locked to `<TargetFrameworks>net8.0</TargetFrameworks>`
  (singular). Practical question: can a net10 Blazor WASM host reference
  KNI's net8 assembly? .NET forward-compat says yes; the risk is the
  Blazor internal shims (`Blazor.runtime.Module`, `Blazor.platform.getArrayLength`)
  may have moved between Blazor 8 and 10. Stand up a stub net10 Blazor
  project, reference KNI net8, copy XnaFiddle's shim code, try to draw a
  sprite. If it boots, ship `WebRuntime.MonoGame` at net10 (matches our
  existing `WebRuntime`). If it doesn't, ship at net8 — the two projects
  publish independently and coexist without conflict either way.
- **Two runtimes, two boot styles.** Existing `WebRuntime/` is plain WASM
  (`dotnet.create()` from `_framework/dotnet.js`). New `WebRuntime.MonoGame`
  is Blazor WASM (`Blazor.start()` from `_framework/blazor.webassembly.js`).
  Both publish to their own `public/*-runtime/` directories.
- **Lifecycle on project-type switch.** Tearing KNI down cleanly is
  unverified — for v1, hide the canvas and leave the runtime warm. Hard
  reset is a page reload.
- **Game1 decomposition.** `Game1` today does its work inside `game.Run()`'s
  blocking loop ([Game1.cs](../Fade.MonoGame/Fade.MonoGame/Fade.MonoGame.Game/Game1.cs)).
  For JS-driven tick we need a `TickOneFrame()` entry point. Probably
  `#if BROWSER` a constructor variant that skips `Run()` plus a public
  method that calls `Update` + `Draw` once. Verify what KNI's `Game` exposes
  for this — they may already have a hook (`RunOneFrame` or similar).
- **Bytecode + content envelope format.** Define a stable JSON+binary envelope
  the editor passes to `LoadProgram`. Same format the Phase-3 publish bundle
  reads at boot. One format, two consumers.

## Risks

- **KNI release cadence vs MonoGame drift.** KNI is the only viable browser
  MonoGame today. If nkast slows down, the inter-op story degrades. Mitigation:
  pin KNI version; track mainline MonoGame's eventual WASM support.
- **HLSL feature subset.** Some HLSL features don't survive SPIR-V → GLSL ES.
  Mitigation: document supported subset; compile-time error on unsupported.
- **Bundle size for itch.** KNI WASM (~4 MB brotli) + WebRuntime WASM
  (~1-2 MB) + content. itch is fine with this but slow initial load. Acceptable
  for v1.
- **No in-game dev UI for v1.** Documented gap; closed in Phase 4.
- **Blazor internal-API shim.** KNI's JS needs `globalThis.Module` and
  `Blazor.platform.getArrayLength`, both of which moved into Blazor
  internals. XnaFiddle bridges them and explicitly comments that future
  Blazor releases may break this. Mitigation: pin Blazor + KNI versions
  together; surface a clear boot error if the shim throws.
- **Game loop watchdog.** A fbasic VM bug or infinite loop can hang the
  rAF tick. Mirror XnaFiddle's `WATCHDOG_FRAME_MS = 5000` — JS measures
  tick time; if exceeded, notify .NET via `OnGameTimedOut` to stop the
  current program. Editor surfaces it as a runtime error.
