# Default MonoGame assets

These are the built-in assets bundled with the fadebasic.com docs site so the
**Commands** and **Game Tutorial** example snippets can load real content —
an image, a font, and sound effects — when you hit **Run**.

They are staged into `public/fade/assets/` at build time (see
`scripts/stage-assets.mjs`, run as part of `npm run prep`) and registered with
the MonoGame runtime when a game snippet boots (see the components package's
`monogame-preview.ts`).

Every MonoGame example docstring in the `Fade.MonoGame` project references
**only** the asset names below, so every sample runs against content that
actually exists.

## Asset names (as used in Fade code)

| Kind  | Fade name    | Source file                    | Load with |
|-------|--------------|--------------------------------|-----------|
| Image | `ghost`      | `images/ghost.png`             | `texture 1, "ghost"` |
| Font  | `font`       | `fonts/press-start-2p.ttf`     | `font 1, "font"` |
| Audio | `jump`       | `audio/jump.wav`               | `load sfx clip 1, "jump"` |
| Audio | `coin`       | `audio/coin.wav`               | `load sfx clip 1, "coin"` |
| Audio | `laser`      | `audio/laser.wav`              | `load sfx clip 1, "laser"` |
| Audio | `powerup`    | `audio/powerup.wav`            | `load sfx clip 1, "powerup"` |
| Audio | `select`     | `audio/select.wav`             | `load sfx clip 1, "select"` |
| Audio | `explosion`  | `audio/explosion.wav`          | `load sfx clip 1, "explosion"` |

Images and fonts are compiled to MonoGame `.xnb` at build time (the runtime's
`BrowserContentManager` only serves XNB); audio is registered as raw bytes and
decoded by Web Audio.

## Provenance & licenses

- **`images/ghost.png`** — "Ghost Lee", the Fade Basic mascot. © Fade Basic project.
- **`fonts/press-start-2p.ttf`** — *Press Start 2P* by CodeMan38.
  License: **SIL Open Font License 1.1 (OFL-1.1)**.
  Source: https://fonts.google.com/specimen/Press+Start+2P
- **`audio/*.wav`** — from *The Essential Retro Video Game Sound Effects
  Collection [512 sounds]* by **Juhani Junkala**.
  License: **CC0-1.0** (public domain).
  Source: https://opengameart.org/content/512-sound-effects-8-bit-style
  (mirrored in the [FadeLand Catalog](https://github.com/cdhanna/FadeLandAssets)).

All bundled assets are CC0 or OFL, i.e. safe to redistribute with the site.
