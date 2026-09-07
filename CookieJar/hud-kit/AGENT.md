# HUD Kit

A stat panel, a message line and a pause overlay — in screen space, with real text.

## Why this exists

Until the `UI` globals landed, a prototype's HUD had to be **world-space sprites following the
player**, with no text of any kind: the scripting contract had no viewport, so a script could not
find the screen edge, and no way to draw a glyph. Jake01's original HUD was five coloured
rectangles floating over the survivor's head, and you could not tell which was hunger and which
was thirst.

This is what replaces that.

## Wiring it up

Put `Hud.js` on an empty actor, tag it `Hud`, and say what the bars are:

```js
var title = "JAKE01";
var bars = [
    { label: "HP",  source: "getHealth01",  colour: "#c63832" },
    { label: "STA", source: "getStamina01", colour: "#d8c88a" },
    { label: "FOOD", source: "getHunger01", colour: "#cf8a3c" },
];
```

Each `source` is a function on the **player's** script returning 0..1:

```js
function getHealth01() { return health / healthMax; }
```

That is the whole coupling: one string per bar. Numbers cross the script boundary identically on
both engines, which objects do not reliably do — so getters that return a plain number are what
to write, not one returning a `{hp, stamina}` object.

## The rest of it

```js
var hud = Scene.findFirstByTag("Hud").getComponent("ScriptComponent");

hud.call("say", "Something moved in the dark.");     // one line, bottom centre, fades
hud.call("setPaused", true, "PAUSED", "Esc to resume");
hud.call("setPaused", true, "YOU DIED", "R to start again");
hud.call("setPaused", false);
hud.call("isPaused");                                 // 1 or 0
```

The overlay is a panel sized to `UI.width` × `UI.height`. There is no "full screen" flag in the
contract, but there is now a viewport, which is the same thing said plainly.

## Tuning

`anchor` (`topleft`) puts the panel in any corner. `panelWidth`, `rowHeight`, `labelWidth`,
`textScale` and `titleScale` are the shape; `panelColour`, `trackColour`, `textColour` and
`dimColour` are the palette. `messageSeconds` is how long `say()` lingers.

Text scale is in whole multiples of a 5×7 cell — `scale: 2` is ten pixels tall. Fractional scales
are rounded, because a bitmap font at 1.5× is mush.

## What it does not do

- **No input handling.** The pause overlay draws; deciding that Esc pauses is your script's job.
- **No layout engine.** Rows stack at a fixed height. Two columns, or a bar that grows with its
  label, is a copy of this file with different arithmetic, not a flag.
- **One player.** Split-screen wants one of these per viewport, and the contract has one viewport.
- It calls `UI.clear()` in `onDestroy`, which drops **this script's** elements only — so a scene
  change cannot leave the last scene's HUD on screen, and cannot take another script's with it.
