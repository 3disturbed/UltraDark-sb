# Tutorial 7 — UI & Menus

**You will build:** a main menu, an in-game HUD, a pause screen and an options
panel with working sliders. **Time:** ~40 minutes.

Builds on [Tutorial 6](06-physics-platformer.md).

---

## 1. Three facts before you start

**UI needs a font, and `AssetManager` cannot load one.** `Canvas.Font` is a
MonoGame `SpriteFont`, which comes from the content pipeline.

**Some widgets need a texture, some do not.** `Widget` provides `FillRect` /
`StrokeRect` helpers over a shared 1×1 pixel, and the newer widgets
(`ProgressBar`, `TextInput`, `Checkbox`, `Dropdown`, `ScrollView`, `TabView`)
use them. But `Panel.BackgroundColor` is only a *tint for `BackgroundTexture`*,
`Button` with no `NormalTexture` draws only its text, and `Image` and `Slider`
need textures too. A 1×1 white texture covers all of them.

**UI would scroll with the camera.** `Canvas.Draw` runs inside the
camera-transformed scene batch, so the `ui` layer needs its own screen-space
pass — which Tutorial 1's `Draw` override already sets up.

## 2. A font

Add a MonoGame content project, or reuse an existing `Content.mgcb`. The
description file, `Content/Fonts/ui.spritefont`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<XnaContent xmlns:Graphics="Microsoft.Xna.Framework.Content.Pipeline.Graphics">
  <Asset Type="Graphics:FontDescription">
    <FontName>Arial</FontName>
    <Size>22</Size>
    <Spacing>0</Spacing>
    <UseKerning>true</UseKerning>
    <Style>Regular</Style>
    <CharacterRegions>
      <CharacterRegion>
        <Start>&#32;</Start>
        <End>&#126;</End>
      </CharacterRegion>
    </CharacterRegions>
  </Asset>
</XnaContent>
```

```csharp
// Content.RootDirectory is hard-coded to "Assets" by SBEngine.
Font = Content.Load<SpriteFont>("Fonts/ui");
```

No pipeline set up yet? Everything below works without one — you just see no
text. Wire the font when you have it; nothing else changes.

## 3. UI scaffolding

Add to `Game.cs`:

```csharp
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;

// … inside class Game …

protected SpriteFont? Font;

protected override void OnEngineReady()
{
    try   { Font = Content.Load<SpriteFont>("Fonts/ui"); }
    catch { Font = null; }              // text will be invisible; layout still works

    BuildScene();
}

/// <summary>Creates a UI actor with a Canvas on the "ui" layer.</summary>
protected Canvas CreateCanvas(Scene scene, string name = "UI")
{
    var actor  = new Actor(name);
    var canvas = actor.AddComponent<Canvas>();
    canvas.Font = Font;

    // Hit-testing reads raw screen pixels while rendering applies the scale
    // matrix, so keep the two in step by using the real back-buffer size.
    canvas.ScaleMode = CanvasScaleMode.PixelPerfect;
    canvas.ReferenceResolution = new Vector2(Config.WindowWidth, Config.WindowHeight);

    scene.AddActor(actor, "ui");
    return canvas;
}

/// <summary>A button that is actually visible: white pixel background plus a tint.</summary>
protected Button MakeButton(string text, Vector2 position, Vector2 size, Action onClick)
{
    var b = new Button
    {
        Text          = text,
        TextColor     = Color.White,
        Position      = position,
        Size          = size,
        NormalTexture = White,
        Tint          = new Color(52, 58, 82),
    };
    b.OnClick      += onClick;
    b.OnHoverEnter += () => b.Tint = new Color(76, 86, 120);
    b.OnHoverExit  += () => b.Tint = new Color(52, 58, 82);
    return b;
}
```

`CanvasScaleMode` is worth understanding before you pick one:

| Mode | Behaviour |
|---|---|
| `PixelPerfect` | identity matrix; one canvas pixel = one screen pixel |
| `ScaleWithScreen` | **default**; non-uniform scale per axis, fills the viewport, distorts aspect |
| `ConstantSize` | uniform scale on the smaller axis; never clipped, letterboxes |

`Canvas.Update` reads `Mouse.GetState()` in **raw screen pixels** and does not
apply the scale matrix, so under `ScaleWithScreen` at a viewport that differs
from `ReferenceResolution`, clicks land in the wrong place. `PixelPerfect` (or
`ReferenceResolution` set to the real back-buffer size) keeps them in sync.

## 4. A main menu

`MyGame/Scenes/MainMenuScene.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Save;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;

