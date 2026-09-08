# Tutorial 7 — UI & Menus

**You will build:** a main menu, an in-game HUD, a pause screen and an options panel with
working sliders. **Time:** ~25 minutes.

Builds on [Tutorial 6](06-physics-platformer.md).

---

## 1. Two facts before you start

**Nothing needs an asset.** Text comes from a 5×7 glyph table the engine embeds, and a panel
is a colour, not a texture. A UI you write in the next five minutes will be visible.

**Layout does the arithmetic.** You say `row`, `column`, `gap` and `grow`; the engine works out
where things go. You will not add up glyph heights to place a label, and you will not resize a
full-screen panel every frame — `width: "*"` already means "as wide as the parent".

---

## 2. A canvas

A `UiCanvas` is a `Component`. Attach one and it is painted, in screen space, after the world.

```csharp
var ui = new Actor("UI");
var canvas = ui.AddComponent<UiCanvas>();

canvas.ScaleMode = UiScaleMode.Match;                       // author at one size, run at any
canvas.ReferenceResolution = new Vector2(1920f, 1080f);

scene.AddActor(ui, "ui");
```

`ScaleMode` is `ConstantPixel` (one unit is one pixel), `ScaleToFit` (letterboxed),
`ScaleToFill` (cropped) or `Match` (blends the two ratios — the usual choice for a HUD).

---

## 3. A main menu

Describe it, rather than assembling it:

```csharp
canvas.Adopt(UiDocument.FromJson("""
{
  "layout": "column", "mainAlign": "center", "crossAlign": "center", "gap": 24,
  "width": "*", "height": "*",
  "children": [
    { "kind": "label", "text": "MY GAME", "scale": 4, "tint": "#ffdc50" },
    {
      "name": "menu", "width": 400, "background": "#0000008c",
      "layout": "column", "gap": 8, "padding": 12, "crossAlign": "stretch",
      "children": [
        { "name": "play",    "kind": "button", "text": "Play",    "height": 56,
          "background": "#ffffff14", "align": "center", "autoFocus": true },
        { "name": "options", "kind": "button", "text": "Options", "height": 56,
          "background": "#ffffff14", "align": "center" },
        { "name": "quit",    "kind": "button", "text": "Quit",    "height": 56,
          "background": "#ffffff14", "align": "center" }
      ]
    }
  ]
}
"""));
```

`mainAlign: center` on a full-size column centres the lot vertically; `crossAlign: stretch`
inside the menu gives all three buttons one width without measuring the longest label.

### Reacting to a click

`Clicked` is true for the frame a press completed on a node — a flag, not a callback, because
the same tree has to work from a JavaScript game and a JS function held by C# marshals
differently on the two engines.

```csharp
UiNode play = canvas.Find("play")!;

protected override void Update(float dt)
{
    if (play.Clicked) SceneManager.LoadScene("Scenes/Level1");
}
```

Fifteen lines of `UiClicks` in `SexyBiscuit.Demo/UI/UiClicks.cs` put handler ergonomics back for
native games; copy it if you would rather write `clicks.On(play, () => ...)`.

### It already works on a pad

Any button is focusable and the engine walks between them spatially. `autoFocus` says which one
starts focused; the D-pad, the left stick and the arrow keys all move from there, and the focus
ring is drawn only when the player is actually using a directional device.

---

## 4. A HUD

```csharp
canvas.Adopt(UiDocument.FromJson("""
{
  "children": [
    {
      "absolute": true, "anchor": "topleft", "x": 20, "y": 20,
      "layout": "column", "gap": 6, "padding": 10, "background": "#161920e6",
      "children": [
        { "kind": "label", "text": "JAKE01", "scale": 2 },
        {
          "layout": "row", "gap": 6, "crossAlign": "center",
          "children": [
            { "kind": "label", "text": "HP", "width": 34, "tint": "#9a978f" },
            { "name": "hp", "kind": "bar", "grow": 1, "height": 8,
              "tint": "#c63832", "background": "#2a2d34" }
          ]
        }
      ]
    },
    { "kind": "label", "text": "v1.0.1", "tint": "#5a5f6a",
      "absolute": true, "anchor": "bottomright", "x": -12, "y": -12 }
  ]
}
"""));
```

