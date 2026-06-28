# FadeBasic Command Reference

## FadeBasic.Lib.Standard.StandardCommands

### rgb

Creates a color with values for red, green, blue, and optionally alpha.Each value should be between 0 and 255.

**Parameters**

- `Byte` **r** - the red channel of the color.
- `Byte` **g** - the green channel of the color.
- `Byte` **b** - the blue channel of the color.
- `Byte` _(optional)_ **a** - the alpha channel of the color. By default, this will be 255, so it is fully opaque.

**Returns** `Integer` - A single integer representing the color

**Remarks**

A few common color codes are, 
-  Red - (255, 0, 0) 
-  Salmon - (255, 128, 128) 
-  White - (255, 255, 255) 



The resulting integer is just a byte packed version of the four strings. It may be negative.

---

### wait ms

**Parameters**

- `Integer` **arg1**

---

### debug breakpoint

This command only exists to help attach a C# debugger to the program.This command will halt execution until a C# debugger is attached to the execution host.

**Parameters**


---

### test build

**Returns** `Integer`

---

### machine name$

**Parameters**

- `String` _(ref)_ **arg1**

---

### randomize

**Parameters**

- `Integer` **arg1**

---

### rnd

**Parameters**

- `Integer` **arg1**

**Returns** `Integer`

---

### timer

**Returns** `DoubleInteger`

---

### inc

**Parameters**

- `Integer` _(ref)_ **arg1**
- `Integer` _(optional)_ **arg2**

---

### dec

**Parameters**

- `Integer` _(ref)_ **arg1**
- `Integer` _(optional)_ **arg2**

---

### upper$

**Parameters**

- `String` **arg1**

**Returns** `String`

---

### lower$

**Parameters**

- `String` **arg1**

**Returns** `String`

---

### right$

**Parameters**

- `String` **arg1**
- `Integer` **arg2**

**Returns** `String`

---

### left$

**Parameters**

- `String` **arg1**
- `Integer` **arg2**

**Returns** `String`

---

### mid$

**Parameters**

- `String` **arg1**
- `Integer` **arg2**

**Returns** `String`

---

### chr$

**Parameters**

- `Integer` **arg1**

**Returns** `String`

---

### str$

**Parameters**

- `Integer` **arg1**

**Returns** `String`

---

### spaces$

**Parameters**

- `Integer` **arg1**

**Returns** `String`

---

### val

**Parameters**

- `String` **arg1**

**Returns** `Float`

---

### asc

**Parameters**

- `String` **arg1**

**Returns** `Integer`

---

## Fade.MonoGame.Lib.FadeMonoGameCommands

### push asset

Pushes an asset file into the content build pipeline.

This is a macro-time command. It runs during compilation, not at game runtime.

**Parameters**

- `String` **path** - The file path of the asset to add to the content build.

**Examples**

Push a texture asset so it is available at runtime:
```
` push an image into the content pipeline
# push asset "Assets/Images/player-sprite-v2.png"
# rename asset "Images/Player"
 ` later at runtime, load it by its renamed path
texture 1, "Images/Player"
sprite 1, 100, 100, 1
```

Push a font asset for text rendering:
```
` push a font into the content pipeline
# push asset "Assets/Fonts/MyFont.spritefont"
# rename asset "Fonts/Main"
 ` later at runtime, load and use the font
font 1, "Fonts/Main"
text 1, 1, 100, 50, "Hello!"
```

**Remarks**

Use this inside a macro block (lines prefixed with `#`) to tell the contentpipeline about an asset your game needs. The pipeline will process and pack it soit is available at runtime through commands like`texture`, `font`, or`load sfx clip`. After pushing, you can rename the asset with`rename asset` if the original filename is unwieldy.The push/rename pair is the most common macro pattern for setting up content.

---

### rename asset

Renames the most recently pushed asset in the content build pipeline.

This is a macro-time command. It runs during compilation, not at game runtime.It operates on whatever `push asset` last added.

**Parameters**

- `String` **name** - The new content name for the asset.

**Examples**

Rename a pushed asset to a shorter, cleaner path:
```
` push an audio file with a long filename and give it a short name
# push asset "Assets/Audio/bubble-pop-2-293341.mp3"
# rename asset "Audio/BubblePop"
 ` at runtime, load using the short name
load sfx clip 1, "Audio/BubblePop"
```

Rename multiple assets in sequence:
```
` push and rename several textures
# push asset "Assets/Images/enemy_spritesheet_final_v3.png"
# rename asset "Images/Enemy"
# push asset "Assets/Images/bg-tiles-large.png"
# rename asset "Images/Background"
 ` at runtime, load them by their clean names
texture 1, "Images/Enemy"
texture 2, "Images/Background"
```

**Remarks**

Call this right after `push asset` when the original filenameis too long, includes version numbers, or does not match the name you want to use inyour runtime code. The new name becomes the content path you pass to loadingcommands like `texture` or`load sfx clip`.

---

### free sfx clip id

Peeks at the next available sound effect clip ID without claiming it.

This doesn't reserve the ID, so another call could grab it before you do.

**Parameters**

- `Integer` _(ref)_ **sfxClipId** - Receives the next free clip ID.

**Returns** `Integer` - The next available clip ID (not yet reserved).

**Examples**

Peek at the next clip ID to see what it would be:
```
` check what ID would be assigned next
nextClipId = free sfx clip id(nextClipId)
```

**Remarks**

Most of the time you'll want `reserve sfx clip id`instead, which actually claims the slot. This is the "peek" half of the peek-vs-claimpattern. If you already know your ID, skip both and call`load sfx clip` directly.

---

### reserve sfx clip id

Claims the next available sound effect clip ID and initializes its slot.

Use this when you need to wire up references before loading the actual audio data.

**Parameters**

- `Integer` _(ref)_ **sfxClipId** - Receives the reserved clip ID.

**Returns** `Integer` - The newly reserved clip ID.

**Examples**

Reserve a clip ID, then load audio into it:
```
` reserve a slot and load a sound effect clip
clipId = reserve sfx clip id(clipId)
load sfx clip clipId, "audio/laser"
```

**Remarks**

The "claim" half of the peek-vs-claim pattern. After reserving, load the audio datawith `load sfx clip`. See also`free sfx clip id` if you only need to peek.

---

### free sfx id

Peeks at the next available sound effect instance ID without claiming it.

This doesn't reserve the ID, so another call could grab it before you do.

**Parameters**

- `Integer` _(ref)_ **sfxId** - Receives the next free instance ID.

**Returns** `Integer` - The next available instance ID (not yet reserved).

**Examples**

Peek at the next instance ID:
```
` check what instance ID would be assigned next
nextSfxId = free sfx id(nextSfxId)
```

**Remarks**

Most of the time you'll want `reserve sfx id`instead, which actually claims the slot. If you already know your ID, skip both andcall `sfx` directly.

---

### reserve sfx id

Claims the next available sound effect instance ID and initializes its slot.

Use this when you need to wire up references before creating the actual instance.

**Parameters**

- `Integer` _(ref)_ **sfxId** - Receives the reserved instance ID.

**Returns** `Integer` - The newly reserved instance ID.

**Examples**

Reserve an instance ID, then create the instance from a loaded clip:
```
` reserve the instance slot first, then create it
mysfxId = reserve sfx id(mysfxId)
sfx mysfxId, clipId
```

**Remarks**

The "claim" half of the peek-vs-claim pattern. After reserving, create the instancewith `sfx`. See also`free sfx id` if you only need to peek.

---

### load sfx clip

Loads a sound effect clip from the content pipeline.

A clip is the raw audio data. Think of it as the sound file itself. Youneed to create an instance from it with `sfx`before you can actually play it.

**Parameters**

- `Integer` **clipId** - The clip ID to assign to the loaded sound.
- `String` **path** - Content path to the sound effect asset, relative to the Content directory.

**Examples**

Load a clip and create a playable instance from it:
```
` load the explosion sound clip
clipId = 1
load sfx clip clipId, "audio/explosion"
 ` create an instance so we can play it
sfxId = 1
sfx sfxId, clipId
play sfx sfxId
```

Load one clip and create multiple instances for overlapping playback:
```
` load the gunshot clip once
gunClip = 1
load sfx clip gunClip, "audio/gunshot"
 ` create three instances so up to three can overlap
sfx 1, gunClip
sfx 2, gunClip
sfx 3, gunClip
```

**Remarks**

Call this during setup. The content path is relative to the Content directory anddoesn't need a file extension. One clip can be used to create many instances, socreate one instance per concurrent playback you need. The typical audio setup is: load a clip here, create an instance with`sfx`, optionally configure pitch/pan/volume/loop,then call `play sfx` when you want to hear it.

---

### sfx

Creates a playable sound effect instance from a loaded clip.

You need a separate instance for each concurrent playback of the same sound.If you want to play the same explosion sound three times overlapping, you need threeinstances.

**Parameters**

- `Integer` **sfxId** - The instance ID to assign to the new sound effect.
- `Integer` **clipId** - The clip ID of a previously loaded sound (from `load sfx clip`).

**Examples**

Full audio setup from clip to playback:
```
` load the clip
clipId = 1
load sfx clip clipId, "audio/laser"
 ` create an instance and configure it
sfxId = 1
sfx sfxId, clipId
set sfx volume sfxId, 0.8
set sfx pitch sfxId, 0.2
 ` fire!
play sfx sfxId
```

Create multiple instances from one clip for overlapping sounds:
```
` one clip, three instances
clipId = 1
load sfx clip clipId, "audio/footstep"
 sfx 10, clipId
sfx 11, clipId
sfx 12, clipId
 ` randomize pitch slightly on each for variety
set sfx pitch 10, -0.1
set sfx pitch 11, 0.0
set sfx pitch 12, 0.1
```

**Remarks**

This is the second step in the audio setup pipeline: first you load a clip with`load sfx clip`, then you create one or moreinstances here. Each instance has its own pitch, pan, volume, and playback state. After creating an instance, configure it with`set sfx pitch`,`set sfx pan`,`set sfx volume`, and`set sfx loop`, then play it with`play sfx`.

---

### pause sfx

Pauses a playing sound effect.

The sound stops where it is and can be resumed from that point by calling`play sfx` again. Note that `play sfx` restartsfrom the beginning, so pausing is mainly useful for stopping a sound temporarily.

**Parameters**

- `Integer` **sfxId** - The instance ID of the sound effect to pause.

**Examples**

Pause a looping ambient sound when the game pauses:
```
` set up a looping wind sound
clipId = 1
load sfx clip clipId, "audio/wind"
windSfx = 1
sfx windSfx, clipId
set sfx loop windSfx, 1
play sfx windSfx
 ` later, when the game pauses
pause sfx windSfx
 ` to resume, call play sfx again (restarts from beginning)
play sfx windSfx
```

**Remarks**