namespace MyGame.Scenes;

public static class MainMenuScene
{
    public static void Load(Game game)
    {
        var scene = game.SceneManager.CreateScene("MainMenu");

        var cameraActor = new Actor("Main Camera") { Tag = "Camera" };
        cameraActor.Transform.Position = new Vector2(640, 360);
        game.SetCamera(cameraActor.AddComponent<Camera2D>());
        scene.AddActor(cameraActor);

        var canvas = game.CreateCanvas(scene, "MainMenuUI");

        var title = canvas.AddWidget<Label>();
        title.Text      = "BISCUIT BLASTER";
        title.TextColor = new Color(255, 220, 120);
        title.Position  = new Vector2(0, 120);
        title.Size      = new Vector2(1280, 80);
        title.Alignment = TextAlignment.Center;

        var subtitle = canvas.AddWidget<Label>();
        subtitle.Text      = "a SexyBiscuit game";
        subtitle.TextColor = new Color(170, 180, 220);
        subtitle.Position  = new Vector2(0, 200);
        subtitle.Size      = new Vector2(1280, 32);
        subtitle.Alignment = TextAlignment.Center;

        // A Panel with LayoutMode stacks its children automatically.
        var panel = canvas.AddWidget<Panel>();
        panel.Position          = new Vector2(480, 300);
        panel.Size              = new Vector2(320, 300);
        panel.BackgroundTexture = game.WhiteTexture;
        panel.BackgroundColor   = new Color(0, 0, 0, 150);
        panel.LayoutMode        = PanelLayoutMode.Vertical;
        panel.Padding           = 12f;

        var newGame = game.MakeButton("New Game", Vector2.Zero, new Vector2(296, 56),
                                      () => GameScene.Load(game));
        panel.AddChild(newGame);

        var continueBtn = game.MakeButton("Continue", Vector2.Zero, new Vector2(296, 56),
                                          () => { GameScene.Load(game); /* then apply the save */ });
        continueBtn.Interactable = SaveManager.SlotExists(0);
        if (!continueBtn.Interactable) continueBtn.TextColor = Color.Gray;
        panel.AddChild(continueBtn);

        panel.AddChild(game.MakeButton("Options", Vector2.Zero, new Vector2(296, 56),
                                       () => OptionsPanel.Show(game, canvas, panel)));

        panel.AddChild(game.MakeButton("Quit", Vector2.Zero, new Vector2(296, 56),
                                       game.Exit));
    }
}
```

Expose the bits the scene needs on `Game`:

```csharp
public Texture2D WhiteTexture => White;
public void SetCamera(Camera2D camera) => Camera = camera;
```

`Panel.LayoutMode = Vertical` re-runs `ApplyLayout()` at the start of every
`Draw`, stacking children along the axis with `Padding` between them — so the
buttons' own `Position` values are ignored.

## 5. Anchoring

`Widget.Bounds` resolves as:

```
origin = Parent == null
       ? AnchorOffset
       : Parent.Bounds.TopLeft + Parent.Bounds.Size * AnchorMin + AnchorOffset

Bounds = Rectangle(origin + Position, Size)
```

So `AnchorMin` picks a point in the parent and `AnchorOffset` nudges from there:

```csharp
// Bottom-right of the parent, 12px inset
child.AnchorMin    = new Vector2(1f, 1f);
child.AnchorOffset = new Vector2(-child.Size.X - 12, -child.Size.Y - 12);
child.Position     = Vector2.Zero;
```

**`Bounds` ignores `AnchorMax`.** Stretch-to-fill only happens through
`AnchorLayout.Apply`, which rewrites `Position` and `Size`:

```csharp
using SexyBiscuit.Engine.UI.Layout;

header.AnchorMin = new Vector2(0f, 0f);
header.AnchorMax = new Vector2(1f, 0f);      // stretch across the full width
header.Size      = new Vector2(0, 64);       // width will be overwritten
AnchorLayout.Apply(header, parentPanel);
```

`Apply` and `ApplyAll` are one-shot — call them after adding or resizing
children, and again on a window resize. `ApplyAll` covers direct children only.

## 6. A HUD

`MyGame/UI/Hud.cs`:

```csharp
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;
using MyGame.Components;

