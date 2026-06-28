# GhostBot

Local **llama.cpp** inference bridge for [Fade Playground](../Playground/). Pairs with the deployed Playground over **WebRTC** (Trystero / BitTorrent trackers) — no `localhost` fetch, no browser GPU limits.

```
Playground (browser)  ←—— WebRTC data channel ——→  GhostBot (Tauri)
        │                                              │
   tool execution                               llama.cpp GGUF
   diff / LSP / Monaco                          Metal / Vulkan / CPU
```

## Quick start

### 1. Launch GhostBot (native window)

```sh
cd ghostBot
npm install
npm start
```

This opens the **desktop app** (Tauri window). Do **not** open `http://localhost:1420` in a browser — model download and inference only work in the native window.

`npm run dev:web` is for frontend-only hacking; it cannot download or load models.

Requires **Rust**, **Node 20+**, and on macOS **Xcode CLI tools** (for Metal + bindgen/clang).

### 2. Download a model

In the GhostBot window:

1. Click **Download recommended** (one-time ~4.5 GB).
2. Click **Load model**.

### 3. Pair with Playground

1. Open Playground → **AI Models** — GhostBot is the default provider.
2. Click **Load** — the panel shows a **join code** (also in loading progress).
3. Enter that code in GhostBot → **Connect**.

When status shows **Connected**, chat uses your local GPU.

## Recommended model

| Model | Quant | Size | Why |
|-------|-------|------|-----|
| **Qwen2.5-7B-Instruct** | **Q4_K_M** | ~4.5 GB | Best balance of tool-following, speed, and VRAM on 8 GB GPUs |

This is the default download (`bartowski/Qwen2.5-7B-Instruct-GGUF`). It follows the Playground’s in-prompt `<tool_call>` protocol much more reliably than in-browser 4B ONNX.

**Alternatives:**

- **Qwen3-4B-Instruct Q4_K_M** — lower VRAM (~3 GB), slightly weaker tools.
- **Hermes-3-Llama-3.1-8B Q4_K_M** — strong instruction following; larger download.

Models are stored in:

- macOS: `~/Library/Application Support/ghostbot/models/`
- Linux: `~/.local/share/ghostbot/models/`

You can also drop any `.gguf` into that folder and select it in the UI.

## VRAM management

- **Unload (free VRAM)** drops the loaded model and releases GPU memory immediately.
- Switching models unloads the previous weights first.
- Quitting the app frees all backend resources.

## Protocol

JSON messages over an ordered WebRTC data channel (`ghost` action, `fade-ghostbot` app id). See:

- `src/protocol.ts` (GhostBot)
- `Playground/src/ai/providers/ghostbot-protocol.ts` (Playground)

Playground sends `stream` requests; GhostBot streams `stream-event` text deltas back. Tool execution stays in the browser — only inference runs locally.

GhostBot also broadcasts `model-status` (on connect, peer join, ping, and
load/unload) so the Playground AI panel can show *which* model is loaded —
or warn "Connected — no model loaded".

**Trystero 0.20 gotcha:** the `rtcConfig` value handed to `joinRoom` is
spread into simple-peer's *options* object, which expects the
RTCConfiguration under its `config` key. Use `toTrysteroRtcConfig()`
(`src/ice-config.ts` here, `ice-probe.ts` in the Playground) — passing a
bare RTCConfiguration is silently ignored and you get simple-peer's
default STUN servers instead of yours.

## Development

```sh
npm run tauri:dev    # hot-reload UI + Rust
npm run tauri:build  # release binary
```

### Packaged app (recommended on macOS 15+)

```sh
npm run tauri:build -- --bundles app
open src-tauri/target/release/bundle/macos/   # GhostBot.app — drag to /Applications if you like
```

Running the packaged app from Finder matters on macOS 15: the **Local
Network** privacy permission is attributed to GhostBot itself (with a
proper prompt on first connect, thanks to `src-tauri/Info.plist`). Under
`npm start` the permission follows whatever launched it — your terminal —
which is a common cause of `Ice connection failed` when pairing with a
Playground on the same machine or LAN. Models are shared with dev builds
(same app identifier → same data directory).

Build notes: `bundle.macOS.minimumSystemVersion` must stay ≥ 10.15 —
`tauri build` exports it as `MACOSX_DEPLOYMENT_TARGET`, and llama.cpp uses
`std::filesystem` (unavailable before 10.15). If you ever change it,
delete `src-tauri/target/release/build/llama-cpp-sys-2-*` first: the
cmake cache pins the old deployment target and the error ("'path' is
unavailable") survives rebuilds. The macOS bundler also requires the
`.icns` entry in `bundle.icon` (generated via `npx tauri icon`).

### Automated pairing probes

Two Playwright probes in `Playground/scripts/` exercise the full WebRTC
path with the real modules from both apps:

- `npm run probe:ghostbot:cross` (from `Playground/`) — browser↔browser
  pairing + stream round-trip. Needs the Playground dev server and a plain
  `npx vite --port 1420` in `ghostBot/`. Pass `webkit` as an arg for a
  WebKit ghost peer (note: cross-engine mDNS does not resolve between
  Playwright's bundled headless browsers, so `webkit` mode fails on ICE in
  a way real Safari/WKWebView does not — use it for tracker/signaling
  debugging only).
- `npm run probe:ghostbot:tauri <CODE>` — drives the REAL desktop app.
  Set `devUrl` in `src-tauri/tauri.conf.json` to
  `http://localhost:1420/?probe=<CODE>` and `npm start`; the dev-only
  `?probe=` hook auto-connects with a canned stream handler (no model
  needed). Remember to revert `devUrl` afterwards.

### Rust features

| OS | Backend |
|----|---------|
| macOS | Metal |
| Linux / Windows | Vulkan (CPU fallback if unavailable) |

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| Playground times out waiting for GhostBot | Enter the join code in GhostBot, load a model, click Connect |
| `Ice connection failed` in the log | A single line is normal — Trystero opens a pool of connection attempts and some lose the race. Only worry if the log says *no attempt has connected* (see next two rows). |
| ICE keeps failing, same machine / LAN | macOS 15 Local Network privacy: System Settings → **Privacy & Security → Local Network**, enable the app that launched GhostBot (your **terminal/IDE** for `npm start`, **GhostBot** for the packaged build). The log pane narrates every attempt: `webrtc attempt #N: connected via host↔host` etc. |
| ICE keeps failing, different networks | Strict NAT needs a TURN relay. Add credentials on **both** sides: localStorage `ghostbot.customIceServers` (GhostBot) and `fade.collab.customIceServers` (Playground) — JSON array of `RTCIceServer`, e.g. `[{"urls":"turn:turn.example.com:3478","username":"u","credential":"c"}]` |
| `load failed` | Ensure GGUF is complete; re-download |
| Slow first token | Normal — model mmap + GPU upload on first load |

## License

Same as parent Fade / dby repository.