A paused sound is different from a stopped one. `is sfx done`returns `0` for paused sounds (they're not "done", just on hold) but`1` for stopped sounds.

---

### play sfx

Plays a sound effect from the beginning.

If the sound is already playing, it stops and restarts from the top. There is noway to layer the same instance on top of itself. Create multiple instances if youneed overlapping playback of the same sound.

**Parameters**

- `Integer` **sfxId** - The instance ID of the sound effect to play.

**Examples**

Basic playback:
```
` load and create
clipId = 1
load sfx clip clipId, "audio/coin"
coinSfx = 1
sfx coinSfx, clipId
 ` play the sound
play sfx coinSfx
```

Wait for a sound to finish before playing the next one:
```
play sfx introSfx
DO
` wait each frame until the sound is done
LOOP UNTIL is sfx done(introSfx) = 1
play sfx mainThemeSfx
```

**Remarks**

This is the command that actually makes noise. You must have created the instancefirst with `sfx`. After calling this, you cancheck `is sfx done` to know when the sound has finished. For delayed playback, use `delay play sfx` instead.

---

### delay play sfx

Plays a sound effect after a delay in milliseconds.

The delay is measured from the moment you call this command, using theinternal audio clock.

**Parameters**

- `Integer` **sfxId** - The instance ID of the sound effect to play.
- `Integer` **delayMs** - Delay in milliseconds before playback starts.

**Examples**

Stagger three impact sounds for a more natural collision:
```
` play three impact sounds with slight offsets
delay play sfx impactSfx1, 0
delay play sfx impactSfx2, 50
delay play sfx impactSfx3, 120
```

Play a warning beep one second from now:
```
` schedule the beep for 1000 milliseconds in the future
delay play sfx warningSfx, 1000
```

**Remarks**

Use this to stagger sound effects for a more natural feel. For example, playingslightly offset impact sounds when multiple objects collide in the same frame. Thedelay runs on the audio system's own timer, not game frames, so it stays accurateregardless of frame rate. Like `play sfx`, this stops any current playback onthe instance before scheduling the delayed start.

---

### set sfx pitch

Sets the pitch of a sound effect instance.

Values outside the `-1` to `1` range are clamped automatically, soyou will not get an error, but the value will not go beyond the limits.

**Parameters**

- `Integer` **sfxId** - The instance ID of the sound effect.
- `Float` **pitch** - Pitch shift, from `-1` (one octave down) to `1` (one octave up). `0` is normal.

**Examples**

Randomize pitch each time you play a footstep:
```
` give each footstep a slightly different pitch
randomPitch = rnd(60) - 30
randomPitch = randomPitch / 100.0
set sfx pitch footstepSfx, randomPitch
play sfx footstepSfx
```

Pitch down an explosion for a heavy feel:
```
set sfx pitch explosionSfx, -0.5
play sfx explosionSfx
```

**Remarks**

Pitch shifts the playback speed and frequency of the sound. A value of `0` isnormal speed, `-1` is one octave down (slower, deeper), and `1` is oneoctave up (faster, higher). Fractional values like `0.5` work fine forsubtle shifts. You can call this before or after `play sfx` and ittakes effect immediately either way. This is handy for randomizing pitch slightlyeach time you play a sound so it doesn't feel repetitive (e.g., footsteps, gunshots). Read the current value back with `sfx pitch`.

---

### sfx pitch

Returns the current pitch of a sound effect instance.

**Parameters**

- `Integer` **sfxId** - The instance ID of the sound effect.

**Returns** `Float` - The current pitch value, from `-1` (one octave down) to `1` (one octave up).

**Examples**

Gradually raise the pitch of a rising siren each frame:
```
` read current pitch and nudge it upward
currentPitch = sfx pitch(sirenSfx)
currentPitch = currentPitch + 0.01
IF currentPitch > 1.0 THEN currentPitch = -1.0
set sfx pitch sirenSfx, currentPitch
```

**Remarks**

Use this to read back whatever was set with `set sfx pitch`.This is useful if you're adjusting pitch incrementally each frame. Grab the currentvalue, nudge it, and write it back. The returned value will always be in the`-1` to `1` range since `set sfx pitch` clampsits input.

---

### set sfx pan

Sets the stereo pan of a sound effect instance.

Values outside the `-1` to `1` range are clamped automatically.

**Parameters**

- `Integer` **sfxId** - The instance ID of the sound effect.
- `Float` **pan** - Stereo position, from `-1` (full left) to `1` (full right). `0` is centered.

**Examples**

Pan a sound based on an enemy's screen position:
```
` calculate pan from enemy X relative to screen center
screenW = screen width()
panValue = (enemyX - (screenW / 2)) / (screenW / 2)
set sfx pan enemySfx, panValue
```

Hard-pan a sound to the left speaker:
```
set sfx pan leftChannelSfx, -1.0
play sfx leftChannelSfx
```

**Remarks**

Pan controls where the sound sits in the stereo field. `-1` is full left,`0` is centered, and `1` is full right. Use fractional values forsubtle positioning. For example, `-0.3` places the sound slightly leftof center. You can call this before or after `play sfx` and ittakes effect immediately. A common pattern is to update pan each frame based onwhere the sound source is relative to the player, giving a simple positionalaudio effect without a full 3D audio system. Read the current value back with `sfx pan`.

---

### sfx pan

Returns the current stereo pan of a sound effect instance.

**Parameters**

- `Integer` **sfxId** - The instance ID of the sound effect.

**Returns** `Float` - The current pan value, from `-1` (full left) to `1` (full right).

**Examples**

Smoothly blend pan toward a target position each frame:
```
` lerp the pan toward the target by 10% each frame
currentPan = sfx pan(engineSfx)
currentPan = currentPan + (targetPan - currentPan) * 0.1
set sfx pan engineSfx, currentPan
```

**Remarks**

Use this to read back whatever was set with `set sfx pan`.Handy if you're blending pan toward a target over time. Grab the current value,interpolate toward where you want it, and write it back with`set sfx pan`. The returned value will always be in the`-1` to `1` range.

---

### set sfx volume

Sets the volume of a sound effect instance.

Values outside the `0` to `1` range are clamped automatically.

**Parameters**

- `Integer` **sfxId** - The instance ID of the sound effect.
- `Float` **volume** - Volume level, from `0` (silent) to `1` (full volume).

**Examples**

Fade out a sound over time each frame:
```
` reduce volume by a small amount each frame
vol = sfx volume(mySfx)
vol = vol - 0.02
IF vol < 0.0 THEN vol = 0.0
set sfx volume mySfx, vol
```

Set a quiet background ambience at half volume:
```
set sfx volume ambientSfx, 0.5
set sfx loop ambientSfx, 1
play sfx ambientSfx
```

**Remarks**

Volume goes from `0` (completely silent) to `1` (full volume). There is noway to boost above `1`. If you need a sound to feel louder, you will need toadjust the source audio asset itself. You can call this before or after `play sfx` and ittakes effect immediately. This makes it easy to fade sounds in and out by adjustingvolume a little each frame. Read the current value back with `sfx volume`.

---

### sfx volume

Returns the current volume of a sound effect instance.

**Parameters**

- `Integer` **sfxId** - The instance ID of the sound effect.

**Returns** `Float` - The current volume level, from `0` (silent) to `1` (full volume).

**Examples**

Fade in a sound from silence to full volume:
```
` increase volume toward 1.0 each frame
vol = sfx volume(mySfx)
IF vol < 1.0
vol = vol + 0.01
set sfx volume mySfx, vol
ENDIF
```

**Remarks**

Use this to read back whatever was set with `set sfx volume`.This is useful for fade-in and fade-out effects. Grab the current volume, adjust ittoward your target, and write it back with `set sfx volume`.The returned value will always be in the `0` to `1` range.

---

### set sfx loop

Sets whether a sound effect should loop continuously.

When looping is enabled, the sound restarts from the beginning each time itreaches the end, and `is sfx done` will neverreturn `1` while it's playing.

**Parameters**

- `Integer` **sfxId** - The instance ID of the sound effect.
- `Boolean` **isLooped** - Pass `1` to loop, `0` to play once.

**Examples**

Set up a looping background ambience:
```
` load and create the ambient loop
clipId = 1
load sfx clip clipId, "audio/forest_ambience"
ambSfx = 1
sfx ambSfx, clipId
 ` enable looping and play at half volume
set sfx loop ambSfx, 1
set sfx volume ambSfx, 0.5
play sfx ambSfx
```

Stop a looping sound gracefully by letting it finish its current pass:
```
` turn off looping so the sound plays to the end and stops
set sfx loop ambSfx, 0
```

**Remarks**

Set this before calling `play sfx` for the cleanestresults. You can also toggle it while a sound is already playing. Turning loop offmid-playback lets the sound finish its current pass and then stop naturally. Looping is great for ambient sounds, music loops, or engine hums, basically anything thatneeds to run indefinitely. When you're done with a looping sound, either call`pause sfx` to silence it or set loop back to `0`and let it finish on its own.

---

### is sfx done

Checks whether a sound effect has finished playing.

A paused sound is not considered "done". Only a sound that has fully stopped(either it played to the end or was never started) returns `1`.

**Parameters**

- `Integer` **sfxId** - The instance ID of the sound effect to check.

**Returns** `Boolean` - `1` if the sound effect has stopped, `0` if it's still playing or paused.

**Examples**

Wait for an intro jingle to finish, then start gameplay music:
```
play sfx jingleSfx
DO
` keep looping until the jingle finishes
LOOP UNTIL is sfx done(jingleSfx) = 1
 ` now start the looping gameplay music
set sfx loop musicSfx, 1
play sfx musicSfx
```

Trigger a visual effect when a sound finishes (called each frame):
```
IF is sfx done(chargeSfx) = 1
` the charge-up sound finished, fire the laser!
play sfx laserSfx
ENDIF
```

**Remarks**

This is how you know when a one-shot sound has finished. Poll it each frame if youneed to trigger something when the sound ends. For example, you could play a follow-upsound or remove a visual effect that was synced to the audio. For looping sounds (set via `set sfx loop`), this willalways return `0` while they're playing, since they never reach a natural end.A sound that was paused with `pause sfx` also returns`0` because it's on hold, not done.

---

### box collider

Creates an axis-aligned box collider at the given position and size.

The collider is static by default and will not move on its own. Attach itto a transform with `attach collider to transform`if you need it to follow a game object.

**Parameters**

- `Integer` **colliderId** - The ID to assign to this collider.
- `Integer` **x** - The X position of the collider's top-left corner.
- `Integer` **y** - The Y position of the collider's top-left corner.
- `Integer` **w** - The width of the collider in pixels.
- `Integer` **h** - The height of the collider in pixels.

**Examples**

Create a collider for a player character and attach it to a transform.
```
` set up the player entity
playerId = 1
transform playerId, 100, 200
box collider playerId, 0, 0, 32, 32
attach collider to transform playerId, playerId
```

Create a static wall collider that does not move.
```
` place a wall at the bottom of the screen
wallId = 99
box collider wallId, 0, 460, 640, 20
```

**Remarks**

Box colliders are the building blocks of Fade's collision system. You create them,optionally parent them to transforms, and then each frame you call`perform collider checks` to find out what's overlapping.After that, use `get collision` to query specific pairs. A typical setup for a game entity looks like this: create a transform with`transform`, create a sprite with[sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite) and attach it via`attach sprite to transform`, then createa collider here and attach it with`attach collider to transform`. Now movingthe transform moves everything together. Collider positions are relative to their attached transform (if any). If you setx=`0`, y=`0` and attach to a transform, the collider sits at thetransform's origin. Offset x and y to shift it relative to that anchor point. There's no limit on the number of colliders you can create, but keep in mind that`perform collider checks` is an O(n^2) broad-phase, sohundreds of active colliders will start to cost you.

---

### attach collider to transform

Attaches a collider to a transform so it follows the transform's position each frame.

Once attached, the collider's x and y become offsets relative to the transform rather than absolute screen positions.

**Parameters**

- `Integer` **colliderId** - The ID of the collider to attach.
- `Integer` **transformId** - The ID of the transform to follow.

**Examples**

Build a complete game entity with a transform, sprite, and collider.
```
` create the entity's transform
enemyId = 5
transform enemyId, 300, 100
 ` create and attach a sprite
sprite enemyId, 0, 0
attach sprite to transform enemyId, enemyId
 ` create and attach a collider
box collider enemyId, -16, -16, 32, 32
attach collider to transform enemyId, enemyId
 ` now moving the transform moves everything
set transform position enemyId, 400, 200
```

**Remarks**

This is how you make a collider stick to a moving game object. Without this, thecollider just sits wherever you placed it with`box collider`. The collision system reads thetransform's world position before doing its sweep each frame, so the colliderautomatically stays in sync. Pairs naturally with `attach sprite to transform`.The typical entity has a transform, a sprite attached to it, and a colliderattached to it. Move the transform and everything follows.

---

### perform collider checks

Runs the broad-phase collision sweep across all active colliders.

You must call this once per frame before using`get collision`, or you'll be reading stalehit data from the previous frame.

**Examples**

A typical game loop that moves objects, sweeps collisions, then checks for hits.
```
` set up a player and an enemy
playerId = 1
enemyId = 2
transform playerId, 100, 200
transform enemyId, 300, 200
box collider playerId, 0, 0, 32, 32
box collider enemyId, 0, 0, 32, 32
attach collider to transform playerId, playerId
attach collider to transform enemyId, enemyId
 set sync rate 16
DO
` move the player toward the enemy
px = get local transform x(playerId)
set transform position playerId, px + 1, 200
   ` sweep all colliders, then check for hits
perform collider checks
hit = get collision(playerId, enemyId)
IF hit = 1 THEN
print "collision detected!"
ENDIF
   sync
LOOP
```

**Remarks**

Collision detection in Fade works in two phases. First, you call this command tosweep all active colliders and build up the internal hit list. Then you queryspecific pairs with `get collision`. Thistwo-phase design means the expensive broad-phase only runs once per frame, nomatter how many pairs you check afterward. Call this once per frame in your `DO...LOOP`, after you've moved everythingbut before you check for hits. Calling it multiple times per frame is harmless butwasteful. Forgetting to call it means`get collision` will never see new overlaps.

---

### get collision

Checks whether two colliders are currently overlapping.

You must call `perform collider checks` earlier inthe frame for this to return up-to-date results. Without that, you're reading stalehit data from the previous frame.

**Parameters**

- `Integer` **aColliderId** - The ID of the first collider.
- `Integer` **bColliderId** - The ID of the second collider.

**Returns** `Boolean` - `1` if the two colliders are overlapping, `0` otherwise.

**Examples**

Check if a bullet hit any of three enemies.
```
` assume bullet and enemy colliders are already set up
perform collider checks
FOR e = 1 TO 3
hit = get collision(bulletId, e)
IF hit = 1 THEN
print "enemy hit!"
ENDIF
NEXT e
```

React to a player touching a pickup item.
```
` inside the game loop, after perform collider checks
hit = get collision(playerId, coinId)
IF hit = 1 THEN
score = score + 10
` move the coin off screen so it stops colliding
set transform position coinId, -100, -100
ENDIF
```

**Remarks**

This is the query side of Fade's two-phase collision system. After`perform collider checks` has done its sweep, call thisto ask about any specific pair of colliders. You can call it as many times as youwant per frame because the expensive work already happened in the sweep. The order of the two collider IDs does not matter. Checking (a, b) is the same aschecking (b, a). If either collider ID doesn't exist or hasn't been involved in any collision, thisreturns `0` rather than throwing an error.

---

### print

Prints one or more values to the console output.

Each value is printed on its own line, so passing three values gives you three lines of output.

**Parameters**

- `any` **values** - One or more values of any type to print. Each value becomes its own line.

**Examples**

Print a simple message and a variable:
```
` print a greeting and the player's score
score = 42
print "hello world"
print score
```

Timestamp debug output with `game ms`:
```
set sync rate 16
DO
t = game ms()
print t
sync
LOOP
```

**Remarks**

This is your go-to debug command. You can call it from macros or at runtime(it works in both contexts), which makes it handy for inspecting values duringcompilation as well as while the game is running. Since it writes to the console, you won't see anything if your game doesn't havea console window attached. It pairs naturally with`game ms` if you want to timestamp your debug output,and with `test` when you just need to dump a single int quickly.

---

### game ms

Returns the total elapsed game time in milliseconds.

This keeps ticking regardless of what your script is doing. It reflects wall-clock time since the game started, not script time.

**Returns** `DoubleFloat` - Total game time in milliseconds.

**Examples**

Use game time to move a sprite smoothly across the screen:
```
` move a sprite based on elapsed time
set sync rate 16
texture 1, "Images/Ship"
sprite 1, 0, 100, 1
DO
t = game ms()
x = t / 10
sprite 1, x, 100, 1
sync
LOOP
```

Build a simple countdown timer:
```
` count down from 5 seconds
set sync rate 16
startTime = game ms()
DO
elapsed = game ms() - startTime
remaining = 5000 - elapsed
IF remaining < 0 THEN remaining = 0
print remaining
sync
LOOP
```

**Remarks**

Call this every frame (after `sync`) when youneed to drive animations, timers, or custom tweens by real elapsed time instead offrame counts. Because it is millisecond-resolution, you can do smooth interpolationwithout worrying about frame-rate jitter. If you only need to know how many frames have passed, use`frame number` instead. And if you are building a tween thatuses angles, the trig helpers like [sin](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sin) and[cos](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Cos) pair well with a time value converted to radians.

---

### begin debug window

**Parameters**

- `String` **arg2**

---

### end debug window

**Parameters**


---

### debug same line

**Parameters**


---

### debug separator

**Parameters**


---

### begin debug tree

**Parameters**

- `String` **arg2**

**Returns** `Integer`

---

### end debug tree

**Parameters**


---

### begin debug tab bar

**Parameters**

- `String` **arg2**

**Returns** `Integer`

---

### end debug tab bar

**Parameters**


---

### begin debug tab

**Parameters**

- `String` **arg2**

**Returns** `Integer`

---

### end debug tab

**Parameters**


---

### debug label

**Parameters**

- `String` **arg2**
- `String` **arg3**

---

### debug text

**Parameters**

- `String` **arg2**

---

### debug button

**Parameters**

- `String` **arg2**

**Returns** `Integer`

---

### debug toggle

**Parameters**

- `String` **arg2**
- `Integer` _(ref)_ **arg3**

**Returns** `Integer`

---

### debug textbox

**Parameters**

- `String` **arg2**
- `String` _(ref)_ **arg3**
- `String` _(optional)_ **arg4**
- `Integer` _(optional)_ **arg5**

**Returns** `Integer`

---

### debug int slider

**Parameters**

- `String` **arg2**
- `Integer` _(ref)_ **arg3**
- `Integer` _(optional)_ **arg4**
- `Integer` _(optional)_ **arg5**

**Returns** `Integer`

---

### debug float slider

**Parameters**

- `String` **arg2**
- `Float` _(ref)_ **arg3**
- `Float` _(optional)_ **arg4**
- `Float` _(optional)_ **arg5**

**Returns** `Integer`

---

### debug drag int

**Parameters**

- `String` **arg2**
- `Integer` _(ref)_ **arg3**

**Returns** `Integer`

---

### debug drag float

**Parameters**

- `String` **arg2**
- `Float` _(ref)_ **arg3**

**Returns** `Integer`

---

### debug color picker

**Parameters**

- `String` **arg2**
- `Integer` _(ref)_ **arg3**

**Returns** `Integer`

---

### enable debug inspector

---

### disable debug inspector

---

### debug browse sprites

**Parameters**


---

### debug browse effects

**Parameters**


---

### debug browse transforms

**Parameters**


---

### debug browse tweens

**Parameters**


---

### debug browse colliders

**Parameters**


---

### debug browse texts

**Parameters**


---

### debug browse sfx

**Parameters**


---

### debug browse textures

**Parameters**


---

### debug browse render outputs

**Parameters**


---

### debug console

**Parameters**


---

### debug inspector

**Parameters**


---

### debug metadata

**Parameters**


---

### debug sprite

**Parameters**

- `Integer` **arg2**

**Returns** `Integer`

---

### debug effect

**Parameters**

- `Integer` **arg2**

**Returns** `Integer`

---

### debug transform

**Parameters**

- `Integer` **arg2**

**Returns** `Integer`

---

### debug tween

**Parameters**

- `Integer` **arg2**

**Returns** `Integer`

---

### debug collider

**Parameters**

- `Integer` **arg2**

**Returns** `Integer`

---

### debug text sprite

**Parameters**

- `Integer` **arg2**

**Returns** `Integer`

---

### debug sfx

**Parameters**

- `Integer` **arg2**

**Returns** `Integer`

---

### debug texture

**Parameters**

- `Integer` **arg2**

**Returns** `Integer`

---

### debug render output

**Parameters**

- `Integer` **arg2**

**Returns** `Integer`

---

### mouse x

Returns the mouse X position in render-buffer coordinates.

This accounts for any offset or scaling between the OS window and the actualrender area, so you always get coordinates that match your game's internal resolution.

**Returns** `Integer` - The mouse X position in render-space pixels.

**Examples**

Track the mouse and position a cursor sprite on it each frame:
```
` load a cursor texture and create a sprite for it
texture 1, "Images/Cursor"
sprite 1, 0, 0, 1
 DO
mx = mouse x()
my = mouse y()
sprite 1, mx, my, 1
sync
LOOP
```

**Remarks**

If your window size and render size differ (e.g., a 320x240 render buffer in an800x600 window), the mouse position is automatically mapped into render space. Thismeans you can compare the result directly against sprite positions without doing anymath yourself. Read this every frame after `sync` to get freshinput. Pairs with `mouse y` to get the full cursor position.

---

### mouse y

Returns the mouse Y position in render-buffer coordinates.

This accounts for any offset or scaling between the OS window and the actualrender area, so you always get coordinates that match your game's internal resolution.

**Returns** `Integer` - The mouse Y position in render-space pixels.

**Examples**

Check if the mouse is inside a rectangular region:
```
` define a button area
btnX = 100
btnY = 200
btnW = 120
btnH = 40
 DO
mx = mouse x()
my = mouse y()
   ` check if mouse is inside the button
IF mx >= btnX AND mx <= btnX + btnW
IF my >= btnY AND my <= btnY + btnH
text 10, 10, "Hovering over button!"
ENDIF
ENDIF
   sync
LOOP
```

**Remarks**

If your window size and render size differ, the mouse position is automaticallymapped into render space. This means you can compare the result directly againstsprite positions without doing any math yourself. Read this every frame after `sync` to get freshinput. Pairs with `mouse x` to get the full cursor position.

---

### left click

Returns `1` while the left mouse button is held down.

This fires every frame the button is pressed, not just the first one. Use`new left click` if you only want to detect theinitial press.

**Returns** `Boolean` - `1` while the left button is pressed, `0` otherwise.

**Examples**

Draw a trail of dots while the player holds the left mouse button:
```
DO
IF left click() = 1
mx = mouse x()
my = mouse y()
dot mx, my
ENDIF
sync
LOOP
```

Hold the left button to charge a power meter:
```
power = 0
maxPower = 100
 DO
IF left click() = 1
IF power < maxPower
power = power + 1
ENDIF
ELSE
power = 0
ENDIF
   text 10, 10, "Power: " + str$(power)
sync
LOOP
```

**Remarks**

Good for continuous actions like dragging, holding to charge, or painting. If youneed a one-shot click (e.g., pressing a button in a menu), use`new left click` instead, because otherwise theaction will fire every frame the player holds the button.

---

### new left click

Returns `1` only on the first frame the left mouse button is pressed.

After that first frame it returns `0`, even if the player keepsholding the button. The player must release and press again to trigger it.

**Returns** `Boolean` - `1` on the frame the left button transitioned from released to pressed.

**Examples**

Click a button to start the game:
```
btnX = 100
btnY = 200
btnW = 120
btnH = 40
started = 0
 DO
mx = mouse x()
my = mouse y()
   IF started = 0
text btnX + 10, btnY + 10, "Start Game"
     ` only fires once per click, so we won't skip frames
IF new left click() = 1
IF mx >= btnX AND mx <= btnX + btnW
IF my >= btnY AND my <= btnY + btnH
started = 1
ENDIF
ENDIF
ENDIF
ELSE
text 10, 10, "Game is running!"
ENDIF
   sync
LOOP
```

**Remarks**

This is edge detection: it fires once per press, not continuously. Use this fordiscrete actions like clicking a menu button, selecting a tile, or firing a singleshot. If you need to detect a held button (e.g., dragging), use`left click` instead.

---

### right click

Returns `1` while the right mouse button is held down.

This fires every frame the button is pressed. There is currently no`new right click` command, so use`new key down` with the right mouse scan code ifyou need edge detection for the right button.

**Returns** `Boolean` - `1` while the right button is pressed, `0` otherwise.

**Examples**

Use right click to place a waypoint at the mouse position:
```
wpX = 0
wpY = 0
hasWaypoint = 0
 DO
IF right click() = 1
wpX = mouse x()
wpY = mouse y()
hasWaypoint = 1
ENDIF
   IF hasWaypoint = 1
text wpX, wpY, "X"
ENDIF
   sync
LOOP
```

**Remarks**

Works the same as `left click` but for the right button.Good for secondary actions like context menus, alternate fire, or camera controls.

---

### upkey

Returns `1` if the up arrow key is currently held down, `0` otherwise.

This is a convenience wrapper. For a more general approach, use`key down` with[scanCode](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/ScanCode) to check any key.

**Returns** `Integer` - `1` if the up arrow is pressed, `0` otherwise.

**Examples**

Move a sprite up and down with the arrow keys:
```
` load a player texture and create a sprite for it
texture 1, "Images/Player"
sprite 1, 160, 120, 1
px = 160
py = 120
speed = 3
 DO
` subtract upkey to move up, add downkey to move down
py = py - upkey() * speed
py = py + downkey() * speed
   sprite 1, px, py, 1
sync
LOOP
```

**Remarks**

You can use the result directly in arithmetic (e.g., multiply it by a speed value).The "new" variant `new upkey` fires only on the first frame.

---

### downkey

Returns `1` if the down arrow key is currently held down, `0` otherwise.

This is a convenience wrapper. For a more general approach, use`key down` with[scanCode](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/ScanCode) to check any key.

**Returns** `Integer` - `1` if the down arrow is pressed, `0` otherwise.

**Examples**

Scroll a camera offset down while the key is held:
```
camY = 0
scrollSpeed = 2
 DO
camY = camY + downkey() * scrollSpeed
camY = camY - upkey() * scrollSpeed
   text 10, 10, "Camera Y: " + str$(camY)
sync
LOOP
```

**Remarks**

Pairs with [upkey](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/upKey)for vertical movement. The "new" variant `new downkey`fires only on the first frame.

---

### rightKey

Returns `1` if the right arrow key is currently held down, `0` otherwise.

This is a convenience wrapper. For a more general approach, use`key down` with[scanCode](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/ScanCode) to check any key.

**Returns** `Integer` - `1` if the right arrow is pressed, `0` otherwise.

**Examples**

Move a character left and right with arrow keys:
```
px = 160
speed = 4
 DO
px = px + rightKey() * speed
px = px - leftKey() * speed
   text px, 120, "@"
sync
LOOP
```

**Remarks**

Pairs with [leftKey](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/leftKey)for horizontal movement. The "new" variant `new rightKey`fires only on the first frame.

---

### leftKey

Returns `1` if the left arrow key is currently held down, `0` otherwise.

This is a convenience wrapper. For a more general approach, use`key down` with[scanCode](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/ScanCode) to check any key.

**Returns** `Integer` - `1` if the left arrow is pressed, `0` otherwise.

**Examples**

Full four-direction movement using all arrow keys:
```
` load a player texture and create a sprite for it
texture 1, "Images/Player"
sprite 1, 160, 120, 1
px = 160
py = 120
speed = 3
 DO
px = px + rightKey() * speed
px = px - leftKey() * speed
py = py + downkey() * speed
py = py - upkey() * speed
   sprite 1, px, py, 1
sync
LOOP
```

**Remarks**

Pairs with [rightKey](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/rightKey)for horizontal movement. The "new" variant `new leftKey`fires only on the first frame.

---

### spaceKey

Returns `1` if the space bar is currently held down, `0` otherwise.

This is a convenience wrapper. For a more general approach, use`key down` with[scanCode](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/ScanCode) to check any key.

**Returns** `Integer` - `1` if space is pressed, `0` otherwise.

**Examples**

Hold space to boost speed:
```
px = 0
baseSpeed = 2
boostSpeed = 6
 DO
` pick speed based on whether space is held
IF spaceKey() = 1
speed = boostSpeed
ELSE
speed = baseSpeed
ENDIF
   px = px + rightKey() * speed
px = px - leftKey() * speed
   text px, 120, ">"
sync
LOOP
```

**Remarks**

The "new" variant`new spaceKey` fires only on the first frame.

---

### new upkey

Returns `1` only on the first frame the up arrow is pressed.

After that first frame it returns `0`, even if the key is still held.The player must release and press again to trigger it.

**Returns** `Boolean` - `1` on the frame the up arrow transitioned from released to pressed.

**Examples**

Navigate a menu with up and down arrow keys (one step per press):
```
menuIndex = 0
menuCount = 3
 DO
` move selection up
IF new upkey() = 1
menuIndex = menuIndex - 1
IF menuIndex < 0
menuIndex = menuCount - 1
ENDIF
ENDIF
   ` move selection down
IF new downkey() = 1
menuIndex = menuIndex + 1
IF menuIndex >= menuCount
menuIndex = 0
ENDIF
ENDIF
   ` draw menu items
FOR i = 0 TO menuCount - 1
IF i = menuIndex
text 20, 40 + i * 20, "> Option " + str$(i)
ELSE
text 20, 40 + i * 20, "  Option " + str$(i)
ENDIF
NEXT i
   sync
LOOP
```

**Remarks**

Edge detection variant of [upkey](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/upKey). Use this for discreteactions like menu navigation where you want one step per press, not continuousscrolling. For the general-purpose version, use`new key down` with a scan code.

---

### new downkey

Returns `1` only on the first frame the down arrow is pressed.

After that first frame it returns `0`, even if the key is still held.

**Returns** `Boolean` - `1` on the frame the down arrow transitioned from released to pressed.

**Examples**

Step through a list of items one at a time:
```
selected = 0
total = 5
 DO
IF new downkey() = 1
IF selected < total - 1
selected = selected + 1
ENDIF
ENDIF
   text 10, 10, "Selected: " + str$(selected) + " of " + str$(total)
sync
LOOP
```

**Remarks**

Edge detection variant of [downkey](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/downKey). Pairs with`new upkey` for menu navigation. For the general-purposeversion, use `new key down` with a scan code.

---

### new rightKey

Returns `1` only on the first frame the right arrow is pressed.

After that first frame it returns `0`, even if the key is still held.

**Returns** `Boolean` - `1` on the frame the right arrow transitioned from released to pressed.

**Examples**

Cycle through tabs with left and right arrows:
```
tab = 0
tabCount = 4
 DO
IF new rightKey() = 1
tab = tab + 1
IF tab >= tabCount
tab = 0
ENDIF
ENDIF
   IF new leftKey() = 1
tab = tab - 1
IF tab < 0
tab = tabCount - 1
ENDIF
ENDIF
   text 10, 10, "Tab: " + str$(tab)
sync
LOOP
```

**Remarks**

Edge detection variant of [rightKey](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/rightKey). Pairs with`new leftKey` for horizontal menu navigation. For thegeneral-purpose version, use `new key down` with ascan code.

---

### new leftKey

Returns `1` only on the first frame the left arrow is pressed.

After that first frame it returns `0`, even if the key is still held.

**Returns** `Boolean` - `1` on the frame the left arrow transitioned from released to pressed.

**Examples**

Go back one page in a book viewer:
```
page = 0
maxPage = 10
 DO
IF new leftKey() = 1
IF page > 0
page = page - 1
ENDIF
ENDIF
   IF new rightKey() = 1
IF page < maxPage
page = page + 1
ENDIF
ENDIF
   text 10, 10, "Page " + str$(page) + " of " + str$(maxPage)
sync
LOOP
```

**Remarks**

Edge detection variant of [leftKey](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/leftKey). Pairs with`new rightKey` for horizontal menu navigation. For thegeneral-purpose version, use `new key down` with ascan code.

---

### new spaceKey

Returns `1` only on the first frame the space bar is pressed.

After that first frame it returns `0`, even if the key is still held.

**Returns** `Boolean` - `1` on the frame the space bar transitioned from released to pressed.

**Examples**

Press space to jump (one jump per press):
```
py = 200
vy = 0
gravity = 1
ground = 200
 DO
` start a jump only on the first frame space is pressed
IF new spaceKey() = 1
IF py >= ground
vy = -12
ENDIF
ENDIF
   ` apply gravity
vy = vy + gravity
py = py + vy
   ` land on the ground
IF py > ground
py = ground
vy = 0
ENDIF
   text 160, py, "O"
sync
LOOP
```

**Remarks**

Edge detection variant of [spaceKey](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/spaceKey). Use this for actionslike jumping or confirming a selection where you want one action per press. For thegeneral-purpose version, use `new key down` with ascan code.

---

### new key down

Returns `1` only on the first frame a key is pressed.

This is the general-purpose edge detection command. It works with any keyvia its scan code. The convenience wrappers like `new upkey`call this under the hood.

**Parameters**

- `Integer` **scanCode** - The scan code of the key. Use [scanCode](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/ScanCode) to convert a name like `"Space"` to its code.

**Returns** `Boolean` - `1` on the frame the key transitioned from released to pressed.

**Examples**

Press E to interact with something:
```
` get the scan code for E once at startup
eKey = scanCode("E")
 DO
IF new key down(eKey) = 1
text 10, 10, "Interacted!"
ENDIF
sync
LOOP
```

Press Escape to toggle a pause menu:
```
escKey = scanCode("Escape")
paused = 0
 DO
IF new key down(escKey) = 1
IF paused = 0
paused = 1
ELSE
paused = 0
ENDIF
ENDIF
   IF paused = 1
text 100, 100, "PAUSED"
ELSE
text 100, 100, "Playing..."
ENDIF
   sync
LOOP
```

**Remarks**

Use this when you need to detect a fresh press for a key that doesn't have its ownconvenience command. Get the scan code with [scanCode](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/ScanCode),for example, `scanCode("A")` gives you the code for the A key. This detects the transition from released to pressed. Once the key is held, itreturns `0` on subsequent frames. The player has to release and press againto trigger it. For continuous held-key detection, use`key down` instead.

---

### key down

Returns `1` while a key is held down.

This fires every frame the key is pressed, not just the first one. Use`new key down` if you only want the initial press.

**Parameters**

- `Integer` **scanCode** - The scan code of the key. Use [scanCode](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/ScanCode) to convert a name to its code.

**Returns** `Boolean` - `1` while the key is pressed, `0` otherwise.

**Examples**

WASD movement using scan codes:
```
` look up scan codes once at startup
wKey = scanCode("W")
aKey = scanCode("A")
sKey = scanCode("S")
dKey = scanCode("D")
 px = 160
py = 120
speed = 3
 DO
py = py - key down(wKey) * speed
py = py + key down(sKey) * speed
px = px - key down(aKey) * speed
px = px + key down(dKey) * speed
   text px, py, "@"
sync
LOOP
```

Hold shift to sprint:
```
shiftKey = scanCode("LeftShift")
px = 0
 DO
IF key down(shiftKey) = 1
speed = 6
ELSE
speed = 2
ENDIF
   px = px + rightKey() * speed
px = px - leftKey() * speed
   text px, 120, ">"
sync
LOOP
```

**Remarks**

This is the general-purpose held-key detection command. It works with any key viaits scan code. Get the code with [scanCode](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/ScanCode), for example,`scanCode("LeftShift")` for the left shift key. Good for continuous actions like movement, sprinting, or camera control where youwant the action to keep going as long as the key is held. The convenience wrapperslike [upkey](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/upKey) do the same thing but are limited to specific keys.

---

### scanCode

Converts a key name string to its integer scan code.

Pass the result to `key down` or`new key down` to check that key's state.

**Parameters**

- `String` **key** - The name of the key. Must match a MonoGame `Keys` value (e.g., `"A"`, `"Space"`, `"LeftShift"`).

**Returns** `Integer` - The integer scan code for the given key.

**Examples**

Store scan codes at startup and use them in the game loop:
```
` resolve scan codes once
jumpKey = scanCode("Space")
shootKey = scanCode("Z")
pauseKey = scanCode("Escape")
 DO
IF new key down(jumpKey) = 1
text 10, 10, "Jump!"
ENDIF
   IF key down(shootKey) = 1
text 10, 30, "Shooting..."
ENDIF
   IF new key down(pauseKey) = 1
text 10, 50, "Paused"
ENDIF
   sync
LOOP
```

Check number keys to select inventory slots:
```
` D1 through D9 are the number row keys
FOR i = 1 TO 9
slotKey(i) = scanCode("D" + str$(i))
NEXT i
 slot = 1
 DO
FOR i = 1 TO 9
IF new key down(slotKey(i)) = 1
slot = i
ENDIF
NEXT i
   text 10, 10, "Active slot: " + str$(slot)
sync
LOOP
```

**Remarks**

The key name must match one of the MonoGame `Keys` enum values. Commonexamples: `"A"` through `"Z"`, `"D0"` through `"D9"` fornumber keys, `"Space"`, `"Enter"`, `"LeftShift"`, `"Escape"`,`"Tab"`. You typically call this once during setup and store the result in a variable, ratherthan converting the string every frame. The scan code does not change at runtime.

---

### sin

Returns the sine of the given angle.

The angle must be in radians. Use [rad](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Rad) to convert from degrees first if needed.

**Parameters**

- `Float` **x** - The angle in radians.

**Returns** `Float` - The sine of the angle, in the range `-1.0` to `1.0`.

**Examples**

Move a sprite up and down in a wave pattern using [sin](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sin).
```
` bob a sprite up and down over time
t = 0
baseY = 200
DO
t = t + 0.05
y = baseY + sin(t) * 30
draw_sprite 1, 100, y
LOOP
```

Move in a circle using both [sin](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sin) and [cos](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Cos).
```
` orbit a point around a center
angle = 0
cx = 320
cy = 240
radius = 80
DO
angle = angle + 0.02
x = cx + cos(angle) * radius
y = cy + sin(angle) * radius
draw_sprite 1, x, y
LOOP
```

**Remarks**

Standard trig helper. You'll use this alongside [cos](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Cos) forcircular motion, wave effects, and oscillation. If you have an angle from[atan2](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Atan2), you can feed it straight in here since it'salready in radians. Passing values outside 0..2*pi is fine. It wraps naturally.

---

### cos

Returns the cosine of the given angle.

The angle must be in radians. Use [rad](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Rad) to convert from degrees first if needed.

**Parameters**

- `Float` **x** - The angle in radians.

**Returns** `Float` - The cosine of the angle, in the range `-1.0` to `1.0`.

**Examples**

Place 8 items evenly around a circle.
```
` arrange 8 sprites in a ring
cx = 320
cy = 240
radius = 100
count = 8
FOR i = 0 TO count - 1
angle = rad(360 / count * i)
x = cx + cos(angle) * radius
y = cy + sin(angle) * radius
draw_sprite i + 1, x, y
NEXT i
```

Scale movement speed by facing direction.
```
` move forward in the direction the player is facing
facing = rad(45)
speed = 3
px = px + cos(facing) * speed
py = py + sin(facing) * speed
```

**Remarks**

Pairs with [sin](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sin) for circular motion and positioning.A common pattern is `x = cos(angle) * radius` and `y = sin(angle) * radius`to place things on a circle. Like all the trig functions here, values outside 0..2*pi wrap naturally.

---

### atan2

Returns the angle (in radians) whose tangent is /.

Unlike [atan](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Atan), this takes both components so it returns the correct quadrant every time.

**Parameters**

- `Float` **y** - The y component of the direction vector.
- `Float` **x** - The x component of the direction vector.

**Returns** `Float` - The angle in radians, in the range `-pi` to `pi`.

**Examples**

Point a turret sprite toward the mouse cursor.
```
` calculate angle from turret to mouse
dx = mouseX - turretX
dy = mouseY - turretY
angle = atan2(dy, dx)
rotate_sprite 1, deg(angle)
```

Move an enemy toward the player at a fixed speed.
```
` chase the player
dx = playerX - enemyX
dy = playerY - enemyY
angle = atan2(dy, dx)
speed = 2
enemyX = enemyX + cos(angle) * speed
enemyY = enemyY + sin(angle) * speed
```

**Remarks**

This is the one you want for finding the angle between two points. Given adirection vector (dx, dy), `atan2(dy, dx)` gives you the angle you canfeed into [sin](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sin) and [cos](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Cos) to movealong that direction. The result is in radians. If you need degrees for display, pipe it through[deg](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Deg). Passing `(0, 0)` returns `0`.

---

### atan

Returns the arctangent of the given value, in radians.

For finding angles between two points, you almost certainly want [atan2](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Atan2) instead. It handles quadrants for you.

**Parameters**

- `Float` **x** - The tangent value to find the angle for.

**Returns** `Float` - The angle in radians, in the range `-pi/2` to `pi/2`.

**Examples**

Find the angle of a slope from rise over run.
```
` calculate the angle of a ramp
rise = 3
run = 4
slope = rise / run
angle = atan(slope)
angleDeg = deg(angle)
` angleDeg is about 36.87
```

**Remarks**

Plain atan only takes one argument and can't distinguish which quadrant theangle falls in. It's here for completeness, but [atan2](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Atan2)is what you'll reach for in practice. The result is in radians; convert with[deg](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Deg) if you need degrees.

---

### sqrt

Returns the square root of the given value.

Passing a negative value returns `NaN`.

**Parameters**

- `Float` **x** - A non-negative value to take the square root of.

**Returns** `Float` - The square root of . Returns `NaN` if  is negative.

**Examples**

Check if two sprites are within range of each other.
```
` calculate distance between player and enemy
dx = playerX - enemyX
dy = playerY - enemyY
dist = sqrt(dx * dx + dy * dy)
IF dist < 50
` enemy is close enough to attack
take_damage 10
ENDIF
```

Normalize a direction vector to unit length.
```
` turn a direction into a unit vector
dx = targetX - startX
dy = targetY - startY
length = sqrt(dx * dx + dy * dy)
IF length > 0
nx = dx / length
ny = dy / length
ENDIF
```

**Remarks**

Most commonly used for distance calculations. If you have dx and dy betweentwo points, `sqrt(dx*dx + dy*dy)` gives you the distance. If you onlyneed to compare distances (e.g., "is this closer than that?"), you can skip thesqrt and compare the squared values directly, which is a bit faster. Pairs well with [atan2](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Atan2) when you need both the distanceand the angle to a target.

---

### deg

Converts an angle from radians to degrees.

All trig functions ([sin](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sin), [cos](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Cos), [atan2](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Atan2), etc.) work in radians, so use this when you need degrees for display or human-friendly output.

**Parameters**

- `Float` **radians** - The angle in radians to convert.

**Returns** `Float` - The equivalent angle in degrees.

**Examples**

Display the angle to a target in degrees.
```
` show the player what direction the objective is
dx = objectiveX - playerX
dy = objectiveY - playerY
angleRad = atan2(dy, dx)
angleDeg = deg(angleRad)
` angleDeg is now in 0..360 range for display
```

Convert an [atan2](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Atan2) result to rotate a sprite.
```
` rotate arrow sprite toward the mouse
dx = mouseX - arrowX
dy = mouseY - arrowY
angle = deg(atan2(dy, dx))
rotate_sprite 1, angle
```

**Remarks**

The inverse of [rad](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Rad). A full circle is `360` degreesor roughly `6.283` radians. If you are doing all your math in radians(recommended), you may only need this for debug printing or UI display.

---

### rad

Converts an angle from degrees to radians.

Use this to feed degree values into trig functions like [sin](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sin) and [cos](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Cos), which expect radians.

**Parameters**

- `Float` **degrees** - The angle in degrees to convert.

**Returns** `Float` - The equivalent angle in radians.

**Examples**

Fire a bullet at a 45-degree angle.
```
` launch a projectile at 45 degrees
angleDeg = 45
angleRad = rad(angleDeg)
speed = 10
velX = cos(angleRad) * speed
velY = sin(angleRad) * speed
```

Rotate something by a fixed number of degrees each frame.
```
` spin a sprite 2 degrees per frame
angleDeg = 0
DO
angleDeg = angleDeg + 2
x = 320 + cos(rad(angleDeg)) * 100
y = 240 + sin(rad(angleDeg)) * 100
draw_sprite 1, x, y
LOOP
```

**Remarks**

The inverse of [deg](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Deg). If you're working with angles thatcome from user input or config files in degrees, run them through this beforepassing to any trig function. A common pattern:`x = cos(rad(angleDeg)) * radius`.

---

### screenshot

Takes a screenshot and saves it as a PNG file.

If the file path you pass doesn't end in `.png`, the extension getsappended automatically, so you don't need to worry about it.

**Parameters**

- `String` **filePath** - The path to save the screenshot to. The `.png` extension is added if missing.

**Examples**

Save a screenshot when the player presses a key:
```
DO
` press S to take a screenshot
IF scancode("S") = 1
screenshot "my_screenshot"
ENDIF
sync
LOOP
```

**Remarks**

This captures whatever is currently in the main render buffer, so call itafter `sync` if you want the finalcomposited frame. Calling it mid-frame will grab a partially drawn buffer,which is usually not what you want. The file is written synchronously, so there may be a tiny hitch on theframe you call it. For most use cases (debug screenshots, photo modes) thisis fine.

---

### set render size

Sets the size of the main render buffer in pixels.

This controls the internal resolution that everything gets drawn at, whichmay differ from the window size. The final image is scaled to fit the window.

**Parameters**

- `Integer` **width** - Width of the render buffer in pixels.
- `Integer` **height** - Height of the render buffer in pixels.

**Examples**

Set up a pixel-art resolution at startup:
```
` configure a small render buffer for pixel art
set render size 320, 180
 ` verify the size was applied
w = render width()
h = render height()
```

Set up a standard HD resolution:
```
set render size 1280, 720
```

**Remarks**

Call this once during setup to define your game's native resolution. Forexample, if you're making a pixel-art game, you might set this to somethingsmall like `320` by `180`. The engine will scale it up to thewindow size, keeping that crispy pixel look. Changing this mid-game is possible but will recreate the render buffer, soit's best done at startup or during a scene transition. You can read thecurrent size back with `render width` and`render height`.

---

### render width

Returns the width of the main render buffer in pixels.

This reflects whatever was last set with`set render size`.

**Returns** `Integer` - The width of the main render buffer in pixels.

**Examples**

Center a sprite horizontally on screen:
```
` place a sprite in the middle of the screen
texture 1, "Images/Logo"
cx = render width() / 2
cy = render height() / 2
sprite 1, cx, cy, 1
```

**Remarks**

Handy when you need to position things relative to the screen edges. Forinstance, centering a sprite horizontally by placing it at`render width` / `2`. Pair with`render height` for full coverage.

---

### render height

Returns the height of the main render buffer in pixels.

This reflects whatever was last set with`set render size`.

**Returns** `Integer` - The height of the main render buffer in pixels.

**Examples**

Place a HUD bar along the bottom of the screen:
```
` draw a health bar at the bottom
barY = render height() - 20
barW = render width()
` use barY and barW to position your HUD sprite
sprite 1, 0, barY, hudImg
```

**Remarks**

Use this alongside `render width` when youneed to know the full dimensions of the render area. For example, toplace HUD elements along the bottom edge, or to calculate aspect ratios.

---

### set background color

Sets the background clear color for the main render buffer.

Every frame, the buffer is filled with this color before anything isdrawn on top of it.

**Parameters**

- `Integer` **colorCode** - A packed RGBA color value. Use [rgb](/command/FadeBasic.Lib.Standard.StandardCommands/Rgb) to build one.

**Examples**

Set a dark blue background at startup:
```
` deep blue sky color
set background color rgb(20, 20, 80)
```

Cycle the background color over time for a day/night effect:
```
t = 0
DO
t = t + 0.01
r = 40 + sin(t) * 40
g = 40 + sin(t) * 20
b = 80 + sin(t) * 60
set background color rgb(r, g, b)
sync
LOOP
```

**Remarks**

This is the color you see wherever nothing else is being drawn. Think ofit as the "sky" or "void" behind your game. Set it once at startup orchange it dynamically for effects like day/night cycles. If you're using render targets, each target can have its own backgroundcolor via `set render target background color`.This command only affects the main buffer.

---

### free effect id

Returns the next available effect ID without reserving it.

Calling this multiple times in a row returns the same ID. It doesn'tadvance until something actually reserves or uses that slot.

**Parameters**

- `Integer` _(ref)_ **effectId** - Receives the next available effect ID.

**Returns** `Integer` - The next available effect ID.

**Examples**

Peek at the next effect ID before deciding to allocate:
```
` check what the next ID would be
nextId = free effect id()
```

**Remarks**

Use this when you want to peek at which ID would be assigned next withoutcommitting to it. If you just need an ID to pass straight into`effect`, use`reserve effect id` instead, which bothgrabs the ID and sets up the internal slot in one call. The typical flow is: call `reserve effect id`,then `effect` with the returned ID. You onlyneed `free effect id` if you're doingsomething more advanced, like checking IDs before deciding whether to allocate.

---

### reserve effect id

Reserves the next available effect ID and initializes its internal slot.

After calling this, the ID is yours. Nothing else will hand it out, andyou can safely pass it to `effect`.

**Parameters**

- `Integer` _(ref)_ **effectId** - Receives the reserved effect ID.

**Returns** `Integer` - The reserved effect ID.

**Examples**

Reserve an effect ID and load a shader:
```
` grab an effect ID and load a bloom shader
fxId = reserve effect id()
effect fxId, "bloom"
```

**Remarks**

This is the recommended way to get a new effect ID. It calls`free effect id` internally and thenmakes sure the slot is ready to go. A typical setup sequence looks like: call`reserve effect id` to get your ID,then `effect` to load the shader, then usethe various `set effect param` commands to configure it.

---

### effect

Loads a shader effect from the content pipeline.

The effect is also watched for file changes, so if you modify theshader on disk, it hot-reloads automatically without restarting.

**Parameters**

- `Integer` **effectId** - The ID to assign to this effect. Use `reserve effect id` to get one.
- `String` **effectName** - The content pipeline asset name of the shader to load.

**Examples**

Load a shader and apply it as a full-screen effect:
```
` set up a post-processing shader
fxId = reserve effect id()
effect fxId, "vignette"
set effect param float fxId, "Intensity", 0.5
set screen effect fxId
```

Load a shader and update parameters each frame:
```
fxId = reserve effect id()
effect fxId, "wave_distort"
set screen effect fxId
 DO
t = game ms() / 1000.0
set effect param float fxId, "Time", t
sync
LOOP
```

**Remarks**

Before calling this, you need an effect ID. Either grab one with`reserve effect id` or pick your ownnumber. The `effectName` is the content pipeline asset name (the samename you'd use in a content project, without the file extension). Once loaded, configure the effect's parameters with commands like`set effect param float`,`set effect param color`,`set effect param texture`, etc.Then apply it to the screen with `set screen effect`. The hot-reload watcher is great during development. Tweak your shaderin an external editor and see changes live without restarting the game.

---

### set screen shake amount

Sets how intense the screen shake effect is.

Higher values produce more dramatic shaking. Set to `0` to stopthe shake entirely.

**Parameters**

- `Float` **mag** - The shake intensity. `0` means no shake; larger values mean more movement.

**Examples**

Trigger a screen shake on an explosion:
```
` big explosion shake
set screen shake amount 15.0
set screen shake bounce 0.8
```

Stop the screen shake:
```
set screen shake amount 0
```

**Remarks**

Screen shake is a great way to add impact to explosions, hits, ordramatic events. The magnitude controls how far the screen can move fromits normal position during a shake. Pair this with `set screen shake bounce`to control how quickly the shake settles down. A high magnitude with lowbounce gives a single sharp jolt; high magnitude with high bounce gives asustained rumble. The shake is applied to the final rendered image, so it affects everythingon screen uniformly.

---

### set screen shake bounce

Sets how bouncy the screen shake feels.

This controls the elasticity, meaning how quickly the shake oscillates andsettles back to center.

**Parameters**

- `Float` **bounce** - The elasticity of the shake. Higher values produce faster, snappier oscillation.

**Examples**

Set up a sharp, punchy camera shake:
```
` quick jolt that settles fast
set screen shake amount 10.0
set screen shake bounce 0.5
```

Set up a sustained earthquake rumble:
```
` ongoing tremor with high elasticity
set screen shake amount 4.0
set screen shake bounce 2.0
```

**Remarks**

Think of this like a spring constant. A higher bounce value makes thescreen snap back and forth more aggressively, creating a jittery feel. Alower value gives a more sluggish, heavy shake. Use this alongside `set screen shake amount`to dial in the feel you want. For a quick camera punch, try a highmagnitude with moderate bounce. For a sustained earthquake effect, keepthe magnitude lower and the bounce higher.

---

### set effect param color

Sets a color parameter on a shader effect.

The color is passed as a packed RGBA value and sent to the namedparameter in the shader.

**Parameters**

- `Integer` **effectId** - The effect to modify. Must have been loaded with `effect`.
- `String` **parameterName** - The name of the shader parameter, exactly as declared in the shader.
- `Integer` **colorCode** - A packed RGBA color value. Use [rgb](/command/FadeBasic.Lib.Standard.StandardCommands/Rgb) to build one.

**Examples**

Pass a tint color to a shader:
```
fxId = reserve effect id()
effect fxId, "color_tint"
 ` set a warm orange tint
set effect param color fxId, "TintColor", rgb(255, 180, 80)
set screen effect fxId
```

**Remarks**

Use this to feed color data into your custom shaders. For example, atint color, an outline color, or a fog color. The `parameterName`must match the parameter name declared in the shader source exactly. If the parameter doesn't exist in the shader, this call is silentlyignored. No error is thrown, which makes it safe to call even if theshader has been hot-reloaded and the parameter was temporarily removed. Load the effect first with `effect`, then setits parameters with this and the other `set effect param` commands.

---

### set effect param float

Sets a single-number parameter on a shader effect.

The parameter name must match the shader source exactly.

**Parameters**

- `Integer` **effectId** - The effect to modify. Must have been loaded with `effect`.
- `String` **parameterName** - The name of the shader parameter, exactly as declared in the shader.
- `Float` **value** - The value to set.

**Examples**

Animate a shader parameter over time:
```
fxId = reserve effect id()
effect fxId, "dissolve"
set screen effect fxId
 DO
` pass elapsed time in seconds to the shader
t = game ms() / 1000.0
set effect param float fxId, "Time", t
set effect param float fxId, "Threshold", 0.5
sync
LOOP
```

**Remarks**

This is the most common way to feed data into shaders. Things like time,intensity, threshold values, or any single number your shader needs. Forexample, you might pass `game ms` divided by`1000` to get a seconds-based timer for animations. If the named parameter doesn't exist in the shader, the call is silentlyignored. This is handy during development when you're iterating on shadercode with hot-reload. Load the effect first with `effect`.

---

### set effect param float2

Sets a two-component parameter on a shader effect.

Use this for shader parameters that expect two values, like a screenresolution or a direction vector.

**Parameters**

- `Integer` **effectId** - The effect to modify. Must have been loaded with `effect`.
- `String` **parameterName** - The name of the shader parameter, exactly as declared in the shader.
- `Float` **x** - The first component.
- `Float` **y** - The second component.

**Examples**

Pass the render resolution to a post-processing shader:
```
fxId = reserve effect id()
effect fxId, "pixelate"
 ` tell the shader the screen dimensions
w = render width()
h = render height()
set effect param float2 fxId, "ScreenSize", w, h
set screen effect fxId
```

**Remarks**

Common uses include passing the render size (from`render width` and`render height`) to a post-processingshader, or sending a normalized direction for effects like directional blur. If the named parameter doesn't exist in the shader, the call is silentlyignored. Load the effect first with `effect`.

---

### set effect param float3

Sets a three-component parameter on a shader effect.

Use this for shader parameters that expect three values, like a positionin 3D space or an RGB color without alpha.

**Parameters**

- `Integer` **effectId** - The effect to modify. Must have been loaded with `effect`.
- `String` **parameterName** - The name of the shader parameter, exactly as declared in the shader.
- `Float` **x** - The first component.
- `Float` **y** - The second component.
- `Float` **z** - The third component.

**Examples**

Pass a light position to a shader:
```
fxId = reserve effect id()
effect fxId, "lighting"
 ` set the light at world position (100, 200, 50)
set effect param float3 fxId, "LightPos", 100.0, 200.0, 50.0
set screen effect fxId
```

Pass an RGB color without alpha as three separate floats:
```
` fog color in 0..1 range
set effect param float3 fxId, "FogColor", 0.6, 0.7, 0.9
```

**Remarks**

If your shader has a light position, a world-space coordinate, or a colorparameter that doesn't need alpha, this is the command for it. For colorsthat do include alpha, consider using`set effect param color` instead,which takes a packed RGBA value. If the named parameter doesn't exist in the shader, the call is silentlyignored. Load the effect first with `effect`.

---

### set effect param float4

Sets a four-component parameter on a shader effect.

Use this for shader parameters that expect four values, like arectangle, a quaternion, or a custom data pack.

**Parameters**

- `Integer` **effectId** - The effect to modify. Must have been loaded with `effect`.
- `String` **parameterName** - The name of the shader parameter, exactly as declared in the shader.
- `Float` **x** - The first component.
- `Float` **y** - The second component.
- `Float` **z** - The third component.
- `Float` **w** - The fourth component.

**Examples**

Pass a clipping rectangle to a shader:
```
fxId = reserve effect id()
effect fxId, "clip_rect"
 ` define a rectangle as (x, y, width, height)
set effect param float4 fxId, "ClipRect", 10.0, 20.0, 200.0, 150.0
set screen effect fxId
```

**Remarks**

This is the most flexible of the `set effect param` family. It canrepresent anything your shader needs as four numbers. If you're passing acolor, though, you'll probably find`set effect param color` moreconvenient since it takes a packed RGBA value directly. If the named parameter doesn't exist in the shader, the call is silentlyignored. Load the effect first with `effect`.

---

### set effect param texture

Sets a texture parameter on a shader effect.

The texture must already be loaded via`texture` or obtained from a`render target texture`.

**Parameters**

- `Integer` **effectId** - The effect to modify. Must have been loaded with `effect`.
- `String` **parameterName** - The name of the texture sampler in the shader.
- `Integer` **textureId** - The texture to assign. Must have been loaded with `texture` or obtained from a render target.

**Examples**

Feed a noise texture into a dissolve shader:
```
` load the noise texture
texture 1, "Images/Noise"
 ` set up the dissolve shader
fxId = reserve effect id()
effect fxId, "dissolve"
set effect param texture fxId, "NoiseTex", 1
set effect param float fxId, "Threshold", 0.3
set screen effect fxId
```

Use a render target's output as input to another shader:
```
` create a render target and grab its texture
rtId = reserve render target id()
render target rtId, 0
rtTex = render target texture(rtId)
 ` pass the render target texture into a blur shader
fxId = reserve effect id()
effect fxId, "blur"
set effect param texture fxId, "SceneTex", rtTex
set screen effect fxId
```

**Remarks**

This is how you feed images into your custom shaders. For example, anoise texture for dissolve effects, a lookup table for color grading, ora render target for multi-pass rendering. The `parameterName` must match the texture sampler name declared inthe shader source exactly. If the parameter doesn't exist, the call issilently ignored. A common pattern is to create a `render target`,draw some sprites to it with `set sprite render target`,then pass that target's texture into a post-processing shader with thiscommand. Load the effect first with `effect`.

---

### clear screen effect

Removes the screen-wide post-processing effect, returning to normal rendering.

After calling this, the main buffer is drawn directly to the screen withno shader applied.

**Examples**

Toggle a post-processing effect on and off with a key press:
```
fxId = reserve effect id()
effect fxId, "grayscale"
effectOn = 0
 DO
IF scancode("G") = 1
IF effectOn = 0
set screen effect fxId
effectOn = 1
ELSE
clear screen effect
effectOn = 0
ENDIF
ENDIF
sync
LOOP
```

**Remarks**

Use this to turn off an effect that was applied with`set screen effect`. This is useful fortoggling effects on and off. For example, removing a blur when a pausemenu closes, or clearing a color-grading pass during a cutscene. You can call this even if no screen effect is currently set; it's harmless.

---

### set screen effect

Applies a shader effect as a full-screen post-processing pass.

The effect is applied to the entire main render buffer every frame untilyou call `clear screen effect`.

**Parameters**

- `Integer` **effectId** - The effect to apply. Must have been loaded with `effect`.

**Examples**

Apply a CRT scanline effect to the whole screen:
```
` load and activate a CRT shader
fxId = reserve effect id()
effect fxId, "crt_scanlines"
set effect param float fxId, "ScanlineIntensity", 0.4
set screen effect fxId
```

**Remarks**

This is how you add screen-wide visual effects like bloom, vignette,color grading, or CRT scanlines. Load an effect with`effect`, configure its parameters with thevarious `set effect param` commands, then call this to activate it. Only one screen effect can be active at a time. Calling this again with adifferent effect ID replaces the previous one. To remove it entirely, call`clear screen effect`. The effect's shader receives the main render buffer as its input texture.Make sure your shader has a texture sampler set up to receive the screencontents.

---

### set render target background color

Sets the background clear color for a specific render target.

Each render target can have its own clear color, independent of themain buffer's `set background color`.

**Parameters**

- `Integer` **outputId** - The render target ID to configure.
- `Integer` **colorCode** - A packed RGBA color value to use as the clear color. Use [rgb](/command/FadeBasic.Lib.Standard.StandardCommands/Rgb) to build one.

**Examples**

Set a render target to clear with a solid color each frame:
```
rtId = reserve render target id()
render target rtId, 0
 ` clear to opaque black each frame
set render target background color rtId, rgb(0, 0, 0)
```

**Remarks**

When a render target is cleared each frame (controlled by`set render target clear flags`),it fills with this color before any sprites are drawn onto it. The defaultis typically transparent black, which is usually what you want for layeredrendering. You might want an opaque color if the render target representsa self-contained scene. Create a render target first with `render target`,then configure its clear behavior with this command and`set render target clear flags`.

---

### set render target clear flags

Controls whether a render target is cleared each frame before drawing.

Pass any value greater than `0` to enable clearing, or `0` todisable it.

**Parameters**

- `Integer` **outputId** - The render target ID to configure.
- `Integer` **clearTarget** - Greater than `0` to clear each frame, `0` to keep previous contents.

**Examples**

Disable clearing for a paint trail effect:
```
rtId = reserve render target id()
render target rtId, 0
 ` don't clear, so previous frames accumulate
set render target clear flags rtId, 0
```

Re-enable clearing after a trail sequence:
```
set render target clear flags rtId, 1
```

**Remarks**

By default, render targets get cleared every frame. Disabling the clearmeans sprites drawn in previous frames stick around, which can be usefulfor trail effects, accumulation buffers, or painting-style visuals whereyou want things to build up over time. When clearing is enabled, the render target fills with whatever color wasset by `set render target background color`before any sprites are drawn to it. Create a render target first with `render target`.

---

### render target texture

Returns the texture ID associated with a render target.

Use the returned ID anywhere you'd use a regular texture. For example,as a [sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite) image or as input to a shader via`set effect param texture`.

**Parameters**

- `Integer` **outputId** - The render target ID to query.

**Returns** `Integer` - The texture ID holding this render target's contents. Use it like any other texture ID.

**Examples**

Display a render target's contents as a sprite:
```
` set up a render target
rtId = reserve render target id()
render target rtId, 0
 ` grab the texture and show it as a sprite
rtTex = render target texture(rtId)
sprite 10, 0, 0, rtTex
```

**Remarks**

Every render target has an associated texture that holds its contents.This command lets you grab that texture ID so you can use the rendertarget's output elsewhere in your rendering pipeline. A common pattern is multi-pass rendering: draw some sprites to a rendertarget, grab its texture with this command, then feed that texture into apost-processing shader or display it on another sprite. The render target must have been set up with`render target` first.

---

### free render target id

Returns the next available render target ID without reserving it.

Calling this multiple times in a row returns the same ID. It doesn'tadvance until something actually reserves or uses that slot.

**Parameters**

- `Integer` _(ref)_ **outputId** - Receives the next available render target ID.

**Returns** `Integer` - The next available render target ID.

**Examples**

Peek at the next available render target ID:
```
nextRtId = free render target id()
```

**Remarks**

Use this when you want to peek at which render target ID would be assignednext without committing to it. In most cases, you'll want`reserve render target id` instead,which both grabs the ID and initializes the slot in one step. The typical flow is: call `reserve render target id`,then `render target` to set it up.You only need this peeking command for more advanced allocation patterns.

---

### reserve render target id

Reserves the next available render target ID and initializes its internal slot.

After calling this, the ID is yours. Pass it to`render target` to finish setting it up.

**Parameters**

- `Integer` _(ref)_ **outputId** - Receives the reserved render target ID.

**Returns** `Integer` - The reserved render target ID.

**Examples**

Full render target setup sequence:
```
` reserve and create a render target
rtId = reserve render target id()
render target rtId, 0
 ` configure it
set render target background color rtId, rgb(0, 0, 0)
set render target clear flags rtId, 1
 ` assign a sprite to draw on it
texture 1, "Images/Player"
sprite 1, 50, 50, 1
set sprite render target 1, rtId
```

**Remarks**

This is the recommended way to get a new render target ID. It calls`free render target id` internallyand makes sure the slot is ready to go. A typical setup sequence: call this to get the ID, then`render target` to create thebacking texture, then optionally configure it with`set render target background color` and`set render target clear flags`.Finally, assign sprites to it with`set sprite render target`.

---

### render target

Creates or configures a render target with an associated texture.

Pass `0` for the texture ID to auto-allocate one, or `-1` totear down the render target and release its texture.

**Parameters**

- `Integer` **outputId** - The render target ID to create or configure.
- `Integer` _(optional)_ **textureId** - The texture ID to associate. Pass `0` to auto-allocate, or `-1` to release.

**Examples**

Create a render target with an auto-allocated texture:
```
` the simplest setup: pass 0 to auto-allocate
rtId = reserve render target id()
render target rtId, 0
 ` draw a sprite onto the render target
texture 1, "Images/Enemy"
sprite 1, 100, 100, 1
set sprite render target 1, rtId
```

Tear down a render target when done:
```
` release the render target and its backing buffer
render target rtId, -1
```

**Remarks**

Render targets let you draw sprites to an off-screen buffer instead of(or in addition to) the main screen. This is the foundation of multi-passrendering, post-processing, and any technique where you need to captureintermediate results. The most common pattern is to pass `0` as the texture ID, which tellsthe system to allocate a texture for you automatically using`reserve texture id`. You can thenretrieve that texture ID with `render target texture`to use it in sprites or shaders. If you pass a specific texture ID, the render target binds to that texture.If the texture ID changes from what was previously bound, a new backingbuffer is created at the current `set render size`dimensions. Passing `-1` clears the render target. Its texture reference isremoved and the backing buffer is released. Once set up, assign sprites to draw on this target using`set sprite render target`, and configureclearing behavior with `set render target background color`and `set render target clear flags`.

---

### set fullscreen

Toggles fullscreen mode on or off.

When going fullscreen, the back buffer resolution is automatically set to match your monitor's native resolution.

**Parameters**

- `Boolean` **fullScreen** - `1` to go fullscreen, `0` for windowed.

**Examples**

Enter fullscreen mode at startup:
```
` configure screen size then go fullscreen
set screen size 1920, 1080
set fullscreen 1
```

Toggle fullscreen on and off with the space key:
```
isFullscreen = 0
set sync rate 16
DO
IF new spaceKey() = 1
IF isFullscreen = 0
set fullscreen 1
isFullscreen = 1
ELSE
set fullscreen 0
isFullscreen = 0
ENDIF
ENDIF
sync
LOOP
```

**Remarks**

Call this during setup after you have configured your desired resolution with`set screen size`. Internally, this applies thechanges and resets render positioning, so you do not need to do that yourself. You cangrab the monitor dimensions ahead of time with `display width`and `display height` if you need to do any math before switching.

---

### set window title

Sets the text that appears in your game window's title bar.

**Parameters**

- `String` **title** - The title string to display in the window bar.

**Examples**

Set the window title at startup:
```
` give the game window a title
set window title "My Awesome Game"
set screen size 1280, 720
```

**Remarks**

Usually you just call this once at startup and forget about it. Nothing stops you fromchanging it later if you want to show dynamic info in the title bar, though.

---

### is os windows

Checks if the game is running on Windows.

**Returns** `Integer` - `1` if running on Windows, `0` otherwise.

**Examples**

Choose a resolution based on the operating system:
```
` set resolution based on platform
IF is os windows() = 1
set screen size 1920, 1080
ELSE
set screen size 1280, 720
ENDIF
```

**Remarks**

Use this alongside `is os mac` when you need to branch onplatform-specific behavior. For example, you might pick different default resolutionson Windows vs Mac.

---

### is os mac

Checks if the game is running on macOS.

**Returns** `Integer` - `1` if running on macOS, `0` otherwise.

**Examples**

Adjust settings on macOS:
```
` check if running on Mac and adjust accordingly
IF is os mac() = 1
set screen size 1280, 800
print "Running on macOS"
ENDIF
```

**Remarks**

Use this alongside `is os windows` when you need to branch onplatform-specific behavior. For example, you might pick different default resolutions orinput handling on Mac vs Windows.

---

### display width

Returns the full width of your physical monitor in pixels.

This is the monitor resolution, not your game window size.

**Returns** `Integer` - The monitor width in pixels.

**Examples**

Print the monitor resolution:
```
` check the monitor's native resolution
w = display width()
h = display height()
print w
print h
```

Set the game window to half the monitor width:
```
` size the window to half the display
dw = display width()
dh = display height()
set screen size dw / 2, dh / 2
```

**Remarks**

Do not confuse this with `screen width`, which gives you thegame's back buffer width (that is, what you set with `set screen size`).This is handy when setting up fullscreen. You can read the display dimensions first todecide how to configure your game resolution. Pairs with `display height`.

---

### display height

Returns the full height of your physical monitor in pixels.

This is the monitor resolution, not your game window size.

**Returns** `Integer` - The monitor height in pixels.

**Examples**

Use the display height to decide on a resolution:
```
` pick a game height based on the monitor
dh = display height()
IF dh >= 1080
set screen size 1920, 1080
ELSE
set screen size 1280, 720
ENDIF
```

**Remarks**

Do not confuse this with `screen height`, which gives you thegame's back buffer height (that is, what you set with `set screen size`).Useful when planning your fullscreen setup. Pairs with `display width`.

---

### screen width

Returns your game's current back buffer width in pixels.

This is the game window size, not the physical monitor resolution.

**Returns** `Integer` - The game's back buffer width in pixels.

**Examples**

Center a sprite horizontally on screen:
```
` place a sprite in the center of the screen
texture 1, "Images/Logo"
sprite 1, 0, 0, 1
sw = screen width()
w = texture width(1)
xPos = (sw - w) / 2
position sprite 1, xPos, 100
```

**Remarks**

This returns whatever you last set with `set screen size`.If you need the physical monitor width instead, use `display width`.Pairs with `screen height`.

---

### screen height

Returns your game's current back buffer height in pixels.

This is the game window size, not the physical monitor resolution.

**Returns** `Integer` - The game's back buffer height in pixels.

**Examples**

Keep a sprite at the bottom of the screen:
```
` position a ground sprite at the bottom edge
texture 1, "Images/Ground"
sprite 1, 0, 0, 1
sh = screen height()
h = texture height(1)
position sprite 1, 0, sh - h
```

**Remarks**

This returns whatever you last set with `set screen size`.If you need the physical monitor height instead, use `display height`.Pairs with `screen width`.

---

### set screen size

Sets the game window resolution by updating the back buffer dimensions.

This applies immediately. There is no need to call a separate apply or refresh command.

**Parameters**

- `Integer` **width** - Desired window width in pixels. Typical values are `640`, `1280`, or `1920`.
- `Integer` **height** - Desired window height in pixels. Typical values are `480`, `720`, or `1080`.

**Examples**

Set up a standard 720p window:
```
` configure a 720p game window
set window title "My Game"
set screen size 1280, 720
set sync rate 16
DO
sync
LOOP
```

Match the screen size to the monitor for borderless windowed:
```
` fill the whole display without going fullscreen
dw = display width()
dh = display height()
set screen size dw, dh
```

**Remarks**

Call this during setup to establish your game's window size. This controls the actual pixeldimensions of the game window (the back buffer), which is different from the internal renderresolution you can set with `set render size`.Think of screen size as "how big is the window on the desktop" and render size as "how manypixels does the game actually draw at internally." After calling this, you can read the values back with `screen width`and `screen height`. If you want to go fullscreen instead, use`set fullscreen`, which will override the back buffer to matchyour monitor's native resolution.

---

### free sprite id

Peeks at the next available sprite ID without claiming it.

This doesn't reserve the ID, so another call could grab it before you do.

**Parameters**

- `Integer` _(ref)_ **spriteId** - Receives the next free sprite ID.

**Returns** `Integer` - The next available sprite ID (not yet reserved).

**Examples**

Peek at the next sprite ID to pre-size an array.
```
` find out what the next sprite ID will be
free sprite id nextId
dim spriteIds(nextId + 10)
```

**Remarks**

Most of the time you'll want `reserve sprite id` instead,which actually claims the slot. This one is handy if you just need to know what the next IDwould be, for example, to pre-allocate an array. If you already know your ID, skip both ofthese and call [sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite) directly.

---

### reserve sprite id

Claims the next available sprite ID and initializes its slot.

The slot is created but the sprite won't be visible until you call [sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite).

**Parameters**

- `Integer` _(ref)_ **spriteId** - Receives the reserved sprite ID.

**Returns** `Integer` - The newly reserved sprite ID.

**Examples**

Reserve a sprite ID, configure it, then make it visible.
```
` reserve a slot and set it up before showing
reserve sprite id spr
set sprite texture spr, texId
scale sprite spr, 2.0, 2.0
sprite spr, 100, 200, texId
```

**Remarks**

Use this when you need to configure a sprite (set its texture, position, etc.) before itofficially exists. The typical pattern is: reserve an ID, set properties on it, then call[sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite) to make it live. If you don't need that setup step, justcall [sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite) directly with a known ID. See also`free sprite id` if you only need to peek without claiming.

---

### sprite

Creates a sprite, or updates an existing one's position and texture.

If the ID already exists, this overwrites its position and texture rather than creating a duplicate.

**Parameters**

- `Integer` **spriteId** - The unique ID for this sprite. Reusing an existing ID updates it.
- `Float` **x** - The X position in screen coordinates.
- `Float` **y** - The Y position in screen coordinates.
- `Integer` **textureId** - The ID of a previously loaded texture.

**Examples**

Load a texture and create a sprite at the center of the screen.
```
` load an image and show it on screen
texture 1, "hero.png"
sprite 1, 320, 240, 1
sync
```

Create multiple sprites from the same texture.
```
` place three copies of the same image in a row
texture 1, "coin.png"
FOR i = 1 TO 3
sprite i, i * 80, 100, 1
NEXT i
DO
sync
LOOP
```

**Remarks**

This is the main way you put images on screen. You'll need to load a texture first with`texture`. The sprite references the texture by ID and won'tactually show up until the next [sync](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sync) call. For moving a sprite aftercreation, `position sprite` is slightly more direct since itskips the texture assignment.

---

### position sprite

Moves a sprite to the given screen position.

**Parameters**

- `Integer` **spriteId** - The ID of the sprite to move.
- `Float` **x** - The new X position in screen coordinates.
- `Float` **y** - The new Y position in screen coordinates.

**Examples**

Move a sprite with the arrow keys.
```
` simple movement loop
texture 1, "player.png"
sprite 1, 320, 240, 1
px = 320
py = 240
DO
IF up key(1) THEN py = py - 2
IF down key(1) THEN py = py + 2
IF left key(1) THEN px = px - 2
IF right key(1) THEN px = px + 2
position sprite 1, px, py
sync
LOOP
```

**Remarks**

Call this every frame for sprites that move, or once for static ones. If you just createdthe sprite with [sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite), the position is already set. Use thisfor updates after creation. The position is where the sprite's origin point lands on screen(see `set sprite offset` to control the origin).

---

### color sprite

Sets the tint color of a sprite using a packed RGBA integer.

This color multiplies with the texture's own colors. A white tint (`0xFFFFFFFF`) shows the texture as-is, while other values shift the hue or darken it.

**Parameters**

- `Integer` **spriteId** - The sprite to tint.
- `Integer` **packedColor** - A packed RGBA color value (e.g. `0xFF0000FF` for opaque red).

**Examples**

Tint a sprite red.
```
` make a sprite appear red-tinted
texture 1, "enemy.png"
sprite 1, 100, 100, 1
color sprite 1, 0xFF0000FF
```

Darken a sprite to 50% brightness.
```
` half-grey tint dims the image
color sprite 1, 0x808080FF
```

**Remarks**

Call this any time after creating the sprite with [sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite). The tint isa multiply blend, so `0xFF0000FF` (red, full alpha) makes the whole sprite red-tinted, and`0x808080FF` (half-grey, full alpha) darkens it to 50%. If you only need to change the RGBchannels without touching alpha, use `set sprite diffuse`.To change just the transparency, use `set sprite alpha`.

---

### order sprite

Sets the draw order (z-order) of a sprite.

Higher values draw on top of lower values, so a sprite with order `10` covers one with order `5`.

**Parameters**

- `Integer` **spriteId** - The sprite to reorder.
- `Integer` **order** - The z-order value. Higher values draw on top.

**Examples**

Layer a background behind a player sprite.
```
` set up two sprites with explicit draw order
texture 1, "background.png"
texture 2, "player.png"
sprite 1, 0, 0, 1
sprite 2, 160, 120, 2
` background draws first, player on top
order sprite 1, 0
order sprite 2, 10
```

**Remarks**

Ordering is per-render-target. A sprite's z-order only matters relative to other sprites on thesame target. If two sprites share the same order value, their draw sequence is undefined, so alwaysassign distinct orders when layering matters. You can call this once at setup or change it dynamically(e.g. to bring a sprite to the front during an animation). See`set sprite render target` for controlling which target a sprite draws to.

---

### hide sprite

Hides a sprite so it is not drawn.

The sprite still exists in memory with all its properties intact. It just skips rendering until you call `show sprite`.

**Parameters**

- `Integer` **spriteId** - The sprite to hide.

**Examples**

Blink a sprite on and off every 30 frames.
```
` simple blink effect
texture 1, "powerup.png"
sprite 1, 200, 150, 1
timer = 0
visible = 1
DO
timer = timer + 1
IF timer > 30
timer = 0
IF visible = 1
hide sprite 1
visible = 0
ELSE
show sprite 1
visible = 1
ENDIF
ENDIF
sync
LOOP
```

**Remarks**

This is cheaper than destroying and recreating a sprite when you need to toggle visibility(e.g. blinking effects, UI panels that open and close). The sprite keeps its position, texture,scale, and everything else. Use `show sprite` to make it visible again.

---

### show sprite

Makes a previously hidden sprite visible again.

Only needed after calling `hide sprite`. Sprites are visible by default when created.

**Parameters**

- `Integer` **spriteId** - The sprite to show.

**Examples**

Show a hidden UI panel when the player presses a key.
```
` toggle an inventory panel with the tab key
texture 10, "inventory.png"
sprite 10, 50, 50, 10
hide sprite 10
panelOpen = 0
DO
IF key hit(scancode("Tab")) = 1
IF panelOpen = 0
show sprite 10
panelOpen = 1
ELSE
hide sprite 10
panelOpen = 0
ENDIF
ENDIF
sync
LOOP
```

**Remarks**

This is the counterpart to `hide sprite`. Calling it on a spritethat is already visible has no effect. The sprite resumes drawing at its current position, scale,and z-order. Nothing else changes.

---

### set sprite texture

Swaps the texture on a sprite without changing anything else.

Position, scale, rotation, color, and all other properties stay the same. Only the image changes.

**Parameters**

- `Integer` **spriteId** - The sprite to update.
- `Integer` **textureId** - The ID of a previously loaded texture.

**Examples**

Swap a character's texture when they take damage.
```
` load both normal and hurt textures
texture 1, "hero.png"
texture 2, "hero_hurt.png"
sprite 1, 200, 200, 1
` later, when the player gets hit
set sprite texture 1, 2
```

**Remarks**

Use this for things like swapping character costumes or cycling through icon states. The newtexture must already be loaded via `texture`. If the new texture hasdifferent dimensions, the sprite's visual size will change (unless you've set an explicit scalewith `scale sprite` or `size sprite`).If the sprite had a frame set via `set sprite frame`, the frameindex carries over. Make sure the new texture has enough frames or reset the frame to `0`.

---

### set sprite render target

Redirects a sprite to draw on a specific render target instead of the default output.

This replaces any previous target assignment. The sprite will only draw to the new target.

**Parameters**

- `Integer` **spriteId** - The sprite to redirect.
- `Integer` **outputId** - The render target ID to draw to.

**Examples**

Draw a sprite to an off-screen render target for a minimap.
```
` create a render target and draw the map icon to it
render target 5, 128, 128
texture 1, "map_icon.png"
sprite 1, 64, 64, 1
set sprite render target 1, 5
```

**Remarks**

By default, sprites draw to the main screen output. Use this to redirect a sprite to an off-screenbuffer created with `render target`. This is how you buildmulti-pass effects, minimaps, or UI layers. The sprite's z-order only competes with other spriteson the same target. To draw a sprite on multiple targets at once, use`add sprite render target` instead. To go back to the defaultoutput, call `reset sprite render target`.

---

### reset sprite render target

Resets a sprite to draw on the default render target.

This undoes any previous `set sprite render target` or `add sprite render target` calls.

**Parameters**

- `Integer` **spriteId** - The sprite to reset to the default output.

**Examples**

Move a sprite back to the main screen after rendering to a buffer.
```
` redirect sprite to a render target, then reset it
set sprite render target 1, 5
` ... do some off-screen rendering ...
reset sprite render target 1
```

**Remarks**

Convenience shortcut, equivalent to calling `set sprite render target`with the default output ID. Use this when you're done drawing a sprite to an off-screen buffer andwant it back on the main screen.

---

### add sprite render target

Adds an additional render target for a sprite, so it draws to multiple targets at once.

Unlike `set sprite render target`, this does not remove existing targets. It stacks.

**Parameters**

- `Integer` **spriteId** - The sprite to add a target to.
- `Integer` **outputId** - The render target ID to add.

**Examples**

Draw a sprite to both the main screen and a minimap buffer.
```
` show the player icon on the main screen and the minimap
render target 5, 128, 128
texture 1, "player_icon.png"
sprite 1, 320, 240, 1
` add the minimap target without removing the main screen
add sprite render target 1, 5
```

**Remarks**

This is how you get a single sprite to appear on both the main screen and an off-screen buffer(or multiple buffers). Each call adds one more target to the sprite's output set. The sprite'sz-order is evaluated independently on each target. To start fresh with a single target, use`set sprite render target` (which replaces rather than adds).To return to defaults, call `reset sprite render target`.

---

### scale sprite

Sets the X and Y scale factors of a sprite directly.

A scale of `1.0` is the original texture size, `2.0` doubles it, `0.5` halves it.

**Parameters**

- `Integer` **spriteId** - The sprite to scale.
- `Float` **x** - Horizontal scale factor. `1.0` = original width.
- `Float` **y** - Vertical scale factor. `1.0` = original height.

**Examples**

Double the size of a sprite uniformly.
```
` make a sprite twice as big
texture 1, "gem.png"
sprite 1, 100, 100, 1
scale sprite 1, 2.0, 2.0
```

Stretch a sprite horizontally for a squash-and-stretch effect.
```
` squash on landing: wide and short
scale sprite 1, 1.4, 0.7
` then spring back to normal
scale sprite 1, 1.0, 1.0
```

**Remarks**

Use this when you want precise control over the scale multiplier. If you'd rather specify atarget pixel size and let Fade figure out the scale, use `size sprite`,`size sprite x`, or `size sprite y`instead. You can set X and Y independently to stretch or squash the sprite. Negative values willmirror the sprite (though `set sprite flip` is cleaner for simple flips).

---

### attach sprite to transform

Attaches a sprite to a transform so it follows the transform's position, rotation, and scale.

The sprite becomes a child of the transform. Move the transform and the sprite moves with it.

**Parameters**

- `Integer` **spriteId** - The sprite to attach.
- `Integer` **transformId** - The transform to follow. Must be created via `transform`.

**Examples**

Attach a sprite and collider to a shared transform.
```
` create a transform and attach both a sprite and a collider
transform 1
texture 1, "hero.png"
sprite 1, 0, 0, 1
attach sprite to transform 1, 1
box collider 1, 0, 0, 32, 32
attach collider to transform 1, 1
` now moving the transform moves everything
position transform 1, 200, 150
```

**Remarks**

This is how you build hierarchical movement. For example, attaching a weapon sprite to a charactertransform so they move together. Create the transform first with `transform`,then attach the sprite here. The sprite's own position becomes a local offset relative to thetransform. You can also attach a collider to the same transform with`attach collider to transform` to keep physics in sync.Call this once during setup; the attachment persists until you change it.

---

### size sprite

Resizes a sprite to exact pixel dimensions by calculating the right scale internally.

This sets X and Y scale independently, so the aspect ratio may change if the target dimensions don't match the texture's ratio.

**Parameters**

- `Integer` **spriteId** - The sprite to resize.
- `Float` **xPixels** - Desired width in pixels.
- `Float` **yPixels** - Desired height in pixels.

**Examples**

Force a sprite to be exactly 64x64 pixels on screen.
```
` resize a sprite to a fixed pixel size regardless of texture dimensions
texture 1, "icon.png"
sprite 1, 10, 10, 1
size sprite 1, 64, 64
```

**Remarks**

This is the easiest way to make a sprite a specific pixel size on screen. It reads the texture'sframe dimensions and computes scale factors to hit the target size. If you want to preserve theaspect ratio, use `size sprite x` (lock width, auto height) or`size sprite y` (lock height, auto width) instead. For directcontrol over the scale multiplier itself, use `scale sprite`.

---

### size sprite x

Resizes a sprite to a target width in pixels while maintaining aspect ratio.

The height scales uniformly with the width, so the image never stretches or squashes.

**Parameters**

- `Integer` **spriteId** - The sprite to resize.
- `Float` **xPixels** - Desired width in pixels. Height adjusts automatically.

**Examples**

Make a sprite 200 pixels wide while keeping its proportions.
```
` set width to 200, height scales automatically
texture 1, "banner.png"
sprite 1, 50, 50, 1
size sprite x 1, 200
```

**Remarks**

This is the go-to for "make this sprite X pixels wide" without distortion. It computes thescale from the texture's frame width and applies it to both axes. If you need to lock the heightinstead, use `size sprite y`. If you want to set both widthand height independently (potentially changing the aspect ratio), use`size sprite`.

---

### size sprite y

Resizes a sprite to a target height in pixels while maintaining aspect ratio.

The width scales uniformly with the height, so the image never stretches or squashes.

**Parameters**

- `Integer` **spriteId** - The sprite to resize.
- `Float` **yPixels** - Desired height in pixels. Width adjusts automatically.

**Examples**

Fit a sprite to a 48-pixel tall slot.
```
` set height to 48, width scales to match
texture 1, "portrait.png"
sprite 1, 10, 10, 1
size sprite y 1, 48
```

**Remarks**

This is the counterpart to `size sprite x`. Use it when youwant to lock the height and let the width follow. It computes the scale from the texture's frameheight and applies it to both axes. For setting exact pixel dimensions on both axes independently,use `size sprite`.

---

### rotate sprite

Rotates a sprite to the given angle in radians.

The sprite rotates around its offset (origin) point. By default that is the top-left corner.

**Parameters**

- `Integer` **spriteId** - The sprite to rotate.
- `Float` **angle** - Rotation angle in radians. `0` is no rotation.

**Examples**

Spin a sprite around its center continuously.
```
` rotate a sprite around its center each frame
texture 1, "star.png"
sprite 1, 320, 240, 1
set sprite offset 1, 0.5, 0.5
angle = 0.0
DO
angle = angle + 0.02
rotate sprite 1, angle
sync
LOOP
```

Rotate a sprite by 45 degrees using the [rad](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Rad) helper.
```
` tilt a sprite 45 degrees
set sprite offset 1, 0.5, 0.5
rotate sprite 1, rad(45)
```

**Remarks**

This sets an absolute angle, not a delta. Calling it with the same value every frame holds therotation steady. If you want the sprite to rotate around its center, set the offset to `(0.5, 0.5)`first with `set sprite offset`. The angle is in radians; use[rad](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Rad) to convert from degrees if needed. If the sprite is attached to atransform via `attach sprite to transform`, thisrotation is applied on top of the transform's rotation.

---

### set sprite offset

Sets the origin point of a sprite as a ratio of its size.

`(0, 0)` is the top-left corner, `(0.5, 0.5)` is the center, `(1, 1)` is the bottom-right. This affects both the rotation pivot and where the position anchors.

**Parameters**

- `Integer` **spriteId** - The sprite to adjust.
- `Float` **xRatio** - Horizontal origin as a 0-to-1 ratio of the sprite's width.
- `Float` **yRatio** - Vertical origin as a 0-to-1 ratio of the sprite's height.

**Examples**

Center a sprite's origin for rotation.
```
` set origin to the center so rotation looks natural
set sprite offset 1, 0.5, 0.5
rotate sprite 1, rad(90)
```

Anchor a sprite from its bottom-center (useful for characters standing on a surface).
```
` anchor at the bottom-center so the feet stay on the ground
set sprite offset 1, 0.5, 1.0
position sprite 1, 320, 400
```

**Remarks**

By default the origin is `(0, 0)` (top-left), which means`position sprite` places the top-left corner at the givencoordinates. Set it to `(0.5, 0.5)` if you want the sprite's center at that position.This is especially important for `rotate sprite`, which pivotsaround the origin. Values outside `0` to `1` are valid and shift the anchor beyond the sprite's bounds.

---

### set sprite all texcoord1

Sets the secondary texture coordinate (texcoord1) for all four vertices of a sprite at once.

This is an advanced feature for passing custom per-sprite data to shaders. You won't need it unless you're writing custom effects.

**Parameters**

- `Integer` **spriteId** - The sprite to update.
- `Float` **x** - The X component of the texcoord1 vector.
- `Float` **y** - The Y component of the texcoord1 vector.
- `Float` **z** - The Z component of the texcoord1 vector.
- `Float` **w** - The W component of the texcoord1 vector.

**Examples**

Pass a dissolve threshold to a custom shader.
```
` set up a dissolve effect and pass the threshold via texcoord1
effect 1, "dissolve.fx"
set sprite effect 1, 1
` x = dissolve threshold (0.0 to 1.0), y/z/w unused
set sprite all texcoord1 1, 0.5, 0.0, 0.0, 0.0
```

**Remarks**

Each sprite quad has four vertices, and each vertex has a second texture coordinate slot (texcoord1)that is not used by the default rendering pipeline. When you assign a custom shader via`set sprite effect`, your shader can read these values to driveeffects like dissolve thresholds, color-cycling parameters, or distortion strength. This overloadsets the same value on all four corners. If you need per-corner values (e.g. for gradient effects),use `set sprite index texcoord1`.

---

### set sprite index texcoord1

Sets the secondary texture coordinate (texcoord1) for a single corner vertex of a sprite.

This is an advanced feature for passing per-vertex data to custom shaders. Most use cases only need `set sprite all texcoord1`.

**Parameters**

- `Integer` **spriteId** - The sprite to update.
- `Integer` **cornerIndex** - Which corner: `0` = top-left, `1` = top-right, `2` = bottom-left, `3` = bottom-right.
- `Float` **x** - The X component of the texcoord1 vector.
- `Float` **y** - The Y component of the texcoord1 vector.
- `Float` **z** - The Z component of the texcoord1 vector.
- `Float` **w** - The W component of the texcoord1 vector.

**Examples**

Set up a vertical gradient by giving top corners one value and bottom corners another.
```
` top corners get 1.0, bottom corners get 0.0 in the x channel
set sprite index texcoord1 1, 0, 1.0, 0.0, 0.0, 0.0
set sprite index texcoord1 1, 1, 1.0, 0.0, 0.0, 0.0
set sprite index texcoord1 1, 2, 0.0, 0.0, 0.0, 0.0
set sprite index texcoord1 1, 3, 0.0, 0.0, 0.0, 0.0
```

**Remarks**

Each sprite is a quad with four corners. This overload lets you set a different texcoord1 value oneach corner, which the GPU interpolates across the sprite's surface. This is useful for gradient-styleshader effects where each corner needs a distinct value. Assign a custom shader first with`set sprite effect`, then set corner data here. Corner indices:`0` = top-left, `1` = top-right, `2` = bottom-left, `3` = bottom-right.

---

### set sprite effect

Assigns a custom shader effect to a sprite.

The sprite will be drawn using this effect instead of the default pipeline. All sprites sharing an effect are batched together.

**Parameters**

- `Integer` **spriteId** - The sprite to apply the effect to.
- `Integer` **effectId** - The ID of a previously loaded effect.

**Examples**

Apply a custom glow shader to a sprite.
```
` load a shader and assign it to a sprite
effect 1, "glow.fx"
texture 1, "orb.png"
sprite 1, 200, 200, 1
set sprite effect 1, 1
```

**Remarks**

Load the effect first with `effect`, then pass its ID here. Onceassigned, the sprite uses that shader every frame until you change it. You can pass per-spritedata to the shader via `set sprite all texcoord1`or `set sprite index texcoord1`.Sprites with the same effect are drawn together in the same batch, so grouping sprites by effectis good for performance.

---

### set sprite diffuse

Sets the RGB color channels of a sprite, leaving alpha unchanged.

Use this when you want to tint or recolor a sprite without affecting its transparency.

**Parameters**

- `Integer` **spriteId** - The sprite to tint.
- `Byte` **red** - Red channel, `0` to `255`.
- `Byte` **green** - Green channel, `0` to `255`.
- `Byte` **blue** - Blue channel, `0` to `255`.

**Examples**

Give a sprite a green tint.
```
` tint the sprite green while keeping alpha as-is
set sprite diffuse 1, 100, 255, 100
```

**Remarks**

This modifies only the red, green, and blue channels. The alpha channel stays at whatever itwas before. Like `color sprite`, these values multiply with thetexture's colors. Setting all three to `255` shows the texture at full brightness. Tochange alpha independently, use `set sprite alpha`.To set all four channels at once with a packed integer, use `color sprite`.

---

### set sprite alpha

Sets the transparency of a sprite.

`0` is fully transparent (invisible), `255` is fully opaque. RGB channels are not affected.

**Parameters**

- `Integer` **spriteId** - The sprite to adjust.
- `Byte` **alpha** - Alpha value, `0` to `255`. `0` = transparent, `255` = opaque.

**Examples**

Fade a sprite in from fully transparent to fully opaque.
```
` gradually fade in a sprite over many frames
texture 1, "title.png"
sprite 1, 200, 100, 1
set sprite alpha 1, 0
alpha = 0
DO
IF alpha < 255
alpha = alpha + 3
IF alpha > 255 THEN alpha = 255
set sprite alpha 1, alpha
ENDIF
sync
LOOP
```

Make a sprite semi-transparent for a ghost effect.
```
` 50% transparency
set sprite alpha 1, 128
```

**Remarks**

This is the quickest way to fade a sprite in or out without touching its color tint. The alphavalue multiplies with the texture's own alpha, so a texture pixel at 50% alpha with a sprite alphaof `128` ends up at roughly 25% opacity. To set RGB channels without touching alpha, use`set sprite diffuse`. To set all fourchannels at once, use `color sprite`.

---

### set sprite frame

Selects which frame of a spritesheet to display on a sprite.

The texture must have its frame grid set up first via `set texture frame grid`, or this won't do anything useful.

**Parameters**

- `Integer` **spriteId** - The sprite to update.
- `Integer` **frameId** - Zero-based frame index into the texture's frame grid.

**Examples**

Animate a sprite by cycling through frames.
```
` set up a 4x4 spritesheet and animate it
texture 1, "walk.png"
set texture frame grid 1, 4, 4
sprite 1, 200, 200, 1
frame = 0
totalFrames = texture frames(1)
timer = 0
DO
timer = timer + 1
IF timer > 5
timer = 0
frame = frame + 1
IF frame >= totalFrames THEN frame = 0
set sprite frame 1, frame
ENDIF
sync
LOOP
```

**Remarks**

Frame indices are zero-based and count left-to-right, top-to-bottom across the grid. You canquery how many frames a texture has with `texture frames`.Call this every frame (or whenever the animation advances) to animate a sprite through itsspritesheet. If the sprite's texture is a single image with no frame grid, frame `0` showsthe whole texture.

---

### set sprite flip

Flips a sprite horizontally, vertically, or both.

Pass `1` to flip an axis, `0` for normal. This is a visual flip only. Position and offset are not affected.

**Parameters**

- `Integer` **spriteId** - The sprite to flip.
- `Integer` **flipHorizontal** - `1` to flip horizontally, `0` for normal.
- `Integer` **flipVertical** - `1` to flip vertically, `0` for normal.

**Examples**

Flip a character sprite to face left when moving left.
```
` flip based on movement direction
IF left key(1)
set sprite flip 1, 1, 0
px = px - 2
ENDIF
IF right key(1)
set sprite flip 1, 0, 0
px = px + 2
ENDIF
```

**Remarks**

This is the cleanest way to mirror a sprite (e.g. flipping a character to face left vs. right).It's cheaper and simpler than using negative scale values via `scale sprite`.Both axes can be flipped simultaneously by passing `1` for both parameters. The flip isapplied after rotation, so a rotated + flipped sprite may look different than a flipped + rotated one.

---

### sprite width

Returns the width of the sprite's current texture frame in pixels, before any scaling is applied.

If the texture uses a frame grid, this returns the width of a single frame, not the whole texture.

**Parameters**

- `Integer` **spriteId** - The sprite to measure.

**Returns** `Float` - Width of the current frame in pixels (before scaling).

**Examples**

Center a sprite based on its width.
```
` place a sprite so its center is at screen X = 320
texture 1, "logo.png"
sprite 1, 0, 100, 1
w = sprite width(1)
position sprite 1, 320 - w / 2, 100
```

**Remarks**

Use this to get the raw pixel dimensions of what the sprite is displaying. This is the basemeasurement that `scale sprite` multiplies against. If you need theon-screen size, multiply this by the sprite's current X scale. Pair with`sprite height` for both dimensions.

---

### sprite height

Returns the height of the sprite's current texture frame in pixels, before any scaling is applied.

If the texture uses a frame grid, this returns the height of a single frame, not the whole texture.

**Parameters**

- `Integer` **spriteId** - The sprite to measure.

**Returns** `Float` - Height of the current frame in pixels (before scaling).

**Examples**

Stack two sprites vertically using their heights.
```
` place sprite 2 directly below sprite 1
h = sprite height(1)
y1 = sprite y(1)
position sprite 2, sprite x(1), y1 + h
```

**Remarks**

Use this to get the raw pixel dimensions of what the sprite is displaying. This is the basemeasurement that `scale sprite` multiplies against. If you need theon-screen size, multiply this by the sprite's current Y scale. Pair with`sprite width` for both dimensions.

---

### sprite x

Returns the current X position of a sprite.

This is the position last set by [sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite) or `position sprite`. It does not include transform offsets.

**Parameters**

- `Integer` **spriteId** - The sprite to query.

**Returns** `Float` - The X position in screen coordinates (or local coordinates if attached to a transform).

**Examples**

Read a sprite's position and print it.
```
` check where a sprite is
px = sprite x(1)
py = sprite y(1)
```

**Remarks**

If the sprite is attached to a transform via `attach sprite to transform`,this returns the sprite's local position, not its final on-screen position. Pair with`sprite y` for the full coordinate.

---

### sprite y

Returns the current Y position of a sprite.

This is the position last set by [sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite) or `position sprite`. It does not include transform offsets.

**Parameters**

- `Integer` **spriteId** - The sprite to query.

**Returns** `Float` - The Y position in screen coordinates (or local coordinates if attached to a transform).

**Examples**

Clamp a sprite so it cannot move off the bottom of the screen.
```
` keep the sprite above the screen floor
py = sprite y(1)
IF py > 440 THEN position sprite 1, sprite x(1), 440
```

**Remarks**

If the sprite is attached to a transform via `attach sprite to transform`,this returns the sprite's local position, not its final on-screen position. Pair with`sprite x` for the full coordinate.

---

### set sync rate

Sets the target frame time in milliseconds.

This controls how long the engine waits between frames: `16` ms gives you roughly 60 fps, `33` ms gives you roughly 30 fps.

**Parameters**

- `Integer` **rate** - Target elapsed time per frame, in milliseconds. Common values: `16` (~60 fps), `33` (~30 fps).

**Examples**

Standard 60 fps game loop setup:
```
` set up a 60 fps game loop
set sync rate 16
DO
` game logic goes here
sync
LOOP
```

Switch to a slower frame rate for a cutscene:
```
` run at 30 fps during a cutscene, then switch back
set sync rate 33
` ... play cutscene ...
set sync rate 16
```

**Remarks**

Call this once during setup, before your main `DO...LOOP`. You generallydon't need to change it at runtime, though nothing stops you from doing so(for example, dropping to 30 fps during a heavy scene). This works hand-in-hand with `sync`.The sync call is what actually yields to let the frame happen, and the rate youset here determines how long that frame takes. If you never call`sync`, this setting has no visible effect.

---

### sync

Suspends script execution and lets a render frame happen.

Without this call, nothing you draw, move, or change will ever appear on screen.

**Parameters**


**Examples**

Minimal game loop that moves a sprite each frame:
```
` move a sprite to the right, one pixel per frame
set sync rate 16
texture 1, "Images/Ball"
sprite 1, 0, 100, 1
x = 0
DO
x = x + 1
sprite 1, x, 100, 1
sync
LOOP
```

**Remarks**

This is THE core game loop command. You'll typically call it once per iterationinside a `DO...LOOP`. Every sprite move, text change, or effect you set upbetween syncs becomes visible only after this call fires. Pair it with `set sync rate` to control how fastframes tick. You can read `game ms` right after a syncto get the current time for animations, or check`frame number` if you prefer frame-based timing. Calling sync twice in a row is harmless; you just get an extra frame with nochanges. Forgetting to call it at all means your script runs to completion andthe window closes (or hangs) without ever rendering.

---

### frame number

Returns the current frame number.

The counter increments by one each time `sync` is called, starting from zero.

**Returns** `DoubleInteger` - The current frame number. Starts at `0` and increments by one per sync.

**Examples**

Cycle a sprite image every 10 frames:
```
` swap between two images every 10 frames
set sync rate 16
texture 1, "Images/Frame1"
texture 2, "Images/Frame2"
sprite 1, 100, 100, 1
DO
f = frame number()
` switch image every 10 frames
img = (f / 10) mod 2 + 1
sprite 1, 100, 100, img
sync
LOOP
```

Trigger an event after 120 frames:
```
set sync rate 16
DO
f = frame number()
IF f = 120 THEN print "two seconds have passed!"
sync
LOOP
```

**Remarks**

Useful for frame-based timing and animations. For example, you can cycle a spritesheet every N frames, or trigger an event after a fixed number of updates. If you need real wall-clock time instead of frame counts, use`game ms`.

---

### free text id

Peeks at the next available text sprite ID without claiming it.

The returned ID is not reserved, so another call could grab it before you do.

**Parameters**

- `Integer` _(ref)_ **textId** - Receives the next available text ID.

**Returns** `Integer` - The next available text ID.

**Examples**

Check what the next text ID will be before creating it.
```
` peek at the next available text ID
nextId = free text id()
print "Next text ID will be: " + str(nextId)
```

**Remarks**

Same pattern as the sprite ID management commands. Call this when you need to know what IDwill be assigned next but aren't ready to create the text sprite yet. If you actually wantto lock in the ID, use `reserve text id` instead.

---

### reserve text id

Claims the next available text sprite ID and initializes its slot.

Unlike `free text id`, this actually reserves the ID so nothing else can take it.

**Parameters**

- `Integer` _(ref)_ **textId** - Receives the reserved text ID.

**Returns** `Integer` - The reserved text ID.

**Examples**

Reserve a text ID ahead of time, then create the text later.
```
` reserve the ID so nothing else grabs it
myTextId = reserve text id()
 ` later, use the reserved ID to create the text
font 1, "Fonts/Arial"
text myTextId, 100, 50, 1, "Hello!"
```

**Remarks**

Same pattern as the sprite ID reservation. Use this when you want to set up an ID ahead of timebefore calling [text](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Text) to fill in the details. Handy if you need to wire upreferences between text sprites before they're fully configured.

---

### text

Creates a text sprite with a position, font, and string content.

If the ID already exists, it updates the existing text sprite instead of creating a new one.

**Parameters**

- `Integer` **textId** - The text sprite ID. If it already exists, the sprite is updated.
- `Integer` **x** - X position in pixels.
- `Integer` **y** - Y position in pixels.
- `Integer` **spriteFontId** - The sprite font ID returned by `font`.
- `String` **text** - The string to display.

**Examples**

Create a simple text sprite and display it.
```
font 1, "Fonts/Arial"
text 1, 100, 50, 1, "Hello World!"
DO
sync
LOOP
```

Update an existing text sprite by reusing the same ID.
```
font 1, "Fonts/Arial"
text 1, 100, 50, 1, "First message"
sync
wait 1000
` reusing ID 1 updates the text in place
text 1, 100, 50, 1, "Updated message"
sync
```

**Remarks**

This is the main entry point for getting text on screen. You need a font loaded via`font` first, or you'll get nothing. The text sprite won'tactually appear until the next [sync](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sync). Text sprites work almostidentically to regular sprites. They share the same rendering pipeline for z-ordering,render targets, transforms, etc.

---

### set text

Updates the displayed string of an existing text sprite.

This changes only the text content. Position, color, scale, and everything else stay the same.

**Parameters**

- `Integer` **textId** - The text sprite ID to update.
- `String` **text** - The new string to display.

**Examples**

Update a score display every frame.
```
font 1, "Fonts/Arial"
text 1, 10, 10, 1, "Score: 0"
score = 0
DO
score = score + 1
set text 1, "Score: " + str(score)
sync
LOOP
```

**Remarks**

Use this when you need to change what a text sprite says without tearing it down and recreating it.For example, updating a score counter or a status label every frame. If you haven't created thetext sprite yet, call [text](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Text) first. If you also need to resize the spriteto fit the new string, follow up with `size text` or`size text x` since the scale won'tautomatically adjust to the new content.

---

### set text position

Moves a text sprite to a new screen position.

This is the text equivalent of `position sprite`.

**Parameters**

- `Integer` **textId** - The text sprite ID to move.
- `Integer` **x** - New X position in pixels.
- `Integer` **y** - New Y position in pixels.

**Examples**

Animate a text sprite moving across the screen.
```
font 1, "Fonts/Arial"
text 1, 0, 100, 1, "Moving text!"
xPos = 0
DO
xPos = xPos + 2
set text position 1, xPos, 100
sync
LOOP
```

**Remarks**

Call this whenever you need to reposition a text sprite. Use it every frame for animation, or oncefor static placement. The position is in screen pixels and represents the top-left corner bydefault, but that changes if you've set a custom origin with`set text offset`. If the text sprite is attached to atransform via `attach text to transform`,this position becomes relative to that transform.

---

### color text

Sets the color of a text sprite using a packed RGBA color value.

This replaces the current color entirely, alpha included. Use`set text alpha` if you only want to change transparency.

**Parameters**

- `Integer` **textId** - The text sprite ID to color.
- `Integer` **colorCode** - Packed RGBA color value.

**Examples**

Color text red and display it.
```
font 1, "Fonts/Arial"
text 1, 100, 50, 1, "Warning!"
` red with full opacity
color text 1, 0xFF0000FF
DO
sync
LOOP
```

**Remarks**

The color value is a packed integer in RGBA format. This works just like`color sprite` but for text. The color tints the renderedglyphs, so white (`0xFFFFFFFF`) shows the font's original appearance. If the textsprite has a drop shadow enabled, use `color text drop shadow`to color the shadow independently.

---

### color text drop shadow

Sets the color of a text sprite's drop shadow independently from the main text color.

The drop shadow must already be enabled via `enable text drop shadow`for this to have any visible effect.

**Parameters**

- `Integer` **textId** - The text sprite ID whose shadow color to change.
- `Integer` **colorCode** - Packed RGBA color value for the shadow.

**Examples**

Change a drop shadow to a subtle blue after enabling it.
```
font 1, "Fonts/Arial"
text 1, 100, 50, 1, "Shadow text"
enable text drop shadow 1, 2, 2, 0x000000FF
` change the shadow color to dark blue with half opacity
color text drop shadow 1, 0x000088AA
DO
sync
LOOP
```

**Remarks**

Use this when you want to change just the shadow color without touching the offset or togglingthe shadow on/off. A common pattern is a dark, semi-transparent shadow. Pack your RGBA witha low alpha for a subtle effect. The shadow is drawn as a second copy of the text at the offsetyou specified when enabling it, so this color applies to that entire second copy.

---

### enable text drop shadow

Enables a drop shadow on a text sprite and configures its offset and color in one call.

The shadow is drawn as a second copy of the text rendered behind the original at the given pixel offset.

**Parameters**

- `Integer` **textId** - The text sprite ID.
- `Integer` **x** - Shadow X offset in pixels from the text position.
- `Integer` **y** - Shadow Y offset in pixels from the text position.
- `Integer` **colorCode** - Packed RGBA color value for the shadow.

**Examples**

Add a black drop shadow offset by 2 pixels in each direction.
```
font 1, "Fonts/Arial"
text 1, 100, 50, 1, "Readable text"
` black shadow, 2 pixels down and right
enable text drop shadow 1, 2, 2, 0x000000FF
DO
sync
LOOP
```

Use a soft, semi-transparent shadow for a subtler effect.
```
font 1, "Fonts/Arial"
text 1, 200, 100, 1, "Soft shadow"
` dark gray shadow with half opacity, offset 1 pixel
enable text drop shadow 1, 1, 1, 0x33333388
DO
sync
LOOP
```

**Remarks**

Drop shadows make text more readable over busy backgrounds. The shadow is literally the samestring drawn again at `(x, y)` pixels from the original position, using the color youprovide here. Common values are small offsets like `(2, 2)` with a dark or black color.Once enabled, you can tweak just the color later with`color text drop shadow`, or turn it off entirelywith `disable text drop shadow`. The shadow respectsthe text sprite's scale, rotation, and render target assignment.

---

### disable text drop shadow

Disables the drop shadow on a text sprite.

The shadow settings (offset, color) are preserved, so re-enabling later restores the previous look.

**Parameters**

- `Integer` **textId** - The text sprite ID whose shadow to disable.

**Examples**

Toggle a drop shadow on and off.
```
font 1, "Fonts/Arial"
text 1, 100, 50, 1, "Toggle shadow"
enable text drop shadow 1, 2, 2, 0x000000FF
sync
wait 2000
` turn off the shadow; settings are preserved
disable text drop shadow 1
sync
wait 2000
` re-enable with the same offset and color
enable text drop shadow 1, 2, 2, 0x000000FF
sync
```

**Remarks**

Use this to turn off a shadow you previously enabled with`enable text drop shadow`. This is a visibility toggleonly. It doesn't clear the offset or color, so calling`enable text drop shadow` again will bring back thesame shadow without needing to reconfigure it.

---

### set text alpha

Sets the transparency of a text sprite.

`0` is fully transparent (invisible) and `255` is fully opaque.

**Parameters**

- `Integer` **textId** - The text sprite ID.
- `Byte` **alpha** - Alpha value from `0` (transparent) to `255` (opaque).

**Examples**

Fade text in from transparent to fully opaque.
```
font 1, "Fonts/Arial"
text 1, 100, 50, 1, "Fading in..."
a = 0
DO
set text alpha 1, a
IF a < 255 THEN a = a + 5 ENDIF
IF a > 255 THEN a = 255 ENDIF
sync
LOOP
```

**Remarks**

This modifies only the alpha channel, leaving the RGB color untouched. If you need tochange both color and alpha at once, use `color text` insteadsince that takes a packed RGBA value. Useful for fade-in/fade-out effects; just tween thealpha value each frame. The drop shadow (if enabled) is not affected by this; it usesthe alpha from its own color set via `color text drop shadow`or `enable text drop shadow`.

---

### scale text

Sets the X and Y scale factors of a text sprite directly.

A scale of `1.0` is the font's native size; values below shrink, above enlarge.

**Parameters**

- `Integer` **textId** - The text sprite ID.
- `Float` **x** - Scale factor on the X axis. `1.0` = native size.
- `Float` **y** - Scale factor on the Y axis. `1.0` = native size.

**Examples**

Double the size of a text sprite.
```
font 1, "Fonts/Arial"
text 1, 100, 50, 1, "Big text"
` scale to twice the native font size
scale text 1, 2.0, 2.0
DO
sync
LOOP
```

**Remarks**

This gives you direct control over the scale, unlike `size text`which calculates the scale from a target pixel size. You can set different X and Y valuesto stretch the text non-uniformly, but that usually looks bad for readable text. If youwant uniform scaling to a target pixel width or height, use`size text x` or`size text y` instead.

---

### order text

Sets the draw order (z-order) for a text sprite.

Higher values draw on top of lower values, just like regular sprites.

**Parameters**

- `Integer` **textId** - The text sprite ID.
- `Integer` **order** - The z-order value. Higher = drawn on top.

**Examples**

Layer text on top of a sprite using z-order.
```
font 1, "Fonts/Arial"
` create a sprite and a text label
sprite 1, 100, 100, loadImage("background.png")
order sprite 1, 5
text 1, 110, 110, 1, "On top!"
order text 1, 10
DO
sync
LOOP
```

**Remarks**

Text sprites and regular sprites share the same z-order space within a render target,so you can interleave them. For example, a text sprite with order `10` draws on top ofa regular [sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite) with order `5`. Setting the order marksthe render target's sprite list as dirty, so it will be re-sorted before the next draw.

---

### hide text

Hides a text sprite so it is not drawn.

The text sprite still exists and keeps all its properties. It just becomes invisible.

**Parameters**

- `Integer` **textId** - The text sprite ID to hide.

**Examples**

Hide a text sprite and show it again after a delay.
```
font 1, "Fonts/Arial"
text 1, 100, 50, 1, "Now you see me"
sync
wait 2000
hide text 1
sync
wait 2000
show text 1
sync
```

**Remarks**

Use this instead of destroying and recreating text sprites when you need to toggle visibility.The sprite stays in memory with its position, color, scale, and everything else intact.Call `show text` to make it visible again. This is the textequivalent of hiding a regular [sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite).

---

### show text

Makes a previously hidden text sprite visible again.

Has no effect if the text sprite is already visible.

**Parameters**

- `Integer` **textId** - The text sprite ID to show.

**Examples**

Show a hidden text sprite.
```
font 1, "Fonts/Arial"
text 1, 100, 50, 1, "Hidden at first"
hide text 1
sync
wait 1000
` make it visible again
show text 1
DO
sync
LOOP
```

**Remarks**

This is the counterpart to `hide text`. The text spritereappears exactly as it was before hiding, with the same position, color, scale, render target,and everything else. You don't need to reconfigure anything after showing it.

---

### set text render target

Assigns a text sprite to draw on a specific render target.

This replaces any previous render target assignment. The text sprite will only draw to the new target.

**Parameters**

- `Integer` **textId** - The text sprite ID.
- `Integer` **outputId** - The render target ID to draw to.

**Examples**

Draw text onto a custom render target.
```
font 1, "Fonts/Arial"
` create a 256x256 render target
rtId = render target(256, 256)
text 1, 10, 10, 1, "On render target"
` redirect text to the custom target
set text render target 1, rtId
DO
sync
LOOP
```

**Remarks**

By default, text sprites draw to the main screen (render target `1`). Use this toredirect a text sprite to a different render target created with`render target`. This works the same way asrender target assignment for regular sprites. If you want the text sprite to appear onmultiple render targets simultaneously, use`add text render target` instead. To go backto the default, call `reset text render target`.

---

### reset text render target

Resets a text sprite to draw on the default render target (the main screen).

This removes any custom render target assignment.

**Parameters**

- `Integer` **textId** - The text sprite ID to reset.

**Examples**

Move text back to the main screen after drawing to a custom render target.
```
font 1, "Fonts/Arial"
rtId = render target(256, 256)
text 1, 10, 10, 1, "Temporary"
set text render target 1, rtId
sync
` move it back to the main screen
reset text render target 1
DO
sync
LOOP
```

**Remarks**

Equivalent to calling `set text render target`with output ID `1`. Use this when you're done drawing a text sprite to an off-screenrender target and want it back on the main screen.

---

### add text render target

Adds an additional render target for a text sprite without removing existing ones.

The text sprite will draw to all assigned render targets each frame.

**Parameters**

- `Integer` **textId** - The text sprite ID.
- `Integer` **outputId** - The additional render target ID to add.

**Examples**

Draw the same text on both the main screen and a custom render target.
```
font 1, "Fonts/Arial"
rtId = render target(256, 256)
text 1, 10, 10, 1, "Everywhere!"
` text already draws to the main screen by default;
` add it to the custom target as well
add text render target 1, rtId
DO
sync
LOOP
```

**Remarks**

Unlike `set text render target` which replacesthe assignment, this stacks on top of whatever targets the text sprite already draws to.Useful when you want the same text to appear on the main screen and also on an off-screenrender target (e.g., a minimap or a UI overlay). Works the same way as adding rendertargets to regular sprites.

---

### size text

Scales a text sprite to fit exact pixel dimensions for both width and height.

This calculates independent X and Y scale factors, so the text may stretch non-uniformly.

**Parameters**

- `Integer` **textId** - The text sprite ID.
- `Float` **xPixels** - Target width in pixels.
- `Float` **yPixels** - Target height in pixels.

**Examples**

Scale text to fill a 200x50 pixel box.
```
font 1, "Fonts/Arial"
text 1, 50, 50, 1, "Stretched to fit"
` scale to exactly 200 wide by 50 tall (may stretch)
size text 1, 200, 50
DO
sync
LOOP
```

**Remarks**

The command measures the text string using the assigned font and then computes the scaleneeded to fill the target rectangle. Because X and Y are calculated independently, thetext will distort if the aspect ratio doesn't match. If you want to scale uniformly(preserving the font's aspect ratio), use`size text x` or`size text y` instead. If you change the textcontent with `set text`, you'll need to call this again sincethe measured size will be different.

