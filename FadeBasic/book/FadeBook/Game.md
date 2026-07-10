# Game Tutorial

_Fade Basic_ can drive a real-time game canvas through the **MonoGame** runtime. The snippets on this page run against a live game window — hit **Run** and a small canvas pops up in the corner. Edit the code and run again to see it change.

> [!NOTE]
> This tutorial is just getting started. Each snippet here runs on the MonoGame runtime, so you get a graphics window instead of a text console.

## Your first canvas

Every MonoGame program is a loop. Each pass through the loop draws one frame, and `sync` presents that frame to the screen. This program animates the background colour by nudging a counter every frame.

```basic
` Animate the background colour every frame.
t = 0
do
    t = t + 1
    set background color rgb(t mod 256, 40, 90)
    sync
loop
```

Try setting a breakpoint on the `t = t + 1` line and hitting **Debug** — you can step frame-by-frame and watch `t` climb in the Variables panel, exactly like the Language examples.

## Drawing a sprite

The docs site ships a handful of built-in assets you can use right away. Load the **ghost** image into a texture slot, then draw it as a sprite each frame.

```basic
` Draw the Fade ghost in the middle of the screen.
texture 1, "ghost"
do
    set background color rgb(20, 20, 40)
    sprite 1, 900, 480, 1
    sync
loop
```

## Text and sound

The built-in **font** renders text, and the built-in sound effects (`jump`, `coin`, `laser`, `powerup`, `select`, `explosion`) play through `load sfx clip`.

```basic
` Show a title and play a coin sound once at startup.
font 1, "font"
load sfx clip 1, "coin"
play sfx 1
do
    set background color rgb(30, 10, 40)
    text 1, 720, 500, 1, "HELLO FADE"
    sync
loop
```
