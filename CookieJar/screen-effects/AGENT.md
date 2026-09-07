# Screen Effects

Shake, flash, fade and hit-stop. The four cheapest things that change how a hit feels.

## Wiring it up

Put `Effects.js` on an empty actor and tag it `Effects`. Then, from anywhere:

```js
var fx = Scene.findFirstByTag("Effects").getComponent("ScriptComponent");

fx.call("shake", 14);                // pixels: 6 a footstep, 14 a hit, 20+ an explosion
fx.call("flash", "#ffffff", 0.12);   // white when you are hit, red when you land one
fx.call("hitStop", 0.06);            // freeze everything for a moment
fx.call("impact", 18);               // all three, for the frame a boss dies
fx.call("fadeTo", 0.6);              // cover the screen before Scene.load
fx.call("fadeFrom", 0.6);            // and reveal it in the next scene's onStart
```

## The two things that make it work rather than fight

**It runs in `onLateUpdate`.** A camera-follow script writes the camera position every frame; if
the shake ran in `onUpdate` the two would take turns and the result would be a stutter rather than
a shake. Late means the follow has already placed the camera and the shake is an offset on top.

**It takes back last frame's offset before adding this frame's.** Without that the camera does a
random walk away from the thing it is meant to be following, slowly, over about ten seconds — the
kind of bug you notice as "the camera drifts" long after you stop suspecting the shake.

## Colours are 8-digit hex, on purpose

`withAlpha` produces `#rrggbbaa`, not `rgba(...)`. Both engines parse eight-digit hex — the C#
side takes 6 or 8 digits and CSS Color 4 takes the same — whereas `rgba()` is browser-only. A
flash written that way fades to nothing natively and is invisible in exactly the build nobody
looks at.

## Tuning

`shakeDecay` (5.0) is how fast it settles; higher is snappier. `maxShake` (40) is a ceiling so a
bug in a damage number cannot throw the camera off the map. `flashSeconds` (0.12) and
`fadeSeconds` (0.6) are the defaults each call can override.

Hit-stop counts down on `Time.unscaledDeltaTime`. It has to: it sets `Time.timeScale = 0`, and a
freeze timed on scaled time can never time itself out.

## What it does not do

- **No easing curves.** Shake decays exponentially and fades are linear. Anything richer is a
  tween library, which the engine has and a script cannot reach.
- **No directional shake.** It is random in both axes. A recoil that kicks away from the impact is
  a few lines more, and worth it for a shooter.
- **It owns `Time.timeScale` while a hit-stop is running.** If something else writes that in the
  same window, the last writer wins and the freeze never lifts.
- The flash and fade are full-screen UI panels, so they sit above the world and below nothing.
  Draw order between them and a HUD is the order the scripts start in.