---

### size text x

Scales a text sprite to a target width in pixels, scaling uniformly to maintain aspect ratio.

Both X and Y scale are set to the same value, so the text won't stretch or squish.

**Parameters**

- `Integer` **textId** - The text sprite ID.
- `Float` **xPixels** - Target width in pixels.

**Examples**

Scale text uniformly to fit a 300-pixel width.
```
font 1, "Fonts/Arial"
text 1, 50, 50, 1, "Uniform scale"
` scale so the width is exactly 300 pixels; height adjusts proportionally
size text x 1, 300
DO
sync
LOOP
```

**Remarks**

This measures the text string's natural width and calculates a uniform scale factor sothe rendered width matches . The height scales proportionally.If the font hasn't been assigned yet, this logs a warning and does nothing. For theheight-based equivalent, see `size text y`. Ifyou need to clamp the resulting scale to a range (e.g., to prevent text from gettingabsurdly large or tiny), use the overload`size text x` thattakes min and max parameters.

---

### size text x

Scales a text sprite to a target width in pixels with clamped scale bounds, maintaining aspect ratio.

The computed scale is clamped between  and ,preventing the text from becoming too small or too large.

**Parameters**

- `Integer` **textId** - The text sprite ID.
- `Float` **xPixels** - Target width in pixels.
- `Float` **min** - Minimum allowed scale factor.
- `Float` **max** - Maximum allowed scale factor.

**Examples**

Size text to 200 pixels wide, but clamp the scale between 0.5 and 2.0.
```
font 1, "Fonts/Arial"
text 1, 50, 50, 1, "Clamped scale"
` target 200px wide, but never shrink below 0.5 or grow above 2.0
size text x 1, 200, 0.5, 2.0
DO
sync
LOOP
```