Two things worth noticing:

- The panel has **no height**. It is `auto`, so it is exactly as tall as its rows. Add a row and
  it grows; you never touch a number.
- The bar has **`grow: 1`**, so it takes whatever the label leaves. Rename `HP` to `SHIELD` and
  the bar shortens by itself.

A bar's `Value` is a fraction from 0 to 1 — divide the maximum out yourself:

```csharp
canvas.Find("hp")!.Value = Math.Clamp(hp / (float)maxHp, 0f, 1f);
```

### Anchors

`absolute: true` takes a node out of the flow. The anchor is **both** where on the parent it
hangs and which of its own corners hangs there, so `bottomright` with `x: -12, y: -12` sits
twelve pixels in from the corner at any window size — the version badge above needs nothing else.

---

## 5. A pause screen

```csharp
UiNode pause = canvas.Root.Add(UiDocument.FromJson("""
{
  "name": "pause", "visible": false, "modal": true, "order": 100,
  "width": "*", "height": "*", "background": "#000000cc",
  "layout": "column", "mainAlign": "center", "crossAlign": "center", "gap": 16,
  "children": [
    { "kind": "label", "text": "PAUSED", "scale": 4 },
    { "name": "resume", "kind": "button", "text": "Resume", "width": 260, "height": 52,
      "background": "#ffffff14", "align": "center", "autoFocus": true },
    { "name": "toMenu", "kind": "button", "text": "Main Menu", "width": 260, "height": 52,
      "background": "#ffffff14", "align": "center" }
  ]
}
"""));
```

- `width: "*"` covers the screen and keeps covering it through a resize, a rotation or going
  fullscreen. There is no resize loop to write.
- `modal: true` traps focus inside it, so a pad cannot walk out of the pause menu into the HUD
  behind it — and a click outside it misses rather than pressing whatever it lands on.
- `order: 100` puts it in front.

Show it by setting `pause.Visible = true`.

---

## 6. Options with working sliders

A `Slider` carries `Value`, `MinValue`, `MaxValue` and `Step`, and the input router drags it for
you — including keeping the pointer if it slides off the track.

```csharp
canvas.Root.Add(UiDocument.FromJson("""
{
  "name": "options", "width": 520, "absolute": true, "anchor": "center",
  "layout": "column", "gap": 12, "padding": 16, "background": "#12141af0",
  "crossAlign": "stretch",
  "children": [
    { "kind": "label", "text": "OPTIONS", "scale": 2, "align": "center" },
    {
      "layout": "row", "gap": 10, "crossAlign": "center",
      "children": [
        { "kind": "label", "text": "Master", "width": 120 },
        { "name": "master", "kind": "slider", "grow": 1, "height": 24,
          "value": 0.8, "minValue": 0, "maxValue": 1, "step": 0.05 }
      ]
    },
    { "name": "fullscreen", "kind": "toggle", "text": "Fullscreen", "height": 32 }
  ]
}
"""));
```

Read them like anything else:

```csharp
AudioBus.Master.Volume = canvas.Find("master")!.Value;
if (canvas.Find("fullscreen")!.Clicked) ToggleFullscreen();
```

A `Toggle`'s state is `Checked`; a `Dropdown` uses `Options` and `SelectedIndex`.

---

## 7. Put it in a file instead

Everything above is JSON, so it does not have to live in a string literal. Save it as a `.ui`
file and point the canvas at it:

```csharp
canvas.Document = "UI/MainMenu.ui";     // loaded in Awake
```

The same document is what a JavaScript game passes to `UI.build({...})`, so a UI written once
runs in the browser and natively without being written twice. See
[11. Scripting](../wiki/11-scripting.md#ui--screen-space).

---

## What you have

A menu, a HUD, a pause screen and an options panel — no fonts, no textures, no layout
arithmetic, and all of it navigable with a mouse, a finger, a pad or a TV remote.

## Next

- [Tutorial 8 — Saving](08-saving.md)
- [9. UI](../wiki/09-ui.md) — the reference, including focus, clipping and the painter
