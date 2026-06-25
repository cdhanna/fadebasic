// Runtime rules for the MonoGame project type — how the game loop and graphics
// actually behave. These are NOT core language syntax (that's FADE_RULES); they
// describe the retained-mode, sync-driven runtime, and exist because the model
// keeps importing habits from immediate-mode engines: "clear/reset the screen
// each frame", "wait/delay to pause", "redraw the sprite every frame". In this
// runtime those are wrong. Injected only for `type: "monogame"` projects.

export const MONOGAME_RULES = `MONOGAME RUNTIME RULES (the game loop + graphics work differently from other engines — do NOT carry over habits):

1. The screen renders ONLY on \`sync\`. One \`sync\` call = one rendered frame. A game/animation loop MUST call \`sync\` each iteration. To "wait", "pause", or advance time, call \`sync\` (in a loop) — there is NO \`wait\`, \`delay\`, or \`sleep\` for frame timing.
2. Graphics are RETAINED, not immediate-mode. A resource persists once created: a sprite keeps drawing itself every frame until you hide or delete it. So:
   - Do NOT "clear the screen", "reset the screen", or "cls" each frame — there is no clear step in a sprite loop, and stale frames are not a thing here.
   - Do NOT recreate/reload a sprite every frame.
3. Pattern: create resources ONCE before the loop, then inside the loop only UPDATE their state (e.g. set the sprite's position) and call \`sync\`. To make something disappear, hide/delete it (search_docs for the exact command) — don't try to paint over it.
4. When loading assets, do NOT include the file extension — pass the bare content path. The asset-loading commands are \`texture\` (images), \`font\` (fonts), \`effect\` (shaders), and \`load sfx clip\` (sounds). e.g. \`texture 1, "ship"\` and \`load sfx clip 1, "laser"\` — NEVER \`"ship.png"\` or \`"laser.wav"\`. The content pipeline resolves the bare name; an extension makes the load fail.
5. NEVER reference an asset file that is not actually in the project. Do NOT invent filenames like \`"player"\` or \`"ship"\` and hope they exist — a missing asset breaks the program. Only use asset names you were explicitly told are available.
6. When you need a simple graphic but have NO image asset, use the BUILT-IN 1×1 white-pixel texture: it is texture id \`0\` and needs NO \`texture\` load. Create the sprite with texture id 0 (\`sprite 1, x, y, 0\`). Because the pixel is 1×1 it is invisible at default size, so you MUST scale it up with \`size sprite 1, 50, 50\` (pick a sensible width/height). This is the correct way to draw a colored rectangle/placeholder without an image file.

If you think you need to "clear the screen" or "wait a bit", you are wrong about this runtime — re-read rules 1–2 and use \`sync\`.`;