**Remarks**

Works like the unclamped `size text x`,but after computing the scale factor it clamps the result to the`[min, max]` range. This is useful when you have dynamic text (like player names orscores) that varies wildly in length. You can target a fixed width but guarantee thetext never scales below a readable minimum or above a maximum that breaks your layout.If the font hasn't been assigned yet, this logs a warning and does nothing.

---

### size text y

Scales a text sprite to a target height in pixels, scaling uniformly to maintain aspect ratio.

Both X and Y scale are set to the same value, so the text won't stretch or squish.

**Parameters**

- `Integer` **textId** - The text sprite ID.
- `Float` **yPixels** - Target height in pixels.

**Examples**

Scale text to fit a 40-pixel tall row.
```
font 1, "Fonts/Arial"
text 1, 50, 50, 1, "Fit the row"
` scale so the height is exactly 40 pixels; width adjusts proportionally
size text y 1, 40
DO
sync
LOOP
```

**Remarks**

This is the height-based counterpart to`size text x`. It measures the textstring's natural height and calculates a uniform scale factor so the rendered heightmatches . The width scales proportionally. If the fonthasn't been assigned yet, this logs a warning and does nothing. Handy when you wanttext to fit a fixed vertical space (like a UI row) regardless of the string length.

---

### attach text to transform

Attaches a text sprite to a transform for hierarchical positioning.

The text sprite's position, rotation, and scale become relative to the transform.

**Parameters**

- `Integer` **textId** - The text sprite ID to attach.
- `Integer` **transformId** - The transform ID to attach to, created via `transform`.

**Examples**

Make a health label follow a character transform.
```
font 1, "Fonts/Arial"
` create a transform for the character
tId = transform()
position transform tId, 200, 150
 ` create the label and attach it to the transform