namespace MyGame.UI;

public sealed class Hud
{
    private readonly Label       _score;
    private readonly ProgressBar _health;
    private readonly Label       _healthText;

    public Hud(Canvas canvas, Texture2D white, Health playerHealth)
    {
        _health = canvas.AddWidget<ProgressBar>();
        _health.Position          = new Vector2(24, 24);
        _health.Size              = new Vector2(280, 22);
        _health.MaxValue          = playerHealth.Max;
        _health.Value             = playerHealth.Current;
        _health.BackgroundTexture = white;
        _health.FillTexture       = white;
        _health.BackgroundColor   = new Color(30, 30, 40);
        _health.FillColor         = new Color(90, 210, 120);
        _health.Direction         = FillDirection.LeftToRight;

        _healthText = canvas.AddWidget<Label>();
        _healthText.Position  = new Vector2(24, 24);
        _healthText.Size      = new Vector2(280, 22);
        _healthText.Alignment = TextAlignment.Center;
        _healthText.Text      = $"{playerHealth.Current} / {playerHealth.Max}";

        _score = canvas.AddWidget<Label>();
        _score.Position  = new Vector2(0, 24);
        _score.Size      = new Vector2(1256, 28);
        _score.Alignment = TextAlignment.Right;
        _score.Text      = "0";

        // Event-driven: no polling in Update.
        playerHealth.Changed += (current, max) =>
        {
            _health.MaxValue = max;
            _health.Value    = current;
            _health.FillColor = current > max * 0.5f ? new Color(90, 210, 120)
                              : current > max * 0.25f ? new Color(230, 190, 80)
                              : new Color(220, 80, 80);
            _healthText.Text = $"{current} / {max}";
        };
    }

    public void SetScore(int value) => _score.Text = value.ToString("N0");
}
```

Driving the HUD from `Health.Changed` rather than polling in `Update` means the
work happens when something actually changes, and there is one place that knows
how health maps to a colour.

## 7. A pause menu

`MyGame/UI/PauseMenu.cs`:

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;

namespace MyGame.UI;

public sealed class PauseMenu
{
    private readonly Panel _root;
    private readonly Game  _game;
    public bool IsOpen { get; private set; }

    public PauseMenu(Game game, Canvas canvas)
    {
        _game = game;

        var dim = canvas.AddWidget<Image>();
        dim.Texture  = game.WhiteTexture;
        dim.Tint     = new Color(0, 0, 0, 170);
        dim.Size     = new Vector2(game.Config.WindowWidth, game.Config.WindowHeight);
        dim.Visible  = false;

        _root = canvas.AddWidget<Panel>();
        _root.Position          = new Vector2(480, 220);
        _root.Size              = new Vector2(320, 280);
        _root.BackgroundTexture = game.WhiteTexture;
        _root.BackgroundColor   = new Color(18, 20, 32, 240);
        _root.LayoutMode        = PanelLayoutMode.Vertical;
        _root.Padding           = 12f;
        _root.Visible           = false;

        var title = new Label
        {
            Text = "PAUSED", Size = new Vector2(296, 40),
            Alignment = TextAlignment.Center, TextColor = Color.White,
        };
        _root.AddChild(title);

        _root.AddChild(game.MakeButton("Resume", Vector2.Zero, new Vector2(296, 52), Close));
        _root.AddChild(game.MakeButton("Options", Vector2.Zero, new Vector2(296, 52), () => { }));
        _root.AddChild(game.MakeButton("Quit to Menu", Vector2.Zero, new Vector2(296, 52),
                                       () => { Close(); Scenes.MainMenuScene.Load(game); }));

        _dim = dim;
    }

    private readonly Image _dim;

    public void Toggle() { if (IsOpen) Close(); else Open(); }

    public void Open()
    {
        IsOpen = true;
        _root.Visible = _dim.Visible = true;
        Time.TimeScale = 0f;              // gameplay freezes; input stays live
    }

    public void Close()
    {
        IsOpen = false;
        _root.Visible = _dim.Visible = false;
        Time.TimeScale = 1f;
    }
}
```

`Time.TimeScale = 0` is the clean way to pause. `Update`, `FixedUpdate` and
`LateUpdate` still run, with `dt == 0`, and `InputManager` is pumped on
**unscaled** time so menus stay responsive.

Physics and tweens are inside the engine's own scaled path, so they pause with
it. **That includes your UI tweens** — a menu animation built with `Tween`
freezes at `TimeScale = 0`. Drive UI motion off `Time.UnscaledDeltaTime`
yourself, or pause by deactivating layers instead (below).

Wire the key:

```csharp
protected override void Update(GameTime gameTime)
{
    base.Update(gameTime);
    if (Input.IsPressed("Pause")) _pause?.Toggle();
    // …
}
```

An alternative that freezes gameplay but keeps UI animation running: deactivate
the gameplay layers instead.

```csharp
foreach (var layer in scene.Layers)
    if (layer.Name != "ui") layer.Active = false;      // Visible stays true
```

## 8. Options with working sliders

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Save;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;

namespace MyGame.UI;

public static class OptionsPanel
{
    public static Panel Show(Game game, Canvas canvas, Widget? hideWhileOpen = null)
    {
        if (hideWhileOpen != null) hideWhileOpen.Visible = false;

        var panel = canvas.AddWidget<Panel>();
        panel.Position          = new Vector2(420, 240);
        panel.Size              = new Vector2(440, 320);
        panel.BackgroundTexture = game.WhiteTexture;
        panel.BackgroundColor   = new Color(18, 20, 32, 240);
        panel.LayoutMode        = PanelLayoutMode.Vertical;
        panel.Padding           = 14f;

        var audio = game.Audio;

        AddSlider(panel, game, "Master", audio.Master.Volume, v =>
        {
            audio.Master.Volume = v;
            PlayerPrefs.SetFloat("vol.master", v);
        });
        AddSlider(panel, game, "Music", audio.Music.Volume, v =>
        {
            audio.Music.Volume = v;
            PlayerPrefs.SetFloat("vol.music", v);
        });
        AddSlider(panel, game, "SFX", audio.SFX.Volume, v =>
        {
            audio.SFX.Volume = v;
            PlayerPrefs.SetFloat("vol.sfx", v);
        });

        panel.AddChild(game.MakeButton("Back", Vector2.Zero, new Vector2(412, 48), () =>
        {
            PlayerPrefs.Save();                    // nothing is written until you call this
            canvas.RemoveWidget(panel);
            if (hideWhileOpen != null) hideWhileOpen.Visible = true;
        }));

        return panel;
    }

    private static void AddSlider(Panel parent, Game game, string label,
                                  float initial, Action<float> onChange)
    {
        parent.AddChild(new Label
        {
            Text = label, Size = new Vector2(412, 26), TextColor = Color.White,
        });

        var slider = new Slider
        {
            MinValue     = 0f,
            MaxValue     = 1f,
            Value        = initial,
            Step         = 0.05f,
            Horizontal   = true,
            Size         = new Vector2(412, 24),
            ThumbSize    = new Vector2(18, 24),
            TrackTexture = game.WhiteTexture,
            ThumbTexture = game.WhiteTexture,
        };
        slider.OnValueChanged += v => onChange(v);
        parent.AddChild(slider);
    }
}
```

Apply saved settings at boot, before the first scene:

```csharp
protected override void OnEngineReady()
{
    Audio.Master.Volume = PlayerPrefs.GetFloat("vol.master", 1f);
    Audio.Music.Volume  = PlayerPrefs.GetFloat("vol.music",  0.7f);
    Audio.SFX.Volume    = PlayerPrefs.GetFloat("vol.sfx",    1f);
    // …
}
```

## 9. Text input

```csharp
var name = canvas.AddWidget<TextInput>();
name.Position         = new Vector2(480, 400);
name.Size             = new Vector2(320, 36);
name.Placeholder      = "Player name";
name.MaxLength        = 24;
name.BackgroundColor  = new Color(28, 30, 44);
name.BorderColor      = new Color(70, 76, 100);
name.FocusBorderColor = new Color(120, 180, 255);
name.OnTextChanged   += t => { };
name.OnSubmit        += t => StartGame(t);      // fires on Enter
```

`TextInput` creates its own pixel texture, so unlike the other widgets it is
visible with no assets. Click to focus; `IsFocused` reports state.

## 10. World-space UI

Health bars over enemies, damage numbers, nameplates:

```csharp
using SexyBiscuit.Engine.UI;