text 1, 0, -20, 1, "100 HP"
attach text to transform 1, tId
 ` now moving the transform moves the text too
DO
position transform tId, 200 + rnd(4), 150
sync
LOOP
```

**Remarks**

Once attached, the text sprite follows the transform as it moves, rotates, and scales.This is how you make text follow a game object. Create a transform with`transform`, attach it to your entity, then attach thetext sprite to that same transform. The text sprite's own position (set via`set text position`) becomes an offset relative to thetransform rather than an absolute screen position. Works identically to how regularsprites attach to transforms.

---

### rotate text

Sets the rotation of a text sprite to a specific angle in radians.

The text rotates around its origin point, which defaults to the top-left corner.

**Parameters**

- `Integer` **textId** - The text sprite ID.
- `Float` **angle** - Rotation angle in radians. `0` = no rotation.

**Examples**

Spin text around its center.
```
font 1, "Fonts/Arial"
text 1, 200, 150, 1, "Spinning!"
` set the origin to center so it rotates in place
set text offset 1, 0.5, 0.5
angle# = 0.0
DO
angle# = angle# + 0.02
rotate text 1, angle#
sync
LOOP
```

**Remarks**

The angle is in radians, not degrees. Use [rad](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Rad) to convert fromdegrees if that's easier to think about. The rotation pivot is the text sprite's origin,which you can change with `set text offset`. Forrotation around the center of the text, set the offset to `(0.5, 0.5)` first.This sets an absolute angle. It doesn't accumulate, so calling it with the same valuetwice has no additional effect.

---

### set text offset

Sets the origin (pivot point) of a text sprite as a ratio of its measured size.

`(0, 0)` is the top-left corner, `(0.5, 0.5)` is the center, and `(1, 1)` is the bottom-right.

**Parameters**

- `Integer` **textId** - The text sprite ID.
- `Float` **xRatio** - Horizontal origin as a ratio. `0` = left edge, `0.5` = center, `1` = right edge.
- `Float` **yRatio** - Vertical origin as a ratio. `0` = top edge, `0.5` = center, `1` = bottom edge.

**Examples**

Center the text origin so it draws centered on its position.
```
font 1, "Fonts/Arial"
text 1, 400, 300, 1, "Centered!"
` set origin to the center of the text
set text offset 1, 0.5, 0.5
DO
sync
LOOP
```