var wc = enemy.AddComponent<WorldCanvas>();
wc.Offset            = new Vector2(0, -44);
wc.Scale             = 0.75f;
wc.BillboardToCamera = true;
wc.Canvas.Font       = Font;

var bar = wc.Canvas.AddWidget<ProgressBar>();
bar.Size              = new Vector2(52, 6);
bar.BackgroundTexture = White;
bar.FillTexture       = White;
bar.FillColor         = new Color(220, 80, 80);
```

`wc.Canvas` is a plain `Canvas` object owned by the component. It draws in the
**world** pass, so it moves with the camera — which is exactly right here.

## 11. Themes

Skin widgets from JSON instead of setting properties one by one.
`MyGame/Assets/UI/theme.json`:

```json
{
  "Button": {
    "normalTexture":  "Assets/UI/btn.png",
    "hoverTexture":   "Assets/UI/btn_hover.png",
    "pressedTexture": "Assets/UI/btn_press.png",
    "textColor":      "#FFFFFF"
  },
  "Panel": { "backgroundColor": "#0E1220F0" },
  "Label": { "textColor": "#E8E8F0" }
}
```

```csharp
Theme.Load("Assets/UI/theme.json");
Theme.Apply(myButton);                 // per widget — not automatic
```

Styles key on **type name** and resolve up the base-class chain, so a `Button`
with no `"Button"` entry falls back to a `"Widget"` entry. Colours parse as
`#RRGGBB` or `#RRGGBBAA`.

## 12. Widgets that render nothing

| Widget | Self-draws? | Silent when |
|---|---|---|
| `Label` | — | `Canvas.Font` is null |
| `Button` | ❌ | no `NormalTexture` |
| `Panel` | ❌ | no `BackgroundTexture` |
| `Image` | ❌ | no `Texture` |
| `Slider` | ❌ | no `TrackTexture` / `ThumbTexture` |
| `ProgressBar`, `TextInput`, `Checkbox`, `Dropdown`, `ScrollView`, `TabView` | ✅ | — |

Plus, for every widget: `Visible = false`, `Opacity = 0`, or a zero `Size` —
only `Label` auto-sizes to its text.

Also: clicks are delivered to **every** widget under the cursor — there is no
event consumption — so avoid overlapping interactive widgets.

## 13. The newer widgets

```csharp
var cb = canvas.AddWidget<Checkbox>();
cb.Text = "Fullscreen";
cb.CheckedChanged.Add(v => SetFullscreen(v));

var dd = canvas.AddWidget<Dropdown>();
dd.SetOptions("Low", "Medium", "High", "Ultra");
dd.SelectedIndex = 2;
dd.SelectionChanged.Add(ApplyQuality);

var sv = canvas.AddWidget<ScrollView>();
sv.Size = new Vector2(360, 400);
foreach (var row in rows) sv.AddChild(row);
sv.LayoutVertically(gap: 4f, padding: 4f);      // also measures the content

var tabs = canvas.AddWidget<TabView>();
tabs.AddPage("Audio", audioPanel);
tabs.AddPage("Video", videoPanel);
tabs.SelectedChanged.Add(i => Debug.WriteLine($"tab {i}"));
```

These broadcast through `SBEvent`, so subscribe with `.Add(handler)` rather than
`+=`. The older widgets (`Button.OnClick`, `Slider.OnValueChanged`,
`TextInput.OnSubmit`) are still plain C# events.

A `TabView` full of `Checkbox`es and `Dropdown`s inside a `ScrollView` is an
options screen with essentially no art — worth knowing before you draw a
nine-patch.

---

## Checkpoint

You have:

- A main menu, a HUD, a pause screen, and options with working sliders
- Understanding of canvas scale modes and why hit-testing can drift
- Anchoring, including where `AnchorMax` does and does not apply
- Settings persisted through `PlayerPrefs`

## Exercises

1. Add a confirmation dialog before "Quit to Menu".
2. Add a fullscreen toggle: `Graphics.IsFullScreen = v; Graphics.ApplyChanges();`
3. Make the HUD health bar lerp toward its target with `SBMath.Damp` instead of
   snapping.

---

**Next:** [Tutorial 8 — Audio](08-audio.md)