**Remarks**

The origin affects where the text sprite "anchors" to its position. By default it's`(0, 0)` (top-left), which means the position you set with`set text position` corresponds to the top-left cornerof the text. Setting it to `(0.5, 0.5)` centers the text on that position, whichis usually what you want for rotation (via `rotate text`)or for centering text in a UI element. The origin also serves as the pivot for scaling.

---

### text x

Returns the current X position of a text sprite.

This is the raw position value, not accounting for transform attachment or origin offset.

**Parameters**

- `Integer` **textId** - The text sprite ID.

**Returns** `Float` - The X position in pixels.

**Examples**

Read back the X position of a text sprite.
```
font 1, "Fonts/Arial"
text 1, 150, 80, 1, "Hello"
xPos = text x(1)
print "Text X is: " + str(xPos)
```

**Remarks**

Returns the X component of the position last set by [text](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Text) or`set text position`. If the text sprite is attached toa transform, this still returns the local position, not the final on-screen position.Use this together with `text y` to read back both coordinates.

---

### text y

Returns the current Y position of a text sprite.

This is the raw position value, not accounting for transform attachment or origin offset.

**Parameters**

- `Integer` **textId** - The text sprite ID.

**Returns** `Float` - The Y position in pixels.

**Examples**

Read back the Y position of a text sprite.
```
font 1, "Fonts/Arial"
text 1, 150, 80, 1, "Hello"
yPos = text y(1)
print "Text Y is: " + str(yPos)
```

**Remarks**

Returns the Y component of the position last set by [text](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Text) or`set text position`. If the text sprite is attached toa transform, this still returns the local position, not the final on-screen position.Use this together with `text x` to read back both coordinates.

---

### font

Loads a font from the content pipeline and assigns it to the given ID.

Call this during setup before you try to render any text. You cannot createa [text](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Text) sprite without a loaded font.

**Parameters**

- `Integer` **fontId** - The ID to assign to this font.
- `String` **filePath** - Content path to the font asset, relative to the Content directory (no extension needed).

**Examples**

Load a font and create a text sprite with it:
```
` load a font and display a greeting
font 1, "Fonts/Arial"
text 1, 100, 50, 1, "Hello World!"
```

Load multiple fonts for different UI elements:
```
` load a heading font and a body font
font 1, "Fonts/TitleFont"
font 2, "Fonts/BodyFont"
 ` use the title font for the game name
text 1, 200, 50, 1, "My Game"
scale text 1, 2.0, 2.0
 ` use the body font for instructions
text 2, 200, 120, 2, "Press space to start"
```

**Remarks**

Fonts are the first thing you need if you want to draw any text on screen. Loadone here, then pass its ID to [text](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Text) when you create a textsprite. You only need to load a font once; after that, any number of text spritescan share the same font ID. The content path is relative to the Content directory and doesn't need a fileextension. So if your font lives at `Content/Fonts/Arial`, just pass`"Fonts/Arial"`.

---

### free texture id

Gets the next available texture ID without reserving it.

The returned ID is not claimed, so another call could grab it before youuse it. If you need a guaranteed slot, use`reserve texture id` instead.

**Parameters**

- `Integer` _(ref)_ **textureId** - Receives the next free texture ID.

**Returns** `Integer` - The next available texture ID. Not yet reserved, just a peek at what is next.

**Examples**

Peek at the next available texture ID:
```
` check what texture ID would be assigned next
nextId = free texture id(nextId)
print nextId
```

**Remarks**

This is handy when you want to peek at what ID is available next without actuallycommitting to it. A common use is to check the next ID for bookkeeping or loggingbefore deciding whether to load a texture. If you plan to actually load something into that slot, prefer`reserve texture id`. It calls thisinternally and then initializes the slot so nothing else can steal the ID outfrom under you.

---

### reserve texture id

Reserves the next available texture ID and initializes its slot.

Unlike `free texture id`, thisactually claims the ID so it will not be handed out again.

**Parameters**

- `Integer` _(ref)_ **textureId** - Receives the reserved texture ID.

**Returns** `Integer` - The newly reserved texture ID, ready to be used.

**Examples**

Reserve a texture ID for later use with a render target:
```
` reserve a texture slot before setting up a render target
texId = reserve texture id(texId)
render target 1, 256, 256
render target texture 1, texId
```

**Remarks**

Use this when you need a texture slot ready before you fill it. For example,when you are about to set up a `render target texture`that writes into a texture, or any other workflow where you need the ID allocatedahead of time. Under the hood, this calls `free texture id`to find the next open slot and then immediately initializes it. After this call,the ID is yours and will not be reused by other texture commands.

---

### texture

Loads a texture from the content pipeline and assigns it to the given ID.

This is the main way to get images into Fade. Once loaded, you can assignthe texture to a [sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite), split it into frames, or queryits dimensions.

**Parameters**

- `Integer` **textureId** - The ID to assign to this texture. Must be unique; loading over an existing ID replaces it.
- `String` **filePath** - Content path to the texture asset, relative to the Content directory (no extension needed).

**Examples**

Load a texture and display it as a sprite:
```
` load a player texture and create a sprite with it
texture 1, "Images/Player"
sprite 1, 100, 100, 1
```

Load a spritesheet texture and set up animation frames:
```
` load a character spritesheet and split it into a 4x2 grid
texture 1, "Images/CharacterSheet"
set texture frame grid 1, 2, 4
 ` create a sprite and show frame 0
sprite 1, 100, 100, 1
set sprite frame 1, 0
```

**Remarks**

Textures are the raw image data that sprites display. You load one here, thenreference it by ID when creating a [sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite). Multiplesprites can share the same texture, which is great for things like particle effectsor tiled backgrounds. The content path is relative to the Content directory and doesn't need a fileextension. If you want to use the texture as a spritesheet, load it first and thencall `set texture frame grid` to carveit into frames. You can also query the loaded texture's size with`texture width` and`texture height`, which is useful for thingslike scaling sprites with `size sprite`.

---

### set texture frame grid

Splits a texture into a grid of frames for spritesheet animation.

Each cell in the grid becomes a separate frame you can select with`set sprite frame`. Frames are numbered left-to-right,top-to-bottom, starting at `0`.

**Parameters**

- `Integer` **textureId** - The ID of the texture to split. Must already be loaded with `texture`.
- `Integer` **rows** - Number of rows in the grid. Must be at least `1`.
- `Integer` **columns** - Number of columns in the grid. Must be at least `1`.

**Examples**

Set up a 4x2 spritesheet and animate it in a loop:
```
` load a spritesheet and split it into frames
texture 1, "Images/RunCycle"
set texture frame grid 1, 2, 4
 ` create the sprite
sprite 1, 100, 100, 1
 ` animate through frames in the game loop
frame = 0
totalFrames = texture frames(1)
set sync rate 16
DO
set sprite frame 1, frame
frame = frame + 1
IF frame >= totalFrames THEN frame = 0
sync
LOOP
```

**Remarks**

This is how you turn a single spritesheet image into an animation-ready texture.Say you have a character sheet that is 4 columns wide and 2 rows tall. Call thiswith rows `2` and columns `4`, and you will get 8 frames numbered `0`through `7`. The texture must already be loaded with `texture` beforeyou call this. The command divides the texture evenly, so make sure your spritesheethas uniform cell sizes. If the texture dimensions do not divide evenly by the rowand column count, you will get frames that clip into neighboring cells. After setting up frames, use `set sprite frame` onany sprite using this texture to pick which frame to display. You can check how manyframes a texture has with `texture frames`.

---

### texture frames

Returns the total number of frames in a texture's frame grid.

Only meaningful after you have called`set texture frame grid` on the texture.

**Parameters**

- `Integer` **textureId** - The ID of the texture to check. Must already be loaded with `texture`.

**Returns** `Integer` - The number of frames in the texture's frame grid.

**Examples**

Use the frame count to loop an animation:
```
` load a spritesheet and get the total frame count
texture 1, "Images/Explosion"
set texture frame grid 1, 4, 4
totalFrames = texture frames(1)
 ` cycle through all frames
frame = 0
set sync rate 16
DO
set sprite frame 1, frame
frame = frame + 1
IF frame >= totalFrames THEN frame = 0
sync
LOOP
```

**Remarks**

This tells you how many frames are available for animation on a given texture.It is useful when you are cycling through frames and need to know when to wrapback to `0`. For example, you might set the sprite frame to`currentFrame mod textureFrames` each tick. If you have not called `set texture frame grid`on this texture yet, the frame count will not reflect a grid layout.

---

### texture width

Returns the width of a texture in pixels.

**Parameters**

- `Integer` **textureId** - The ID of the texture to measure. Must already be loaded with `texture`.

**Returns** `Integer` - The width of the texture in pixels.

**Examples**

Size a sprite to match its texture dimensions:
```
` load a texture and size the sprite to match
texture 1, "Images/Logo"
sprite 1, 100, 100, 1
w = texture width(1)
h = texture height(1)
size sprite 1, w, h
```

**Remarks**

Handy when you need to know a texture's dimensions for layout or scaling. Forexample, you might use this alongside `texture height`to size a [sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite) to match its texture exactly, or tocalculate a custom aspect ratio. You can also grab the pre-calculated ratio directly with`texture aspect` if that is all you need.

---

### texture height

Returns the height of a texture in pixels.

**Parameters**

- `Integer` **textureId** - The ID of the texture to measure. Must already be loaded with `texture`.

**Returns** `Integer` - The height of the texture in pixels.

**Examples**

Use texture height to center a sprite vertically on screen:
```
` load a texture and center the sprite vertically
texture 1, "Images/Banner"
sprite 1, 0, 0, 1
h = texture height(1)
screenH = screen height()
yPos = (screenH - h) / 2
position sprite 1, 0, yPos
```

**Remarks**

Use this when you need to know a texture's vertical size for layout or scaling.Pair it with `texture width` to get the fulldimensions, or use `texture aspect` if youjust need the ratio. This is particularly useful when you want to scale a sprite proportionally.For instance, use `size sprite x` to setthe width and let it calculate the height from the aspect ratio.

---

### texture aspect

Returns the aspect ratio of a texture, calculated as height divided by width.

A value greater than `1.0` means the texture is taller than it is wide.Less than `1.0` means it is wider than it is tall.

**Parameters**

- `Integer` **textureId** - The ID of the texture to measure. Must already be loaded with `texture`.

**Returns** `Float` - The height-to-width ratio as a decimal. For example, a 200x100 texture returns `2.0` and a 100x200 texture returns `0.5`.

**Examples**

Scale a sprite to a target width while preserving proportions:
```
` load a texture and scale the sprite proportionally
texture 1, "Images/Portrait"
sprite 1, 50, 50, 1
 ` set a target width and compute the matching height
targetW = 200
aspect = texture aspect(1)
targetH = targetW * aspect
size sprite 1, targetW, targetH
```

**Remarks**

This saves you from doing the division yourself when you need to scale thingsproportionally. A common pattern is to set a sprite's width to some target sizeand then multiply by the aspect ratio to get the matching height, keeping theimage from looking stretched. If you need the raw pixel dimensions instead, use`texture width` and`texture height`.

---

### free transform id

Peeks at the next available transform ID without claiming it.

This doesn't reserve the ID, so another call could grab it before you do.

**Parameters**

- `Integer` _(ref)_ **transformId** - Receives the next free transform ID.

**Returns** `Integer` - The next available transform ID (not yet reserved).

**Examples**

Peek at the next ID to size an array, then reserve and create transforms.
```
` find out what the next ID will be
nextId = free transform id()
print nextId
```

**Remarks**

Most of the time you'll want `reserve transform id`instead, which actually claims the slot. This one is handy if you just need to know whatthe next ID would be, for example to pre-allocate an array. If you already know yourID, skip both of these and call `transform` directly.

---

### reserve transform id

Claims the next available transform ID and initializes its slot.

The slot is created but the transform won't affect anything until you set itsposition with `transform` or`set transform position`.

**Parameters**

- `Integer` _(ref)_ **transformId** - Receives the reserved transform ID.

**Returns** `Integer` - The newly reserved transform ID.

**Examples**

Reserve IDs for a batch of enemies, then create their transforms.
```
` reserve five enemy transform IDs
FOR i = 1 TO 5
id = reserve transform id()
transform id, i * 64, 100
NEXT i
```

**Remarks**

Use this when you need to wire up references to a transform before it's fullyconfigured. The typical pattern is: reserve an ID, then call`transform` to place it. If you don't need thatsetup step, just call `transform` directly with aknown ID. See also `free transform id` ifyou only need to peek without claiming.

---

### transform

Creates a transform at the given position.

Transforms are the backbone of Fade's scene hierarchy. They let you groupsprites, text, and colliders so they all move, rotate, and scale together.

**Parameters**

- `Integer` **transformId** - The ID to assign to this transform.
- `Float` **x** - The starting X position.
- `Float` **y** - The starting Y position.

**Examples**

Create a full game entity with a transform, sprite, and collider.
```
` build a player entity at the center of the screen
playerId = 1
transform playerId, 320, 240
 ` attach a sprite and a collider
sprite playerId, 0, 0
attach sprite to transform playerId, playerId
box collider playerId, -16, -16, 32, 32
attach collider to transform playerId, playerId
```

Create a parent transform and a child that follows it.
```
` create a ship and an orbiting shield
shipId = 1
shieldId = 2
transform shipId, 320, 240
transform shieldId, 30, 0
set transform parent shieldId, shipId
 ` moving the ship moves the shield too
set transform position shipId, 400, 240
```

**Remarks**

This is usually one of the first things you create for a game entity. The typicalpattern looks like this: create a transform here, create a sprite with[sprite](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Sprite) and attach it via`attach sprite to transform`, create acollider with `box collider` and attach it via`attach collider to transform`. Now movingthe transform with `set transform position`moves everything together. Transforms can also be parented to other transforms with`set transform parent`, forming a hierarchy wherechildren inherit their parent's position, rotation, and scale.

---

### set transform position

Sets the position of a transform.

If this transform has children (sprites, colliders, or other transforms parentedto it), they all move with it.

**Parameters**

- `Integer` **transformId** - The ID of the transform.
- `Float` **x** - The new X position.
- `Float` **y** - The new Y position.

**Examples**

Move a player to the right each frame.
```
` set up the player
playerId = 1
transform playerId, 0, 240
px = 0
 set sync rate 16
DO
px = px + 2
set transform position playerId, px, 240
sync
LOOP
```

**Remarks**

Call this every frame for transforms that move, or once for static ones. This isthe main way you drive game object movement. Move the transform, and everythingattached to it follows. The position is local to the transform's parent (if it has one via`set transform parent`). If there's no parent,the position is in screen coordinates. You can read the position back with`get local transform x` and`get local transform y`.

---

### get local transform x

Returns the local X position of a transform.

This is the position relative to the transform's parent, not its final worldposition. If the transform has no parent, local and world are the same thing.

**Parameters**

- `Integer` **transformId** - The ID of the transform.

**Returns** `Float` - The local X position.

**Examples**

Read the player's X position and print it each frame.
```
` track the player's horizontal position
playerId = 1
transform playerId, 100, 200
 set sync rate 16
DO
px = get local transform x(playerId)
print px
sync
LOOP
```

**Remarks**

Use this to read back whatever you set with`set transform position`. If the transform isparented via `set transform parent`, this returnsthe offset from the parent, not the on-screen position. Pairs with`get local transform y`.

---

### get local transform y

Returns the local Y position of a transform.

This is the position relative to the transform's parent, not its final worldposition. If the transform has no parent, local and world are the same thing.

**Parameters**

- `Integer` **transformId** - The ID of the transform.

**Returns** `Float` - The local Y position.

**Examples**

Read both X and Y to compute distance from origin.
```
` check how far the player is from the top-left corner
playerId = 1
px = get local transform x(playerId)
py = get local transform y(playerId)
dist = sqrt(px * px + py * py)
print dist
```

**Remarks**

Use this to read back whatever you set with`set transform position`. If the transform isparented via `set transform parent`, this returnsthe offset from the parent, not the on-screen position. Pairs with`get local transform x`.

---

### get local transform scale x

Returns the local X scale of a transform.

A value of `1.0` is the default (no scaling). This does not account forparent scaling; it is just what you set on this transform.

**Parameters**

- `Integer` **transformId** - The ID of the transform.

**Returns** `Float` - The X scale factor. `1.0` is the default.

**Examples**

Check if a transform has been flipped horizontally.
```
` read the X scale to see if the entity is facing left
sx = get local transform scale x(playerId)
IF sx < 0 THEN
print "facing left"
ENDIF
```

**Remarks**

Reads back the X component of whatever you set with`set transform scale`. Pairs with`get local transform scale y`.

---

### get local transform scale y

Returns the local Y scale of a transform.

A value of `1.0` is the default (no scaling). This does not account forparent scaling; it is just what you set on this transform.

**Parameters**

- `Integer` **transformId** - The ID of the transform.

**Returns** `Float` - The Y scale factor. `1.0` is the default.

**Examples**

Read both scale axes and print them.
```
` inspect the current scale of an entity
sx = get local transform scale x(entityId)
sy = get local transform scale y(entityId)
print sx
print sy
```

**Remarks**

Reads back the Y component of whatever you set with`set transform scale`. Pairs with`get local transform scale x`.

---

### set transform scale

Sets the scale of a transform on the X and Y axes.

A scale of `1.0` is the default. Children attached to this transform(sprites, text, colliders, and child transforms) inherit the scaling.

**Parameters**

- `Integer` **transformId** - The ID of the transform.
- `Float` **x** - The X scale factor. `1.0` is no change, `2.0` is double size.
- `Float` **y** - The Y scale factor. `1.0` is no change, `2.0` is double size.

**Examples**

Double the size of an entity uniformly.
```
` make the boss twice as big
bossId = 10
transform bossId, 320, 240
set transform scale bossId, 2.0, 2.0
```

Flip a character horizontally when they change direction.
```
` flip the sprite to face left by using negative X scale
set transform scale playerId, -1.0, 1.0
```

**Remarks**

Use this to grow or shrink everything attached to a transform at once. Pass thesame value for both axes for uniform scaling, or different values to stretch.Negative values will flip the attached sprites. You can read the scale back with`get local transform scale x` and`get local transform scale y`.

---

### set transform rotation

Sets the rotation of a transform in radians.

Children attached to this transform inherit the rotation, so rotating a parentspins everything attached to it.

**Parameters**

- `Integer` **transformId** - The ID of the transform.
- `Float` **angle** - The rotation angle in radians. Use [rad](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Rad) to convert from degrees.

**Examples**

Spin an entity continuously each frame.
```
` rotate a spinning coin
coinId = 3
transform coinId, 320, 240
angle = 0.0
 set sync rate 16
DO
angle = angle + 0.05
set transform rotation coinId, angle
sync
LOOP
```

Set a fixed rotation using degrees.
```
` tilt an entity 45 degrees
set transform rotation entityId, rad(45)
```

**Remarks**

If you're working in degrees, convert with [rad](/command/Fade.MonoGame.Lib.FadeMonoGameCommands/Rad) first. A fullrotation is roughly `6.283` radians (2*pi). The rotation applies around thetransform's position, which acts as the pivot point. This is the transform-level rotation. Individual sprites can also have their ownrotation via `rotate sprite`, which stacks on top ofwhatever the transform is doing.

---

### set transform parent

Parents a transform to another transform.

The child inherits the parent's position, rotation, and scale. The child's ownvalues become relative to the parent rather than the screen.

**Parameters**

- `Integer` **transformId** - The ID of the child transform.
- `Integer` **parentTransformId** - The ID of the parent transform to attach to.

**Examples**

Create a character with a weapon that follows it.
```
` set up a character and a weapon
charId = 1
weaponId = 2
transform charId, 200, 300
transform weaponId, 20, -10
 ` parent the weapon to the character
set transform parent weaponId, charId
 ` now moving the character moves the weapon too
set sync rate 16
cx = 200
DO
cx = cx + 1
set transform position charId, cx, 300
sync
LOOP
```

Build a three-level hierarchy: ship, turret, and barrel.
```
` the barrel is offset from the turret, which is offset from the ship
shipId = 1
turretId = 2
barrelId = 3
transform shipId, 320, 400
transform turretId, 0, -20
transform barrelId, 10, -15
 set transform parent turretId, shipId
set transform parent barrelId, turretId
 ` rotating the ship rotates everything
set transform rotation shipId, rad(30)
```

**Remarks**

This is how you build a scene hierarchy. For example, you might parent a weapontransform to a character transform. Moving the character automatically moves theweapon, and the weapon's position becomes an offset from the character. Re-parenting is supported: calling this on a transform that already has a parentdetaches it from the old parent and attaches to the new one. The system managesreference counts internally. The local getters (`get local transform x`,`get local transform y`) return the positionrelative to the parent, not the final on-screen position.

---

### free tween id

Peeks at the next available tween ID without claiming it.

This doesn't reserve the ID, so another call could grab it before you do.

**Parameters**

- `Integer` _(ref)_ **tweenId** - Receives the next free tween ID.

**Returns** `Integer` - The next available tween ID (not yet reserved).

**Examples**

Peek at the next tween ID before deciding whether to create one.
```
` check what the next tween ID would be
nextId = free tween id()
print nextId
```

**Remarks**

Most of the time you'll want `reserve tween id`instead, which actually claims the slot. This one is handy if you just need to knowwhat the next ID would be. If you already know your ID, skip both of these and call`create basic tween` directly.

---

### reserve tween id

Claims the next available tween ID and initializes its slot.

The slot is created but the tween won't start until you call`create basic tween` to configure it.

**Parameters**

- `Integer` _(ref)_ **tweenId** - Receives the reserved tween ID.

**Returns** `Integer` - The newly reserved tween ID.

**Examples**

Reserve tween IDs for a staggered animation sequence.
```
` reserve three tween IDs for a multi-part intro
t1 = reserve tween id()
t2 = reserve tween id()
t3 = reserve tween id()
 ` now configure them with staggered delays
create basic tween t1, 0, 255, 500, 0
create basic tween t2, 0, 255, 500, 200
create basic tween t3, 0, 255, 500, 400
```

**Remarks**

Use this when you need to set up a tween ID ahead of time, for example to storeit in an array before configuring the actual tween. If you don't need that setupstep, just call `create basic tween` directly with aknown ID. See also `free tween id` if you onlyneed to peek without claiming.

---

### create basic tween

Creates a tween that smoothly interpolates a value from start to end over a duration.

Defaults to cubic ease-in-out. Change the curve with`set tween easing` after creation.

**Parameters**

- `Integer` **tweenId** - The ID to assign to this tween.
- `Float` **start** - The starting value.
- `Float` **end** - The ending value.
- `Float` **duration** - How long the tween takes, in milliseconds.
- `Float` **delay** - How long to wait before starting, in milliseconds. Pass `0` to start immediately.

**Examples**

Slide a sprite from left to right over one second.
```
` tween the X position from 0 to 640 in 1000ms
tweenId = 1
spriteId = 1
create basic tween tweenId, 0, 640, 1000, 0
 set sync rate 16
DO
x = tweenVal(tweenId)
set transform position spriteId, x, 240
sync
LOOP
```

Fade in a sprite's alpha after a half-second delay.
```
` fade alpha from 0 to 255 over 800ms, starting after 500ms
tweenId = 2
create basic tween tweenId, 0, 255, 800, 500
 set sync rate 16
DO
a = tweenVal(tweenId)
set sprite alpha spriteId, a
sync
LOOP
```

**Remarks**

This is the main entry point for Fade's tween system. Tweens run on real time(milliseconds), not frame counts, so they're smooth regardless of frame rate. Thesystem updates them automatically each frame. The typical pattern is: create a tween, then each frame read its current value with`tweenVal` and use that to drive a position, alpha,scale, or anything else you want to animate. Check`is tween done` to know when it's finished. By default a tween plays once and stops. Use`set tween type` to make it loop or ping-pong.

---

### set tween easing

Sets the easing function for a tween.

Call this right after `create basic tween` tooverride the default cubic ease-in-out.

**Parameters**

- `Integer` **tweenId** - The ID of the tween.
- `Integer` **easingType** - The easing curve. Common values include linear, ease-in, ease-out, and cubic variants.

**Examples**

Create a tween with a linear easing so it moves at constant speed.
```
` slide a sprite at constant speed
tweenId = 1
create basic tween tweenId, 0, 640, 2000, 0
set tween easing tweenId, 0
```

**Remarks**

The easing type controls the shape of the interpolation curve, whether the tweenstarts slow and speeds up (ease-in), starts fast and slows down (ease-out), orsomething else entirely. If you don't call this, the tween uses cubic ease-in-out, which is a safe defaultfor most UI and game animations.

---

### set tween type

Sets the execution behavior of a tween (play once, loop, ping-pong, etc.).

By default tweens play once and stop. Call this right after`create basic tween` to change that.

**Parameters**

- `Integer` **tweenId** - The ID of the tween.
- `Integer` **type** - The execution type. Common values: once, loop, ping-pong.

**Examples**

Make a sprite bob up and down forever with a ping-pong tween.
```
` bob between y=200 and y=240 over 1 second, repeating forever
tweenId = 1
spriteId = 1
create basic tween tweenId, 200, 240, 1000, 0
set tween type tweenId, 2
 set sync rate 16
DO
y = tweenVal(tweenId)
set transform position spriteId, 320, y
sync
LOOP
```

**Remarks**

A looping tween repeats from start to end indefinitely. A ping-pong tween bouncesback and forth between start and end. These are useful for ambient animations likebobbing, pulsing, or breathing effects. Note that `is tween done` will never return`1` for a looping or ping-pong tween, since they never finish.

---

### tweenVal

Returns the current interpolated value of a tween.

This is the main output of the tween system, the number that smoothly movesfrom start to end according to the easing curve.

**Parameters**

- `Integer` **tweenId** - The ID of the tween.

**Returns** `Float` - The current tweened value, between start and end.

**Examples**

Use a tween to animate a transform's X position.
```
` smoothly slide an entity from x=50 to x=500
tweenId = 1
entityId = 1
transform entityId, 50, 300
create basic tween tweenId, 50, 500, 1500, 0
 set sync rate 16
DO
x = tweenVal(tweenId)
set transform position entityId, x, 300
sync
LOOP
```

Animate scale using two tweens at once.
```
` grow an entity from half-size to full-size
tweenX = 1
tweenY = 2
create basic tween tweenX, 0.5, 1.0, 600, 0
create basic tween tweenY, 0.5, 1.0, 600, 0
 set sync rate 16
DO
sx = tweenVal(tweenX)
sy = tweenVal(tweenY)
set transform scale entityId, sx, sy
sync
LOOP
```

**Remarks**

Read this every frame to drive your animation. If you created a tween from `0`to `100`, this will smoothly return values between 0 and 100 as the tweenprogresses. Feed this into `set transform position`,`set sprite alpha`, or anything else youwant to animate. If you need the raw 0-to-1 progress instead of the interpolated value, use`tweenRatio`.

---

### tweenRatio

Returns the raw progress ratio of a tween, from `0` (just started) to `1` (finished).

Unlike `tweenVal`, this gives you theun-interpolated progress, useful when you want to drive your own math.

**Parameters**

- `Integer` **tweenId** - The ID of the tween.

**Returns** `Float` - The progress ratio, from `0.0` (just started) to `1.0` (finished).

**Examples**

Use the ratio to blend between two colors manually.
```
` blend from red to blue using the raw ratio
tweenId = 1
create basic tween tweenId, 0, 1, 2000, 0
 set sync rate 16
DO
r = tweenRatio(tweenId)
red = 255 * (1.0 - r)
blue = 255 * r
sync
LOOP
```

**Remarks**

Most of the time you'll want `tweenVal` instead, whichgives you the actual number between start and end. This is for cases where you needthe raw 0-to-1 ratio to feed into your own interpolation logic, for exampleblending between two colors or computing a custom curve.

---

### is tween done

Returns `1` if a tween has finished playing.

A tween is "done" when its progress ratio reaches `1` or beyond. Loopingand ping-pong tweens never finish.

**Parameters**

- `Integer` **tweenId** - The ID of the tween.

**Returns** `Boolean` - `1` if the tween's progress ratio has reached `1` or beyond.

**Examples**

Wait for a slide-in to finish, then print a message.
```
` slide a title in from the left
tweenId = 1
create basic tween tweenId, -200, 320, 1000, 0
 set sync rate 16
DO
x = tweenVal(tweenId)
set transform position titleId, x, 100
   done = is tween done(tweenId)
IF done = 1 THEN
print "title is in place!"
ENDIF
   sync
LOOP
```

**Remarks**

Use this to sequence actions after a tween completes, for example destroying anentity after its fade-out finishes, or starting the next animation in a chain. If you need to wait for several tweens at once, use`any tweens running` instead of checking eachone individually.

---

### any tweens running

Checks if any of the given tweens are still running.

Returns `1` if at least one tween in the list hasn't finished yet.Returns `0` only when every tween is done.

**Parameters**

- `Integer` **tweenIds** - One or more tween IDs to check.

**Returns** `Boolean` - `1` if at least one tween is still running, `0` if all are done.

**Examples**

Wait for all UI tweens to finish before showing a menu.
```
` kick off three staggered fade-in tweens
t1 = 1
t2 = 2
t3 = 3
create basic tween t1, 0, 255, 400, 0
create basic tween t2, 0, 255, 400, 150
create basic tween t3, 0, 255, 400, 300
 ` wait until all three are done
set sync rate 16
DO
running = any tweens running(t1, t2, t3)
IF running = 0 THEN
print "all animations finished!"
ENDIF
sync
LOOP
```

**Remarks**

This is the batch version of `is tween done`.Instead of checking each tween individually, pass them all in and get a singleanswer. Common use case: you've kicked off several tweens to animate a UI transition,and you want to wait until they're all finished before proceeding. Since this returns `1` while tweens are still going, you'd typically use itin a loop condition: keep calling `sync` while`any tweens running` is true.

---

